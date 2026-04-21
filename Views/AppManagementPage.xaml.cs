using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Serilog;
using Windows.UI;
using lunagalLauncher.Data;
using lunagalLauncher.Core;
using lunagalLauncher.Utils;
using WinRT.Interop;

namespace lunagalLauncher.Views
{
    /// <summary>
    /// 应用项视图模型
    /// Application item view model for data binding
    /// </summary>
    public class AppItemViewModel : System.ComponentModel.INotifyPropertyChanged
    {
        private bool _enabled;
        private bool _isRunning;
        private bool _canDelete;
        private string _name = string.Empty;
        private string _path = string.Empty;
        private string _customIconPath = string.Empty;
        private Microsoft.UI.Xaml.Media.ImageSource? _iconSource;

        /// <summary>应用ID / Application ID</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>应用名称 / Application name</summary>
        public string Name
        {
            get => _name;
            set
            {
                if (_name != value)
                {
                    _name = value;
                    OnPropertyChanged(nameof(Name));
                }
            }
        }

        /// <summary>应用路径 / Application path</summary>
        public string Path
        {
            get => _path;
            set
            {
                if (_path != value)
                {
                    _path = value;
                    OnPropertyChanged(nameof(Path));
                }
            }
        }

        /// <summary>自定义图标路径 / Custom icon path</summary>
        public string CustomIconPath
        {
            get => _customIconPath;
            set
            {
                if (_customIconPath != value)
                {
                    _customIconPath = value;
                    OnPropertyChanged(nameof(CustomIconPath));
                }
            }
        }

        /// <summary>图标源 / Icon source</summary>
        public Microsoft.UI.Xaml.Media.ImageSource? IconSource
        {
            get => _iconSource;
            set
            {
                if (_iconSource != value)
                {
                    _iconSource = value;
                    OnPropertyChanged(nameof(IconSource));
                    OnPropertyChanged(nameof(HasCustomIcon));
                    OnPropertyChanged(nameof(IconVisibility));
                    OnPropertyChanged(nameof(DefaultIconVisibility));
                }
            }
        }

        /// <summary>是否有自定义图标 / Whether has custom icon</summary>
        public bool HasCustomIcon => IconSource != null;

        /// <summary>图标可见性 / Icon visibility</summary>
        public Visibility IconVisibility => IconSource != null ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>默认图标可见性 / Default icon visibility</summary>
        public Visibility DefaultIconVisibility => IconSource != null ? Visibility.Collapsed : Visibility.Visible;

        /// <summary>是否启用 / Whether enabled</summary>
        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled != value)
                {
                    _enabled = value;
                    OnPropertyChanged(nameof(Enabled));

                    // 触发配置保存事件
                    // Trigger configuration save event
                    EnabledChanged?.Invoke(this, EventArgs.Empty);
                }
            }
        }

        /// <summary>是否正在运行 / Whether running</summary>
        public bool IsRunning
        {
            get => _isRunning;
            set
            {
                if (_isRunning != value)
                {
                    _isRunning = value;
                    OnPropertyChanged(nameof(IsRunning));
                    OnPropertyChanged(nameof(StatusText));
                    OnPropertyChanged(nameof(StatusColor));
                }
            }
        }

        /// <summary>是否可删除 / Whether can be deleted</summary>
        public bool CanDelete
        {
            get => _canDelete;
            set
            {
                if (_canDelete != value)
                {
                    _canDelete = value;
                    OnPropertyChanged(nameof(CanDelete));
                }
            }
        }

        /// <summary>
        /// 进程运行状态（与「是否启用启动」开关无关）
        /// </summary>
        public string StatusText =>
            IsRunning ? "● 运行中" : "● 未运行";

        /// <summary>状态颜色：运行中绿色，未运行灰色</summary>
        public SolidColorBrush StatusColor =>
            IsRunning
                ? new SolidColorBrush(Colors.LimeGreen)
                : new SolidColorBrush(Colors.Gray);

        /// <summary>启用状态改变事件 / Enabled state changed event</summary>
        public event EventHandler? EnabledChanged;

        /// <summary>属性改变事件 / Property changed event</summary>
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        /// <summary>触发属性改变事件 / Raise property changed event</summary>
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
        }
    }

    /// <summary>
    /// 应用管理页面
    /// Application management page
    /// </summary>
    public sealed partial class AppManagementPage : Page
    {
        /// <summary>
        /// 应用项集合
        /// Collection of application items
        /// </summary>
        public ObservableCollection<AppItemViewModel> AppItems { get; } = new ObservableCollection<AppItemViewModel>();

        /// <summary>
        /// 应用启动管理器
        /// Application launch manager
        /// </summary>
        private LaunchManager? _launchManager;

        /// <summary>
        /// 状态更新定时器
        /// Status update timer
        /// </summary>
        private DispatcherTimer? _statusUpdateTimer;

        /// <summary>
        /// 构造函数
        /// Constructor - initializes the application management page
        /// </summary>
        public AppManagementPage()
        {
            Log.Information("正在初始化应用管理页面...");
            this.InitializeComponent();
            LoadApplications();
            
            // 启动状态更新定时器
            // Start status update timer
            StartStatusUpdateTimer();
            
            Log.Information("应用管理页面初始化完成");
        }

        /// <summary>
        /// 设置启动管理器
        /// Sets the launch manager
        /// </summary>
        /// <param name="launchManager">启动管理器实例 / Launch manager instance</param>
        public void SetLaunchManager(LaunchManager launchManager)
        {
            _launchManager = launchManager;
            
            // 订阅事件
            // Subscribe to events
            if (_launchManager != null)
            {
                _launchManager.LaunchStatusChanged += OnLaunchStatusChanged;
                _launchManager.ProcessExited += OnProcessExited;
            }
            
            // 立即更新一次状态
            // Update status immediately
            UpdateAllAppStatus();
        }

        /// <summary>
        /// 启动状态更新定时器
        /// Starts the status update timer
        /// </summary>
        private void StartStatusUpdateTimer()
        {
            _statusUpdateTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(2) // 每2秒更新一次
            };
            _statusUpdateTimer.Tick += (s, e) => UpdateAllAppStatus();
            _statusUpdateTimer.Start();
        }

        /// <summary>
        /// 更新所有应用的运行状态
        /// Updates running status for all applications
        /// </summary>
        private void UpdateAllAppStatus()
        {
            foreach (var appItem in AppItems)
            {
                // 首先检查 LaunchManager 是否跟踪了这个进程
                // First check if LaunchManager is tracking this process
                bool isRunningInManager = _launchManager?.IsApplicationRunning(appItem.Id) ?? false;
                
                // 如果 LaunchManager 没有跟踪，检查系统中是否有该进程在运行
                // If not tracked by LaunchManager, check if process is running in system
                bool isRunningInSystem = ProcessDetector.IsProcessRunning(appItem.Path);
                
                // 只要有一个为 true，就认为是运行中
                // If either is true, consider it as running
                bool isRunning = isRunningInManager || isRunningInSystem;
                
                if (appItem.IsRunning != isRunning)
                {
                    appItem.IsRunning = isRunning;
                }
            }
        }

        /// <summary>
        /// 启动状态改变事件处理
        /// Launch status changed event handler
        /// </summary>
        private void OnLaunchStatusChanged(object? sender, LaunchResult result)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var appItem = AppItems.FirstOrDefault(a => a.Id == result.AppConfig.Id);
                if (appItem != null)
                {
                    bool isRunning = result.Status == LaunchStatus.Launched ||
                                     result.Status == LaunchStatus.SkippedAlreadyRunning;
                    if (appItem.IsRunning != isRunning)
                    {
                        appItem.IsRunning = isRunning;
                        Log.Information("应用状态已更新: {AppName} -> IsRunning={IsRunning}", appItem.Name, isRunning);
                    }
                }
            });
        }

        /// <summary>
        /// 进程退出事件处理
        /// Process exited event handler
        /// </summary>
        private void OnProcessExited(object? sender, (string AppId, int ExitCode) data)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                var appItem = AppItems.FirstOrDefault(a => a.Id == data.AppId);
                if (appItem != null)
                {
                    appItem.IsRunning = false;
                    Log.Information("应用进程已退出: {AppName}, 退出代码: {ExitCode}", appItem.Name, data.ExitCode);
                }
            });
        }

        /// <summary>
        /// 空状态时隐藏列表滚动区，使空状态 StackPanel 在内容区真正居中；有数据时显示列表。
        /// </summary>
        private void SyncAppListEmptyChrome(bool isEmpty)
        {
            EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
            AppListScrollViewer.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        }

        /// <summary>
        /// 将路径规范化为可比较形式，用于判断是否已添加同一可执行文件。
        /// </summary>
        private static string NormalizeLaunchAppPathForComparison(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return string.Empty;
            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                return path.Trim();
            }
        }

        /// <summary>
        /// 为单条配置创建列表项并绑定事件（供全量加载与单条追加共用，避免重复逻辑）。
        /// </summary>
        private AppItemViewModel CreateAppItemViewModel(AppConfig app)
        {
            var viewModel = new AppItemViewModel
            {
                Id = app.Id ?? string.Empty,
                Name = app.Name,
                Path = app.Path,
                CustomIconPath = app.CustomIconPath,
                Enabled = app.Enabled,
                CanDelete = !app.IsBuiltIn
            };

            LoadAppIcon(viewModel, app);
            viewModel.IsRunning = ProcessDetector.IsProcessRunning(app.Path);
            viewModel.EnabledChanged += (_, _) => OnAppEnabledChanged(viewModel);
            return viewModel;
        }

        /// <summary>
        /// 解析删除按钮所在行的 ViewModel（优先 DataContext，避免仅依赖 Tag 与配置 Id 不一致时失败）。
        /// </summary>
        private AppItemViewModel? ResolveDeleteRowViewModel(Button button)
        {
            if (button.DataContext is AppItemViewModel fromRow)
                return fromRow;
            if (button.Tag is string tag && !string.IsNullOrEmpty(tag))
                return AppItems.FirstOrDefault(v => string.Equals(v.Id, tag, StringComparison.Ordinal));
            return null;
        }

        /// <summary>
        /// 在配置中查找待删除项：先按 Id，再按规范化路径（导入/合并配置后 Id 可能与列表不一致时的回退）。
        /// </summary>
        private AppConfig? FindAppConfigForDeletion(AppItemViewModel itemVm)
        {
            var apps = App.AppConfig.LaunchSettings.Apps;
            if (apps == null || apps.Count == 0)
                return null;

            var app = apps.FirstOrDefault(a =>
                string.Equals(a.Id ?? string.Empty, itemVm.Id ?? string.Empty, StringComparison.Ordinal));
            if (app != null)
                return app;

            var norm = NormalizeLaunchAppPathForComparison(itemVm.Path);
            if (string.IsNullOrEmpty(norm))
                return null;

            return apps.FirstOrDefault(a =>
                string.Equals(NormalizeLaunchAppPathForComparison(a.Path), norm, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 加载应用程序列表
        /// Loads the application list
        /// </summary>
        private void LoadApplications()
        {
            try
            {
                Log.Information("正在加载应用程序列表...");

                // 清空现有列表
                // Clear existing list
                AppItems.Clear();

                // 获取应用配置列表
                // Get application configuration list
                var apps = App.AppConfig.LaunchSettings.Apps;

                if (apps == null || apps.Count == 0)
                {
                    Log.Information("没有配置任何应用程序");
                    SyncAppListEmptyChrome(true);
                    return;
                }

                foreach (var app in apps)
                    AppItems.Add(CreateAppItemViewModel(app));

                SyncAppListEmptyChrome(AppItems.Count == 0);

                Log.Information("已加载 {Count} 个应用程序", AppItems.Count);
                
                // 立即更新运行状态
                // Update running status immediately
                UpdateAllAppStatus();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载应用程序列表失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 编辑保存后仅同步单条 ViewModel，避免 <see cref="LoadApplications"/> 清空列表导致 ListView 复用行上 ToggleSwitch 换绑闪烁。
        /// </summary>
        private void RefreshAppItemViewModelFromConfig(string appId)
        {
            var app = App.AppConfig.LaunchSettings.Apps.FirstOrDefault(a => a.Id == appId);
            var vm = AppItems.FirstOrDefault(x => x.Id == appId);
            if (app == null || vm == null)
            {
                LoadApplications();
                return;
            }

            vm.Name = app.Name;
            vm.Path = app.Path;
            vm.CustomIconPath = app.CustomIconPath;
            vm.CanDelete = !app.IsBuiltIn;
            LoadAppIcon(vm, app);
            vm.IsRunning = ProcessDetector.IsProcessRunning(app.Path);
        }

        /// <summary>
        /// 加载应用图标
        /// Loads application icon
        /// </summary>
        /// <param name="viewModel">应用项视图模型 / Application item view model</param>
        /// <param name="appConfig">应用配置 / Application configuration</param>
        private void LoadAppIcon(AppItemViewModel viewModel, AppConfig appConfig)
        {
            try
            {
                // 优先使用自定义图标
                // Prefer custom icon
                if (!string.IsNullOrWhiteSpace(appConfig.CustomIconPath) && System.IO.File.Exists(appConfig.CustomIconPath))
                {
                    viewModel.IconSource = IconExtractor.LoadImageFromFile(appConfig.CustomIconPath);
                    if (viewModel.IconSource != null)
                    {
                        Log.Debug("已加载自定义图标: {AppName}", appConfig.Name);
                        return;
                    }
                }

                // 从可执行文件提取图标
                // Extract icon from executable
                if (!string.IsNullOrWhiteSpace(appConfig.Path) && System.IO.File.Exists(appConfig.Path))
                {
                    viewModel.IconSource = IconExtractor.ExtractIconFromExe(appConfig.Path);
                    if (viewModel.IconSource != null)
                    {
                        Log.Debug("已提取应用图标: {AppName}", appConfig.Name);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载应用图标失败: {AppName} - {Message}", appConfig.Name, ex.Message);
            }
        }

        /// <summary>
        /// 应用启用状态改变事件处理
        /// Application enabled state changed event handler
        /// </summary>
        /// <param name="viewModel">应用项视图模型 / Application item view model</param>
        private void OnAppEnabledChanged(AppItemViewModel viewModel)
        {
            try
            {
                Log.Information("应用启用状态改变: {AppName} -> {Enabled}", viewModel.Name, viewModel.Enabled);

                // 查找并更新配置
                // Find and update configuration
                var app = App.AppConfig.LaunchSettings.Apps.FirstOrDefault(a => a.Id == viewModel.Id);

                if (app != null)
                {
                    app.Enabled = viewModel.Enabled;

                    // 保存配置
                    // Save configuration
                    bool saved = App.ConfigManager.SaveConfig(App.AppConfig);

                    if (saved)
                    {
                        Log.Information("配置已自动保存");
                    }
                    else
                    {
                        Log.Error("自动保存配置失败");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理应用启用状态改变失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 添加应用按钮点击事件
        /// Add application button click event handler
        /// </summary>
        private async void AddAppButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log.Information("用户点击「添加应用」按钮");

                var launcher = (App)App.Current;
                if (launcher?.window == null)
                {
                    Log.Error("无法获取应用程序窗口实例");
                    return;
                }

                var hwnd = WindowNative.GetWindowHandle(launcher.window);
                var filePath = await Win32FileDialog.ShowOpenFileDialogAsync(
                    hwnd,
                    "可执行文件|*.exe;*.bat;*.cmd|所有文件|*.*",
                    "选择应用程序",
                    null);

                if (string.IsNullOrEmpty(filePath))
                {
                    Log.Information("用户取消了文件选择");
                    return;
                }

                var resolvedPath = NormalizeLaunchAppPathForComparison(filePath);
                if (string.IsNullOrEmpty(resolvedPath))
                {
                    Log.Warning("无法解析所选路径，已取消添加");
                    return;
                }

                bool duplicate = App.AppConfig.LaunchSettings.Apps.Any(a =>
                    string.Equals(
                        NormalizeLaunchAppPathForComparison(a.Path),
                        resolvedPath,
                        StringComparison.OrdinalIgnoreCase));
                if (duplicate)
                {
                    Log.Information("已存在相同可执行文件路径的应用，跳过添加: {Path}", resolvedPath);
                    return;
                }

                Log.Information("用户选择了文件: {FilePath}", resolvedPath);

                var newApp = new AppConfig
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = Path.GetFileNameWithoutExtension(resolvedPath),
                    Path = resolvedPath,
                    Enabled = true,
                    IsBuiltIn = false
                };

                App.AppConfig.LaunchSettings.Apps.Add(newApp);

                bool saved = App.ConfigManager.SaveConfig(App.AppConfig);

                if (saved)
                {
                    Log.Information("应用添加成功: {AppName}", newApp.Name);

                    AppItems.Add(CreateAppItemViewModel(newApp));
                    SyncAppListEmptyChrome(AppItems.Count == 0);
                    UpdateAllAppStatus();
                }
                else
                {
                    Log.Error("保存配置失败");
                    throw new Exception("保存配置失败");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "添加应用失败: {Message}", ex.Message);

                var dialog = new ContentDialog
                {
                    Title = "添加失败",
                    Content = $"添加应用时发生错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = GetDialogXamlRoot()
                };
                await dialog.ShowAsync(ContentDialogPlacement.Popup);
            }
        }

        /// <summary>
        /// 启动应用按钮点击事件
        /// Launch application button click event handler
        /// </summary>
        private async void LaunchAppButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is string appId)
                {
                    Log.Information("用户点击「启动应用」按钮: {AppId}", appId);

                    // 查找应用配置
                    // Find application configuration
                    var app = App.AppConfig.LaunchSettings.Apps.FirstOrDefault(a => a.Id == appId);

                    if (app == null)
                    {
                        throw new Exception("未找到应用配置");
                    }

                    // 检查是否已经在运行
                    // Check if already running
                    if (_launchManager != null && _launchManager.IsLaunchTargetAlreadyRunning(app))
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "提示",
                            Content = $"应用「{app.Name}」已经在运行中。",
                            CloseButtonText = "确定",
                            XamlRoot = GetDialogXamlRoot()
                        };
                        await dialog.ShowAsync(ContentDialogPlacement.Popup);
                        return;
                    }

                    // 使用 LaunchManager 启动应用
                    // Use LaunchManager to launch application
                    if (_launchManager != null)
                    {
                        var result = await _launchManager.LaunchApplicationAsync(app, minimized: app.LaunchMinimized);

                        if (result.Status == LaunchStatus.Launched || result.Status == LaunchStatus.SkippedAlreadyRunning)
                        {
                            Log.Information("应用启动成功: {AppName}", app.Name);
                            
                            // 更新状态
                            // Update status
                            UpdateAllAppStatus();
                        }
                        else
                        {
                            throw new Exception(result.ErrorMessage ?? "未知错误");
                        }
                    }
                    else
                    {
                        throw new Exception("启动管理器未初始化");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动应用失败: {Message}", ex.Message);

                var dialog = new ContentDialog
                {
                    Title = "启动失败",
                    Content = $"启动应用时发生错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = GetDialogXamlRoot()
                };
                await dialog.ShowAsync(ContentDialogPlacement.Popup);
            }
        }

        /// <summary>
        /// 编辑应用按钮点击事件
        /// Edit application button click event handler
        /// </summary>
        private async void EditAppButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is string appId)
                {
                    Log.Information("用户点击「编辑应用」按钮: {AppId}", appId);

                    // 查找应用配置
                    // Find application configuration
                    var app = App.AppConfig.LaunchSettings.Apps.FirstOrDefault(a => a.Id == appId);

                    if (app == null)
                    {
                        throw new Exception("未找到应用配置");
                    }

                    await ShowEditAppModalOverlayAsync(app);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "编辑应用失败: {Message}", ex.Message);

                var dialog = new ContentDialog
                {
                    Title = "编辑失败",
                    Content = $"编辑应用时发生错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = GetDialogXamlRoot()
                };
                await dialog.ShowAsync(ContentDialogPlacement.Popup);
            }
        }

        /// <summary>
        /// 编辑应用面板背景：不透明实色（避免使用 CardBackgroundFillColorDefaultBrush 等半透明主题画刷导致叠层透底）。
        /// </summary>
        private static SolidColorBrush CreateOpaqueEditAppDialogBackgroundBrush()
        {
            bool dark = Application.Current?.RequestedTheme == ApplicationTheme.Dark;
            // 浅色：与用户提供的参考色块（图2）一致 #FAFAFA，比纯白柔和、减轻刺眼感。
            Color c = dark
                ? Color.FromArgb(255, 45, 45, 48)
                : Color.FromArgb(255, 250, 250, 250);
            return new SolidColorBrush(c);
        }

        /// <summary>
        /// 使用主窗口全屏叠层 + 卡片 Horizontal/Vertical Center，在整窗范围内居中（绕过 ContentDialog 在 NavigationView 下的定位问题）。
        /// </summary>
        private async Task ShowEditAppModalOverlayAsync(AppConfig appConfig)
        {
            if (Application.Current is not App appSingleton || appSingleton.WindowModalOverlay is not { } overlay)
            {
                Log.Warning("WindowModalOverlay 未初始化，无法显示编辑应用面板");
                return;
            }

            overlay.Children.Clear();

            // 主题里的 CardBackgroundFillColorDefaultBrush 常为半透明（亚克力感），叠在整窗遮罩上会显得透底；此处用与 ContentDialog 面板一致的不透明实色。
            SolidColorBrush cardBg = CreateOpaqueEditAppDialogBackgroundBrush();
            Brush? strokeBrush = null;
            if (Application.Current.Resources.TryGetValue("CardStrokeColorDefaultBrush", out var stObj) && stObj is Brush stb)
                strokeBrush = stb;

            var nameTextBox = new TextBox
            {
                Header = "应用名称",
                Text = appConfig.Name,
                PlaceholderText = "输入应用名称",
                MinHeight = 36
            };

            var iconSection = new StackPanel { Spacing = 8 };
            iconSection.Children.Add(new TextBlock
            {
                Text = "自定义图标",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var iconGrid = new Grid { MinHeight = 36 };
            iconGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            iconGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            iconGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var iconPathTextBox = new TextBox
            {
                Text = appConfig.CustomIconPath,
                PlaceholderText = "选择图标文件（留空使用默认图标）",
                IsReadOnly = true,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 36
            };
            Grid.SetColumn(iconPathTextBox, 0);
            iconGrid.Children.Add(iconPathTextBox);

            var browseIconButton = new Button
            {
                Content = "浏览",
                MinHeight = 34,
                MinWidth = 72,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(browseIconButton, 1);
            iconGrid.Children.Add(browseIconButton);

            var clearIconButton = new Button
            {
                Content = "清除",
                MinHeight = 34,
                MinWidth = 72,
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(clearIconButton, 2);
            iconGrid.Children.Add(clearIconButton);
            iconSection.Children.Add(iconGrid);

            browseIconButton.Click += async (_, _) =>
            {
                var launcher = (App)App.Current;
                if (launcher?.window == null) return;
                var hwnd = WindowNative.GetWindowHandle(launcher.window);
                var initDir = Win32FileDialog.TryGetInitialDirectoryFromExistingPaths(new[] { iconPathTextBox.Text });
                var filePath = await Win32FileDialog.ShowOpenFileDialogAsync(
                    hwnd,
                    "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.ico|所有文件|*.*",
                    "选择图标文件",
                    initDir);
                if (!string.IsNullOrEmpty(filePath))
                    iconPathTextBox.Text = filePath;
            };
            clearIconButton.Click += (_, _) => { iconPathTextBox.Text = string.Empty; };

            var pathSection = new StackPanel { Spacing = 8 };
            pathSection.Children.Add(new TextBlock
            {
                Text = "应用路径",
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
            });
            var pathGrid = new Grid { MinHeight = 36 };
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var pathTextBox = new TextBox
            {
                Text = appConfig.Path,
                PlaceholderText = "应用程序路径",
                IsReadOnly = true,
                VerticalAlignment = VerticalAlignment.Center,
                MinHeight = 36
            };
            Grid.SetColumn(pathTextBox, 0);
            pathGrid.Children.Add(pathTextBox);

            var browsePathButton = new Button
            {
                Content = "浏览",
                MinHeight = 34,
                MinWidth = 72,
                Margin = new Thickness(10, 0, 0, 0)
            };
            Grid.SetColumn(browsePathButton, 1);
            pathGrid.Children.Add(browsePathButton);
            pathSection.Children.Add(pathGrid);

            browsePathButton.Click += async (_, _) =>
            {
                var launcher = (App)App.Current;
                if (launcher?.window == null) return;
                var hwnd = WindowNative.GetWindowHandle(launcher.window);
                var initDir = Win32FileDialog.TryGetInitialDirectoryFromExistingPaths(new[] { pathTextBox.Text });
                var filePath = await Win32FileDialog.ShowOpenFileDialogAsync(
                    hwnd,
                    "可执行文件|*.exe;*.bat;*.cmd|所有文件|*.*",
                    "选择应用程序",
                    initDir);
                if (!string.IsNullOrEmpty(filePath))
                    pathTextBox.Text = filePath;
            };

            var launchMinimizedSwitch = new ToggleSwitch
            {
                Header = "最小化启动",
                OnContent = "开",
                OffContent = "关",
                IsOn = appConfig.LaunchMinimized,
                MinWidth = 0,
                Margin = new Thickness(0, 2, 0, 0)
            };
            ToolTipService.SetToolTip(launchMinimizedSwitch,
                "开启后，从本程序启动该应用（含托盘菜单）时使用最小化窗口，适合常驻系统托盘的应用。");

            var fieldsStack = new StackPanel { Spacing = 14 };
            fieldsStack.Children.Add(nameTextBox);
            fieldsStack.Children.Add(iconSection);
            fieldsStack.Children.Add(pathSection);
            fieldsStack.Children.Add(launchMinimizedSwitch);

            var scroll = new ScrollViewer
            {
                MaxHeight = 400,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(0, 0, 4, 0),
                Content = fieldsStack
            };

            var saveBtn = new Button { Content = "保存", MinWidth = 100 };
            if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accObj) && accObj is Style accStyle)
                saveBtn.Style = accStyle;
            var cancelBtn = new Button { Content = "取消", MinWidth = 100 };

            var footer = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 8,
                Margin = new Thickness(0, 16, 0, 0)
            };
            footer.Children.Add(saveBtn);
            footer.Children.Add(cancelBtn);

            var titleBlock = new TextBlock
            {
                Text = "编辑应用",
                FontSize = 20,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 16)
            };

            var inner = new StackPanel { Spacing = 0 };
            inner.Children.Add(titleBlock);
            inner.Children.Add(scroll);
            inner.Children.Add(footer);

            var card = new Border
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MinWidth = 680,
                MaxWidth = 840,
                Padding = new Thickness(24),
                CornerRadius = new CornerRadius(8),
                Background = cardBg,
                Child = inner
            };
            if (strokeBrush != null)
            {
                card.BorderBrush = strokeBrush;
                card.BorderThickness = new Thickness(1);
            }

            var tcs = new TaskCompletionSource<bool>();

            void HideOverlay()
            {
                overlay.Visibility = Visibility.Collapsed;
                overlay.Children.Clear();
            }

            saveBtn.Click += async (_, _) =>
            {
                try
                {
                    appConfig.Name = nameTextBox.Text ?? string.Empty;
                    appConfig.Path = pathTextBox.Text ?? string.Empty;
                    appConfig.CustomIconPath = iconPathTextBox.Text ?? string.Empty;
                    appConfig.LaunchMinimized = launchMinimizedSwitch.IsOn;

                    bool saved = App.ConfigManager.SaveConfig(App.AppConfig);
                    HideOverlay();
                    if (!saved)
                        throw new Exception("保存配置失败");

                    Log.Information("应用编辑成功: {AppName}", appConfig.Name);
                    RefreshAppItemViewModelFromConfig(appConfig.Id);
                    tcs.TrySetResult(true);
                }
                catch (Exception ex)
                {
                    HideOverlay();
                    Log.Error(ex, "保存编辑应用失败: {Message}", ex.Message);
                    tcs.TrySetResult(false);
                    var errDialog = new ContentDialog
                    {
                        Title = "保存失败",
                        Content = ex.Message,
                        CloseButtonText = "确定",
                        XamlRoot = GetDialogXamlRoot()
                    };
                    await errDialog.ShowAsync(ContentDialogPlacement.Popup);
                }
            };

            cancelBtn.Click += (_, _) =>
            {
                HideOverlay();
                tcs.TrySetResult(false);
            };

            overlay.Children.Add(card);
            overlay.Visibility = Visibility.Visible;

            await tcs.Task;
        }

        /// <summary>
        /// 删除应用按钮点击事件
        /// Delete application button click event handler
        /// </summary>
        private async void DeleteAppButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is not Button button)
                    return;

                var itemVm = ResolveDeleteRowViewModel(button);
                if (itemVm == null)
                {
                    Log.Warning("删除应用：无法解析列表行");
                    throw new Exception("未找到应用配置");
                }

                Log.Information("用户点击「删除应用」按钮: Id={AppId}, Name={Name}", itemVm.Id, itemVm.Name);

                var app = FindAppConfigForDeletion(itemVm);

                if (app == null)
                {
                    Log.Warning("配置中无对应应用，仅从列表移除（可能为导入后 Id 不一致或残留行）: {Name} {Path}", itemVm.Name, itemVm.Path);
                    var row = AppItems.FirstOrDefault(v => ReferenceEquals(v, itemVm))
                              ?? AppItems.FirstOrDefault(v => string.Equals(v.Id, itemVm.Id, StringComparison.Ordinal));
                    if (row != null)
                        AppItems.Remove(row);
                    SyncAppListEmptyChrome(AppItems.Count == 0);
                    UpdateAllAppStatus();
                    return;
                }

                App.AppConfig.LaunchSettings.Apps.Remove(app);

                bool saved = App.ConfigManager.SaveConfig(App.AppConfig);

                if (saved)
                {
                    Log.Information("应用删除成功: {AppName}", app.Name);

                    var row = AppItems.FirstOrDefault(v => ReferenceEquals(v, itemVm))
                              ?? AppItems.FirstOrDefault(v => string.Equals(v.Id, itemVm.Id, StringComparison.Ordinal));
                    if (row != null)
                    {
                        AppItems.Remove(row);
                        SyncAppListEmptyChrome(AppItems.Count == 0);
                        UpdateAllAppStatus();
                    }
                    else
                    {
                        LoadApplications();
                    }
                }
                else
                {
                    throw new Exception("保存配置失败");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "删除应用失败: {Message}", ex.Message);

                var dialog = new ContentDialog
                {
                    Title = "删除失败",
                    Content = $"删除应用时发生错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = GetDialogXamlRoot()
                };
                await dialog.ShowAsync(ContentDialogPlacement.Popup);
            }
        }

        /// <summary>
        /// 使用主窗口根元素的 XamlRoot，使 ContentDialog 遮罩覆盖整窗并在窗口内居中（避免仅相对导航内容区偏左）。
        /// </summary>
        private XamlRoot GetDialogXamlRoot()
        {
            if (Application.Current is App app && app.window?.Content is FrameworkElement fe && fe.XamlRoot != null)
                return fe.XamlRoot;
            return this.XamlRoot;
        }
    }
}
