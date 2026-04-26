using lunagalLauncher.Data;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace lunagalLauncher.Infrastructure
{
    /// <summary>
    /// 日志管理器
    /// Logger manager for initializing and configuring Serilog
    /// </summary>
    public static class LoggerManager
    {
        private static readonly object LogLifecycleLock = new();

        /// <summary>
        /// Serilog 按日滚动时的路径模板（含 lunagalLauncher-.log，实际文件名为 lunagalLauncher-yyyyMMdd.log）
        /// </summary>
        private static string? _logFilePath;

        /// <summary>
        /// 日志根目录（与主程序同目录下的 Logs 文件夹）
        /// </summary>
        public static string GetLogsDirectory()
        {
            string baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string logFolder = Path.Combine(baseDir, "Logs");
            if (!Directory.Exists(logFolder))
                Directory.CreateDirectory(logFolder);
            return logFolder;
        }

        /// <summary>
        /// 当日滚动日志文件的完整路径（与 Serilog RollingInterval.Day 生成规则一致）
        /// </summary>
        public static string GetTodayLogFilePath()
        {
            string date = DateTime.Now.ToString("yyyyMMdd");
            return Path.Combine(GetLogsDirectory(), $"lunagalLauncher-{date}.log");
        }

        /// <summary>
        /// 初始化日志系统
        /// Initializes the logging system with Serilog
        /// </summary>
        public static void Initialize()
        {
            lock (LogLifecycleLock)
            {
                try
                {
                    string logFolder = GetLogsDirectory();
                    _logFilePath = Path.Combine(logFolder, "lunagalLauncher-.log");
                    Log.Logger = BuildLogger();

                    Log.Information("========================================");
                    Log.Information("lunagalLauncher 启动");
                    Log.Information("日志系统初始化成功");
                    Log.Information("日志文件路径: {LogFilePath}", _logFilePath);
                    Log.Information("========================================");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"日志系统初始化失败: {ex.Message}");
                    Console.WriteLine(ex.StackTrace);
                }
            }
        }

        /// <summary>
        /// 删除当日滚动日志文件并重新创建 Serilog。须先 CloseAndFlush 以释放 Async/File 句柄，否则无法删除文件。
        /// </summary>
        public static bool TryTruncateTodayLogAndRecreateLogger()
        {
            lock (LogLifecycleLock)
            {
                bool diskOk = false;
                try
                {
                    try
                    {
                        Log.CloseAndFlush();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"清空日志：CloseAndFlush 异常（继续尝试删文件）: {ex.Message}");
                    }

                    var todayPath = GetTodayLogFilePath();
                    try
                    {
                        if (File.Exists(todayPath))
                            File.Delete(todayPath);
                        diskOk = true;
                    }
                    catch
                    {
                        try
                        {
                            using (var fs = new FileStream(todayPath, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                            }
                            diskOk = true;
                        }
                        catch
                        {
                            diskOk = false;
                        }
                    }

                    string logFolder = GetLogsDirectory();
                    _logFilePath = Path.Combine(logFolder, "lunagalLauncher-.log");
                    Log.Logger = BuildLogger();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"清空日志并重建 Logger 失败: {ex.Message}");
                    try
                    {
                        string logFolder = GetLogsDirectory();
                        _logFilePath = Path.Combine(logFolder, "lunagalLauncher-.log");
                        Log.Logger = BuildLogger();
                    }
                    catch
                    {
                        // ignored
                    }
                    return false;
                }

                return diskOk;
            }
        }

        private static Logger BuildLogger()
        {
            if (string.IsNullOrEmpty(_logFilePath))
            {
                string logFolder = GetLogsDirectory();
                _logFilePath = Path.Combine(logFolder, "lunagalLauncher-.log");
            }

            return new LoggerConfiguration()
                .MinimumLevel.Debug()
                .MinimumLevel.Override("Microsoft", LogEventLevel.Information)
                .MinimumLevel.Override("System", LogEventLevel.Warning)
                .WriteTo.Async(
                    configure: wt => wt.File(
                        path: _logFilePath!,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        shared: true,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                    ),
                    bufferSize: 10_000,
                    blockWhenFull: false
                )
#if DEBUG
                .WriteTo.Debug(
                    outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                )
#endif
                .CreateLogger();
        }

        /// <summary>
        /// 关闭日志系统
        /// Closes the logging system and flushes all pending log entries
        /// </summary>
        public static void Shutdown()
        {
            lock (LogLifecycleLock)
            {
                try
                {
                    Log.Information("========================================");
                    Log.Information("lunagalLauncher 关闭");
                    Log.Information("日志系统正在关闭...");
                    Log.Information("========================================");

                    Log.CloseAndFlush();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"日志系统关闭失败: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// 获取 Serilog 配置的滚动路径模板（lunagalLauncher-.log）
        /// </summary>
        public static string? GetLogFilePath()
        {
            return _logFilePath;
        }

        /// <summary>
        /// 记录应用程序启动信息
        /// Logs application startup information
        /// </summary>
        public static void LogStartupInfo()
        {
            try
            {
                Log.Information("应用程序信息:");
                Log.Information("  - 版本: {Version}", GetAppVersion());
                Log.Information("  - 操作系统: {OS}", Environment.OSVersion);
                Log.Information("  - .NET 版本: {DotNetVersion}", Environment.Version);
                Log.Information("  - 工作目录: {WorkingDirectory}", Environment.CurrentDirectory);
                Log.Information("  - 配置模式: {Mode}", AppStoragePaths.IsPortableMode ? "便携（exe 旁 setting\\config.json）" : "安装型（%APPDATA%\\lunagalLauncher）");
                Log.Information("  - 用户名: {UserName}", Environment.UserName);
                Log.Information("  - 机器名: {MachineName}", Environment.MachineName);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "记录启动信息失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 获取应用程序版本
        /// Gets the application version
        /// </summary>
        /// <returns>应用程序版本字符串 / Application version string</returns>
        private static string GetAppVersion()
        {
            try
            {
                var assembly = System.Reflection.Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return version?.ToString() ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }

        /// <summary>
        /// 记录异常信息（带上下文）
        /// Logs exception with context
        /// </summary>
        /// <param name="ex">异常对象 / Exception object</param>
        /// <param name="context">上下文信息 / Context information</param>
        public static void LogException(Exception ex, string context)
        {
            Log.Error(ex, "异常发生在 {Context}: {Message}", context, ex.Message);
            Log.Error("异常类型: {ExceptionType}", ex.GetType().Name);
            Log.Error("堆栈跟踪: {StackTrace}", ex.StackTrace);

            // 如果有内部异常，也记录下来
            // If there's an inner exception, log it too
            if (ex.InnerException != null)
            {
                Log.Error("内部异常: {InnerException}", ex.InnerException.Message);
                Log.Error("内部异常堆栈: {InnerStackTrace}", ex.InnerException.StackTrace);
            }
        }
    }
}
