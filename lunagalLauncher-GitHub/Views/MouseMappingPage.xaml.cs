using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using lunagalLauncher.Data;
using lunagalLauncher.Infrastructure;
using lunagalLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Newtonsoft.Json;
using Serilog;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace lunagalLauncher.Views
{
    /// <summary>
    /// 鼠标映射配置页：规则列表、全局进程过滤，以及 Raw Input 桥接的预安装。
    /// </summary>
    /// <remarks>
    /// 本页诊断与操作轨迹写入 Serilog 静态类 <see cref="Serilog.Log"/>（与全应用共用同一管道）；
    /// 文件与级别在 <see cref="LoggerManager.Initialize"/> 中统一初始化；角色上类似 Node 侧的模块化 Winston，本仓库为 .NET 故采用 Serilog（见 <c>Infrastructure/LoggerManager.cs</c>）。
    /// </remarks>
    public sealed partial class MouseMappingPage : Page
    {
        /// <summary>统一日志消息前缀，便于在 lunagalLauncher-*.log 中快速过滤本页轨迹。</summary>
        private const string LogScope = "[鼠标映射]";
        private ObservableCollection<MouseMappingRule> _rules = new();
        private readonly ObservableCollection<string> _globalProcessItems = new();

        /// <summary>与规则行「过滤模式」文案一致，便于 IndexOfLabel 与配置枚举对齐。</summary>
        private static readonly string[] GlobalContextModeLabels =
        {
            "仅过滤名单内生效 (白名单)", "过滤名单内不生效 (黑名单)"
        };

        private bool _suppressGlobalToggle;
        private bool _suppressGlobalProcessToggle;
        /// <summary>从配置加载全局进程 UI 时跳过「打开开关默认黑名单」逻辑。</summary>
        private bool _applyingGlobalProcessFromConfig;
        /// <summary>从配置加载「任务栏/边缘」全局开关时跳过自动保存。</summary>
        private bool _suppressGlobalSpatialFromConfig;
        private bool _suppressAutosave;
        private DispatcherQueueTimer? _autosaveTimer;
        private readonly PointerEventHandler _pagePointerPressedHandler;

        public MouseMappingPage()
        {
            this.InitializeComponent();
            Log.Debug("{Scope} 页面构造完成", LogScope);
            _pagePointerPressedHandler = PageRoot_PointerPressed;
            Loaded += MouseMappingPage_Loaded;
            Unloaded += MouseMappingPage_Unloaded;
            GlobalEnabledSwitch.Toggled += GlobalEnabledSwitch_Toggled;
            GlobalProcessFilterSwitch.Toggled += GlobalProcessFilterSwitch_Toggled;
            GlobalDisableOnTaskbarSwitch.Toggled += GlobalSpatialSwitch_Toggled;
            GlobalDisableOnScreenEdgesSwitch.Toggled += GlobalSpatialSwitch_Toggled;
        }

        private void GlobalSpatialSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressGlobalSpatialFromConfig) return;
            Log.Information("{Scope} 用户切换全局任务栏/边缘限制（任务栏={T}，边缘={E}）", LogScope,
                GlobalDisableOnTaskbarSwitch.IsOn, GlobalDisableOnScreenEdgesSwitch.IsOn);
            ScheduleAutosave();
        }

        /// <summary>点击非「物理键」录入区域时移走焦点，结束录入状态（与 LostFocus 一致）。</summary>
        private void PageRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (IsUnderPhysicalKeyCaptureHost(e.OriginalSource as DependencyObject))
                return;
            MouseMappingRuleRow.RequestEndPhysicalKeyCaptureAll();
            TryClearPhysicalKeyBoxFocus();
            TryClearKeyComboBoxFocus(e.OriginalSource as DependencyObject);
            TryClearMouseMappingMsFieldFocus(e.OriginalSource as DependencyObject);
        }

        private static bool IsUnderPhysicalKeyCaptureHost(DependencyObject? d)
        {
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.Tag is string s && s == "PhysicalKeyCaptureHost")
                    return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        private void TryClearPhysicalKeyBoxFocus()
        {
            var fe = GetFocusedElementInPage();
            if (fe is not TextBox tb)
                return;
            if (!IsUnderPhysicalKeyCaptureHost(tb))
                return;
            MoveFocusToAddRuleButtonDeferred();
        }

        private const string MouseMappingMsFieldTag = "MouseMappingMsField";
        private const string KeyComboCaptureHostTag = "KeyComboCaptureHost";

        /// <summary>组合键栏：点击区域外结束录制并移走焦点（与毫秒字段同理，避免 WinUI 点击空白仍保留 TextBox 焦点）。</summary>
        private void TryClearKeyComboBoxFocus(DependencyObject? clickSource)
        {
            if (GetFocusedElementInPage() is not DependencyObject focused)
                return;
            var host = FindTaggedAncestor(focused, KeyComboCaptureHostTag);
            if (host == null)
                return;
            if (clickSource != null && IsDescendantOf(clickSource, host))
                return;
            MouseMappingRuleRow.RequestEndKeyComboRecordingAll();
            MoveFocusToAddRuleButtonDeferred();
        }

        /// <summary>规则名称、优先级、按住阈值、连发间隔：点击空白区域表示输入完毕并移走焦点（含同一块内的上下箭头）。</summary>
        private void TryClearMouseMappingMsFieldFocus(DependencyObject? clickSource)
        {
            if (GetFocusedElementInPage() is not DependencyObject focused)
                return;
            var host = FindTaggedAncestor(focused, MouseMappingMsFieldTag);
            if (host == null)
                return;
            if (clickSource != null && IsDescendantOf(clickSource, host))
                return;
            MoveFocusToAddRuleButtonDeferred();
        }

        /// <summary>WinUI 3 需使用带 XamlRoot 的重载，否则常返回 null，焦点无法移走。</summary>
        private DependencyObject? GetFocusedElementInPage()
        {
            if (XamlRoot != null)
                return FocusManager.GetFocusedElement(XamlRoot) as DependencyObject;
            return FocusManager.GetFocusedElement() as DependencyObject;
        }

        /// <summary>在指针路由完成后将焦点移到工具栏按钮，避免 TextBox 仍显示插入光标。</summary>
        private void MoveFocusToAddRuleButtonDeferred()
        {
            DispatcherQueue.GetForCurrentThread()?.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                _ = FocusManager.TryFocusAsync(AddRuleButton, FocusState.Programmatic);
            });
        }

        private static DependencyObject? FindTaggedAncestor(DependencyObject? d, string tag)
        {
            while (d != null)
            {
                if (d is FrameworkElement fe && fe.Tag is string s && s == tag)
                    return d;
                d = VisualTreeHelper.GetParent(d);
            }
            return null;
        }

        private static bool IsDescendantOf(DependencyObject? d, DependencyObject? ancestor)
        {
            while (d != null)
            {
                if (d == ancestor) return true;
                d = VisualTreeHelper.GetParent(d);
            }
            return false;
        }

        private void MouseMappingPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                this.AddHandler(UIElement.PointerPressedEvent, _pagePointerPressedHandler, true);

                try
                {
                    var app = (App)App.Current;
                    if (app?.window != null)
                    {
                        var hwnd = WindowNative.GetWindowHandle(app.window);
                        Log.Debug("{Scope} 正在为 HWND 预安装 Raw Input 桥接", LogScope);
                        RawInputMouseBridge.EnsureInstalled(hwnd);
                        Log.Information("{Scope} Raw Input 桥接已确保安装", LogScope);
                    }
                    else
                        Log.Warning("{Scope} 主窗口为空，跳过 Raw Input 预安装", LogScope);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "{Scope} Raw Input 预安装失败（可忽略）", LogScope);
                }

                var cfg = App.AppConfig.MouseMapping;
                _rules = new ObservableCollection<MouseMappingRule>(cfg.Rules ?? new List<MouseMappingRule>());
                RulesListView.ItemsSource = _rules;

                GlobalContextModeDropdownItems.ItemsSource = GlobalContextModeLabels;
                GlobalProcessItemsControl.ItemsSource = _globalProcessItems;
                GlobalContextModeDropdown.TextChanged += (_, _) => ScheduleAutosave();
                GlobalProcessDropdown.TextChanged += (_, _) => ScheduleAutosave();

                _applyingGlobalProcessFromConfig = true;
                _suppressGlobalProcessToggle = true;
                try
                {
                    GlobalProcessFilterSwitch.IsOn = cfg.GlobalRestrictToProcessList;
                    GlobalContextModeDropdown.Text =
                        GlobalContextModeLabels[Math.Clamp((int)cfg.GlobalContextMode, 0, GlobalContextModeLabels.Length - 1)];
                    _globalProcessItems.Clear();
                    if (cfg.GlobalProcessFilter != null)
                    {
                        foreach (var p in cfg.GlobalProcessFilter)
                        {
                            if (!string.IsNullOrWhiteSpace(p))
                                _globalProcessItems.Add(p.Trim());
                        }
                    }
                }
                finally
                {
                    _suppressGlobalProcessToggle = false;
                    _applyingGlobalProcessFromConfig = false;
                }
                UpdateGlobalProcessFilterDetailsVisibility();
                DispatcherQueue.GetForCurrentThread()?.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    foreach (var cb in FindVisualChildren<CheckBox>(GlobalProcessItemsControl))
                    {
                        if (cb.Tag is string)
                            cb.IsChecked = true;
                    }
                    UpdateGlobalProcessDropdownDisplayText();
                });

                _suppressGlobalToggle = true;
                try
                {
                    GlobalEnabledSwitch.IsOn = cfg.GlobalEnabled;
                }
                finally
                {
                    _suppressGlobalToggle = false;
                }

                _suppressGlobalSpatialFromConfig = true;
                try
                {
                    GlobalDisableOnTaskbarSwitch.IsOn = cfg.GlobalDisableOnTaskbar;
                    GlobalDisableOnScreenEdgesSwitch.IsOn = cfg.GlobalDisableOnScreenEdges;
                }
                finally
                {
                    _suppressGlobalSpatialFromConfig = false;
                }

                MouseMappingRuleRow.AnyRuleEditedFromUi += OnAnyRuleEditedFromUi;

                Log.Information(
                    "{Scope} 页面加载完成：总开关={GlobalOn}，全局进程过滤={GlobalProc}，任务栏全局禁用={T}，边缘全局禁用={E}，规则数={RuleCount}，全局名单项={ProcItems}",
                    LogScope,
                    cfg.GlobalEnabled,
                    cfg.GlobalRestrictToProcessList,
                    cfg.GlobalDisableOnTaskbar,
                    cfg.GlobalDisableOnScreenEdges,
                    _rules.Count,
                    _globalProcessItems.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Scope} 加载失败", LogScope);
            }
        }

        private void MouseMappingPage_Unloaded(object sender, RoutedEventArgs e)
        {
            try
            {
                this.RemoveHandler(UIElement.PointerPressedEvent, _pagePointerPressedHandler);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "{Scope} RemoveHandler PointerPressed 失败（忽略）", LogScope);
            }

            MouseMappingRuleRow.AnyRuleEditedFromUi -= OnAnyRuleEditedFromUi;
            if (_autosaveTimer != null)
            {
                _autosaveTimer.Stop();
                _autosaveTimer.Tick -= AutosaveTimer_Tick;
                _autosaveTimer = null;
            }

            Log.Debug("{Scope} 页面已卸载，已解除规则编辑事件与自动保存计时器", LogScope);
        }

        private void OnAnyRuleEditedFromUi(object? sender, EventArgs e)
        {
            if (_suppressAutosave) return;
            Log.Debug("{Scope} 规则行 UI 变更，准备合并自动保存", LogScope);
            ScheduleAutosave();
        }

        private void ScheduleAutosave()
        {
            var dq = DispatcherQueue.GetForCurrentThread();
            if (_autosaveTimer == null)
            {
                _autosaveTimer = dq.CreateTimer();
                _autosaveTimer.Interval = TimeSpan.FromMilliseconds(380);
                _autosaveTimer.Tick += AutosaveTimer_Tick;
                Log.Debug("{Scope} 已创建自动保存防抖计时器（380ms）", LogScope);
            }

            _autosaveTimer.Stop();
            _autosaveTimer.Start();
            Log.Debug("{Scope} 自动保存已重新调度", LogScope);
        }

        private void AutosaveTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            sender.Stop();
            try
            {
                Log.Debug("{Scope} 防抖计时器触发，开始持久化并应用运行时", LogScope);
                PersistMouseMappingToDiskAndApply();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Scope} 自动保存失败", LogScope);
            }
        }

        private void FlushAllRuleRowsFromUi()
        {
            foreach (var row in FindVisualChildren<MouseMappingRuleRow>(RulesListView))
                row.FlushFromUi();
        }

        private void GlobalEnabledSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressGlobalToggle) return;
            Log.Information("{Scope} 用户切换总开关 -> {Enabled}", LogScope, GlobalEnabledSwitch.IsOn);
            try
            {
                PersistMouseMappingToDiskAndApply();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Scope} 应用总开关失败", LogScope);
            }
        }

        private void PersistMouseMappingToDiskAndApply()
        {
            FlushAllRuleRowsFromUi();
            FlushGlobalProcessFilterFromUi();

            var cfg = App.AppConfig.MouseMapping;
            cfg.GlobalEnabled = GlobalEnabledSwitch.IsOn;
            cfg.HotReload = true;
            cfg.Rules = _rules.ToList();
            cfg.SchemaVersion = "2";

            App.ConfigManager.SaveConfig(App.AppConfig);
            MouseMappingRuntime.ApplyFromCurrentConfig();
            Log.Debug(
                "{Scope} 已落盘并应用运行时：总开关={On}，规则数={Count}，全局进程过滤模式索引={Mode}，全局任务栏禁用={T}，全局边缘禁用={E}",
                LogScope,
                cfg.GlobalEnabled,
                cfg.Rules.Count,
                (int)cfg.GlobalContextMode,
                cfg.GlobalDisableOnTaskbar,
                cfg.GlobalDisableOnScreenEdges);
        }

        /// <summary>将页顶「全局进程过滤」写回 <see cref="MouseMappingConfig"/>（与规则列表独立持久化）。</summary>
        private void FlushGlobalProcessFilterFromUi()
        {
            var cfg = App.AppConfig.MouseMapping;
            cfg.GlobalRestrictToProcessList = GlobalProcessFilterSwitch.IsOn;
            cfg.GlobalContextMode = (MouseContextWhitelistMode)Math.Clamp(
                IndexOfLabel(GlobalContextModeLabels, GlobalContextModeDropdown.Text), 0, 1);
            cfg.GlobalProcessFilter = _globalProcessItems
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToList();
            cfg.GlobalDisableOnTaskbar = GlobalDisableOnTaskbarSwitch.IsOn;
            cfg.GlobalDisableOnScreenEdges = GlobalDisableOnScreenEdgesSwitch.IsOn;
        }

        private void UpdateGlobalProcessFilterDetailsVisibility()
        {
            if (GlobalProcessFilterDetailsPanel == null) return;
            GlobalProcessFilterDetailsPanel.Visibility = GlobalProcessFilterSwitch.IsOn
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void GlobalProcessFilterSwitch_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressGlobalProcessToggle) return;
            Log.Information("{Scope} 用户切换全局进程过滤 -> {On}", LogScope, GlobalProcessFilterSwitch.IsOn);
            UpdateGlobalProcessFilterDetailsVisibility();
            if (!_applyingGlobalProcessFromConfig && GlobalProcessFilterSwitch.IsOn)
            {
                _suppressAutosave = true;
                GlobalContextModeDropdown.Text = GlobalContextModeLabels[(int)MouseContextWhitelistMode.Exclude];
                _suppressAutosave = false;
            }
            if (!_suppressAutosave)
                ScheduleAutosave();
        }

        private void UpdateGlobalProcessDropdownDisplayText()
        {
            var selected = new List<string>();
            foreach (var cb in FindVisualChildren<CheckBox>(GlobalProcessItemsControl))
            {
                if (cb.IsChecked == true && cb.Tag is string path)
                    selected.Add(path);
            }
            if (selected.Count == 0)
                GlobalProcessDropdown.Text = string.Empty;
            else
                GlobalProcessDropdown.Text = $"已选 {selected.Count} 项";
        }

        private void GlobalProcessItemBorder_Tapped(object sender, TappedRoutedEventArgs e)
        {
            try
            {
                Log.Debug("{Scope} 用户切换全局过滤名单中某项勾选状态", LogScope);
                if (sender is not Border border) return;
                var cb = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (cb == null) return;
                cb.IsChecked = cb.IsChecked != true;
                ScheduleAutosave();
                UpdateGlobalProcessDropdownDisplayText();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Scope} 全局过滤名单项点击失败", LogScope);
            }
        }

        private void GlobalProcessItemBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
                b.Background = new SolidColorBrush(Color.FromArgb(255, 229, 229, 229));
        }

        private void GlobalProcessItemBorder_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
                b.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        private void GlobalProcessSelectAll_Click(object sender, RoutedEventArgs e)
        {
            Log.Information("{Scope} 全局过滤名单：全选", LogScope);
            foreach (var cb in FindVisualChildren<CheckBox>(GlobalProcessItemsControl))
                cb.IsChecked = true;
            UpdateGlobalProcessDropdownDisplayText();
            ScheduleAutosave();
        }

        private void GlobalProcessClearChecks_Click(object sender, RoutedEventArgs e)
        {
            Log.Information("{Scope} 全局过滤名单：清空勾选", LogScope);
            foreach (var cb in FindVisualChildren<CheckBox>(GlobalProcessItemsControl))
                cb.IsChecked = false;
            UpdateGlobalProcessDropdownDisplayText();
            ScheduleAutosave();
        }

        private async void GlobalProcessDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var remove = new List<string>();
            foreach (var cb in FindVisualChildren<CheckBox>(GlobalProcessItemsControl))
            {
                if (cb.IsChecked == true && cb.Tag is string path)
                    remove.Add(path);
            }
            if (remove.Count == 0)
            {
                _ = await new ContentDialog
                {
                    Title = "提示",
                    Content = "请先勾选要删除的过滤名单项",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot
                }.ShowAsync();
                return;
            }

            var dlg = new ContentDialog
            {
                Title = "确认删除",
                Content = $"从全局过滤名单中移除 {remove.Count} 项？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            Log.Information("{Scope} 全局过滤名单：已确认删除 {Count} 项", LogScope, remove.Count);
            foreach (var p in remove)
                _globalProcessItems.Remove(p);
            RefreshGlobalProcessList();
            ScheduleAutosave();
        }

        private async void GlobalBrowseExe_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var app = (App)App.Current;
                if (app?.window == null)
                {
                    Log.Warning("{Scope} 浏览 exe 时主窗口为空", LogScope);
                    return;
                }

                var hwnd = WindowNative.GetWindowHandle(app.window);
                string? path = null;
                try
                {
                    var picker = new FileOpenPicker();
                    picker.FileTypeFilter.Add(".exe");
                    InitializeWithWindow.Initialize(picker, hwnd);
                    var f = await picker.PickSingleFileAsync();
                    path = f?.Path;
                }
                catch (COMException ex)
                {
                    Log.Warning(ex, "{Scope} FileOpenPicker 失败，回退 Win32 对话框", LogScope);
                    path = Win32FileDialog.ShowOpenFileDialog(hwnd, "可执行文件|*.exe", "选择要加入全局列表的程序");
                }

                if (string.IsNullOrEmpty(path)) return;
                if (!_globalProcessItems.Contains(path))
                {
                    _globalProcessItems.Add(path);
                    Log.Information("{Scope} 全局过滤名单：通过浏览新增路径 {Path}", LogScope, path);
                }
                else
                    Log.Debug("{Scope} 全局过滤名单：浏览所选路径已存在，跳过", LogScope);
                RefreshGlobalProcessList();
                ScheduleAutosave();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Scope} 全局过滤浏览 exe 失败", LogScope);
            }
        }

        private void RefreshGlobalProcessList()
        {
            GlobalProcessItemsControl.ItemsSource = null;
            GlobalProcessItemsControl.ItemsSource = _globalProcessItems;
            DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
            {
                foreach (var cb in FindVisualChildren<CheckBox>(GlobalProcessItemsControl))
                {
                    if (cb.Tag is string)
                        cb.IsChecked = true;
                }
                UpdateGlobalProcessDropdownDisplayText();
            });
        }

        private void GlobalContextModeDropdownItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border { DataContext: string s })
            {
                Log.Information("{Scope} 用户选择全局过滤模式：{Mode}", LogScope, s);
                GlobalContextModeDropdown.Text = s;
                GlobalContextModeDropdown.IsOpen = false;
                ScheduleAutosave();
            }
        }

        private void GlobalDropdownItemBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
                b.Background = new SolidColorBrush(Color.FromArgb(255, 229, 229, 229));
        }

        private void GlobalDropdownItemBorder_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
                b.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        /// <summary>与 <see cref="MouseMappingRuleRow"/> 相同：整行 / 括号标签 / 子串匹配，避免误匹配。</summary>
        private static int IndexOfLabel(string[] labels, string? text, int fallback = 0)
        {
            if (string.IsNullOrEmpty(text)) return fallback;
            int i = Array.IndexOf(labels, text);
            if (i >= 0) return i;
            string t = text.Trim();

            for (int k = 0; k < labels.Length; k++)
            {
                int p = labels[k].IndexOf('(');
                int end = labels[k].LastIndexOf(')');
                if (p < 0 || end <= p) continue;
                string tag = labels[k].Substring(p + 1, end - p - 1).Trim();
                if (string.Equals(t, tag, StringComparison.OrdinalIgnoreCase))
                    return k;
            }

            for (int k = 0; k < labels.Length; k++)
            {
                if (labels[k].IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0)
                    return k;
            }

            return fallback;
        }

        private void AddRuleButton_Click(object sender, RoutedEventArgs e)
        {
            Log.Information("{Scope} 用户点击添加规则", LogScope);
            var r = new MouseMappingRule
            {
                Name = "新规则",
                Enabled = true,
                Priority = 0,
                Button = MousePhysicalButton.Left,
                Trigger = MouseTriggerKind.Click,
                Action = MouseActionKind.MouseButton,
                HoldThresholdMs = 200,
                RepeatIntervalMs = 100
            };
            _rules.Add(r);
            try
            {
                PersistMouseMappingToDiskAndApply();
                Log.Information("{Scope} 已添加规则 Id={RuleId}，当前规则数={Count}", LogScope, r.Id, _rules.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Scope} 添加规则后应用失败", LogScope);
            }
        }

        private void DeleteRuleButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button b || b.Tag is not string id) return;
            var rule = _rules.FirstOrDefault(x => x.Id == id);
            if (rule != null)
            {
                Log.Information("{Scope} 用户删除规则 Id={RuleId}，名称={Name}", LogScope, id, rule.Name);
                _rules.Remove(rule);
                try
                {
                    PersistMouseMappingToDiskAndApply();
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "{Scope} 删除规则后应用失败", LogScope);
                }
            }
        }

        /// <summary>
        /// 主导航「导出设置」前调用：将当前页 UI 写入内存并落盘，保证备份含最新鼠标映射。
        /// 必须在 UI 线程调用。
        /// </summary>
        public void FlushPendingMouseMappingToConfig()
        {
            try
            {
                Log.Information("{Scope} 主导航触发导出/同步：正在将当前页写入配置并落盘", LogScope);
                PersistMouseMappingToDiskAndApply();
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "{Scope} 导出前同步失败", LogScope);
            }
        }

        private static List<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            var children = new List<T>();
            if (parent == null) return children;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    children.Add(typedChild);

                children.AddRange(FindVisualChildren<T>(child));
            }

            return children;
        }
    }
}
