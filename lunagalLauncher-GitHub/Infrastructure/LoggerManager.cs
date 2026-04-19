using System;
using System.IO;
using Serilog;
using Serilog.Events;

namespace lunagalLauncher.Infrastructure
{
    /// <summary>
    /// 日志管理器
    /// Logger manager for initializing and configuring Serilog
    /// </summary>
    public static class LoggerManager
    {
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
            try
            {
                string logFolder = GetLogsDirectory();

                // 设置日志文件路径（按日期滚动）
                // Set log file path (rolling by date)
                _logFilePath = Path.Combine(logFolder, "lunagalLauncher-.log");

                // 配置 Serilog
                // Configure Serilog
                Log.Logger = new LoggerConfiguration()
                    // 设置最小日志级别为 Debug
                    // Set minimum log level to Debug
                    .MinimumLevel.Debug()

                    // 覆盖 Microsoft 命名空间的日志级别为 Information（减少噪音）
                    // Override Microsoft namespace log level to Information (reduce noise)
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Information)

                    // 覆盖 System 命名空间的日志级别为 Warning（减少噪音）
                    // Override System namespace log level to Warning (reduce noise)
                    .MinimumLevel.Override("System", LogEventLevel.Warning)

                    // 添加文件输出（按日期滚动，保留 30 天）
                    // Add file output (rolling by date, retain 30 days)
                    .WriteTo.File(
                        path: _logFilePath,
                        rollingInterval: RollingInterval.Day,
                        retainedFileCountLimit: 30,
                        outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                    )

                    // 添加调试输出（仅在 Debug 模式下）
                    // Add debug output (only in Debug mode)
#if DEBUG
                    .WriteTo.Debug(
                        outputTemplate: "{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}"
                    )
#endif

                    // 创建日志记录器
                    // Create logger
                    .CreateLogger();

                // 记录初始化成功
                // Log initialization success
                Log.Information("========================================");
                Log.Information("lunagalLauncher 启动");
                Log.Information("日志系统初始化成功");
                Log.Information("日志文件路径: {LogFilePath}", _logFilePath);
                Log.Information("========================================");
            }
            catch (Exception ex)
            {
                // 如果日志初始化失败，至少尝试输出到控制台
                // If log initialization fails, at least try to output to console
                Console.WriteLine($"日志系统初始化失败: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }

        /// <summary>
        /// 关闭日志系统
        /// Closes the logging system and flushes all pending log entries
        /// </summary>
        public static void Shutdown()
        {
            try
            {
                Log.Information("========================================");
                Log.Information("lunagalLauncher 关闭");
                Log.Information("日志系统正在关闭...");
                Log.Information("========================================");

                // 刷新并关闭所有日志输出
                // Flush and close all log outputs
                Log.CloseAndFlush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"日志系统关闭失败: {ex.Message}");
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
