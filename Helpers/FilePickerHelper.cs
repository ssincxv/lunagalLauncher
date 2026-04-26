using System.Runtime.InteropServices;
using System.Text;

namespace lunagalLauncher.Helpers;

/// <summary>
/// 独立子进程模式（同一 exe 加 <c>--file-picker</c>）：在极简 STA + <see cref="OleInitialize"/> 下弹出文件框，
/// 将 Shell 扩展崩溃隔离出主 WinUI 进程。不引用 Serilog / WinUI。
/// </summary>
public static class FilePickerHelper
{
    private const uint FOS_OVERWRITEPROMPT = 0x00000002;
    private const uint FOS_NOCHANGEDIR = 0x00000008;
    private const uint FOS_DONTADDTORECENT = 0x02000000;
    private const uint FOS_FORCEFILESYSTEM = 0x00000040;
    private const uint FOS_PATHMUSTEXIST = 0x00000800;
    private const uint FOS_FILEMUSTEXIST = 0x00001000;
    private const uint FOS_NODEREFERENCELINKS = 0x00100000;
    private const uint FOS_FORCEPREVIEWPANE_OFF = 0x40000000;
    private const uint FOS_HIDEPINNEDPLACES = 0x20000000;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int S_OK = 0;
    private const int HRESULT_ERROR_CANCELLED = unchecked((int)0x800704C7);
    private const uint CLSCTX_INPROC_SERVER = 0x1;
    private const uint CLSCTX_ALL = 0x17;

    private const int OFN_PATHMUSTEXIST = 0x00000800;
    private const int OFN_FILEMUSTEXIST = 0x00001000;
    private const int OFN_NOCHANGEDIR = 0x00000008;
    private const int OFN_DONTADDTORECENT = 0x02000000;
    private const int OFN_LONGNAMES = 0x00200000;
    private const int OFN_OVERWRITEPROMPT = 0x00000002;
    private const int OFN_EXPLORER = 0x00080000;
    /// <summary>允许 Explorer 样式对话框用鼠标/键盘调整大小（须与 <see cref="OFN_EXPLORER"/> 同用）。</summary>
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

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int OleInitialize(IntPtr pvReserved);

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern void OleUninitialize();

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
        ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetOpenFileNameW(ref OPENFILENAMEW ofn);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern bool GetSaveFileNameW(ref OPENFILENAMEW ofn);

    [DllImport("comdlg32.dll", ExactSpelling = true)]
    private static extern int CommDlgExtendedError();

    private static readonly Guid CLSID_FileSaveDialog = new("C0B4E2F3-BA21-4773-8DBA-335EC946EB8B");
    private static readonly Guid CLSID_FileOpenDialog = new("DC1C5A9C-88D5-4336-A45D-742C11672812");
    private static readonly Guid IID_IShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");
    private static readonly Guid IID_IFileDialog = new("42f85136-db7e-439c-85fb-4201c6db9ee0");
    private static readonly Guid IID_IFileSaveDialog = new("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB");

    /// <summary>0 成功；1 错误；2 用户取消。</summary>
    public static int Run(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = Console.OutputEncoding;

        var tcs = new TaskCompletionSource<(int exitCode, string line)>();
        var worker = new Thread(() =>
        {
            int oleHr = OleInitialize(IntPtr.Zero);
            try
            {
                if (oleHr != 0 && oleHr != 1)
                {
                    tcs.TrySetResult((1, "ERROR:OleInitialize 0x" + oleHr.ToString("X8")));
                    return;
                }

                try
                {
                    using (FileDialogRmbSuppressHook.InstallOnCurrentThread())
                    {
                        var parsed = ParseArgs(args);
                        string line;
                        int code = RunDialog(parsed, out line);
                        tcs.TrySetResult((code, line));
                    }
                }
                catch (Exception ex)
                {
                    tcs.TrySetResult((1, "ERROR:" + ex.Message));
                }
            }
            finally
            {
                if (oleHr == 0 || oleHr == 1)
                {
                    try { OleUninitialize(); } catch { /* ignore */ }
                }
            }
        })
        {
            IsBackground = true,
            Name = "Lunagal-FilePickerHelper-STA",
        };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        var (exitCode, line) = tcs.Task.GetAwaiter().GetResult();
        Console.Out.WriteLine(line);
        return exitCode;
    }

    private sealed class ParsedArgs
    {
        public string Mode = "open";
        public string Filter = string.Empty;
        public string Title = string.Empty;
        public string InitDir = string.Empty;
        public string Suggested = string.Empty;
        public string? DefaultExt;
        public bool UseCommDlg;
    }

    private static ParsedArgs ParseArgs(string[] args)
    {
        var p = new ParsedArgs();
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            if (a == "--use-commdlg")
            {
                p.UseCommDlg = true;
                continue;
            }

            if (i + 1 >= args.Length)
                break;

            string v = args[++i];
            switch (a)
            {
                case "--mode": p.Mode = v; break;
                case "--filter": p.Filter = v; break;
                case "--title": p.Title = v; break;
                case "--initdir": p.InitDir = v; break;
                case "--suggested": p.Suggested = v; break;
                case "--default-ext": p.DefaultExt = string.IsNullOrEmpty(v) ? null : v; break;
            }
        }

        return p;
    }

    /// <returns>退出码；<paramref name="line"/> 为输出行（PATH:/CANCEL/ERROR:）。</returns>
    private static int RunDialog(ParsedArgs p, out string line)
    {
        line = "ERROR:unknown";
        if (string.Equals(p.Mode, "save", StringComparison.OrdinalIgnoreCase))
        {
            string? path = p.UseCommDlg
                ? GetSaveFileNameCommDlg(p.Filter, p.Title, p.InitDir, p.Suggested, p.DefaultExt ?? GetDefaultExtension(p.Filter))
                : ShowIFileSaveDialog(p.Filter, p.Title, p.InitDir, p.Suggested);
            if (path == null)
            {
                if (!p.UseCommDlg)
                {
                    line = "CANCEL";
                    return 2;
                }

                int err = CommDlgExtendedError();
                if (err == 0)
                {
                    line = "CANCEL";
                    return 2;
                }

                line = "ERROR:GetSaveFileNameW CommDlgExtendedError=0x" + err.ToString("X");
                return 1;
            }

            line = "PATH:" + path;
            return 0;
        }

        string? openPath = p.UseCommDlg
            ? GetOpenFileNameCommDlg(p.Filter, p.Title, p.InitDir)
            : ShowIFileOpenDialog(p.Filter, p.Title, p.InitDir);
        if (openPath == null)
        {
            if (!p.UseCommDlg)
            {
                line = "CANCEL";
                return 2;
            }

            int err = CommDlgExtendedError();
            if (err == 0)
            {
                line = "CANCEL";
                return 2;
            }

            line = "ERROR:GetOpenFileNameW CommDlgExtendedError=0x" + err.ToString("X");
            return 1;
        }

        line = "PATH:" + openPath;
        return 0;
    }

    private static string? ShowIFileOpenDialog(string? filter, string? title, string? initialDir)
    {
        object? dialogObj = null;
        try
        {
            CoCreateFileOpenDialog(out dialogObj);
            if (dialogObj is not IFileDialog dialog)
                return null;

            dialog.SetTitle(title ?? string.Empty);
            dialog.SetOptions(FOS_FILEMUSTEXIST | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM
                | FOS_NOCHANGEDIR | FOS_DONTADDTORECENT
                | FOS_NODEREFERENCELINKS | FOS_FORCEPREVIEWPANE_OFF | FOS_HIDEPINNEDPLACES);

            var specs = EnsureNonEmptyOpenSpecs(filter);
            dialog.SetFileTypes((uint)specs.Length, specs);

            string? defExt = GetDefaultExtension(filter);
            if (!string.IsNullOrEmpty(defExt))
                dialog.SetDefaultExtension(defExt);

            TrySetInitialFolder(psi => _ = dialog.SetFolder(psi), initialDir);

            int hr;
            try
            {
                FileDialogRmbSuppressHook.RegisterActiveFileDialog(dialogObj);
                hr = dialog.Show(IntPtr.Zero);
            }
            finally
            {
                FileDialogRmbSuppressHook.RegisterActiveFileDialog(null);
            }

            if (hr == HRESULT_ERROR_CANCELLED)
                return null;
            if (hr != S_OK)
                return null;

            if (dialog.GetResult(out var item) != S_OK || item == null)
                return null;
            try
            {
                if (item.GetDisplayName(SIGDN_FILESYSPATH, out var path) != S_OK)
                    return null;
                return string.IsNullOrEmpty(path) ? null : path;
            }
            finally
            {
                Marshal.FinalReleaseComObject(item);
            }
        }
        finally
        {
            if (dialogObj != null)
                Marshal.FinalReleaseComObject(dialogObj);
        }
    }

    private static string? ShowIFileSaveDialog(string? filter, string? title, string? initialDir, string? suggested)
    {
        object? dialogObj = null;
        try
        {
            CoCreateFileSaveDialog(out dialogObj);
            if (dialogObj is not IFileSaveDialog dialog)
                return null;

            dialog.SetTitle(title ?? string.Empty);
            dialog.SetOptions(FOS_OVERWRITEPROMPT | FOS_PATHMUSTEXIST | FOS_FORCEFILESYSTEM
                | FOS_NOCHANGEDIR | FOS_DONTADDTORECENT
                | FOS_NODEREFERENCELINKS | FOS_FORCEPREVIEWPANE_OFF | FOS_HIDEPINNEDPLACES);

            if (!string.IsNullOrEmpty(suggested))
                dialog.SetFileName(suggested);

            var specs = EnsureNonEmptySaveSpecs(filter);
            dialog.SetFileTypes((uint)specs.Length, specs);

            string? defExt = GetDefaultExtension(filter);
            if (!string.IsNullOrEmpty(defExt))
                dialog.SetDefaultExtension(defExt);

            TrySetInitialFolder(psi => _ = dialog.SetFolder(psi), initialDir);

            int hr;
            try
            {
                FileDialogRmbSuppressHook.RegisterActiveFileDialog(dialogObj);
                hr = dialog.Show(IntPtr.Zero);
            }
            finally
            {
                FileDialogRmbSuppressHook.RegisterActiveFileDialog(null);
            }

            if (hr == HRESULT_ERROR_CANCELLED)
                return null;
            if (hr != S_OK)
                return null;

            if (dialog.GetResult(out var item) != S_OK || item == null)
                return null;
            try
            {
                if (item.GetDisplayName(SIGDN_FILESYSPATH, out var path) != S_OK)
                    return null;
                return string.IsNullOrEmpty(path) ? null : path;
            }
            finally
            {
                Marshal.FinalReleaseComObject(item);
            }
        }
        finally
        {
            if (dialogObj != null)
                Marshal.FinalReleaseComObject(dialogObj);
        }
    }

    private static void CoCreateFileOpenDialog(out object dialogObj)
    {
        Guid clsid = CLSID_FileOpenDialog;
        Guid iid = IID_IFileDialog;
        try
        {
            CoCreateInstance(ref clsid, IntPtr.Zero, CLSCTX_INPROC_SERVER, ref iid, out dialogObj);
        }
        catch (COMException)
        {
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
        catch (COMException)
        {
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
        catch
        {
            /* ignore */
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
            catch { /* ignore */ }
        }

        string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrEmpty(docs) && Directory.Exists(docs))
            return Path.GetFullPath(docs);
        return Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
    }

    private static string? GetOpenFileNameCommDlg(string? filter, string? title, string? initialDir)
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
                hwndOwner = IntPtr.Zero,
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
                return null;

            string? s = Marshal.PtrToStringUni(pFile);
            if (string.IsNullOrEmpty(s)) return null;
            int z = s.IndexOf('\0', StringComparison.Ordinal);
            if (z >= 0) s = s.Substring(0, z);
            s = s.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally
        {
            Marshal.FreeHGlobal(pFile);
        }
    }

    private static string? GetSaveFileNameCommDlg(string? filter, string? title, string? initialDir, string? suggestedFileName, string? defExt)
    {
        string filterNative = BuildCommDlgFilterString(filter);
        string initDir = ResolveCommDlgInitialDir(initialDir);

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
                hwndOwner = IntPtr.Zero,
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
                return null;

            string? s = Marshal.PtrToStringUni(pFile);
            if (string.IsNullOrEmpty(s)) return null;
            int z = s.IndexOf('\0', StringComparison.Ordinal);
            if (z >= 0) s = s.Substring(0, z);
            s = s.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        finally
        {
            Marshal.FreeHGlobal(pFile);
        }
    }
}
