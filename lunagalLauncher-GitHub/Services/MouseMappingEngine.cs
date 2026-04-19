using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using lunagalLauncher.Data;
using lunagalLauncher;
using Microsoft.UI.Dispatching;
using Serilog;
using WinRT.Interop;

namespace lunagalLauncher.Services
{
    /// <summary>
    /// 低层鼠标/键盘钩子与规则匹配（安装钩子的线程上回调）
    /// </summary>
    internal static class MouseMappingEngine
    {
        private static IntPtr _mouseHook = IntPtr.Zero;
        private static MouseInputNative.LowLevelMouseProc? _proc;
        private static IntPtr _kbdHook = IntPtr.Zero;
        private static MouseInputNative.LowLevelKeyboardProc? _kbdProc;
        private static MouseMappingConfig? _config;
        private static DispatcherQueue? _dispatcher;
        private static readonly DispatcherQueueTimer?[] _repeatTimers = new DispatcherQueueTimer?[5];
        private static readonly ButtonFsm[] _btn = { new(), new(), new(), new(), new() };

        /// <summary>键盘物理键的状态机（vk → 与鼠标 _btn 索引对称）</summary>
        private static readonly Dictionary<int, ButtonFsm> _kbdFsm = new();

        /// <summary>Raw Input 物理键：键 = RI 按下标志(0..0xFFFF) 或 (ulRawButtons 掩码 &lt;&lt; 32)</summary>
        private static readonly Dictionary<ulong, ButtonFsm> _rawFsm = new();

        /// <summary>RIM_TYPEHID：每设备上一帧 HID 报告哈希</summary>
        /// <summary>上一帧 HID 稳定指纹（按设备 + ReportId，避免同设备多报告互相覆盖）。</summary>
        private static readonly Dictionary<(IntPtr hDevice, byte reportId), ulong> _hidLastSig = new();

        /// <summary>轮询连发：需检测的键盘 vk（RepeatWhileHeld + 物理键为键盘）</summary>
        private static int[] _keyboardRepeatVks = Array.Empty<int>();

        private static readonly Dictionary<int, long> _kbdPollHeldMs = new();
        private static readonly Dictionary<int, bool> _kbdPollFiring = new();

        /// <summary>
        /// 物理鼠标按键状态（由 LL 钩子在非注入事件时更新），不受 SendInput 注入干扰。
        /// 0=未按下, 1=按下。使用 int 以兼容 Volatile.Read/Write 原子操作。
        /// </summary>
        private static readonly int[] _physicalDown = new int[5];

        /// <summary>
        /// 物理键盘键状态（由键盘 LL 钩子对非注入 KEYDOWN/UP 更新）。用于连发间隔=1 的按住模式，
        /// 避免 SendInput 注入后 GetAsyncKeyState 与物理键混淆导致「松手后仍按住」。
        /// </summary>
        private static readonly int[] _physicalKbdDown = new int[256];

        internal static void SetDispatcherQueue(DispatcherQueue? dq) => _dispatcher = dq;

        private static volatile bool _pollThreadStarted;

        internal static void Apply(MouseMappingConfig config)
        {
            if (config.Rules != null)
                MouseMappingMigration.NormalizeActionWhenKeyComboTextPresent(config.Rules);
            _config = config;
            UninstallMouseHook();
            UninstallKeyboardHook();
            int enabledCount = config.Rules?.Count(r => r.Enabled) ?? 0;
            Log.Information("鼠标映射引擎：Apply GlobalEnabled={G} 规则总数={N} 启用={E}",
                config.GlobalEnabled, config.Rules?.Count ?? 0, enabledCount);
            TestSendInputCapability();

            if (!config.GlobalEnabled || config.Rules == null || !config.Rules.Any(r => r.Enabled))
            {
                Log.Information("鼠标映射引擎：未安装钩子（已关闭或无规则）");
                _keyboardRepeatVks = Array.Empty<int>();
                EnsurePollingThread();
                return;
            }

            bool needMouse = config.Rules.Any(r => r.Enabled && r.PhysicalKeyVirtualKey == 0
                    && r.PhysicalMouseRawButtonFlag == 0 && r.PhysicalMouseRawButtonsMask == 0
                    && r.PhysicalMouseHidSignature == 0);
            bool needRaw = config.Rules.Any(r => r.Enabled && r.PhysicalKeyVirtualKey == 0
                    && (r.PhysicalMouseRawButtonFlag != 0 || r.PhysicalMouseRawButtonsMask != 0
                        || r.PhysicalMouseHidSignature != 0));
            bool needKeyboard = config.Rules.Any(r => r.Enabled && r.PhysicalKeyVirtualKey != 0);
            _keyboardRepeatVks = config.Rules
                .Where(r => r.Enabled
                            && r.PhysicalKeyVirtualKey != 0
                            && r.Behavior == MouseBehaviorMode.RepeatWhileHeld
                            && (r.Trigger == MouseTriggerKind.Click || r.Trigger == MouseTriggerKind.Hold))
                .Select(r => r.PhysicalKeyVirtualKey)
                .Distinct()
                .ToArray();

            // 鼠标 LL 钩子：追踪物理鼠标键状态 + 非连发规则；键盘 LL 钩子：物理键为 vk 的规则。
            // RepeatWhileHeld 由轮询线程处理，避免回调里 SendInput 重入超时。
            if (needMouse)
                InstallMouseHook();
            if (needKeyboard)
                InstallKeyboardHook();

            Log.Information("鼠标映射引擎：钩子 鼠标={M} 键盘={K} 键盘连发轮询 vk 数={N} RawInput={R}",
                needMouse, needKeyboard, _keyboardRepeatVks.Length, needRaw);
            EnsurePollingThread();

            if (needRaw)
            {
                try
                {
                    if (Microsoft.UI.Xaml.Application.Current is App app && app.window != null)
                    {
                        var hwnd = WindowNative.GetWindowHandle(app.window);
                        RawInputMouseBridge.EnsureInstalled(hwnd);
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "鼠标映射：安装 Raw Input 桥接失败（忽略）");
                }
            }
        }

        /// <summary>
        /// 检测物理按键是否按下。优先使用 LL 钩子追踪的状态（不受注入干扰），
        /// 钩子未安装时回退到 GetAsyncKeyState（注入期间可能不准确）。
        /// </summary>
        private static bool IsPhysicallyDown(int b, int vkMouse)
        {
            if (_mouseHook != IntPtr.Zero)
                return System.Threading.Volatile.Read(ref _physicalDown[b]) == 1;
            return (MouseInputNative.GetAsyncKeyState(vkMouse) & 0x8000) != 0;
        }

        /// <summary>物理键盘键是否按下（优先钩子追踪，避免与注入的同名键状态混淆）。</summary>
        private static bool IsPhysicalKeyboardDown(int vk)
        {
            int i = vk & 0xFF;
            if (_kbdHook != IntPtr.Zero)
                return System.Threading.Volatile.Read(ref _physicalKbdDown[i]) == 1;
            return (MouseInputNative.GetAsyncKeyState(vk) & 0x8000) != 0;
        }

        /// <summary>
        /// 连发间隔=1 的鼠标按住循环：钩子+Raw 共同维护 _physicalDown；物理槽与模拟槽不同时可用 GetAsyncKeyState 交叉校验。
        /// </summary>
        private static bool IsPhysicalMouseHoldStillPressed(int b, int vkMouse, MouseMappingRule rule)
        {
            if (_mouseHook == IntPtr.Zero)
                return (MouseInputNative.GetAsyncKeyState(vkMouse) & 0x8000) != 0;

            bool hookDown = System.Threading.Volatile.Read(ref _physicalDown[b]) == 1;

            if (rule.Action == MouseActionKind.MouseButton && rule.SimulatedMouseButton != b)
            {
                // 异槽：物理 vk 不被合成键污染，Async 可作辅助；仍以钩子为主。
                bool asyncDown = (MouseInputNative.GetAsyncKeyState(vkMouse) & 0x8000) != 0;
                return hookDown && asyncDown;
            }

            if (rule.Action == MouseActionKind.KeyCombo)
            {
                if (hookDown) return true;
                // Raw 可能比 LL 先误清 _physicalDown；鼠标 VK 不受本程序合成键盘影响，可作交叉校验。
                return (MouseInputNative.GetAsyncKeyState(vkMouse) & 0x8000) != 0;
            }

            // 同槽 MouseButton（如左→左）：只用钩子态，不用 Async（同上，避免合成左键污染）。
            return hookDown;
        }

        /// <summary>连发间隔=1 的键盘按住循环：模拟鼠标时不污染物理 vk，可与 Async 交叉校验；组合键仍以钩子为准避免自注入误判。</summary>
        private static bool IsPhysicalKeyboardHoldStillPressed(int vk, MouseMappingRule rule)
        {
            if (_kbdHook == IntPtr.Zero)
                return (MouseInputNative.GetAsyncKeyState(vk) & 0x8000) != 0;

            bool hookDown = System.Threading.Volatile.Read(ref _physicalKbdDown[vk & 0xFF]) == 1;
            if (rule.Action == MouseActionKind.MouseButton)
            {
                bool asyncDown = (MouseInputNative.GetAsyncKeyState(vk) & 0x8000) != 0;
                return hookDown && asyncDown;
            }

            return hookDown;
        }

        /// <summary>轮询/初始化用：物理键槽位 0–4 → Win32 VK</summary>
        private static int VkForPhysicalMouseSlot(int b) =>
            b switch
            {
                1 => MouseInputNative.VK_RBUTTON,
                2 => MouseInputNative.VK_MBUTTON,
                3 => MouseInputNative.VK_XBUTTON1,
                4 => MouseInputNative.VK_XBUTTON2,
                _ => MouseInputNative.VK_LBUTTON
            };

        /// <summary>
        /// Raw Input 的 UP 沿有时早于 LL 钩子或误报；仅当 GetAsyncKeyState 也认为已松开时，才把 _physicalDown 清 0，
        /// 避免「按住 Ctrl」循环立刻退出且系统仍出现一次完整右键。
        /// </summary>
        private static void ClearPhysicalDownFromRawIfHardwareReleased(int slot01234)
        {
            int vk = VkForPhysicalMouseSlot(slot01234);
            if ((MouseInputNative.GetAsyncKeyState(vk) & 0x8000) == 0)
                System.Threading.Volatile.Write(ref _physicalDown[slot01234], 0);
        }

        /// <summary>
        /// 确保轮询线程在跑（只启动一次，永不停止）。线程内部自己根据 _config 判断是否工作。
        /// </summary>
        private static void EnsurePollingThread()
        {
            if (_pollThreadStarted) return;
            _pollThreadStarted = true;
            var t = new System.Threading.Thread(PollingLoop)
            {
                IsBackground = true,
                Name = "MouseRepeatPoller",
                Priority = System.Threading.ThreadPriority.AboveNormal
            };
            t.Start();
            Log.Information("鼠标映射：轮询连发线程已启动（不依赖 LL 钩子，兼容 360 等安全软件）");
        }

        /// <summary>
        /// 纯轮询连发：每 20ms 检查一次物理鼠标按键状态（优先读取 LL 钩子追踪的 _physicalDown，
        /// 不受 SendInput 注入干扰；钩子不可用时回退到 GetAsyncKeyState）。
        /// 按住超过 HoldThreshold 后开始连发 UP→DOWN 循环。
        /// 不抑制物理事件，最大限度兼容 360 等安全软件。
        /// </summary>
        private static void PollingLoop()
        {
            var btnHeld = new long[5];
            var btnFiring = new bool[5];

            while (true)
            {
                try
                {
                    if (_config == null || !_config.GlobalEnabled)
                    {
                        System.Threading.Thread.Sleep(100);
                        continue;
                    }

                    for (int b = 0; b < 5; b++)
                    {
                        int vk = VkForPhysicalMouseSlot(b);
                        bool physicallyDown = IsPhysicallyDown(b, vk);

                        if (!physicallyDown)
                        {
                            btnHeld[b] = 0;
                            btnFiring[b] = false;
                            continue;
                        }

                        if (!MouseInputNative.GetCursorPos(out var pt))
                        {
                            System.Threading.Thread.Sleep(20);
                            continue;
                        }

                        // 仅当「光标命中本进程」且「前台也是本进程」时才跳过轮询，避免 WinUI 透明层与下层应用重叠时
                        // WindowFromPoint 误判为本进程，导致按住连发永远不开始。
                        if (ShouldSkipPollingMappingBecauseOverOwnUi(pt))
                        {
                            btnHeld[b] = 0;
                            btnFiring[b] = false;
                            continue;
                        }

                        // 找匹配的 RepeatWhileHeld 规则（与单次触发一致：进程过滤、全局任务栏/边缘禁用）
                        var rules = MatchingRules(b);
                        MouseMappingRule? rule = null;
                        foreach (var r in rules)
                        {
                            if (r.Behavior != MouseBehaviorMode.RepeatWhileHeld) continue;
                            if (r.Trigger != MouseTriggerKind.Click && r.Trigger != MouseTriggerKind.Hold) continue;
                            if (!ContextOk(r, pt)) continue;
                            if (ShouldSkipForGlobalSpatialContext(pt)) continue;
                            rule = r;
                            break;
                        }
                        if (rule == null)
                        {
                            btnHeld[b] = 0;
                            continue;
                        }

                        // 移动容差检查：按住后鼠标移动超过规则设定的像素距离时不触发连发（视为拖拽）
                        if (_btn[b].MovedTooFar)
                        {
                            btnHeld[b] = 0;
                            continue;
                        }

                        long now = Environment.TickCount64;
                        if (btnHeld[b] == 0)
                            btnHeld[b] = now;

                        long held = now - btnHeld[b];
                        int threshold = rule.Trigger == MouseTriggerKind.Hold ? rule.HoldThresholdMs : 0;

                        if (held < threshold)
                        {
                            System.Threading.Thread.Sleep(10);
                            continue;
                        }

                        btnFiring[b] = true;
                        int actionBtn = rule.SimulatedMouseButton;
                        bool isXButton = actionBtn >= 3;
                        uint meDown, meUp, meData = 0;
                        if (isXButton)
                        {
                            meDown = MouseInputNative.MOUSEEVENTF_XDOWN;
                            meUp = MouseInputNative.MOUSEEVENTF_XUP;
                            meData = actionBtn == 3 ? MouseInputNative.XBUTTON1 : MouseInputNative.XBUTTON2;
                        }
                        else
                        {
                            meDown = actionBtn switch { 1 => MouseInputNative.MOUSEEVENTF_RIGHTDOWN, 2 => MouseInputNative.MOUSEEVENTF_MIDDLEDOWN, _ => MouseInputNative.MOUSEEVENTF_LEFTDOWN };
                            meUp = actionBtn switch { 1 => MouseInputNative.MOUSEEVENTF_RIGHTUP, 2 => MouseInputNative.MOUSEEVENTF_MIDDLEUP, _ => MouseInputNative.MOUSEEVENTF_LEFTUP };
                        }

                        // 连发间隔=1：模拟鼠标键为「按下→轮询物理松开→抬起」；钩子忽略本程序合成注入，_physicalDown/Raw 可追踪真实硬件。
                        if (rule.RepeatIntervalMs == 1)
                        {
                            if (_mouseHook == IntPtr.Zero)
                            {
                                Log.Warning("鼠标映射：间隔=1 按住模式需要鼠标钩子追踪物理键，钩子未安装，已跳过");
                                btnHeld[b] = 0;
                                btnFiring[b] = false;
                                continue;
                            }

                            Log.Information("鼠标映射：轮询按住（间隔=1）开始 btn={B} 规则={N}", b, rule.Name);
                            if (rule.Action == MouseActionKind.KeyCombo)
                            {
                                if (InputSimulatorHelper.SendKeyComboHoldDown(rule.KeyComboText))
                                {
                                    try
                                    {
                                        while (_config != null && _config.GlobalEnabled && IsPhysicalMouseHoldStillPressed(b, vk, rule))
                                            System.Threading.Thread.Sleep(10);
                                    }
                                    finally
                                    {
                                        InputSimulatorHelper.SendKeyComboHoldUp(rule.KeyComboText);
                                    }
                                }
                            }
                            else
                            {
                                // MouseButton：同槽（左→左）与异槽均「按下→轮询物理松开→抬起」。
                                // 钩子已忽略本程序带 MappingSyntheticExtraInfo 的注入，_physicalDown 仍可反映真实硬件（间隔=1 表示按住不放，非脉冲连点）。
                                if (isXButton)
                                    MouseInputNative.mouse_event(meDown, 0, 0, meData, InputSimulatorHelper.MappingSyntheticExtraInfoPtr);
                                else
                                    InputSimulatorHelper.SendMouseDown(actionBtn);
                                try
                                {
                                    while (_config != null && _config.GlobalEnabled && IsPhysicalMouseHoldStillPressed(b, vk, rule))
                                        System.Threading.Thread.Sleep(10);
                                }
                                finally
                                {
                                    if (isXButton)
                                        MouseInputNative.mouse_event(meUp, 0, 0, meData, InputSimulatorHelper.MappingSyntheticExtraInfoPtr);
                                    else
                                        InputSimulatorHelper.SendMouseUp(actionBtn);
                                }
                            }

                            Log.Information("鼠标映射：轮询按住（间隔=1）结束 btn={B}", b);
                            btnHeld[b] = 0;
                            btnFiring[b] = false;
                            continue;
                        }

                        int totalMs = Math.Max(60, rule.RepeatIntervalMs);
                        Log.Information("鼠标映射：轮询连发开始 btn={B} 规则={N} 间隔={I}ms", b, rule.Name, totalMs);

                        int clickCount = 0;
                        while (_config != null && _config.GlobalEnabled)
                        {
                            if (rule.Action == MouseActionKind.KeyCombo)
                            {
                                InputSimulatorHelper.SendKeyCombo(rule.KeyComboText);
                            }
                            else
                            {
                                MouseInputNative.mouse_event(meDown, 0, 0, meData, InputSimulatorHelper.MappingSyntheticExtraInfoPtr);
                                System.Threading.Thread.Sleep(20);
                                MouseInputNative.mouse_event(meUp, 0, 0, meData, InputSimulatorHelper.MappingSyntheticExtraInfoPtr);
                                if (!isXButton)
                                    try { InputSimulatorHelper.PostMouseClickToForeground(actionBtn); } catch { }
                            }

                            clickCount++;
                            if (clickCount <= 5 || clickCount % 10 == 0)
                                Log.Debug("鼠标映射：连发 #{C} btn={B} action={A}", clickCount, b, rule.Action);

                            System.Threading.Thread.Sleep(totalMs);

                            if (!IsPhysicallyDown(b, vk))
                            {
                                Log.Information("鼠标映射：轮询连发停止 btn={B} 总点击={C}（物理键已松开）", b, clickCount);
                                break;
                            }
                        }
                        btnHeld[b] = 0;
                        btnFiring[b] = false;
                        continue;
                    }

                    // 键盘物理键的 RepeatWhileHeld：逻辑与鼠标轮询一致，用 GetAsyncKeyState 检测按住
                    foreach (int kbdVk in _keyboardRepeatVks)
                    {
                        if (!IsPhysicalKeyboardDown(kbdVk))
                        {
                            _kbdPollHeldMs[kbdVk] = 0;
                            _kbdPollFiring[kbdVk] = false;
                            continue;
                        }

                        if (!MouseInputNative.GetCursorPos(out var ptKbd))
                        {
                            System.Threading.Thread.Sleep(20);
                            continue;
                        }

                        if (ShouldSkipPollingMappingBecauseOverOwnUi(ptKbd))
                        {
                            _kbdPollHeldMs[kbdVk] = 0;
                            _kbdPollFiring[kbdVk] = false;
                            continue;
                        }

                        var rulesK = MatchingKeyboardRules(kbdVk);
                        MouseMappingRule? ruleK = null;
                        foreach (var r in rulesK)
                        {
                            if (r.Behavior != MouseBehaviorMode.RepeatWhileHeld) continue;
                            if (r.Trigger != MouseTriggerKind.Click && r.Trigger != MouseTriggerKind.Hold) continue;
                            if (!ContextOk(r, ptKbd)) continue;
                            if (ShouldSkipForGlobalSpatialContext(ptKbd)) continue;
                            ruleK = r;
                            break;
                        }

                        if (ruleK == null)
                        {
                            _kbdPollHeldMs[kbdVk] = 0;
                            continue;
                        }

                        long nowK = Environment.TickCount64;
                        if (!_kbdPollHeldMs.TryGetValue(kbdVk, out long heldStart) || heldStart == 0)
                            _kbdPollHeldMs[kbdVk] = nowK;

                        heldStart = _kbdPollHeldMs[kbdVk];
                        long heldK = nowK - heldStart;
                        int thresholdK = ruleK.Trigger == MouseTriggerKind.Hold ? ruleK.HoldThresholdMs : 0;

                        if (heldK < thresholdK)
                        {
                            System.Threading.Thread.Sleep(10);
                            continue;
                        }

                        _kbdPollFiring[kbdVk] = true;
                        int actionBtnK = ruleK.SimulatedMouseButton;
                        bool isXButtonK = actionBtnK >= 3;
                        uint meDownK, meUpK, meDataK = 0;
                        if (isXButtonK)
                        {
                            meDownK = MouseInputNative.MOUSEEVENTF_XDOWN;
                            meUpK = MouseInputNative.MOUSEEVENTF_XUP;
                            meDataK = actionBtnK == 3 ? MouseInputNative.XBUTTON1 : MouseInputNative.XBUTTON2;
                        }
                        else
                        {
                            meDownK = actionBtnK switch { 1 => MouseInputNative.MOUSEEVENTF_RIGHTDOWN, 2 => MouseInputNative.MOUSEEVENTF_MIDDLEDOWN, _ => MouseInputNative.MOUSEEVENTF_LEFTDOWN };
                            meUpK = actionBtnK switch { 1 => MouseInputNative.MOUSEEVENTF_RIGHTUP, 2 => MouseInputNative.MOUSEEVENTF_MIDDLEUP, _ => MouseInputNative.MOUSEEVENTF_LEFTUP };
                        }

                        if (ruleK.RepeatIntervalMs == 1)
                        {
                            if (_kbdHook == IntPtr.Zero)
                            {
                                Log.Warning("鼠标映射：间隔=1 键盘按住模式需要键盘钩子追踪物理键，钩子未安装，已跳过 vk={Vk}", kbdVk);
                                _kbdPollHeldMs[kbdVk] = 0;
                                _kbdPollFiring[kbdVk] = false;
                                continue;
                            }

                            Log.Information("鼠标映射：键盘轮询按住（间隔=1）开始 vk={Vk} 规则={N}", kbdVk, ruleK.Name);
                            if (ruleK.Action == MouseActionKind.KeyCombo)
                            {
                                if (InputSimulatorHelper.SendKeyComboHoldDown(ruleK.KeyComboText))
                                {
                                    try
                                    {
                                        while (_config != null && _config.GlobalEnabled
                                               && IsPhysicalKeyboardHoldStillPressed(kbdVk, ruleK))
                                            System.Threading.Thread.Sleep(10);
                                    }
                                    finally
                                    {
                                        InputSimulatorHelper.SendKeyComboHoldUp(ruleK.KeyComboText);
                                    }
                                }
                            }
                            else
                            {
                                if (isXButtonK)
                                    MouseInputNative.mouse_event(meDownK, 0, 0, meDataK, InputSimulatorHelper.MappingSyntheticExtraInfoPtr);
                                else
                                    InputSimulatorHelper.SendMouseDown(actionBtnK);
                                while (_config != null && _config.GlobalEnabled
                                       && IsPhysicalKeyboardHoldStillPressed(kbdVk, ruleK))
                                    System.Threading.Thread.Sleep(10);
                                if (isXButtonK)
                                    MouseInputNative.mouse_event(meUpK, 0, 0, meDataK, InputSimulatorHelper.MappingSyntheticExtraInfoPtr);
                                else
                                    InputSimulatorHelper.SendMouseUp(actionBtnK);
                            }

                            Log.Information("鼠标映射：键盘轮询按住（间隔=1）结束 vk={Vk}", kbdVk);
                            _kbdPollHeldMs[kbdVk] = 0;
                            _kbdPollFiring[kbdVk] = false;
                            continue;
                        }

                        int totalMsK = Math.Max(60, ruleK.RepeatIntervalMs);
                        Log.Information("鼠标映射：键盘轮询连发开始 vk={Vk} 规则={N} 间隔={I}ms", kbdVk, ruleK.Name, totalMsK);

                        int clickCountK = 0;
                        while (_config != null && _config.GlobalEnabled)
                        {
                            if (ruleK.Action == MouseActionKind.KeyCombo)
                            {
                                InputSimulatorHelper.SendKeyCombo(ruleK.KeyComboText);
                            }
                            else
                            {
                                MouseInputNative.mouse_event(meDownK, 0, 0, meDataK, InputSimulatorHelper.MappingSyntheticExtraInfoPtr);
                                System.Threading.Thread.Sleep(20);
                                MouseInputNative.mouse_event(meUpK, 0, 0, meDataK, InputSimulatorHelper.MappingSyntheticExtraInfoPtr);
                                if (!isXButtonK)
                                    try { InputSimulatorHelper.PostMouseClickToForeground(actionBtnK); } catch { }
                            }

                            clickCountK++;
                            if (clickCountK <= 5 || clickCountK % 10 == 0)
                                Log.Debug("鼠标映射：键盘连发 #{C} vk={Vk} action={A}", clickCountK, kbdVk, ruleK.Action);

                            System.Threading.Thread.Sleep(totalMsK);

                            if (!IsPhysicalKeyboardDown(kbdVk))
                            {
                                Log.Information("鼠标映射：键盘轮询连发停止 vk={Vk} 总次数={C}", kbdVk, clickCountK);
                                break;
                            }
                        }

                        _kbdPollHeldMs[kbdVk] = 0;
                        _kbdPollFiring[kbdVk] = false;
                    }

                    System.Threading.Thread.Sleep(20);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "鼠标映射：轮询线程异常");
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

        /// <summary>
        /// 执行一次无副作用的 MOUSEEVENTF_MOVE(0,0) 注入，判断 SendInput 是否被系统（安全软件/驱动/UIPI）阻塞。
        /// </summary>
        private static void TestSendInputCapability()
        {
            try
            {
                var inputs = new MouseInputNative.INPUT[1];
                inputs[0].type = MouseInputNative.INPUT_MOUSE;
                inputs[0].U.mi = new MouseInputNative.MOUSEINPUT
                {
                    dx = 0,
                    dy = 0,
                    mouseData = 0,
                    dwFlags = MouseInputNative.MOUSEEVENTF_MOVE,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero
                };
                int size = Marshal.SizeOf<MouseInputNative.INPUT>();
                uint sent = MouseInputNative.SendInput(1, inputs, size);
                int err = Marshal.GetLastWin32Error();
                int ownIntegrity = GetCurrentIntegrityLevel();
                Log.Information("鼠标映射：SendInput 自测 sent={S}/1 cbSize={Sz} err={E} 本进程完整性={I}",
                    sent, size, err, IntegrityToText(ownIntegrity));
                if (sent == 0)
                    Log.Warning("鼠标映射：SendInput 自测失败——系统正在阻塞注入。常见原因：游戏外设/反作弊/安全软件（Razer/Logitech G HUB/雷电模拟器/360/AnyDesk/远程桌面/家长控制等）启用了输入拦截驱动。请尝试以管理员运行本启动器，或临时关闭相关软件。");
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "鼠标映射：SendInput 自测异常");
            }
        }

        [DllImport("advapi32.dll", SetLastError = true)]
        private static extern bool GetTokenInformation(IntPtr TokenHandle, int TokenInformationClass,
            IntPtr TokenInformation, int TokenInformationLength, out int ReturnLength);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthority(IntPtr pSid, int nSubAuthority);

        [DllImport("advapi32.dll")]
        private static extern IntPtr GetSidSubAuthorityCount(IntPtr pSid);

        private const int TokenIntegrityLevel = 25;

        private static int GetCurrentIntegrityLevel()
        {
            try
            {
                IntPtr token = System.Security.Principal.WindowsIdentity.GetCurrent().Token;
                GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int size);
                if (size == 0) return 0;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    if (!GetTokenInformation(token, TokenIntegrityLevel, buf, size, out _))
                        return 0;
                    IntPtr pSid = Marshal.ReadIntPtr(buf);
                    IntPtr pCount = GetSidSubAuthorityCount(pSid);
                    byte count = Marshal.ReadByte(pCount);
                    IntPtr pRid = GetSidSubAuthority(pSid, count - 1);
                    return Marshal.ReadInt32(pRid);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch
            {
                return 0;
            }
        }

        private static string IntegrityToText(int rid) => rid switch
        {
            0x0000 => "Untrusted(0x0)",
            0x1000 => "Low(0x1000)",
            0x2000 => "Medium(0x2000)",
            0x2100 => "MediumPlus(0x2100)",
            0x3000 => "High/管理员(0x3000)",
            0x4000 => "System(0x4000)",
            _ => $"Unknown(0x{rid:X})"
        };

        private static void InstallMouseHook()
        {
            if (_mouseHook != IntPtr.Zero) return;
            _proc = HookCallback;
            _mouseHook = MouseInputNative.SetWindowsHookEx(MouseInputNative.WH_MOUSE_LL, _proc,
                IntPtr.Zero, 0);
            if (_mouseHook == IntPtr.Zero)
            {
                Log.Error("SetWindowsHookEx(WH_MOUSE_LL) 失败");
            }
            else
            {
                // 从 GetAsyncKeyState 初始化物理按键状态（安装钩子瞬间无注入活动，读数可靠）
                for (int i = 0; i < 5; i++)
                {
                    int vk = VkForPhysicalMouseSlot(i);
                    System.Threading.Volatile.Write(ref _physicalDown[i],
                        (MouseInputNative.GetAsyncKeyState(vk) & 0x8000) != 0 ? 1 : 0);
                }
                Log.Information("鼠标映射：低层钩子已安装（含物理按键状态追踪）");
            }
        }

        private static void UninstallMouseHook()
        {
            for (int i = 0; i < _repeatTimers.Length; i++)
            {
                _repeatTimers[i]?.Stop();
                _repeatTimers[i] = null;
            }

            ResetButtonFsm();

            // 重置物理按键状态追踪，避免残留状态影响下次启用
            for (int i = 0; i < 5; i++)
                System.Threading.Volatile.Write(ref _physicalDown[i], 0);

            if (_mouseHook == IntPtr.Zero) return;
            MouseInputNative.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            Log.Information("鼠标映射：鼠标低层钩子已卸载");
        }

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

        /// <summary>前台窗口是否属于本进程（本应用内录入快捷键时不拦截）</summary>
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
                if (fsm.MovedTooFar && rule.Trigger is not MouseTriggerKind.DoubleClick and not MouseTriggerKind.MultiClick)
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

        /// <summary>
        /// 卸载钩子或关闭功能时清空按键状态，避免残留“按下/吞键”状态影响系统鼠标。
        /// </summary>
        private static void ResetButtonFsm()
        {
            for (int i = 0; i < _btn.Length; i++)
            {
                _btn[i].Down = false;
                _btn[i].MovedTooFar = false;
                _btn[i].DownSuppressed = false;
                _btn[i].FireOnceKeyComboSentOnDown = false;
                _btn[i].UpSequence = 0;
            }
            _rawFsm.Clear();
            _hidLastSig.Clear();
        }

        /// <summary>
        /// 在 LL 鼠标钩子中更新 _physicalDown：忽略本程序带 MappingSyntheticExtraInfoPtr 的注入，
        /// 仍处理其它来源（含部分驱动标记为 LLMHF_INJECTED 的物理抬起），避免间隔=1 按住模式松手后仍认为按下。
        /// </summary>
        private static void UpdatePhysicalMouseStateFromLLHook(MouseInputNative.MSLLHOOKSTRUCT info, int msg)
        {
            if (msg == MouseInputNative.WM_MOUSEMOVE) return;
            int? trackBtn = MessageToButton(msg, info.mouseData);
            if (trackBtn == null) return;

            bool injected = (info.flags & MouseInputNative.LLMHF_INJECTED) != 0;
            if (injected && info.dwExtraInfo == InputSimulatorHelper.MappingSyntheticExtraInfoPtr)
                return;

            if (IsButtonDownMessage(msg))
                System.Threading.Volatile.Write(ref _physicalDown[trackBtn.Value], 1);
            else if (IsButtonUpMessage(msg))
                System.Threading.Volatile.Write(ref _physicalDown[trackBtn.Value], 0);
        }

        private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode < 0 || _config == null || !_config.GlobalEnabled)
                return MouseInputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            var info = Marshal.PtrToStructure<MouseInputNative.MSLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            UpdatePhysicalMouseStateFromLLHook(info, msg);
            // 仅跳过「本程序 SendInput」的合成事件；部分硬件/驱动会把物理键标为 INJECTED，若此处一律 CallNext，
            // 则不会进入 OnButtonDown，无法吞右键→组合键等，用户仍收到系统右键。
            bool injected = (info.flags & MouseInputNative.LLMHF_INJECTED) != 0;
            if (injected && info.dwExtraInfo == InputSimulatorHelper.MappingSyntheticExtraInfoPtr)
                return MouseInputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            if (msg == MouseInputNative.WM_MOUSEMOVE)
            {
                for (int bi = 0; bi < 5; bi++)
                {
                    if (!_btn[bi].Down) continue;
                    int dx = info.pt.X - _btn[bi].DownX;
                    int dy = info.pt.Y - _btn[bi].DownY;
                    if (dx * dx + dy * dy > _btn[bi].MoveToleranceSq)
                        _btn[bi].MovedTooFar = true;
                }
                foreach (var kv in _rawFsm)
                {
                    if (!kv.Value.Down) continue;
                    int dx = info.pt.X - kv.Value.DownX;
                    int dy = info.pt.Y - kv.Value.DownY;
                    if (dx * dx + dy * dy > kv.Value.MoveToleranceSq)
                        kv.Value.MovedTooFar = true;
                }
                return MouseInputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            int? btn = MessageToButton(msg, info.mouseData);
            if (btn == null)
                return MouseInputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            int b = btn.Value;

            // 须同时「前台为本程序」：否则游戏前台时 WinUI分层子窗口常使 WindowFromPoint 仍为本进程，会误跳过侧键→组合键（如 Magpie Shift+Z）。
            if (ShouldSkipMouseMappingBecauseCursorOnOwnForegroundUi(info.pt))
            {
                if (IsButtonDownMessage(msg))
                    StopRepeatTimer(b);
                else if (IsButtonUpMessage(msg))
                {
                    StopRepeatTimer(b);
                    _btn[b].Down = false;
                }
                return MouseInputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            if (IsButtonDownMessage(msg))
            {
                Log.Debug("鼠标映射: HookCallback WM_*DOWN btn={B} pt=({X},{Y})", b, info.pt.X, info.pt.Y);
                return OnButtonDown(b, info, nCode, wParam, lParam);
            }
            if (IsButtonUpMessage(msg))
            {
                Log.Debug("鼠标映射: HookCallback WM_*UP btn={B} pt=({X},{Y})", b, info.pt.X, info.pt.Y);
                return OnButtonUp(b, info, nCode, wParam, lParam);
            }

            return MouseInputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        /// <summary>
        /// 光标下窗口是否属于当前进程（本应用 UI 与对话框）
        /// </summary>
        private static bool IsMouseOverOwnProcessWindow(MouseInputNative.POINT pt)
        {
            var h = MouseInputNative.WindowFromPoint(pt);
            if (h == IntPtr.Zero) return false;
            MouseInputNative.GetWindowThreadProcessId(h, out uint pid);
            return pid == (uint)Environment.ProcessId;
        }

        /// <summary>
        /// 仅当本程序为前台且光标落在自有窗口上时不做鼠标映射。避免仅依赖 WindowFromPoint 在游戏画面上误判为本进程。
        /// </summary>
        private static bool ShouldSkipMouseMappingBecauseCursorOnOwnForegroundUi(MouseInputNative.POINT pt) =>
            IsForegroundOwnProcess() && IsMouseOverOwnProcessWindow(pt);

        /// <summary>
        /// 轮询连发是否应跳过：与低层钩子一致，仅前台+光标均在自有 UI 时跳过。
        /// </summary>
        private static bool ShouldSkipPollingMappingBecauseOverOwnUi(MouseInputNative.POINT pt) =>
            ShouldSkipMouseMappingBecauseCursorOnOwnForegroundUi(pt);

        /// <summary>按下后移动超过该像素（平方比较）则视为拖拽，不触发映射（与旧版默认 MoveTolerancePx=8 一致）</summary>
        private const int DefaultMoveTolerancePx = 8;

        /// <summary>与 <see cref="MouseInputNative.GetDoubleClickTime"/> 一致，避免 400ms 硬编码短于 Windows 默认（通常 500ms）导致双击/连击无法命中。</summary>
        private static int MouseDoubleClickGroupingIntervalMs()
        {
            uint ms = MouseInputNative.GetDoubleClickTime();
            if (ms == 0) ms = 500;
            return (int)Math.Clamp(ms, 200, 2000);
        }

        /// <summary>
        /// 用于区分「单击」与「长按」的按下时长阈值。对 <see cref="MouseTriggerKind.Click"/>，若用户将 HoldThreshold 设得过小（如 50ms），
        /// 正常人从按下到抬起普遍超过该值，会被判成长期按住（isHold=true），单击规则永不命中。
        /// </summary>
        private const int MinClickHoldThresholdMs = 200;

        private static int EffectiveClickVsHoldThresholdMs(MouseMappingRule rule)
        {
            int t = Math.Max(1, rule.HoldThresholdMs);
            if (rule.Trigger == MouseTriggerKind.Click)
                return Math.Max(MinClickHoldThresholdMs, t);
            return t;
        }

        private static IntPtr OnButtonDown(int b, MouseInputNative.MSLLHOOKSTRUCT info, int nCode, IntPtr wParam, IntPtr lParam)
        {
            _btn[b].Down = true;
            _btn[b].DownMs = Environment.TickCount64;
            _btn[b].DownX = info.pt.X;
            _btn[b].DownY = info.pt.Y;
            _btn[b].MovedTooFar = false;
            _btn[b].DownSuppressed = false;
            _btn[b].FireOnceKeyComboSentOnDown = false;

            int minTol = DefaultMoveTolerancePx;
            _btn[b].MoveToleranceSq = minTol * minTol;

            // 组合键/异槽鼠标：吞掉物理键 DOWN；FireOnce+单击+组合键在按下瞬间触发一次，抬起仅补鼠标 UP。同槽左→左仍透传物理键。
            var suppressRule = FirstRuleForMouseDownThatSuppresses(b, info.pt);
            if (suppressRule != null
                && MayInterceptOriginalInput(suppressRule, info.pt))
            {
                _btn[b].DownSuppressed = true;
                if (suppressRule.Behavior == MouseBehaviorMode.FireOnce
                    && suppressRule.Trigger == MouseTriggerKind.Click
                    && suppressRule.Action == MouseActionKind.KeyCombo)
                {
                    _btn[b].FireOnceKeyComboSentOnDown = true;
                    var ruleForBg = suppressRule;
                    System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            ExecuteAction(ruleForBg);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "鼠标映射：FireOnce+单击+组合键 按下触发 ExecuteAction 异常");
                        }
                    });
                }

                return (IntPtr)1;
            }

            // RepeatWhileHeld 连发由轮询线程处理；未吞键时透传
            return MouseInputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
        }

        /// <summary>
        /// RepeatWhileHeld 模式下的单次触发：根据动作类型发送鼠标/按键。
        /// 关键：必须在后台线程执行 SendInput。LL 钩子装在 UI 线程，若直接在 UI 线程同步
        /// 调用 SendInput，Windows 要把注入派发给"同一个 UI 线程"上的钩子回调，但 UI 线程
        /// 正卡在 SendInput 里，互相等待 300ms 后注入被丢弃（sent=0, LastError=0）。
        /// 放到线程池执行后，UI 线程能自由响应钩子回调，注入就能正常完成。
        /// </summary>
        private static void FireRepeatAction(int b, MouseMappingRule rule)
        {
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (rule.Action == MouseActionKind.KeyCombo)
                        InputSimulatorHelper.SendKeyCombo(rule.KeyComboText);
                    else
                        InputSimulatorHelper.SendMouseButtonClick(rule.SimulatedMouseButton);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "鼠标映射：FireRepeatAction 后台注入异常");
                }
            });
        }

        private static IntPtr OnButtonUp(int b, MouseInputNative.MSLLHOOKSTRUCT info, int nCode, IntPtr wParam, IntPtr lParam)
        {
            StopRepeatTimer(b);
            if (!_btn[b].Down)
                return MouseInputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);

            bool keyComboSentOnDown = _btn[b].FireOnceKeyComboSentOnDown;
            _btn[b].FireOnceKeyComboSentOnDown = false;

            long duration = Environment.TickCount64 - _btn[b].DownMs;
            _btn[b].Down = false;

            var rules = MatchingRules(b);
            UpdateClickSequence(b, MouseDoubleClickGroupingIntervalMs());

            MouseMappingRule? matched = null;
            foreach (var rule in rules)
            {
                // 物理 DOWN 已被「需吞键」规则吞掉时（RepeatWhileHeld 或 FireOnce+单击+组合键/异槽鼠标）：
                // - 短按抬起仍须允许「单击 + FireOnce」匹配，由 ExecuteAction 合成完整点击（否则与长按规则并存时短按永远得不到模拟右键）；
                // - 长按抬起仍只匹配 RepeatWhileHeld+需吞键 的规则（由下方 triggerOk 区分短/长）。
                if (_btn[b].DownSuppressed)
                {
                    bool allowShortClickAfterSuppress =
                        rule.Behavior == MouseBehaviorMode.FireOnce
                        && rule.Trigger == MouseTriggerKind.Click;

                    bool allowRepeatWhileHeldAfterSuppress =
                        rule.Behavior == MouseBehaviorMode.RepeatWhileHeld
                        && ShouldSuppressPhysicalMouseOnDown(rule, b);

                    if (!allowShortClickAfterSuppress && !allowRepeatWhileHeldAfterSuppress)
                        continue;
                }

                if (!ContextOk(rule, info.pt)) continue;
                if (ShouldSkipForGlobalSpatialContext(info.pt)) continue;
                if (_btn[b].MovedTooFar && rule.Trigger is not MouseTriggerKind.DoubleClick and not MouseTriggerKind.MultiClick)
                    continue;

                bool isHold = duration >= EffectiveClickVsHoldThresholdMs(rule);
                bool triggerOk = rule.Trigger switch
                {
                    MouseTriggerKind.Click => !isHold,
                    MouseTriggerKind.Hold => isHold,
                    MouseTriggerKind.DoubleClick => _btn[b].UpSequence >= 2,
                    MouseTriggerKind.MultiClick => _btn[b].UpSequence >= 2,
                    _ => false
                };

                if (!triggerOk) continue;

                matched = rule;
                break;
            }

            if (matched == null)
            {
                // 按下曾被吞掉但没有任何规则处理抬起：吞掉抬起，避免孤儿 UP
                if (_btn[b].DownSuppressed)
                {
                    _btn[b].DownSuppressed = false;
                    return (IntPtr)1;
                }
                return MouseInputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            bool downWasSuppressed = _btn[b].DownSuppressed;
            if (matched.Trigger is MouseTriggerKind.DoubleClick or MouseTriggerKind.MultiClick)
                _btn[b].UpSequence = 0;

            

            if (!MayInterceptOriginalInput(matched, info.pt))
            {
                // 若按下已被吞掉，不能再放行抬起（否则孤儿 WM_*UP）；未吞键时才透传
                if (downWasSuppressed)
                {
                    _btn[b].DownSuppressed = false;
                    return (IntPtr)1;
                }
                return MouseInputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            // 按住连发由轮询线程在按住期间处理；若在抬起时再 ExecuteAction 会多触发一次组合键/点击，并干扰合成状态。
            if (matched.Behavior == MouseBehaviorMode.RepeatWhileHeld)
            {
                if (downWasSuppressed)
                {
                    _btn[b].DownSuppressed = false;
                    return (IntPtr)1;
                }
                return MouseInputNative.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }

            _btn[b].DownSuppressed = false;

            var matchedForBg = matched;
            bool skipKeyComboOnUp = keyComboSentOnDown
                && matchedForBg.Action == MouseActionKind.KeyCombo
                && matchedForBg.Behavior == MouseBehaviorMode.FireOnce
                && matchedForBg.Trigger == MouseTriggerKind.Click;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                if (skipKeyComboOnUp)
                {
                    // 按下已注入组合键：须等 SendKeyCombo 释放锁后再补鼠标 UP，否则与 Shift/C 注入交错易残留 Shift。
                    lock (InputSimulatorHelper.ComboInjectionSyncLock)
                    {
                        try
                        {
                            InputSimulatorHelper.SendMouseButtonUpOnly(b);
                        }
                        catch (Exception ex)
                        {
                            Log.Warning(ex, "释放物理键状态失败（忽略）");
                        }
                    }

                    return;
                }

                try
                {
                    InputSimulatorHelper.SendMouseButtonUpOnly(b);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "释放物理键状态失败（忽略）");
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

        private static void UpdateClickSequence(int b, int maxIntervalMs)
        {
            long now = Environment.TickCount64;
            if (now - _btn[b].LastUpMs <= maxIntervalMs)
                _btn[b].UpSequence++;
            else
                _btn[b].UpSequence = 1;
            _btn[b].LastUpMs = now;
        }

        private static void ExecuteAction(MouseMappingRule rule)
        {
            if (rule.Action == MouseActionKind.KeyCombo)
                InputSimulatorHelper.SendKeyCombo(rule.KeyComboText);
            else
                InputSimulatorHelper.SendMouseButtonClickFireOnce(rule.SimulatedMouseButton);
        }

        /// <summary>
        /// 启动按住连发：Click 触发立刻开火并按 RepeatInterval 重复；
        /// Hold 触发先等待 HoldThreshold 毫秒再开火并重复。按键抬起时停止。
        /// </summary>
        private static void StartRepeatTimer(int b, MouseMappingRule rule)
        {
            StopRepeatTimer(b);
            if (_dispatcher == null)
            {
                Log.Warning("DispatcherQueue 未设置，无法连发");
                return;
            }

            int interval = Math.Max(1, rule.RepeatIntervalMs);
            int initialDelay = rule.Trigger == MouseTriggerKind.Hold
                ? Math.Max(1, rule.HoldThresholdMs)
                : interval;

            // Click 触发：在按下瞬间先开一枪
            if (rule.Trigger != MouseTriggerKind.Hold)
                FireRepeatAction(b, rule);

            var t = _dispatcher.CreateTimer();
            t.Interval = TimeSpan.FromMilliseconds(initialDelay);
            bool firstFired = rule.Trigger != MouseTriggerKind.Hold;
            int tickCount = 0;
            t.Tick += (_, _) =>
            {
                try
                {
                    if (!_btn[b].Down)
                    {
                        t.Stop();
                        Log.Debug("鼠标映射: 连发 Tick 停止 btn={B} 总次数={C}", b, tickCount);
                        return;
                    }
                    FireRepeatAction(b, rule);
                    tickCount++;
                    Log.Debug("鼠标映射: 连发 Tick 发射 btn={B} #={N} action={A}", b, tickCount, rule.Action);
                    if (!firstFired)
                    {
                        firstFired = true;
                        t.Interval = TimeSpan.FromMilliseconds(interval);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "鼠标映射：连发 Tick 异常，已停止连发以避免崩溃");
                    try { t.Stop(); } catch { }
                }
            };
            t.Start();
            _repeatTimers[b] = t;
            Log.Information("鼠标映射：按住连发启动 Button={B} Trigger={T} Action={A} InitialDelay={D}ms Interval={I}ms",
                rule.Button, rule.Trigger, rule.Action, initialDelay, interval);
        }

        private static void StopRepeatTimer(int b)
        {
            _repeatTimers[b]?.Stop();
            _repeatTimers[b] = null;
        }

        private static List<MouseMappingRule> MatchingRules(int b)
        {
            if (_config?.Rules == null) return new List<MouseMappingRule>();
            var btn = (MousePhysicalButton)b;
            return _config.Rules
                .Where(r => r.Enabled && r.PhysicalKeyVirtualKey == 0
                    && r.PhysicalMouseRawButtonFlag == 0 && r.PhysicalMouseRawButtonsMask == 0
                    && r.PhysicalMouseHidSignature == 0
                    && r.Button == btn)
                .OrderByDescending(r => r.Priority)
                .ToList();
        }

        private static List<MouseMappingRule> MatchingRawFlagRules(ushort downFlag)
        {
            if (_config?.Rules == null) return new List<MouseMappingRule>();
            return _config.Rules
                .Where(r => r.Enabled && r.PhysicalKeyVirtualKey == 0
                    && r.PhysicalMouseHidSignature == 0
                    && r.PhysicalMouseRawButtonFlag == downFlag && r.PhysicalMouseRawButtonsMask == 0)
                .OrderByDescending(r => r.Priority)
                .ToList();
        }

        private static List<MouseMappingRule> MatchingRawMaskRules(uint fallingMask)
        {
            if (_config?.Rules == null) return new List<MouseMappingRule>();
            return _config.Rules
                .Where(r => r.Enabled && r.PhysicalKeyVirtualKey == 0
                    && r.PhysicalMouseHidSignature == 0
                    && r.PhysicalMouseRawButtonFlag == 0 && r.PhysicalMouseRawButtonsMask != 0
                    && (fallingMask & r.PhysicalMouseRawButtonsMask) != 0)
                .OrderByDescending(r => r.Priority)
                .ToList();
        }

        private static ushort DownFromUpRi(ushort upFlag)
        {
            if ((upFlag & 0x0002) != 0) return 0x0001;
            if ((upFlag & 0x0008) != 0) return 0x0004;
            if ((upFlag & 0x0020) != 0) return 0x0010;
            if ((upFlag & 0x0080) != 0) return 0x0040;
            if ((upFlag & 0x0200) != 0) return 0x0100;
            if ((upFlag & 0x0800) != 0) return 0x0400;
            if ((upFlag & 0x2000) != 0) return 0x1000;
            if (upFlag != 0 && (upFlag & (ushort)(upFlag - 1)) == 0)
                return (ushort)(upFlag >> 1);
            return 0;
        }

        private static int? SlotFromRiDownFlag(ushort downFlag)
        {
            if (downFlag == RawInputMouseBridge.RI_MOUSE_LEFT_BUTTON_DOWN) return 0;
            if (downFlag == RawInputMouseBridge.RI_MOUSE_RIGHT_BUTTON_DOWN) return 1;
            if (downFlag == RawInputMouseBridge.RI_MOUSE_MIDDLE_BUTTON_DOWN) return 2;
            if (downFlag == RawInputMouseBridge.RI_MOUSE_BUTTON_4_DOWN) return 3;
            if (downFlag == RawInputMouseBridge.RI_MOUSE_BUTTON_5_DOWN) return 4;
            return null;
        }

        internal static void NotifyRawFlagDown(ushort downFlag)
        {
            if (_config == null || !_config.GlobalEnabled || downFlag == 0) return;
            // Raw Input 与 LL 钩子并行：部分设备/前台下物理键沿只稳定出现在 WM_INPUT，需同步到轮询用的 _physicalDown
            int? physSlot = SlotFromRiDownFlag(downFlag);
            if (physSlot != null)
                System.Threading.Volatile.Write(ref _physicalDown[physSlot.Value], 1);

            if (!MouseInputNative.GetCursorPos(out var pt)) return;
            if (ShouldSkipMouseMappingBecauseCursorOnOwnForegroundUi(pt)) return;

            ulong key = downFlag;
            if (!_rawFsm.TryGetValue(key, out var fsm))
            {
                fsm = new ButtonFsm();
                _rawFsm[key] = fsm;
            }
            fsm.Down = true;
            fsm.DownMs = Environment.TickCount64;
            fsm.DownX = pt.X;
            fsm.DownY = pt.Y;
            fsm.MovedTooFar = false;
            fsm.DownSuppressed = false;
            int minTol = DefaultMoveTolerancePx;
            fsm.MoveToleranceSq = minTol * minTol;
        }

        internal static void NotifyRawFlagUp(ushort upFlag)
        {
            if (_config == null || !_config.GlobalEnabled) return;
            ushort downFlag = DownFromUpRi(upFlag);
            if (downFlag != 0)
            {
                int? physSlot = SlotFromRiDownFlag(downFlag);
                if (physSlot != null)
                    ClearPhysicalDownFromRawIfHardwareReleased(physSlot.Value);
            }

            if (downFlag == 0) return;
            if (!MouseInputNative.GetCursorPos(out var pt)) return;
            if (ShouldSkipMouseMappingBecauseCursorOnOwnForegroundUi(pt)) return;

            ulong key = downFlag;
            if (!_rawFsm.TryGetValue(key, out var fsm) || !fsm.Down)
                return;

            long duration = Environment.TickCount64 - fsm.DownMs;
            fsm.Down = false;

            var rules = MatchingRawFlagRules(downFlag);
            UpdateClickSequenceRaw(key, MouseDoubleClickGroupingIntervalMs());

            MouseMappingRule? matched = null;
            foreach (var rule in rules)
            {
                if (fsm.DownSuppressed && rule.Action == MouseActionKind.MouseButton)
                    continue;
                if (!ContextOk(rule, pt)) continue;
                if (ShouldSkipForGlobalSpatialContext(pt)) continue;
                if (fsm.MovedTooFar && rule.Trigger is not MouseTriggerKind.DoubleClick and not MouseTriggerKind.MultiClick)
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
                    return;
                }
                return;
            }

            bool downWasSuppressed = fsm.DownSuppressed;
            if (matched.Trigger is MouseTriggerKind.DoubleClick or MouseTriggerKind.MultiClick)
                fsm.UpSequence = 0;

            if (!MayInterceptOriginalInput(matched, pt))
            {
                if (downWasSuppressed)
                {
                    fsm.DownSuppressed = false;
                    return;
                }
                return;
            }

            fsm.DownSuppressed = false;

            var matchedForBg = matched;
            int? slot = SlotFromRiDownFlag(downFlag);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (slot != null)
                        InputSimulatorHelper.SendMouseButtonUpOnly(slot.Value);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Raw 物理键释放失败（忽略）");
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
        }

        internal static void NotifyRawMaskDown(uint risingMask)
        {
            if (risingMask == 0 || _config == null || !_config.GlobalEnabled) return;
            for (uint m = risingMask; m != 0; )
            {
                uint low = m & (uint)-(int)m;
                RawMaskDownSingle(low);
                m &= ~low;
            }
        }

        private static void RawMaskDownSingle(uint singleBitMask)
        {
            if (!MouseInputNative.GetCursorPos(out var pt)) return;
            if (ShouldSkipMouseMappingBecauseCursorOnOwnForegroundUi(pt)) return;

            ulong key = (ulong)singleBitMask << 32;
            if (!_rawFsm.TryGetValue(key, out var fsm))
            {
                fsm = new ButtonFsm();
                _rawFsm[key] = fsm;
            }
            fsm.Down = true;
            fsm.DownMs = Environment.TickCount64;
            fsm.DownX = pt.X;
            fsm.DownY = pt.Y;
            fsm.MovedTooFar = false;
            fsm.DownSuppressed = false;
            int minTol = DefaultMoveTolerancePx;
            fsm.MoveToleranceSq = minTol * minTol;
        }

        internal static void NotifyRawMaskUp(uint fallingMask)
        {
            if (fallingMask == 0 || _config == null || !_config.GlobalEnabled) return;
            for (uint m = fallingMask; m != 0; )
            {
                uint low = m & (uint)-(int)m;
                RawMaskUpSingle(low);
                m &= ~low;
            }
        }

        private static void RawMaskUpSingle(uint fallingBit)
        {
            if (!MouseInputNative.GetCursorPos(out var pt)) return;
            if (ShouldSkipMouseMappingBecauseCursorOnOwnForegroundUi(pt)) return;

            ulong key = (ulong)fallingBit << 32;
            if (!_rawFsm.TryGetValue(key, out var fsm) || !fsm.Down)
                return;

            long duration = Environment.TickCount64 - fsm.DownMs;
            fsm.Down = false;

            var rules = MatchingRawMaskRules(fallingBit);
            UpdateClickSequenceRaw(key, MouseDoubleClickGroupingIntervalMs());

            MouseMappingRule? matched = null;
            foreach (var rule in rules)
            {
                if (fsm.DownSuppressed && rule.Action == MouseActionKind.MouseButton)
                    continue;
                if (!ContextOk(rule, pt)) continue;
                if (ShouldSkipForGlobalSpatialContext(pt)) continue;
                if (fsm.MovedTooFar && rule.Trigger is not MouseTriggerKind.DoubleClick and not MouseTriggerKind.MultiClick)
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
                    return;
                }
                return;
            }

            bool downWasSuppressed = fsm.DownSuppressed;
            if (matched.Trigger is MouseTriggerKind.DoubleClick or MouseTriggerKind.MultiClick)
                fsm.UpSequence = 0;

            if (!MayInterceptOriginalInput(matched, pt))
            {
                if (downWasSuppressed)
                {
                    fsm.DownSuppressed = false;
                    return;
                }
                return;
            }

            fsm.DownSuppressed = false;

            var matchedForBg = matched;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ExecuteAction(matchedForBg);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "ExecuteAction 异常（已吞掉）");
                }
            });
        }

        private static void UpdateClickSequenceRaw(ulong key, int maxIntervalMs)
        {
            if (!_rawFsm.TryGetValue(key, out var fsm)) return;
            long now = Environment.TickCount64;
            if (now - fsm.LastUpMs <= maxIntervalMs)
                fsm.UpSequence++;
            else
                fsm.UpSequence = 1;
            fsm.LastUpMs = now;
        }

        private static List<MouseMappingRule> MatchingKeyboardRules(int vk)
        {
            if (_config?.Rules == null) return new List<MouseMappingRule>();
            return _config.Rules
                .Where(r => r.Enabled && r.PhysicalKeyVirtualKey == vk
                    && r.PhysicalMouseRawButtonFlag == 0 && r.PhysicalMouseRawButtonsMask == 0
                    && r.PhysicalMouseHidSignature == 0)
                .OrderByDescending(r => r.Priority)
                .ToList();
        }

        private static List<MouseMappingRule> MatchingHidRules(ulong signature)
        {
            if (_config?.Rules == null) return new List<MouseMappingRule>();
            return _config.Rules
                .Where(r => r.Enabled && r.PhysicalKeyVirtualKey == 0
                    && r.PhysicalMouseRawButtonFlag == 0 && r.PhysicalMouseRawButtonsMask == 0
                    && r.PhysicalMouseHidSignature == signature)
                .OrderByDescending(r => r.Priority)
                .ToList();
        }

        /// <summary>物理键录入前清空 HID 上一帧状态，与 RawInputMouseBridge 基线一致。</summary>
        internal static void ResetHidBaselinesForRecording()
        {
            _hidLastSig.Clear();
        }

        /// <summary>RIM_TYPEHID：报告哈希变化时调用；在「上一帧不是该规则签名 → 当前帧等于规则签名」时触发一次。</summary>
        internal static void NotifyHidRawReport(ulong sig, IntPtr hDevice, byte reportId)
        {
            if (_config == null || !_config.GlobalEnabled || sig == 0) return;
            if (!MouseInputNative.GetCursorPos(out var pt)) return;
            if (ShouldSkipMouseMappingBecauseCursorOnOwnForegroundUi(pt)) return;

            var hidKey = (hDevice, reportId);
            if (!_hidLastSig.TryGetValue(hidKey, out ulong prev))
            {
                _hidLastSig[hidKey] = sig;
                return;
            }
            if (prev == sig) return;
            _hidLastSig[hidKey] = sig;

            var rules = MatchingHidRules(sig);
            MouseMappingRule? matched = null;
            foreach (var rule in rules)
            {
                if (prev == rule.PhysicalMouseHidSignature) continue;
                if (sig != rule.PhysicalMouseHidSignature) continue;
                if (!ContextOk(rule, pt)) continue;
                if (ShouldSkipForGlobalSpatialContext(pt)) continue;
                matched = rule;
                break;
            }

            if (matched == null) return;
            if (!MayInterceptOriginalInput(matched, pt)) return;

            var forBg = matched;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    ExecuteAction(forBg);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "HID 物理键映射 ExecuteAction 异常（已吞掉）");
                }
            });
        }

        /// <summary>
        /// 配置级全局名单：先于每条规则评估；关闭或名单为空时不限制。
        /// </summary>
        private static bool GlobalProcessContextOk(MouseInputNative.POINT pt)
        {
            var cfg = _config;
            if (cfg == null || !cfg.GlobalRestrictToProcessList || cfg.GlobalProcessFilter == null || cfg.GlobalProcessFilter.Count == 0)
                return true;

            string? exe = GetExePathForWindowAtPoint(pt);
            if (string.IsNullOrEmpty(exe)) return false;

            string name = Path.GetFileName(exe);
            bool inList = cfg.GlobalProcessFilter.Any(entry =>
                !string.IsNullOrWhiteSpace(entry) &&
                (entry.Equals(exe, StringComparison.OrdinalIgnoreCase) ||
                 entry.Equals(name, StringComparison.OrdinalIgnoreCase)));

            return cfg.GlobalContextMode == MouseContextWhitelistMode.IncludeOnly ? inList : !inList;
        }

        /// <summary>单条规则的进程上下文（与全局无关，便于 MayIntercept 拆开组合）。</summary>
        private static bool RuleProcessContextOk(MouseMappingRule rule, MouseInputNative.POINT pt)
        {
            if (!rule.RestrictToProcessList || rule.ProcessFilter == null || rule.ProcessFilter.Count == 0)
                return true;

            // 必须用光标下窗口所属进程，不能用前台窗口：多窗口/嵌入/焦点滞后时前台与点击目标不一致会导致规则永远不命中。
            string? exe = GetExePathForWindowAtPoint(pt);
            if (string.IsNullOrEmpty(exe)) return false;

            string name = Path.GetFileName(exe);
            bool inList = rule.ProcessFilter.Any(entry =>
                !string.IsNullOrWhiteSpace(entry) &&
                (entry.Equals(exe, StringComparison.OrdinalIgnoreCase) ||
                 entry.Equals(name, StringComparison.OrdinalIgnoreCase)));

            return rule.ContextMode == MouseContextWhitelistMode.IncludeOnly ? inList : !inList;
        }

        /// <summary>
        /// 进程上下文：默认同 <see cref="GlobalProcessContextOk"/> 与 <see cref="RuleProcessContextOk"/> 同时成立。
        /// 例外：全局为黑名单且当前进程被全局拦下时，若本条规则单独使用白名单且该进程落在规则名单内，则仅此规则仍放行（覆盖全局黑名单对该进程的限制）；其它规则仍受全局黑名单约束。
        /// </summary>
        private static bool ContextOk(MouseMappingRule rule, MouseInputNative.POINT pt)
        {
            bool globalOk = GlobalProcessContextOk(pt);
            bool ruleOk = RuleProcessContextOk(rule, pt);

            if (globalOk && ruleOk)
                return true;

            var cfg = _config;
            if (!globalOk &&
                cfg != null &&
                cfg.GlobalRestrictToProcessList &&
                cfg.GlobalContextMode == MouseContextWhitelistMode.Exclude &&
                cfg.GlobalProcessFilter is { Count: > 0 } &&
                rule.RestrictToProcessList &&
                rule.ContextMode == MouseContextWhitelistMode.IncludeOnly &&
                rule.ProcessFilter is { Count: > 0 } &&
                RuleProcessContextOk(rule, pt))
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// 是否允许拦截（吞掉）原始鼠标消息。
        /// 未启用进程过滤时：在非本进程窗口上全局映射（本进程窗口上 HookCallback 已一律透传，不会卡死本程序）。
        /// 启用进程过滤时：仅当光标下窗口所属进程命中白/黑名单时才拦截，避免对无关进程误吞键。
        /// </summary>
        private static bool MayInterceptOriginalInput(MouseMappingRule rule, MouseInputNative.POINT pt) =>
            ContextOk(rule, pt);

        /// <summary>
        /// 按下时是否吞掉物理 WM_*DOWN：输出为组合键或异槽鼠标时吞键，避免前台收到原物理键；
        /// RepeatWhileHeld 由轮询注入；FireOnce+单击+组合键 在按下时 ExecuteAction，其它 FireOnce+单击 仍在抬起时 ExecuteAction。
        /// </summary>
        private static bool ShouldSuppressPhysicalMouseOnDown(MouseMappingRule rule, int physicalSlotB)
        {
            if (rule.Trigger != MouseTriggerKind.Click && rule.Trigger != MouseTriggerKind.Hold)
                return false;

            if (rule.Behavior == MouseBehaviorMode.RepeatWhileHeld)
            {
                if (rule.Action == MouseActionKind.KeyCombo) return true;
                return rule.Action == MouseActionKind.MouseButton && rule.SimulatedMouseButton != physicalSlotB;
            }

            if (rule.Behavior == MouseBehaviorMode.FireOnce && rule.Trigger == MouseTriggerKind.Click)
            {
                if (rule.Action == MouseActionKind.KeyCombo) return true;
                return rule.Action == MouseActionKind.MouseButton && rule.SimulatedMouseButton != physicalSlotB;
            }

            return false;
        }

        /// <summary>与 <see cref="MatchingRules"/> 顺序一致的首条「按下需吞键」规则（用于 OnButtonDown）。</summary>
        private static MouseMappingRule? FirstRuleForMouseDownThatSuppresses(int b, MouseInputNative.POINT pt)
        {
            foreach (var rule in MatchingRules(b))
            {
                if (!ShouldSuppressPhysicalMouseOnDown(rule, b)) continue;
                if (!ContextOk(rule, pt)) continue;
                if (ShouldSkipForGlobalSpatialContext(pt)) continue;
                return rule;
            }

            return null;
        }

        /// <summary>光标下命中窗口所属进程的 exe 路径（进程白名单/黑名单匹配用）。</summary>
        private static string? GetExePathForWindowAtPoint(MouseInputNative.POINT pt)
        {
            try
            {
                var h = MouseInputNative.WindowFromPoint(pt);
                if (h == IntPtr.Zero) return null;
                MouseInputNative.GetWindowThreadProcessId(h, out uint pid);
                using var p = Process.GetProcessById((int)pid);
                return p.MainModule?.FileName;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 页顶全局开关：在任务栏或屏幕边缘时不命中任何规则（与旧版「每条规则单独勾选」等价，现统一为全局）。
        /// </summary>
        private static bool ShouldSkipForGlobalSpatialContext(MouseInputNative.POINT pt)
        {
            if (_config == null) return false;
            if (_config.GlobalDisableOnTaskbar && IsCursorOnTaskbar(pt)) return true;
            if (_config.GlobalDisableOnScreenEdges && IsOnScreenEdge(pt)) return true;
            return false;
        }

        private static bool IsCursorOnTaskbar(MouseInputNative.POINT pt)
        {
            var h = MouseInputNative.WindowFromPoint(pt);
            if (h == IntPtr.Zero) return false;
            // 托盘、开始按钮、任务栏按钮等是 Shell_TrayWnd 的子窗口，仅比对命中窗口类名会漏判。
            if (IsShellTaskbarWindowClass(h))
                return true;
            var root = MouseInputNative.GetAncestor(h, MouseInputNative.GA_ROOT);
            if (root != IntPtr.Zero && IsShellTaskbarWindowClass(root))
                return true;
            IntPtr w = h;
            for (int i = 0; i < 32; i++)
            {
                w = MouseInputNative.GetParent(w);
                if (w == IntPtr.Zero) break;
                if (IsShellTaskbarWindowClass(w))
                    return true;
            }

            return false;
        }

        private static bool IsShellTaskbarWindowClass(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return false;
            var buf = new char[256];
            int n = MouseInputNative.GetClassName(hwnd, buf, buf.Length);
            if (n <= 0) return false;
            string cls = new string(buf, 0, n);
            return cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd";
        }

        private static bool IsOnScreenEdge(MouseInputNative.POINT pt)
        {
            int vx = MouseInputNative.GetSystemMetrics(MouseInputNative.SM_XVIRTUALSCREEN);
            int vy = MouseInputNative.GetSystemMetrics(MouseInputNative.SM_YVIRTUALSCREEN);
            int vw = MouseInputNative.GetSystemMetrics(MouseInputNative.SM_CXVIRTUALSCREEN);
            int vh = MouseInputNative.GetSystemMetrics(MouseInputNative.SM_CYVIRTUALSCREEN);
            const int edge = 6;
            return pt.X <= vx + edge || pt.Y <= vy + edge || pt.X >= vx + vw - edge || pt.Y >= vy + vh - edge;
        }

        /// <summary>mouseData 高字为 XBUTTON1(1)/XBUTTON2(2)，与 Win32 WM_XBUTTON* 一致。</summary>
        private static int? MessageToButton(int msg, uint mouseData)
        {
            if (msg == MouseInputNative.WM_LBUTTONDOWN || msg == MouseInputNative.WM_LBUTTONUP) return 0;
            if (msg == MouseInputNative.WM_RBUTTONDOWN || msg == MouseInputNative.WM_RBUTTONUP) return 1;
            if (msg == MouseInputNative.WM_MBUTTONDOWN || msg == MouseInputNative.WM_MBUTTONUP) return 2;
            if (msg == MouseInputNative.WM_XBUTTONDOWN || msg == MouseInputNative.WM_XBUTTONUP)
            {
                uint x = (mouseData >> 16) & 0xFFFF;
                if (x == MouseInputNative.XBUTTON1) return 3;
                if (x == MouseInputNative.XBUTTON2) return 4;
            }

            return null;
        }

        private static bool IsButtonDownMessage(int msg) =>
            msg is MouseInputNative.WM_LBUTTONDOWN or MouseInputNative.WM_RBUTTONDOWN or MouseInputNative.WM_MBUTTONDOWN
                or MouseInputNative.WM_XBUTTONDOWN;

        private static bool IsButtonUpMessage(int msg) =>
            msg is MouseInputNative.WM_LBUTTONUP or MouseInputNative.WM_RBUTTONUP or MouseInputNative.WM_MBUTTONUP
                or MouseInputNative.WM_XBUTTONUP;

        private sealed class ButtonFsm
        {
            public bool Down;
            public long DownMs;
            public int DownX, DownY;
            public bool MovedTooFar;
            /// <summary>WM_*DOWN 已被本钩子吞掉（RepeatMacro 按下路径），抬起必须与之一致，否则会产生孤儿 UP</summary>
            public bool DownSuppressed;
            /// <summary>FireOnce+单击+组合键已在 WM_*DOWN 触发，抬起时不再 SendKeyCombo，避免重复注入与 Shift 残留。</summary>
            public bool FireOnceKeyComboSentOnDown;
            public int MoveToleranceSq = 64;
            public long LastUpMs;
            public int UpSequence;
        }
    }
}
