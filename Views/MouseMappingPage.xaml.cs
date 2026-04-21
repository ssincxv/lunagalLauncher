using System.Collections.ObjectModel;
using lunagalLauncher.Data;
using lunagalLauncher.Infrastructure;
using lunagalLauncher.Services;
using lunagalLauncher.Strings;
using lunagalLauncher.Utils;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.UI;
using static lunagalLauncher.Utils.VisualTreeExtensions;
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

        /// <summary>
        /// 首次 <see cref="MouseMappingPage_Loaded"/> 是否已经完成"从 config 构建 UI 数据模型"这段重活。
        ///
        /// <para>
        /// 背景：缓存页面导航机制下，页面切走再切回时 <c>Unloaded</c> / <c>Loaded</c> 仍会触发，
        /// 但 XAML 控件、已绑定的 <c>ItemsSource</c>、规则 <see cref="ObservableCollection{MouseMappingRule}"/>
        /// 都还活着。若每次 Loaded 都重建 <c>_rules = new ObservableCollection(cfg.Rules)</c> + 重设
        /// <c>RulesListView.ItemsSource</c>，会触发 ListView 把所有 <c>MouseMappingRuleRow</c>（里面一堆
        /// <c>CustomDropdown</c> ApplyTemplate + Composition 初始化）全部重新生成——这就是"切到鼠标映射
        /// 感到明显卡顿"的根因。
        /// </para>
        /// <para>
        /// 本字段 = false 的首次 Loaded 走完整初始化（旧行为）；= true 的后续切回只做事件订阅一类
        /// 幂等工作。配置由外部变更的路径（如「导入设置」）调 <see cref="ReapplyMouseMappingConfigToUi"/>
        /// 显式重新同步到 UI。
        /// </para>
        /// </summary>
        private bool _hasInitializedBindings;

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
                // --- 每次切入都要做的幂等工作 ---

                this.AddHandler(UIElement.PointerPressedEvent, _pagePointerPressedHandler, true);

                try
                {
                    if (App.TryGetMainWindowHandle(out var hwnd))
                    {
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

                // 事件订阅幂等化：先 -= 再 += 避免重复挂接（缓存页多次 Loaded 场景下必要）
                MouseMappingRuleRow.AnyRuleEditedFromUi -= OnAnyRuleEditedFromUi;
                MouseMappingRuleRow.AnyRuleEditedFromUi += OnAnyRuleEditedFromUi;

                // --- 首次（或外部配置被导入后）需要做的重活：从 config 构建 UI 数据模型 ---
                if (!_hasInitializedBindings)
                {
                    ApplyMouseMappingConfigToUiCore();
                    _hasInitializedBindings = true;
                }
                else
                {
                    Log.Debug("{Scope} 页面再次切入，跳过数据重绑定（规则 {RuleCount} 条保持不变）",
                        LogScope, _rules.Count);
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Scope} 加载失败", LogScope);
            }
        }

        /// <summary>
        /// 外部路径（如"导入设置"）覆盖了 <see cref="App.AppConfig"/>.MouseMapping 后，
        /// 调此方法让本页 UI 重新从配置读取一次、同步到视图。
        /// </summary>
        /// <remarks>
        /// 不依赖 <see cref="_hasInitializedBindings"/> —— 无论首次还是后续都会执行。
        /// 若页面尚未 Loaded（XAML 控件字段可能为 null），静默跳过，等页面实际 Loaded 时再由
        /// <see cref="MouseMappingPage_Loaded"/> 的初次分支完成初始化。
        /// </remarks>
        public void ReapplyMouseMappingConfigToUi()
        {
            try
            {
                if (GlobalEnabledSwitch == null) return; // 页面尚未加载完毕
                ApplyMouseMappingConfigToUiCore();
                _hasInitializedBindings = true;
                Log.Information("{Scope} 已从最新配置重新同步到 UI（外部导入触发）", LogScope);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "{Scope} ReapplyMouseMappingConfigToUi 失败", LogScope);
            }
        }

        /// <summary>
        /// 「从 <see cref="App.AppConfig"/>.MouseMapping 构建/重建 UI 数据模型」的核心，
        /// 由 <see cref="MouseMappingPage_Loaded"/> 首次 + <see cref="ReapplyMouseMappingConfigToUi"/> 复用。
        /// </summary>
        private void ApplyMouseMappingConfigToUiCore()
        {
            var cfg = App.AppConfig.MouseMapping;

            _rules = new ObservableCollection<MouseMappingRule>(cfg.Rules ?? new List<MouseMappingRule>());
            RulesListView.ItemsSource = _rules;

            GlobalContextModeDropdownItems.ItemsSource = GlobalContextModeLabels;
            GlobalProcessItemsControl.ItemsSource = _globalProcessItems;

            // 这两个 TextChanged 订阅只挂一次即可；用 `-=` 前缀安全起见（同一 lambda 对象每次是新的，
            // 所以实际上只能挂一次——首次调用时）。为避免字段存储委托再做 dedupe，仅靠
            // `_hasInitializedBindings` 守门：外部 Reapply 不会重复进来；这里不额外处理。
            if (!_hasInitializedBindings)
            {
                GlobalContextModeDropdown.TextChanged += (_, _) => ScheduleAutosave();
                GlobalProcessDropdown.TextChanged += (_, _) => ScheduleAutosave();
            }

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

            Log.Information(
                "{Scope} 配置已同步到 UI：总开关={GlobalOn}，全局进程过滤={GlobalProc}，任务栏全局禁用={T}，边缘全局禁用={E}，规则数={RuleCount}，全局名单项={ProcItems}",
                LogScope,
                cfg.GlobalEnabled,
                cfg.GlobalRestrictToProcessList,
                cfg.GlobalDisableOnTaskbar,
                cfg.GlobalDisableOnScreenEdges,
                _rules.Count,
                _globalProcessItems.Count);
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

        /// <summary>
        /// 遍历所有规则行，把 UI 尚未落到数据模型的编辑值刷到 <see cref="MouseMappingRule"/>。
        ///
        /// <para>
        /// 方案 C Phase 2b：<c>RulesListView</c> 从 <see cref="ItemsControl"/> 换成 <see cref="ItemsRepeater"/>
        /// 后，只实例化视口内的 3-5 行，屏外行根本没有 <see cref="MouseMappingRuleRow"/> UI 实例。
        /// 老实现用 <see cref="FindVisualChildren{T}"/> 遍历可视树，虚拟化后只能拿到可见行——屏外规则
        /// 的编辑不会丢失（TextBox/Dropdown 失焦时会直接写回 Rule 对象），但保存流程仍希望对所有已实现
        /// 的行做一次 Flush，确保最后一次未失焦的编辑也被收走。
        /// </para>
        /// <para>
        /// 新实现：按数据源下标遍历，对每个下标调 <see cref="ItemsRepeater.TryGetElement"/>。
        /// 返回 null 即该行尚未实例化（屏外且从未滚入），此行没有 UI 端需要 Flush 的待写入。
        /// </para>
        /// </summary>
        private void FlushAllRuleRowsFromUi()
        {
            if (RulesListView == null || _rules == null) return;

            for (int i = 0; i < _rules.Count; i++)
            {
                if (RulesListView.TryGetElement(i) is FrameworkElement fe)
                {
                    // DataTemplate 根是 StackPanel，里面第一个子元素才是 MouseMappingRuleRow。
                    // 直接向下找一层即可命中（DataTemplate 结构见 MouseMappingPage.xaml）。
                    var row = fe is MouseMappingRuleRow direct
                        ? direct
                        : FindVisualChildren<MouseMappingRuleRow>(fe).FirstOrDefault();
                    row?.FlushFromUi();
                }
            }
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
            ScrollGlobalProcessFilterListToTop();
        }

        private void GlobalProcessClearChecks_Click(object sender, RoutedEventArgs e)
        {
            Log.Information("{Scope} 全局过滤名单：清空勾选", LogScope);
            foreach (var cb in FindVisualChildren<CheckBox>(GlobalProcessItemsControl))
                cb.IsChecked = false;
            UpdateGlobalProcessDropdownDisplayText();
            ScheduleAutosave();
            ScrollGlobalProcessFilterListToTop();
        }

        /// <summary>全选/清空后滚回列表顶部，避免 ScrollViewer 偏移或布局更新后仍停在错误位置。</summary>
        private void ScrollGlobalProcessFilterListToTop()
        {
            try
            {
                GlobalProcessListScrollViewer?.ChangeView(null, 0, null);
                DispatcherQueue.GetForCurrentThread()?.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    GlobalProcessListScrollViewer?.ChangeView(null, 0, null));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "{Scope} 全局过滤名单滚动回顶失败（忽略）", LogScope);
            }
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
                await UiDialogs.ShowAlertAsync(XamlRoot, DialogMessages.PromptTitle, DialogMessages.SelectFilterItemsToDelete);
                return;
            }

            Log.Information("{Scope} 全局过滤名单：立即删除 {Count} 项（无确认框）", LogScope, remove.Count);
            foreach (var p in remove)
                _globalProcessItems.Remove(p);
            RefreshGlobalProcessList();
            ScheduleAutosave();
            GlobalProcessDropdown.IsOpen = false;
        }

        private async void GlobalBrowseExe_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await CustomDropdownModalPrep.CloseIfOpenAndWaitForAnimationAsync(GlobalProcessDropdown);

                if (!App.TryGetMainWindowHandle(out var hwnd))
                {
                    Log.Warning("{Scope} 浏览 exe 时主窗口为空", LogScope);
                    return;
                }

                var initDir = Win32FileDialog.TryGetInitialDirectoryFromExistingPaths(_globalProcessItems);
                string? path = await Win32FileDialog.ShowOpenFileDialogAsync(hwnd, "可执行文件|*.exe", "选择要加入全局列表的程序", initDir);

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
                ScrollGlobalProcessFilterListToTop();
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

        // FindVisualChildren<T> 已集中到 Utils.VisualTreeExtensions（通过文件顶部 using static 裸调）。
    }
}
