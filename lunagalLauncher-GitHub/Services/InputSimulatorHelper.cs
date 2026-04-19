using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Serilog;

namespace lunagalLauncher.Services
{
    /// <summary>
    /// 使用 SendInput 发送鼠标与键盘输入（避免递归钩子时需配合 LLMHF_INJECTED 过滤）
    /// </summary>
    internal static class InputSimulatorHelper
    {
        /// <summary>
        /// 本程序映射注入使用的 dwExtraInfo 标记。低层钩子中：仅忽略带此标记的注入以更新物理状态，
        /// 仍处理「驱动/外设标记为注入」的真实 WM_*UP，避免物理已松开但 _physicalDown 永远为 1。
        /// </summary>
        internal const ulong MappingSyntheticExtraInfoValue = 0x4C4E4155;
        internal static readonly IntPtr MappingSyntheticExtraInfoPtr = new(unchecked((int)MappingSyntheticExtraInfoValue));

        /// <summary>防止多路线程池同时 SendKeyCombo，修饰键 DOWN/UP 交错导致 Shift 卡住。</summary>
        private static readonly object _sendKeyComboSync = new();

        /// <summary>与 <see cref="SendKeyCombo"/> 共用：抬起时 <see cref="SendMouseButtonUpOnly"/> 须等组合键注入结束，避免鼠标与键盘注入交错。</summary>
        internal static object ComboInjectionSyncLock => _sendKeyComboSync;

        internal static void SendMouseButtonClick(int button012)
        {
            try
            {
                uint downFlag = button012 switch
                {
                    0 => MouseInputNative.MOUSEEVENTF_LEFTDOWN,
                    1 => MouseInputNative.MOUSEEVENTF_RIGHTDOWN,
                    2 => MouseInputNative.MOUSEEVENTF_MIDDLEDOWN,
                    _ => MouseInputNative.MOUSEEVENTF_LEFTDOWN
                };
                uint upFlag = button012 switch
                {
                    0 => MouseInputNative.MOUSEEVENTF_LEFTUP,
                    1 => MouseInputNative.MOUSEEVENTF_RIGHTUP,
                    2 => MouseInputNative.MOUSEEVENTF_MIDDLEUP,
                    _ => MouseInputNative.MOUSEEVENTF_LEFTUP
                };

                // 连发模式：只发 UP→DOWN。不发最后的 UP2——
                // 因为 UP2 会把 GetAsyncKeyState 改成"未按下"，导致轮询线程误判为松手而停止连发。
                // 完整的点击周期由"这次的 UP + 下次的 DOWN"构成（间隔 = RepeatInterval）。
                // 视觉小说：每次 UP 时推进对话；Chrome：UP+DOWN 构成状态变化也能触发 click。
                if (!TrySendAbsoluteUpDown(upFlag, downFlag))
                {
                    var inputs = new MouseInputNative.INPUT[1];
                    inputs[0].type = MouseInputNative.INPUT_MOUSE;

                    inputs[0].U.mi = new MouseInputNative.MOUSEINPUT { dwFlags = upFlag, dwExtraInfo = MappingSyntheticExtraInfoPtr };
                    SendInputs(inputs);
                    System.Threading.Thread.Sleep(15);

                    inputs[0].U.mi.dwFlags = downFlag;
                    SendInputs(inputs);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SendMouseButtonClick 异常（已吞掉，防止崩溃）");
            }
        }

        /// <summary>
        /// 「触发一次」等单次完整点击：按下→抬起。与 <see cref="SendMouseButtonClick"/> 的连发用 UP→DOWN 区分开，
        /// 避免与吞键后补的 <see cref="SendMouseButtonUpOnly"/> 叠加成两次 UP + DOWN，导致应用侧出现双击或状态错乱。
        /// </summary>
        internal static void SendMouseButtonClickFireOnce(int button012)
        {
            try
            {
                SendMouseDown(button012);
                System.Threading.Thread.Sleep(10);
                SendMouseUp(button012);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SendMouseButtonClickFireOnce 异常（已吞掉，防止崩溃）");
            }
        }

        private static bool _absWarned;

        /// <summary>
        /// 绝对坐标 UP→DOWN 注入。不含 UP2，避免改变 GetAsyncKeyState 导致轮询误停。
        /// </summary>
        private static bool TrySendAbsoluteUpDown(uint upFlag, uint downFlag)
        {
            try
            {
                if (!MouseInputNative.GetCursorPos(out var pt)) return false;

                int sx = MouseInputNative.GetSystemMetrics(MouseInputNative.SM_CXSCREEN);
                int sy = MouseInputNative.GetSystemMetrics(MouseInputNative.SM_CYSCREEN);
                if (sx <= 0 || sy <= 0) return false;

                int absX = (int)((pt.X * 65536.0 + sx - 1) / sx);
                int absY = (int)((pt.Y * 65536.0 + sy - 1) / sy);

                uint abs = MouseInputNative.MOUSEEVENTF_ABSOLUTE;
                int size = Marshal.SizeOf<MouseInputNative.INPUT>();

                var single = new MouseInputNative.INPUT[1];
                single[0].type = MouseInputNative.INPUT_MOUSE;

                // 第一步：UP（释放当前按下态）
                single[0].U.mi = new MouseInputNative.MOUSEINPUT
                {
                    dx = absX, dy = absY, mouseData = 0,
                    dwFlags = abs | upFlag, time = 0, dwExtraInfo = MappingSyntheticExtraInfoPtr
                };
                uint s1 = MouseInputNative.SendInput(1, single, size);
                if (s1 == 0)
                {
                    if (!_absWarned)
                    {
                        _absWarned = true;
                        int err = Marshal.GetLastWin32Error();
                        Log.Warning("鼠标映射：绝对坐标注入也被拒 sent=0/1 err={E}，回退到相对坐标/PostMessage", err);
                    }
                    return false;
                }

                System.Threading.Thread.Sleep(15);

                // 第二步：DOWN（新的独立按下）——不发 UP2，避免改变 GetAsyncKeyState 导致轮询误停
                single[0].U.mi = new MouseInputNative.MOUSEINPUT
                {
                    dx = absX, dy = absY, mouseData = 0,
                    dwFlags = abs | downFlag, time = 0, dwExtraInfo = MappingSyntheticExtraInfoPtr
                };
                uint s2 = MouseInputNative.SendInput(1, single, size);

                if (!_absWarned)
                {
                    _absWarned = true;
                    Log.Information("鼠标映射：绝对坐标 UP→DOWN 注入 UP={A} DOWN={B}", s1, s2);
                }
                return s2 == 1;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "TrySendAbsoluteClick 异常");
                return false;
            }
        }

        private static int _postMsgFallbackCount;

        /// <summary>
        /// 向光标下窗口（子窗口 + 顶层窗口）投递完整的 DOWN+UP 鼠标点击消息。
        /// 绕过输入管道（SendInput/mouse_event），直接通过窗口消息触发点击。
        /// Chrome 等 Chromium 系应用主要靠此方式响应连发点击。
        /// </summary>
        internal static void PostMouseClickToForeground(int button012) => PostMouseClickToWindowUnderCursor(button012);

        private static void PostMouseClickToWindowUnderCursor(int button012)
        {
            try
            {
                if (!MouseInputNative.GetCursorPos(out var pt)) return;
                var child = MouseInputNative.WindowFromPoint(pt);
                if (child == IntPtr.Zero) return;
                var top = MouseInputNative.GetAncestor(child, MouseInputNative.GA_ROOT);
                if (top == IntPtr.Zero) top = child;

                (uint down, uint up, uint mk) = button012 switch
                {
                    1 => ((uint)MouseInputNative.WM_RBUTTONDOWN, (uint)MouseInputNative.WM_RBUTTONUP, MouseInputNative.MK_RBUTTON),
                    2 => ((uint)MouseInputNative.WM_MBUTTONDOWN, (uint)MouseInputNative.WM_MBUTTONUP, MouseInputNative.MK_MBUTTON),
                    _ => ((uint)MouseInputNative.WM_LBUTTONDOWN, (uint)MouseInputNative.WM_LBUTTONUP, MouseInputNative.MK_LBUTTON)
                };

                PostButtonToHwnd(child, pt, down, up, mk);
                if (top != child)
                    PostButtonToHwnd(top, pt, down, up, mk);

                int n = System.Threading.Interlocked.Increment(ref _postMsgFallbackCount);
                if (n == 1 || n % 20 == 0)
                {
                    Log.Information("鼠标映射：PostMessage 回退 #{N} child=0x{C:X}({CC}) top=0x{T:X}({TC}) fg=({FC}) 屏={X},{Y}",
                        n, child.ToInt64(), MouseInputNative.GetClassNameSafe(child),
                        top.ToInt64(), MouseInputNative.GetClassNameSafe(top),
                        MouseInputNative.GetClassNameSafe(MouseInputNative.GetForegroundWindow()),
                        pt.X, pt.Y);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "PostMessage 回退失败");
            }
        }

        private static void PostButtonToHwnd(IntPtr hwnd, MouseInputNative.POINT screenPt, uint down, uint up, uint mk)
        {
            var local = screenPt;
            MouseInputNative.ScreenToClient(hwnd, ref local);
            int lParam = ((local.Y & 0xFFFF) << 16) | (local.X & 0xFFFF);
            MouseInputNative.PostMessage(hwnd, down, (IntPtr)(long)mk, (IntPtr)(long)lParam);
            MouseInputNative.PostMessage(hwnd, up, IntPtr.Zero, (IntPtr)(long)lParam);
        }

        /// <summary>
        /// 只发 UP（绝对坐标）。用于连发周期的"释放"阶段。
        /// </summary>
        internal static void SendMouseUp(int button012)
        {
            try
            {
                uint upFlag = button012 switch
                {
                    0 => MouseInputNative.MOUSEEVENTF_LEFTUP,
                    1 => MouseInputNative.MOUSEEVENTF_RIGHTUP,
                    2 => MouseInputNative.MOUSEEVENTF_MIDDLEUP,
                    _ => MouseInputNative.MOUSEEVENTF_LEFTUP
                };
                SendAbsSingle(upFlag);
            }
            catch (Exception ex) { Log.Warning(ex, "SendMouseUp 异常"); }
        }

        /// <summary>
        /// 只发 DOWN（绝对坐标）。用于连发周期的"按下"阶段。
        /// </summary>
        internal static void SendMouseDown(int button012)
        {
            try
            {
                uint downFlag = button012 switch
                {
                    0 => MouseInputNative.MOUSEEVENTF_LEFTDOWN,
                    1 => MouseInputNative.MOUSEEVENTF_RIGHTDOWN,
                    2 => MouseInputNative.MOUSEEVENTF_MIDDLEDOWN,
                    _ => MouseInputNative.MOUSEEVENTF_LEFTDOWN
                };
                SendAbsSingle(downFlag);
            }
            catch (Exception ex) { Log.Warning(ex, "SendMouseDown 异常"); }
        }

        private static bool _tierLogDone;

        /// <summary>
        /// 三层递进注入：优先使用与 AHK SendEvent 一致的方式（不带 ABSOLUTE，不产生多余 WM_MOUSEMOVE），
        /// 避免 AVG 游戏因额外 MOUSEMOVE 将注入误判为拖拽而丢弃点击。
        /// 仅在上层被安全软件拦截时才逐级回退到更激进的注入方式。
        /// </summary>
        private static void SendAbsSingle(uint flag)
        {
            if (!MouseInputNative.GetCursorPos(out var pt)) return;
            int size = Marshal.SizeOf<MouseInputNative.INPUT>();

            // ── 第 1 层：不带 ABSOLUTE 的 SendInput（与 AHK SendEvent 行为一致） ──
            // 不会产生额外的 WM_MOUSEMOVE，AVG/视觉小说引擎兼容性最佳
            var simple = new MouseInputNative.INPUT[1];
            simple[0].type = MouseInputNative.INPUT_MOUSE;
            simple[0].U.mi = new MouseInputNative.MOUSEINPUT
            {
                dx = 0, dy = 0, mouseData = 0,
                dwFlags = flag, time = 0, dwExtraInfo = MappingSyntheticExtraInfoPtr
            };
            uint sent1 = MouseInputNative.SendInput(1, simple, size);
            if (sent1 == 1)
            {
                if (!_tierLogDone)
                {
                    _tierLogDone = true;
                    Log.Information("鼠标映射：注入走第 1 层（无 ABSOLUTE SendInput）成功");
                }
                return;
            }

            // ── 第 2 层：带 ABSOLUTE 的 SendInput（绕过 360 等安全软件对非绝对坐标注入的拦截） ──
            int sx = MouseInputNative.GetSystemMetrics(MouseInputNative.SM_CXSCREEN);
            int sy = MouseInputNative.GetSystemMetrics(MouseInputNative.SM_CYSCREEN);
            if (sx <= 0 || sy <= 0) return;
            int absX = (int)((pt.X * 65536.0 + sx - 1) / sx);
            int absY = (int)((pt.Y * 65536.0 + sy - 1) / sy);

            var absInput = new MouseInputNative.INPUT[1];
            absInput[0].type = MouseInputNative.INPUT_MOUSE;
            absInput[0].U.mi = new MouseInputNative.MOUSEINPUT
            {
                dx = absX, dy = absY, mouseData = 0,
                dwFlags = MouseInputNative.MOUSEEVENTF_ABSOLUTE | flag,
                time = 0, dwExtraInfo = MappingSyntheticExtraInfoPtr
            };
            uint sent2 = MouseInputNative.SendInput(1, absInput, size);
            if (sent2 == 1)
            {
                if (!_tierLogDone)
                {
                    _tierLogDone = true;
                    Log.Information("鼠标映射：注入走第 2 层（ABSOLUTE SendInput）成功，第 1 层被拦截");
                }
                return;
            }

            // ── 第 3 层：窗口消息回退（PostMessage） ──
            if (!_tierLogDone)
            {
                _tierLogDone = true;
                Log.Warning("鼠标映射：SendInput 全部被拦截（sent1={S1} sent2={S2}），回退到 PostMessage", sent1, sent2);
            }

            try
            {
                var child = MouseInputNative.WindowFromPoint(pt);
                if (child != IntPtr.Zero)
                {
                    bool isDown = (flag & (MouseInputNative.MOUSEEVENTF_LEFTDOWN | MouseInputNative.MOUSEEVENTF_RIGHTDOWN | MouseInputNative.MOUSEEVENTF_MIDDLEDOWN)) != 0;
                    uint msg;
                    IntPtr wParam;
                    if ((flag & (MouseInputNative.MOUSEEVENTF_LEFTDOWN | MouseInputNative.MOUSEEVENTF_LEFTUP)) != 0)
                    {
                        msg = isDown ? (uint)MouseInputNative.WM_LBUTTONDOWN : (uint)MouseInputNative.WM_LBUTTONUP;
                        wParam = isDown ? (IntPtr)MouseInputNative.MK_LBUTTON : IntPtr.Zero;
                    }
                    else if ((flag & (MouseInputNative.MOUSEEVENTF_RIGHTDOWN | MouseInputNative.MOUSEEVENTF_RIGHTUP)) != 0)
                    {
                        msg = isDown ? (uint)MouseInputNative.WM_RBUTTONDOWN : (uint)MouseInputNative.WM_RBUTTONUP;
                        wParam = isDown ? (IntPtr)MouseInputNative.MK_RBUTTON : IntPtr.Zero;
                    }
                    else
                    {
                        msg = isDown ? (uint)MouseInputNative.WM_MBUTTONDOWN : (uint)MouseInputNative.WM_MBUTTONUP;
                        wParam = isDown ? (IntPtr)MouseInputNative.MK_MBUTTON : IntPtr.Zero;
                    }

                    var local = pt;
                    MouseInputNative.ScreenToClient(child, ref local);
                    IntPtr lParam = (IntPtr)(((local.Y & 0xFFFF) << 16) | (local.X & 0xFFFF));
                    MouseInputNative.PostMessage(child, msg, wParam, lParam);

                    var top = MouseInputNative.GetAncestor(child, MouseInputNative.GA_ROOT);
                    if (top != IntPtr.Zero && top != child)
                    {
                        var localTop = pt;
                        MouseInputNative.ScreenToClient(top, ref localTop);
                        IntPtr lParamTop = (IntPtr)(((localTop.Y & 0xFFFF) << 16) | (localTop.X & 0xFFFF));
                        MouseInputNative.PostMessage(top, msg, wParam, lParamTop);
                    }
                }
            }
            catch { }
        }

        /// <summary>
        /// 与 AHK <c>SendEvent("{Shift Down}{z Down}"), Sleep(60), SendEvent("{z Up}{Shift Up}")</c> 一致：
        /// 修饰键与主键在同一批按下后共同保持，再逆序抬起，便于依赖 GetAsyncKeyState / 低层键盘钩子的程序判到组合键。
        /// </summary>
        private const int ComboKeysHoldAfterDownMs = 80;

        /// <summary>
        /// 与 AHK SendEvent 类似：多键分多次 SendInput 并短间隔，减轻 RegisterHotKey（Magpie 等）在单批注入时漏检。
        /// </summary>
        private const int ComboKeyInterDownMs = 2;

        /// <summary>组合键主序列完成后，再等此时间再执行 Shift nudge，避免 Magpie 等全局热键尚未处理完就被额外 KEYUP 打乱。</summary>
        private const int NudgeShiftAfterComboMs = 120;

        /// <summary>拆分组合键（最多 3 键）；全角 ＋ 视为半角 +，与 UI 输入法兼容。</summary>
        private static List<string> ParseKeyComboParts(string? keyComboText)
        {
            if (string.IsNullOrWhiteSpace(keyComboText))
                return new List<string>();
            var s = keyComboText.Trim().Replace('\uFF0B', '+');
            var parts = s.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            while (parts.Count > 3)
                parts.RemoveAt(0);
            return parts;
        }

        internal static void SendKeyCombo(string? keyComboText)
        {
            lock (_sendKeyComboSync)
            {
                SendKeyComboCore(keyComboText);
            }
        }

        private static void SendKeyComboCore(string? keyComboText)
        {
            if (string.IsNullOrWhiteSpace(keyComboText))
            {
                Log.Warning("SendKeyCombo: 空字符串");
                return;
            }

            var parts = ParseKeyComboParts(keyComboText);
            if (parts.Count == 0) return;

            var modifiers = new List<ushort>();
            var mains = new List<string>();
            foreach (var p in parts)
            {
                if (TryModifierVk(p, out ushort mvk))
                    modifiers.Add(mvk);
                else
                    mains.Add(p);
            }

            modifiers = modifiers.Distinct().ToList();

            if (mains.Count == 0)
            {
                if (modifiers.Count == 0)
                {
                    Log.Warning("SendKeyCombo: 无有效主键（仅修饰键）");
                    return;
                }

                var seqTapModDown = new List<MouseInputNative.INPUT>();
                foreach (var vk in modifiers)
                    seqTapModDown.Add(KeyDownInputExternal(vk));
                SendInputsSequential(seqTapModDown, ComboKeyInterDownMs);
                System.Threading.Thread.Sleep(ComboKeysHoldAfterDownMs);
                var seqTapModUp = new List<MouseInputNative.INPUT>();
                foreach (var vk in Enumerable.Reverse(modifiers))
                    seqTapModUp.Add(KeyUpInputExternal(vk));
                SendInputsSequential(seqTapModUp, ComboKeyInterDownMs);
                NudgeShiftReleasedForGalgames(modifiers);
                return;
            }

            // 单主键：外部程序用 dwExtraInfo=0；DOWN/UP 各一批 SendInput，减少 Gal 轮询漏 KEYUP。
            if (mains.Count == 1)
            {
                ushort vkMain = VkFromToken(mains[0]);
                if (vkMain == 0)
                {
                    Log.Warning("SendKeyCombo: 无法解析按键 {Key}", mains[0]);
                    return;
                }

                if (modifiers.Count > 0)
                {
                    var down = new List<MouseInputNative.INPUT>(modifiers.Count + 1);
                    foreach (var vk in modifiers)
                        down.Add(KeyDownInputExternal(vk));
                    // 主键用扫描码 + ExtraInfo=0；逐键短间隔发送，贴近 AHK SendEvent，减轻 Magpie 等单批漏检。
                    down.Add(KeyDownInputForComboExternal(vkMain));
                    SendInputsSequential(down, ComboKeyInterDownMs);
                    System.Threading.Thread.Sleep(ComboKeysHoldAfterDownMs);
                    var up = new List<MouseInputNative.INPUT>(modifiers.Count + 1) { KeyUpInputForComboExternal(vkMain) };
                    foreach (var vk in Enumerable.Reverse(modifiers))
                        up.Add(KeyUpInputExternal(vk));
                    SendInputsSequential(up, ComboKeyInterDownMs);
                    NudgeShiftReleasedForGalgames(modifiers);
                }
                else
                {
                    SendInputs(new[] { KeyDownInputExternal(vkMain) });
                    System.Threading.Thread.Sleep(ComboKeyInterDownMs);
                    SendInputs(new[] { KeyUpInputExternal(vkMain) });
                }

                return;
            }

            // 2～3 个主键：和弦主键仍可用扫描码（全局热键）；修饰键用外部注入。
            if (mains.Count is >= 2 and <= 3)
            {
                var vks = new List<ushort>();
                foreach (var m in mains)
                {
                    ushort vk = VkFromToken(m);
                    if (vk == 0)
                    {
                        Log.Warning("SendKeyCombo: 无法解析和弦键 {Key}", m);
                        return;
                    }
                    vks.Add(vk);
                }

                var downAll = new List<MouseInputNative.INPUT>();
                foreach (var vk in modifiers)
                    downAll.Add(KeyDownInputExternal(vk));
                foreach (var vk in vks)
                    downAll.Add(KeyDownInputForCombo(vk));
                SendInputsSequential(downAll, ComboKeyInterDownMs);
                System.Threading.Thread.Sleep(ComboKeysHoldAfterDownMs);
                var upAll = new List<MouseInputNative.INPUT>();
                foreach (var vk in Enumerable.Reverse(vks))
                    upAll.Add(KeyUpInputForCombo(vk));
                foreach (var vk in Enumerable.Reverse(modifiers))
                    upAll.Add(KeyUpInputExternal(vk));
                SendInputsSequential(upAll, ComboKeyInterDownMs);
                NudgeShiftReleasedForGalgames(modifiers);
            }
        }

        /// <summary>
        /// 连发间隔为 1 时：按下组合键并保持，松手前调用 <see cref="SendKeyComboHoldUp"/>。
        /// </summary>
        internal static bool SendKeyComboHoldDown(string? keyComboText)
        {
            lock (_sendKeyComboSync)
            {
                return SendKeyComboHoldDownCore(keyComboText);
            }
        }

        private static bool SendKeyComboHoldDownCore(string? keyComboText)
        {
            if (string.IsNullOrWhiteSpace(keyComboText)) return false;

            var parts = ParseKeyComboParts(keyComboText);
            if (parts.Count == 0) return false;

            var modifiers = new List<ushort>();
            var mains = new List<string>();
            foreach (var p in parts)
            {
                if (TryModifierVk(p, out ushort mvk))
                    modifiers.Add(mvk);
                else
                    mains.Add(p);
            }

            modifiers = modifiers.Distinct().ToList();

            // 仅修饰键（如单独 Ctrl/Shift）：按住修饰键，无“主键”也合法（连发间隔=1 长按场景）
            if (mains.Count == 0)
            {
                if (modifiers.Count == 0)
                {
                    Log.Warning("SendKeyComboHoldDown: 无有效主键");
                    return false;
                }

                var seqModOnly = new List<MouseInputNative.INPUT>();
                foreach (var vk in modifiers)
                    seqModOnly.Add(KeyDownInputExternal(vk));
                return SendInputsSequential(seqModOnly, ComboKeyInterDownMs);
            }

            if (mains.Count == 1)
            {
                ushort vkMain = VkFromToken(mains[0]);
                if (vkMain == 0)
                {
                    Log.Warning("SendKeyComboHoldDown: 无法解析按键 {Key}", mains[0]);
                    return false;
                }

                var down = new List<MouseInputNative.INPUT>(modifiers.Count + 1);
                foreach (var vk in modifiers)
                    down.Add(KeyDownInputExternal(vk));
                down.Add(KeyDownInputForComboExternal(vkMain));
                return SendInputsSequential(down, ComboKeyInterDownMs);
            }

            if (mains.Count is >= 2 and <= 3)
            {
                var vks = new List<ushort>();
                foreach (var m in mains)
                {
                    ushort vk = VkFromToken(m);
                    if (vk == 0)
                    {
                        Log.Warning("SendKeyComboHoldDown: 无法解析和弦键 {Key}", m);
                        return false;
                    }
                    vks.Add(vk);
                }

                var downAll = new List<MouseInputNative.INPUT>();
                foreach (var vk in modifiers)
                    downAll.Add(KeyDownInputExternal(vk));
                foreach (var vk in vks)
                    downAll.Add(KeyDownInputForCombo(vk));
                return SendInputsSequential(downAll, ComboKeyInterDownMs);
            }

            return false;
        }

        /// <summary>
        /// 与 <see cref="SendKeyComboHoldDown"/> 配对，在物理键松开时抬起。
        /// </summary>
        internal static void SendKeyComboHoldUp(string? keyComboText)
        {
            lock (_sendKeyComboSync)
            {
                SendKeyComboHoldUpCore(keyComboText);
            }
        }

        private static void SendKeyComboHoldUpCore(string? keyComboText)
        {
            if (string.IsNullOrWhiteSpace(keyComboText)) return;

            var parts = ParseKeyComboParts(keyComboText);
            if (parts.Count == 0) return;

            var modifiers = new List<ushort>();
            var mains = new List<string>();
            foreach (var p in parts)
            {
                if (TryModifierVk(p, out ushort mvk))
                    modifiers.Add(mvk);
                else
                    mains.Add(p);
            }

            modifiers = modifiers.Distinct().ToList();

            if (mains.Count == 0)
            {
                if (modifiers.Count == 0) return;
                var seqModUp = new List<MouseInputNative.INPUT>();
                foreach (var vk in Enumerable.Reverse(modifiers))
                    seqModUp.Add(KeyUpInputExternal(vk));
                SendInputsSequential(seqModUp, ComboKeyInterDownMs);
                NudgeShiftReleasedForGalgames(modifiers);
                return;
            }

            if (mains.Count == 1)
            {
                ushort vkMain = VkFromToken(mains[0]);
                if (vkMain == 0) return;

                var seq = new List<MouseInputNative.INPUT>
                {
                    KeyUpInputForComboExternal(vkMain)
                };
                foreach (var vk in Enumerable.Reverse(modifiers))
                    seq.Add(KeyUpInputExternal(vk));
                SendInputsSequential(seq, ComboKeyInterDownMs);
                NudgeShiftReleasedForGalgames(modifiers);
                return;
            }

            if (mains.Count is >= 2 and <= 3)
            {
                var vks = new List<ushort>();
                foreach (var m in mains)
                {
                    ushort vk = VkFromToken(m);
                    if (vk == 0) return;
                    vks.Add(vk);
                }

                var chord = new List<MouseInputNative.INPUT>();
                foreach (var vk in Enumerable.Reverse(vks))
                    chord.Add(KeyUpInputForCombo(vk));
                foreach (var vk in Enumerable.Reverse(modifiers))
                    chord.Add(KeyUpInputExternal(vk));
                SendInputsSequential(chord, ComboKeyInterDownMs);
                NudgeShiftReleasedForGalgames(modifiers);
            }
        }

        /// <summary>
        /// 解析修饰键：裸写 shift 为左 Shift（VK_LSHIFT）；lshift/rshift 为左右；ctrl/alt/win 同理可分左右。
        /// </summary>
        private static bool TryModifierVk(string token, out ushort vk)
        {
            vk = 0;
            token = token.Trim();
            if (token.Length == 0) return false;
            var low = token.ToLowerInvariant();
            // UI 写「shift」默认左 Shift（VK_LSHIFT），与 AHK 常见行为及 MOD_SHIFT 热键更一致；明确左右请写 lshift/rshift。
            if (low is "shift") { vk = 0xA0; return true; } // VK_LSHIFT
            if (low is "lshift") { vk = 0xA0; return true; } // VK_LSHIFT
            if (low is "rshift") { vk = 0xA1; return true; } // VK_RSHIFT
            if (low is "ctrl" or "control" or "lcontrol") { vk = 0xA2; return true; } // VK_LCONTROL
            if (low is "rcontrol") { vk = 0xA3; return true; }            // VK_RCONTROL
            if (low is "alt" or "lalt") { vk = 0xA4; return true; }       // VK_LMENU
            if (low is "ralt") { vk = 0xA5; return true; }                // VK_RMENU
            if (low is "win" or "lwin") { vk = 0x5B; return true; }       // VK_LWIN
            if (low is "rwin") { vk = 0x5C; return true; }                // VK_RWIN
            return false;
        }

        /// <summary>
        /// 仅发送鼠标键抬起，用于在拦截 WM_*UP 后补全"释放"状态
        /// </summary>
        /// <summary>
        /// 仅发送某键的 KEYUP（用于吞掉物理键盘键抬起前释放系统按键状态，与鼠标 SendMouseButtonUpOnly 对称）。
        /// </summary>
        internal static void SendKeyUpOnly(ushort vk)
        {
            try
            {
                var inputs = new[] { KeyUpInput(vk) };
                SendInputs(inputs);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "SendKeyUpOnly vk={Vk} 异常（已吞掉）", vk);
            }
        }

        /// <summary>物理键索引 0–4：左/右/中/侧键1/侧键2（与 MousePhysicalButton 一致）</summary>
        internal static void SendMouseButtonUpOnly(int button01234)
        {
            if (button01234 >= 3)
            {
                uint xdata = button01234 == 3 ? MouseInputNative.XBUTTON1 : MouseInputNative.XBUTTON2;
                var input = new MouseInputNative.INPUT
                {
                    type = MouseInputNative.INPUT_MOUSE,
                    U = new MouseInputNative.InputUnion
                    {
                        mi = new MouseInputNative.MOUSEINPUT
                        {
                            dx = 0,
                            dy = 0,
                            mouseData = xdata,
                            dwFlags = MouseInputNative.MOUSEEVENTF_XUP,
                            time = 0,
                            dwExtraInfo = MappingSyntheticExtraInfoPtr
                        }
                    }
                };
                SendInputs(new[] { input });
                return;
            }

            uint upFlag = button01234 switch
            {
                0 => MouseInputNative.MOUSEEVENTF_LEFTUP,
                1 => MouseInputNative.MOUSEEVENTF_RIGHTUP,
                2 => MouseInputNative.MOUSEEVENTF_MIDDLEUP,
                _ => MouseInputNative.MOUSEEVENTF_LEFTUP
            };
            var input2 = new MouseInputNative.INPUT
            {
                type = MouseInputNative.INPUT_MOUSE,
                U = new MouseInputNative.InputUnion
                {
                    mi = new MouseInputNative.MOUSEINPUT
                    {
                        dx = 0,
                        dy = 0,
                        mouseData = 0,
                        dwFlags = upFlag,
                        time = 0,
                        dwExtraInfo = MappingSyntheticExtraInfoPtr
                    }
                }
            };
            SendInputs(new[] { input2 });
        }

        private static ushort VkFromToken(string token)
        {
            token = token.Trim();
            if (token.Length == 0) return 0;
            if (token.Length == 1)
            {
                char c = char.ToUpperInvariant(token[0]);
                if (c is >= 'A' and <= 'Z') return (ushort)c;
                if (c is >= '0' and <= '9') return (ushort)c;
            }

            var t = token.ToLowerInvariant();
            return t switch
            {
                "f1" => 0x70, "f2" => 0x71, "f3" => 0x72, "f4" => 0x73,
                "f5" => 0x74, "f6" => 0x75, "f7" => 0x76, "f8" => 0x77,
                "f9" => 0x78, "f10" => 0x79, "f11" => 0x7A, "f12" => 0x7B,
                "space" => 0x20,
                "enter" or "return" => 0x0D,
                "tab" => 0x09,
                "esc" or "escape" => 0x1B,
                "home" => 0x24,
                "end" => 0x23,
                "pgup" or "pageup" => 0x21,
                "pgdn" or "pagedown" => 0x22,
                "up" => 0x26,
                "down" => 0x28,
                "left" => 0x25,
                "right" => 0x27,
                "backspace" or "back" => 0x08,
                "delete" or "del" => 0x2E,
                _ => 0
            };
        }

        /// <summary>组合键注入：优先扫描码（KEYEVENTF_SCANCODE），与 AHK SendEvent / 物理键盘更接近，利于全局热键。</summary>
        private static bool TryGetScanForCombo(ushort vk, out ushort scan, out bool extended)
        {
            scan = 0;
            extended = false;
            uint m = MouseInputNative.MapVirtualKey(vk, MouseInputNative.MAPVK_VK_TO_VSC_EX);
            if (m == 0)
            {
                m = MouseInputNative.MapVirtualKey(vk, MouseInputNative.MAPVK_VK_TO_VSC);
                if (m == 0) return false;
                scan = (ushort)(m & 0xFF);
                return scan != 0;
            }

            scan = (ushort)(m & 0xFF);
            extended = (m & 0x100) != 0 || ((m >> 8) & 0xFF) == 0xE0;
            if (!extended)
                extended = IsExtendedVkForScanCombo(vk);
            return scan != 0;
        }

        private static bool IsExtendedVkForScanCombo(ushort vk) => vk switch
        {
            0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 => true, // prior/next/home/end/arrows
            0x2D or 0x2E => true, // insert delete
            0x5B or 0x5C or 0x5D => true, // LWin RWin Apps
            0x6F => true, // numpad divide
            0xA3 or 0xA5 => true, // RCtrl RAlt
            _ => false
        };

        private static MouseInputNative.INPUT KeyDownInputForCombo(ushort vk)
        {
            if (TryGetScanForCombo(vk, out ushort scan, out bool ext))
            {
                // KEYEVENTF_SCANCODE 时 wVk 须为 0（与 MSDN 一致）；非0 时部分环境下修饰键 KEYUP 无法清掉聚合态，表现为 Shift 卡住。
                uint f = MouseInputNative.KEYEVENTF_SCANCODE;
                if (ext) f |= MouseInputNative.KEYEVENTF_EXTENDEDKEY;
                return new MouseInputNative.INPUT
                {
                    type = MouseInputNative.INPUT_KEYBOARD,
                    U = new MouseInputNative.InputUnion
                    {
                        ki = new MouseInputNative.KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = scan,
                            dwFlags = f,
                            time = 0,
                            dwExtraInfo = MappingSyntheticExtraInfoPtr
                        }
                    }
                };
            }

            return KeyDownInput(vk);
        }

        private static MouseInputNative.INPUT KeyUpInputForCombo(ushort vk)
        {
            if (TryGetScanForCombo(vk, out ushort scan, out bool ext))
            {
                uint f = MouseInputNative.KEYEVENTF_SCANCODE | MouseInputNative.KEYEVENTF_KEYUP;
                if (ext) f |= MouseInputNative.KEYEVENTF_EXTENDEDKEY;
                return new MouseInputNative.INPUT
                {
                    type = MouseInputNative.INPUT_KEYBOARD,
                    U = new MouseInputNative.InputUnion
                    {
                        ki = new MouseInputNative.KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = scan,
                            dwFlags = f,
                            time = 0,
                            dwExtraInfo = MappingSyntheticExtraInfoPtr
                        }
                    }
                };
            }

            return KeyUpInput(vk);
        }

        /// <summary>与 <see cref="KeyDownInputForCombo"/> 相同扫描码路径，但 dwExtraInfo=0，供「修饰键 External + 主键扫描码」单主键组合键使用。</summary>
        private static MouseInputNative.INPUT KeyDownInputForComboExternal(ushort vk)
        {
            if (TryGetScanForCombo(vk, out ushort scan, out bool ext))
            {
                uint f = MouseInputNative.KEYEVENTF_SCANCODE;
                if (ext) f |= MouseInputNative.KEYEVENTF_EXTENDEDKEY;
                return new MouseInputNative.INPUT
                {
                    type = MouseInputNative.INPUT_KEYBOARD,
                    U = new MouseInputNative.InputUnion
                    {
                        ki = new MouseInputNative.KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = scan,
                            dwFlags = f,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                };
            }

            return KeyDownInputExternal(vk);
        }

        private static MouseInputNative.INPUT KeyUpInputForComboExternal(ushort vk)
        {
            if (TryGetScanForCombo(vk, out ushort scan, out bool ext))
            {
                uint f = MouseInputNative.KEYEVENTF_SCANCODE | MouseInputNative.KEYEVENTF_KEYUP;
                if (ext) f |= MouseInputNative.KEYEVENTF_EXTENDEDKEY;
                return new MouseInputNative.INPUT
                {
                    type = MouseInputNative.INPUT_KEYBOARD,
                    U = new MouseInputNative.InputUnion
                    {
                        ki = new MouseInputNative.KEYBDINPUT
                        {
                            wVk = 0,
                            wScan = scan,
                            dwFlags = f,
                            time = 0,
                            dwExtraInfo = IntPtr.Zero
                        }
                    }
                };
            }

            return KeyUpInputExternal(vk);
        }

        private static bool SendInputsSequential(IReadOnlyList<MouseInputNative.INPUT> inputs, int interDelayMs)
        {
            bool ok = true;
            for (int i = 0; i < inputs.Count; i++)
            {
                ok &= SendInputs(new[] { inputs[i] });
                if (i < inputs.Count - 1 && interDelayMs > 0)
                    System.Threading.Thread.Sleep(interDelayMs);
            }

            return ok;
        }

        private static MouseInputNative.INPUT KeyDownInput(ushort vk) => new()
        {
            type = MouseInputNative.INPUT_KEYBOARD,
            U = new MouseInputNative.InputUnion
            {
                ki = new MouseInputNative.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = MappingSyntheticExtraInfoPtr
                }
            }
        };

        private static MouseInputNative.INPUT KeyUpInput(ushort vk) => new()
        {
            type = MouseInputNative.INPUT_KEYBOARD,
            U = new MouseInputNative.InputUnion
            {
                ki = new MouseInputNative.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = MouseInputNative.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = MappingSyntheticExtraInfoPtr
                }
            }
        };

        /// <summary>发往外部程序（Gal等）的组合键：dwExtraInfo=0，避免引擎/轮询忽略 KEYUP 导致 Shift 一直快进。</summary>
        private static MouseInputNative.INPUT KeyDownInputExternal(ushort vk) => new()
        {
            type = MouseInputNative.INPUT_KEYBOARD,
            U = new MouseInputNative.InputUnion
            {
                ki = new MouseInputNative.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = 0,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        private static MouseInputNative.INPUT KeyUpInputExternal(ushort vk) => new()
        {
            type = MouseInputNative.INPUT_KEYBOARD,
            U = new MouseInputNative.InputUnion
            {
                ki = new MouseInputNative.KEYBDINPUT
                {
                    wVk = vk,
                    wScan = 0,
                    dwFlags = MouseInputNative.KEYEVENTF_KEYUP,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                }
            }
        };

        private static bool ModifiersIncludeShift(IReadOnlyList<ushort> modifiers)
        {
            foreach (ushort m in modifiers)
            {
                if (m is 0x10 or 0xA0 or 0xA1)
                    return true;
            }

            return false;
        }

        /// <summary>部分 AVG 在注入修饰键后仍认为 Shift 按下；短延迟后批量补发 KEYUP 清左右与聚合 Shift。</summary>
        private static void NudgeShiftReleasedForGalgames(IReadOnlyList<ushort> modifiers)
        {
            if (!ModifiersIncludeShift(modifiers)) return;
            try
            {
                System.Threading.Thread.Sleep(NudgeShiftAfterComboMs);
                SendInputsSequential(new[]
                {
                    KeyUpInputExternal(0xA1),
                    KeyUpInputExternal(0xA0),
                    KeyUpInputExternal(0x10)
                }, ComboKeyInterDownMs);
            }
            catch { }
        }

        private static bool _uipiWarned;

        private static bool SendInputs(MouseInputNative.INPUT[] inputs)
        {
            int size = Marshal.SizeOf<MouseInputNative.INPUT>();
            uint sent = MouseInputNative.SendInput((uint)inputs.Length, inputs, size);
            if (sent == inputs.Length) return true;

            int err = Marshal.GetLastWin32Error();
            if (sent == 0 && !_uipiWarned)
            {
                _uipiWarned = true;
                Log.Warning("SendInput 被系统拒绝。sent={Sent}/{Total} LastError={Err}（若 LastError=5 通常是目标以管理员/更高完整性运行；若 LastError=0 多半是安全/外设驱动在拦截）",
                    sent, inputs.Length, err);
            }
            else
            {
                Log.Warning("SendInput 部分失败: {Sent}/{Total} LastError={Err}", sent, inputs.Length, err);
            }
            return false;
        }
    }
}
