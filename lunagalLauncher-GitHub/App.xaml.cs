using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Windowing;
using lunagalLauncher.Infrastructure;
using lunagalLauncher.Data;
using lunagalLauncher.Core;
using Serilog;

namespace lunagalLauncher
{
    /// <summary>
    /// 应用程序主类
    /// Provides application-specific behavior to supplement the default Application class.
    /// </summary>
    public partial class App : Application
    {
        /// <summary>
        /// 主窗口实例
        /// Main window instance
        /// </summary>
        public Window? window { get; private set; }

        /// <summary>
        /// 配置持久化管理器
        /// Configuration persistence manager
        /// </summary>
        public static ConfigPersistence ConfigManager { get; private set; } = null!;

        /// <summary>
        /// 应用程序配置
        /// Application configuration
        /// </summary>
        public static RootConfig AppConfig { get; set; } = null!;

        /// <summary>
        /// 初始化单例应用程序对象
        /// Initializes the singleton application object. This is the first line of authored code
        /// executed, and as such is the logical equivalent of main() or WinMain().
        /// </summary>
        public App()
        {
            // 初始化日志系统（必须最先执行）
            // Initialize logging system (must be first)
            LoggerManager.Initialize();
            Log.Information("应用程序构造函数开始执行");

            try
            {
                // 初始化 XAML 组件
                // Initialize XAML components
                this.InitializeComponent();

                // 初始化配置管理器
                // Initialize configuration manager
                Log.Information("正在初始化配置管理器...");
                ConfigManager = new ConfigPersistence();

                // 加载应用程序配置
                // Load application configuration
                Log.Information("正在加载应用程序配置...");
                AppConfig = ConfigManager.LoadConfig();

                // 记录启动信息
                // Log startup information
                LoggerManager.LogStartupInfo();

                // 注册未处理异常处理器
                // Register unhandled exception handlers
                this.UnhandledException += App_UnhandledException;
                AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;

                Log.Information("应用程序初始化完成");
            }
            catch (Exception ex)
            {
                // 记录初始化异常
                // Log initialization exception
                LoggerManager.LogException(ex, "应用程序初始化");
                throw;
            }
        }

        /// <summary>
        /// 应用程序正常启动时调用
        /// Invoked when the application is launched normally by the end user. Other entry points
        /// will be used such as when the application is launched to open a specific file.
        /// </summary>
        /// <param name="e">启动请求和进程的详细信息 / Details about the launch request and process</param>
        protected override void OnLaunched(LaunchActivatedEventArgs e)
        {
            try
            {
                Log.Information("应用程序启动事件触发");

                // 创建主窗口
                // Create main window
                window = new Window
                {
                    Title = "lunagalLauncher"
                };

                // 恢复窗口大小和位置
                // Restore window size and position
                RestoreWindowSettings();

                // 订阅窗口关闭事件
                // Subscribe to window closed event
                window.Closed += Window_Closed;

                // 创建导航框架
                // Create navigation frame
                if (window.Content is not Frame rootFrame)
                {
                    rootFrame = new Frame();
                    rootFrame.NavigationFailed += OnNavigationFailed;
                    window.Content = rootFrame;

                    Log.Information("导航框架已创建");
                }

                // 导航到主页面
                // Navigate to main page
                if (rootFrame.Content == null)
                {
                    Log.Information("正在导航到主页面...");
                    _ = rootFrame.Navigate(typeof(MainPage), e.Arguments);
                }

                // 激活窗口
                // Activate window
                window.Activate();
                Log.Information("主窗口已激活");
            }
            catch (Exception ex)
            {
                // 记录启动异常
                // Log launch exception
                LoggerManager.LogException(ex, "应用程序启动");
                throw;
            }
        }

        /// <summary>
        /// 全量导入配置后，将主窗口大小/位置/最大化状态与当前 <see cref="AppConfig.WindowSettings"/> 对齐。
        /// </summary>
        public void ReapplyWindowLayoutFromConfig()
        {
            try
            {
                if (window == null) return;
                RestoreWindowSettings();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "导入后应用窗口布局失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 恢复窗口设置
        /// Restore window settings
        /// </summary>
        private void RestoreWindowSettings()
        {
            try
            {
                if (window == null) return;

                var settings = AppConfig.WindowSettings;
                var appWindow = window.AppWindow;

                // 标准重叠窗口：必须可调整大小，否则用户无法拖动边缘改尺寸
                // Standard overlapped window: ensure resizable so users can drag edges
                if (appWindow.Presenter is OverlappedPresenter overlapped)
                {
                    overlapped.IsResizable = true;
                    overlapped.IsMaximizable = true;
                    overlapped.IsMinimizable = true;
                    // Windows App SDK 1.4+：限制用户可把窗口拖到的最小尺寸
                    overlapped.PreferredMinimumWidth = 800;
                    overlapped.PreferredMinimumHeight = 560;
                }

                // 若配置里保存了异常小的尺寸（或旧版本 bug），提升到可用默认值
                const int minW = 800;
                const int minH = 560;
                const int defaultW = 1280;
                const int defaultH = 800;
                if (settings.Width < minW || settings.Height < minH)
                {
                    settings.Width = settings.Width < minW ? defaultW : settings.Width;
                    settings.Height = settings.Height < minH ? defaultH : settings.Height;
                    Log.Information("窗口尺寸过小，已校正为: {Width}x{Height}", settings.Width, settings.Height);
                    try
                    {
                        ConfigManager.SaveConfig(AppConfig);
                    }
                    catch (Exception saveEx)
                    {
                        Log.Warning(saveEx, "写入校正后的窗口尺寸失败");
                    }
                }

                // 恢复窗口大小
                // Restore window size
                if (settings.Width > 0 && settings.Height > 0)
                {
                    appWindow.Resize(new Windows.Graphics.SizeInt32(settings.Width, settings.Height));
                    Log.Information("恢复窗口大小: {Width}x{Height}", settings.Width, settings.Height);
                }

                // 恢复窗口位置
                // Restore window position
                if (settings.X >= 0 && settings.Y >= 0)
                {
                    appWindow.Move(new Windows.Graphics.PointInt32(settings.X, settings.Y));
                    Log.Information("恢复窗口位置: ({X}, {Y})", settings.X, settings.Y);
                }

                // 恢复最大化状态
                // Restore maximized state
                if (settings.IsMaximized)
                {
                    // WinUI 3 中使用 Presenter 来设置窗口状态
                    if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                    {
                        presenter.Maximize();
                        Log.Information("窗口已最大化");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "恢复窗口设置失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 保存窗口设置
        /// Save window settings
        /// </summary>
        private void SaveWindowSettings()
        {
            try
            {
                if (window == null) return;

                var appWindow = window.AppWindow;
                var settings = AppConfig.WindowSettings;

                // 检查是否最大化
                // Check if maximized
                bool isMaximized = false;
                if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
                {
                    isMaximized = presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Maximized;
                }

                settings.IsMaximized = isMaximized;

                // 如果不是最大化，保存当前大小和位置
                // If not maximized, save current size and position
                if (!isMaximized)
                {
                    settings.Width = appWindow.Size.Width;
                    settings.Height = appWindow.Size.Height;
                    settings.X = appWindow.Position.X;
                    settings.Y = appWindow.Position.Y;

                    Log.Information("保存窗口设置: 大小={Width}x{Height}, 位置=({X},{Y})", 
                        settings.Width, settings.Height, settings.X, settings.Y);
                }
                else
                {
                    Log.Information("保存窗口设置: 最大化状态");
                }

                // 保存配置到文件
                // Save configuration to file
                ConfigManager.SaveConfig(AppConfig);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存窗口设置失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 窗口关闭事件处理
        /// Window closed event handler
        /// </summary>
        /// <param name="sender">事件发送者 / Event sender</param>
        /// <param name="args">事件参数 / Event arguments</param>
        private void Window_Closed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
        {
            try
            {
                Log.Information("窗口正在关闭...");

                // 关闭由 lunagalLauncher 启动的后台 llama-server 进程。
                // 之前的版本退出后 llama-server 会继续驻留，占用显存 / 端口 8080，
                // 下次启动 lunagalLauncher 会报 "端口已被占用" 或端口冲突后看似启动
                // 失败。这里统一由启动器 "谁拉起谁收拾"。
                // Stop the background llama-server spawned by this launcher.
                // Previously the child kept running after the UI exited, holding
                // GPU memory and TCP port 8080 hostage, which made the next run
                // fail with port-already-in-use. We apply an owner-cleanup rule:
                // whoever started it terminates it.
                ShutdownLlamaServiceBeforeExit();

                Log.Information("保存窗口设置...");
                SaveWindowSettings();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "窗口关闭事件处理失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 在窗口关闭时同步关闭后台的 llama-server 进程
        /// Synchronously terminate the background llama-server while the
        /// Window_Closed event handler is still on the UI thread. Uses a hard
        /// timeout to avoid blocking the app shutdown if the child is hung.
        /// 
        /// 为什么在这里同步 Wait：
        /// WinUI 3 的 Window_Closed 触发后不久进程就会结束，异步 Task 有可能来不及
        /// 发出 Kill 就被进程回收。所以这里用 Task.Run + Wait(超时) 强制落地。
        /// Why synchronous Wait here:
        /// After Window_Closed fires the app process exits shortly; a fire-and-
        /// forget async Task might not get scheduled in time to send the kill.
        /// Wrapping with Wait(timeout) guarantees we at least attempted Kill
        /// before our own process goes away, while still bounding the delay.
        /// </summary>
        private static void ShutdownLlamaServiceBeforeExit()
        {
            try
            {
                var manager = LlamaServiceManager.Instance;
                var status = manager.Status;

                // 仅当服务确实处于活跃状态时才停 —— 避免把已 NotStarted/Stopped
                // 状态的管理器再跑一遍流程产生多余日志。
                // Skip when the service is already idle to keep the exit log clean.
                if (status != LlamaServiceStatus.Running &&
                    status != LlamaServiceStatus.Starting &&
                    status != LlamaServiceStatus.Error)
                {
                    Log.Information("llama 服务当前状态为 {Status}，无需在退出时停止", status);
                    return;
                }

                Log.Information("程序退出：正在强制关闭后台 llama-server (Status={Status})...", status);

                // 给强制 Kill 最多 6 秒时间 (StopServiceAsync 内部本身有 5s 等待)。
                // Give the force-kill path up to 6s (StopServiceAsync's internal
                // wait is 5s + 2s overhead).
                bool finished = Task.Run(async () =>
                {
                    await manager.StopServiceAsync(force: true);
                }).Wait(TimeSpan.FromSeconds(6));

                if (!finished)
                {
                    Log.Warning("在 6 秒超时内未能彻底关闭 llama-server，将让操作系统回收其句柄");
                }
                else
                {
                    Log.Information("后台 llama-server 已关闭");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "退出时关闭 llama 服务时出现异常: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 导航到特定页面失败时调用
        /// Invoked when Navigation to a certain page fails
        /// </summary>
        /// <param name="sender">导航失败的框架 / The Frame which failed navigation</param>
        /// <param name="e">导航失败的详细信息 / Details about the navigation failure</param>
        private void OnNavigationFailed(object sender, NavigationFailedEventArgs e)
        {
            Log.Error("导航失败: 无法加载页面 {PageType}", e.SourcePageType.FullName);
            throw new Exception($"Failed to load Page {e.SourcePageType.FullName}");
        }

        /// <summary>
        /// 处理应用程序未处理的异常
        /// Handles unhandled exceptions in the application
        /// </summary>
        /// <param name="sender">事件发送者 / Event sender</param>
        /// <param name="e">异常事件参数 / Exception event arguments</param>
        private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
        {
            // 记录未处理的异常
            // Log unhandled exception
            LoggerManager.LogException(e.Exception, "应用程序未处理异常");

            // 标记异常已处理（防止应用崩溃）
            // Mark exception as handled (prevent app crash)
            e.Handled = true;

            Log.Warning("异常已被捕获并标记为已处理，应用程序将继续运行");
        }

        /// <summary>
        /// 处理 AppDomain 未处理的异常
        /// Handles unhandled exceptions in the AppDomain
        /// </summary>
        /// <param name="sender">事件发送者 / Event sender</param>
        /// <param name="e">异常事件参数 / Exception event arguments</param>
        private void CurrentDomain_UnhandledException(object sender, System.UnhandledExceptionEventArgs e)
        {
            if (e.ExceptionObject is Exception ex)
            {
                // 记录 AppDomain 未处理的异常
                // Log AppDomain unhandled exception
                LoggerManager.LogException(ex, "AppDomain 未处理异常");
            }

            // 如果是终止异常，关闭日志系统
            // If terminating, shutdown logging system
            if (e.IsTerminating)
            {
                Log.Fatal("应用程序即将终止");
                LoggerManager.Shutdown();
            }
        }
    }
}
