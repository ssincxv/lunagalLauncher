using System;
using System.IO;
using Serilog;

namespace lunagalLauncher.Data
{
    /// <summary>
    /// 解析配置根目录：默认 %APPDATA%\lunagalLauncher；exe 旁存在便携标记文件时使用 setting\config.json。
    /// </summary>
    public static class AppStoragePaths
    {
        /// <summary>与主程序同目录、放置空文件即启用便携模式（无扩展名）。</summary>
        public const string PortableMarkerFileName = "portable";

        /// <summary>可选：便携标记为 portable.txt 亦可识别。</summary>
        public const string PortableMarkerAlternateFileName = "portable.txt";

        private static string AppBaseDirectory =>
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        /// <summary>是否在便携模式下运行。</summary>
        public static bool IsPortableMode =>
            File.Exists(Path.Combine(AppBaseDirectory, PortableMarkerFileName)) ||
            File.Exists(Path.Combine(AppBaseDirectory, PortableMarkerAlternateFileName));

        /// <summary>配置目录（已确保存在）。</summary>
        public static string GetConfigDirectory()
        {
            string dir = IsPortableMode
                ? Path.Combine(AppBaseDirectory, "setting")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "lunagalLauncher");

            if (!Directory.Exists(dir))
            {
                Log.Information("创建配置目录: {Dir}", dir);
                Directory.CreateDirectory(dir);
            }

            return dir;
        }

        /// <summary>主配置文件完整路径。</summary>
        public static string GetConfigFilePath() => Path.Combine(GetConfigDirectory(), "config.json");

        /// <summary>
        /// 首次启用便携且 setting 下尚无 config.json 时，从 Roaming 复制一份（可选迁移）。
        /// </summary>
        public static void TryMigrateRoamingConfigIntoPortable(string portableConfigPath)
        {
            if (!IsPortableMode || File.Exists(portableConfigPath))
            {
                return;
            }

            try
            {
                string roaming = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "lunagalLauncher",
                    "config.json");

                if (!File.Exists(roaming))
                {
                    return;
                }

                File.Copy(roaming, portableConfigPath);
                Log.Information("便携模式：已从 Roaming 复制 config.json 到 {Path}", portableConfigPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "便携模式：从 Roaming 迁移 config.json 失败（可忽略）");
            }
        }
    }
}
