using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Serilog;

namespace lunagalLauncher.Services
{
    /// <summary>
    /// 主窗口 WM_INPUT：Pointer/LL 钩子无法覆盖的扩展键依赖 Raw Input 的 usButtonFlags / ulRawButtons。
    /// </summary>
    internal static class RawInputMouseBridge
    {
        private const uint SubclassId = 0x4D525741;

        private static bool _subclassInstalled;
        private static SUBCLASSPROC? _proc;

        /// <summary>RI_MOUSE_*（与 winuser.h 一致）</summary>
        internal const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
        internal const ushort RI_MOUSE_LEFT_BUTTON_UP = 0x0002;
        internal const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
        internal const ushort RI_MOUSE_RIGHT_BUTTON_UP = 0x0008;
        internal const ushort RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010;
        internal const ushort RI_MOUSE_MIDDLE_BUTTON_UP = 0x0020;
        internal const ushort RI_MOUSE_BUTTON_4_DOWN = 0x0040;
        internal const ushort RI_MOUSE_BUTTON_4_UP = 0x0080;
        internal const ushort RI_MOUSE_BUTTON_5_DOWN = 0x0100;
        internal const ushort RI_MOUSE_BUTTON_5_UP = 0x0200;
        internal const ushort RI_MOUSE_WHEEL = 0x0400;
        internal const ushort RI_MOUSE_HWHEEL = 0x0800;

        private static readonly Dictionary<IntPtr, uint> _lastRawButtonsByDevice = new();
        /// <summary>UI：上一帧 HID 稳定指纹（按设备 + ReportId）。</summary>
        private static readonly Dictionary<(IntPtr hDevice, byte reportId), ulong> _lastHidSigForUi = new();
        /// <summary>计算稳定指纹时用的上一帧原始字节（按设备 + ReportId）。</summary>
        private static readonly Dictionary<(IntPtr hDevice, byte reportId), byte[]> _lastHidRawForStable = new();

        internal const uint WM_INPUT = 0x00FF;
        private const uint RIM_TYPEMOUSE = 0;
        private const uint RID_INPUT = 0x10000003;
        /// <summary>x64 RAWINPUTHEADER 大小（dwType,dwSize,hDevice,wParam）</summary>
        private const uint RawInputHeaderSize = 24;

        /// <summary>与 WinUI Pointer 已覆盖的左/右/中/侧键1/侧键2 相同的 RI 按下沿；UI 录入应走 Pointer，避免点空白结束录入时被 Raw 误记为扩展键。</summary>
        internal static bool IsStandardPointerRiDownFlag(ushort d) =>
            d == RI_MOUSE_LEFT_BUTTON_DOWN || d == RI_MOUSE_RIGHT_BUTTON_DOWN
            || d == RI_MOUSE_MIDDLE_BUTTON_DOWN || d == RI_MOUSE_BUTTON_4_DOWN
            || d == RI_MOUSE_BUTTON_5_DOWN;

        /// <summary>UI 线程：按下沿（非滚轮）。flag 非 0 为 usButtonFlags 路径；否则 mask 非 0 为 ulRawButtons 上升沿。</summary>
        internal static event Action<ushort, uint>? RawMousePhysicalDownRecorded;

        /// <summary>UI 线程：RIM_TYPEHID 报告内容哈希（部分品牌扩展键仅出现在 HID 原始报告）。</summary>
        internal static event Action<ulong>? RawHidPhysicalSignatureRecorded;

        private const uint RIM_TYPEHID = 2;
        private const uint RIDEV_INPUTSINK = 0x00000100;
        private const uint RIDEV_PAGEONLY = 0x00000020;

        private static IntPtr SubclassWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, uint dwRefData)
        {
            if (msg == WM_INPUT && _subclassInstalled)
            {
                try
                {
                    ProcessWmInput(lParam);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "RawInput: WM_INPUT 解析异常（忽略）");
                }
            }

            return DefSubclassProc(hWnd, msg, wParam, lParam);
        }

        internal static void EnsureInstalled(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || _subclassInstalled) return;
            _proc = SubclassWndProc;
            if (!SetWindowSubclass(hwnd, _proc, SubclassId, 0))
            {
                Log.Error("RawInput: SetWindowSubclass 失败");
                return;
            }

            uint devSz = (uint)Marshal.SizeOf<RAWINPUTDEVICE>();
            var devices = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x02, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
                new RAWINPUTDEVICE { usUsagePage = 0x01, usUsage = 0x00, dwFlags = RIDEV_INPUTSINK | RIDEV_PAGEONLY, hwndTarget = hwnd }
            };
            if (!RegisterRawInputDevices(devices, 2, devSz))
            {
                Log.Warning("RawInput: 鼠标+Generic Desktop(PAGEONLY) 注册失败，回退仅鼠标");
                if (!RegisterRawInputDevices(new[] { devices[0] }, 1, devSz))
                {
                    Log.Error("RawInput: RegisterRawInputDevices 失败");
                    RemoveWindowSubclass(hwnd, _proc, SubclassId);
                    return;
                }
            }

            _subclassInstalled = true;
            Log.Information("RawInput：已子类化主窗口并注册 Raw Input（鼠标 + PAGEONLY 以捕获 HID 扩展键）");
        }

        /// <summary>
        /// 进入物理键录入态时调用：清空上一帧状态，使下一次按下产生「上升沿」/ HID 哈希变化，避免首包仅建基线导致录不到。
        /// </summary>
        internal static void ResetBaselinesForPhysicalCaptureRecording()
        {
            _lastRawButtonsByDevice.Clear();
            _lastHidSigForUi.Clear();
            _lastHidRawForStable.Clear();
            MouseMappingEngine.ResetHidBaselinesForRecording();
        }

        private static void ProcessWmInput(IntPtr lParam)
        {
            uint size = 0;
            uint headerSize = RawInputHeaderSize;
            _ = GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size, headerSize);
            if (size == 0 || size > 65536) return;

            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                uint cb = size;
                if (GetRawInputData(lParam, RID_INPUT, buf, ref cb, headerSize) == uint.MaxValue)
                    return;

                uint dwType = (uint)Marshal.ReadInt32(buf, 0);
                IntPtr hDevice = Marshal.ReadIntPtr(buf, 8);

                if (dwType == RIM_TYPEHID)
                {
                    ProcessHidRawInput(buf, cb, hDevice);
                    return;
                }

                if (dwType != RIM_TYPEMOUSE) return;

                // RAWMOUSE 紧跟 RAWINPUTHEADER（x64 上为 24 字节）
                // RAWMOUSE：usFlags +2，usButtonFlags +2，usButtonData +2（勿读 +4 当 flags，那是滚轮增量）
                const int rawMouseOff = 24;
                if (cb < rawMouseOff + 16) return;

                ushort usButtonFlags = (ushort)Marshal.ReadInt16(buf, rawMouseOff + 2);
                uint ulRawButtons = (uint)Marshal.ReadInt32(buf, rawMouseOff + 8);

                ushort fNoWheel = (ushort)(usButtonFlags & ~(RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL));
                if (fNoWheel == 0 && (usButtonFlags & (RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL)) != 0)
                    return;

                // 必须先算 ulRawButtons 差分，且不能与 usButtonFlags 边沿互斥：
                // 很多「扩展键」与左/右/中键 transition 在同一条 WM_INPUT 里，旧逻辑提前 return 会丢掉 ulRawButtons。
                uint rising = 0;
                uint falling = 0;
                if (!_lastRawButtonsByDevice.TryGetValue(hDevice, out uint prevRb))
                {
                    _lastRawButtonsByDevice[hDevice] = ulRawButtons;
                }
                else
                {
                    rising = ulRawButtons & ~prevRb;
                    falling = prevRb & ~ulRawButtons;
                    _lastRawButtonsByDevice[hDevice] = ulRawButtons;
                }

                ushort downEdge = 0;
                ushort upEdge = 0;
                ClassifyButtonFlagEdges(fNoWheel, ref downEdge, ref upEdge);

                if (downEdge != 0)
                    MouseMappingEngine.NotifyRawFlagDown(downEdge);
                if (upEdge != 0)
                    MouseMappingEngine.NotifyRawFlagUp(upEdge);

                if (rising != 0)
                    MouseMappingEngine.NotifyRawMaskDown(rising);
                if (falling != 0)
                    MouseMappingEngine.NotifyRawMaskUp(falling);

                var dq = DispatcherQueue.GetForCurrentThread();
                if (downEdge != 0 && dq != null && !IsStandardPointerRiDownFlag(downEdge))
                {
                    ushort d = downEdge;
                    _ = dq.TryEnqueue(
                        DispatcherQueuePriority.Low,
                        () => RawMousePhysicalDownRecorded?.Invoke(d, 0));
                }

                // 每位单独入队，便于规则里存单 bit 掩码，与引擎 NotifyRawMask* 一致。
                if (rising != 0 && dq != null)
                {
                    for (uint m = rising; m != 0;)
                    {
                        uint low = m & (uint)-(int)m;
                        uint capturedMask = low;
                        _ = dq.TryEnqueue(
                            DispatcherQueuePriority.Low,
                            () => RawMousePhysicalDownRecorded?.Invoke(0, capturedMask));
                        m &= ~low;
                    }
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }

        /// <summary>RIM_TYPEHID：支持 dwCount 多段报告；每段单独算稳定指纹。</summary>
        private static void ProcessHidRawInput(IntPtr buf, uint cb, IntPtr hDevice)
        {
            const int rawHidOff = 24;
            if (cb < rawHidOff + 8) return;
            uint dwSizeHid = (uint)Marshal.ReadInt32(buf, rawHidOff);
            uint dwCount = (uint)Marshal.ReadInt32(buf, rawHidOff + 4);
            if (dwSizeHid == 0 || dwCount == 0) return;
            int payloadLen = (int)cb - rawHidOff - 8;
            if (payloadLen <= 0) return;
            ulong reportBytes = (ulong)dwSizeHid * dwCount;
            int n = payloadLen;
            if (reportBytes > 0 && reportBytes < (ulong)n)
                n = (int)reportBytes;
            if (n > 256) n = 256;
            var raw = new byte[n];
            Marshal.Copy(IntPtr.Add(buf, rawHidOff + 8), raw, 0, n);

            int offset = 0;
            for (uint k = 0; k < dwCount; k++)
            {
                int piece = (int)dwSizeHid;
                if (piece <= 0 || offset + piece > n) break;
                var slice = new byte[piece];
                Buffer.BlockCopy(raw, offset, slice, 0, piece);
                offset += piece;
                ProcessOneHidReportSlice(slice, piece, hDevice);
            }
        }

        private static void ProcessOneHidReportSlice(byte[] raw, int n, IntPtr hDevice)
        {
            if (n <= 0) return;
            byte reportId = raw[0];
            byte[] stable = ExtractStableHidReportBytes(raw, n, hDevice);
            ulong sig = Fnv1a64Hash(stable);
            MouseMappingEngine.NotifyHidRawReport(sig, hDevice, reportId);

            var keyUi = (hDevice, reportId);
            if (!_lastHidSigForUi.TryGetValue(keyUi, out ulong prevUi))
            {
                _lastHidSigForUi[keyUi] = sig;
                return;
            }
            if (prevUi == sig) return;
            _lastHidSigForUi[keyUi] = sig;

            var dq = DispatcherQueue.GetForCurrentThread();
            ulong capSig = sig;
            _ = dq?.TryEnqueue(DispatcherQueuePriority.Low, () => RawHidPhysicalSignatureRecorded?.Invoke(capSig));
        }

        private static ulong Fnv1a64Hash(byte[] data)
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            ulong h = offset;
            for (int i = 0; i < data.Length; i++)
            {
                h ^= data[i];
                h *= prime;
            }
            return h == 0 ? 1UL : h;
        }

        /// <summary>
        /// 排除「典型相对位移/细滚轮/计数器小步」：对索引 1–15 若与上一帧差值在 ±3 内（且 d≠0，d==0 仍保留以维持按键状态）则不纳入哈希。
        /// 首帧退化为前两字节；按 (hDevice, ReportId) 分别建上一帧快照。其余字节（大跳变或索引&gt;15）纳入 FNV。
        /// </summary>
        private static byte[] ExtractStableHidReportBytes(byte[] raw, int n, IntPtr hDevice)
        {
            if (n <= 0) return Array.Empty<byte>();
            byte rid = raw[0];
            var key = (hDevice, rid);
            const int max = 32;
            int len = Math.Min(n, max);

            if (!_lastHidRawForStable.TryGetValue(key, out var last) || last == null || last.Length < len)
            {
                last = new byte[len];
                Array.Copy(raw, last, len);
                _lastHidRawForStable[key] = last;
                return n >= 2 ? new[] { raw[0], raw[1] } : new[] { raw[0] };
            }

            var parts = new List<byte>(len);
            parts.Add(raw[0]);
            for (int i = 1; i < len; i++)
            {
                int d = raw[i] - last[i];
                if (d == 0)
                {
                    parts.Add(raw[i]);
                    continue;
                }
                // 滚轮/倾斜/部分厂商计数器常在 6–15 内小步变化
                if (i >= 1 && i <= 15 && d >= -3 && d <= 3)
                    continue;
                parts.Add(raw[i]);
            }
            Array.Copy(raw, last, len);
            return parts.ToArray();
        }

        /// <summary>将 RI 标志拆成按下沿 / 抬起沿（同一消息通常只有一个 transition）。</summary>
        private static void ClassifyButtonFlagEdges(ushort f, ref ushort downEdge, ref ushort upEdge)
        {
            if ((f & RI_MOUSE_LEFT_BUTTON_DOWN) != 0) downEdge |= RI_MOUSE_LEFT_BUTTON_DOWN;
            if ((f & RI_MOUSE_LEFT_BUTTON_UP) != 0) upEdge |= RI_MOUSE_LEFT_BUTTON_UP;
            if ((f & RI_MOUSE_RIGHT_BUTTON_DOWN) != 0) downEdge |= RI_MOUSE_RIGHT_BUTTON_DOWN;
            if ((f & RI_MOUSE_RIGHT_BUTTON_UP) != 0) upEdge |= RI_MOUSE_RIGHT_BUTTON_UP;
            if ((f & RI_MOUSE_MIDDLE_BUTTON_DOWN) != 0) downEdge |= RI_MOUSE_MIDDLE_BUTTON_DOWN;
            if ((f & RI_MOUSE_MIDDLE_BUTTON_UP) != 0) upEdge |= RI_MOUSE_MIDDLE_BUTTON_UP;
            if ((f & RI_MOUSE_BUTTON_4_DOWN) != 0) downEdge |= RI_MOUSE_BUTTON_4_DOWN;
            if ((f & RI_MOUSE_BUTTON_4_UP) != 0) upEdge |= RI_MOUSE_BUTTON_4_UP;
            if ((f & RI_MOUSE_BUTTON_5_DOWN) != 0) downEdge |= RI_MOUSE_BUTTON_5_DOWN;
            if ((f & RI_MOUSE_BUTTON_5_UP) != 0) upEdge |= RI_MOUSE_BUTTON_5_UP;

            const ushort knownMask = 0x03FF;
            ushort rem = (ushort)(f & ~knownMask);
            rem &= (ushort)(ushort.MaxValue & ~(RI_MOUSE_WHEEL | RI_MOUSE_HWHEEL));
            int remInt = rem;
            while (remInt != 0)
            {
                int lowInt = remInt & -remInt;
                ushort low = (ushort)lowInt;
                if ((low & 0x5555) != 0)
                    downEdge |= low;
                else
                    upEdge |= low;
                remInt &= ~lowInt;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RAWINPUTDEVICE
        {
            public ushort usUsagePage;
            public ushort usUsage;
            public uint dwFlags;
            public IntPtr hwndTarget;
        }

        private delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint uIdSubclass, uint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool SetWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass, uint dwRefData);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern bool RemoveWindowSubclass(IntPtr hWnd, SUBCLASSPROC pfnSubclass, uint uIdSubclass);

        [DllImport("comctl32.dll", SetLastError = true)]
        private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);
    }
}
