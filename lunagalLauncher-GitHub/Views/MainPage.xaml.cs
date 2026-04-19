using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using lunagalLauncher;
using lunagalLauncher.Core;
using lunagalLauncher.Data;
using lunagalLauncher.Services;
using Microsoft.UI.Xaml.Controls;
using Serilog;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace lunagalLauncher.Views
{
    /// <summary>
    /// 主页面
    /// Main page that serves as the application's primary interface
    /// </summary>
    public partial class MainPage : Page
    {
        /// <summary>
        /// 应用启动管理器
        /// Application launch manager
        /// </summary>
        private readonly LaunchManager _launchManager;

        /// <summary>
        /// 页面缓存字典
        /// Page cache dictionary for reusing page instances
        /// </summary>
        private readonly Dictionary<Type, Page> _pageCache = new Dictionary<Type, Page>();

        /// <summary>最近一次真正的内容页导航 Tag（排除 Go!/导出/导入），用于全量导入后恢复内容区。</summary>
        private string _lastNonActionNavTag = "AppManagement";

        /// <summary>
        /// 构造函数
        /// Constructor - initializes the main page
        /// </summary>
        public MainPage()
        {
            Log.Information("正在初始化主页面...");
            this.InitializeComponent();
            Log.Information("主页面初始化完成");

            // 初始化启动管理器
            // Initialize launch manager
            _launchManager = new LaunchManager();

            // 订阅启动状态改变事件
            // Subscribe to launch status changed event
            _launchManager.LaunchStatusChanged += OnLaunchStatusChanged;

            // 订阅进程退出事件
            // Subscribe to process exited event
            _launchManager.ProcessExited += OnProcessExited;

            // 设置默认选中第一个导航项，但不立即导航
            // Set default selected navigation item, but don't navigate immediately
            if (MainNavigationView.MenuItems.Count > 0)
            {
                MainNavigationView.SelectedItem = MainNavigationView.MenuItems[0];
                // 不在这里导航，让用户先看到欢迎页面
                // Don't navigate here, let user see welcome page first
            }

            // 异步预加载所有页面（不阻塞UI）
            // Async preload all pages (non-blocking)
            _ = PreloadPagesAsync();

            // 为导航项添加按压动画
            // Add press animations to navigation items
            this.Loaded += MainPage_Loaded;
        }

        /// <summary>
        /// 页面加载完成后的处理
        /// </summary>
        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            AttachNavigationItemAnimations();
            try
            {
                MouseMappingRuntime.InitializeUi(DispatcherQueue);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "鼠标映射运行时初始化失败");
            }
        }

        /// <summary>
        /// 为所有导航项附加 iOS/macOS 风格的按压动画
        /// </summary>
        private void AttachNavigationItemAnimations()
        {
            try
            {
                // 为菜单项添加动画
                foreach (var item in MainNavigationView.MenuItems)
                {
                    if (item is NavigationViewItem navItem)
                    {
                        AttachPressAnimation(navItem);
                    }
                }

                // 为页脚项添加动画
                foreach (var item in MainNavigationView.FooterMenuItems)
                {
                    if (item is NavigationViewItem navItem)
                    {
                        AttachPressAnimation(navItem);
                    }
                }

                Log.Information("已为导航项添加按压动画");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "添加导航项动画失败");
            }
        }

        /// <summary>
        /// 为单个导航项附加按压动画
        /// </summary>
        private void AttachPressAnimation(NavigationViewItem navItem)
        {
            navItem.PointerPressed += NavItem_PointerPressed;
            navItem.PointerReleased += NavItem_PointerReleased;
            navItem.PointerCanceled += NavItem_PointerReleased;
            navItem.PointerCaptureLost += NavItem_PointerReleased;
        }

        /// <summary>
        /// 导航项按下动画 - iOS/macOS 风格
        /// </summary>
        private void NavItem_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is NavigationViewItem navItem)
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(navItem);
                var compositor = visual.Compositor;

                // 创建缩放动画 - 缩小到 0.95
                var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
                scaleAnimation.Duration = TimeSpan.FromMilliseconds(100);
                scaleAnimation.InsertKeyFrame(1.0f, new System.Numerics.Vector3(0.95f, 0.95f, 1.0f));

                // 创建透明度动画 - 降低到 0.7
                var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
                opacityAnimation.Duration = TimeSpan.FromMilliseconds(100);
                opacityAnimation.InsertKeyFrame(1.0f, 0.7f);

                // 设置中心点为元素中心
                visual.CenterPoint = new System.Numerics.Vector3((float)navItem.ActualWidth / 2, (float)navItem.ActualHeight / 2, 0);

                visual.StartAnimation("Scale", scaleAnimation);
                visual.StartAnimation("Opacity", opacityAnimation);
            }
        }

        /// <summary>
        /// 导航项松开动画 - iOS/macOS 风格
        /// </summary>
        private void NavItem_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            if (sender is NavigationViewItem navItem)
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(navItem);
                var compositor = visual.Compositor;

                // 创建弹性恢复动画 - 使用 Spring 效果
                var scaleAnimation = compositor.CreateVector3KeyFrameAnimation();
                scaleAnimation.Duration = TimeSpan.FromMilliseconds(200);
                
                // 使用缓动函数实现弹性效果
                var easingFunction = compositor.CreateCubicBezierEasingFunction(
                    new System.Numerics.Vector2(0.25f, 0.1f),
                    new System.Numerics.Vector2(0.25f, 1.0f)
                );
                
                scaleAnimation.InsertKeyFrame(1.0f, new System.Numerics.Vector3(1.0f, 1.0f, 1.0f), easingFunction);

                // 创建透明度恢复动画
                var opacityAnimation = compositor.CreateScalarKeyFrameAnimation();
                opacityAnimation.Duration = TimeSpan.FromMilliseconds(200);
                opacityAnimation.InsertKeyFrame(1.0f, 1.0f, easingFunction);

                visual.StartAnimation("Scale", scaleAnimation);
                visual.StartAnimation("Opacity", opacityAnimation);
            }
        }

        /// <summary>
        /// 异步预加载所有页面
        /// Async preload all pages to improve navigation performance
        /// </summary>
        private async Task PreloadPagesAsync()
        {
            try
            {
                Log.Information("开始异步预加载页面...");

                // 立即开始预加载，不延迟
                // Start preloading immediately without delay
                await Task.Delay(50);

                // 在UI线程上创建页面
                // Create pages on UI thread
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    try
                    {
                        // 预加载应用管理页面（优先级最高）
                        // Preload app management page (highest priority)
                        _pageCache[typeof(AppManagementPage)] = new AppManagementPage();
                        Log.Debug("已预加载: AppManagementPage");
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "预加载 AppManagementPage 失败");
                    }
                });

                // 不预加载 LlamaServicePage，因为它太重了，按需加载
                // Don't preload LlamaServicePage as it's too heavy, load on demand
                Log.Information("跳过预加载 LlamaServicePage（按需加载）");

                await Task.Delay(50);

                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    try
                    {
                        // 预加载日志查看器页面
                        // Preload log viewer page
                        _pageCache[typeof(LogViewerPage)] = new LogViewerPage();
                        Log.Debug("已预加载: LogViewerPage");

                        Log.Information("页面预加载完成，共 {Count} 个页面", _pageCache.Count);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "预加载 LogViewerPage 失败");
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "预加载页面失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 一键开启导航项点击事件
        /// One-click launch navigation item tapped event handler
        /// </summary>
        /// <param name="sender">事件发送者 / Event sender</param>
        /// <param name="e">事件参数 / Event arguments</param>
        private async void LaunchAllNavItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            await LaunchAllApplicationsAsync();
        }

        private async void ExportAllSettingsNavItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                FlushMouseMappingPageIfCached();

                var path = await PickSaveFullBackupPathAsync();
                if (string.IsNullOrEmpty(path))
                    return;

                var json = App.ConfigManager.SerializeFullSettingsForExport(App.AppConfig);
                await File.WriteAllTextAsync(path, json);
                Log.Information("已导出完整设置到 {Path}", path);

                _ = await new ContentDialog
                {
                    Title = "导出成功",
                    Content = $"已导出全部设置到：\n{path}",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot
                }.ShowAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "导出完整设置失败");
                await ShowBackupMessageAsync("导出失败", ex.Message);
            }
        }

        private async void ImportAllSettingsNavItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                var confirm = new ContentDialog
                {
                    Title = "导入完整设置",
                    Content = "将用所选备份覆盖当前所有设置（应用列表、llama 服务、鼠标映射、窗口布局等），并写入本机配置文件。是否继续？",
                    PrimaryButtonText = "导入",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = XamlRoot
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary)
                    return;

                var path = await PickOpenFullBackupPathAsync();
                if (string.IsNullOrEmpty(path))
                    return;

                var json = await File.ReadAllTextAsync(path);
                if (!App.ConfigManager.TryParseFullSettingsImport(json, out var imported) || imported == null)
                {
                    await ShowBackupMessageAsync(
                        "导入失败",
                        "无法解析备份文件。请使用本程序「导出设置」生成的完整 JSON（须包含 LaunchSettings 节点）。");
                    return;
                }

                App.AppConfig = imported;
                if (!App.ConfigManager.SaveConfig(App.AppConfig))
                {
                    await ShowBackupMessageAsync("导入失败", "写入本机配置文件失败。");
                    return;
                }

                MouseMappingRuntime.ApplyFromCurrentConfig();
                if (Application.Current is App app)
                    app.ReapplyWindowLayoutFromConfig();

                RefreshUiAfterFullConfigImport();

                _ = await new ContentDialog
                {
                    Title = "导入成功",
                    Content = "已应用备份中的全部设置，界面已按当前配置刷新。",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot
                }.ShowAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "导入完整设置失败");
                await ShowBackupMessageAsync("导入失败", ex.Message);
            }
        }

        private void FlushMouseMappingPageIfCached()
        {
            if (_pageCache.TryGetValue(typeof(MouseMappingPage), out var page) && page is MouseMappingPage mmp)
                mmp.FlushPendingMouseMappingToConfig();
        }

        private void RefreshUiAfterFullConfigImport()
        {
            _pageCache.Clear();

            if (!TryGetPageTypeForNavTag(_lastNonActionNavTag, out var pageType))
            {
                _lastNonActionNavTag = "AppManagement";
                TryGetPageTypeForNavTag(_lastNonActionNavTag, out pageType);
            }

            SelectMenuNavigationItemByTag(_lastNonActionNavTag);
            NavigateToPage(pageType);

            if (_lastNonActionNavTag == "LogViewer")
                _ = ScheduleLogViewerScrollToLatestAsync();

            if (_lastNonActionNavTag == "AppManagement")
                _ = DeferSetLaunchManagerForAppManagementAsync();
        }

        /// <summary>异步创建页面完成后补挂 <see cref="LaunchManager"/>（与导航到应用管理页行为一致）。</summary>
        private async Task DeferSetLaunchManagerForAppManagementAsync()
        {
            for (var i = 0; i < 12; i++)
            {
                await Task.Delay(60);
                if (ContentFrame.Content is AppManagementPage amp)
                {
                    amp.SetLaunchManager(_launchManager);
                    return;
                }
            }
        }

        private void SelectMenuNavigationItemByTag(string tag)
        {
            foreach (var o in MainNavigationView.MenuItems)
            {
                if (o is NavigationViewItem n && string.Equals(n.Tag?.ToString(), tag, StringComparison.Ordinal))
                {
                    MainNavigationView.SelectedItem = n;
                    return;
                }
            }
        }

        private static bool TryGetPageTypeForNavTag(string tag, out Type pageType)
        {
            switch (tag)
            {
                case "AppManagement":
                    pageType = typeof(AppManagementPage);
                    return true;
                case "LlamaService":
                    pageType = typeof(LlamaServicePage);
                    return true;
                case "MouseMapping":
                    pageType = typeof(MouseMappingPage);
                    return true;
                case "LogViewer":
                    pageType = typeof(LogViewerPage);
                    return true;
                default:
                    pageType = null!;
                    return false;
            }
        }

        private async Task<string?> PickSaveFullBackupPathAsync()
        {
            const string suggested = "lunagalLauncher-full-backup.json";
            var app = (App)Application.Current;
            if (app?.window == null)
            {
                Log.Error("无法获取应用程序窗口实例");
                return null;
            }

            var hwnd = WindowNative.GetWindowHandle(app.window);
            try
            {
                var picker = new FileSavePicker();
                InitializeWithWindow.Initialize(picker, hwnd);
                picker.SuggestedFileName = Path.GetFileNameWithoutExtension(suggested);
                picker.FileTypeChoices.Add("JSON (*.json)", new List<string> { ".json" });
                picker.DefaultFileExtension = ".json";

                var file = await picker.PickSaveFileAsync();
                return file?.Path;
            }
            catch (COMException)
            {
                Log.Warning("FileSavePicker 失败，使用 Win32 另存为");
                return Win32FileDialog.ShowSaveFileDialog(hwnd, "JSON (*.json)|*.json", "导出完整设置", suggested);
            }
        }

        private async Task<string?> PickOpenFullBackupPathAsync()
        {
            var app = (App)Application.Current;
            if (app?.window == null)
            {
                Log.Error("无法获取应用程序窗口实例");
                return null;
            }

            var hwnd = WindowNative.GetWindowHandle(app.window);
            const string filter = "JSON (*.json)|*.json";

            try
            {
                var picker = new FileOpenPicker();
                picker.FileTypeFilter.Add(".json");
                InitializeWithWindow.Initialize(picker, hwnd);
                var file = await picker.PickSingleFileAsync();
                return file?.Path;
            }
            catch (COMException)
            {
                Log.Warning("FileOpenPicker 失败，使用 Win32 打开对话框");
                return Win32FileDialog.ShowOpenFileDialog(hwnd, filter, "选择完整设置备份");
            }
        }

        private async Task ShowBackupMessageAsync(string title, string message)
        {
            _ = await new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "确定",
                XamlRoot = XamlRoot
            }.ShowAsync();
        }

        /// <summary>
        /// 一键开启按钮点击事件（已废弃，保留以防万一）
        /// One-click launch button click event handler (deprecated, kept just in case)
        /// </summary>
        /// <param name="sender">事件发送者 / Event sender</param>
        /// <param name="e">事件参数 / Event arguments</param>
        private async void LaunchAllButton_Click(object sender, RoutedEventArgs e)
        {
            await LaunchAllApplicationsAsync();
        }

        /// <summary>
        /// 启动所有应用程序的核心逻辑
        /// Core logic for launching all applications
        /// </summary>
        private async Task LaunchAllApplicationsAsync()
        {
            try
            {
                Log.Information("用户点击「Go！」导航项");

                // 禁用导航项防止重复点击
                // Disable navigation item to prevent multiple clicks
                if (LaunchAllNavItem != null)
                {
                    LaunchAllNavItem.IsEnabled = false;
                }

                // 获取应用配置列表
                // Get application configuration list
                var appConfigs = App.AppConfig.LaunchSettings.Apps;

                if (appConfigs == null || appConfigs.Count == 0)
                {
                    Log.Warning("没有配置任何应用程序");

                    var noAppsDialog = new ContentDialog
                    {
                        Title = "提示",
                        Content = "您还没有配置任何应用程序。\n请先在「应用管理」中添加应用。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    await noAppsDialog.ShowAsync();
                    return;
                }

                // 检查是否使用最小化模式
                // Check if using minimized mode
                bool minimized = App.AppConfig.LaunchSettings.LaunchMode == "minimized";
                Log.Information("启动模式: {Mode}", minimized ? "最小化" : "正常");

                // 先启动并等待 llama 服务就绪，再启动依赖它的应用 (LunaTranslator 等)
                // Start and WAIT for the llama service to become HTTP-ready
                // before launching downstream apps (e.g. LunaTranslator) that
                // connect to http://127.0.0.1:8080. Without this gate the old
                // flow launched LunaTranslator immediately — it would race the
                // model-loading window and fail its initial /v1/models probe,
                // leaving the user with "LunaTranslator 对接不上 llama 服务".
                await EnsureLlamaServiceReadyAsync();

                // 启动所有应用
                // Launch all applications
                var results = await _launchManager.LaunchAllApplicationsAsync(appConfigs, minimized);

                // 统计结果
                // Count results
                var successCount = results.Count(r => r.Status == LaunchStatus.Launched);
                var failedCount = results.Count(r => r.Status == LaunchStatus.Failed);

                Log.Information("所有应用启动完成 - 成功: {Success}, 失败: {Failed}", successCount, failedCount);

                // 只在有失败的情况下显示错误详情
                // Only show error details if there are failures
                if (failedCount > 0)
                {
                    string dialogTitle;
                    string dialogContent;

                    if (successCount == 0)
                    {
                        dialogTitle = "启动失败";
                        dialogContent = $"所有应用程序启动失败！\n\n失败详情：\n";
                    }
                    else
                    {
                        dialogTitle = "部分成功";
                        dialogContent = $"成功启动 {successCount} 个应用，{failedCount} 个失败。\n\n失败详情：\n";
                    }

                    foreach (var result in results.Where(r => r.Status == LaunchStatus.Failed))
                    {
                        dialogContent += $"• {result.AppConfig.Name}: {result.ErrorMessage}\n";
                    }

                    var dialog = new ContentDialog
                    {
                        Title = dialogTitle,
                        Content = dialogContent,
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
            }
            catch (Exception ex)
            {
                // 记录异常
                // Log exception
                Log.Error(ex, "一键启动失败: {Message}", ex.Message);

                // 显示错误提示
                // Show error notification
                var dialog = new ContentDialog
                {
                    Title = "启动失败",
                    Content = $"启动应用时发生错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                // 重新启用导航项
                // Re-enable navigation item
                if (LaunchAllNavItem != null)
                {
                    LaunchAllNavItem.IsEnabled = true;
                }
            }
        }

        /// <summary>
        /// 在启动依赖 llama 服务的应用 (LunaTranslator 等) 之前，确保 llama 服务已启动并完成 HTTP 监听
        /// Guarantees the llama service is running AND its HTTP endpoint is
        /// reachable before the caller proceeds to launch downstream apps.
        /// 
        /// 逻辑 (Logic):
        ///   1. 若 LlamaService.Enabled 为 false，跳过（用户显式禁用）
        ///   2. 若服务已处于 Running 状态 (用户在 LlamaServicePage 手动启动过)，直接返回
        ///   3. 否则调用 <see cref="LlamaServiceManager.StartServiceAsync"/>，该方法内部
        ///      会轮询 /v1/models 直到真正可达才返回 true
        ///   4. 启动失败时弹窗但不阻断其余应用启动 — 避免"磁盘里没有模型"等个别问题拖垮整个一键开启
        /// </summary>
        private async Task EnsureLlamaServiceReadyAsync()
        {
            try
            {
                var llamaCfg = App.AppConfig.LaunchSettings.LlamaService;

                // 用户禁用了 llama 服务则跳过 —— 例如仅想启动 LunaTranslator 用云端 API
                // Honor the user's opt-out so one-click still works for cloud-only setups.
                if (llamaCfg == null || !llamaCfg.Enabled)
                {
                    Log.Information("llama 服务已禁用，跳过自动启动");
                    return;
                }

                // 必要的前置检查：路径缺失时无法启动，且没必要阻塞
                // Skip (with warning) when the user hasn't configured the service yet.
                if (string.IsNullOrWhiteSpace(llamaCfg.ServicePath) ||
                    string.IsNullOrWhiteSpace(llamaCfg.ModelPath))
                {
                    Log.Warning("llama 服务未配置完整 (ServicePath / ModelPath 为空)，跳过自动启动");
                    return;
                }

                var manager = LlamaServiceManager.Instance;

                // 已经在运行则直接复用，避免二次启动造成端口冲突 / 进程重复
                // Reuse the existing instance — starting twice would collide
                // on the TCP port and leave users with a zombie llama-server.
                if (manager.Status == LlamaServiceStatus.Running)
                {
                    Log.Information("llama 服务已在运行，跳过启动步骤");
                    return;
                }

                Log.Information("一键开启：自动启动 llama 服务并等待就绪...");
                var gpuDetector = new GpuDetector();
                bool ok = await manager.StartServiceAsync(llamaCfg, gpuDetector);

                if (!ok)
                {
                    // 非致命：后续应用仍然会被启动，只是依赖 llama 的翻译功能暂不可用
                    // Non-fatal: downstream apps still launch; the user can retry
                    // via LlamaServicePage after resolving the underlying error.
                    Log.Warning("llama 服务启动失败，相关翻译功能可能不可用");

                    var dialog = new ContentDialog
                    {
                        Title = "llama 服务启动失败",
                        Content =
                            "llama 服务未能启动或未在预期时间内开放 HTTP 端点，\n" +
                            "LunaTranslator 等依赖本地 Sakura API 的功能可能无法使用。\n\n" +
                            "可前往「llama 服务」页面查看日志并手动重试。",
                        CloseButtonText = "继续启动其它应用",
                        XamlRoot = this.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
                else
                {
                    Log.Information("✅ llama 服务已就绪，开始启动依赖它的应用");
                }
            }
            catch (Exception ex)
            {
                // 捕获一切异常：这里的失败不应阻塞一键启动主流程
                // Swallow to protect the main one-click flow; the detailed error
                // is in the log and visible on the LlamaServicePage.
                Log.Error(ex, "准备 llama 服务时发生异常: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 导航视图选择改变事件
        /// Navigation view selection changed event handler
        /// </summary>
        /// <param name="sender">事件发送者 / Event sender</param>
        /// <param name="args">事件参数 / Event arguments</param>
        private void MainNavigationView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            try
            {
                // 获取选中的导航项
                // Get selected navigation item
                if (args.SelectedItem is NavigationViewItem selectedItem)
                {
                    string? tag = selectedItem.Tag?.ToString();
                    Log.Information("导航到: {Tag}", tag);

                    // 根据标签导航到不同页面
                    // Navigate to different pages based on tag
                    switch (tag)
                    {
                        case "LaunchAll":
                        case "ExportAllSettings":
                        case "ImportAllSettings":
                            break;

                        case "AppManagement":
                            _lastNonActionNavTag = tag;
                            NavigateToPage(typeof(AppManagementPage));
                            
                            // 传递 LaunchManager 给应用管理页面
                            // Pass LaunchManager to app management page
                            if (ContentFrame.Content is AppManagementPage appManagementPage)
                            {
                                appManagementPage.SetLaunchManager(_launchManager);
                            }
                            
                            Log.Information("已导航到应用管理页面");
                            break;

                        case "LlamaService":
                            _lastNonActionNavTag = tag;
                            NavigateToPage(typeof(LlamaServicePage));
                            Log.Information("已导航到 llama 服务页面");
                            break;

                        case "MouseMapping":
                            _lastNonActionNavTag = tag;
                            NavigateToPage(typeof(MouseMappingPage));
                            Log.Information("已导航到鼠标映射页面");
                            break;

                        case "LogViewer":
                            _lastNonActionNavTag = tag;
                            NavigateToPage(typeof(LogViewerPage));
                            _ = ScheduleLogViewerScrollToLatestAsync();
                            Log.Information("已导航到日志页面");
                            break;

                        default:
                            Log.Warning("未知的导航标签: {Tag}", tag);
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                // 记录导航异常
                // Log navigation exception
                Log.Error(ex, "导航失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 进入日志页后多次尝试吸底：缓存页切换、异步创建完成与布局就绪的时序不一致。
        /// </summary>
        private async System.Threading.Tasks.Task ScheduleLogViewerScrollToLatestAsync()
        {
            for (var i = 0; i < 4; i++)
            {
                if (ContentFrame.Content is LogViewerPage lv)
                {
                    if (i == 0)
                        lv.ScrollToLatestWhenShown();
                    else
                        lv.NudgeScrollToLatest();
                }
                await System.Threading.Tasks.Task.Delay(i == 0 ? 0 : 120);
            }
        }

        /// <summary>
        /// 导航到指定页面（使用缓存）
        /// Navigate to specified page (with caching)
        /// </summary>
        /// <param name="pageType">页面类型 / Page type</param>
        private void NavigateToPage(Type pageType)
        {
            try
            {
                // 检查缓存中是否已有该页面实例
                // Check if page instance exists in cache
                if (_pageCache.ContainsKey(pageType))
                {
                    // 获取缓存的页面实例 - 这个是即时的
                    // Get cached page instance - this is instant
                    var cachedPage = _pageCache[pageType];

                    // 直接设置 Frame 的内容（避免 Navigate 的开销）
                    // Directly set Frame content (avoid Navigate overhead)
                    if (!ReferenceEquals(ContentFrame.Content, cachedPage))
                    {
                        // 添加 iOS 风格的淡入+滑动动画
                        AnimatePageTransition(cachedPage);
                        Log.Debug("切换到页面: {PageType}", pageType.Name);
                    }
                    return;
                }

                // 页面未缓存 - 立即显示空白页，然后异步加载
                // Page not cached - show blank immediately, then load async
                Log.Information("页面未预加载: {PageType}", pageType.Name);
                
                // 创建一个极简的占位页面（不创建任何控件，最快）
                var placeholderPage = new Page { Background = ContentFrame.Background };
                AnimatePageTransition(placeholderPage);

                // 异步创建加载UI和真实页面
                _ = LoadPageAsync(pageType, placeholderPage);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "导航到页面失败: {PageType}, {Message}", pageType.Name, ex.Message);
            }
        }

        /// <summary>
        /// iOS 风格的页面切换动画
        /// </summary>
        private void AnimatePageTransition(Page newPage)
        {
            var oldPage = ContentFrame.Content as UIElement;
            
            // 重置新页面的状态
            newPage.Opacity = 1;
            newPage.Translation = new System.Numerics.Vector3(0, 0, 0);
            
            ContentFrame.Content = newPage;

            // 获取 Visual
            var newVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(newPage);
            var compositor = newVisual.Compositor;

            // 创建淡出动画（旧页面）- 缩短到 150ms
            if (oldPage != null)
            {
                var oldVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(oldPage);
                
                var fadeOutAnimation = compositor.CreateScalarKeyFrameAnimation();
                fadeOutAnimation.Duration = TimeSpan.FromMilliseconds(150);
                fadeOutAnimation.InsertKeyFrame(1.0f, 0.0f);
                
                var slideOutAnimation = compositor.CreateVector3KeyFrameAnimation();
                slideOutAnimation.Duration = TimeSpan.FromMilliseconds(150);
                slideOutAnimation.InsertKeyFrame(1.0f, new System.Numerics.Vector3(-20, 0, 0));
                
                oldVisual.StartAnimation("Opacity", fadeOutAnimation);
                oldVisual.StartAnimation("Offset", slideOutAnimation);
            }

            // 创建淡入+滑入动画（新页面）- 缩短到 200ms
            var fadeInAnimation = compositor.CreateScalarKeyFrameAnimation();
            fadeInAnimation.Duration = TimeSpan.FromMilliseconds(200);
            fadeInAnimation.InsertKeyFrame(0.0f, 0.0f);
            fadeInAnimation.InsertKeyFrame(1.0f, 1.0f);
            
            // 使用缓动函数实现 iOS 风格的流畅动画
            var easingFunction = compositor.CreateCubicBezierEasingFunction(
                new System.Numerics.Vector2(0.25f, 0.1f),
                new System.Numerics.Vector2(0.25f, 1.0f)
            );
            
            var slideInAnimation = compositor.CreateVector3KeyFrameAnimation();
            slideInAnimation.Duration = TimeSpan.FromMilliseconds(200);
            slideInAnimation.InsertKeyFrame(0.0f, new System.Numerics.Vector3(20, 0, 0));
            slideInAnimation.InsertKeyFrame(1.0f, new System.Numerics.Vector3(0, 0, 0), easingFunction);
            
            newVisual.StartAnimation("Opacity", fadeInAnimation);
            newVisual.StartAnimation("Offset", slideInAnimation);
        }

        /// <summary>
        /// 异步加载页面
        /// </summary>
        private async Task LoadPageAsync(Type pageType, Page placeholderPage)
        {
            try
            {
                // 先让占位页面渲染出来
                await Task.Delay(1);

                // 在低优先级队列中添加加载UI
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
                {
                    if (ReferenceEquals(ContentFrame.Content, placeholderPage))
                    {
                        var loadingPanel = new StackPanel
                        {
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Center,
                            Spacing = 16
                        };
                        
                        var progressRing = new ProgressRing
                        {
                            IsActive = true,
                            Width = 48,
                            Height = 48
                        };
                        
                        var loadingText = new TextBlock
                        {
                            Text = "正在加载...",
                            FontSize = 16,
                            HorizontalAlignment = HorizontalAlignment.Center,
                            Opacity = 0.7
                        };
                        
                        loadingPanel.Children.Add(progressRing);
                        loadingPanel.Children.Add(loadingText);
                        placeholderPage.Content = loadingPanel;
                    }
                });

                // 在后台线程创建页面
                await Task.Run(async () =>
                {
                    await Task.Delay(10);
                    
                    DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                    {
                        try
                        {
                            Log.Information("开始创建页面实例: {PageType}", pageType.Name);
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            
                            var page = (Page)Activator.CreateInstance(pageType)!;
                            _pageCache[pageType] = page;
                            
                            sw.Stop();
                            Log.Information("页面创建完成: {PageType}，耗时: {Ms}ms", pageType.Name, sw.ElapsedMilliseconds);
                            
                            // 只有当前还在显示占位页面时才切换
                            if (ReferenceEquals(ContentFrame.Content, placeholderPage))
                            {
                                AnimatePageTransition(page);
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Error(ex, "创建页面失败: {PageType}", pageType.Name);
                        }
                    });
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "异步加载页面失败: {PageType}", pageType.Name);
            }
        }

        /// <summary>
        /// 启动状态改变事件处理
        /// Launch status changed event handler
        /// </summary>
        /// <param name="sender">事件发送者 / Event sender</param>
        /// <param name="result">启动结果 / Launch result</param>
        private void OnLaunchStatusChanged(object? sender, LaunchResult result)
        {
            // 在 UI 线程上记录日志
            // Log on UI thread
            DispatcherQueue.TryEnqueue(() =>
            {
                Log.Information("应用状态改变: {AppName} -> {Status}", result.AppConfig.Name, result.Status);
            });
        }

        /// <summary>
        /// 进程退出事件处理
        /// Process exited event handler
        /// </summary>
        /// <param name="sender">事件发送者 / Event sender</param>
        /// <param name="data">应用ID和退出代码 / App ID and exit code</param>
        private void OnProcessExited(object? sender, (string AppId, int ExitCode) data)
        {
            // 在 UI 线程上记录日志
            // Log on UI thread
            DispatcherQueue.TryEnqueue(() =>
            {
                Log.Information("应用进程已退出: {AppId}, 退出代码: {ExitCode}", data.AppId, data.ExitCode);
            });
        }
    }
}
