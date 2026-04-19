using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using lunagalLauncher.Data;
using Serilog;

namespace lunagalLauncher.Core
{
    /// <summary>
    /// 应用启动状态枚举
    /// Application launch status enumeration
    /// </summary>
    public enum LaunchStatus
    {
        /// <summary>未启动 / Not launched</summary>
        NotLaunched,
        /// <summary>正在启动 / Launching</summary>
        Launching,
        /// <summary>已启动 / Launched</summary>
        Launched,
        /// <summary>启动失败 / Launch failed</summary>
        Failed
    }

    /// <summary>
    /// 应用启动结果
    /// Application launch result
    /// </summary>
    public class LaunchResult
    {
        /// <summary>应用配置 / Application configuration</summary>
        public AppConfig AppConfig { get; set; } = null!;

        /// <summary>启动状态 / Launch status</summary>
        public LaunchStatus Status { get; set; }

        /// <summary>进程对象 / Process object</summary>
        public Process? Process { get; set; }

        /// <summary>错误消息 / Error message</summary>
        public string? ErrorMessage { get; set; }
    }

    /// <summary>
    /// 应用启动管理器
    /// Application launch manager - manages launching and tracking of applications
    /// </summary>
    public class LaunchManager
    {
        /// <summary>
        /// 已启动的应用进程字典（应用ID -> 进程对象）
        /// Dictionary of launched application processes (App ID -> Process)
        /// </summary>
        private readonly Dictionary<string, Process> _launchedProcesses;

        /// <summary>
        /// 启动结果列表
        /// List of launch results
        /// </summary>
        private readonly List<LaunchResult> _launchResults;

        /// <summary>
        /// 线程锁对象
        /// Thread lock object for synchronization
        /// </summary>
        private readonly object _lockObject = new object();

        /// <summary>
        /// 启动状态改变事件
        /// Event fired when launch status changes
        /// </summary>
        public event EventHandler<LaunchResult>? LaunchStatusChanged;

        /// <summary>
        /// 进程退出事件
        /// Event fired when a process exits
        /// </summary>
        public event EventHandler<(string AppId, int ExitCode)>? ProcessExited;

        /// <summary>
        /// 构造函数
        /// Constructor - initializes the launch manager
        /// </summary>
        public LaunchManager()
        {
            _launchedProcesses = new Dictionary<string, Process>();
            _launchResults = new List<LaunchResult>();
            Log.Information("LaunchManager 已初始化");
        }

        /// <summary>
        /// 启动单个应用程序
        /// Launches a single application
        /// </summary>
        /// <param name="appConfig">应用配置 / Application configuration</param>
        /// <param name="minimized">是否最小化启动 / Whether to launch minimized</param>
        /// <returns>启动结果 / Launch result</returns>
        public async Task<LaunchResult> LaunchApplicationAsync(AppConfig appConfig, bool minimized = false)
        {
            Log.Information("正在启动应用: {AppName} (ID: {AppId})", appConfig.Name, appConfig.Id);

            // 创建启动结果对象
            // Create launch result object
            var result = new LaunchResult
            {
                AppConfig = appConfig,
                Status = LaunchStatus.Launching
            };

            // 触发状态改变事件
            // Fire status changed event
            LaunchStatusChanged?.Invoke(this, result);

            try
            {
                // 验证应用路径是否存在
                // Validate application path exists
                if (string.IsNullOrWhiteSpace(appConfig.Path))
                {
                    throw new ArgumentException("应用程序路径不能为空");
                }

                if (!System.IO.File.Exists(appConfig.Path))
                {
                    throw new System.IO.FileNotFoundException($"应用程序文件不存在: {appConfig.Path}");
                }

                // 配置进程启动信息
                // Configure process start info
                var startInfo = new ProcessStartInfo
                {
                    FileName = appConfig.Path,
                    UseShellExecute = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(appConfig.Path) ?? string.Empty,
                    // 尝试以管理员权限启动（如果需要）
                    // Try to launch with admin privileges if needed
                    Verb = "runas"
                };

                // 设置窗口样式（最小化或正常）
                // Set window style (minimized or normal)
                if (minimized)
                {
                    startInfo.WindowStyle = ProcessWindowStyle.Minimized;
                    Log.Information("应用将以最小化模式启动");
                }

                // 启动进程
                // Start process
                Log.Information("正在启动进程: {FileName}", startInfo.FileName);
                Process? process = null;
                
                try
                {
                    process = Process.Start(startInfo);
                }
                catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    // 用户取消了 UAC 提示
                    // User cancelled UAC prompt
                    throw new InvalidOperationException("用户取消了管理员权限请求");
                }

                if (process == null)
                {
                    throw new InvalidOperationException("进程启动失败，返回 null");
                }

                // 等待进程初始化（增加等待时间）
                // Wait for process initialization (increased wait time)
                await Task.Delay(1500);

                // 检查进程是否已退出
                // Check if process has exited
                if (process.HasExited)
                {
                    // 某些应用（如 Magpie）可能会启动后立即退出，但实际上已经在后台运行
                    // 我们不将退出代码 0 视为错误
                    // Some apps (like Magpie) may exit immediately but are actually running in background
                    // We don't treat exit code 0 as an error
                    if (process.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"进程启动后立即退出，退出代码: {process.ExitCode}");
                    }
                    else
                    {
                        Log.Information("进程正常退出（退出代码 0），可能已在后台运行");
                        // 不抛出异常，将其视为成功启动
                        // Don't throw exception, treat as successful launch
                    }
                }

                // 只有进程未退出时才注册事件和保存进程
                // Only register events and save process if it hasn't exited
                if (!process.HasExited)
                {
                    // 注册进程退出事件
                    // Register process exit event
                    process.EnableRaisingEvents = true;
                    process.Exited += (sender, args) => OnProcessExited(appConfig.Id, process.ExitCode);

                    // 保存进程到字典
                    // Save process to dictionary
                    lock (_lockObject)
                    {
                        _launchedProcesses[appConfig.Id] = process;
                    }

                    Log.Information("应用启动成功: {AppName} (PID: {ProcessId})", appConfig.Name, process.Id);
                }
                else
                {
                    Log.Information("应用启动成功: {AppName} (进程已退出，可能在后台运行)", appConfig.Name);
                }

                // 更新启动结果
                // Update launch result
                result.Status = LaunchStatus.Launched;
                result.Process = process;
            }
            catch (Exception ex)
            {
                // 记录启动失败
                // Log launch failure
                Log.Error(ex, "应用启动失败: {AppName} - {Message}", appConfig.Name, ex.Message);

                result.Status = LaunchStatus.Failed;
                result.ErrorMessage = ex.Message;
            }

            // 保存启动结果
            // Save launch result
            lock (_lockObject)
            {
                _launchResults.Add(result);
            }

            // 触发状态改变事件
            // Fire status changed event
            LaunchStatusChanged?.Invoke(this, result);

            return result;
        }

        /// <summary>
        /// 启动所有已启用的应用程序
        /// Launches all enabled applications
        /// </summary>
        /// <param name="apps">应用配置列表 / List of application configurations</param>
        /// <param name="minimized">是否最小化启动 / Whether to launch minimized</param>
        /// <returns>启动结果列表 / List of launch results</returns>
        public async Task<List<LaunchResult>> LaunchAllApplicationsAsync(List<AppConfig> apps, bool minimized = false)
        {
            Log.Information("开始批量启动应用，共 {Count} 个应用", apps.Count);

            var results = new List<LaunchResult>();

            // 筛选已启用的应用
            // Filter enabled applications
            var enabledApps = apps.Where(app => app.Enabled).ToList();
            Log.Information("已启用的应用数量: {Count}", enabledApps.Count);

            // 逐个启动应用
            // Launch applications one by one
            foreach (var app in enabledApps)
            {
                var result = await LaunchApplicationAsync(app, minimized);
                results.Add(result);

                // 在启动之间添加短暂延迟，避免系统负载过高
                // Add short delay between launches to avoid system overload
                await Task.Delay(300);
            }

            // 统计启动结果
            // Count launch results
            var successCount = results.Count(r => r.Status == LaunchStatus.Launched);
            var failedCount = results.Count(r => r.Status == LaunchStatus.Failed);

            Log.Information("批量启动完成 - 成功: {Success}, 失败: {Failed}", successCount, failedCount);

            return results;
        }

        /// <summary>
        /// 关闭指定应用程序
        /// Closes a specific application
        /// </summary>
        /// <param name="appId">应用ID / Application ID</param>
        /// <param name="force">是否强制关闭 / Whether to force close</param>
        /// <returns>是否成功关闭 / Whether successfully closed</returns>
        public bool CloseApplication(string appId, bool force = false)
        {
            Log.Information("正在关闭应用: {AppId} (强制: {Force})", appId, force);

            lock (_lockObject)
            {
                if (!_launchedProcesses.TryGetValue(appId, out var process))
                {
                    Log.Warning("未找到应用进程: {AppId}", appId);
                    return false;
                }

                try
                {
                    // 检查进程是否已退出
                    // Check if process has already exited
                    if (process.HasExited)
                    {
                        Log.Information("进程已退出: {AppId}", appId);
                        _launchedProcesses.Remove(appId);
                        return true;
                    }

                    // 尝试优雅关闭或强制终止
                    // Try graceful close or force kill
                    if (force)
                    {
                        Log.Information("强制终止进程: {AppId} (PID: {ProcessId})", appId, process.Id);
                        process.Kill();
                    }
                    else
                    {
                        Log.Information("请求关闭进程: {AppId} (PID: {ProcessId})", appId, process.Id);
                        process.CloseMainWindow();
                    }

                    // 等待进程退出
                    // Wait for process to exit
                    bool exited = process.WaitForExit(5000);

                    if (exited)
                    {
                        Log.Information("进程已成功关闭: {AppId}", appId);
                        _launchedProcesses.Remove(appId);
                        return true;
                    }
                    else
                    {
                        Log.Warning("进程未在超时时间内退出: {AppId}", appId);
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "关闭应用失败: {AppId} - {Message}", appId, ex.Message);
                    return false;
                }
            }
        }

        /// <summary>
        /// 关闭所有已启动的应用程序
        /// Closes all launched applications
        /// </summary>
        /// <param name="force">是否强制关闭 / Whether to force close</param>
        /// <returns>成功关闭的应用数量 / Number of successfully closed applications</returns>
        public int CloseAllApplications(bool force = false)
        {
            Log.Information("正在关闭所有应用 (强制: {Force})", force);

            int closedCount = 0;

            // 获取所有应用ID的副本
            // Get copy of all app IDs
            List<string> appIds;
            lock (_lockObject)
            {
                appIds = _launchedProcesses.Keys.ToList();
            }

            // 逐个关闭应用
            // Close applications one by one
            foreach (var appId in appIds)
            {
                if (CloseApplication(appId, force))
                {
                    closedCount++;
                }
            }

            Log.Information("已关闭 {Count} 个应用", closedCount);
            return closedCount;
        }

        /// <summary>
        /// 获取正在运行的应用数量
        /// Gets the count of running applications
        /// </summary>
        /// <returns>运行中的应用数量 / Count of running applications</returns>
        public int GetRunningApplicationCount()
        {
            lock (_lockObject)
            {
                // 清理已退出的进程
                // Clean up exited processes
                var exitedAppIds = _launchedProcesses
                    .Where(kvp => kvp.Value.HasExited)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var appId in exitedAppIds)
                {
                    _launchedProcesses.Remove(appId);
                }

                return _launchedProcesses.Count;
            }
        }

        /// <summary>
        /// 检查指定应用是否正在运行
        /// Checks if a specific application is running
        /// </summary>
        /// <param name="appId">应用ID / Application ID</param>
        /// <returns>是否正在运行 / Whether the application is running</returns>
        public bool IsApplicationRunning(string appId)
        {
            lock (_lockObject)
            {
                if (_launchedProcesses.TryGetValue(appId, out var process))
                {
                    if (!process.HasExited)
                    {
                        return true;
                    }
                    else
                    {
                        // 清理已退出的进程
                        // Clean up exited process
                        _launchedProcesses.Remove(appId);
                    }
                }
                return false;
            }
        }

        /// <summary>
        /// 获取所有启动结果
        /// Gets all launch results
        /// </summary>
        /// <returns>启动结果列表 / List of launch results</returns>
        public List<LaunchResult> GetLaunchResults()
        {
            lock (_lockObject)
            {
                return new List<LaunchResult>(_launchResults);
            }
        }

        /// <summary>
        /// 清除启动结果历史
        /// Clears launch result history
        /// </summary>
        public void ClearLaunchResults()
        {
            lock (_lockObject)
            {
                _launchResults.Clear();
                Log.Information("已清除启动结果历史");
            }
        }

        /// <summary>
        /// 进程退出事件处理
        /// Process exit event handler
        /// </summary>
        /// <param name="appId">应用ID / Application ID</param>
        /// <param name="exitCode">退出代码 / Exit code</param>
        private void OnProcessExited(string appId, int exitCode)
        {
            Log.Information("进程已退出: {AppId}, 退出代码: {ExitCode}", appId, exitCode);

            // 从字典中移除
            // Remove from dictionary
            lock (_lockObject)
            {
                _launchedProcesses.Remove(appId);
            }

            // 触发进程退出事件
            // Fire process exited event
            ProcessExited?.Invoke(this, (appId, exitCode));
        }

        /// <summary>
        /// 释放资源
        /// Disposes resources
        /// </summary>
        public void Dispose()
        {
            Log.Information("正在释放 LaunchManager 资源...");

            // 关闭所有应用
            // Close all applications
            CloseAllApplications(force: false);

            // 清理进程对象
            // Clean up process objects
            lock (_lockObject)
            {
                foreach (var process in _launchedProcesses.Values)
                {
                    try
                    {
                        process.Dispose();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "释放进程对象失败: {Message}", ex.Message);
                    }
                }
                _launchedProcesses.Clear();
            }

            Log.Information("LaunchManager 资源已释放");
        }
    }
}
