using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace lunagalLauncher.Helpers;

[ComImport, Guid("0000010b-0000-0000-c000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPersistFileForShellLink
{
    void GetClassID(out Guid pClassID);

    [PreserveSig]
    int IsDirty();

    [PreserveSig]
    int Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

    [PreserveSig]
    int Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, [MarshalAs(UnmanagedType.Bool)] bool fRemember);

    [PreserveSig]
    int SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

    [PreserveSig]
    int GetCurFile([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFileName);
}

[ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkWGetPath
{
    [PreserveSig]
    int GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMax, IntPtr pfd, int fFlags);
}

/// <summary>
/// 在文件/保存对话框所在的 STA 线程上安装 <c>WH_GETMESSAGE</c> 发现 Shell 列表/树窗口，
/// 用 <c>SetWindowSubclass</c> 拦截 <c>WM_CONTEXTMENU</c> 并直接吞掉，不在浏览区域内显示右键菜单
/// （仅保留左键操作），同时避免 <c>IContextMenu</c> / Shell 扩展在对话框内被触发导致崩溃。
/// 不引用 Serilog / WinUI。
/// </summary>
public static class FileDialogRmbSuppressHook
{
    /// <summary>本 STA 线程上最近一次成功响应 <c>CDM_GETFOLDERPATH</c> 的句柄（同一会话内优先，避免重复枚举）。</summary>
    [ThreadStatic]
    private static IntPtr t_lastWorkingCdmHwnd;

    private static void NoteWorkingCdmHwnd(IntPtr h)
    {
        if (h != IntPtr.Zero && IsWindow(h))
            t_lastWorkingCdmHwnd = h;
    }

    private static void ClearWorkingCdmHwnd() => t_lastWorkingCdmHwnd = IntPtr.Zero;

    private const int MaxCommDlgCandidates = 200;
    /// <summary>BFS 遍历窗口子树的上限（与 <see cref="MaxCommDlgCandidates"/> 分离，避免普通控件占满候选槽）。</summary>
    private const int MaxSubtreeBfsVisits = 5000;

    private const int WH_GETMESSAGE = 3;
    private const int HC_ACTION = 0;

    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_USER = 0x0400;
    /// <summary>commdlg.h：<c>CDM_FIRST</c>。</summary>
    private const uint CDM_FIRST = WM_USER + 100;
    /// <summary>commdlg：文件名编辑框当前文本（常为选中项显示名）。</summary>
    private const uint CDM_GETSPEC = CDM_FIRST + 0;
    /// <summary>commdlg / Win32 通用项对话框（NT+）。</summary>
    private const uint CDM_GETFILEPATH = CDM_FIRST + 1;
    /// <summary>commdlg：当前文件夹路径。</summary>
    private const uint CDM_GETFOLDERPATH = CDM_FIRST + 2;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    private const int LVM_FIRST = 0x1000;
    private const int LVM_GETNEXTITEM = LVM_FIRST + 12;
    private const int LVM_GETITEMTEXT = LVM_FIRST + 115;
    private const int LVM_SUBITEMHITTEST = LVM_FIRST + 57;
    private const int LVNI_SELECTED = 2;
    private const int LVNI_FOCUSED = 1;
    private const uint LVIF_TEXT = 0x1;

    private const int VK_F5 = 0x74;

    private const int GA_ROOT = 2;

    /// <summary><c>shobjidl.h</c>：<c>SBSP_ABSOLUTE</c>。</summary>
    private const uint SBSP_ABSOLUTE = 0;

    /// <summary><c>shobjidl.h</c>：<c>SBSP_EXPLOREMODE</c>。</summary>
    private const uint SBSP_EXPLOREMODE = 0x0020;

    /// <summary><c>shobjidl.h</c>：<c>SBSP_OPENMODE</c>。</summary>
    private const uint SBSP_OPENMODE = 0x0010;

    /// <summary>MSAA：<c>ROLE_SYSTEM_LISTITEM</c>。</summary>
    private const int MSAA_ROLE_SYSTEM_LISTITEM = 0x22;

    /// <summary>MSAA：树项。</summary>
    private const int MSAA_ROLE_SYSTEM_OUTLINEITEM = 0x24;

    /// <summary>MSAA：行（部分列表实现）。</summary>
    private const int MSAA_ROLE_SYSTEM_ROW = 0x28;

    /// <summary>MSAA：静态文本（DirectUI 列表项显示名常见）。</summary>
    private const int MSAA_ROLE_SYSTEM_STATICTEXT = 0x29;

    /// <summary>MSAA：可编辑文本。</summary>
    private const int MSAA_ROLE_SYSTEM_TEXT = 0x2A;

    private static readonly Guid IID_IShellBrowser = new("000214E2-0000-0000-C000-000000000046");
    private static readonly Guid SID_SCommDlgBrowser = new("000214F1-0000-0000-C000-000000000046");

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const uint MF_STRING = 0;
    private const uint MF_SEPARATOR = 0x800;
    private const uint MF_GRAYED = 0x1;
    private const uint MF_DISABLED = 0x2;

    private const int CF_UNICODETEXT = 13;
    private const uint GMEM_MOVEABLE = 0x0002;

    private const int CmdCopyPath = 1001;
    private const int CmdCopyName = 1002;
    private const int CmdOpenFolder = 1003;
    private const int CmdRefresh = 1004;

    private static readonly UIntPtr SubclassId = new(0x4C474652); // 'LGFR'

    private static readonly HashSet<string> s_subclassTargets = new(StringComparer.OrdinalIgnoreCase)
    {
        "SysListView32",
        "SHELLDLL_DefView",
        "DirectUIHWND",
        "NamespaceTreeControl",
        "ExplorerBrowserControl",
    };

    private static readonly object s_subLock = new();
    private static readonly HashSet<IntPtr> s_subclassed = new();

    private static readonly HookProc s_hookProc = HookCallback;
    private static readonly SubclassWndProc s_subclassProc = SubclassWndProcImpl;

    /// <summary>
    /// helper 线程当前显示的原生 IFileOpenDialog。右键 hook 用它直接读取当前选中项/文件夹，
    /// 避免只靠 DirectUI 内部窗口文本猜测。
    /// </summary>
    [ThreadStatic]
    private static IFileDialogForHook? t_activeFileDialog;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate IntPtr SubclassWndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [ComImport]
    [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemForHook
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IShellItemForHook ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IShellItemForHook psi, uint hint, out int piOrder);
    }

    [ComImport]
    [Guid("42f85136-db7e-439c-85fb-4201c6db9ee0")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileDialogForHook
    {
        [PreserveSig] int Show(IntPtr hwndOwner);
        [PreserveSig] int SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        [PreserveSig] int SetFileTypeIndex(uint iFileType);
        [PreserveSig] int GetFileTypeIndex(out uint piFileType);
        [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOptions(uint fos);
        [PreserveSig] int GetOptions(out uint pfos);
        [PreserveSig] int SetDefaultFolder(IShellItemForHook psi);
        [PreserveSig] int SetFolder(IShellItemForHook psi);
        [PreserveSig] int GetFolder(out IShellItemForHook ppsi);
        [PreserveSig] int GetCurrentSelection(out IShellItemForHook ppsi);
        [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        [PreserveSig] int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        [PreserveSig] int GetResult(out IShellItemForHook ppsi);
        [PreserveSig] int AddPlace(IShellItemForHook psi, int fdap);
        [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        [PreserveSig] int Close(int hr);
        [PreserveSig] int SetClientGuid(ref Guid guid);
        [PreserveSig] int ClearClientData();
        [PreserveSig] int SetFilter(IntPtr pFilter);
    }

    [ComImport]
    [Guid("00000117-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IOleWindowForHook
    {
        [PreserveSig] int GetWindow(out IntPtr phwnd);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
    }

    [ComImport]
    [Guid("000214E2-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowserForHook : IOleWindowForHook
    {
        [PreserveSig] new int GetWindow(out IntPtr phwnd);
        [PreserveSig] new int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool fEnterMode);
        [PreserveSig] int InsertMenusSB(IntPtr hmenuShared, ref IntPtr lpMenuWidths);
        [PreserveSig] int SetMenuSB(IntPtr hmenuShared, IntPtr holemenuRes, IntPtr hwndActiveObject);
        [PreserveSig] int RemoveMenusSB(IntPtr hmenuShared);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string pszStatusText);
        [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool fEnable);
        [PreserveSig] int TranslateAcceleratorSB(IntPtr pmsg, ushort wID);
        [PreserveSig] int BrowseObject(IntPtr pidl, uint wFlags);
        [PreserveSig] int GetViewStateStream(uint grfMode, out IntPtr ppStrm);
        [PreserveSig] int GetControlWindow(uint id, ref IntPtr phwnd);
        [PreserveSig] int SendControlMsg(uint id, uint uMsg, IntPtr wParam, IntPtr lParam, out IntPtr pret);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.Interface)] out object? ppshv);
        [PreserveSig] int OnViewWindowActive(object? pshv);
        [PreserveSig] int SetToolbarItems(IntPtr lpButtons, uint nButtons, uint uFlags);
    }

    [ComImport]
    [Guid("00000040-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProviderForHook
    {
        [PreserveSig]
        int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LVITEMW
    {
        public uint mask;
        public int iItem;
        public int iSubItem;
        public uint state;
        public uint stateMask;
        public IntPtr pszText;
        public int cchTextMax;
        public int iImage;
        public IntPtr lParam;
        public int iIndent;
        public int iGroupId;
        public uint cColumns;
        public IntPtr puColumns;
        public IntPtr piColFmt;
        public int iGroup;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LVHITTESTINFO
    {
        public POINT pt;
        public uint flags;
        public int iItem;
        public int iSubItem;
        public int iGroup;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, [Out] char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, ref LVITEMW lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, ref LVHITTESTINFO lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassWndProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassWndProc pfnSubclass, UIntPtr uIdSubclass);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, UIntPtr uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr hMem);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetCurrentDirectoryW(uint nBufferLength, [Out] char[] lpBuffer);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetDlgItem(IntPtr hDlg, int nIDDlgItem);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, [Out] StringBuilder lpString, int nMaxCount);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string pszName,
        IntPtr pbc,
        out IntPtr ppidl,
        uint sfgaoIn,
        out uint psfgaoOut);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath,
        IntPtr pbc,
        ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemForHook? ppv);

    [StructLayout(LayoutKind.Sequential)]
    private struct OLEACC_POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("oleacc.dll", PreserveSig = true)]
    private static extern int AccessibleObjectFromPoint(ref OLEACC_POINT pt, [MarshalAs(UnmanagedType.IDispatch)] out object? ppacc, [MarshalAs(UnmanagedType.Struct)] out object pvarChild);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPhysicalPoint(POINT pt);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowTextW(IntPtr hWnd, string? lpString);

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint cInputs, INPUT[] pInputs, int cbSize);

    private static void SendKeyVk(ushort vk, bool keyUp)
    {
        var inp = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = vk,
                    dwFlags = keyUp ? KEYEVENTF_KEYUP : 0,
                },
            },
        };
        _ = SendInput(1, new[] { inp }, Marshal.SizeOf<INPUT>());
    }

    /// <summary>commdlg 外壳在 Shell 浏览器的 <c>BrowseObject</c> 无效时，尝试 Alt+D 聚焦地址栏后键入路径并回车。</summary>
    private static bool TryNavigateCommDlgViaAddressBarKeyboard(IntPtr dlg32770, string fullDir)
    {
        if (dlg32770 == IntPtr.Zero || !IsWindow(dlg32770) || string.IsNullOrWhiteSpace(fullDir))
            return false;

        string dirNorm;
        try
        {
            if (!Directory.Exists(fullDir))
                return false;
            dirNorm = Path.GetFullPath(fullDir.Trim());
        }
        catch
        {
            return false;
        }

        try
        {

            _ = SetForegroundWindow(dlg32770);
            Thread.Sleep(45);

            const ushort vkMenu = 0x12;
            const ushort vkD = 0x44;
            const ushort vkReturn = 0x0D;

            SendKeyVk(vkMenu, keyUp: false);
            SendKeyVk(vkD, keyUp: false);
            SendKeyVk(vkD, keyUp: true);
            SendKeyVk(vkMenu, keyUp: true);

            Thread.Sleep(160);
            IntPtr focus = GetFocus();
            if (focus == IntPtr.Zero || !IsWindow(focus))
                return false;

            if (!SetWindowTextW(focus, dirNorm))
                return false;

            Thread.Sleep(35);
            SendKeyVk(vkReturn, keyUp: false);
            SendKeyVk(vkReturn, keyUp: true);
            Thread.Sleep(80);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>与 <c>IUIAutomation::ElementFromPoint</c> 的 <c>POINT</c> 布局一致（勿用 <c>System.Drawing.Point</c> 以免封送偏差）。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct UIA_TAGPOINT
    {
        public int X;
        public int Y;
    }

    /// <summary>诊断日志是否对候选窗发 CDM（易触发布局刷新）；需 <c>LUNAGAL_FILEDIALOG_RMB_DEEP_LOG=1</c>。</summary>
    private static bool ShouldDiagDeepLogFile() =>
        string.Equals(Environment.GetEnvironmentVariable("LUNAGAL_FILEDIALOG_RMB_DEEP_LOG"), "1", StringComparison.Ordinal);

    /// <summary>在当前 STA 线程安装钩子；失败时返回可安全 Dispose 的空操作句柄（不抛异常）。</summary>
    public static IDisposable InstallOnCurrentThread()
    {
        uint tid = GetCurrentThreadId();
        IntPtr h = SetWindowsHookExW(WH_GETMESSAGE, s_hookProc, IntPtr.Zero, tid);
        if (h == IntPtr.Zero)
            return NoopDisposable.Instance;
        return new HookHandle(h);
    }

    /// <summary>登记/清除当前 STA 正在显示的原生文件对话框，供右键菜单解析当前选中项。</summary>
    public static void RegisterActiveFileDialog(object? dialog)
    {
        t_activeFileDialog = null;
        if (dialog == null)
            return;

        try
        {
            if (dialog is IFileDialogForHook ok)
            {
                t_activeFileDialog = ok;
                return;
            }

            IntPtr unk = Marshal.GetIUnknownForObject(dialog);
            try
            {
                Guid riid = new("42f85136-db7e-439c-85fb-4201c6db9ee0");
                if (Marshal.QueryInterface(unk, in riid, out IntPtr pv) != 0 || pv == IntPtr.Zero)
                    return;

                try
                {
                    t_activeFileDialog = (IFileDialogForHook)Marshal.GetObjectForIUnknown(pv);
                }
                finally
                {
                    Marshal.Release(pv);
                }
            }
            finally
            {
                Marshal.Release(unk);
            }
        }
        catch
        {
            t_activeFileDialog = null;
        }
    }

    private static IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode == HC_ACTION && wParam != IntPtr.Zero && lParam != IntPtr.Zero)
        {
            MSG msg = Marshal.PtrToStructure<MSG>(lParam);
            if (msg.hwnd != IntPtr.Zero)
                TrySubclassShellView(msg.hwnd);
        }

        return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static void TrySubclassShellView(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return;

        lock (s_subLock)
        {
            if (s_subclassed.Contains(hwnd) && !IsWindow(hwnd))
                s_subclassed.Remove(hwnd);
        }

        if (!IsWindow(hwnd))
            return;

        if (!IsSubclassTargetClass(hwnd))
            return;

        lock (s_subLock)
        {
            if (s_subclassed.Contains(hwnd))
                return;

            if (!SetWindowSubclass(hwnd, s_subclassProc, SubclassId, UIntPtr.Zero))
                return;

            s_subclassed.Add(hwnd);
        }
    }

    private static bool IsSubclassTargetClass(IntPtr hwnd)
    {
        var buf = new char[256];
        int n = GetClassNameW(hwnd, buf, buf.Length);
        if (n <= 0)
            return false;

        ReadOnlySpan<char> span = buf.AsSpan(0, n);
        if (span.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase))
            return false;

        string name = new string(span);
        if (name.Equals("Edit", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ComboBox", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ComboLBox", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Button", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Static", StringComparison.OrdinalIgnoreCase))
            return false;

        return s_subclassTargets.Contains(name);
    }

    private static IntPtr SubclassWndProcImpl(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (uMsg == WM_CONTEXTMENU)
        {
            // 浏览框列表/树等区域不弹出右键菜单（与仅使用左键一致）；返回非零已处理，不调用系统默认菜单。
            return (IntPtr)1;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam, uIdSubclass, dwRefData);
    }

    private static void ShowSafePopup(IntPtr viewHwnd, int screenX, int screenY)
    {
        IntPtr dlg = FindCommDlgHwnd(viewHwnd);
        if (dlg == IntPtr.Zero)
            dlg = GetAncestor(viewHwnd, GA_ROOT);
        if (dlg == IntPtr.Zero)
            dlg = viewHwnd;

        IReadOnlyList<IntPtr> candidates = BuildCommDlgCandidateList(viewHwnd, dlg);
        string? folder = TryFirstCommDlgFolder(candidates);
        FillCommDlgTemplateFolderHintsOnly(dlg, ref folder);

        TryResolveHitItemUnderCursor(viewHwnd, folder, screenX, screenY, out string? hitPath, out string? hitName);

        string? path = !string.IsNullOrEmpty(hitPath) ? hitPath : null;
        if (string.IsNullOrEmpty(path))
            path = TryResolvePickerItemPath(
                viewHwnd,
                dlg,
                screenX,
                screenY,
                candidates,
                allowShortcutResolution: false,
                allowCommDlgFilenameEditFallback: t_activeFileDialog == null);

        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(hitName))
        {
            string? onlyHit = TryCombineFolderAndNameWithExistenceProbe(folder, hitName);
            if (!string.IsNullOrEmpty(onlyHit) && (File.Exists(onlyHit) || Directory.Exists(onlyHit)))
                path = onlyHit;
        }

        bool combinedPathWithoutProbe = false;
        string? nameForClipboard = !string.IsNullOrWhiteSpace(hitName) ? hitName.Trim() : null;
        if (string.IsNullOrEmpty(nameForClipboard))
            nameForClipboard = !string.IsNullOrEmpty(path) ? Path.GetFileName(path) : null;
        if (string.IsNullOrEmpty(nameForClipboard))
            nameForClipboard = TryOleaccShortLeafNameAtScreenPoint(screenX, screenY);
        if (string.IsNullOrEmpty(nameForClipboard) && t_activeFileDialog == null && string.IsNullOrEmpty(hitName))
        {
            string? specHint = TryCommDlgTemplateFileSpec(dlg);
            if (!string.IsNullOrWhiteSpace(specHint))
                nameForClipboard = specHint.Trim();
        }

        if (string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(nameForClipboard))
        {
            string? merged = TryCombineFolderAndNameWithExistenceProbe(folder, nameForClipboard);
            if (!string.IsNullOrEmpty(merged))
            {
                path = merged;
                combinedPathWithoutProbe = true;
            }
        }

        path = TryDereferenceShellShortcutToTargetPath(path);

        bool folderLooksLikePath = !string.IsNullOrEmpty(folder) && LooksLikeReasonableFsPath(folder.Trim());
        string? pathForClipboard = path;
        if (string.IsNullOrEmpty(pathForClipboard) && !string.IsNullOrEmpty(hitName) && !string.IsNullOrEmpty(folder))
            pathForClipboard = TryCombineFolderAndNameWithExistenceProbe(folder, hitName);
        if (string.IsNullOrEmpty(pathForClipboard) && !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(nameForClipboard))
            pathForClipboard = TryCombineFolderAndNameWithExistenceProbe(folder, nameForClipboard);

        if (string.IsNullOrEmpty(pathForClipboard) && !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(hitName))
        {
            string? spec = TryCombineFolderAndName(folder, hitName);
            if (!string.IsNullOrEmpty(spec) && LooksLikeReasonableFsPath(spec))
                pathForClipboard = spec;
        }

        if (string.IsNullOrEmpty(pathForClipboard) && !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(nameForClipboard))
        {
            string? spec = TryCombineFolderAndName(folder, nameForClipboard);
            if (!string.IsNullOrEmpty(spec) && LooksLikeReasonableFsPath(spec))
                pathForClipboard = spec;
        }

        if (string.IsNullOrEmpty(nameForClipboard) && !string.IsNullOrEmpty(hitName))
            nameForClipboard = hitName.Trim();

        pathForClipboard = TryDereferenceShellShortcutToTargetPath(pathForClipboard);

        bool hasResolvedPath = !string.IsNullOrEmpty(path);
        bool isFilePath = hasResolvedPath && File.Exists(path!);
        bool isDirectoryPath = hasResolvedPath && Directory.Exists(path!);
        bool canOpenLocation = isFilePath
            || isDirectoryPath
            || (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            || folderLooksLikePath;

        bool canCopyFullPath = !string.IsNullOrEmpty(pathForClipboard)
            && (LooksLikeReasonableFsPath(pathForClipboard.Trim())
                || File.Exists(pathForClipboard)
                || Directory.Exists(pathForClipboard)
                || (combinedPathWithoutProbe && pathForClipboard.Trim().Length > 0)
                || (!string.IsNullOrEmpty(hitName) && !string.IsNullOrEmpty(folder)));

        bool canCopyName = !string.IsNullOrEmpty(nameForClipboard)
            || !string.IsNullOrEmpty(hitName);
        string copyNameLabel = isDirectoryPath ? "复制文件夹名" : "复制文件名";

#if DEBUG
        LogFileDialogRmbDiagnostic(viewHwnd, dlg, candidates, path, folder, screenX, screenY, hitName);
#endif

        uint disCopyPath = canCopyFullPath ? 0 : (MF_GRAYED | MF_DISABLED);
        uint disCopyName = canCopyName ? 0 : (MF_GRAYED | MF_DISABLED);
        uint disOpen = canOpenLocation ? 0 : (MF_GRAYED | MF_DISABLED);

        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
            return;

        try
        {
            AppendMenuW(menu, MF_STRING | disCopyPath, (UIntPtr)CmdCopyPath, "复制路径");
            AppendMenuW(menu, MF_STRING | disCopyName, (UIntPtr)CmdCopyName, copyNameLabel);
            AppendMenuW(menu, MF_STRING | disOpen, (UIntPtr)CmdOpenFolder, "打开所在文件夹");
            AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            AppendMenuW(menu, MF_STRING, (UIntPtr)CmdRefresh, "刷新列表");

            int cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screenX, screenY, 0, dlg, IntPtr.Zero);

            switch (cmd)
            {
                case CmdCopyPath:
                    {
                        string? z = pathForClipboard;
                        if (string.IsNullOrEmpty(z))
                            z = path;
                        if (string.IsNullOrEmpty(z) && !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(hitName))
                            z = TryCombineFolderAndNameWithExistenceProbe(folder, hitName);
                        if (string.IsNullOrEmpty(z) && !string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(nameForClipboard))
                            z = TryCombineFolderAndNameWithExistenceProbe(folder, nameForClipboard);
                        z = TryDereferenceShellShortcutToTargetPath(z);
                        if (!string.IsNullOrEmpty(z))
                            SetClipboardUnicode(z);
                        break;
                    }
                case CmdCopyName:
                    {
                        string? n = nameForClipboard;
                        if (string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(hitName))
                            n = hitName;
                        if (string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(path))
                            n = Path.GetFileName(path);
                        if (string.IsNullOrEmpty(n) && !string.IsNullOrEmpty(pathForClipboard))
                            n = Path.GetFileName(pathForClipboard);
                        if (!string.IsNullOrEmpty(n))
                            SetClipboardUnicode(n);
                        break;
                    }
                case CmdOpenFolder:
                    {
                        string? targetDir = null;

                        if (!string.IsNullOrEmpty(hitName) && !string.IsNullOrEmpty(folder))
                        {
                            string? hitMerged = TryCombineFolderAndNameWithExistenceProbe(folder, hitName);
                            if (!string.IsNullOrEmpty(hitMerged))
                            {
                                try
                                {
                                    if (Directory.Exists(hitMerged))
                                        targetDir = Path.GetFullPath(hitMerged);
                                    else if (File.Exists(hitMerged))
                                        targetDir = Path.GetDirectoryName(Path.GetFullPath(hitMerged));
                                }
                                catch
                                {
                                    /* ignore */
                                }
                            }
                        }

                        if (string.IsNullOrEmpty(targetDir) && isDirectoryPath && !string.IsNullOrEmpty(path))
                            targetDir = path;
                        if (string.IsNullOrEmpty(targetDir) && isFilePath && !string.IsNullOrEmpty(path))
                        {
                            try
                            {
                                targetDir = Path.GetDirectoryName(Path.GetFullPath(path));
                            }
                            catch
                            {
                                targetDir = Path.GetDirectoryName(path);
                            }
                        }

                        if (string.IsNullOrEmpty(targetDir) && !string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                            targetDir = folder;
                        if (string.IsNullOrEmpty(targetDir) && !string.IsNullOrEmpty(path) && LooksLikeReasonableFsPath(path))
                        {
                            try
                            {
                                targetDir = Path.GetDirectoryName(Path.GetFullPath(path));
                            }
                            catch
                            {
                                targetDir = Path.GetDirectoryName(path);
                            }

                            if (string.IsNullOrEmpty(targetDir))
                                targetDir = path;
                        }

                        if (!string.IsNullOrEmpty(targetDir) && TryBrowseShellViewToPath(dlg, viewHwnd, targetDir))
                            break;

                        if (isFilePath)
                            TryOpenInExplorerSelect(path!);
                        else if (isDirectoryPath && !string.IsNullOrEmpty(path))
                            TryOpenExplorerFolder(path);
                        else if (!string.IsNullOrEmpty(folder))
                            TryOpenExplorerFolder(folder);
                        else if (!string.IsNullOrEmpty(path) && LooksLikeReasonableFsPath(path))
                        {
                            string? d = Path.GetDirectoryName(path);
                            if (!string.IsNullOrEmpty(d))
                                TryOpenExplorerFolder(d);
                            else
                                TryOpenExplorerFolder(path);
                        }
                        break;
                    }
                case CmdRefresh:
                    PostRefreshList(viewHwnd);
                    break;
            }
        }
        finally
        {
            try { DestroyMenu(menu); } catch { /* ignore */ }
        }
    }

    private static string? TryFirstCommDlgFolder(IReadOnlyList<IntPtr> candidates)
    {
        string? activeFolder = TryActiveFileDialogFolderPath();
        if (!string.IsNullOrEmpty(activeFolder))
            return activeFolder;

        foreach (IntPtr h in candidates)
        {
            string? f = TryCommDlgGetFolderPath(h);
            if (!string.IsNullOrEmpty(f))
            {
                NoteWorkingCdmHwnd(h);
                return f;
            }
        }

        foreach (IntPtr h in candidates)
        {
            string? fp = TryCommDlgGetFilePath(h);
            if (string.IsNullOrEmpty(fp))
                continue;
            fp = fp.Trim();
            try
            {
                if (Directory.Exists(fp))
                    return fp;
                if (File.Exists(fp))
                {
                    string? d = Path.GetDirectoryName(fp);
                    if (!string.IsNullOrEmpty(d))
                        return d;
                }
            }
            catch
            {
                /* ignore */
            }
        }

        return null;
    }

    private static string? GetWindowTextSafe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd))
            return null;
        int n = GetWindowTextLengthW(hwnd);
        if (n <= 0)
            return null;
        var sb = new StringBuilder(Math.Min(n + 2, 32768));
        int got = GetWindowTextW(hwnd, sb, sb.Capacity);
        if (got <= 0)
            return null;
        string s = sb.ToString().Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static string? TryGetProcessCurrentDirectoryPath()
    {
        try
        {
            var buf = new char[32768];
            uint r = GetCurrentDirectoryW((uint)buf.Length, buf);
            if (r == 0 || r >= buf.Length)
                return null;
            string s = new string(buf.AsSpan(0, (int)r)).Trim();
            if (string.IsNullOrEmpty(s) || !LooksLikeReasonableFsPath(s))
                return null;
            return NormalizeFsPath(Path.GetFullPath(s));
        }
        catch
        {
            return null;
        }
    }

    private static void ApplyCommDlgFolderHintOnly(string? raw, ref string? folder)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return;
        string t = raw.Trim().Trim('"');
        if (t.Length == 0)
            return;
        try
        {
            if (Directory.Exists(t))
            {
                folder = NormalizeFsPath(Path.GetFullPath(t));
                return;
            }

            if (File.Exists(t))
            {
                string? fp = NormalizeFsPath(Path.GetFullPath(t));
                string? d = Path.GetDirectoryName(fp);
                if (!string.IsNullOrEmpty(d))
                    folder ??= NormalizeFsPath(Path.GetFullPath(d));
                return;
            }

            if (LooksLikeReasonableFsPath(t)
                && (t.Contains(Path.DirectorySeparatorChar) || t.Contains(Path.AltDirectorySeparatorChar)))
                folder ??= NormalizeFsPath(Path.GetFullPath(t));
        }
        catch
        {
            /* ignore */
        }
    }

    private static void FillCommDlgTemplateFolderHintsOnly(IntPtr dlg32770, ref string? folder)
    {
        if (dlg32770 == IntPtr.Zero || !IsWindow(dlg32770))
            return;

        string? bestFolder = folder;
        if (string.IsNullOrEmpty(bestFolder))
            bestFolder = TryGetProcessCurrentDirectoryPath();

        foreach (int id in new[] { 0x480, 0x3E9, 0x47C, 0x3EC, 0x3EB, 0x3E8, 1152, 1001, 1148, 1120 })
        {
            IntPtr h = GetDlgItem(dlg32770, id);
            if (h == IntPtr.Zero || !IsWindow(h))
                continue;
            ApplyCommDlgFolderHintOnly(GetWindowTextSafe(h), ref bestFolder);
        }

        var seen = new HashSet<IntPtr>();
        var q = new Queue<IntPtr>();
        q.Enqueue(dlg32770);
        int visits = 0;
        while (q.Count > 0 && visits < 800)
        {
            IntPtr p = q.Dequeue();
            visits++;
            if (!seen.Add(p))
                continue;

            string cls = GetWindowClassName(p);
            if (cls.Equals("Edit", StringComparison.OrdinalIgnoreCase)
                || cls.Contains("Combo", StringComparison.OrdinalIgnoreCase))
                ApplyCommDlgFolderHintOnly(GetWindowTextSafe(p), ref bestFolder);

            for (IntPtr ch = FindWindowExW(p, IntPtr.Zero, null, null); ch != IntPtr.Zero; ch = FindWindowExW(p, ch, null, null))
                q.Enqueue(ch);
        }

        folder = bestFolder;
    }

    private static string? TryCommDlgTemplateFileSpec(IntPtr dlg32770)
    {
        if (dlg32770 == IntPtr.Zero || !IsWindow(dlg32770))
            return null;

        string? spec = null;
        foreach (int id in new[] { 0x480, 0x3E9, 0x47C, 0x3EC, 0x3EB, 0x3E8, 1152, 1001 })
        {
            IntPtr h = GetDlgItem(dlg32770, id);
            if (h == IntPtr.Zero || !IsWindow(h))
                continue;
            string? t = GetWindowTextSafe(h);
            if (string.IsNullOrWhiteSpace(t))
                continue;
            t = t.Trim();
            if (t is "." or "..")
                continue;
            if (t.Contains(':', StringComparison.Ordinal) || t.Contains('\\') || t.Contains('/'))
                continue;
            if (t.Length > 0 && t.Length < 512)
                spec = t;
        }

        return spec;
    }

    private static string? TryActiveFileDialogSelectionPath()
    {
        var dialog = t_activeFileDialog;
        if (dialog == null)
            return null;

        IShellItemForHook? item = null;
        try
        {
            if (dialog.GetCurrentSelection(out item) != 0 || item == null)
                return null;
            if (item.GetDisplayName(SIGDN_FILESYSPATH, out var path) != 0)
                return null;
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                if (File.Exists(path) || Directory.Exists(path))
                    return NormalizeFsPath(Path.GetFullPath(path));
            }
            catch
            {
                return NormalizeFsPath(path);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (item != null)
            {
                try { Marshal.FinalReleaseComObject(item); } catch { /* ignore */ }
            }
        }

        return null;
    }

    private static string? TryActiveFileDialogFolderPath()
    {
        var dialog = t_activeFileDialog;
        if (dialog == null)
            return null;

        IShellItemForHook? item = null;
        try
        {
            if (dialog.GetFolder(out item) != 0 || item == null)
                return null;
            if (item.GetDisplayName(SIGDN_FILESYSPATH, out var path) != 0)
                return null;
            if (string.IsNullOrWhiteSpace(path))
                return null;
            try
            {
                if (Directory.Exists(path))
                    return NormalizeFsPath(Path.GetFullPath(path));
                return NormalizeFsPath(path);
            }
            catch
            {
                return NormalizeFsPath(path);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            if (item != null)
            {
                try { Marshal.FinalReleaseComObject(item); } catch { /* ignore */ }
            }
        }
    }

    private static void TryOpenExplorerFolder(string folder)
    {
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            string arg = "\"" + folder.Trim().Replace("\"", "\\\"") + "\"";
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = explorer,
                Arguments = arg,
                UseShellExecute = false,
            });
        }
        catch
        {
            /* ignore */
        }
    }

    private static IntPtr FindCommDlgHwnd(IntPtr start)
    {
        IntPtr w = start;
        for (int i = 0; i < 40 && w != IntPtr.Zero; i++)
        {
            if (string.Equals(GetWindowClassName(w), "#32770", StringComparison.Ordinal))
                return w;
            w = GetParent(w);
        }

        return IntPtr.Zero;
    }

    private static string GetWindowClassName(IntPtr hwnd)
    {
        var buf = new char[256];
        int n = GetClassNameW(hwnd, buf, buf.Length);
        return n <= 0 ? string.Empty : new string(buf.AsSpan(0, n));
    }

    private static bool IsLikelyIUnknownPtr(IntPtr p)
    {
        if (p == IntPtr.Zero)
            return false;
        if (IntPtr.Size == 8)
        {
            ulong u = unchecked((ulong)p.ToInt64());
            if (u < 0x10000UL || u > 0x00007FFFFFFFFFFFUL)
                return false;
            return true;
        }

        return unchecked((uint)p.ToInt32()) >= 0x10000U;
    }

    private static IntPtr TryGetIShellBrowserPtr(IntPtr defView)
    {
        if (defView == IntPtr.Zero)
            return IntPtr.Zero;

        Guid iidSb = IID_IShellBrowser;
        foreach (uint msg in new uint[] { WM_USER + 166, WM_USER + 109, WM_USER + 168, WM_USER + 164, WM_USER + 107, WM_USER + 103, WM_USER + 102 })
        {
            IntPtr pUnk = SendMessageW(defView, msg, IntPtr.Zero, IntPtr.Zero);
            if (pUnk == IntPtr.Zero || !IsLikelyIUnknownPtr(pUnk))
                continue;

            try
            {
                if (Marshal.QueryInterface(pUnk, in iidSb, out IntPtr pOk) == 0 && pOk != IntPtr.Zero)
                {
                    Marshal.Release(pOk);
                    return pUnk;
                }

                Guid iidSp = new("00000040-0000-0000-C000-000000000046");
                if (Marshal.QueryInterface(pUnk, in iidSp, out IntPtr pSp) != 0 || pSp == IntPtr.Zero)
                {
                    Marshal.Release(pUnk);
                    continue;
                }

                try
                {
                    var sp = (IServiceProviderForHook)Marshal.GetObjectForIUnknown(pSp);
                    try
                    {
                        Guid sidDlg = SID_SCommDlgBrowser;
                        Guid riidBr = iidSb;
                        if (sp.QueryService(ref sidDlg, ref riidBr, out IntPtr pBrowser) == 0 && pBrowser != IntPtr.Zero)
                        {
                            Marshal.Release(pUnk);
                            return pBrowser;
                        }

                        Guid sidTop = iidSb;
                        riidBr = iidSb;
                        if (sp.QueryService(ref sidTop, ref riidBr, out IntPtr pBrowser2) == 0 && pBrowser2 != IntPtr.Zero)
                        {
                            Marshal.Release(pUnk);
                            return pBrowser2;
                        }
                    }
                    finally
                    {
                        try { Marshal.FinalReleaseComObject(sp); } catch { /* ignore */ }
                    }
                }
                finally
                {
                    Marshal.Release(pSp);
                }

                Marshal.Release(pUnk);
            }
            catch
            {
                try { Marshal.Release(pUnk); } catch { /* ignore */ }
            }
        }

        return IntPtr.Zero;
    }

    private static List<IntPtr> ListShellDllDefViewsOrdered(IntPtr root)
    {
        var ranked = new List<(IntPtr h, int score)>();
        if (root == IntPtr.Zero || !IsWindow(root))
            return new List<IntPtr>();

        var seen = new HashSet<IntPtr>();
        var q = new Queue<IntPtr>();
        q.Enqueue(root);
        int visits = 0;
        while (q.Count > 0 && visits < MaxSubtreeBfsVisits)
        {
            IntPtr p = q.Dequeue();
            visits++;
            if (!seen.Add(p))
                continue;

            if (GetWindowClassName(p).Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase))
            {
                int score = 0;
                if (FindWindowExW(p, IntPtr.Zero, "SysListView32", null) != IntPtr.Zero)
                    score += 10;
                if (FindWindowExW(p, IntPtr.Zero, "DirectUIHWND", null) != IntPtr.Zero)
                    score += 8;
                ranked.Add((p, score));
            }

            for (IntPtr ch = FindWindowExW(p, IntPtr.Zero, null, null); ch != IntPtr.Zero; ch = FindWindowExW(p, ch, null, null))
                q.Enqueue(ch);
        }

        ranked.Sort((a, b) => b.score.CompareTo(a.score));
        var list = new List<IntPtr>(ranked.Count);
        foreach (var x in ranked)
            list.Add(x.h);
        return list;
    }

    /// <summary>当前线程上的 Vista+ <c>IFileOpenDialog</c> 在浏览区内导航（与 <c>IShellBrowser::BrowseObject</c> 互补）。</summary>
    private static bool TryNavigateActiveIFileDialogToPath(string directoryPath)
    {
        IFileDialogForHook? dlg = t_activeFileDialog;
        if (dlg == null || string.IsNullOrWhiteSpace(directoryPath))
            return false;

        string dir = directoryPath.Trim();
        try
        {
            if (File.Exists(dir))
            {
                string? d = Path.GetDirectoryName(Path.GetFullPath(dir));
                if (!string.IsNullOrEmpty(d))
                    dir = d;
            }

            if (!Directory.Exists(dir))
                return false;

            dir = Path.GetFullPath(dir);
            Guid riid = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
            if (SHCreateItemFromParsingName(dir, IntPtr.Zero, ref riid, out IShellItemForHook? item) != 0 || item == null)
                return false;

            try
            {
                return dlg.SetFolder(item) == 0;
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(item); } catch { /* ignore */ }
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool TryBrowseShellViewToPath(IntPtr dlg32770, IntPtr viewHwnd, string targetPath)
    {
        if (dlg32770 == IntPtr.Zero || !IsWindow(dlg32770) || string.IsNullOrWhiteSpace(targetPath))
            return false;

        string navigateTo = targetPath.Trim();
        try
        {
            if (File.Exists(navigateTo))
            {
                string? d = Path.GetDirectoryName(Path.GetFullPath(navigateTo));
                if (!string.IsNullOrEmpty(d))
                    navigateTo = d;
            }
            else if (!Directory.Exists(navigateTo))
            {
                string? d = Path.GetDirectoryName(Path.GetFullPath(navigateTo));
                if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
                    navigateTo = d;
                else
                    return false;
            }
            else
                navigateTo = Path.GetFullPath(navigateTo);
        }
        catch
        {
            return false;
        }

        if (TryNavigateActiveIFileDialogToPath(navigateTo))
            return true;

        uint[] browseFlags =
        {
            SBSP_ABSOLUTE,
            SBSP_ABSOLUTE | SBSP_EXPLOREMODE,
            SBSP_ABSOLUTE | SBSP_OPENMODE,
            SBSP_ABSOLUTE | SBSP_EXPLOREMODE | SBSP_OPENMODE,
        };

        foreach (IntPtr defView in EnumerateDefViewsForBrowse(dlg32770, viewHwnd))
        {
            IntPtr psb = TryGetIShellBrowserPtr(defView);
            if (psb == IntPtr.Zero)
                continue;

            IntPtr pidl = IntPtr.Zero;
            try
            {
                if (!TryShParseDisplayNameToPidl(navigateTo, out pidl) || pidl == IntPtr.Zero)
                    continue;

                var browser = (IShellBrowserForHook)Marshal.GetObjectForIUnknown(psb);
                try
                {
                    foreach (uint fl in browseFlags)
                    {
                        int hr = browser.BrowseObject(pidl, fl);
                        if (hr >= 0)
                            return true;
                    }
                }
                finally
                {
                    try { Marshal.FinalReleaseComObject(browser); } catch { /* ignore */ }
                }
            }
            catch
            {
                /* next defview */
            }
            finally
            {
                if (pidl != IntPtr.Zero)
                    ILFree(pidl);
            }
        }

        if (TryNavigateCommDlgViaAddressBarKeyboard(dlg32770, navigateTo))
            return true;

        return false;
    }

    private static bool TryShParseDisplayNameToPidl(string path, out IntPtr pidl)
    {
        pidl = IntPtr.Zero;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        path = path.Trim();
        if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
            return true;

        string? alt = null;
        try
        {
            if (!path.StartsWith("\\\\?\\", StringComparison.Ordinal))
            {
                if (path.StartsWith("\\\\", StringComparison.Ordinal))
                    alt = "\\\\?\\UNC\\" + path[2..];
                else
                    alt = "\\\\?\\" + path;
            }
        }
        catch
        {
            /* ignore */
        }

        if (!string.IsNullOrEmpty(alt) && SHParseDisplayName(alt, IntPtr.Zero, out pidl, 0, out _) == 0 && pidl != IntPtr.Zero)
            return true;

        return false;
    }

    /// <summary>
    /// <paramref name="allowShortcutResolution"/> 为 true 时优先 IFileDialog 选中项与 CDM_GETFILEPATH（非右键场景）。
    /// <paramref name="allowCommDlgFilenameEditFallback"/> 为 false 时不用 <c>CDM_GETSPEC</c> / 模板文件名框（避免与光标下项不一致）。
    /// </summary>
    private static string? TryResolvePickerItemPath(
        IntPtr viewHwnd,
        IntPtr dlgGuess,
        int screenX,
        int screenY,
        IReadOnlyList<IntPtr> candidates,
        bool allowShortcutResolution,
        bool allowCommDlgFilenameEditFallback = true)
    {
        if (allowShortcutResolution)
        {
            string? activeSelection = TryActiveFileDialogSelectionPath();
            if (!string.IsNullOrEmpty(activeSelection))
                return activeSelection;

            foreach (IntPtr hTry in candidates)
            {
                string? direct = TryCommDlgGetFilePath(hTry);
                if (string.IsNullOrEmpty(direct))
                    continue;
                direct = direct.Trim();
                try
                {
                    if (File.Exists(direct) || Directory.Exists(direct))
                        return NormalizeFsPath(Path.GetFullPath(direct));
                }
                catch
                {
                    /* ignore */
                }
            }
        }

        IntPtr commDlg = FindCommDlgHwnd(viewHwnd);
        if (commDlg == IntPtr.Zero)
            commDlg = dlgGuess;

        string? folder = null;
        foreach (IntPtr h in candidates)
        {
            string? f = TryCommDlgGetFolderPath(h);
            if (!string.IsNullOrEmpty(f))
            {
                folder = f;
                commDlg = h;
                break;
            }
        }

        bool allowSelectionFallback = allowShortcutResolution;
        string? name = TryListViewDisplayNameForContext(viewHwnd, screenX, screenY, allowSelectionFallback);
        if (allowCommDlgFilenameEditFallback)
        {
            if (string.IsNullOrEmpty(name) && commDlg != IntPtr.Zero)
                name = TryCommDlgGetSpec(commDlg);

            if (string.IsNullOrEmpty(name))
            {
                foreach (IntPtr h in candidates)
                {
                    if (!GetWindowClassName(h).Equals("#32770", StringComparison.Ordinal))
                        continue;
                    string? spec = TryCommDlgGetSpec(h);
                    if (!string.IsNullOrEmpty(spec))
                    {
                        name = spec;
                        commDlg = h;
                        break;
                    }
                }
            }
        }

        if (!string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(name))
        {
            name = name.Trim();
            if (name is "." or "..")
                return null;

            try
            {
                if (Path.IsPathRooted(name))
                    return NormalizeFsPath(Path.GetFullPath(name));
                return TryCombineFolderAndNameWithExistenceProbe(folder, name);
            }
            catch
            {
                return null;
            }
        }

        if (allowCommDlgFilenameEditFallback && string.IsNullOrEmpty(folder) && !string.IsNullOrEmpty(name))
        {
            string trimmedName = name.Trim();
            if (trimmedName is "." or "..")
                return null;

            foreach (IntPtr h in candidates)
            {
                if (!GetWindowClassName(h).Equals("#32770", StringComparison.Ordinal))
                    continue;
                string? fp = TryCommDlgGetFilePath(h);
                if (string.IsNullOrEmpty(fp))
                    continue;
                fp = fp.Trim();
                try
                {
                    if (File.Exists(fp) && string.Equals(Path.GetFileName(fp), trimmedName, StringComparison.OrdinalIgnoreCase))
                        return NormalizeFsPath(Path.GetFullPath(fp));
                }
                catch
                {
                    /* ignore */
                }
            }

            foreach (IntPtr h in candidates)
            {
                if (!GetWindowClassName(h).Equals("#32770", StringComparison.Ordinal))
                    continue;
                string? spec = TryCommDlgGetSpec(h);
                if (string.IsNullOrEmpty(spec))
                    continue;
                if (!string.Equals(spec.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase))
                    continue;

                string? f = TryCommDlgGetFolderPath(h);
                if (!string.IsNullOrEmpty(f))
                {
                    try
                    {
                        return NormalizeFsPath(Path.GetFullPath(Path.Combine(f.Trim(), trimmedName)));
                    }
                    catch
                    {
                        /* ignore */
                    }
                }

                string? fp2 = TryCommDlgGetFilePath(h);
                if (!string.IsNullOrEmpty(fp2))
                {
                    fp2 = fp2.Trim();
                    try
                    {
                        if (File.Exists(fp2))
                            return NormalizeFsPath(Path.GetFullPath(fp2));
                    }
                    catch
                    {
                        /* ignore */
                    }
                }
            }
        }

        return null;
    }

    private static bool IsShellHostClass(string cls) =>
        cls.Equals("WorkerW", StringComparison.OrdinalIgnoreCase)
        || cls.Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase)
        || cls.Equals("DirectUIHWND", StringComparison.OrdinalIgnoreCase)
        || cls.Equals("SysListView32", StringComparison.OrdinalIgnoreCase)
        || cls.Equals("NamespaceTreeControl", StringComparison.OrdinalIgnoreCase);

    /// <summary>有序、去重：优先父链与 <c>#32770</c> 子对话框，再补充常见 Shell 宿主窗口。</summary>
    private static List<IntPtr> BuildCommDlgCandidateList(IntPtr viewHwnd, IntPtr dlgGuess)
    {
        var list = new List<IntPtr>(64);
        var inList = new HashSet<IntPtr>();
        int n = 0;

        void TryAdd(IntPtr h)
        {
            if (n >= MaxCommDlgCandidates) return;
            if (h == IntPtr.Zero || !IsWindow(h)) return;
            if (!inList.Add(h)) return;
            list.Add(h);
            n++;
        }

        IntPtr cached = t_lastWorkingCdmHwnd;
        if (cached != IntPtr.Zero && IsWindow(cached))
            TryAdd(cached);

        TryAdd(dlgGuess);

        IntPtr w = viewHwnd;
        for (int i = 0; i < 40 && w != IntPtr.Zero; i++)
        {
            TryAdd(w);
            w = GetParent(w);
        }

        IntPtr root = GetAncestor(viewHwnd, GA_ROOT);
        TryAdd(root);

        var dialogs32770 = new List<IntPtr>(16);
        var shellHosts = new List<IntPtr>(32);
        var bfsSeen = new HashSet<IntPtr>();
        var q = new Queue<IntPtr>();
        if (root != IntPtr.Zero && IsWindow(root) && bfsSeen.Add(root))
            q.Enqueue(root);

        int visits = 0;
        while (q.Count > 0 && visits < MaxSubtreeBfsVisits)
        {
            IntPtr p = q.Dequeue();
            visits++;
            string pcls = GetWindowClassName(p);
            if (pcls.Equals("#32770", StringComparison.Ordinal))
                dialogs32770.Add(p);
            else if (IsShellHostClass(pcls))
                shellHosts.Add(p);

            IntPtr ch = FindWindowExW(p, IntPtr.Zero, null, null);
            while (ch != IntPtr.Zero && visits < MaxSubtreeBfsVisits)
            {
                if (bfsSeen.Add(ch))
                    q.Enqueue(ch);
                ch = FindWindowExW(p, ch, null, null);
            }
        }

        foreach (IntPtr h in dialogs32770)
            TryAdd(h);
        foreach (IntPtr h in shellHosts)
            TryAdd(h);

        return list;
    }

    private static string? NormalizeFsPath(string s)
    {
        s = s.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    private static bool LooksLikeReasonableFsPath(string s)
    {
        if (string.IsNullOrWhiteSpace(s))
            return false;
        s = s.Trim();
        return s.Length >= 2
            && (s.Contains(':', StringComparison.Ordinal)
                || s.StartsWith("\\\\", StringComparison.Ordinal)
                || s.Contains('\\')
                || s.Contains('/'));
    }

    private static string? TryCombineFolderAndName(string? folder, string? name)
    {
        if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(name))
            return null;
        name = name.Trim();
        if (name is "." or "..")
            return null;
        try
        {
            if (Path.IsPathRooted(name))
                return NormalizeFsPath(Path.GetFullPath(name));
            return NormalizeFsPath(Path.GetFullPath(Path.Combine(folder.Trim(), name)));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>合并 folder+显示名，并在资源管理器隐藏扩展名时补全同名的 <c>.lnk</c>。</summary>
    private static string? TryCombineFolderAndNameWithExistenceProbe(string? folder, string? name)
    {
        string? combined = TryCombineFolderAndName(folder, name);
        if (string.IsNullOrEmpty(combined))
            return null;
        try
        {
            if (File.Exists(combined) || Directory.Exists(combined))
                return NormalizeFsPath(Path.GetFullPath(combined));
            string lnkTry = combined + ".lnk";
            if (File.Exists(lnkTry))
                return NormalizeFsPath(Path.GetFullPath(lnkTry));
        }
        catch
        {
            /* ignore */
        }

        return combined;
    }

    private static bool MsaaRoleIsPrimaryListItem(int role) =>
        role == MSAA_ROLE_SYSTEM_LISTITEM
        || role == MSAA_ROLE_SYSTEM_OUTLINEITEM
        || role == MSAA_ROLE_SYSTEM_ROW;

    private static bool MsaaRoleIsPossibleDirectUiLabel(int role) =>
        role == MSAA_ROLE_SYSTEM_STATICTEXT || role == MSAA_ROLE_SYSTEM_TEXT;

    /// <summary>将 .lnk 解析为本地目标路径（用于复制「真实文件」路径）；失败则返回原字符串。</summary>
    private static string? TryDereferenceShellShortcutToTargetPath(string? pathOrLnk)
    {
        if (string.IsNullOrEmpty(pathOrLnk))
            return pathOrLnk;

        try
        {
            if (Directory.Exists(pathOrLnk))
                return pathOrLnk;
        }
        catch
        {
            /* ignore */
        }

        string work;
        try
        {
            work = Path.GetFullPath(pathOrLnk.Trim());
        }
        catch
        {
            return pathOrLnk;
        }

        try
        {
            if (File.Exists(work) && !work.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                return NormalizeFsPath(work);
        }
        catch
        {
            /* ignore */
        }

        if (!work.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                if (!File.Exists(work) && !Directory.Exists(work))
                {
                    string cand = work + ".lnk";
                    if (File.Exists(cand))
                        work = Path.GetFullPath(cand);
                    else
                        return pathOrLnk;
                }
                else
                {
                    return pathOrLnk;
                }
            }
            catch
            {
                return pathOrLnk;
            }
        }

        if (!work.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            return pathOrLnk;

        string? viaWsh = TryDereferenceShortcutTargetViaWScript(work);
        if (!string.IsNullOrEmpty(viaWsh))
            return viaWsh;

        string? viaCom = TryDereferenceShortcutTargetViaShellLinkCom(work);
        return !string.IsNullOrEmpty(viaCom) ? viaCom : pathOrLnk;
    }

    private static string? TryDereferenceShortcutTargetViaWScript(string pathOrLnk)
    {
        try
        {
            Type? t = Type.GetTypeFromProgID("WScript.Shell");
            if (t == null)
                return null;

            object? shObj = Activator.CreateInstance(t);
            if (shObj == null)
                return null;

            try
            {
                object? sc = shObj.GetType().InvokeMember(
                    "CreateShortcut",
                    BindingFlags.InvokeMethod,
                    null,
                    shObj,
                    new object[] { pathOrLnk });
                if (sc == null)
                    return null;

                try
                {
                    object? targetObj = sc.GetType().InvokeMember(
                        "TargetPath",
                        BindingFlags.GetProperty,
                        null,
                        sc,
                        null);
                    string? target = targetObj as string;
                    if (string.IsNullOrWhiteSpace(target))
                        return null;

                    target = target.Trim();
                    try
                    {
                        if (File.Exists(target) || Directory.Exists(target))
                            return NormalizeFsPath(Path.GetFullPath(target));
                    }
                    catch
                    {
                        /* ignore */
                    }

                    return LooksLikeReasonableFsPath(target) ? NormalizeFsPath(Path.GetFullPath(target)) : null;
                }
                finally
                {
                    try { Marshal.FinalReleaseComObject(sc); } catch { /* ignore */ }
                }
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(shObj); } catch { /* ignore */ }
            }
        }
        catch
        {
            return null;
        }
    }

    private static string? TryDereferenceShortcutTargetViaShellLinkCom(string lnkPath)
    {
        try
        {
            Type? t = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"));
            if (t == null)
                return null;

            object? unk = Activator.CreateInstance(t);
            if (unk == null)
                return null;

            try
            {
                var pf = (IPersistFileForShellLink)unk;
                if (pf.Load(lnkPath, 0) != 0)
                    return null;

                var sl = (IShellLinkWGetPath)unk;
                var sb = new StringBuilder(1024);
                if (sl.GetPath(sb, sb.Capacity, IntPtr.Zero, 0) != 0)
                    return null;

                string target = sb.ToString().Trim();
                if (string.IsNullOrEmpty(target))
                    return null;

                try
                {
                    if (File.Exists(target) || Directory.Exists(target))
                        return NormalizeFsPath(Path.GetFullPath(target));
                }
                catch
                {
                    /* ignore */
                }

                return LooksLikeReasonableFsPath(target) ? NormalizeFsPath(Path.GetFullPath(target)) : null;
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(unk); } catch { /* ignore */ }
            }
        }
        catch
        {
            return null;
        }
    }

    private static IntPtr FindShellDllDefViewAncestor(IntPtr viewHwnd)
    {
        IntPtr w = viewHwnd;
        for (int i = 0; i < 40 && w != IntPtr.Zero; i++)
        {
            if (GetWindowClassName(w).Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase))
                return w;
            w = GetParent(w);
        }

        return IntPtr.Zero;
    }

    private static IEnumerable<IntPtr> EnumerateDefViewsForBrowse(IntPtr dlg32770, IntPtr viewHwnd)
    {
        var yielded = new HashSet<IntPtr>();
        IntPtr near = FindShellDllDefViewAncestor(viewHwnd);
        if (near != IntPtr.Zero && yielded.Add(near))
            yield return near;

        foreach (IntPtr d in ListShellDllDefViewsOrdered(dlg32770))
        {
            if (yielded.Add(d))
                yield return d;
        }
    }

    private static string? TryCommDlgGetFilePath(IntPtr dlg)
    {
        if (dlg == IntPtr.Zero)
            return null;

        const int maxChars = 32768;
        var buf = new char[maxChars];
        GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            IntPtr p = pin.AddrOfPinnedObject();
            int len = (int)(nint)SendMessageW(dlg, CDM_GETFILEPATH, (IntPtr)maxChars, p);
            if (len <= 1)
                return null;

            int z = Array.IndexOf(buf, '\0');
            if (z >= 0 && z < len)
                len = z;

            ReadOnlySpan<char> span = buf.AsSpan(0, len);
            string s = new string(span).Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally
        {
            pin.Free();
        }
    }

    private static string? TryCommDlgGetFolderPath(IntPtr dlg)
    {
        if (dlg == IntPtr.Zero)
            return null;

        const int maxChars = 32768;
        var buf = new char[maxChars];
        GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            IntPtr p = pin.AddrOfPinnedObject();
            int len = (int)(nint)SendMessageW(dlg, CDM_GETFOLDERPATH, (IntPtr)maxChars, p);
            if (len <= 1)
                return null;

            int z = Array.IndexOf(buf, '\0');
            if (z >= 0 && z < len)
                len = z;

            string s = new string(buf.AsSpan(0, len)).Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally
        {
            pin.Free();
        }
    }

    private static string? TryCommDlgGetSpec(IntPtr dlg)
    {
        if (dlg == IntPtr.Zero)
            return null;

        const int maxChars = 32768;
        var buf = new char[maxChars];
        GCHandle pin = GCHandle.Alloc(buf, GCHandleType.Pinned);
        try
        {
            IntPtr p = pin.AddrOfPinnedObject();
            int len = (int)(nint)SendMessageW(dlg, CDM_GETSPEC, (IntPtr)maxChars, p);
            if (len <= 1)
                return null;

            int z = Array.IndexOf(buf, '\0');
            if (z >= 0 && z < len)
                len = z;

            string s = new string(buf.AsSpan(0, len)).Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally
        {
            pin.Free();
        }
    }

    private static IntPtr ResolveSysListView(IntPtr viewHwnd)
    {
        string cls = GetWindowClassName(viewHwnd);
        if (cls.Equals("SysListView32", StringComparison.OrdinalIgnoreCase))
            return viewHwnd;
        if (cls.Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase))
            return FindWindowExW(viewHwnd, IntPtr.Zero, "SysListView32", null);
        return IntPtr.Zero;
    }

    private static string? TryListViewDisplayNameForContext(IntPtr viewHwnd, int screenX, int screenY, bool allowSelectionFallback = true)
    {
        IntPtr lv = ResolveSysListView(viewHwnd);
        if (lv == IntPtr.Zero || !IsWindow(lv))
            return null;

        string? hit = TryListViewItemAtScreenPoint(lv, screenX, screenY);
        if (!string.IsNullOrEmpty(hit))
            return hit;

        if (!allowSelectionFallback)
            return null;

        int idx = (int)(nint)SendMessageW(lv, (uint)LVM_GETNEXTITEM, (IntPtr)(-1), (IntPtr)(LVNI_SELECTED | LVNI_FOCUSED));
        if (idx < 0)
            idx = (int)(nint)SendMessageW(lv, (uint)LVM_GETNEXTITEM, (IntPtr)(-1), (IntPtr)LVNI_SELECTED);
        if (idx < 0)
            return null;

        return TryListViewItemText(lv, idx);
    }

    private static string? TryListViewItemAtScreenPoint(IntPtr lv, int screenX, int screenY)
    {
        var pt = new POINT { x = screenX, y = screenY };
        if (!ScreenToClient(lv, ref pt))
            return null;

        var hti = new LVHITTESTINFO
        {
            pt = pt,
            iItem = -1,
            iSubItem = 0,
        };

        _ = (int)(nint)SendMessageW(lv, (uint)LVM_SUBITEMHITTEST, (IntPtr)(-1), ref hti);
        if (hti.iItem < 0)
            return null;

        return TryListViewItemText(lv, hti.iItem);
    }

    /// <summary>从物理屏幕点 (<paramref name="screenX"/>, <paramref name="screenY"/>) 向上找 <c>SysListView32</c>（commdlg 下祖先 HWND 可能不是列表本身）。</summary>
    private static string? TrySysListViewNameFromPhysicalScreenPoint(int screenX, int screenY)
    {
        var ppt = new POINT { x = screenX, y = screenY };
        IntPtr h = WindowFromPhysicalPoint(ppt);
        for (int i = 0; i < 48 && h != IntPtr.Zero; i++)
        {
            if (!IsWindow(h))
                break;

            if (GetWindowClassName(h).Equals("SysListView32", StringComparison.OrdinalIgnoreCase))
            {
                string? t = TryListViewItemAtScreenPoint(h, screenX, screenY);
                if (!string.IsNullOrEmpty(t))
                    return t;
            }

            h = GetParent(h);
        }

        return null;
    }

    private static string? TryListViewItemText(IntPtr lv, int idx)
    {
        var textBuf = new char[1024];
        GCHandle th = GCHandle.Alloc(textBuf, GCHandleType.Pinned);
        try
        {
            var item = new LVITEMW
            {
                mask = LVIF_TEXT,
                iItem = idx,
                iSubItem = 0,
                pszText = th.AddrOfPinnedObject(),
                cchTextMax = textBuf.Length,
            };

            int got = (int)(nint)SendMessageW(lv, (uint)LVM_GETITEMTEXT, (IntPtr)idx, ref item);
            if (got <= 0)
                return null;

            int z = Array.IndexOf(textBuf, '\0');
            int len = z >= 0 ? z : Math.Min(got, textBuf.Length);
            string s = new string(textBuf.AsSpan(0, len)).Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally
        {
            th.Free();
        }
    }

    private static int? TryMsaaRoleFromVariant(object? o)
    {
        if (o == null)
            return null;
        try
        {
            return o switch
            {
                int i => i,
                uint ui => unchecked((int)ui),
                short sh => sh,
                ushort ush => ush,
                long l => checked((int)l),
                _ => Convert.ToInt32(o),
            };
        }
        catch
        {
            return null;
        }
    }

    private static int? TryInvokeAccRole(object acc, object childId)
    {
        try
        {
            object? r = acc.GetType().InvokeMember(
                "get_accRole",
                BindingFlags.InvokeMethod,
                null,
                acc,
                new[] { childId });
            return TryMsaaRoleFromVariant(r);
        }
        catch
        {
            return null;
        }
    }

    private static string? TryInvokeAccName(object acc, object childId)
    {
        try
        {
            object? n = acc.GetType().InvokeMember(
                "get_accName",
                BindingFlags.InvokeMethod,
                null,
                acc,
                new[] { childId });
            return n as string;
        }
        catch
        {
            return null;
        }
    }

    private static object? TryInvokeAccParent(object acc)
    {
        try
        {
            return acc.GetType().InvokeMember(
                "get_accParent",
                BindingFlags.InvokeMethod,
                null,
                acc,
                null);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsReasonableMsaaItemDisplayName(string t)
    {
        if (string.IsNullOrWhiteSpace(t))
            return false;
        t = t.Trim();
        if (t.Length == 0 || t.Length > 512)
            return false;
        if (t is "." or "..")
            return false;
        return true;
    }

    private static string? TryResolveMsaaListItemNameAtScreenPoint(int screenX, int screenY)
    {
        object? cur = null;
        try
        {
            var pt = new OLEACC_POINT { X = screenX, Y = screenY };
            if (AccessibleObjectFromPoint(ref pt, out object? accObj, out object childVar) != 0 || accObj == null)
                return null;

            cur = accObj;
            object walkChild = childVar;
            for (int depth = 0; depth < 28 && cur != null; depth++)
            {
                int? role = TryInvokeAccRole(cur, walkChild);
                string? name = TryInvokeAccName(cur, walkChild);
                if (role.HasValue && !string.IsNullOrWhiteSpace(name) && IsReasonableMsaaItemDisplayName(name))
                {
                    string trimmed = name.Trim();
                    if (MsaaRoleIsPrimaryListItem(role.Value))
                        return trimmed;

                    // DirectUI 列表项常以 StaticText/Text 暴露显示名（无 LISTITEM 角色）
                    if (depth <= 14
                        && MsaaRoleIsPossibleDirectUiLabel(role.Value)
                        && !trimmed.Contains('\\')
                        && !trimmed.Contains('/')
                        && trimmed.IndexOf(':') < 0)
                        return trimmed;
                }

                object? parent = TryInvokeAccParent(cur);
                try
                {
                    try { Marshal.FinalReleaseComObject(cur); } catch { /* ignore */ }
                }
                catch
                {
                    /* ignore */
                }

                cur = parent;
                walkChild = 0;
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (cur != null)
            {
                try { Marshal.FinalReleaseComObject(cur); } catch { /* ignore */ }
            }
        }
    }

    private static string? TryOleaccDisplayNameAtScreenPoint(int screenX, int screenY)
    {
        try
        {
            var pt = new OLEACC_POINT { X = screenX, Y = screenY };
            if (AccessibleObjectFromPoint(ref pt, out object? accObj, out object childVar) != 0 || accObj == null)
                return null;

            try
            {
                object? n = accObj.GetType().InvokeMember(
                    "get_accName",
                    BindingFlags.InvokeMethod,
                    null,
                    accObj,
                    new[] { childVar });
                if (n is not string s || string.IsNullOrWhiteSpace(s))
                    return null;
                s = s.Trim();
                return s.Length == 0 ? null : s;
            }
            finally
            {
                try { Marshal.FinalReleaseComObject(accObj); } catch { /* ignore */ }
            }
        }
        catch
        {
            return null;
        }
    }

    private static bool UiaControlTypeIsListLikeRow(int controlTypeId) =>
        controlTypeId == 50007
        || controlTypeId == 50024
        || controlTypeId == 50025
        || controlTypeId == 50026
        || controlTypeId == 50029
        || controlTypeId == 50034;

    private static bool UiaControlTypeIsFuzzyChrome(int controlTypeId) =>
        controlTypeId == 50000
        || controlTypeId == 50004
        || controlTypeId == 50019
        || controlTypeId == 50021
        || controlTypeId == 50032
        || controlTypeId == 50037;

    private static int UiaTryGetCurrentControlTypeId(object element)
    {
        try
        {
            object? current = element.GetType().InvokeMember(
                "Current",
                BindingFlags.GetProperty,
                null,
                element,
                null);
            if (current == null)
                return 0;

            object? ct = current.GetType().InvokeMember(
                "ControlType",
                BindingFlags.GetProperty,
                null,
                current,
                null);
            if (ct == null)
                return 0;

            object? idObj = ct.GetType().InvokeMember(
                "Id",
                BindingFlags.GetProperty,
                null,
                ct,
                null);
            return idObj == null ? 0 : Convert.ToInt32(idObj);
        }
        catch
        {
            return 0;
        }
    }

    private static string? UiaTryGetCurrentName(object element)
    {
        try
        {
            object? current = element.GetType().InvokeMember(
                "Current",
                BindingFlags.GetProperty,
                null,
                element,
                null);
            if (current == null)
                return null;

            object? n = current.GetType().InvokeMember(
                "Name",
                BindingFlags.GetProperty,
                null,
                current,
                null);
            return n as string;
        }
        catch
        {
            return null;
        }
    }

    private static object? UiaTryGetParentElement(object automation, object walker, object element)
    {
        try
        {
            return walker.GetType().InvokeMember(
                "GetParentElement",
                BindingFlags.InvokeMethod,
                null,
                walker,
                new[] { element });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Win10/11 文件对话框 DirectUI 列表：用 UI Automation（与 MSAA 分离）按点取 ListItem/DataItem 名。</summary>
    private static string? TryUiaResolveItemDisplayNameAtScreenPoint(int screenX, int screenY)
    {
        object? automation = null;
        try
        {
            Type? autoType = Type.GetTypeFromCLSID(new Guid("FF48DBA4-60EF-4201-8307-8672900D8767"));
            if (autoType == null)
                return null;

            automation = Activator.CreateInstance(autoType);
            if (automation == null)
                return null;

            object? walker = automation.GetType().InvokeMember(
                "RawViewWalker",
                BindingFlags.GetProperty | BindingFlags.Public | BindingFlags.Instance,
                null,
                automation,
                null);

            var pt = new UIA_TAGPOINT { X = screenX, Y = screenY };
            object? walk = automation.GetType().InvokeMember(
                "ElementFromPoint",
                BindingFlags.InvokeMethod | BindingFlags.Public | BindingFlags.Instance,
                null,
                automation,
                new object[] { pt });

            if (walk == null || walker == null)
                return null;

            string? fuzzyBest = null;
            int fuzzyBestDepth = -1;

            try
            {
                for (int depth = 0; depth < 48 && walk != null; depth++)
                {
                    int ctId = UiaTryGetCurrentControlTypeId(walk);
                    string? name = UiaTryGetCurrentName(walk);
                    if (!string.IsNullOrWhiteSpace(name) && IsReasonableMsaaItemDisplayName(name))
                    {
                        string t = name.Trim();
                        bool looksPath = LooksLikeReasonableFsPath(t);
                        bool existsPath = looksPath && (File.Exists(t) || Directory.Exists(t));

                        if (UiaControlTypeIsListLikeRow(ctId))
                        {
                            if (!looksPath || !existsPath)
                            {
                                try { Marshal.FinalReleaseComObject(walk); } catch { /* ignore */ }
                                walk = null;
                                return t;
                            }

                            if (existsPath)
                            {
                                try { Marshal.FinalReleaseComObject(walk); } catch { /* ignore */ }
                                walk = null;
                                return t;
                            }
                        }

                        if (!looksPath
                            && !t.Contains('\\')
                            && t.IndexOf(':') < 0
                            && t.Length <= 260
                            && depth >= 0
                            && depth <= 28
                            && !UiaControlTypeIsFuzzyChrome(ctId))
                        {
                            if (depth > fuzzyBestDepth)
                            {
                                fuzzyBest = t;
                                fuzzyBestDepth = depth;
                            }
                        }
                    }

                    object? parent = UiaTryGetParentElement(automation, walker, walk);
                    try { Marshal.FinalReleaseComObject(walk); } catch { /* ignore */ }
                    walk = parent;
                }
            }
            finally
            {
                if (walk != null)
                {
                    try { Marshal.FinalReleaseComObject(walk); } catch { /* ignore */ }
                }
            }

            return fuzzyBest;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (automation != null)
            {
                try { Marshal.FinalReleaseComObject(automation); } catch { /* ignore */ }
            }
        }
    }

    private static string? TryOleaccShortLeafNameAtScreenPoint(int screenX, int screenY)
    {
        string? leaf = TryOleaccDisplayNameAtScreenPoint(screenX, screenY);
        if (string.IsNullOrEmpty(leaf))
            return null;
        leaf = leaf.Trim();
        if (!IsReasonableMsaaItemDisplayName(leaf))
            return null;
        if (leaf.Contains('\\') || leaf.Contains('/') || leaf.IndexOf(':') >= 0)
            return null;
        return leaf;
    }

    private static void TryResolveHitItemUnderCursor(
        IntPtr viewHwnd,
        string? parentFolder,
        int screenX,
        int screenY,
        out string? hitPath,
        out string? hitName)
    {
        hitPath = null;
        hitName = null;

        string? name = TrySysListViewNameFromPhysicalScreenPoint(screenX, screenY);
        if (string.IsNullOrEmpty(name))
            name = TryUiaResolveItemDisplayNameAtScreenPoint(screenX, screenY);
        if (string.IsNullOrEmpty(name))
            name = TryResolveMsaaListItemNameAtScreenPoint(screenX, screenY);
        if (string.IsNullOrEmpty(name))
            name = TryListViewDisplayNameForContext(viewHwnd, screenX, screenY, allowSelectionFallback: false);
        if (string.IsNullOrEmpty(name))
            name = TryOleaccShortLeafNameAtScreenPoint(screenX, screenY);
        if (string.IsNullOrEmpty(name))
            return;

        hitName = name.Trim();
        if (string.IsNullOrEmpty(hitName))
            return;

        try
        {
            if (LooksLikeReasonableFsPath(hitName) && (File.Exists(hitName) || Directory.Exists(hitName)))
            {
                hitPath = NormalizeFsPath(Path.GetFullPath(hitName));
                return;
            }
        }
        catch
        {
            /* ignore */
        }

        if (!string.IsNullOrEmpty(parentFolder))
        {
            string? merged = TryCombineFolderAndNameWithExistenceProbe(parentFolder, hitName);
            if (!string.IsNullOrEmpty(merged))
                hitPath = merged;
        }
    }

    private static void SetClipboardUnicode(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;

        if (!OpenClipboard(IntPtr.Zero))
            return;

        try
        {
            if (!EmptyClipboard())
                return;

            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            IntPtr hg = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)bytes.Length);
            if (hg == IntPtr.Zero)
                return;

            IntPtr p = GlobalLock(hg);
            if (p == IntPtr.Zero)
            {
                GlobalFree(hg);
                return;
            }

            try
            {
                Marshal.Copy(bytes, 0, p, bytes.Length);
            }
            finally
            {
                GlobalUnlock(hg);
            }

            if (SetClipboardData(CF_UNICODETEXT, hg) == IntPtr.Zero)
                GlobalFree(hg);
        }
        finally
        {
            try { CloseClipboard(); } catch { /* ignore */ }
        }
    }

    private static void TryOpenInExplorerSelect(string path)
    {
        try
        {
            string explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            string arg = "/select,\"" + path.Replace("\"", "\\\"") + "\"";
            using var _ = Process.Start(new ProcessStartInfo
            {
                FileName = explorer,
                Arguments = arg,
                UseShellExecute = false,
            });
        }
        catch
        {
            /* ignore */
        }
    }

    private static void PostRefreshList(IntPtr viewHwnd)
    {
        IntPtr target = viewHwnd;
        var buf = new char[256];
        int n = GetClassNameW(viewHwnd, buf, buf.Length);
        if (n > 0 && new string(buf.AsSpan(0, n)).Equals("SHELLDLL_DefView", StringComparison.OrdinalIgnoreCase))
        {
            IntPtr lv = FindWindowExW(viewHwnd, IntPtr.Zero, "SysListView32", null);
            if (lv != IntPtr.Zero)
                target = lv;
        }

        if (target == IntPtr.Zero || !IsWindow(target))
            return;

        PostMessageW(target, WM_KEYDOWN, (IntPtr)VK_F5, IntPtr.Zero);
        PostMessageW(target, WM_KEYUP, (IntPtr)VK_F5, IntPtr.Zero);
    }

#if DEBUG
    private static readonly object s_diagLogLock = new();
    private const string DiagLogFileName = "lunagal-filedialog-rmb.log";
    private const int DiagMaxFileBytes = 200_000;

    private static void LogFileDialogRmbDiagnostic(
        IntPtr viewHwnd,
        IntPtr dlgGuess,
        IReadOnlyList<IntPtr> candidates,
        string? path,
        string? folder,
        int screenX,
        int screenY,
        string? hitName)
    {
        try
        {
            bool deep = ShouldDiagDeepLogFile();
            var sb = new StringBuilder(6144);
            sb.AppendLine($"==== {DateTime.Now:O} tid={GetCurrentThreadId()} ====");
            sb.AppendLine(
                $"view=0x{viewHwnd.ToInt64():X} cls={DiagOneLine(GetWindowClassName(viewHwnd))} pt={screenX},{screenY}");
            sb.AppendLine(
                $"dlgGuess=0x{dlgGuess.ToInt64():X} path={DiagOneLine(path)} folder={DiagOneLine(folder)} cand={candidates.Count}");
            sb.AppendLine($"hitUnderCursor={DiagOneLine(hitName)} deepDiag={(deep ? 1 : 0)}");
            sb.AppendLine(
                $"lastWorkingCdm=0x{t_lastWorkingCdmHwnd.ToInt64():X} valid={(t_lastWorkingCdmHwnd != IntPtr.Zero && IsWindow(t_lastWorkingCdmHwnd))}");

            sb.AppendLine("parentChain:");
            IntPtr pw = viewHwnd;
            for (int i = 0; i < 40 && pw != IntPtr.Zero; i++)
            {
                sb.AppendLine($"  [{i}] 0x{pw.ToInt64():X} {DiagOneLine(GetWindowClassName(pw))}");
                pw = GetParent(pw);
            }

            IntPtr root = GetAncestor(viewHwnd, GA_ROOT);
            sb.AppendLine($"root=0x{root.ToInt64():X} {DiagOneLine(GetWindowClassName(root))}");

            int maxProbe = Math.Min(candidates.Count, 45);
            if (deep)
            {
                for (int i = 0; i < maxProbe; i++)
                {
                    IntPtr h = candidates[i];
                    string cls = GetWindowClassName(h);
                    string? fp = TryCommDlgGetFilePath(h);
                    string? fo = TryCommDlgGetFolderPath(h);
                    string? sp = TryCommDlgGetSpec(h);
                    sb.AppendLine(
                        $"cand[{i}] 0x{h.ToInt64():X} cls={DiagOneLine(cls)} GETFILEPATH len={LenOrZero(fp)}{DiagTail(fp)} GETFOLDERPATH len={LenOrZero(fo)}{DiagTail(fo)} GETSPEC len={LenOrZero(sp)}{DiagTail(sp)}");
                }
            }
            else
            {
                for (int i = 0; i < maxProbe; i++)
                {
                    IntPtr h = candidates[i];
                    sb.AppendLine($"cand[{i}] 0x{h.ToInt64():X} cls={DiagOneLine(GetWindowClassName(h))}");
                }
                sb.AppendLine("cand CDM probes skipped (set LUNAGAL_FILEDIALOG_RMB_DEEP_LOG=1 to enable)");
            }

            sb.AppendLine();

            lock (s_diagLogLock)
            {
                string fullPath = Path.Combine(Path.GetTempPath(), DiagLogFileName);
                File.AppendAllText(fullPath, sb.ToString(), Encoding.UTF8);
                TrimDiagLogFileIfNeeded(fullPath);
            }
        }
        catch
        {
            /* ignore */
        }
    }

    private static int LenOrZero(string? s) => string.IsNullOrEmpty(s) ? 0 : s.Trim().Length;

    private static string DiagTail(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        string t = s.Trim().Replace('\r', ' ').Replace('\n', ' ');
        return t.Length <= 96 ? $" \"{t}\"" : $" \"{t.Substring(0, 93)}...\"";
    }

    private static string DiagOneLine(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "(null)";
        string t = s.Replace('\r', ' ').Replace('\n', ' ');
        return t.Length <= 160 ? t : t.Substring(0, 157) + "...";
    }

    private static void TrimDiagLogFileIfNeeded(string fullPath)
    {
        try
        {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists || fi.Length <= DiagMaxFileBytes)
                return;

            string all = File.ReadAllText(fullPath, Encoding.UTF8);
            int keepFrom = Math.Max(0, all.Length - DiagMaxFileBytes / 2);
            int nl = all.IndexOf('\n', keepFrom);
            if (nl >= 0 && nl < all.Length - 1)
                keepFrom = nl + 1;
            string tail = all[keepFrom..];
            File.WriteAllText(fullPath, "...(truncated)...\n" + tail, Encoding.UTF8);
        }
        catch
        {
            /* ignore */
        }
    }
#endif

    private sealed class HookHandle : IDisposable
    {
        private IntPtr _hook;

        public HookHandle(IntPtr hook) => _hook = hook;

        public void Dispose()
        {
            lock (s_subLock)
            {
                foreach (IntPtr hwnd in s_subclassed)
                {
                    if (IsWindow(hwnd))
                    {
                        try { RemoveWindowSubclass(hwnd, s_subclassProc, SubclassId); } catch { /* ignore */ }
                    }
                }

                s_subclassed.Clear();
            }

            ClearWorkingCdmHwnd();

            if (_hook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_hook); } catch { /* ignore */ }
                _hook = IntPtr.Zero;
            }
        }
    }

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();
        public void Dispose() { }
    }
}
