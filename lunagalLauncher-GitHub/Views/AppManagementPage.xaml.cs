using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Dispatching;
using Serilog;
using lunagalLauncher.Data;
using lunagalLauncher.Core;
using lunagalLauncher.Utils;

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
        private Microsoft.UI.Xaml.Media.ImageSource? _iconSource;

        /// <summary>应用ID / Application ID</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>应用名称 / Application name</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>应用路径 / Application path</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>自定义图标路径 / Custom icon path</summary>
        public string CustomIconPath { get; set; } = string.Empty;

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
        public bool CanDelete { get; set; }

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
                    bool isRunning = result.Status == LaunchStatus.Launched;
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

                // 添加应用到列表
                // Add applications to list
                foreach (var app in apps)
                {
                    var viewModel = new AppItemViewModel
                    {
                        Id = app.Id,
                        Name = app.Name,
                        Path = app.Path,
                        CustomIconPath = app.CustomIconPath,
                        Enabled = app.Enabled,
                        CanDelete = !app.IsBuiltIn
                    };

                    // 加载图标
                    // Load icon
                    LoadAppIcon(viewModel, app);

                    // 检测是否已经在运行
                    // Detect if already running
                    viewModel.IsRunning = ProcessDetector.IsProcessRunning(app.Path);

                    // 订阅启用状态改变事件
                    // Subscribe to enabled state changed event
                    viewModel.EnabledChanged += (sender, e) => OnAppEnabledChanged(viewModel);

                    AppItems.Add(viewModel);
                }

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

                // 使用 Win32 文件选择器
                // Use Win32 file picker
                var filePath = FilePickerHelper.PickApplicationFile();

                if (string.IsNullOrEmpty(filePath))
                {
                    Log.Information("用户取消了文件选择");
                    return;
                }

                Log.Information("用户选择了文件: {FilePath}", filePath);

                // 创建新的应用配置
                // Create new application configuration
                var newApp = new AppConfig
                {
                    Id = Guid.NewGuid().ToString(),
                    Name = System.IO.Path.GetFileNameWithoutExtension(filePath),
                    Path = filePath,
                    Enabled = true,
                    IsBuiltIn = false
                };

                // 添加到配置
                // Add to configuration
                App.AppConfig.LaunchSettings.Apps.Add(newApp);

                // 保存配置
                // Save configuration
                bool saved = App.ConfigManager.SaveConfig(App.AppConfig);

                if (saved)
                {
                    Log.Information("应用添加成功: {AppName}", newApp.Name);

                    // 刷新列表
                    // Refresh list
                    LoadApplications();
                    
                    // 更新状态
                    // Update status
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
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
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
                    if (_launchManager != null && _launchManager.IsApplicationRunning(appId))
                    {
                        var dialog = new ContentDialog
                        {
                            Title = "提示",
                            Content = $"应用「{app.Name}」已经在运行中。",
                            CloseButtonText = "确定",
                            XamlRoot = this.XamlRoot
                        };
                        await dialog.ShowAsync();
                        return;
                    }

                    // 使用 LaunchManager 启动应用
                    // Use LaunchManager to launch application
                    if (_launchManager != null)
                    {
                        var result = await _launchManager.LaunchApplicationAsync(app, minimized: false);

                        if (result.Status == LaunchStatus.Launched)
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
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
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

                    // 创建编辑对话框
                    // Create edit dialog
                    var dialog = new ContentDialog
                    {
                        Title = "编辑应用",
                        PrimaryButtonText = "保存",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = this.XamlRoot
                    };

                    // 创建对话框内容
                    // Create dialog content
                    var stackPanel = new StackPanel { Spacing = 12 };

                    // 应用名称
                    // Application name
                    var nameTextBox = new TextBox
                    {
                        Header = "应用名称",
                        Text = app.Name,
                        PlaceholderText = "输入应用名称"
                    };
                    stackPanel.Children.Add(nameTextBox);

                    // 应用路径
                    // Application path
                    var pathPanel = new StackPanel { Spacing = 8 };
                    var pathHeader = new TextBlock { Text = "应用路径", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                    pathPanel.Children.Add(pathHeader);

                    var pathGrid = new Grid();
                    pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    pathGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var pathTextBox = new TextBox
                    {
                        Text = app.Path,
                        PlaceholderText = "应用程序路径",
                        IsReadOnly = true
                    };
                    Grid.SetColumn(pathTextBox, 0);
                    pathGrid.Children.Add(pathTextBox);

                    var browsePathButton = new Button
                    {
                        Content = "浏览",
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(browsePathButton, 1);
                    pathGrid.Children.Add(browsePathButton);

                    pathPanel.Children.Add(pathGrid);
                    stackPanel.Children.Add(pathPanel);

                    // 浏览应用路径按钮事件
                    // Browse application path button event
                    browsePathButton.Click += (s, args) =>
                    {
                        var filePath = FilePickerHelper.PickApplicationFile();
                        if (!string.IsNullOrEmpty(filePath))
                        {
                            pathTextBox.Text = filePath;
                        }
                    };

                    // 自定义图标
                    // Custom icon
                    var iconPanel = new StackPanel { Spacing = 8 };
                    var iconHeader = new TextBlock { Text = "自定义图标", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                    iconPanel.Children.Add(iconHeader);

                    var iconGrid = new Grid();
                    iconGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    iconGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    iconGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var iconPathTextBox = new TextBox
                    {
                        Text = app.CustomIconPath,
                        PlaceholderText = "选择图标文件（留空使用默认图标）",
                        IsReadOnly = true
                    };
                    Grid.SetColumn(iconPathTextBox, 0);
                    iconGrid.Children.Add(iconPathTextBox);

                    var browseIconButton = new Button
                    {
                        Content = "浏览",
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(browseIconButton, 1);
                    iconGrid.Children.Add(browseIconButton);

                    var clearIconButton = new Button
                    {
                        Content = "清除",
                        Margin = new Thickness(8, 0, 0, 0)
                    };
                    Grid.SetColumn(clearIconButton, 2);
                    iconGrid.Children.Add(clearIconButton);

                    iconPanel.Children.Add(iconGrid);
                    stackPanel.Children.Add(iconPanel);

                    // 浏览图标按钮事件
                    // Browse icon button event
                    browseIconButton.Click += (s, args) =>
                    {
                        var filePath = FilePickerHelper.PickImageFile();
                        if (!string.IsNullOrEmpty(filePath))
                        {
                            iconPathTextBox.Text = filePath;
                        }
                    };

                    // 清除图标按钮事件
                    // Clear icon button event
                    clearIconButton.Click += (s, args) =>
                    {
                        iconPathTextBox.Text = string.Empty;
                    };

                    dialog.Content = stackPanel;

                    // 显示对话框
                    // Show dialog
                    var result = await dialog.ShowAsync();

                    if (result == ContentDialogResult.Primary)
                    {
                        // 保存更改
                        // Save changes
                        app.Name = nameTextBox.Text;
                        app.Path = pathTextBox.Text;
                        app.CustomIconPath = iconPathTextBox.Text;

                        // 保存配置
                        // Save configuration
                        bool saved = App.ConfigManager.SaveConfig(App.AppConfig);

                        if (saved)
                        {
                            Log.Information("应用编辑成功: {AppName}", app.Name);

                            // 刷新列表
                            // Refresh list
                            LoadApplications();

                            // 显示成功提示
                            // Show success notification
                            var successDialog = new ContentDialog
                            {
                                Title = "保存成功",
                                Content = "应用信息已更新！",
                                CloseButtonText = "确定",
                                XamlRoot = this.XamlRoot
                            };
                            await successDialog.ShowAsync();
                        }
                        else
                        {
                            throw new Exception("保存配置失败");
                        }
                    }
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
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        /// <summary>
        /// 删除应用按钮点击事件
        /// Delete application button click event handler
        /// </summary>
        private async void DeleteAppButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (sender is Button button && button.Tag is string appId)
                {
                    Log.Information("用户点击「删除应用」按钮: {AppId}", appId);

                    // 查找应用配置
                    // Find application configuration
                    var app = App.AppConfig.LaunchSettings.Apps.FirstOrDefault(a => a.Id == appId);

                    if (app == null)
                    {
                        throw new Exception("未找到应用配置");
                    }

                    // 确认删除
                    // Confirm deletion
                    var confirmDialog = new ContentDialog
                    {
                        Title = "确认删除",
                        Content = $"确定要删除应用「{app.Name}」吗？\n此操作无法撤销。",
                        PrimaryButtonText = "删除",
                        CloseButtonText = "取消",
                        DefaultButton = ContentDialogButton.Close,
                        XamlRoot = this.XamlRoot
                    };

                    var result = await confirmDialog.ShowAsync();

                    if (result == ContentDialogResult.Primary)
                    {
                        // 从配置中删除
                        // Remove from configuration
                        App.AppConfig.LaunchSettings.Apps.Remove(app);

                        // 保存配置
                        // Save configuration
                        bool saved = App.ConfigManager.SaveConfig(App.AppConfig);

                        if (saved)
                        {
                            Log.Information("应用删除成功: {AppName}", app.Name);

                            // 刷新列表
                            // Refresh list
                            LoadApplications();
                        }
                        else
                        {
                            throw new Exception("保存配置失败");
                        }
                    }
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
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }
    }
}
