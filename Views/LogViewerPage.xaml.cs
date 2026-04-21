using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using lunagalLauncher.Infrastructure;
using Serilog;
using Windows.System;
using Windows.UI.Core;

namespace lunagalLauncher.Views
{
    /// <summary>
    /// 日志项视图模型
    /// Log item view model for data binding
    /// </summary>
    public class LogItemViewModel
    {
        /// <summary>时间戳 / Timestamp</summary>
        public string Timestamp { get; set; } = string.Empty;

        /// <summary>日志级别 / Log level</summary>
        public string Level { get; set; } = string.Empty;

        /// <summary>日志消息 / Log message</summary>
        public string Message { get; set; } = string.Empty;

        /// <summary>日志级别颜色 / Log level color</summary>
        public SolidColorBrush LevelBrush { get; set; } = new SolidColorBrush(Colors.Gray);

        /// <summary>背景颜色 / Background color</summary>
        public SolidColorBrush BackgroundBrush { get; set; } = new SolidColorBrush(Colors.Transparent);
    }

    /// <summary>
    /// 日志查看器页面
    /// Log viewer page for displaying application logs
    /// </summary>
    public sealed partial class LogViewerPage : Page
    {
        /// <summary>
        /// 最大日志保留数量（防止内存溢出）
        /// Maximum log retention count (prevent memory overflow)
        /// </summary>
        private const int MAX_LOG_ITEMS = 5000;

        /// <summary>
        /// 日志项集合
        /// Collection of log items
        /// </summary>
        public ObservableCollection<LogItemViewModel> LogItems { get; } = new ObservableCollection<LogItemViewModel>();

        /// <summary>
        /// 所有日志项（未筛选）
        /// All log items (unfiltered)
        /// </summary>
        private readonly ObservableCollection<LogItemViewModel> _allLogItems = new ObservableCollection<LogItemViewModel>();

        /// <summary>
        /// 日志文件路径
        /// Log file path
        /// </summary>
        private string _logFilePath = string.Empty;

        /// <summary>
        /// 日志文件监视器
        /// Log file watcher
        /// </summary>
        private FileSystemWatcher? _fileWatcher;

        /// <summary>
        /// 上次读取的文件位置
        /// Last read file position
        /// </summary>
        private long _lastFilePosition = 0;

        /// <summary>
        /// 是否正在加载
        /// Whether currently loading
        /// </summary>
        private bool _isLoading = false;

        /// <summary>
        /// 用户「清空」后：刷新只追文件末尾新内容，不把历史再从磁盘载入；按住 Shift 点刷新可恢复整文件重载。
        /// </summary>
        private bool _tailOnlyModeAfterClear;

        /// <summary>
        /// 构造函数
        /// Constructor - initializes the log viewer page
        /// </summary>
        public LogViewerPage()
        {
            Log.Information("正在初始化日志查看器页面...");
            this.InitializeComponent();

            // 隐藏加载状态和空状态
            // Hide loading and empty states
            LoadingPanel.Visibility = Visibility.Collapsed;
            EmptyStatePanel.Visibility = Visibility.Collapsed;

            // 与 Serilog 一致：主程序目录下的 Logs\lunagalLauncher-yyyyMMdd.log
            _logFilePath = LoggerManager.GetTodayLogFilePath();

            // 异步初始化（不阻塞构造函数）
            // Async initialization (non-blocking)
            _ = InitializeAsync();

            Log.Information("日志查看器页面初始化完成");
        }

        /// <summary>
        /// 异步初始化日志查看器
        /// Async initialization of log viewer
        /// </summary>
        private async Task InitializeAsync()
        {
            try
            {
                // 立即加载现有日志（不阻塞UI）
                // Load existing logs immediately (non-blocking)
                await LoadExistingLogsAsync();

                // 启动文件监视器
                // Start file watcher
                StartFileWatcher();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "异步初始化日志查看器失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 启动文件监视器
        /// Starts the file watcher
        /// </summary>
        private void StartFileWatcher()
        {
            try
            {
                var logFolderPath = Path.GetDirectoryName(_logFilePath);
                if (string.IsNullOrEmpty(logFolderPath) || !Directory.Exists(logFolderPath))
                {
                    Log.Warning("日志文件夹不存在: {LogFolderPath}", logFolderPath);
                    return;
                }

                _fileWatcher = new FileSystemWatcher(logFolderPath)
                {
                    Filter = Path.GetFileName(_logFilePath),
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size
                };

                _fileWatcher.Changed += OnLogFileChanged;
                _fileWatcher.EnableRaisingEvents = true;

                Log.Information("文件监视器已启动: {LogFilePath}", _logFilePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "启动文件监视器失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 日志文件改变事件
        /// Log file changed event handler
        /// </summary>
        private async void OnLogFileChanged(object sender, FileSystemEventArgs e)
        {
            // 防止重复触发
            // Prevent duplicate triggers
            if (_isLoading)
                return;

            _isLoading = true;

            try
            {
                // 延迟一小段时间，确保文件写入完成
                // Delay a bit to ensure file write is complete
                await Task.Delay(100);

                // 读取新增的日志行
                // Read new log lines
                await ReadNewLogLinesAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "读取新日志行失败: {Message}", ex.Message);
            }
            finally
            {
                _isLoading = false;
            }
        }

        /// <summary>
        /// 加载现有日志
        /// Loads existing logs
        /// </summary>
        private async Task LoadExistingLogsAsync()
        {
            try
            {
                if (!File.Exists(_logFilePath))
                {
                    Log.Information("日志文件不存在，等待创建: {LogFilePath}", _logFilePath);
                    return;
                }

                await ReadNewLogLinesAsync();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "加载现有日志失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 读取新增的日志行
        /// Reads new log lines
        /// </summary>
        private async Task ReadNewLogLinesAsync()
        {
            try
            {
                if (!File.Exists(_logFilePath))
                    return;

                using var fileStream = new FileStream(
                    _logFilePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);

                // 跳到上次读取的位置
                // Seek to last read position
                fileStream.Seek(_lastFilePosition, SeekOrigin.Begin);

                using var reader = new StreamReader(fileStream);
                var newLines = new List<string>();

                string? line;
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        newLines.Add(line);
                    }
                }

                // 更新文件位置
                // Update file position
                _lastFilePosition = fileStream.Position;

                // 在UI线程上添加新日志
                // Add new logs on UI thread
                if (newLines.Count > 0)
                {
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        // 批量添加日志项（性能优化）
                        // Batch add log items (performance optimization)
                        var itemsToAdd = new List<LogItemViewModel>();

                        foreach (var line in newLines)
                        {
                            var logItem = ParseLogLine(line);
                            if (logItem != null)
                            {
                                _allLogItems.Add(logItem);

                                // 检查是否符合当前筛选条件
                                // Check if matches current filter
                                if (ShouldIncludeLogItem(logItem))
                                {
                                    itemsToAdd.Add(logItem);
                                }
                            }
                        }

                        // 批量添加到 UI 集合
                        // Batch add to UI collection
                        foreach (var item in itemsToAdd)
                        {
                            LogItems.Add(item);
                        }

                        // 限制日志数量，防止内存溢出
                        // Limit log count to prevent memory overflow
                        TrimLogItems();

                        // 更新统计信息
                        // Update statistics
                        UpdateStatistics();

                        // 自动滚动到底部
                        // Auto scroll to bottom
                        if (AutoScrollToggle.IsOn)
                        {
                            ScrollToBottom();
                        }

                        // 隐藏空状态
                        // Hide empty state
                        EmptyStatePanel.Visibility = LogItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    });
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "读取新日志行失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 限制日志数量，防止内存溢出
        /// Trims log items to prevent memory overflow
        /// </summary>
        private void TrimLogItems()
        {
            try
            {
                // 如果超过最大数量，删除最旧的日志
                // If exceeds max count, remove oldest logs
                while (_allLogItems.Count > MAX_LOG_ITEMS)
                {
                    var oldestItem = _allLogItems[0];
                    _allLogItems.RemoveAt(0);

                    // 同时从显示列表中删除
                    // Also remove from display list
                    var displayItem = LogItems.FirstOrDefault(item =>
                        item.Timestamp == oldestItem.Timestamp &&
                        item.Message == oldestItem.Message);

                    if (displayItem != null)
                    {
                        LogItems.Remove(displayItem);
                    }
                }

                // 如果显示列表也超过限制，直接修剪
                // If display list also exceeds limit, trim directly
                while (LogItems.Count > MAX_LOG_ITEMS)
                {
                    LogItems.RemoveAt(0);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "修剪日志列表失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 检查日志项是否应该包含在当前筛选中
        /// Checks if log item should be included in current filter
        /// </summary>
        private bool ShouldIncludeLogItem(LogItemViewModel item)
        {
            var selectedIndex = LogLevelFilterComboBox.SelectedIndex;

            return selectedIndex switch
            {
                0 => true, // 全部
                1 => item.Level == "调试", // 调试
                2 => item.Level == "信息", // 信息
                3 => item.Level == "警告", // 警告
                4 => item.Level == "错误", // 错误
                5 => item.Level == "致命", // 致命
                _ => true
            };
        }

        /// <summary>
        /// 解析日志行
        /// Parses a log line
        /// </summary>
        /// <param name="line">日志行 / Log line</param>
        /// <returns>日志项视图模型 / Log item view model</returns>
        private LogItemViewModel? ParseLogLine(string line)
        {
            try
            {
                // Serilog 日志格式: [时间戳] [级别] 消息
                // Serilog log format: [Timestamp] [Level] Message
                // 例如: 2026-01-29 17:51:04.528 [INF] 应用程序启动

                // 检查最小长度
                // Check minimum length
                if (line.Length < 30)
                    return null;

                // 检查是否以日期开头 (YYYY-MM-DD)
                // Check if starts with date format
                if (!char.IsDigit(line[0]) || !char.IsDigit(line[1]) || !char.IsDigit(line[2]) || !char.IsDigit(line[3]))
                    return null;

                if (line.Length < 23 || line[4] != '-' || line[7] != '-' || line[10] != ' ')
                    return null;

                // 提取时间戳（前23个字符: YYYY-MM-DD HH:mm:ss.fff）
                // Extract timestamp (first 23 characters)
                var timestamp = line.Substring(0, 23).Trim();

                // 查找日志级别（从第24个字符开始）
                // Find log level (starting from character 24)
                var levelStart = line.IndexOf('[', 23);
                var levelEnd = line.IndexOf(']', levelStart);

                if (levelStart == -1 || levelEnd == -1 || levelStart >= line.Length || levelEnd >= line.Length)
                    return null;

                var level = line.Substring(levelStart + 1, levelEnd - levelStart - 1).Trim();

                // 提取消息（确保不越界）
                // Extract message (ensure no out of bounds)
                var messageStart = levelEnd + 1;
                if (messageStart >= line.Length)
                    return null;

                var message = line.Substring(messageStart).Trim();

                // 创建日志项
                // Create log item
                var logItem = new LogItemViewModel
                {
                    Timestamp = timestamp,
                    Level = GetLevelDisplayName(level),
                    Message = message,
                    LevelBrush = GetLevelBrush(level),
                    BackgroundBrush = GetBackgroundBrush(level)
                };

                return logItem;
            }
            catch (Exception)
            {
                // 静默失败，不记录日志避免递归
                // Fail silently to avoid recursive logging
                return null;
            }
        }

        /// <summary>
        /// 获取日志级别显示名称
        /// Gets the display name for a log level
        /// </summary>
        /// <param name="level">日志级别 / Log level</param>
        /// <returns>显示名称 / Display name</returns>
        private string GetLevelDisplayName(string level)
        {
            return level.ToUpper() switch
            {
                "DBG" => "调试",
                "INF" => "信息",
                "WRN" => "警告",
                "ERR" => "错误",
                "FTL" => "致命",
                _ => level
            };
        }

        /// <summary>
        /// 获取日志级别颜色
        /// Gets the color for a log level
        /// </summary>
        /// <param name="level">日志级别 / Log level</param>
        /// <returns>颜色画刷 / Color brush</returns>
        private SolidColorBrush GetLevelBrush(string level)
        {
            return level.ToUpper() switch
            {
                "DBG" => new SolidColorBrush(Colors.Gray),
                "INF" => new SolidColorBrush(Colors.DodgerBlue),
                "WRN" => new SolidColorBrush(Colors.Orange),
                "ERR" => new SolidColorBrush(Colors.Red),
                "FTL" => new SolidColorBrush(Colors.DarkRed),
                _ => new SolidColorBrush(Colors.Gray)
            };
        }

        /// <summary>
        /// 获取背景颜色
        /// Gets the background color for a log level
        /// </summary>
        /// <param name="level">日志级别 / Log level</param>
        /// <returns>背景颜色画刷 / Background color brush</returns>
        private SolidColorBrush GetBackgroundBrush(string level)
        {
            return level.ToUpper() switch
            {
                "ERR" => new SolidColorBrush(Windows.UI.Color.FromArgb(20, 231, 72, 86)),
                "FTL" => new SolidColorBrush(Windows.UI.Color.FromArgb(30, 139, 0, 0)),
                _ => new SolidColorBrush(Colors.Transparent)
            };
        }

        /// <summary>
        /// 应用日志级别筛选
        /// Applies log level filter
        /// </summary>
        private void ApplyFilter()
        {
            try
            {
                LogItems.Clear();

                var selectedIndex = LogLevelFilterComboBox.SelectedIndex;

                foreach (var item in _allLogItems)
                {
                    bool shouldInclude = selectedIndex switch
                    {
                        0 => true, // 全部
                        1 => item.Level == "调试", // 调试
                        2 => item.Level == "信息", // 信息
                        3 => item.Level == "警告", // 警告
                        4 => item.Level == "错误", // 错误
                        5 => item.Level == "致命", // 致命
                        _ => true
                    };

                    if (shouldInclude)
                    {
                        LogItems.Add(item);
                    }
                }

                Log.Debug("应用筛选后，显示 {Count} 条日志", LogItems.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "应用筛选失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 更新统计信息
        /// Updates statistics
        /// </summary>
        private void UpdateStatistics()
        {
            try
            {
                var totalCount = _allLogItems.Count;
                var errorCount = _allLogItems.Count(item => item.Level == "错误" || item.Level == "致命");
                var warningCount = _allLogItems.Count(item => item.Level == "警告");

                LogCountText.Text = $"总计: {totalCount} 条";
                ErrorCountText.Text = $"错误: {errorCount}";
                WarningCountText.Text = $"警告: {warningCount}";

                Log.Debug("统计信息更新 - 总计: {Total}, 错误: {Error}, 警告: {Warning}",
                    totalCount, errorCount, warningCount);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新统计信息失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 缓存的 ListView 内部 ScrollViewer（用于可靠的自动滚动）
        /// Cached ScrollViewer inside LogListView's template. We need it
        /// because ListView.ScrollIntoView is unreliable during rapid
        /// streaming: newly-added items haven't been measured/realized yet
        /// when ScrollIntoView is called, so it often no-ops or scrolls
        /// to an outdated offset. Driving the underlying ScrollViewer
        /// directly bypasses virtualization timing issues entirely.
        /// </summary>
        private Microsoft.UI.Xaml.Controls.ScrollViewer? _logScrollViewer;

        /// <summary>
        /// 在 LogListView 的 VisualTree 中查找内部 ScrollViewer
        /// Walks LogListView's visual tree to locate the ScrollViewer
        /// that actually owns the scroll offset. Caches the reference
        /// after first successful lookup. Returns null before the
        /// ListView's template has been applied (first frame).
        /// </summary>
        private Microsoft.UI.Xaml.Controls.ScrollViewer? ResolveLogScrollViewer()
        {
            if (_logScrollViewer != null) return _logScrollViewer;
            if (LogListView == null) return null;

            // 深度优先遍历查找第一个 ScrollViewer。
            // 使用 VisualTreeHelper 是 WinUI 3 唯一可靠的模板内部访问途径。
            _logScrollViewer = FindDescendant<Microsoft.UI.Xaml.Controls.ScrollViewer>(LogListView);
            return _logScrollViewer;
        }

        /// <summary>
        /// 通用 VisualTreeHelper 深度查找子元素
        /// Generic visual-tree descendant finder (depth-first).
        /// </summary>
        private static T? FindDescendant<T>(Microsoft.UI.Xaml.DependencyObject root) where T : Microsoft.UI.Xaml.DependencyObject
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                var nested = FindDescendant<T>(child);
                if (nested != null) return nested;
            }
            return null;
        }

        /// <summary>
        /// 从导航切换到本页时调用：增量读取日志文件并滚动到列表底部（最新一条）。
        /// </summary>
        public void ScrollToLatestWhenShown()
        {
            _ = ScrollToLatestWhenShownAsync();
        }

        /// <summary>仅根据当前列表滚动到底部（不读盘），用于布局稍晚就绪时的多次重试。</summary>
        public void NudgeScrollToLatest()
        {
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    ScrollToBottom();
                    DispatcherQueue.TryEnqueue(
                        Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                        ScrollToBottom);
                });
        }

        private async Task ScrollToLatestWhenShownAsync()
        {
            try
            {
                await ReadNewLogLinesAsync();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "切换至日志页时增量读取失败: {Message}", ex.Message);
            }

            NudgeScrollToLatest();
        }

        /// <summary>
        /// 滚动到底部（可靠实现）
        /// Scrolls the log list to the absolute bottom reliably.
        /// 
        /// 核心原理：
        ///   1. 放到 DispatcherQueue 的 Low 优先级回调里 —— 保证本轮布局 / 测量已经
        ///      完成，ScrollableHeight 才会包含新添加项的高度。
        ///   2. 直接在内部 ScrollViewer 上调用 ChangeView(null, ScrollableHeight, null)
        ///      —— 这是 WinUI 3 "滚到最底" 的唯一保证手段；不依赖虚拟化时序。
        ///   3. 带上 ScrollIntoView 作为后备 —— 如果 ScrollViewer 找不到（极端情况下
        ///      模板未展开），至少 ListView 的高层 API 还能尝试生效。
        /// 
        /// Reliable auto-scroll: dispatch low-priority to let layout settle,
        /// then call ChangeView(ScrollableHeight) on the inner ScrollViewer,
        /// which is the only WinUI 3 API that guarantees "stick to bottom"
        /// on a virtualized list.
        /// </summary>
        private void ScrollToBottom()
        {
            if (LogItems.Count == 0) return;

            // 延迟到布局完成后（Low 优先级 < Normal 优先级的 LogItems.Add）
            // Deferred so the just-Add-ed item has been measured & arranged
            // by the layout system before we compute ScrollableHeight.
            DispatcherQueue.TryEnqueue(
                Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
                () =>
                {
                    try
                    {
                        var sv = ResolveLogScrollViewer();
                        if (sv != null)
                        {
                            // 强制一次 UpdateLayout 再取 ScrollableHeight —— 避免
                            // 多条日志同一帧批量 Add 时仅第一次就绪的假象。
                            sv.UpdateLayout();
                            sv.ChangeView(null, sv.ScrollableHeight, null, disableAnimation: true);
                        }
                        else
                        {
                            // 后备：模板尚未展开时（极少见）退化到 ListView API
                            LogListView.ScrollIntoView(LogItems[LogItems.Count - 1]);
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Debug(ex, "滚动到底部失败: {Message}", ex.Message);
                    }
                });
        }

        /// <summary>
        /// "自动滚动"开关切换事件
        /// Fires when the user flips the AutoScroll toggle. When turning it ON
        /// we immediately scroll to the latest log so the user doesn't have to
        /// wait for the next incoming line to see what they switched it on for.
        /// </summary>
        private void AutoScrollToggle_Toggled(object sender, RoutedEventArgs e)
        {
            try
            {
                // OFF → ON: 立即吸底一次，让用户马上看到最新日志位置
                // Immediately snap to bottom on OFF→ON so the user sees latest
                if (AutoScrollToggle?.IsOn == true)
                {
                    Log.Information("用户开启日志自动滚动，立即滚动到最新日志");
                    ScrollToBottom();
                }
                else
                {
                    Log.Information("用户关闭日志自动滚动");
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "自动滚动开关切换处理失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 刷新按钮点击事件
        /// Refresh button click event handler
        /// </summary>
        private async void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log.Information("用户点击「刷新」按钮");

                if (IsShiftKeyDown())
                    _tailOnlyModeAfterClear = false;

                if (!_tailOnlyModeAfterClear)
                {
                    _allLogItems.Clear();
                    LogItems.Clear();
                    _lastFilePosition = 0;
                    await LoadExistingLogsAsync();
                }
                else
                {
                    _allLogItems.Clear();
                    LogItems.Clear();
                    _lastFilePosition = GetExistingFileLength(_logFilePath);
                    await ReadNewLogLinesAsync();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        UpdateStatistics();
                        EmptyStatePanel.Visibility = LogItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                    });
                }

                Log.Information("日志已刷新");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "刷新日志失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 清空按钮点击事件
        /// Clear button click event handler
        /// </summary>
        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log.Information("用户点击「清空」按钮");

                _tailOnlyModeAfterClear = true;
                _lastFilePosition = GetExistingFileLength(_logFilePath);

                _allLogItems.Clear();
                LogItems.Clear();
                UpdateStatistics();

                EmptyStatePanel.Visibility = Visibility.Visible;

                Log.Information("日志显示已清空");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清空日志显示失败: {Message}", ex.Message);
            }
        }

        private static long GetExistingFileLength(string path)
        {
            try
            {
                if (File.Exists(path))
                    return new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "读取日志文件长度失败");
            }
            return 0;
        }

        private static bool IsShiftKeyDown()
        {
            try
            {
                var state = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift);
                return state.HasFlag(CoreVirtualKeyStates.Down);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 打开日志文件夹按钮点击事件
        /// Open log folder button click event handler
        /// </summary>
        private async void OpenLogFolderButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Log.Information("用户点击「打开日志文件夹」按钮");

                var logFolderPath = LoggerManager.GetLogsDirectory();

                // 打开文件夹
                // Open folder
                await Windows.System.Launcher.LaunchFolderPathAsync(logFolderPath);

                Log.Information("已打开日志文件夹: {LogFolderPath}", logFolderPath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "打开日志文件夹失败: {Message}", ex.Message);

                var dialog = new ContentDialog
                {
                    Title = "打开失败",
                    Content = $"打开日志文件夹时发生错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        /// <summary>
        /// 日志级别筛选改变事件
        /// Log level filter changed event handler
        /// </summary>
        private void LogLevelFilterComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            try
            {
                Log.Information("用户改变日志级别筛选: {SelectedIndex}", LogLevelFilterComboBox.SelectedIndex);
                ApplyFilter();
                EmptyStatePanel.Visibility = LogItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "改变日志级别筛选失败: {Message}", ex.Message);
            }
        }
    }
}
