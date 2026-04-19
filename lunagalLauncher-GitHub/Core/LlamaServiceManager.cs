using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Serilog;

namespace lunagalLauncher.Core
{
    /// <summary>
    /// llama 服务状态枚举
    /// llama service status enumeration
    /// </summary>
    public enum LlamaServiceStatus
    {
        /// <summary>未启动 / Not started</summary>
        NotStarted,
        /// <summary>正在启动 / Starting</summary>
        Starting,
        /// <summary>运行中 / Running</summary>
        Running,
        /// <summary>正在停止 / Stopping</summary>
        Stopping,
        /// <summary>已停止 / Stopped</summary>
        Stopped,
        /// <summary>错误 / Error</summary>
        Error
    }

    /// <summary>
    /// IPC 消息类型枚举
    /// IPC message type enumeration
    /// </summary>
    public enum IpcMessageType
    {
        /// <summary>心跳 / Heartbeat</summary>
        Heartbeat,
        /// <summary>命令 / Command</summary>
        Command,
        /// <summary>响应 / Response</summary>
        Response,
        /// <summary>通知 / Notification</summary>
        Notification,
        /// <summary>错误 / Error</summary>
        Error
    }

    /// <summary>
    /// IPC 消息
    /// IPC message for communication between launcher and llama service
    /// </summary>
    public class IpcMessage
    {
        /// <summary>消息类型 / Message type</summary>
        public IpcMessageType Type { get; set; }

        /// <summary>消息内容 / Message content</summary>
        public string Content { get; set; } = string.Empty;

        /// <summary>时间戳 / Timestamp</summary>
        public DateTime Timestamp { get; set; } = DateTime.Now;

        /// <summary>消息ID / Message ID</summary>
        public string MessageId { get; set; } = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// llama 服务管理器
    /// llama service manager - manages llama service process and IPC communication
    /// </summary>
    public class LlamaServiceManager : IDisposable
    {
        /// <summary>
        /// 命名管道名称
        /// Named pipe name for IPC communication
        /// </summary>
        private const string PIPE_NAME = "lunagalLauncher_LlamaService";

        /// <summary>
        /// 心跳间隔（毫秒）
        /// Heartbeat interval in milliseconds
        /// </summary>
        private const int HEARTBEAT_INTERVAL = 5000;

        /// <summary>
        /// 心跳超时（毫秒）
        /// Heartbeat timeout in milliseconds
        /// </summary>
        private const int HEARTBEAT_TIMEOUT = 15000;

        /// <summary>
        /// 就绪探测最大等待时间（毫秒）
        /// 大模型（4B/7B）在低速磁盘上加载 + CUDA 预热 可能长达 2 分钟
        /// Max readiness wait in ms. Large (4B/7B) models on slow disks plus
        /// CUDA warm-up can take up to ~2 minutes before llama-server's HTTP
        /// listener actually accepts connections.
        /// </summary>
        private const int READY_PROBE_TIMEOUT_MS = 120_000;

        /// <summary>
        /// 就绪探测轮询间隔（毫秒）
        /// Polling interval (ms) used while waiting for HTTP readiness.
        /// </summary>
        private const int READY_PROBE_INTERVAL_MS = 300;

        /// <summary>
        /// 共享 HttpClient（避免频繁创建导致 socket 耗尽）
        /// Shared HttpClient instance; reused for every HTTP readiness probe
        /// so we don't exhaust local sockets during the polling loop.
        /// </summary>
        private static readonly HttpClient _readinessClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(2) // 单次探测超时
        };

        /// <summary>
        /// 全局单例实例（由 MainPage「一键开启」和 LlamaServicePage 共享）
        /// Global singleton. Shared between the one-click launcher on MainPage
        /// and the management UI on LlamaServicePage so both see the same
        /// process / status / event stream.
        /// 注意：保留公开构造函数以兼容已存在的 `new LlamaServiceManager()` 调用，
        /// 但推荐所有新代码通过 Instance 访问。
        /// </summary>
        private static readonly Lazy<LlamaServiceManager> _instance =
            new Lazy<LlamaServiceManager>(() => new LlamaServiceManager(), isThreadSafe: true);

        /// <summary>
        /// 单例访问器
        /// Singleton accessor used by any caller that needs to observe /
        /// control the llama service without passing the instance around.
        /// </summary>
        public static LlamaServiceManager Instance => _instance.Value;

        /// <summary>
        /// llama 服务进程
        /// llama service process
        /// </summary>
        private Process? _serviceProcess;

        /// <summary>
        /// 命名管道服务器
        /// Named pipe server for IPC
        /// </summary>
        private NamedPipeServerStream? _pipeServer;

        /// <summary>
        /// 当前服务状态
        /// Current service status
        /// </summary>
        private LlamaServiceStatus _status = LlamaServiceStatus.NotStarted;

        /// <summary>
        /// 心跳定时器
        /// Heartbeat timer
        /// </summary>
        private Timer? _heartbeatTimer;

        /// <summary>
        /// 最后一次心跳时间
        /// Last heartbeat time
        /// </summary>
        private DateTime _lastHeartbeat = DateTime.MinValue;

        /// <summary>
        /// 取消令牌源
        /// Cancellation token source
        /// </summary>
        private CancellationTokenSource? _cancellationTokenSource;

        /// <summary>
        /// 线程锁对象
        /// Thread lock object for synchronization
        /// </summary>
        private readonly object _lockObject = new object();

        /// <summary>
        /// 服务状态改变事件
        /// Event fired when service status changes
        /// </summary>
        public event EventHandler<LlamaServiceStatus>? StatusChanged;

        /// <summary>
        /// 参数输出数据接收事件
        /// Event fired when parameter output data is received
        /// </summary>
        public event EventHandler<string>? ParamsOutputDataReceived;

        /// <summary>
        /// 当前服务状态
        /// Current service status
        /// </summary>
        public LlamaServiceStatus Status
        {
            get
            {
                lock (_lockObject)
                {
                    return _status;
                }
            }
            private set
            {
                lock (_lockObject)
                {
                    if (_status != value)
                    {
                        _status = value;
                        Log.Information("llama 服务状态改变: {Status}", value);
                        StatusChanged?.Invoke(this, value);
                    }
                }
            }
        }

        /// <summary>
        /// 构造函数
        /// Constructor - initializes the llama service manager
        /// </summary>
        public LlamaServiceManager()
        {
            Log.Information("LlamaServiceManager 已初始化");
        }

        /// <summary>
        /// 构建 llama-server 命令行参数
        /// Builds llama-server command line arguments
        /// </summary>
        /// <param name="config">llama 服务配置 / llama service configuration</param>
        /// <returns>命令行参数字符串 / Command line arguments string</returns>
        public string BuildCommandLineArguments(Data.LlamaServiceConfig config)
        {
            // 如果有自定义完整命令，直接返回
            // If custom full command exists, return it directly
            if (!string.IsNullOrWhiteSpace(config.CustomCommand))
            {
                Log.Information("使用自定义完整命令");
                return config.CustomCommand;
            }

            var args = new System.Text.StringBuilder();

            // 模型路径 (必需)
            // Model path (required)
            if (!string.IsNullOrWhiteSpace(config.ModelPath))
            {
                args.Append($" --model \"{config.ModelPath}\"");
            }

            // 主机地址
            // Host address
            args.Append($" --host {config.Host}");

            // 端口
            // Port
            args.Append($" --port {config.Port}");

            // GPU 层数
            // GPU layers
            args.Append($" -ngl {config.GpuLayers}");

            // 上下文长度
            // Context length
            args.Append($" -c {config.ContextLength}");

            // 并行线程数
            // Parallel threads
            args.Append($" -np {config.ParallelThreads}");

            // 模型别名（使用模型文件名）
            // Model alias (use model file name)
            if (!string.IsNullOrWhiteSpace(config.ModelPath))
            {
                string modelName = System.IO.Path.GetFileNameWithoutExtension(config.ModelPath);
                args.Append($" -a \"{modelName}\"");
            }

            // Flash Attention
            if (config.FlashAttention)
            {
                args.Append(" -fa");
            }

            // No Mmap
            if (config.NoMmap)
            {
                args.Append(" --no-mmap");
            }

            // 日志格式
            // Log format
            if (!string.IsNullOrWhiteSpace(config.LogFormat) && config.LogFormat != "none")
            {
                args.Append($" --log-format {config.LogFormat}");
            }

            // 自定义追加命令
            // Custom append command
            if (!string.IsNullOrWhiteSpace(config.CustomCommandAppend))
            {
                args.Append($" {config.CustomCommandAppend}");
            }

            string result = args.ToString().Trim();
            Log.Information("构建的命令行参数: {Arguments}", result);
            return result;
        }

        /// <summary>
        /// 启动 llama 服务（使用配置对象）
        /// Starts the llama service with configuration object
        /// </summary>
        /// <param name="config">llama 服务配置 / llama service configuration</param>
        /// <param name="gpuDetector">GPU 检测器（可选）/ GPU detector (optional)</param>
        /// <returns>是否启动成功 / Whether start was successful</returns>
        public async Task<bool> StartServiceAsync(Data.LlamaServiceConfig config, GpuDetector? gpuDetector = null)
        {
            Log.Information("正在启动 llama 服务...");

            try
            {
                // 检查服务是否已在运行
                // Check if service is already running
                if (Status == LlamaServiceStatus.Running)
                {
                    Log.Warning("llama 服务已在运行");
                    return true;
                }

                // 验证配置
                // Validate configuration
                if (string.IsNullOrWhiteSpace(config.ServicePath))
                {
                    throw new ArgumentException("服务路径不能为空");
                }

                if (!File.Exists(config.ServicePath))
                {
                    throw new FileNotFoundException($"服务文件不存在: {config.ServicePath}");
                }

                if (string.IsNullOrWhiteSpace(config.ModelPath))
                {
                    throw new ArgumentException("模型路径不能为空");
                }

                if (!File.Exists(config.ModelPath))
                {
                    throw new FileNotFoundException($"模型文件不存在: {config.ModelPath}");
                }

                // 设置 GPU 环境变量
                // Set GPU environment variables
                if (config.SingleGpuMode && gpuDetector != null)
                {
                    int gpuIndex = config.SelectedGpuIndex;

                    // 如果有手动指定的 GPU 索引，使用手动指定的
                    // If manual GPU index is specified, use it
                    if (!string.IsNullOrWhiteSpace(config.ManualGpuIndex) && 
                        int.TryParse(config.ManualGpuIndex, out int manualIndex))
                    {
                        gpuIndex = manualIndex;
                        Log.Information("使用手动指定的 GPU 索引: {Index}", gpuIndex);
                    }

                    gpuDetector.SetGpuEnvironment(gpuIndex);
                }

                // 构建命令行参数
                // Build command line arguments
                string arguments = BuildCommandLineArguments(config);

                // 更新状态
                // Update status
                Status = LlamaServiceStatus.Starting;

                // 创建取消令牌
                // Create cancellation token
                _cancellationTokenSource = new CancellationTokenSource();

                // 配置进程启动信息
                // Configure process start info
                var startInfo = new ProcessStartInfo
                {
                    FileName = config.ServicePath,
                    Arguments = arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(config.ServicePath) ?? string.Empty
                };

                // 启动进程
                // Start process
                Log.Information("正在启动 llama 服务进程...");
                Log.Information("命令: {FileName} {Arguments}", startInfo.FileName, startInfo.Arguments);
                
                _serviceProcess = Process.Start(startInfo);

                if (_serviceProcess == null)
                {
                    throw new InvalidOperationException("服务进程启动失败，返回 null");
                }

                // 注册进程退出事件
                // Register process exit event
                _serviceProcess.EnableRaisingEvents = true;
                _serviceProcess.Exited += OnServiceProcessExited;

                // 重定向输出到日志
                // Redirect output to log
                _serviceProcess.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Log.Information("[llama-server] {Output}", e.Data);
                        ParamsOutputDataReceived?.Invoke(this, e.Data);
                    }
                };
                _serviceProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Log.Warning("[llama-server] {Error}", e.Data);
                        ParamsOutputDataReceived?.Invoke(this, e.Data);
                    }
                };
                _serviceProcess.BeginOutputReadLine();
                _serviceProcess.BeginErrorReadLine();

                // 等待 HTTP 端点真正可达（TCP 绑定 + 模型加载完成）
                // Actively wait for the llama-server HTTP endpoint to be
                // reachable. The previous fixed `Task.Delay(3000)` lied about
                // readiness for large models (e.g. GalTransl-v4-4B ≈ 4-5s,
                // Sakura-14B 可达 30-60s) and caused downstream consumers
                // (LunaTranslator 等) 在服务尚未 listen 时就发起请求而对接失败。
                Log.Information("等待 llama 服务 HTTP 端点就绪...");
                bool ready = await WaitForHttpReadyAsync(
                    config.Host,
                    config.Port,
                    READY_PROBE_TIMEOUT_MS,
                    _cancellationTokenSource.Token);

                if (!ready)
                {
                    // 超时：进程可能仍在加载，但已超过我们允许的最大时长
                    // Timed out. The process may still be loading the model
                    // but we refuse to report Running unless we can reach
                    // the HTTP listener, to avoid silent downstream failures.
                    throw new TimeoutException(
                        $"llama 服务在 {READY_PROBE_TIMEOUT_MS / 1000} 秒内未开放 HTTP 端点 " +
                        $"http://{config.Host}:{config.Port}/ ，可能模型过大或加载失败");
                }

                // 更新状态
                // Update status
                Status = LlamaServiceStatus.Running;
                _lastHeartbeat = DateTime.Now;

                Log.Information("llama 服务启动成功 (PID: {ProcessId}, 端点: http://{Host}:{Port})",
                    _serviceProcess.Id, config.Host, config.Port);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动 llama 服务失败: {Message}", ex.Message);
                Status = LlamaServiceStatus.Error;

                // 清理资源
                // Clean up resources
                await CleanupAsync();

                return false;
            }
        }

        /// <summary>
        /// 主动轮询 llama-server 的 HTTP 端点，直到返回 2xx/4xx（只要能响应即视为已绑定端口）
        /// Actively polls the llama-server HTTP endpoint until it responds.
        /// A successful TCP connect followed by any HTTP response (even 404)
        /// means the server is accepting traffic; at that point LunaTranslator
        /// / Sakura translator 等客户端可以稳定对接。
        /// </summary>
        /// <param name="host">主机地址 / Bind host (may be 0.0.0.0 - probe loopback in that case)</param>
        /// <param name="port">端口 / TCP port</param>
        /// <param name="timeoutMs">总超时毫秒数 / Total timeout budget in milliseconds</param>
        /// <param name="token">取消令牌 / Cancellation token</param>
        /// <returns>是否在超时前变为就绪 / Whether readiness was confirmed</returns>
        private async Task<bool> WaitForHttpReadyAsync(string host, int port, int timeoutMs, CancellationToken token)
        {
            // 如果服务绑定到 0.0.0.0 / ::，探测应当走 127.0.0.1，避免解析歧义
            // When bound to 0.0.0.0 or :: we must probe via loopback, otherwise
            // the outbound connect would resolve to a wildcard address.
            string probeHost = host;
            if (string.IsNullOrWhiteSpace(probeHost) ||
                probeHost == "0.0.0.0" ||
                probeHost == "::" ||
                probeHost == "*")
            {
                probeHost = "127.0.0.1";
            }

            string modelsUrl = $"http://{probeHost}:{port}/v1/models";
            var sw = Stopwatch.StartNew();
            int attempt = 0;

            while (sw.ElapsedMilliseconds < timeoutMs && !token.IsCancellationRequested)
            {
                attempt++;

                // 早退检查：进程若已退出则无需继续等待
                // Early exit: if the process already died, abandon readiness check.
                if (_serviceProcess != null && _serviceProcess.HasExited)
                {
                    Log.Warning("就绪探测中止：服务进程已退出 (退出代码: {ExitCode})",
                        _serviceProcess.ExitCode);
                    return false;
                }

                // 先做 TCP 探测，比 HTTP 更廉价，失败时绝大多数时间就停在这里
                // TCP probe first — cheaper than HTTP and eliminates the usual
                // "connection refused" period while the server is still loading.
                try
                {
                    using var tcp = new TcpClient();
                    var connectTask = tcp.ConnectAsync(probeHost, port);
                    var winner = await Task.WhenAny(connectTask, Task.Delay(1000, token));
                    if (winner != connectTask || !tcp.Connected)
                    {
                        await DelayBetweenProbesAsync(token);
                        continue;
                    }
                }
                catch
                {
                    await DelayBetweenProbesAsync(token);
                    continue;
                }

                // TCP 通了之后再做一次 HTTP GET /v1/models 以确认 OpenAI 兼容层已就绪
                // Once TCP is up, confirm the OpenAI-compatible layer is alive by
                // actually issuing GET /v1/models. This matches what LunaTranslator
                // does as its first request, so success here guarantees对接可用.
                try
                {
                    using var resp = await _readinessClient.GetAsync(
                        modelsUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        token);
                    // 任意响应都视为就绪：llama-server 在加载期间可能返回 503，
                    // 但一旦进入主循环会立刻变为 200。这里不看 body，只看状态码。
                    // Any response counts as "HTTP layer alive"; llama-server may
                    // return 503 while still warming, so we additionally require
                    // success status to avoid lying to the caller.
                    if (resp.IsSuccessStatusCode)
                    {
                        Log.Information(
                            "✅ llama 服务就绪 (耗时: {Elapsed}ms, 探测次数: {Attempt}, URL: {Url})",
                            sw.ElapsedMilliseconds, attempt, modelsUrl);
                        return true;
                    }
                    Log.Debug("HTTP 探测返回 {Status}，继续等待...", (int)resp.StatusCode);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    // 典型场景：模型仍在加载，HTTP 监听线程还没起来
                    // Typical: model is still loading, HTTP thread not yet spun up.
                    Log.Verbose("HTTP 探测失败 (第 {Attempt} 次): {Message}", attempt, ex.Message);
                }

                await DelayBetweenProbesAsync(token);
            }

            Log.Warning("⏱ 就绪探测超时 ({Timeout}ms, 共 {Attempt} 次尝试)", timeoutMs, attempt);
            return false;
        }

        /// <summary>
        /// 轮询间隔等待，统一处理 OperationCanceledException
        /// Unified inter-probe delay; swallows cancellation so callers see a
        /// clean `false` instead of an exception propagating out of the loop.
        /// </summary>
        private static async Task DelayBetweenProbesAsync(CancellationToken token)
        {
            try { await Task.Delay(READY_PROBE_INTERVAL_MS, token); }
            catch (OperationCanceledException) { /* 由主循环感知 token */ }
        }

        /// <summary>
        /// 启动 llama 服务（简化版本，仅需服务路径）
        /// Starts the llama service (simplified version with only service path)
        /// </summary>
        /// <param name="servicePath">服务可执行文件路径 / Service executable path</param>
        /// <returns>是否启动成功 / Whether start was successful</returns>
        public async Task<bool> StartServiceAsync(string servicePath)
        {
            Log.Information("正在启动 llama 服务: {ServicePath}", servicePath);

            try
            {
                // 检查服务是否已在运行
                // Check if service is already running
                if (Status == LlamaServiceStatus.Running)
                {
                    Log.Warning("llama 服务已在运行");
                    return true;
                }

                // 更新状态
                // Update status
                Status = LlamaServiceStatus.Starting;

                // 验证服务路径
                // Validate service path
                if (string.IsNullOrWhiteSpace(servicePath))
                {
                    throw new ArgumentException("服务路径不能为空");
                }

                if (!File.Exists(servicePath))
                {
                    throw new FileNotFoundException($"服务文件不存在: {servicePath}");
                }

                // 创建取消令牌
                // Create cancellation token
                _cancellationTokenSource = new CancellationTokenSource();

                // 启动命名管道服务器
                // Start named pipe server
                await StartPipeServerAsync(_cancellationTokenSource.Token);

                // 配置进程启动信息
                // Configure process start info
                var startInfo = new ProcessStartInfo
                {
                    FileName = servicePath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(servicePath) ?? string.Empty,
                    // 传递管道名称作为命令行参数
                    // Pass pipe name as command line argument
                    Arguments = $"--pipe-name {PIPE_NAME}"
                };

                // 启动进程
                // Start process
                Log.Information("正在启动 llama 服务进程...");
                _serviceProcess = Process.Start(startInfo);

                if (_serviceProcess == null)
                {
                    throw new InvalidOperationException("服务进程启动失败，返回 null");
                }

                // 注册进程退出事件
                // Register process exit event
                _serviceProcess.EnableRaisingEvents = true;
                _serviceProcess.Exited += OnServiceProcessExited;

                // 重定向输出到日志
                // Redirect output to log
                _serviceProcess.OutputDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Log.Information("[llama 服务] {Output}", e.Data);
                        ParamsOutputDataReceived?.Invoke(this, e.Data);
                    }
                };
                _serviceProcess.ErrorDataReceived += (sender, e) =>
                {
                    if (!string.IsNullOrEmpty(e.Data))
                    {
                        Log.Error("[llama 服务] {Error}", e.Data);
                        ParamsOutputDataReceived?.Invoke(this, e.Data);
                    }
                };
                _serviceProcess.BeginOutputReadLine();
                _serviceProcess.BeginErrorReadLine();

                // 等待服务初始化
                // Wait for service initialization
                Log.Information("等待 llama 服务初始化...");
                await Task.Delay(2000);

                // 检查进程是否已退出
                // Check if process has exited
                if (_serviceProcess.HasExited)
                {
                    throw new InvalidOperationException($"服务进程启动后立即退出，退出代码: {_serviceProcess.ExitCode}");
                }

                // 等待客户端连接
                // Wait for client connection
                Log.Information("等待 llama 服务连接到管道...");
                var connectTask = WaitForConnectionAsync(_cancellationTokenSource.Token);
                var timeoutTask = Task.Delay(10000);

                var completedTask = await Task.WhenAny(connectTask, timeoutTask);

                if (completedTask == timeoutTask)
                {
                    throw new TimeoutException("等待 llama 服务连接超时");
                }

                // 启动心跳定时器
                // Start heartbeat timer
                StartHeartbeatTimer();

                // 更新状态
                // Update status
                Status = LlamaServiceStatus.Running;
                _lastHeartbeat = DateTime.Now;

                Log.Information("llama 服务启动成功 (PID: {ProcessId})", _serviceProcess.Id);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动 llama 服务失败: {Message}", ex.Message);
                Status = LlamaServiceStatus.Error;

                // 清理资源
                // Clean up resources
                await CleanupAsync();

                return false;
            }
        }

        /// <summary>
        /// 停止 llama 服务
        /// Stops the llama service
        /// </summary>
        /// <param name="force">是否强制停止 / Whether to force stop</param>
        /// <returns>是否停止成功 / Whether stop was successful</returns>
        public async Task<bool> StopServiceAsync(bool force = false)
        {
            Log.Information("正在停止 llama 服务 (强制: {Force})", force);

            try
            {
                // 检查服务是否在运行
                // Check if service is running
                if (Status == LlamaServiceStatus.NotStarted || Status == LlamaServiceStatus.Stopped)
                {
                    Log.Warning("llama 服务未在运行");
                    return true;
                }

                // 更新状态
                // Update status
                Status = LlamaServiceStatus.Stopping;

                // 停止心跳定时器
                // Stop heartbeat timer
                StopHeartbeatTimer();

                // 取消所有异步操作
                // Cancel all async operations
                _cancellationTokenSource?.Cancel();

                // 发送停止命令
                // Send stop command
                if (!force && _pipeServer != null && _pipeServer.IsConnected)
                {
                    try
                    {
                        Log.Information("发送停止命令到 llama 服务...");
                        await SendMessageAsync(new IpcMessage
                        {
                            Type = IpcMessageType.Command,
                            Content = "STOP"
                        });

                        // 等待服务优雅退出
                        // Wait for graceful exit
                        await Task.Delay(2000);
                    }
                    catch (Exception ex)
                    {
                        Log.Warning(ex, "发送停止命令失败: {Message}", ex.Message);
                    }
                }

                // 关闭进程
                // Close process
                if (_serviceProcess != null && !_serviceProcess.HasExited)
                {
                    if (force)
                    {
                        Log.Information("强制终止 llama 服务进程 (PID: {ProcessId})", _serviceProcess.Id);
                        _serviceProcess.Kill();
                    }
                    else
                    {
                        Log.Information("请求关闭 llama 服务进程 (PID: {ProcessId})", _serviceProcess.Id);
                        _serviceProcess.CloseMainWindow();
                    }

                    // 等待进程退出
                    // Wait for process to exit
                    bool exited = _serviceProcess.WaitForExit(5000);

                    if (!exited)
                    {
                        Log.Warning("llama 服务进程未在超时时间内退出，强制终止");
                        _serviceProcess.Kill();
                        _serviceProcess.WaitForExit(2000);
                    }
                }

                // 清理资源
                // Clean up resources
                await CleanupAsync();

                // 更新状态
                // Update status
                Status = LlamaServiceStatus.Stopped;

                Log.Information("llama 服务已停止");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "停止 llama 服务失败: {Message}", ex.Message);
                Status = LlamaServiceStatus.Error;
                return false;
            }
        }

        /// <summary>
        /// 发送消息到 llama 服务
        /// Sends a message to the llama service
        /// </summary>
        /// <param name="message">消息对象 / Message object</param>
        /// <returns>是否发送成功 / Whether send was successful</returns>
        public async Task<bool> SendMessageAsync(IpcMessage message)
        {
            try
            {
                if (_pipeServer == null || !_pipeServer.IsConnected)
                {
                    Log.Warning("管道未连接，无法发送消息");
                    return false;
                }

                // 序列化消息
                // Serialize message
                string json = JsonConvert.SerializeObject(message);
                byte[] data = Encoding.UTF8.GetBytes(json);

                // 写入长度前缀（4字节）
                // Write length prefix (4 bytes)
                byte[] lengthPrefix = BitConverter.GetBytes(data.Length);
                await _pipeServer.WriteAsync(lengthPrefix, 0, lengthPrefix.Length);

                // 写入消息数据
                // Write message data
                await _pipeServer.WriteAsync(data, 0, data.Length);
                await _pipeServer.FlushAsync();

                Log.Debug("已发送消息: {Type} - {Content}", message.Type, message.Content);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "发送消息失败: {Message}", ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 启动命名管道服务器
        /// Starts the named pipe server
        /// </summary>
        /// <param name="cancellationToken">取消令牌 / Cancellation token</param>
        private async Task StartPipeServerAsync(CancellationToken cancellationToken)
        {
            try
            {
                Log.Information("正在启动命名管道服务器: {PipeName}", PIPE_NAME);

                // 创建命名管道服务器
                // Create named pipe server
                _pipeServer = new NamedPipeServerStream(
                    PIPE_NAME,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                Log.Information("命名管道服务器已创建，等待连接...");

                // 在后台任务中处理消息接收
                // Handle message receiving in background task
                _ = Task.Run(async () => await ReceiveMessagesAsync(cancellationToken), cancellationToken);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动命名管道服务器失败: {Message}", ex.Message);
                throw;
            }
        }

        /// <summary>
        /// 等待客户端连接
        /// Waits for client connection
        /// </summary>
        /// <param name="cancellationToken">取消令牌 / Cancellation token</param>
        private async Task WaitForConnectionAsync(CancellationToken cancellationToken)
        {
            if (_pipeServer == null)
            {
                throw new InvalidOperationException("管道服务器未初始化");
            }

            await _pipeServer.WaitForConnectionAsync(cancellationToken);
            Log.Information("llama 服务已连接到管道");
        }

        /// <summary>
        /// 接收消息循环
        /// Message receiving loop
        /// </summary>
        /// <param name="cancellationToken">取消令牌 / Cancellation token</param>
        private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_pipeServer == null)
                {
                    return;
                }

                // 等待连接
                // Wait for connection
                await _pipeServer.WaitForConnectionAsync(cancellationToken);

                Log.Information("开始接收消息...");

                while (!cancellationToken.IsCancellationRequested && _pipeServer.IsConnected)
                {
                    try
                    {
                        // 读取长度前缀（4字节）
                        // Read length prefix (4 bytes)
                        byte[] lengthPrefix = new byte[4];
                        int bytesRead = await _pipeServer.ReadAsync(lengthPrefix, 0, 4, cancellationToken);

                        if (bytesRead == 0)
                        {
                            Log.Warning("管道连接已关闭");
                            break;
                        }

                        int messageLength = BitConverter.ToInt32(lengthPrefix, 0);

                        // 读取消息数据
                        // Read message data
                        byte[] messageData = new byte[messageLength];
                        bytesRead = await _pipeServer.ReadAsync(messageData, 0, messageLength, cancellationToken);

                        if (bytesRead == 0)
                        {
                            Log.Warning("管道连接已关闭");
                            break;
                        }

                        // 反序列化消息
                        // Deserialize message
                        string json = Encoding.UTF8.GetString(messageData);
                        var message = JsonConvert.DeserializeObject<IpcMessage>(json);

                        if (message != null)
                        {
                            Log.Debug("收到消息: {Type} - {Content}", message.Type, message.Content);

                            // 处理消息
                            // Handle message
                            HandleMessage(message);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        Log.Information("消息接收已取消");
                        break;
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "接收消息失败: {Message}", ex.Message);
                        await Task.Delay(1000, cancellationToken);
                    }
                }

                Log.Information("消息接收循环已结束");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "消息接收循环异常: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 处理接收到的消息
        /// Handles received message
        /// </summary>
        /// <param name="message">消息对象 / Message object</param>
        private void HandleMessage(IpcMessage message)
        {
            try
            {
                switch (message.Type)
                {
                    case IpcMessageType.Heartbeat:
                        // 更新心跳时间
                        // Update heartbeat time
                        _lastHeartbeat = DateTime.Now;
                        Log.Debug("收到心跳");
                        break;

                    case IpcMessageType.Response:
                        // 处理响应
                        // Handle response
                        Log.Information("收到响应: {Content}", message.Content);
                        break;

                    case IpcMessageType.Notification:
                        // 处理通知
                        // Handle notification
                        Log.Information("收到通知: {Content}", message.Content);
                        break;

                    case IpcMessageType.Error:
                        // 处理错误
                        // Handle error
                        Log.Error("收到错误消息: {Content}", message.Content);
                        break;

                    default:
                        Log.Warning("未知的消息类型: {Type}", message.Type);
                        break;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理消息失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 启动心跳定时器
        /// Starts the heartbeat timer
        /// </summary>
        private void StartHeartbeatTimer()
        {
            Log.Information("启动心跳定时器");

            _heartbeatTimer = new Timer(
                callback: _ => CheckHeartbeat(),
                state: null,
                dueTime: HEARTBEAT_INTERVAL,
                period: HEARTBEAT_INTERVAL);
        }

        /// <summary>
        /// 停止心跳定时器
        /// Stops the heartbeat timer
        /// </summary>
        private void StopHeartbeatTimer()
        {
            if (_heartbeatTimer != null)
            {
                Log.Information("停止心跳定时器");
                _heartbeatTimer.Dispose();
                _heartbeatTimer = null;
            }
        }

        /// <summary>
        /// 检查心跳
        /// Checks heartbeat
        /// </summary>
        private void CheckHeartbeat()
        {
            try
            {
                var timeSinceLastHeartbeat = DateTime.Now - _lastHeartbeat;

                if (timeSinceLastHeartbeat.TotalMilliseconds > HEARTBEAT_TIMEOUT)
                {
                    Log.Warning("llama 服务心跳超时: {Elapsed}ms", timeSinceLastHeartbeat.TotalMilliseconds);
                    Status = LlamaServiceStatus.Error;

                    // 尝试重启服务
                    // Try to restart service
                    _ = Task.Run(async () =>
                    {
                        Log.Information("尝试重启 llama 服务...");
                        await StopServiceAsync(force: true);
                        // 注意：这里需要服务路径，实际使用时需要保存服务路径
                        // Note: Service path is needed here, should be saved in actual use
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "检查心跳失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 服务进程退出事件处理
        /// Service process exited event handler
        /// </summary>
        private void OnServiceProcessExited(object? sender, EventArgs e)
        {
            if (_serviceProcess != null)
            {
                Log.Warning("llama 服务进程已退出 (退出代码: {ExitCode})", _serviceProcess.ExitCode);
                Status = LlamaServiceStatus.Stopped;
            }
        }

        /// <summary>
        /// 清理资源
        /// Cleans up resources
        /// </summary>
        private async Task CleanupAsync()
        {
            try
            {
                Log.Information("正在清理 llama 服务资源...");

                // 停止心跳定时器
                // Stop heartbeat timer
                StopHeartbeatTimer();

                // 取消异步操作
                // Cancel async operations
                _cancellationTokenSource?.Cancel();
                _cancellationTokenSource?.Dispose();
                _cancellationTokenSource = null;

                // 关闭管道
                // Close pipe
                if (_pipeServer != null)
                {
                    try
                    {
                        if (_pipeServer.IsConnected)
                        {
                            _pipeServer.Disconnect();
                        }
                        _pipeServer.Dispose();
                        _pipeServer = null;
                        Log.Information("管道已关闭");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "关闭管道失败: {Message}", ex.Message);
                    }
                }

                // 清理进程
                // Clean up process
                if (_serviceProcess != null)
                {
                    try
                    {
                        _serviceProcess.Exited -= OnServiceProcessExited;
                        _serviceProcess.Dispose();
                        _serviceProcess = null;
                        Log.Information("进程对象已释放");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "释放进程对象失败: {Message}", ex.Message);
                    }
                }

                await Task.CompletedTask;
                Log.Information("llama 服务资源清理完成");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清理资源失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 释放资源
        /// Disposes resources
        /// </summary>
        public void Dispose()
        {
            Log.Information("正在释放 LlamaServiceManager 资源...");

            // 同步停止服务
            // Synchronously stop service
            Task.Run(async () => await StopServiceAsync(force: true)).Wait();

            Log.Information("LlamaServiceManager 资源已释放");
        }
    }
}
