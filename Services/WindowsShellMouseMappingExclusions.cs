namespace lunagalLauncher.Services
{
    /// <summary>
    /// 默认不应用鼠标映射的 Windows 内置壳层进程（仅按 exe 文件名匹配，与名单过滤一致）。
    /// </summary>
    internal static class WindowsShellMouseMappingExclusions
    {
        private static readonly HashSet<string> s_exeFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "explorer.exe", // 资源管理器、此电脑、桌面、文件夹
            "ShellExperienceHost.exe",
            "StartMenuExperienceHost.exe",
            "SearchHost.exe", // Win11 搜索
            "SearchApp.exe", // Win10 搜索
            "SystemSettings.exe",
            "Microsoft.Photos.exe",
            "Photos.exe",
        };

        internal static bool IsWindowsShellUiProcess(string? exeFullPath)
        {
            if (string.IsNullOrEmpty(exeFullPath)) return false;
            return s_exeFileNames.Contains(Path.GetFileName(exeFullPath));
        }
    }
}
