using System;
using System.Runtime.InteropServices;
using lunagalLauncher.Data;
using Serilog;

namespace lunagalLauncher.Services
{
    /// <summary>
    /// <see cref="MouseMappingEngine"/> 的键盘钩子部分：
    /// 低层 WH_KEYBOARD_LL 安装/卸载、回调分发，按下/抬起 FSM，以及连击时序追踪。
    ///
    /// <para>
    /// 从主文件 <c>MouseMappingEngine.cs</c> 切出来做物理拆分。字段（如 <c>_kbdHook</c>、<c>_kbdProc</c>、
    /// <c>_kbdFsm</c>、<c>_physicalKbdDown</c>、<c>_config</c>）仍在主文件中声明、由同一 partial class 共享。
    /// </para>
    ///
    /// <para>
    /// <c>IsForegroundOwnProcess</c> 虽然也被鼠标钩子处的 <c>ShouldSkipMouseMappingBecauseCursorOnOwnForegroundUi</c>
    /// 调用，但它本质是"外部窗口上下文"判断、与键盘钩子的入站判断更紧密，也一并搬到此处；partial class 内部
    /// 方法互可见，外部调用点无需改动。
    /// </para>
    /// </summary>
    internal static partial class MouseMappingEngine
    {
        private static void InstallKeyboardHook()
        {
            if (_kbdHook != IntPtr.Zero) return;
            _kbdProc = KeyboardHookCallback;
            _kbdHook = MouseInputNative.SetWindowsHookExKeyboard(MouseInputNative.WH_KEYBOARD_LL, _kbdProc,
                IntPtr.Zero, 0);
            if (_kbdHook == IntPtr.Zero)
                Log.Error("SetWindowsHookEx(WH_KEYBOARD_LL) 失败");
            else
            {
                for (int i = 0; i < 256; i++)
                {
                    System.Threading.Volatile.Write(ref _physicalKbdDown[i],
                        (MouseInputNative.GetAsyncKeyState(i) & 0x8000) != 0 ? 1 : 0);
                }
                Log.Information("鼠标映射：键盘低层钩子已安装（含物理键状态追踪）");
            }
        }

        private static void UninstallKeyboardHook()
        {
            _kbdFsm.Clear();
            for (int i = 0; i < 256; i++)
                System.Threading.Volatile.Write(ref _physicalKbdDown[i], 0);
            if (_kbdHook == IntPtr.Zero) return;
            MouseInputNative.UnhookWindowsHookEx(_kbdHook);
            _kbdHook = IntPtr.Zero;
            _kbdProc = null;
            Log.Information("鼠标映射：键盘低层钩子已卸载");
        }

        /// <summary>前台窗口是否属于本进程（本应用内录入快捷键时不拦截）。</summary>
        private static bool IsForegroundOwnProcess()
        {
            var h = MouseInputNative.GetForegroundWindow();
            if (h == IntPtr.Zero) return false;
            MouseInputNative.GetWindowThreadProcessId(h, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }

        private static IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || _config == null || !_config.GlobalEnabled)
                return MouseInputNative.CallNextHookEx(_kbdHook, nCode, wParam, lParam);

            var info = Marshal.PtrToStructure<MouseInputNative.KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)info.vkCode;
            int msg = wParam.ToInt32();

            bool kbdInjected = (info.flags & MouseInputNative.LLKHF_INJECTED) != 0;
            bool ourKbdSynthetic = kbdInjected && info.dwExtraInfo.ToUInt64() == InputSimulatorHelper.MappingSyntheticExtraInfoValue;

            if (!ourKbdSynthetic)
            {
                if (msg == MouseInputNative.WM_KEYDOWN || msg == MouseInputNative.WM_SYSKEYDOWN)
                    System.Threading.Volatile.Write(ref _physicalKbdDown[vk & 0xFF], 1);
                else if (msg == MouseInputNative.WM_KEYUP || msg == MouseInputNative.WM_SYSKEYUP)
                    System.Threading.Volatile.Write(ref _physicalKbdDown[vk & 0xFF], 0);
            }

            if (kbdInjected)
                return MouseInputNative.CallNextHookEx(_kbdHook, nCode, wParam, lParam);

            if (IsForegroundOwnProcess())
                return MouseInputNative.CallNextHookEx(_kbdHook, nCode, wParam, lParam);

            if (!MouseInputNative.GetCursorPos(out var pt))
                return MouseInputNative.CallNextHookEx(_kbdHook, nCode, wParam, lParam);

            if (msg == MouseInputNative.WM_KEYDOWN || msg == MouseInputNative.WM_SYSKEYDOWN)
                return OnKeyboardKeyDown(vk, pt, nCode, wParam, lParam);
            if (msg == MouseInputNative.WM_KEYUP || msg == MouseInputNative.WM_SYSKEYUP)
                return OnKeyboardKeyUp(vk, pt, nCode, wParam, lParam);

            return MouseInputNative.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
        }

        private static IntPtr OnKeyboardKeyDown(int vk, MouseInputNative.POINT pt, int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (!_kbdFsm.TryGetValue(vk, out var fsm))
            {
                fsm = new ButtonFsm();
                _kbdFsm[vk] = fsm;
            }

            if (!fsm.Down)
            {
                fsm.Down = true;
                fsm.DownMs = Environment.TickCount64;
                fsm.DownX = pt.X;
                fsm.DownY = pt.Y;
                fsm.MovedTooFar = false;
                fsm.DownSuppressed = false;
            }

            return MouseInputNative.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
        }

        private static IntPtr OnKeyboardKeyUp(int vk, MouseInputNative.POINT pt, int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (!_kbdFsm.TryGetValue(vk, out var fsm) || !fsm.Down)
                return MouseInputNative.CallNextHookEx(_kbdHook, nCode, wParam, lParam);

            long duration = Environment.TickCount64 - fsm.DownMs;
            fsm.Down = false;

            var rules = MatchingKeyboardRules(vk);
            UpdateClickSequenceKbd(vk, MouseDoubleClickGroupingIntervalMs());

            MouseMappingRule? matched = null;
            foreach (var rule in rules)
            {
                if (fsm.DownSuppressed && rule.Action == MouseActionKind.MouseButton)
                    continue;

                if (!ContextOk(rule, pt)) continue;
                if (ShouldSkipForGlobalSpatialContext(pt)) continue;
                if (RuleSkippedDueToMoveTooFar(rule, fsm.MovedTooFar, duration))
                    continue;

                bool isHold = duration >= EffectiveClickVsHoldThresholdMs(rule);
                bool triggerOk = rule.Trigger switch
                {
                    MouseTriggerKind.Click => !isHold,
                    MouseTriggerKind.Hold => isHold,
                    MouseTriggerKind.DoubleClick => fsm.UpSequence >= 2,
                    MouseTriggerKind.MultiClick => fsm.UpSequence >= 2,
                    _ => false
                };

                if (!triggerOk) continue;

                matched = rule;
                break;
            }

            if (matched == null)
            {
                if (fsm.DownSuppressed)
                {
                    fsm.DownSuppressed = false;
                    return (IntPtr)1;
                }

                return MouseInputNative.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
            }

            bool downWasSuppressed = fsm.DownSuppressed;
            if (matched.Trigger is MouseTriggerKind.DoubleClick or MouseTriggerKind.MultiClick)
                fsm.UpSequence = 0;

            if (!MayInterceptOriginalInput(matched, pt))
            {
                if (downWasSuppressed)
                {
                    fsm.DownSuppressed = false;
                    return (IntPtr)1;
                }

                return MouseInputNative.CallNextHookEx(_kbdHook, nCode, wParam, lParam);
            }

            fsm.DownSuppressed = false;

            var matchedForBg = matched;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    InputSimulatorHelper.SendKeyUpOnly((ushort)vk);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "键盘物理键抬起：SendKeyUpOnly 异常（忽略）");
                }

                try
                {
                    ExecuteAction(matchedForBg);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "ExecuteAction 异常（已吞掉）");
                }
            });

            return (IntPtr)1;
        }

        private static void UpdateClickSequenceKbd(int vk, int maxIntervalMs)
        {
            if (!_kbdFsm.TryGetValue(vk, out var fsm)) return;
            long now = Environment.TickCount64;
            if (now - fsm.LastUpMs <= maxIntervalMs)
                fsm.UpSequence++;
            else
                fsm.UpSequence = 1;
            fsm.LastUpMs = now;
        }
    }
}
