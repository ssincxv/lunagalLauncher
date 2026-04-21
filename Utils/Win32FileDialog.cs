using System.Runtime.InteropServices;
using lunagalLauncher.Services;
using Serilog;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// 文件「打开 / 另存为」对话框封装。
    ///
    /// 实现路径（演进梳理）：
    /// 1) <c>comdlg32!GetOpenFileName</c>：Win11 + WinUI 3 下直接 native AV；放弃。
    /// 2) <see cref="Windows.Storage.Pickers.FileOpenPicker"/>：走 picker broker 跨进程，管理员权限下
    ///    静默失败不弹任何对话框；放弃。
    /// 3) 独立子进程 + <see cref="IFileOpenDialog"/>：可用但子进程 CLR 冷启动 300–500ms 明显卡；放弃。
    /// 4) <b>当前方案</b>：主进程直接 <c>CoCreateInstance IFileOpenDialog</c>（in-process、无子进程启动开销）
    ///    + 临时 LL 鼠标钩子吞掉**落在本进程对话框窗口上的右键**——右键不到达对话框 → <c>IContextMenu</c>
    ///    不触发 → 第三方 shell 扩展 dll 根本不会加载，从根上避免它们 AV 把主进程带走。
    ///    <br/>并调用 <see cref="MouseMappingEngine.SuspendHooks"/> 暂停鼠标映射引擎的 LL 钩子，
    ///    避免合成事件 / 映射逻辑与文件对话框消息泵互扰。
    ///
    /// 对外 API 保持签名不变，上层调用方（MouseMappingPage / MouseMappingRuleRow / AppManagementPage /
    /// MainPage / LlamaServicePage）零改动。
    /// </summary>
    public static class Win32FileDialog
    {
        #region FILEOPENDIALOGOPTIONS

        private const uint FOS_OVERWRITEPROMPT   = 0x00000002;
        private const uint FOS_NOCHANGEDIR       = 0x00000008;
        private const uint FOS_FORCEFILESYSTEM   = 0x00000040;
        private const uint FOS_PATHMUSTEXIST     = 0x00000800;
        private const uint FOS_FILEMUSTEXIST     = 0x00001000;
        private const uint SIGDN_FILESYSPATH     = 0x80058000;
        private const int  S_OK                  = 0;
        private const int  HRESULT_ERROR_CANCELLED = unchecked((int)0x800704C7);
        private const uint CLSCTX_ALL            = 0x1 | 0x2 | 0x4;

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
        [Guid("D57C7288-D4AD-4768-BE02-9D969532D960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
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
            [PreserveSig] int GetResults(out IntPtr ppenum);
            [PreserveSig] int GetSelectedItems(out IntPtr ppsai);
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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
            ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW([MarshalAs(UnmanagedType.LPWStr)] string lpLibFileName);

        private static Guid CLSID_FileOpenDialog = new("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7");
        private static Guid CLSID_FileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
        private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
        private static Guid IID_IFileOpenDialog = new("D57C7288-D4AD-4768-BE02-9D969532D960");
        private static Guid IID_IFileSaveDialog = new("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB");

        #endregion

        #region 公开 API

        public static string? ShowOpenFileDialog(IntPtr hwndOwner, string filter, string title, string? initialDirectory = null)
            => ShowOpenFileDialogAsync(hwndOwner, filter, title, initialDirectory).GetAwaiter().GetResult();

        public static async Task<string?> ShowOpenFileDialogAsync(IntPtr hwndOwner, string filter, string title, string? initialDirectory = null)
        {
            // 让 UI 帧先处理完当前事件（比如按钮按下动画），避免视觉上"按钮还没起来就卡"。
            await Task.Yield();

            using var _suspend = MouseMappingEngine.SuspendHooks();
            using var _swallower = RightClickSwallower.Install();

            return ShowOpenOnUi(hwndOwner, filter, title, initialDirectory);
        }

        public static string? ShowSaveFileDialog(IntPtr hwndOwner, string filter, string title, string suggestedFileName = "mouse-mapping.json", string? initialDirectory = null)
            => ShowSaveFileDialogAsync(hwndOwner, filter, title, suggestedFileName, initialDirectory).GetAwaiter().GetResult();

        public static async Task<string?> ShowSaveFileDialogAsync(IntPtr hwndOwner, string filter, string title, string suggestedFileName = "mouse-mapping.json", string? initialDirectory = null)
        {
            await Task.Yield();

            using var _suspend = MouseMappingEngine.SuspendHooks();
            using var _swallower = RightClickSwallower.Install();

            return ShowSaveOnUi(hwndOwner, filter, title, suggestedFileName, initialDirectory);
        }

        public static string? TryGetInitialDirectoryFromExistingPaths(IEnumerable<string>? paths)
        {
            if (paths == null) return null;
            foreach (var p in paths)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(p)) continue;
                    var t = p.Trim();
                    if (Directory.Exists(t)) return Path.GetFullPath(t);
                    var d = Path.GetDirectoryName(t);
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) return Path.GetFullPath(d);
                }
                catch { }
            }
            return null;
        }

        /// <summary>兼容保留（历史 comdlg32 过滤串入口）。</summary>
        public static string BuildFilterString(string pipeSeparatedFilter) => pipeSeparatedFilter ?? string.Empty;

        /// <summary>
        /// 预热文件对话框相关资源。在 app 启动后调用一次，后台完成：
        ///
        /// 1) <b>预加载 shell 文件对话框实现依赖的重 DLL</b>：
        ///    <c>explorerframe.dll</c>（Windows 现代文件对话框里嵌入的文件视图）、
        ///    <c>windows.storage.dll</c>、<c>propsys.dll</c>、<c>thumbcache.dll</c>、
        ///    <c>shlwapi.dll</c> 等。这些 dll 在 <c>IFileDialog.Show()</c> 时才会被 LoadLibrary，
        ///    是首次点"浏览"时真正的主要卡顿来源（占 200–300ms 冷磁盘 IO + 初始化）。
        ///    提前加载能把这段开销完全移到启动后空闲时期。
        /// 2) <b>CoCreateInstance 两个对话框</b>：解析并缓存 in-proc server；触发 CLR JIT
        ///    本文件的所有 P/Invoke + COM interop 方法。
        ///
        /// 预热不显示任何 UI，COM 对象创建后立即释放。在后台线程执行（IFileOpenDialog 是
        /// both-threaded，只有 Show() 要求 STA，CoCreateInstance 无限制）。
        /// </summary>
        public static void Prewarm()
        {
            _ = Task.Run(() =>
            {
                try
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();

                    // 1) 预加载 FileDialog Show() 时会用到的重 DLL。
                    //    按依赖顺序加载：shlwapi → propsys → windows.storage → thumbcache → explorerframe。
                    //    用 LoadLibraryW 而不是 Assembly.LoadFrom/CoCreateInstance，避免触发 COM 注册副作用。
                    foreach (var name in new[]
                    {
                        "shlwapi.dll",
                        "propsys.dll",
                        "windows.storage.dll",
                        "thumbcache.dll",
                        "explorerframe.dll",
                    })
                    {
                        try { LoadLibraryW(name); } catch { }
                    }

                    // 2) CoCreateInstance 两个对话框，让 in-proc server 和 JIT 就绪
                    object? open = null;
                    try
                    {
                        CoCreateInstance(ref CLSID_FileOpenDialog, IntPtr.Zero, CLSCTX_ALL, ref IID_IFileOpenDialog, out open);
                    }
                    finally
                    {
                        if (open != null) Marshal.FinalReleaseComObject(open);
                    }

                    object? save = null;
                    try
                    {
                        CoCreateInstance(ref CLSID_FileSaveDialog, IntPtr.Zero, CLSCTX_ALL, ref IID_IFileSaveDialog, out save);
                    }
                    finally
                    {
                        if (save != null) Marshal.FinalReleaseComObject(save);
                    }

                    sw.Stop();
                    Log.Debug("文件对话框 COM + shell DLL 预热完成（耗时 {Ms}ms）", sw.ElapsedMilliseconds);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "文件对话框 COM 预热失败（忽略）");
                }
            });
        }

        #endregion

        #region 核心实现

        private static string? ShowOpenOnUi(IntPtr hwndOwner, string? filter, string? title, string? initialDir)
        {
            object? dialogObj = null;
            try
            {
                CoCreateInstance(ref CLSID_FileOpenDialog, IntPtr.Zero, CLSCTX_ALL, ref IID_IFileOpenDialog, out dialogObj);
                if (dialogObj is not IFileOpenDialog dialog)
                    throw new InvalidOperationException("CoCreateInstance 未返回 IFileOpenDialog");

                dialog.SetTitle(title ?? string.Empty);
                dialog.SetOptions(FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);

                var specs = BuildFilterSpecs(filter);
                if (specs.Length > 0) dialog.SetFileTypes((uint)specs.Length, specs);

                var folder = CreateFolderItem(initialDir);
                if (folder != null)
                {
                    try { dialog.SetFolder(folder); }
                    catch (Exception ex) { Log.Debug(ex, "SetFolder(Open) 忽略"); }
                    finally { Marshal.FinalReleaseComObject(folder); }
                }

                int hr = dialog.Show(hwndOwner);
                if (hr == HRESULT_ERROR_CANCELLED) return null;
                if (hr != S_OK) { Log.Warning("IFileOpenDialog.Show 返回 0x{Hr:X8}", hr); return null; }

                int ghr = dialog.GetResult(out var item);
                if (ghr != S_OK || item == null) { Log.Warning("IFileOpenDialog.GetResult 返回 0x{Hr:X8}", ghr); return null; }
                try
                {
                    if (item.GetDisplayName(SIGDN_FILESYSPATH, out var path) != S_OK) return null;
                    return string.IsNullOrEmpty(path) ? null : path;
                }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "IFileOpenDialog 调用失败: {Message}", ex.Message);
                return null;
            }
            finally
            {
                if (dialogObj != null) Marshal.FinalReleaseComObject(dialogObj);
            }
        }

        private static string? ShowSaveOnUi(IntPtr hwndOwner, string? filter, string? title, string? suggestedFileName, string? initialDir)
        {
            object? dialogObj = null;
            try
            {
                CoCreateInstance(ref CLSID_FileSaveDialog, IntPtr.Zero, CLSCTX_ALL, ref IID_IFileSaveDialog, out dialogObj);
                if (dialogObj is not IFileSaveDialog dialog)
                    throw new InvalidOperationException("CoCreateInstance 未返回 IFileSaveDialog");

                dialog.SetTitle(title ?? string.Empty);
                dialog.SetOptions(FOS_OVERWRITEPROMPT | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM | FOS_NOCHANGEDIR);
                if (!string.IsNullOrEmpty(suggestedFileName)) dialog.SetFileName(suggestedFileName);

                string? defExt = GetDefaultExtension(filter);
                if (!string.IsNullOrEmpty(defExt)) dialog.SetDefaultExtension(defExt);

                var specs = BuildFilterSpecs(filter);
                if (specs.Length > 0) dialog.SetFileTypes((uint)specs.Length, specs);

                var folder = CreateFolderItem(initialDir);
                if (folder != null)
                {
                    try { dialog.SetFolder(folder); }
                    catch (Exception ex) { Log.Debug(ex, "SetFolder(Save) 忽略"); }
                    finally { Marshal.FinalReleaseComObject(folder); }
                }

                int hr = dialog.Show(hwndOwner);
                if (hr == HRESULT_ERROR_CANCELLED) return null;
                if (hr != S_OK) { Log.Warning("IFileSaveDialog.Show 返回 0x{Hr:X8}", hr); return null; }

                int ghr = dialog.GetResult(out var item);
                if (ghr != S_OK || item == null) { Log.Warning("IFileSaveDialog.GetResult 返回 0x{Hr:X8}", ghr); return null; }
                try
                {
                    if (item.GetDisplayName(SIGDN_FILESYSPATH, out var path) != S_OK) return null;
                    return string.IsNullOrEmpty(path) ? null : path;
                }
                finally { Marshal.FinalReleaseComObject(item); }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "IFileSaveDialog 调用失败: {Message}", ex.Message);
                return null;
            }
            finally
            {
                if (dialogObj != null) Marshal.FinalReleaseComObject(dialogObj);
            }
        }

        private static IShellItem? CreateFolderItem(string? initialDir)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(initialDir)) return null;
                string full = Path.GetFullPath(initialDir);
                if (!Directory.Exists(full)) return null;
                Guid iid = IID_IShellItem;
                SHCreateItemFromParsingName(full, IntPtr.Zero, ref iid, out var item);
                return item;
            }
            catch { return null; }
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

        #region 右键吞噬器（防止 shell 扩展加载）

        /// <summary>
        /// 对话框打开期间安装一个进程级 LL 鼠标钩子，吞掉落在**本进程窗口**上的右键事件。
        /// 右键不到达 IFileOpenDialog 的文件视图 → IContextMenu 不触发 → 第三方 shell 扩展 dll
        /// 不会被加载，从根上避免它们 AV 杀进程。对话框关闭后 Dispose 立即卸载，对整个应用生命周期
        /// 的其它鼠标行为无影响。
        /// </summary>
        private sealed class RightClickSwallower : IDisposable
        {
            private const int WH_MOUSE_LL = 14;
            private const int WM_RBUTTONDOWN = 0x0204;
            private const int WM_RBUTTONUP = 0x0205;
            private const int WM_RBUTTONDBLCLK = 0x0206;

            [StructLayout(LayoutKind.Sequential)]
            private struct POINT { public int X, Y; }

            [StructLayout(LayoutKind.Sequential)]
            private struct MSLLHOOKSTRUCT
            {
                public POINT pt;
                public uint mouseData;
                public uint flags;
                public uint time;
                public IntPtr dwExtraInfo;
            }

            private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", SetLastError = true)]
            private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);
            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static extern bool UnhookWindowsHookEx(IntPtr hhk);
            [DllImport("user32.dll")]
            private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
            [DllImport("user32.dll")]
            private static extern IntPtr WindowFromPoint(POINT pt);
            [DllImport("user32.dll")]
            private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
            [DllImport("kernel32.dll")]
            private static extern uint GetCurrentProcessId();

            // 委托实例保存为字段防 GC（一旦 GC 回收，钩子回调到一半会崩）
            private readonly LowLevelMouseProc _proc;
            private IntPtr _hookHandle;
            private readonly uint _pid;
            private int _disposed;

            private RightClickSwallower()
            {
                _pid = GetCurrentProcessId();
                _proc = HookProc;
                _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _proc, IntPtr.Zero, 0);
                if (_hookHandle == IntPtr.Zero)
                    Log.Warning("RightClickSwallower: SetWindowsHookEx 失败 (err={Err})", Marshal.GetLastWin32Error());
            }

            internal static RightClickSwallower Install() => new RightClickSwallower();

            public void Dispose()
            {
                if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
                if (_hookHandle != IntPtr.Zero)
                {
                    try { UnhookWindowsHookEx(_hookHandle); } catch { }
                    _hookHandle = IntPtr.Zero;
                }
            }

            private IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
            {
                if (nCode < 0) return CallNextHookEx(_hookHandle, nCode, wParam, lParam);

                int msg = wParam.ToInt32();
                if (msg == WM_RBUTTONDOWN || msg == WM_RBUTTONUP || msg == WM_RBUTTONDBLCLK)
                {
                    try
                    {
                        var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        IntPtr h = WindowFromPoint(info.pt);
                        if (h != IntPtr.Zero)
                        {
                            GetWindowThreadProcessId(h, out uint pid);
                            if (pid == _pid)
                                return (IntPtr)1; // 吞掉；shell 右键菜单不弹，shell 扩展不加载
                        }
                    }
                    catch { /* 钩子回调里绝不能抛，吞掉 */ }
                }
                return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
            }
        }

        #endregion
    }
}
