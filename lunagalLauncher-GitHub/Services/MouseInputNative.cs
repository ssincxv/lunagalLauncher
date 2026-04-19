using System;
using System.Runtime.InteropServices;

namespace lunagalLauncher.Services
{
    internal static class MouseInputNative
    {
        internal const int WH_MOUSE_LL = 14;

        internal const int WM_LBUTTONDOWN = 0x0201;
        internal const int WM_LBUTTONUP = 0x0202;
        internal const int WM_RBUTTONDOWN = 0x0204;
        internal const int WM_RBUTTONUP = 0x0205;
        internal const int WM_MBUTTONDOWN = 0x0207;
        internal const int WM_MBUTTONUP = 0x0208;
        internal const int WM_XBUTTONDOWN = 0x020B;
        internal const int WM_XBUTTONUP = 0x020C;
        internal const int WM_MOUSEMOVE = 0x0200;

        /// <summary>GetAsyncKeyState / 物理键轮询用（与 MSLLHOOKSTRUCT 侧键一致）</summary>
        internal const int VK_XBUTTON1 = 0x05;
        internal const int VK_XBUTTON2 = 0x06;

        internal const uint LLMHF_INJECTED = 1;

        internal const uint INPUT_MOUSE = 0;
        internal const uint INPUT_KEYBOARD = 1;

        internal const uint MOUSEEVENTF_MOVE = 0x0001;
        internal const uint MOUSEEVENTF_ABSOLUTE = 0x8000;
        internal const uint MOUSEEVENTF_VIRTUALDESK = 0x4000;
        internal const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
        internal const uint MOUSEEVENTF_LEFTUP = 0x0004;
        internal const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
        internal const uint MOUSEEVENTF_RIGHTUP = 0x0010;
        internal const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
        internal const uint MOUSEEVENTF_MIDDLEUP = 0x0040;
        internal const uint MOUSEEVENTF_XDOWN = 0x0080;
        internal const uint MOUSEEVENTF_XUP = 0x0100;

        internal const uint XBUTTON1 = 0x0001;
        internal const uint XBUTTON2 = 0x0002;

        internal const uint KEYEVENTF_KEYUP = 0x0002;
        internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
        internal const uint KEYEVENTF_SCANCODE = 0x0008;

        /// <summary>MapVirtualKey uMapType：虚拟键 → 扫描码（无左右区分）。</summary>
        internal const uint MAPVK_VK_TO_VSC = 0;
        /// <summary>区分左右修饰键等的扫描码；返回值低字为扫描码，高字非零表示扩展键。</summary>
        internal const uint MAPVK_VK_TO_VSC_EX = 4;

        [DllImport("user32.dll")]
        internal static extern uint MapVirtualKey(uint uCode, uint uMapType);

        [StructLayout(LayoutKind.Sequential)]
        internal struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct INPUT
        {
            public uint type;
            public InputUnion U;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        internal delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        [DllImport("user32.dll")]
        internal static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint cButtons, IntPtr dwExtraInfo);

        [DllImport("user32.dll")]
        internal static extern short GetAsyncKeyState(int vKey);

        /// <summary>
        /// 当前线程键盘位图（256 个虚拟键），高字节为 1 表示按下。用于组合键录制，比单独 GetAsyncKeyState 更完整。
        /// </summary>
        [DllImport("user32.dll")]
        internal static extern bool GetKeyboardState(byte[] lpKeyState);

        internal const int VK_LBUTTON = 0x01;
        internal const int VK_RBUTTON = 0x02;
        internal const int VK_MBUTTON = 0x04;

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        internal static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        internal static extern IntPtr GetAncestor(IntPtr hWnd, uint gaFlags);

        [DllImport("user32.dll", SetLastError = false)]
        internal static extern IntPtr GetParent(IntPtr hWnd);

        internal const uint GA_ROOT = 2;
        internal const uint GA_ROOTOWNER = 3;

        internal const uint MK_LBUTTON = 0x0001;
        internal const uint MK_RBUTTON = 0x0002;
        internal const uint MK_MBUTTON = 0x0010;

        [DllImport("user32.dll")]
        internal static extern IntPtr GetForegroundWindow();

        /// <summary>系统双击间隔（毫秒），与控制面板「双击速度」一致；返回 0 时表示需由调用方使用默认。</summary>
        [DllImport("user32.dll")]
        internal static extern uint GetDoubleClickTime();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        internal static extern IntPtr WindowFromPoint(POINT point);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

        internal static string GetClassNameSafe(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return string.Empty;
            var buf = new char[256];
            int n = GetClassName(hWnd, buf, buf.Length);
            return n > 0 ? new string(buf, 0, n) : string.Empty;
        }

        [DllImport("user32.dll")]
        internal static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        internal static extern int GetSystemMetrics(int nIndex);

        internal const int SM_CXSCREEN = 0;
        internal const int SM_CYSCREEN = 1;
        /// <summary>虚拟屏幕左上角 X（多显示器）</summary>
        internal const int SM_XVIRTUALSCREEN = 76;
        /// <summary>虚拟屏幕左上角 Y</summary>
        internal const int SM_YVIRTUALSCREEN = 77;
        internal const int SM_CXVIRTUALSCREEN = 78;
        internal const int SM_CYVIRTUALSCREEN = 79;

        /// <summary>低层键盘钩子 id（与 WH_MOUSE_LL 一样为全局钩子）</summary>
        internal const int WH_KEYBOARD_LL = 13;

        internal const int WM_KEYDOWN = 0x0100;
        internal const int WM_KEYUP = 0x0101;
        internal const int WM_SYSKEYDOWN = 0x0104;
        internal const int WM_SYSKEYUP = 0x0105;

        /// <summary>LLKHF_INJECTED：来自 SendInput 等注入</summary>
        internal const uint LLKHF_INJECTED = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        internal struct KBDLLHOOKSTRUCT
        {
            public uint vkCode;
            public uint scanCode;
            public uint flags;
            public uint time;
            public UIntPtr dwExtraInfo;
        }

        internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

        /// <summary>与现有 SetWindowsHookEx 同入口，委托类型不同故单独声明。</summary>
        [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowsHookExW")]
        internal static extern IntPtr SetWindowsHookExKeyboard(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);
    }
}
