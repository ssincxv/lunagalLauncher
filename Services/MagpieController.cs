using System;
using System.Runtime.InteropServices;
using Serilog;

namespace lunagalLauncher.Services
{
    /// <summary>
    /// 通过向 Magpie 的 message-only 热键窗口 PostMessage 触发缩放切换。
    ///
    /// 原理（来自 Magpie 开源实现，main 分支 <c>ShortcutService.cpp</c>）：
    /// - Magpie 用 <c>CreateWindow(HWND_MESSAGE, "Magpie_Hotkey", ...)</c> 创建一个 message-only 窗口，
    ///   在 WndProc 中处理 <c>WM_HOTKEY</c>，<c>wParam</c> 即为 <c>ShortcutAction</c> 枚举值：
    ///   0 = Scale（全屏缩放切换）、1 = WindowedModeScale、2 = Toolbar。
    /// - 我们直接 <see cref="PostMessageW"/>(hwnd, WM_HOTKEY, 0, 0) 即可复用 Magpie 的 _FireShortcut(Scale)，
    ///   效果和按下用户配置的热键完全一致，但不经过任何 SendInput，前台 galgame 不会看到 Shift 等修饰键。
    ///
    /// 注意：message-only 窗口不会被 <c>FindWindow</c> 枚举，必须用
    /// <see cref="FindWindowExW"/>(HWND_MESSAGE, NULL, className, NULL) 才能定位。
    /// </summary>
    internal static class MagpieController
    {
        private const string HotkeyWindowClassName = "Magpie_Hotkey";
        private const string MainWindowClassName = "Magpie_Main";
        private const uint WM_HOTKEY = 0x0312;

        /// <summary>Magpie 的 <c>ShortcutAction::Scale</c>（来自 Magpie 源码枚举顺序）。</summary>
        private const int ShortcutActionScale = 0;

        private static readonly IntPtr HWND_MESSAGE = new(-3);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// 定位 Magpie 的热键窗口。找不到时返回 <see cref="IntPtr.Zero"/>，调用方需判断是否运行。
        /// 优先 message-only 热键窗口；若 Magpie 未启用缩放功能（理论上不会）则回退到主窗口。
        /// </summary>
        internal static IntPtr FindHotkeyWindow()
        {
            IntPtr h = FindWindowExW(HWND_MESSAGE, IntPtr.Zero, HotkeyWindowClassName, null);
            if (h != IntPtr.Zero) return h;

            // 极少数情况下 message-only 搜索失败，再退一层普通 FindWindow（不会找到 message-only 窗口）。
            return FindWindowW(HotkeyWindowClassName, null);
        }

        /// <summary>Magpie 是否在运行（仅通过窗口存在性判断，不启动进程）。</summary>
        internal static bool IsMagpieRunning()
        {
            if (FindHotkeyWindow() != IntPtr.Zero) return true;
            // Magpie 主窗口可能存在但热键窗口出问题；尽量给 UI 一个「在跑」的信号。
            return FindWindowW(MainWindowClassName, null) != IntPtr.Zero;
        }

        /// <summary>
        /// 触发 Magpie 的「切换全屏缩放」动作。成功返回 true；Magpie 未运行或投递失败返回 false。
        /// 本方法不抛异常，失败仅记日志。
        /// </summary>
        internal static bool ToggleScaling()
        {
            try
            {
                IntPtr h = FindHotkeyWindow();
                if (h == IntPtr.Zero)
                {
                    Log.Information("MagpieController：未找到 Magpie_Hotkey 窗口，Magpie 可能未运行");
                    return false;
                }

                if (!PostMessageW(h, WM_HOTKEY, (IntPtr)ShortcutActionScale, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    Log.Warning("MagpieController：PostMessage WM_HOTKEY 失败 hwnd=0x{H:X} err={E}", h.ToInt64(), err);
                    return false;
                }

                Log.Debug("MagpieController：Scale 已投递给 Magpie hwnd=0x{H:X}", h.ToInt64());
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "MagpieController：ToggleScaling 异常（已吞掉）");
                return false;
            }
        }
    }
}
