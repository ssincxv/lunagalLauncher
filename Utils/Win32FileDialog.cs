using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using lunagalLauncher.Helpers;
using lunagalLauncher.Services;
using Serilog;

namespace lunagalLauncher.Utils
{
    public enum OpenFilePickerCompletion
    {
        Success,
        Cancelled,
        HelperExitedAbnormally,
        Unavailable,
    }

    /// <param name="Path"></param>

    /// <param name="Completion"></param>    /// <param name="HelperExitCode">子进程退出码；未能启动子进程或未退出时为 null。</param>
    public readonly record struct OpenFilePickerResult(string? Path, OpenFilePickerCompletion Completion, int? HelperExitCode);

    /// <summary>
    /// 打开/保存文件对话框：优先 Vista+ <c>IFileOpenDialog</c> / <c>IFileSaveDialog</c>（在独立 STA 线程上 CoCreate+Show），
    /// 失败或 <c>Show</c> 异常时回退到带 <c>OFN_EXPLORER</c> 的 <c>GetOpenFileNameW</c> / <c>GetSaveFileNameW</c>（Explorer 外壳，非 Win3.x 遗留样式）。
    ///
    /// <para>
    /// WinUI 3 UI 为 ASTA，主线程 <c>CoCreateInstance(FileOpenDialog)</c> 会失败；故在
    /// <see cref="RunOnStaThread{T}"/> 上跑 COM。另：空 <c>SetFileTypes(0, …)</c>、仅 INPROC 的 CLSCTX、
    /// 以及从 STA 对 WinUI 父窗 <c>Show</c> 均可能导致对话框不弹出；本实现已做过滤保底、CLSCTX 重试、
    /// owner/无 owner 二次尝试与 commdlg 回退。
    /// </para>
    /// </summary>
    public static class Win32FileDialog
    {
        #region IFileDialog 常量

        private const uint FOS_OVERWRITEPROMPT = 0x00000002;
        private const uint FOS_NOCHANGEDIR = 0x00000008;
        private const uint FOS_DONTADDTORECENT = 0x02000000;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const uint FOS_FILEMUSTEXIST = 0x00001000;
        /// <summary>浏览时不解析 .lnk 目标，减轻多余 I/O。</summary>
        private const uint FOS_NODEREFERENCELINKS = 0x00100000;
        /// <summary>强制关闭预览窗格，避免预览管线拖慢列表与点击。</summary>
        private const uint FOS_FORCEPREVIEWPANE_OFF = 0x40000000;
        /// <summary>隐藏导航窗格中的固定（已固定）位置，略减 Shell 侧开销。</summary>
        private const uint FOS_HIDEPINNEDPLACES = 0x20000000;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const int S_OK = 0;
        private const int HRESULT_ERROR_CANCELLED = unchecked((int)0x800704C7);
        private const uint CLSCTX_INPROC_SERVER = 0x1;
        private const uint CLSCTX_ALL = 0x17;

        #endregion

        #region GetOpenFileName / GetSaveFileName（OFN_EXPLORER）

        private const int OFN_PATHMUSTEXIST = 0x00000800;
        private const int OFN_FILEMUSTEXIST = 0x00001000;
        private const int OFN_NOCHANGEDIR = 0x00000008;
        private const int OFN_DONTADDTORECENT = 0x02000000;
        private const int OFN_LONGNAMES = 0x00200000;
        private const int OFN_OVERWRITEPROMPT = 0x00000002;
        /// <summary>Explorer 外壳；缺少此标志时为 Win3.x 老样式，易在 WinUI/高完整性下出问题。</summary>
        private const int OFN_EXPLORER = 0x00080000;
        private const int OFN_ENABLESIZING = 0x00800000;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct OPENFILENAMEW
        {
            public int lStructSize;
            public IntPtr hwndOwner;
            public IntPtr hInstance;
            public string lpstrFilter;
            public string lpstrCustomFilter;
            public int nMaxCustFilter;
            public int nFilterIndex;
            public IntPtr lpstrFile;
            public int nMaxFile;
            public string lpstrFileTitle;
            public int nMaxFileTitle;
            public string lpstrInitialDir;
            public string lpstrTitle;
            public int Flags;
            public short nFileOffset;
            public short nFileExtension;
            public string? lpstrDefExt;
            public IntPtr lCustData;
            public IntPtr lpfnHook;
            public string lpTemplateName;
            public IntPtr pvReserved;
            public int dwReserved;
            public int FlagsEx;
        }

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

        [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        private static extern bool GetSaveFileNameW(ref OPENFILENAMEW ofn);

        [DllImport("comdlg32.dll", ExactSpelling = true)]
        private static extern int CommDlgExtendedError();

        #endregion

        #region COM interop

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct COMDLG_FILTERSPEC
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string pszName;
            [MarshalAs(UnmanagedType.LPWStr)] public string pszSpec;
        }

        [ComImport]
        [Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            [PreserveSig] int BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            [PreserveSig] int GetParent(out IShellItem ppsi);
            [PreserveSig] int GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            [PreserveSig] int Compare(IShellItem psi, uint hint, out int piOrder);
        }

        [ComImport]
        [Guid("42f85136-db7e-439c-85fb-4201c6db9ee0")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr hwndOwner);
            [PreserveSig] int SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
            [PreserveSig] int SetFileTypeIndex(uint iFileType);
            [PreserveSig] int GetFileTypeIndex(out uint piFileType);
            [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
            [PreserveSig] int Unadvise(uint dwCookie);
            [PreserveSig] int SetOptions(uint fos);
            [PreserveSig] int GetOptions(out uint pfos);
            [PreserveSig] int SetDefaultFolder(IShellItem psi);
            [PreserveSig] int SetFolder(IShellItem psi);
            [PreserveSig] int GetFolder(out IShellItem ppsi);
            [PreserveSig] int GetCurrentSelection(out IShellItem ppsi);
            [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            [PreserveSig] int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            [PreserveSig] int GetResult(out IShellItem ppsi);
            [PreserveSig] int AddPlace(IShellItem psi, int fdap);
            [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            [PreserveSig] int Close(int hr);
            [PreserveSig] int SetClientGuid(ref Guid guid);
            [PreserveSig] int ClearClientData();
            [PreserveSig] int SetFilter(IntPtr pFilter);
        }

        [ComImport]
        [Guid("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileSaveDialog
        {
            [PreserveSig] int Show(IntPtr hwndOwner);
            [PreserveSig] int SetFileTypes(uint cFileTypes, [MarshalAs(UnmanagedType.LPArray)] COMDLG_FILTERSPEC[] rgFilterSpec);
            [PreserveSig] int SetFileTypeIndex(uint iFileType);
            [PreserveSig] int GetFileTypeIndex(out uint piFileType);
            [PreserveSig] int Advise(IntPtr pfde, out uint pdwCookie);
            [PreserveSig] int Unadvise(uint dwCookie);
            [PreserveSig] int SetOptions(uint fos);
            [PreserveSig] int GetOptions(out uint pfos);
            [PreserveSig] int SetDefaultFolder(IShellItem psi);
            [PreserveSig] int SetFolder(IShellItem psi);
            [PreserveSig] int GetFolder(out IShellItem ppsi);
            [PreserveSig] int GetCurrentSelection(out IShellItem ppsi);
            [PreserveSig] int SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            [PreserveSig] int GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            [PreserveSig] int SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            [PreserveSig] int SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            [PreserveSig] int SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            [PreserveSig] int GetResult(out IShellItem ppsi);
            [PreserveSig] int AddPlace(IShellItem psi, int fdap);
            [PreserveSig] int SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            [PreserveSig] int Close(int hr);
            [PreserveSig] int SetClientGuid(ref Guid guid);
            [PreserveSig] int ClearClientData();
            [PreserveSig] int SetFilter(IntPtr pFilter);
            [PreserveSig] int SetSaveAsItem(IShellItem psi);
            [PreserveSig] int SetProperties(IntPtr pStore);
            [PreserveSig] int SetCollectedProperties(IntPtr pList, [MarshalAs(UnmanagedType.Bool)] bool fAppendDefault);
            [PreserveSig] int GetProperties(out IntPtr ppStore);
            [PreserveSig] int ApplyProperties(IShellItem psi, IntPtr pStore, IntPtr hwnd, IntPtr pSink);
        }

        [DllImport("ole32.dll", PreserveSig = false)]
        private static extern void CoCreateInstance(
            ref Guid rclsid, IntPtr pUnkOuter, uint dwClsContext,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

        /// <summary>commdlg OFN_EXPLORER 右键/IContextMenu/拖放依赖 OLE；仅依赖 CLR 隐式 CoInitialize 不足。</summary>
        [DllImport("ole32.dll", PreserveSig = true)]
        private static extern int OleInitialize(IntPtr pvReserved);

        [DllImport("ole32.dll", PreserveSig = true)]
        private static extern void OleUninitialize();

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string lpLibFileName);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static readonly Guid CLSID_FileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
        private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-88D5-4336-A45D-742C11672812");
        private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        private static readonly Guid IID_IFileDialog = new("42f85136-db7e-439c-85fb-4201c6db9ee0");
        private static readonly Guid IID_IFileSaveDialog = new("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB");

        #endregion

        #region 字段

        private static readonly object _fileDialogPrewarmLock = new();
        private static Task? _fileDialogPrewarmTask;

        /// <summary>常驻 STA：单次 OleInitialize，复用 Shell/COM 状态。</summary>
        private static readonly object _staInitLock = new();
        private static Thread? _staWorkerThread;
        private static readonly ManualResetEventSlim _staOleReady = new(false);
        private static readonly object _staWorkLock = new();
        private static readonly AutoResetEvent _staHasWork = new(false);
        private static Action? _staWork;

        #endregion

        #region 公开 API

        public static async Task<string?> ShowOpenFileDialogAsync(
            IntPtr hwndOwner, string filter, string title, string? initialDirectory = null)
        {
            await EnsureFileDialogPrewarmedAsync();

            using var _isolate = MouseMappingEngine.EnterFileDialogHookIsolation();
            await Task.Yield();

            IntPtr owner = NormalizeOwner(hwndOwner);
            if (owner != IntPtr.Zero)
            {
                try { SetForegroundWindow(owner); } catch { }
            }

            return await RunOnStaThread(() => OpenDialogOnSta(owner, filter, title, initialDirectory));
        }

        public static async Task<string?> ShowOpenFileDialogForMainWindowAsync(
            string filter, string title, string? initialDirectory = null) =>
            (await ShowOpenFileDialogForMainWindowWithResultAsync(filter, title, initialDirectory)).Path;

        public static async Task<OpenFilePickerResult> ShowOpenFileDialogForMainWindowWithResultAsync(
            string filter, string title, string? initialDirectory = null)
        {
            try
            {
                if (!App.TryGetMainWindowHandle(out _))
                    Log.Warning("无法获取主窗口句柄，仍将使用 file-picker 子进程（不依赖 owner）");

                return await ShowOpenFileDialogViaHelperWithResultAsync(filter, title, initialDirectory, useCommDlg: true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "文件选择失败: {Message}", ex.Message);
                return new OpenFilePickerResult(null, OpenFilePickerCompletion.Unavailable, null);
            }
        }

        /// <summary>
        /// 仅使用带 <c>OFN_EXPLORER</c> 的 <c>GetOpenFileNameW</c>，不经过 <c>IFileOpenDialog</c>。
        /// 用于 WinUI 全窗叠层等场景下 Common Item Dialog 与 Shell 右键菜单易崩溃的情况。
        /// </summary>
        public static async Task<string?> ShowOpenFileDialogCommDlgExplorerAsync(
            IntPtr hwndOwner, string filter, string title, string? initialDirectory = null)
        {
            await EnsureFileDialogPrewarmedAsync();

            using var _isolate = MouseMappingEngine.EnterFileDialogHookIsolation();
            await Task.Yield();

            IntPtr owner = NormalizeOwner(hwndOwner);
            if (owner != IntPtr.Zero)
            {
                try { SetForegroundWindow(owner); } catch { }
            }

            return await RunOnStaThread(() =>
                GetOpenFileNameExplorer(owner, filter, title, initialDirectory));
        }

        /// <summary>
        /// 通过同一 exe 的 <c>--file-picker</c> 子进程打开「打开文件」对话框，将 Shell 右键扩展崩溃与主 WinUI 进程隔离。
        /// 子进程异常时<strong>不回退</strong>本进程 commdlg：否则扩展仍会载入主进程，下一次右键即可拖垮整个应用。
        /// </summary>
        public static async Task<string?> ShowOpenFileDialogViaHelperAsync(
            string filter, string title, string? initialDirectory, bool useCommDlg = true) =>
            (await ShowOpenFileDialogViaHelperWithResultAsync(filter, title, initialDirectory, useCommDlg)).Path;

        public static async Task<OpenFilePickerResult> ShowOpenFileDialogViaHelperWithResultAsync(
            string filter, string title, string? initialDirectory, bool useCommDlg = true)
        {
            await EnsureFileDialogPrewarmedAsync();

            using var _isolate = MouseMappingEngine.EnterFileDialogHookIsolation();
            await Task.Yield();

            bool preferNativeExplorer = IsExecutablePickerFilter(filter);

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                Log.Error("Environment.ProcessPath 为空，无法启动 file-picker，已跳过本进程对话框（避免 Shell 扩展拖死主进程）");
                return new OpenFilePickerResult(null, OpenFilePickerCompletion.Unavailable, null);
            }

            try
            {
                string? exeDir = Path.GetDirectoryName(exe);
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = string.IsNullOrEmpty(exeDir) ? Environment.CurrentDirectory : exeDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    // 与 FilePickerHelper.Run 中 Console.OutputEncoding（UTF-8）一致；否则中文路径会被按系统 ANSI 误解码为乱码。
                    StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    CreateNoWindow = true,
                };
                psi.ArgumentList.Add("--file-picker");
                psi.ArgumentList.Add("--mode");
                psi.ArgumentList.Add("open");
                psi.ArgumentList.Add("--filter");
                psi.ArgumentList.Add(filter);
                psi.ArgumentList.Add("--title");
                psi.ArgumentList.Add(title);
                psi.ArgumentList.Add("--initdir");
                psi.ArgumentList.Add(initialDirectory ?? string.Empty);
                if (useCommDlg)
                    psi.ArgumentList.Add("--use-commdlg");

                using var proc = Process.Start(psi);
                if (proc == null)
                {
                    Log.Error("无法启动 file-picker 子进程，已跳过本进程对话框");
                    return await TryShowModernPickerFallbackAsync(
                        preferNativeExplorer,
                        filter,
                        title,
                        initialDirectory,
                        new OpenFilePickerResult(null, OpenFilePickerCompletion.Unavailable, null));
                }

                string? line = await proc.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                await proc.WaitForExitAsync().ConfigureAwait(false);

                line = line?.Trim();
                int exit = proc.ExitCode;
                if (exit == 2 || string.Equals(line, "CANCEL", StringComparison.OrdinalIgnoreCase))
                    return new OpenFilePickerResult(null, OpenFilePickerCompletion.Cancelled, exit);

                if (exit == 0 && !string.IsNullOrEmpty(line) && line.StartsWith("PATH:", StringComparison.Ordinal))
                {
                    string path = line.Substring("PATH:".Length).Trim();
                    if (string.IsNullOrEmpty(path))
                        return new OpenFilePickerResult(null, OpenFilePickerCompletion.HelperExitedAbnormally, exit);
                    return new OpenFilePickerResult(path, OpenFilePickerCompletion.Success, exit);
                }

                Log.Warning(
                    "file-picker 子进程异常结束：退出码={Code} 输出={Line}。不回退本进程对话框，请重试浏览或检查第三方 Shell 扩展。",
                    exit,
                    line);

                return await TryShowModernPickerFallbackAsync(
                    preferNativeExplorer,
                    filter,
                    title,
                    initialDirectory,
                    new OpenFilePickerResult(null, OpenFilePickerCompletion.HelperExitedAbnormally, exit));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "file-picker 子进程启动或通信失败，不回退本进程对话框");
                return await TryShowModernPickerFallbackAsync(
                    preferNativeExplorer,
                    filter,
                    title,
                    initialDirectory,
                    new OpenFilePickerResult(null, OpenFilePickerCompletion.Unavailable, null));
            }
        }

        private static async Task<OpenFilePickerResult> TryShowModernPickerFallbackAsync(
            bool enabled,
            string filter,
            string title,
            string? initialDirectory,
            OpenFilePickerResult originalResult)
        {
            if (!enabled)
                return originalResult;

            try
            {
                Log.Warning(
                    "原生 Win11 IFileOpenDialog helper 不可用或异常，将回退自绘文件选择器。filter={Filter} title={Title}",
                    filter,
                    title);
                var fallback = await ModernFilePickerWindow.PresentAsync(title, initialDirectory);
                return fallback.Completion == OpenFilePickerCompletion.Unavailable
                    ? originalResult
                    : fallback;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "自绘文件选择器 fallback 也不可用");
                return originalResult;
            }
        }

        public static async Task<string?> ShowSaveFileDialogAsync(
            IntPtr hwndOwner, string filter, string title,
            string suggestedFileName = "mouse-mapping.json", string? initialDirectory = null)
        {
            await EnsureFileDialogPrewarmedAsync();

            using var _isolate = MouseMappingEngine.EnterFileDialogHookIsolation();
            await Task.Yield();

            IntPtr owner = NormalizeOwner(hwndOwner);
            if (owner != IntPtr.Zero)
            {
                try { SetForegroundWindow(owner); } catch { }
            }

            return await RunOnStaThread(() =>
                SaveDialogOnSta(owner, filter, title, suggestedFileName, initialDirectory));
        }

        /// <summary>判断过滤器是否为「可执行文件」类（用于启用应用内 Modern picker）。</summary>
        private static bool IsExecutablePickerFilter(string filter)
        {
            if (string.IsNullOrWhiteSpace(filter))
                return false;
            var f = filter.Replace(" ", "", StringComparison.Ordinal);
            return f.Contains("*.exe", StringComparison.OrdinalIgnoreCase)
                   || f.Contains("*.bat", StringComparison.OrdinalIgnoreCase)
                   || f.Contains("*.cmd", StringComparison.OrdinalIgnoreCase);
        }

        public static string TryGetInitialDirectoryFromExistingPaths(IEnumerable<string>? paths)
        {
            if (paths != null)
            {
                foreach (var raw in paths)
                {
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var t = raw.Trim();
                    try
                    {
                        if (File.Exists(t))
                        {
                            var d = Path.GetDirectoryName(t);
                            if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
                                return Path.GetFullPath(d);
                        }
                        else if (Directory.Exists(t))
                        {
                            return Path.GetFullPath(t);
                        }
                    }
                    catch { }
                }
            }

            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(docs) && Directory.Exists(docs))
                return Path.GetFullPath(docs);

            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        public static void Prewarm() => _ = GetOrStartFileDialogPrewarmTask();

        public static Task EnsureFileDialogPrewarmedAsync()
        {
            var t = _fileDialogPrewarmTask;
            if (t is { IsCompletedSuccessfully: true })
                return Task.CompletedTask;

            lock (_fileDialogPrewarmLock)
            {
                t = _fileDialogPrewarmTask;
                if (t is { IsCompletedSuccessfully: true })
                    return Task.CompletedTask;
                return _fileDialogPrewarmTask ??= Task.Run(PrewarmCore);
            }
        }

        #endregion

        #region STA 工人 + 对话框核心

        private static void EnsureFileDialogStaWorker()
        {
            if (_staWorkerThread != null && _staOleReady.IsSet)
                return;

            lock (_staInitLock)
            {
                if (_staWorkerThread != null)
                {
                    _staOleReady.Wait();
                    return;
                }

                var t = new Thread(FileDialogStaWorkerMain)
                {
                    IsBackground = true,
                    Name = "Lunagal-FileDialog-STA",
                };
                t.SetApartmentState(ApartmentState.STA);
                t.Start();
                _staOleReady.Wait();
                _staWorkerThread = t;
            }
        }

        /// <summary>
        /// 常驻 STA 线程：启动时 <see cref="OleInitialize"/>，循环执行排队的对话框工作（模态框内部会泵消息）。
        /// </summary>
        private static void FileDialogStaWorkerMain()
        {
            int oleHr = OleInitialize(IntPtr.Zero);
            if (oleHr != 0 && oleHr != 1)
            {
                Log.Warning(
                    "FileDialog STA: OleInitialize 返回 0x{Hr:X8}（右键/拖放可能异常）",
                    oleHr);
            }

            _staOleReady.Set();

            IDisposable? rmbHook = null;
            try
            {
                rmbHook = FileDialogRmbSuppressHook.InstallOnCurrentThread();
                for (; ; )
                {
                    _staHasWork.WaitOne();
                    Action? run;
                    lock (_staWorkLock)
                    {
                        run = _staWork;
                        _staWork = null;
                    }

                    run?.Invoke();
                }
            }
            finally
            {
                try { rmbHook?.Dispose(); } catch { /* ignore */ }
                if (oleHr == 0 || oleHr == 1)
                {
                    try { OleUninitialize(); } catch { /* 进程退出 */ }
                }
            }
        }

        private static Task<T?> RunOnStaThread<T>(Func<T?> work) where T : class
        {
            EnsureFileDialogStaWorker();

            var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_staWorkLock)
            {
                if (_staWork != null)
                {
                    throw new InvalidOperationException(
                        "已有文件对话框在 STA 队列中执行，请等待结束后再打开新的对话框。");
                }

                _staWork = () =>
                {
                    try { tcs.TrySetResult(work()); }
                    catch (Exception ex) { tcs.TrySetException(ex); }
                };
            }

            _staHasWork.Set();
            return tcs.Task;
        }

        private static IntPtr NormalizeOwner(IntPtr hwndOwner) =>
            hwndOwner != IntPtr.Zero && IsWindow(hwndOwner) ? hwndOwner : IntPtr.Zero;

        private static void CoCreateFileOpenDialog(out object dialogObj)
        {
            Guid clsid = CLSID_FileOpenDialog;
            Guid iid = IID_IFileDialog;
            try
            {
                CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out dialogObj);
            }
            catch (COMException ex)
            {
                Log.Debug(ex, "CoCreate IFileOpenDialog CLSCTX_INPROC 失败，重试 CLSCTX_ALL");
                CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out dialogObj);
            }
        }

        private static void CoCreateFileSaveDialog(out object dialogObj)
        {
            Guid clsid = CLSID_FileSaveDialog;
            Guid iid = IID_IFileSaveDialog;
            try
            {
                CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out dialogObj);
            }
            catch (COMException ex)
            {
                Log.Debug(ex, "CoCreate IFileSaveDialog CLSCTX_INPROC 失败，重试 CLSCTX_ALL");
                CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_ALL, ref iid, out dialogObj);
            }
        }

        private static COMDLG_FILTERSPEC[] EnsureNonEmptyOpenSpecs(string? filter)
        {
            var specs = BuildFilterSpecs(filter);
            if (specs.Length > 0) return specs;
            return new[] { new COMDLG_FILTERSPEC { pszName = "所有文件", pszSpec = "*.*" } };
        }

        private static COMDLG_FILTERSPEC[] EnsureNonEmptySaveSpecs(string? filter)
        {
            var specs = BuildFilterSpecs(filter);
            if (specs.Length > 0) return specs;
            return new[] { new COMDLG_FILTERSPEC { pszName = "所有文件", pszSpec = "*.*" } };
        }

        /// <summary>优先无 owner 再回退到 hwnd，减轻 WinUI 与 Shell 的跨窗同步等待。</summary>
        private static List<IntPtr> BuildDialogOwnerSequence(IntPtr hwndOwner)
        {
            var owners = new List<IntPtr>();
            if (hwndOwner != IntPtr.Zero)
            {
                owners.Add(IntPtr.Zero);
                owners.Add(hwndOwner);
            }
            else
            {
                owners.Add(IntPtr.Zero);
            }

            return owners;
        }

        private static string? OpenDialogOnSta(IntPtr hwndOwner, string? filter, string? title, string? initialDir)
        {
            var owners = BuildDialogOwnerSequence(hwndOwner);

            foreach (var owner in owners)
            {
                object? dialogObj = null;
                try
                {
                    CoCreateFileOpenDialog(out dialogObj);
                    if (dialogObj is not IFileDialog dialog)
                    {
                        Log.Warning("CoCreate 未返回 IFileDialog");
                        break;
                    }

                    dialog.SetTitle(title ?? string.Empty);
                    dialog.SetOptions(FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM
                        | FOS_NOCHANGEDIR | FOS_DONTADDTORECENT
                        | FOS_NODEREFERENCELINKS | FOS_FORCEPREVIEWPANE_OFF
                        | FOS_HIDEPINNEDPLACES);

                    var specs = EnsureNonEmptyOpenSpecs(filter);
                    dialog.SetFileTypes((uint)specs.Length, specs);

                    string? defExt = GetDefaultExtension(filter);
                    if (!string.IsNullOrEmpty(defExt))
                        dialog.SetDefaultExtension(defExt);

                    TrySetInitialFolder(psi => _ = dialog.SetFolder(psi), initialDir);

                    int hr = dialog.Show(owner);
                    if (hr == HRESULT_ERROR_CANCELLED)
                    {
                        Log.Debug("IFileOpenDialog：用户取消");
                        return null;
                    }

                    if (hr == S_OK)
                    {
                        string? path = GetFilesystemPathFromOpenDialog(dialog);
                        if (path != null) return path;
                    }

                    Log.Warning("IFileOpenDialog.Show(owner={HasOwner}) 返回 0x{Hr:X8}", owner != IntPtr.Zero, hr);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "IFileOpenDialog 路径失败 (owner={HasOwner})", owner != IntPtr.Zero);
                }
                finally
                {
                    if (dialogObj != null)
                        Marshal.FinalReleaseComObject(dialogObj);
                }
            }

            try
            {
                return GetOpenFileNameExplorer(hwndOwner != IntPtr.Zero ? hwndOwner : IntPtr.Zero, filter, title, initialDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GetOpenFileNameW(OFN_EXPLORER) 回退失败");
                return null;
            }
        }

        private static string? GetFilesystemPathFromOpenDialog(IFileDialog dialog)
        {
            if (dialog.GetResult(out var item) != S_OK || item == null)
                return null;
            try
            {
                if (item.GetDisplayName(SIGDN_FILESYSPATH, out var path) != S_OK)
                    return null;
                Log.Debug("IFileOpenDialog 已选: {Path}", path);
                return string.IsNullOrEmpty(path) ? null : path;
            }
            finally
            {
                Marshal.FinalReleaseComObject(item);
            }
        }

        private static string? SaveDialogOnSta(
            IntPtr hwndOwner, string? filter, string? title,
            string? suggestedFileName, string? initialDir)
        {
            var owners = BuildDialogOwnerSequence(hwndOwner);

            foreach (var owner in owners)
            {
                object? dialogObj = null;
                try
                {
                    CoCreateFileSaveDialog(out dialogObj);
                    if (dialogObj is not IFileSaveDialog dialog)
                    {
                        Log.Warning("CoCreate 未返回 IFileSaveDialog");
                        break;
                    }

                    dialog.SetTitle(title ?? string.Empty);
                    dialog.SetOptions(FOS_OVERWRITEPROMPT | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM
                        | FOS_NOCHANGEDIR | FOS_DONTADDTORECENT
                        | FOS_NODEREFERENCELINKS | FOS_FORCEPREVIEWPANE_OFF
                        | FOS_HIDEPINNEDPLACES);

                    if (!string.IsNullOrEmpty(suggestedFileName))
                        dialog.SetFileName(suggestedFileName);

                    var specs = EnsureNonEmptySaveSpecs(filter);
                    dialog.SetFileTypes((uint)specs.Length, specs);

                    string? defExt = GetDefaultExtension(filter);
                    if (!string.IsNullOrEmpty(defExt))
                        dialog.SetDefaultExtension(defExt);

                    TrySetInitialFolder(psi => _ = dialog.SetFolder(psi), initialDir);

                    int hr = dialog.Show(owner);
                    if (hr == HRESULT_ERROR_CANCELLED)
                    {
                        Log.Debug("IFileSaveDialog：用户取消");
                        return null;
                    }

                    if (hr == S_OK)
                    {
                        if (dialog.GetResult(out var item) == S_OK && item != null)
                        {
                            try
                            {
                                if (item.GetDisplayName(SIGDN_FILESYSPATH, out var path) == S_OK
                                    && !string.IsNullOrEmpty(path))
                                {
                                    Log.Debug("IFileSaveDialog 已选: {Path}", path);
                                    return path;
                                }
                            }
                            finally
                            {
                                Marshal.FinalReleaseComObject(item);
                            }
                        }
                    }

                    Log.Warning("IFileSaveDialog.Show(owner={HasOwner}) 返回 0x{Hr:X8}", owner != IntPtr.Zero, hr);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "IFileSaveDialog 路径失败 (owner={HasOwner})", owner != IntPtr.Zero);
                }
                finally
                {
                    if (dialogObj != null)
                        Marshal.FinalReleaseComObject(dialogObj);
                }
            }

            try
            {
                return GetSaveFileNameExplorer(
                    hwndOwner != IntPtr.Zero ? hwndOwner : IntPtr.Zero,
                    filter, title, suggestedFileName, initialDir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "GetSaveFileNameW(OFN_EXPLORER) 回退失败");
                return null;
            }
        }

        private static void TrySetInitialFolder(Action<IShellItem> setFolder, string? initialDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(initialDir)) return;
                string full = Path.GetFullPath(initialDir.Trim());
                if (!Directory.Exists(full)) return;

                Guid iid = IID_IShellItem;
                SHCreateItemFromParsingName(full, IntPtr.Zero, ref iid, out var item);
                if (item == null) return;
                try { setFolder(item); }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "TrySetInitialFolder 忽略");
            }
        }

        private static string BuildCommDlgFilterString(string? pipeFilter)
        {
            if (string.IsNullOrEmpty(pipeFilter)) return "所有文件\0*.*\0\0";
            var segments = pipeFilter.Split('|');
            var sb = new StringBuilder();
            for (int i = 0; i + 1 < segments.Length; i += 2)
            {
                string name = segments[i].Trim();
                string spec = segments[i + 1].Trim();
                if (string.IsNullOrEmpty(spec)) continue;
                if (string.IsNullOrEmpty(name)) name = spec;
                sb.Append(name);
                sb.Append('\0');
                sb.Append(spec);
                sb.Append('\0');
            }

            sb.Append('\0');
            return sb.ToString();
        }

        private static string ResolveCommDlgInitialDir(string? initialDir)
        {
            if (!string.IsNullOrWhiteSpace(initialDir))
            {
                try
                {
                    var t = initialDir.Trim();
                    if (Directory.Exists(t))
                        return Path.GetFullPath(t);
                }
                catch { }
            }

            string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(docs) && Directory.Exists(docs))
                return Path.GetFullPath(docs);
            return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }

        /// <remarks>超大目录下列表填充与加载圈主要由 Shell 枚举决定，应用层无法消除该等待。</remarks>
        private static string? GetOpenFileNameExplorer(
            IntPtr hwndOwner, string? filter, string? title, string? initialDir)
        {
            string filterNative = BuildCommDlgFilterString(filter);
            string initDir = ResolveCommDlgInitialDir(initialDir);

            const int nMaxFileChars = 32768;
            IntPtr pFile = Marshal.AllocHGlobal(nMaxFileChars * 2);
            try
            {
                var zero = new byte[nMaxFileChars * 2];
                Marshal.Copy(zero, 0, pFile, zero.Length);

                int flags = OFN_PATHMUSTEXIST | OFN_FILEMUSTEXIST | OFN_NOCHANGEDIR | OFN_DONTADDTORECENT
                    | OFN_LONGNAMES | OFN_EXPLORER | OFN_ENABLESIZING;

                var ofn = new OPENFILENAMEW
                {
                    lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                    hwndOwner = hwndOwner,
                    hInstance = IntPtr.Zero,
                    lpstrFilter = filterNative,
                    nFilterIndex = 1,
                    lpstrFile = pFile,
                    nMaxFile = nMaxFileChars,
                    lpstrInitialDir = initDir,
                    lpstrTitle = title ?? string.Empty,
                    Flags = flags,
                    pvReserved = IntPtr.Zero,
                    dwReserved = 0,
                    FlagsEx = 0,
                };

                if (!GetOpenFileNameW(ref ofn))
                {
                    int err = CommDlgExtendedError();
                    if (err == 0)
                    {
                        Log.Debug("GetOpenFileNameW 用户取消");
                        return null;
                    }
                    Log.Warning("GetOpenFileNameW CommDlgExtendedError=0x{Err:X}", err);
                    return null;
                }

                string? s = Marshal.PtrToStringUni(pFile);
                if (string.IsNullOrEmpty(s)) return null;
                int z = s.IndexOf('\0', StringComparison.Ordinal);
                if (z >= 0) s = s.Substring(0, z);
                s = s.Trim();
                Log.Debug("GetOpenFileNameW 已选: {Path}", s);
                return string.IsNullOrEmpty(s) ? null : s;
            }
            finally
            {
                Marshal.FreeHGlobal(pFile);
            }
        }

        private static string? GetSaveFileNameExplorer(
            IntPtr hwndOwner, string? filter, string? title, string? suggestedFileName, string? initialDir)
        {
            string filterNative = BuildCommDlgFilterString(filter);
            string initDir = ResolveCommDlgInitialDir(initialDir);
            string? defExt = GetDefaultExtension(filter);

            const int nMaxFileChars = 32768;
            IntPtr pFile = Marshal.AllocHGlobal(nMaxFileChars * 2);
            try
            {
                var zero = new byte[nMaxFileChars * 2];
                Marshal.Copy(zero, 0, pFile, zero.Length);
                if (!string.IsNullOrEmpty(suggestedFileName))
                {
                    byte[] uni = Encoding.Unicode.GetBytes(suggestedFileName + "\0");
                    Marshal.Copy(uni, 0, pFile, Math.Min(uni.Length, nMaxFileChars * 2 - 2));
                }

                int flags = OFN_PATHMUSTEXIST | OFN_OVERWRITEPROMPT | OFN_NOCHANGEDIR | OFN_DONTADDTORECENT
                    | OFN_LONGNAMES | OFN_EXPLORER | OFN_ENABLESIZING;

                var ofn = new OPENFILENAMEW
                {
                    lStructSize = Marshal.SizeOf<OPENFILENAMEW>(),
                    hwndOwner = hwndOwner,
                    hInstance = IntPtr.Zero,
                    lpstrFilter = filterNative,
                    nFilterIndex = 1,
                    lpstrFile = pFile,
                    nMaxFile = nMaxFileChars,
                    lpstrInitialDir = initDir,
                    lpstrTitle = title ?? string.Empty,
                    Flags = flags,
                    lpstrDefExt = string.IsNullOrEmpty(defExt) ? null : defExt,
                    pvReserved = IntPtr.Zero,
                    dwReserved = 0,
                    FlagsEx = 0,
                };

                if (!GetSaveFileNameW(ref ofn))
                {
                    int err = CommDlgExtendedError();
                    if (err == 0)
                    {
                        Log.Debug("GetSaveFileNameW 用户取消");
                        return null;
                    }
                    Log.Warning("GetSaveFileNameW CommDlgExtendedError=0x{Err:X}", err);
                    return null;
                }

                string? s = Marshal.PtrToStringUni(pFile);
                if (string.IsNullOrEmpty(s)) return null;
                int z = s.IndexOf('\0', StringComparison.Ordinal);
                if (z >= 0) s = s.Substring(0, z);
                s = s.Trim();
                Log.Debug("GetSaveFileNameW 已选: {Path}", s);
                return string.IsNullOrEmpty(s) ? null : s;
            }
            finally
            {
                Marshal.FreeHGlobal(pFile);
            }
        }

        private static COMDLG_FILTERSPEC[] BuildFilterSpecs(string? filter)
        {
            if (string.IsNullOrEmpty(filter)) return Array.Empty<COMDLG_FILTERSPEC>();
            var segments = filter.Split('|');
            var result = new List<COMDLG_FILTERSPEC>();
            for (int i = 0; i + 1 < segments.Length; i += 2)
            {
                string name = segments[i].Trim();
                string spec = segments[i + 1].Trim();
                if (string.IsNullOrEmpty(spec)) continue;
                if (string.IsNullOrEmpty(name)) name = spec;
                result.Add(new COMDLG_FILTERSPEC { pszName = name, pszSpec = spec });
            }
            return result.ToArray();
        }

        private static string? GetDefaultExtension(string? filter)
        {
            if (string.IsNullOrEmpty(filter)) return null;
            var segments = filter.Split('|');
            for (int i = 1; i < segments.Length; i += 2)
            {
                foreach (var piece in segments[i].Split(';'))
                {
                    string ext = piece.Trim();
                    int dot = ext.LastIndexOf('.');
                    if (dot < 0 || dot == ext.Length - 1) continue;
                    string e = ext.Substring(dot + 1);
                    if (e == "*") continue;
                    return e;
                }
            }
            return null;
        }

        #endregion

        #region 预热

        private static Task GetOrStartFileDialogPrewarmTask()
        {
            lock (_fileDialogPrewarmLock)
            {
                return _fileDialogPrewarmTask ??= Task.Run(PrewarmCore);
            }
        }

        private static void PrewarmCore()
        {
            try
            {
                foreach (var name in new[]
                {
                    "shell32.dll",
                    "comdlg32.dll",
                    "shlwapi.dll",
                    "propsys.dll",
                    "windows.storage.dll",
                    "thumbcache.dll",
                    "explorerframe.dll",
                    "ntshrui.dll",
                    "LinkInfo.dll",
                    "shdocvw.dll",
                    "urlmon.dll",
                    "actxprxy.dll",
                    "windowscodecs.dll",
                })
                {
                    try { LoadLibraryW(name); } catch { }
                }

                Log.Debug("文件对话框 Shell DLL 预热完成");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "文件对话框 Shell DLL 预热失败（忽略）");
            }
        }

        #endregion
    }
}
