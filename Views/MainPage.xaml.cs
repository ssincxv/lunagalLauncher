using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using lunagalLauncher;
using lunagalLauncher.Core;
using lunagalLauncher.Data;
using lunagalLauncher.Services;
using lunagalLauncher.Utils;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Serilog;
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

        /// <summary>
        /// 每种 Page 类型的预挂载完成信号：Task.CompletedTask 表示已就绪。
        /// NavigateToPage 在页面尚未就绪时会 await 这个 Task，避免用户在预加载期间点切换
        /// 触发同步构造造成主 UI 线程卡顿——用户看到的只是"页面瞬间出现"（如果预加载跑得快）
        /// 或"短暂 loading 后自然切入"（如果抢在预加载之前）。
        /// </summary>
        private readonly Dictionary<Type, Task> _pagePreloadTasks = new Dictionary<Type, Task>();

        /// <summary>最近一次真正的内容页导航 Tag（排除 Go!/导出/导入），用于全量导入后恢复内容区。</summary>
        private string _lastNonActionNavTag = "AppManagement";

        /// <summary>
        /// 与分帧预建、<see cref="NavigateToPage"/> fallback 共享，避免预建与抢先导航并发各 new 一份同一 Page。
        /// </summary>
        private readonly object _pageCacheLock = new object();

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

            //
            // 首开 A+B：ctor 只同步 AppManagement；Log / 鼠标 / Llama 在 MainPage_Loaded 后 Low 分帧预建。
            // 若用户快于预建时点侧栏，NavigateToPage 内同步 fallback（等同纯 A 兜底）。
            //
            try
            {
                var totalSw = System.Diagnostics.Stopwatch.StartNew();

                var appMgmt = new AppManagementPage();
                lock (_pageCacheLock)
                {
                    _pageCache[typeof(AppManagementPage)] = appMgmt;
                    _pagePreloadTasks[typeof(AppManagementPage)] = Task.CompletedTask;
                }

                appMgmt.SetLaunchManager(_launchManager);
                ContentFrame.Content = appMgmt;

                totalSw.Stop();
                Log.Information("MainPage 首屏同步完成（仅 AppManagement，{Ms}ms）；其余三页将在 Loaded 后 Low 分帧预建",
                    totalSw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "首屏构造失败");
            }

            // 设置默认选中第一个导航项（此时 ContentFrame 已是 AppManagement，
            // SelectionChanged 触发的 NavigateToPage 会缓存命中、无视觉变化）。
            if (MainNavigationView.MenuItems.Count > 0)
            {
                MainNavigationView.SelectedItem = MainNavigationView.MenuItems[0];
            }

            // 预加载在 MainPage_Loaded 里触发（届时 PagePrewarmStage 已经挂进 XAML tree，子 Page 的 Loaded 才能正确触发）

            // 为导航项添加按压动画
            // Add press animations to navigation items
            this.Loaded += MainPage_Loaded;

            // 页脚整体上移：仅用 RenderTransform + 清除页脚 ScrollViewer 内部 Clip；勿在页脚项上设 Stretch/大 Padding（收起侧栏会裁切）。
            MainNavigationView.Loaded += (_, __) => ScheduleApplyFooterVisualLift();
            MainNavigationView.SizeChanged += (_, __) => TryApplyFooterVisualLift();
        }

        /// <summary>页脚四键整体上移的像素（负向 TranslateTransform.Y）。</summary>
        private const double NavigationFooterVisualLiftPixels = 15;

        /// <summary>
        /// 在 NavigationView 模板展开后排队应用页脚上移（Low/Normal/High 各一次）。
        /// </summary>
        private void ScheduleApplyFooterVisualLift()
        {
            TryApplyFooterVisualLift();
            DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Low, TryApplyFooterVisualLift);
            DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.Normal, TryApplyFooterVisualLift);
            DispatcherQueue?.TryEnqueue(DispatcherQueuePriority.High, TryApplyFooterVisualLift);
        }

        /// <summary>
        /// 将包住页脚菜单的 ScrollViewer 整体上移，底部留出空隙；页脚项保持默认紧凑模板以兼容侧栏收起。
        /// </summary>
        private void TryApplyFooterVisualLift()
        {
            try
            {
                if (LaunchAllNavItem == null)
                {
                    return;
                }

                var footerScroll = FindFirstAncestorOfType<ScrollViewer>(LaunchAllNavItem);
                if (footerScroll == null)
                {
                    return;
                }

                TryClearScrollContentPresenterClip(footerScroll);
                footerScroll.Clip = null;
                footerScroll.RenderTransform = new TranslateTransform { Y = -NavigationFooterVisualLiftPixels };
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "页脚整体上移跳过");
            }
        }

        private static T? FindFirstAncestorOfType<T>(DependencyObject? start) where T : class
        {
            for (var current = VisualTreeHelper.GetParent(start); current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is T match)
                {
                    return match;
                }
            }

            return null;
        }

        private static void TryClearScrollContentPresenterClip(DependencyObject scrollViewer, int depth = 0)
        {
            if (depth > 12)
            {
                return;
            }

            int count = VisualTreeHelper.GetChildrenCount(scrollViewer);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(scrollViewer, i);
                if (child is UIElement ue && string.Equals(child.GetType().Name, "ScrollContentPresenter", StringComparison.Ordinal))
                {
                    ue.Clip = null;
                    return;
                }

                TryClearScrollContentPresenterClip(child, depth + 1);
            }
        }

        /// <summary>
        /// 页面加载完成后：仅排队 Low 链（三页预建 → 导航按压动画 → 鼠标映射引擎），避免在 Loaded 同步尾占用 UI 与输入栈抢时间片。
        /// </summary>
        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            Log.Debug("MainPage_Loaded：开始排队分帧预建链（导航动画与 InitializeUi 在链尾 Low 执行）");
            ScheduleDeferredPagePreloads();
            ScheduleApplyFooterVisualLift();
        }

        /// <summary>
        /// 将工作投递到 UI 线程 Low 队列，便于预建 Page 之间插入输入与布局等高优先级项。
        /// </summary>
        private void EnqueueMainPageLow(Action work)
        {
            if (DispatcherQueue == null)
            {
                Log.Warning("DispatcherQueue 为空，无法分帧预挂载子页");
                return;
            }

            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "MainPage Low 预挂载任务异常: {Message}", ex.Message);
                }
            });
        }

        /// <summary>
        /// 首帧后链式预建 LogViewer → MouseMapping → LlamaService，再在独立 Low 项中挂导航动画与鼠标引擎（避免与首帧指针/布局同段执行）。
        /// </summary>
        private void ScheduleDeferredPagePreloads()
        {
            EnqueueMainPageLow(() =>
            {
                TryAddPageToCache(typeof(LogViewerPage), () => new LogViewerPage());
                EnqueueMainPageLow(() =>
                {
                    TryAddPageToCache(typeof(MouseMappingPage), () => new MouseMappingPage());
                    EnqueueMainPageLow(() =>
                    {
                        TryAddPageToCache(typeof(LlamaServicePage), () => new LlamaServicePage());
                        Log.Information("MainPage：三页 Low 分帧预挂载链已调度完毕（若已存在则跳过）");

                        EnqueueMainPageLow(() =>
                        {
                            var sw = Stopwatch.StartNew();
                            AttachNavigationItemAnimations();
                            sw.Stop();
                            Log.Information("MainPage：AttachNavigationItemAnimations 完成，耗时 {Ms}ms", sw.ElapsedMilliseconds);

                            EnqueueMainPageLow(() =>
                            {
                                sw = Stopwatch.StartNew();
                                try
                                {
                                    MouseMappingRuntime.InitializeUi(DispatcherQueue);
                                }
                                catch (Exception ex)
                                {
                                    Log.Warning(ex, "鼠标映射运行时初始化失败");
                                }
                                finally
                                {
                                    sw.Stop();
                                    Log.Information("MainPage：MouseMappingRuntime.InitializeUi 完成，耗时 {Ms}ms", sw.ElapsedMilliseconds);
                                }
                            });
                        });
                    });
                });
            });
        }

        /// <summary>
        /// 若类型尚未在缓存中则构造并登记；与 <see cref="NavigateToPage"/> fallback 共用锁。
        /// </summary>
        private void TryAddPageToCache(Type pageType, Func<Page> factory)
        {
            lock (_pageCacheLock)
            {
                if (_pageCache.ContainsKey(pageType))
                {
                    return;
                }

                var sw = Stopwatch.StartNew();
                _pageCache[pageType] = factory();
                sw.Stop();
                _pagePreloadTasks[pageType] = Task.CompletedTask;
                Log.Information("MainPage：已预挂载 {Page}（InitializeComponent 等 {Ms}ms）", pageType.Name, sw.ElapsedMilliseconds);
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

        /// <summary>
        /// 在资源管理器中打开当前配置所在目录（便携模式为 exe\setting，否则为 %APPDATA%\lunagalLauncher）。
        /// </summary>
        private async void OpenSettingsFolderNavItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                string? configPath = App.ConfigManager.ConfigFilePath;
                string? dir = Path.GetDirectoryName(configPath);
                if (string.IsNullOrEmpty(dir))
                {
                    await ShowBackupMessageAsync("无法打开", "未能解析配置目录路径。");
                    return;
                }

                Directory.CreateDirectory(dir);

                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"\"{dir}\"",
                    UseShellExecute = true
                });

                Log.Information("已打开设置目录: {Dir}", dir);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开设置目录失败");
                await ShowBackupMessageAsync("无法打开文件夹", ex.Message);
            }
        }

        private async void ImportAllSettingsNavItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
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

                // 导入完成后：在清缓存前若已有 MouseMapping 实例则先按新配置刷新 UI。
                lock (_pageCacheLock)
                {
                    if (_pageCache.TryGetValue(typeof(MouseMappingPage), out var mmpRaw) && mmpRaw is MouseMappingPage mmp)
                    {
                        mmp.ReapplyMouseMappingConfigToUi();
                    }
                }

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
            lock (_pageCacheLock)
            {
                if (_pageCache.TryGetValue(typeof(MouseMappingPage), out var page) && page is MouseMappingPage mmp)
                {
                    mmp.FlushPendingMouseMappingToConfig();
                }
            }
        }

        private void RefreshUiAfterFullConfigImport()
        {
            lock (_pageCacheLock)
            {
                _pageCache.Clear();
                _pagePreloadTasks.Clear();
            }

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

            ScheduleDeferredPagePreloads();
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
            if (!App.TryGetMainWindowHandle(out var hwnd))
            {
                Log.Error("无法获取应用程序窗口实例");
                return null;
            }

            return await Win32FileDialog.ShowSaveFileDialogAsync(hwnd, "JSON (*.json)|*.json", "导出完整设置", suggested);
        }

        private async Task<string?> PickOpenFullBackupPathAsync()
        {
            if (!App.TryGetMainWindowHandle(out var hwnd))
            {
                Log.Error("无法获取应用程序窗口实例");
                return null;
            }

            const string filter = "JSON (*.json)|*.json";
            return await Win32FileDialog.ShowOpenFileDialogAsync(hwnd, filter, "选择完整设置备份");
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
                var skippedCount = results.Count(r => r.Status == LaunchStatus.SkippedAlreadyRunning);

                Log.Information("所有应用启动完成 - 成功: {Success}, 失败: {Failed}, 跳过(已运行): {Skipped}", successCount, failedCount, skippedCount);

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

                    // ctor 已把 AppManagement 放入 ContentFrame；设置 SelectedItem 首次触发本分支时
                    // NavigateToPage(AppManagement) 通常缓存命中、无视觉变化。

                    Log.Information("导航到: {Tag}", tag);

                    // 根据标签导航到不同页面
                    // Navigate to different pages based on tag
                    switch (tag)
                    {
                        case "LaunchAll":
                        case "ExportAllSettings":
                        case "OpenSettingsFolder":
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
        /// 导航到指定页面：优先 <see cref="_pageCache"/>；未命中则同步构造 fallback（快于 Low 预建时）。
        /// </summary>
        private void NavigateToPage(Type pageType)
        {
            try
            {
                lock (_pageCacheLock)
                {
                    if (_pageCache.TryGetValue(pageType, out var cachedPage))
                    {
                        if (!ReferenceEquals(ContentFrame.Content, cachedPage))
                        {
                            AnimatePageTransition(cachedPage);
                            Log.Debug("切换到页面: {PageType}", pageType.Name);
                        }

                        return;
                    }

                    Log.Warning("{PageType} 未在缓存中；走同步构造 fallback（可能快于 Low 预建）", pageType.Name);
                    Page? fallback = null;
                    if (pageType == typeof(LogViewerPage)) fallback = new LogViewerPage();
                    else if (pageType == typeof(MouseMappingPage)) fallback = new MouseMappingPage();
                    else if (pageType == typeof(LlamaServicePage)) fallback = new LlamaServicePage();
                    else if (pageType == typeof(AppManagementPage))
                    {
                        var amp = new AppManagementPage();
                        amp.SetLaunchManager(_launchManager);
                        fallback = amp;
                    }

                    if (fallback != null)
                    {
                        _pageCache[pageType] = fallback;
                        _pagePreloadTasks[pageType] = Task.CompletedTask;
                        AnimatePageTransition(fallback);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "导航到页面失败: {PageType}, {Message}", pageType.Name, ex.Message);
            }
        }

        /// <summary>
        /// 页面切换：瞬切（不做过渡动画），直接切换 <c>ContentFrame.Content</c>。
        /// </summary>
        private void AnimatePageTransition(Page newPage)
        {
            newPage.Opacity = 1;
            newPage.Translation = new System.Numerics.Vector3(0, 0, 0);
            ContentFrame.Content = newPage;
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
