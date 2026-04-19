using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.InteropServices;
using lunagalLauncher.Controls;
using lunagalLauncher.Data;
using lunagalLauncher.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Serilog;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI;
using WinRT.Interop;

namespace lunagalLauncher.Views
{
    public sealed partial class MouseMappingRuleRow : UserControl
    {
        /// <summary>任意规则行在 UI 同步到模型后触发（用于父页面防抖自动保存）。</summary>
        public static event EventHandler? AnyRuleEditedFromUi;

        /// <summary>父页面点击空白等：先通知所有行结束物理键录入态，再移焦点。</summary>
        private static event Action? EndPhysicalKeyCaptureRequested;

        internal static void RequestEndPhysicalKeyCaptureAll() => EndPhysicalKeyCaptureRequested?.Invoke();

        /// <summary>父页面点击组合键区域外：立即结束「录制组合键」态，避免 LostFocus 未到时仍吃掉键盘。</summary>
        private static event Action? EndKeyComboRecordingRequested;

        internal static void RequestEndKeyComboRecordingAll() => EndKeyComboRecordingRequested?.Invoke();

        public static readonly DependencyProperty RuleProperty = DependencyProperty.Register(
            nameof(Rule),
            typeof(MouseMappingRule),
            typeof(MouseMappingRuleRow),
            new PropertyMetadata(null, OnRulePropertyChanged));

        private bool _suppressDirty;
        /// <summary>ApplyRuleToUi 程序化写开关时为 true，避免误触发「用户刚打开进程过滤」的默认黑名单逻辑。</summary>
        private bool _applyingRuleToUi;
        private bool _isRecordingKeyCombo;
        /// <summary>物理键：先点击聚焦，延迟武装后再接受鼠标/键盘录入（避免首击左键被当成绑定键）。</summary>
        private bool _isRecordingPhysicalKey;

        /// <summary>用于作废 GotFocus 中尚未执行的 TryEnqueue 武装回调（失焦时递增）。</summary>
        private uint _physicalKeyArmGeneration;

        /// <summary>鼠标 Pointer 录入后的短时间内忽略 PreviewKeyDown，避免连点左键被 TextBox/路由误当成字母（如 C）。</summary>
        private long _suppressKeyboardPhysicalCaptureUntilMs;
        private readonly ObservableCollection<string> _processItems = new();
        private bool _combosInitialized;

        private const int MsHoldMin = 0;
        private const int MsHoldMax = 600000;
        private const int MsHoldStep = 10;
        private const int MsRepeatMin = 1;
        private const int MsRepeatMax = 600000;
        private const int MsRepeatStep = 10;

        private const int PriorityMin = -9999;
        private const int PriorityMax = 99999;
        private const int PriorityStep = 1;

        /// <summary>SimMouseDropdown 选项与 SimulatedMouseButton：0=左,1=右,2=中,3=侧键1,4=侧键2</summary>
        private static readonly int[] SimMouseValues = { 0, 1, 2, 3, 4 };

        private static readonly string[] ButtonLabels = { "左键", "右键", "中键", "侧键1", "侧键2" };
        private static readonly string[] TriggerLabels =
        {
            "单击 (Click)", "长按 (Hold)", "双击 (DoubleClick)", "连击 (MultiClick)"
        };
        private static readonly string[] BehaviorLabels = { "触发一次 (FireOnce)", "按住持续连发 (RepeatWhileHeld)" };
        private static readonly string[] ActionLabels = { "模拟鼠标键", "组合键" };
        private static readonly string[] SimMouseLabels =
        {
            "鼠标左键", "鼠标右键", "鼠标中键", "鼠标侧键", "鼠标侧键2"
        };
        /// <summary>与「过滤模式」下拉一致：白/黑仍表示仅名单内生效或排除名单内进程（持久化枚举未改名）。</summary>
        private static readonly string[] ContextModeLabels = { "仅过滤名单内生效 (白名单)", "过滤名单内不生效 (黑名单)" };

        public MouseMappingRule? Rule
        {
            get => (MouseMappingRule?)GetValue(RuleProperty);
            set => SetValue(RuleProperty, value);
        }

        public MouseMappingRuleRow()
        {
            InitializeComponent();
            InitializeCombos();
            Loaded += MouseMappingRuleRow_Loaded;
            Unloaded += MouseMappingRuleRow_Unloaded;
        }

        private void MouseMappingRuleRow_Unloaded(object sender, RoutedEventArgs e)
        {
            EndPhysicalKeyCaptureRequested -= OnEndPhysicalKeyCaptureRequested;
            EndKeyComboRecordingRequested -= OnEndKeyComboRecordingRequested;
            RawInputMouseBridge.RawMousePhysicalDownRecorded -= OnRawMousePhysicalDownRecorded;
        }

        private void OnEndPhysicalKeyCaptureRequested()
        {
            _physicalKeyArmGeneration++;
            _isRecordingPhysicalKey = false;
        }

        private void OnEndKeyComboRecordingRequested()
        {
            if (!_isRecordingKeyCombo) return;
            _isRecordingKeyCombo = false;
            KeyComboBox.PlaceholderText = "点击后按下快捷键";
        }

        private void OnRawMousePhysicalDownRecorded(ushort downFlag, uint mask)
        {
            if (!_isRecordingPhysicalKey) return;
            var r = Rule;
            if (r == null) return;

            // 左/右/中/侧键1/2 已由 Pointer 路径录入；标准 RI 按下沿忽略（避免点空白结束录入时被 Raw 误记）
            if (downFlag != 0 && RawInputMouseBridge.IsStandardPointerRiDownFlag(downFlag))
                return;

            r.PhysicalKeyVirtualKey = 0;
            r.PhysicalMouseHidSignature = 0;
            if (downFlag != 0)
            {
                r.PhysicalMouseRawButtonFlag = downFlag;
                r.PhysicalMouseRawButtonsMask = 0;
            }
            else if (mask != 0)
            {
                r.PhysicalMouseRawButtonFlag = 0;
                r.PhysicalMouseRawButtonsMask = mask;
            }
            else
                return;

            r.Button = MousePhysicalButton.Left;
            _suppressKeyboardPhysicalCaptureUntilMs = Environment.TickCount64 + 220;
            _suppressDirty = true;
            PhysicalKeyBox.Text = FormatPhysicalKeyDisplay(r);
            _suppressDirty = false;
            SyncFromUi();
            Log.Information("鼠标映射 UI：物理键已设为 Raw down=0x{F:X4} mask=0x{M:X8}", downFlag, mask);
        }

        /// <summary>
        /// 与 LlamaServicePage 一致：用 AddHandler(..., handledEventsToo: true) 订阅箭头，
        /// 否则 FontIcon 会吞掉 PointerPressed，按钮位移动画不触发。
        /// </summary>
        private void MouseMappingRuleRow_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                EndPhysicalKeyCaptureRequested -= OnEndPhysicalKeyCaptureRequested;
                EndPhysicalKeyCaptureRequested += OnEndPhysicalKeyCaptureRequested;
                EndKeyComboRecordingRequested -= OnEndKeyComboRecordingRequested;
                EndKeyComboRecordingRequested += OnEndKeyComboRecordingRequested;
                RawInputMouseBridge.RawMousePhysicalDownRecorded -= OnRawMousePhysicalDownRecorded;
                RawInputMouseBridge.RawMousePhysicalDownRecorded += OnRawMousePhysicalDownRecorded;

                HoldThresholdUpButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(HoldThresholdUpButton_PointerPressed_Manual), true);
                HoldThresholdUpButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(HoldThresholdUpButton_PointerReleased_Manual), true);
                HoldThresholdDownButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(HoldThresholdDownButton_PointerPressed_Manual), true);
                HoldThresholdDownButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(HoldThresholdDownButton_PointerReleased_Manual), true);
                RepeatIntervalUpButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(RepeatIntervalUpButton_PointerPressed_Manual), true);
                RepeatIntervalUpButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(RepeatIntervalUpButton_PointerReleased_Manual), true);
                RepeatIntervalDownButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(RepeatIntervalDownButton_PointerPressed_Manual), true);
                RepeatIntervalDownButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(RepeatIntervalDownButton_PointerReleased_Manual), true);
                PriorityUpButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PriorityUpButton_PointerPressed_Manual), true);
                PriorityUpButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PriorityUpButton_PointerReleased_Manual), true);
                PriorityDownButton.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PriorityDownButton_PointerPressed_Manual), true);
                PriorityDownButton.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(PriorityDownButton_PointerReleased_Manual), true);

                // 只读 TextBox 会吞掉 PointerPressed，Border 收不到；必须 handledEventsToo 才能录鼠标键。
                PhysicalKeyBox.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(PhysicalKeyBox_PointerPressed), true);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "鼠标映射：毫秒输入框箭头 Pointer 订阅失败");
            }
        }

        private void AnimateSpinnerButton(ButtonBase button, bool isPressed, bool isUp)
        {
            if (button.RenderTransform is not CompositeTransform transform)
                return;
            try
            {
                var storyboard = new Storyboard();
                if (isPressed)
                {
                    var animation = new DoubleAnimation
                    {
                        Duration = new Duration(TimeSpan.FromMilliseconds(100)),
                        To = isUp ? -1.5 : 1.5,
                        EasingFunction = new QuadraticEase()
                    };
                    Storyboard.SetTarget(animation, transform);
                    Storyboard.SetTargetProperty(animation, "TranslateY");
                    storyboard.Children.Add(animation);
                    storyboard.Begin();
                }
                else
                {
                    var keyFrameAnimation = new DoubleAnimationUsingKeyFrames
                    {
                        Duration = new Duration(TimeSpan.FromMilliseconds(200))
                    };
                    keyFrameAnimation.KeyFrames.Add(new EasingDoubleKeyFrame
                    {
                        KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
                        Value = isUp ? 0.8 : -0.8
                    });
                    keyFrameAnimation.KeyFrames.Add(new EasingDoubleKeyFrame
                    {
                        KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)),
                        Value = 0
                    });
                    Storyboard.SetTarget(keyFrameAnimation, transform);
                    Storyboard.SetTargetProperty(keyFrameAnimation, "TranslateY");
                    storyboard.Children.Add(keyFrameAnimation);
                    storyboard.Begin();
                }
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "箭头按钮动画失败");
            }
        }

        private void HoldThresholdUpButton_PointerPressed_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, true, true);
        }

        private void HoldThresholdUpButton_PointerReleased_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, false, true);
        }

        private void HoldThresholdDownButton_PointerPressed_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, true, false);
        }

        private void HoldThresholdDownButton_PointerReleased_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, false, false);
        }

        private void RepeatIntervalUpButton_PointerPressed_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, true, true);
        }

        private void RepeatIntervalUpButton_PointerReleased_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, false, true);
        }

        private void RepeatIntervalDownButton_PointerPressed_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, true, false);
        }

        private void RepeatIntervalDownButton_PointerReleased_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, false, false);
        }

        private void PriorityUpButton_PointerPressed_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, true, true);
        }

        private void PriorityUpButton_PointerReleased_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, false, true);
        }

        private void PriorityDownButton_PointerPressed_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, true, false);
        }

        private void PriorityDownButton_PointerReleased_Manual(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ButtonBase b) AnimateSpinnerButton(b, false, false);
        }

        private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressDirty) return;
            SyncFromUi();
        }

        private void PriorityTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressDirty) return;
            if (sender is not TextBox tb) return;
            if (string.IsNullOrWhiteSpace(tb.Text))
            {
                SyncFromUi();
                return;
            }
            if (!int.TryParse(tb.Text, out int v)) return;
            int c = ClampPriority(v);
            if (c != v)
            {
                _suppressDirty = true;
                tb.Text = c.ToString();
                _suppressDirty = false;
            }
            SyncFromUi();
        }

        private void PriorityUpButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PriorityTextBox.Text, out int cur)) cur = 0;
            int n = ClampPriority(cur + PriorityStep);
            _suppressDirty = true;
            PriorityTextBox.Text = n.ToString();
            _suppressDirty = false;
            SyncFromUi();
        }

        private void PriorityDownButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(PriorityTextBox.Text, out int cur)) cur = 0;
            int n = ClampPriority(cur - PriorityStep);
            _suppressDirty = true;
            PriorityTextBox.Text = n.ToString();
            _suppressDirty = false;
            SyncFromUi();
        }

        private static int ClampPriority(int v) => Math.Clamp(v, PriorityMin, PriorityMax);

        private static int ParsePriorityInt(string? text, int defaultVal)
        {
            if (!int.TryParse(text?.Trim(), out int v)) return ClampPriority(defaultVal);
            return ClampPriority(v);
        }

        private void HoldThresholdTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressDirty) return;
            if (sender is not TextBox tb) return;
            if (string.IsNullOrWhiteSpace(tb.Text)) return;
            if (!int.TryParse(tb.Text, out int v)) return;
            int c = ClampHold(v);
            if (c != v)
            {
                _suppressDirty = true;
                tb.Text = c.ToString();
                _suppressDirty = false;
            }
            SyncFromUi();
        }

        private void RepeatIntervalTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_suppressDirty) return;
            if (sender is not TextBox tb) return;
            if (string.IsNullOrWhiteSpace(tb.Text)) return;
            if (!int.TryParse(tb.Text, out int v)) return;
            int c = ClampRepeat(v);
            if (c != v)
            {
                _suppressDirty = true;
                tb.Text = c.ToString();
                _suppressDirty = false;
            }
            SyncFromUi();
        }

        private static int ClampHold(int v) => Math.Clamp(v, MsHoldMin, MsHoldMax);
        private static int ClampRepeat(int v) => Math.Clamp(v, MsRepeatMin, MsRepeatMax);

        private static int ParseMsInt(string? text, int defaultVal, Func<int, int> clamp)
        {
            if (!int.TryParse(text?.Trim(), out int v)) return clamp(defaultVal);
            return clamp(v);
        }

        private void HoldThresholdUpButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(HoldThresholdTextBox.Text, out int cur)) cur = 200;
            int n = ClampHold(cur + MsHoldStep);
            _suppressDirty = true;
            HoldThresholdTextBox.Text = n.ToString();
            _suppressDirty = false;
            SyncFromUi();
        }

        private void HoldThresholdDownButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(HoldThresholdTextBox.Text, out int cur)) cur = 200;
            int n = ClampHold(cur - MsHoldStep);
            _suppressDirty = true;
            HoldThresholdTextBox.Text = n.ToString();
            _suppressDirty = false;
            SyncFromUi();
        }

        private void RepeatIntervalUpButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(RepeatIntervalTextBox.Text, out int cur)) cur = 100;
            int n = ClampRepeat(cur + MsRepeatStep);
            _suppressDirty = true;
            RepeatIntervalTextBox.Text = n.ToString();
            _suppressDirty = false;
            SyncFromUi();
        }

        private void RepeatIntervalDownButton_Click(object sender, RoutedEventArgs e)
        {
            if (!int.TryParse(RepeatIntervalTextBox.Text, out int cur)) cur = 100;
            int n = ClampRepeat(cur - MsRepeatStep);
            _suppressDirty = true;
            RepeatIntervalTextBox.Text = n.ToString();
            _suppressDirty = false;
            SyncFromUi();
        }

        private static void OnRulePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is MouseMappingRuleRow row)
                row.ApplyRuleToUi();
        }

        private void InitializeCombos()
        {
            if (_combosInitialized) return;
            _combosInitialized = true;

            TriggerDropdownItems.ItemsSource = TriggerLabels;
            BehaviorDropdownItems.ItemsSource = BehaviorLabels;
            ActionDropdownItems.ItemsSource = ActionLabels;
            SimMouseDropdownItems.ItemsSource = SimMouseLabels;
            ContextModeDropdownItems.ItemsSource = ContextModeLabels;

            void hook(CustomDropdown d)
            {
                d.TextChanged += (_, _) => SyncFromUi();
            }

            hook(TriggerDropdown);
            TriggerDropdown.TextChanged += (_, _) =>
            {
                RefreshBehaviorOptionsForTrigger();
                UpdateMsTimingFieldsLayout();
            };
            hook(BehaviorDropdown);
            BehaviorDropdown.TextChanged += (_, _) => UpdateMsTimingFieldsLayout();
            hook(ActionDropdown);
            hook(SimMouseDropdown);
            hook(ContextModeDropdown);
            ActionDropdown.TextChanged += (_, _) => UpdateActionVisibility();

            KeyComboBox.TextChanged += (_, _) => SyncFromUi();
            EnabledSwitch.Toggled += (_, _) => SyncFromUi();
            // 用户打开「进程过滤」时默认黑名单：双击/连击不支持按住连发，行为列表在 RefreshBehaviorOptionsForTrigger 中收缩
            RestrictToggle.Toggled += (_, _) =>
            {
                UpdateProcessFilterDetailsVisibility();
                if (!_applyingRuleToUi && RestrictToggle.IsOn)
                {
                    _suppressDirty = true;
                    ContextModeDropdown.Text = ContextModeLabels[(int)MouseContextWhitelistMode.Exclude];
                    _suppressDirty = false;
                }
                SyncFromUi();
            };
            ProcessItemsControl.ItemsSource = _processItems;

            RefreshBehaviorOptionsForTrigger();
        }

        /// <summary>先精确匹配整行；再与括号内英文标签做全字匹配（避免 DoubleClick 含 Click 误判为单击）；最后整行子串匹配。</summary>
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

        /// <summary>
        /// 仅当进程过滤开启时展示「过滤模式 / 过滤名单」，与引擎一致：未启用时全局规则不读名单。
        /// </summary>
        private void UpdateProcessFilterDetailsVisibility()
        {
            if (ProcessFilterDetailsPanel == null) return;
            ProcessFilterDetailsPanel.Visibility = RestrictToggle.IsOn
                ? Visibility.Visible
                : Visibility.Collapsed;
            Log.Debug("进程过滤详情区 {State}", RestrictToggle.IsOn ? "显示" : "隐藏");
        }

        /// <summary>根据动作类型切换：模拟鼠标键下拉 / 组合键纯输入栏</summary>
        private void UpdateActionVisibility()
        {
            bool isKeyCombo = ActionDropdown.Text == ActionLabels[1];
            SimMouseDropdown.Visibility = isKeyCombo ? Visibility.Collapsed : Visibility.Visible;
            KeyComboBorder.Visibility = isKeyCombo ? Visibility.Visible : Visibility.Collapsed;
            ActionSecondaryLabel.Text = isKeyCombo ? "组合键" : "模拟鼠标键";
        }

        /// <summary>
        /// 阈值：单击/长按用；双击/连击引擎不读 HoldThreshold，故隐藏。连发间隔：仅 RepeatWhileHeld。二者皆无则整行折叠。
        /// </summary>
        private void UpdateMsTimingFieldsLayout()
        {
            if (MsTimingFieldsGrid == null || HoldThresholdFieldPanel == null || RepeatIntervalFieldPanel == null) return;

            int tk = IndexOfLabel(TriggerLabels, TriggerDropdown.Text, 0);
            bool showHold = tk != (int)MouseTriggerKind.DoubleClick && tk != (int)MouseTriggerKind.MultiClick;
            bool showRepeat = IndexOfLabel(BehaviorLabels, BehaviorDropdown.Text, 0) == (int)MouseBehaviorMode.RepeatWhileHeld;

            if (!showHold && !showRepeat)
            {
                MsTimingFieldsGrid.Visibility = Visibility.Collapsed;
                return;
            }

            MsTimingFieldsGrid.Visibility = Visibility.Visible;
            HoldThresholdFieldPanel.Visibility = showHold ? Visibility.Visible : Visibility.Collapsed;
            RepeatIntervalFieldPanel.Visibility = showRepeat ? Visibility.Visible : Visibility.Collapsed;

            if (showHold && showRepeat)
            {
                HoldThresholdTimeColumn!.Width = new GridLength(1, GridUnitType.Star);
                RepeatIntervalTimeColumn!.Width = new GridLength(1, GridUnitType.Star);
            }
            else if (showHold)
            {
                HoldThresholdTimeColumn!.Width = new GridLength(1, GridUnitType.Star);
                RepeatIntervalTimeColumn!.Width = new GridLength(0);
            }
            else
            {
                HoldThresholdTimeColumn!.Width = new GridLength(0);
                RepeatIntervalTimeColumn!.Width = new GridLength(1, GridUnitType.Star);
            }
        }

        /// <summary>
        /// 双击/连击不参与按住连发轮询：下拉里移除 RepeatWhileHeld，并强制展示「触发一次」以免选到无效组合。
        /// </summary>
        private void RefreshBehaviorOptionsForTrigger()
        {
            if (BehaviorDropdownItems == null || TriggerDropdown == null) return;

            int tk = IndexOfLabel(TriggerLabels, TriggerDropdown.Text, 0);
            bool noRepeat = tk == (int)MouseTriggerKind.DoubleClick || tk == (int)MouseTriggerKind.MultiClick;
            string[] opts = noRepeat ? new[] { BehaviorLabels[0] } : BehaviorLabels;

            BehaviorDropdownItems.ItemsSource = opts;

            if (noRepeat || Array.IndexOf(opts, BehaviorDropdown.Text) < 0)
            {
                _suppressDirty = true;
                BehaviorDropdown.Text = BehaviorLabels[0];
                _suppressDirty = false;
            }
        }

        private void ApplyRuleToUi()
        {
            var r = Rule;
            if (r == null) return;

            InitializeCombos();

            _suppressDirty = true;
            try
            {
                NameBox.Text = r.Name ?? string.Empty;
                PriorityTextBox.Text = r.Priority.ToString();
                EnabledSwitch.IsOn = r.Enabled;

                r.Button = (MousePhysicalButton)Math.Clamp((int)r.Button, 0, (int)MousePhysicalButton.XButton2);
                PhysicalKeyBox.Text = FormatPhysicalKeyDisplay(r);
                TriggerDropdown.Text = TriggerLabels[Math.Clamp((int)r.Trigger, 0, TriggerLabels.Length - 1)];
                // 双击/连击与 RepeatWhileHeld 在引擎侧不兼容；落盘数据一并修正，避免 UI 与模型长期不一致
                int tkApply = Math.Clamp((int)r.Trigger, 0, 3);
                if (tkApply == (int)MouseTriggerKind.DoubleClick || tkApply == (int)MouseTriggerKind.MultiClick)
                    r.Behavior = MouseBehaviorMode.FireOnce;
                RefreshBehaviorOptionsForTrigger();
                BehaviorDropdown.Text = BehaviorLabels[Math.Clamp((int)r.Behavior, 0, BehaviorLabels.Length - 1)];
                ActionDropdown.Text = ActionLabels[Math.Clamp((int)r.Action, 0, ActionLabels.Length - 1)];

                HoldThresholdTextBox.Text = r.HoldThresholdMs.ToString();
                RepeatIntervalTextBox.Text = r.RepeatIntervalMs.ToString();
                UpdateHoldThresholdCaption();

                int simIdx = Array.IndexOf(SimMouseValues, r.SimulatedMouseButton);
                SimMouseDropdown.Text = simIdx >= 0 ? SimMouseLabels[simIdx] : SimMouseLabels[0];

                // 仅「组合键」动作显示组合键文本；模拟鼠标键时清空，避免隐藏框内残留 Ctrl 等被再次保存为错误 Action
                KeyComboBox.Text = r.Action == MouseActionKind.KeyCombo ? (r.KeyComboText ?? string.Empty) : string.Empty;

                ContextModeDropdown.Text = ContextModeLabels[Math.Clamp((int)r.ContextMode, 0, ContextModeLabels.Length - 1)];

                UpdateActionVisibility();
                UpdateMsTimingFieldsLayout();

                _applyingRuleToUi = true;
                try
                {
                    RestrictToggle.IsOn = r.RestrictToProcessList;
                }
                finally
                {
                    _applyingRuleToUi = false;
                }
                UpdateProcessFilterDetailsVisibility();

                _processItems.Clear();
                if (r.ProcessFilter != null)
                {
                    foreach (var p in r.ProcessFilter)
                    {
                        if (!string.IsNullOrWhiteSpace(p))
                            _processItems.Add(p.Trim());
                    }
                }
                ProcessItemsControl.ItemsSource = null;
                ProcessItemsControl.ItemsSource = _processItems;
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
                {
                    foreach (var cb in FindVisualChildren<CheckBox>(ProcessItemsControl))
                    {
                        if (cb.Tag is string)
                            cb.IsChecked = true;
                    }
                    UpdateProcessDropdownDisplayText();
                });
            }
            finally
            {
                _suppressDirty = false;
            }
        }

        private void SyncFromUi(bool raiseEditedEvent = true)
        {
            if (_suppressDirty) return;
            var r = Rule;
            if (r == null) return;

            r.Name = NameBox.Text?.Trim() ?? string.Empty;
            r.Priority = ParsePriorityInt(PriorityTextBox.Text, 0);
            r.Enabled = EnabledSwitch.IsOn;
            // 物理键（Button / PhysicalKeyVirtualKey）仅由录入事件写入，不由下拉同步
            r.Trigger = (MouseTriggerKind)Math.Clamp(IndexOfLabel(TriggerLabels, TriggerDropdown.Text), 0, 3);
            r.Behavior = (MouseBehaviorMode)Math.Clamp(IndexOfLabel(BehaviorLabels, BehaviorDropdown.Text), 0, 1);
            r.Action = (MouseActionKind)Math.Clamp(IndexOfLabel(ActionLabels, ActionDropdown.Text), 0, 1);
            r.ContextMode = (MouseContextWhitelistMode)Math.Clamp(IndexOfLabel(ContextModeLabels, ContextModeDropdown.Text), 0, 1);

            r.HoldThresholdMs = ParseMsInt(HoldThresholdTextBox.Text, 200, ClampHold);
            r.RepeatIntervalMs = ParseMsInt(RepeatIntervalTextBox.Text, 100, ClampRepeat);

            int simIdx = IndexOfLabel(SimMouseLabels, SimMouseDropdown.Text);
            r.SimulatedMouseButton = simIdx >= 0 && simIdx < SimMouseValues.Length ? SimMouseValues[simIdx] : 0;

            r.KeyComboText = r.Action == MouseActionKind.KeyCombo
                ? (KeyComboBox.Text?.Trim() ?? string.Empty)
                : string.Empty;

            r.RestrictToProcessList = RestrictToggle.IsOn;

            r.ProcessFilter = _processItems.ToList();

            UpdateHoldThresholdCaption();

            if (raiseEditedEvent)
                AnyRuleEditedFromUi?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// 「按住阈值」在单击与长按下含义相反：单击表示短按允许的最长按住时间，避免用户误以为调大数字要「按更久」才触发。
        /// </summary>
        private void UpdateHoldThresholdCaption()
        {
            if (HoldThresholdCaptionTextBlock == null) return;
            var r = Rule;
            if (r == null)
            {
                HoldThresholdCaptionTextBlock.Text = "按住阈值 (ms)";
                ToolTipService.SetToolTip(HoldThresholdCaptionTextBlock, null);
                return;
            }

            switch (r.Trigger)
            {
                case MouseTriggerKind.Click:
                    HoldThresholdCaptionTextBlock.Text = "短按上限 (ms)";
                    ToolTipService.SetToolTip(HoldThresholdCaptionTextBlock,
                        "单击：从按下到松开若不超过该毫秒数，视为短按并触发本规则；超过则视为长按，不触发本条单击规则。在 200～500 之间调节时，请用「按住约 250ms 再松开」对比：阈值 200 时不触发，阈值 500 时仍触发。");
                    break;
                case MouseTriggerKind.Hold:
                    HoldThresholdCaptionTextBlock.Text = "长按起算 (ms)";
                    ToolTipService.SetToolTip(HoldThresholdCaptionTextBlock,
                        "长按：松开时若按住时长达到或超过该毫秒数，才匹配本规则。");
                    break;
                default:
                    HoldThresholdCaptionTextBlock.Text = "按住阈值 (ms)";
                    ToolTipService.SetToolTip(HoldThresholdCaptionTextBlock, null);
                    break;
            }
        }

        /// <summary>将当前输入框状态写入规则模型，但不触发自动保存事件（供父页面落盘前强制同步）。</summary>
        internal void FlushFromUi() => SyncFromUi(raiseEditedEvent: false);

        #region 物理键录入（无下拉，与组合键栏行为类似）

        private static string FormatPhysicalKeyDisplay(MouseMappingRule r)
        {
            if (r.PhysicalKeyVirtualKey != 0)
                return Win32VkToDisplayName(r.PhysicalKeyVirtualKey);
            if (r.PhysicalMouseHidSignature != 0)
                return $"扩展键 (HID 0x{r.PhysicalMouseHidSignature:X16})";
            if (r.PhysicalMouseRawButtonsMask != 0)
                return $"扩展键 (Raw 0x{r.PhysicalMouseRawButtonsMask:X8})";
            if (r.PhysicalMouseRawButtonFlag != 0)
                return $"扩展键 (RI 0x{r.PhysicalMouseRawButtonFlag:X4})";
            int bi = Math.Clamp((int)r.Button, 0, ButtonLabels.Length - 1);
            return ButtonLabels[bi];
        }

        /// <summary>将 Win32 虚拟键码格式化为与组合键栏一致的短名称。</summary>
        private static string Win32VkToDisplayName(int vk)
        {
            if (vk >= 0 && vk <= 0xFF)
                return VkToName((VirtualKey)vk);
            return $"VK_{vk}";
        }

        private void PhysicalKeyBox_BeforeTextChanging(TextBox sender, TextBoxBeforeTextChangingEventArgs args)
        {
            if (_suppressDirty) return;
            // 仅允许程序写 Text；禁止用户键入/粘贴，否则会与「按键录入」逻辑冲突。IsReadOnly=False 用于显示插入光标。
            args.Cancel = true;
        }

        /// <summary>鼠标左键点入物理键框时清空，准备重新录入；Tab 等仅聚焦不清空。</summary>
        private void ClearPhysicalKeyForNewCapture()
        {
            var r = Rule;
            if (r == null) return;
            r.PhysicalKeyVirtualKey = 0;
            r.PhysicalMouseRawButtonFlag = 0;
            r.PhysicalMouseRawButtonsMask = 0;
            r.PhysicalMouseHidSignature = 0;
            r.Button = MousePhysicalButton.Left;
            _suppressDirty = true;
            PhysicalKeyBox.Text = string.Empty;
            _suppressDirty = false;
            SyncFromUi();
        }

        private void PhysicalKeyBox_GotFocus(object sender, RoutedEventArgs e)
        {
            // 首击左键用于聚焦时，PointerPressed 可能与 GotFocus 同帧；延后武装，避免把「左键」误记为物理键。
            _isRecordingPhysicalKey = false;
            PhysicalKeyBox.PlaceholderText = "请再按一次要绑定的鼠标键或键盘键…";
            uint gen = ++_physicalKeyArmGeneration;
            // 必须用类型名限定：否则与 UI 元素的 DispatcherQueue 实例属性冲突（CS0176）
            var dq = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            dq?.TryEnqueue(() =>
            {
                dq?.TryEnqueue(() =>
                {
                    if (gen != _physicalKeyArmGeneration) return;
                    RawInputMouseBridge.ResetBaselinesForPhysicalCaptureRecording();
                    _isRecordingPhysicalKey = true;
                    try
                    {
                        PhysicalKeyBox.Select(0, 0);
                    }
                    catch { }
                });
            });
        }

        private void PhysicalKeyBox_LostFocus(object sender, RoutedEventArgs e)
        {
            _physicalKeyArmGeneration++;
            _isRecordingPhysicalKey = false;
            PhysicalKeyBox.PlaceholderText = "先左键点此处聚焦，再按要绑定的鼠标或键盘键";
            var r = Rule;
            if (r != null)
            {
                _suppressDirty = true;
                PhysicalKeyBox.Text = FormatPhysicalKeyDisplay(r);
                _suppressDirty = false;
            }
        }

        /// <summary>鼠标键：侧键优先于左/右/中（部分驱动会同时置位多个标志）。</summary>
        private void PhysicalKeyBox_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(PhysicalKeyBox);
            // 仅「尚未武装」时的左键：视为再次点入准备录入，清空旧键位；武装后左键留给「绑定左键」逻辑，不能先清空。
            if (!_isRecordingPhysicalKey
                && e.Pointer.PointerDeviceType == PointerDeviceType.Mouse
                && pt.Properties.IsLeftButtonPressed)
            {
                ClearPhysicalKeyForNewCapture();
            }

            if (!_isRecordingPhysicalKey) return;
            var r = Rule;
            if (r == null) return;

            var props = pt.Properties;
            MousePhysicalButton? chosen = null;
            var kind = props.PointerUpdateKind;
            if (kind == PointerUpdateKind.XButton1Pressed)
                chosen = MousePhysicalButton.XButton1;
            else if (kind == PointerUpdateKind.XButton2Pressed)
                chosen = MousePhysicalButton.XButton2;
            else if (props.IsLeftButtonPressed)
                chosen = MousePhysicalButton.Left;
            else if (props.IsRightButtonPressed)
                chosen = MousePhysicalButton.Right;
            else if (props.IsMiddleButtonPressed)
                chosen = MousePhysicalButton.Middle;
            if (chosen == null) return;

            e.Handled = true;
            r.PhysicalKeyVirtualKey = 0;
            r.PhysicalMouseRawButtonFlag = 0;
            r.PhysicalMouseRawButtonsMask = 0;
            r.PhysicalMouseHidSignature = 0;
            r.Button = chosen.Value;
            _suppressKeyboardPhysicalCaptureUntilMs = Environment.TickCount64 + 220;
            _suppressDirty = true;
            PhysicalKeyBox.Text = ButtonLabels[(int)chosen.Value];
            _suppressDirty = false;
            SyncFromUi();
            Log.Information("鼠标映射 UI：物理键已设为鼠标 {Btn}", chosen.Value);
        }

        /// <summary>键盘键：单键录入（修饰键单独作为源键时也可选）。</summary>
        private void PhysicalKeyBorder_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!_isRecordingPhysicalKey) return;
            if (Environment.TickCount64 < _suppressKeyboardPhysicalCaptureUntilMs)
                return;
            if (PhysicalKeyBox.FocusState == FocusState.Unfocused) return;
            if (!ReferenceEquals(FocusManager.GetFocusedElement(XamlRoot), PhysicalKeyBox))
                return;
            var r = Rule;
            if (r == null) return;

            VirtualKey vk = e.Key;
            e.Handled = true;
            int winVk = (int)vk;
            r.PhysicalKeyVirtualKey = winVk;
            r.PhysicalMouseRawButtonFlag = 0;
            r.PhysicalMouseRawButtonsMask = 0;
            r.PhysicalMouseHidSignature = 0;
            r.Button = MousePhysicalButton.Left;
            _suppressDirty = true;
            PhysicalKeyBox.Text = VkToName(vk);
            _suppressDirty = false;
            SyncFromUi();
            Log.Information("鼠标映射 UI：物理键已设为键盘 vk={Vk}", winVk);
        }

        #endregion

        #region CustomDropdown 列表项

        private void TriggerDropdownItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border { DataContext: string s })
            {
                TriggerDropdown.Text = s;
                TriggerDropdown.IsOpen = false;
                RefreshBehaviorOptionsForTrigger();
                UpdateMsTimingFieldsLayout();
                SyncFromUi();
            }
        }

        private void BehaviorDropdownItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border { DataContext: string s })
            {
                BehaviorDropdown.Text = s;
                BehaviorDropdown.IsOpen = false;
                UpdateMsTimingFieldsLayout();
                SyncFromUi();
            }
        }

        private void ActionDropdownItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border { DataContext: string s })
            {
                ActionDropdown.Text = s;
                ActionDropdown.IsOpen = false;
                UpdateActionVisibility();
                SyncFromUi();
            }
        }

        private void SimMouseDropdownItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border { DataContext: string s })
            {
                SimMouseDropdown.Text = s;
                SimMouseDropdown.IsOpen = false;
                SyncFromUi();
            }
        }

        private void ContextModeDropdownItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border { DataContext: string s })
            {
                ContextModeDropdown.Text = s;
                ContextModeDropdown.IsOpen = false;
                SyncFromUi();
            }
        }

        private void DropdownItemBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
                b.Background = new SolidColorBrush(Color.FromArgb(255, 229, 229, 229));
        }

        private void DropdownItemBorder_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
                b.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        #endregion

        #region 组合键录入（最多 3 段，如 Ctrl+Shift+Z）

        /// <summary>GetKeyboardState 失败时的回退。</summary>
        private static bool IsAsyncKeyDown(int virtualKey) =>
            (MouseInputNative.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        /// <summary>
        /// 使用 GetKeyboardState 全键位图，并同时检查通用 VK（0x10/0x11/0x12）与左右修饰键，减少漏检。
        /// </summary>
        private static void AppendModifierTokensFromKeyboardState(List<string> parts)
        {
            byte[] state = new byte[256];
            if (!MouseInputNative.GetKeyboardState(state))
            {
                if (IsAsyncKeyDown(0xA2) || IsAsyncKeyDown(0xA3)) parts.Add("Ctrl");
                if (IsAsyncKeyDown(0xA0) || IsAsyncKeyDown(0xA1)) parts.Add("Shift");
                if (IsAsyncKeyDown(0xA4) || IsAsyncKeyDown(0xA5)) parts.Add("Alt");
                if (IsAsyncKeyDown(0x5B) || IsAsyncKeyDown(0x5C)) parts.Add("Win");
                return;
            }

            static bool Down(byte[] s, int vk) => vk >= 0 && vk < s.Length && (s[vk] & 0x80) != 0;

            bool ctrl = Down(state, 0x11) || Down(state, 0xA2) || Down(state, 0xA3);
            bool shift = Down(state, 0x10) || Down(state, 0xA0) || Down(state, 0xA1);
            bool alt = Down(state, 0x12) || Down(state, 0xA4) || Down(state, 0xA5);
            bool win = Down(state, 0x5B) || Down(state, 0x5C);

            if (ctrl) parts.Add("Ctrl");
            if (shift) parts.Add("Shift");
            if (alt) parts.Add("Alt");
            if (win) parts.Add("Win");
        }

        private void KeyComboBox_GotFocus(object sender, RoutedEventArgs e)
        {
            _isRecordingKeyCombo = true;
            KeyComboBox.PlaceholderText = "请按下快捷键...";
        }

        private void KeyComboBox_LostFocus(object sender, RoutedEventArgs e)
        {
            _isRecordingKeyCombo = false;
            KeyComboBox.PlaceholderText = "点击后按下快捷键";
        }

        /// <summary>
        /// 挂在 KeyComboBorder 上（隧道先于 TextBox），避免 TextBox 内部先处理 Ctrl+Shift+… 导致只能录到两段。
        /// </summary>
        private void KeyComboBorder_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!_isRecordingKeyCombo) return;
            if (KeyComboBox.FocusState == FocusState.Unfocused) return;

            e.Handled = true;

            var parts = new List<string>();
            AppendModifierTokensFromKeyboardState(parts);

            // 主键必须用 e.Key：在 Ctrl+Shift+字母时，OriginalKey 常为 Shift 等修饰键而非字母，
            // 若用 OriginalKey 覆盖会漏掉主键，表现为只能录两段（如 Ctrl+Shift 而无 Z）。
            VirtualKey vk = e.Key;

            if (vk is not (VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
                or VirtualKey.LeftControl or VirtualKey.RightControl
                or VirtualKey.LeftShift or VirtualKey.RightShift
                or VirtualKey.LeftMenu or VirtualKey.RightMenu
                or VirtualKey.LeftWindows or VirtualKey.RightWindows))
            {
                string keyName = VkToName(vk);
                if (!string.IsNullOrEmpty(keyName))
                    parts.Add(keyName);
            }

            while (parts.Count > 3)
                parts.RemoveAt(0);

            if (parts.Count > 0)
            {
                _suppressDirty = true;
                KeyComboBox.Text = string.Join("+", parts);
                _suppressDirty = false;
                SyncFromUi();
            }
        }

        private static string VkToName(VirtualKey vk) => vk switch
        {
            >= VirtualKey.A and <= VirtualKey.Z => vk.ToString(),
            >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((char)('0' + (int)vk - (int)VirtualKey.Number0)).ToString(),
            VirtualKey.F1 => "F1", VirtualKey.F2 => "F2", VirtualKey.F3 => "F3", VirtualKey.F4 => "F4",
            VirtualKey.F5 => "F5", VirtualKey.F6 => "F6", VirtualKey.F7 => "F7", VirtualKey.F8 => "F8",
            VirtualKey.F9 => "F9", VirtualKey.F10 => "F10", VirtualKey.F11 => "F11", VirtualKey.F12 => "F12",
            VirtualKey.Space => "Space", VirtualKey.Enter => "Enter", VirtualKey.Tab => "Tab",
            VirtualKey.Escape => "Esc", VirtualKey.Back => "Backspace", VirtualKey.Delete => "Delete",
            VirtualKey.Home => "Home", VirtualKey.End => "End",
            VirtualKey.PageUp => "PgUp", VirtualKey.PageDown => "PgDn",
            VirtualKey.Up => "Up", VirtualKey.Down => "Down", VirtualKey.Left => "Left", VirtualKey.Right => "Right",
            _ => MapVirtualKeyFallbackName(vk)
        };

        /// <summary>部分设备/驱动下滚轮等会映射为扩展 VirtualKey，ToString 为 WheelUp/WheelDown 等英文。</summary>
        private static string MapVirtualKeyFallbackName(VirtualKey vk)
        {
            string raw = vk.ToString();
            return raw switch
            {
                "WheelUp" => "滚轮向上",
                "WheelDown" => "滚轮向下",
                _ => raw
            };
        }

        #endregion

        #region 过滤名单（进程路径多选 UI）

        private void UpdateProcessDropdownDisplayText()
        {
            var selected = new List<string>();
            foreach (var cb in FindVisualChildren<CheckBox>(ProcessItemsControl))
            {
                if (cb.IsChecked == true && cb.Tag is string path)
                    selected.Add(path);
            }
            // 输入框不展示完整路径，仅在下拉中展示；摘要文案让用户知悉已选条目数
            if (selected.Count == 0)
                ProcessDropdown.Text = string.Empty;
            else
                ProcessDropdown.Text = $"已选 {selected.Count} 项";
        }

        private void ProcessItemBorder_Tapped(object sender, TappedRoutedEventArgs e)
        {
            try
            {
                if (sender is not Border border) return;
                var cb = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (cb == null) return;
                cb.IsChecked = cb.IsChecked != true;
                SyncFromUi();
                UpdateProcessDropdownDisplayText();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "过滤名单项点击失败");
            }
        }

        private void ProcessItemBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
                b.Background = new SolidColorBrush(Color.FromArgb(255, 229, 229, 229));
        }

        private void ProcessItemBorder_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
                b.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        private void ProcessSelectAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var cb in FindVisualChildren<CheckBox>(ProcessItemsControl))
                cb.IsChecked = true;
            UpdateProcessDropdownDisplayText();
        }

        private void ProcessClearChecks_Click(object sender, RoutedEventArgs e)
        {
            foreach (var cb in FindVisualChildren<CheckBox>(ProcessItemsControl))
                cb.IsChecked = false;
            UpdateProcessDropdownDisplayText();
        }

        private async void ProcessDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var remove = new List<string>();
            foreach (var cb in FindVisualChildren<CheckBox>(ProcessItemsControl))
            {
                if (cb.IsChecked == true && cb.Tag is string path)
                    remove.Add(path);
            }
            if (remove.Count == 0)
            {
                _ = new ContentDialog
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
                Content = $"从过滤名单中移除 {remove.Count} 项？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

            foreach (var p in remove)
                _processItems.Remove(p);
            SyncFromUi();
            RefreshProcessList();
        }

        private async void BrowseExe_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var app = (App)App.Current;
                if (app?.window == null)
                {
                    Log.Warning("浏览 exe：主窗口为空");
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
                    Log.Warning(ex, "FileOpenPicker 失败，使用 Win32 对话框");
                    path = Win32FileDialog.ShowOpenFileDialog(hwnd, "可执行文件|*.exe", "选择要加入列表的程序");
                }

                if (string.IsNullOrEmpty(path)) return;
                if (!_processItems.Contains(path))
                    _processItems.Add(path);
                SyncFromUi();
                RefreshProcessList();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "浏览 exe 失败");
            }
        }

        private void RefreshProcessList()
        {
            _suppressDirty = true;
            ProcessItemsControl.ItemsSource = null;
            ProcessItemsControl.ItemsSource = _processItems;
            _suppressDirty = false;
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
            {
                foreach (var cb in FindVisualChildren<CheckBox>(ProcessItemsControl))
                {
                    if (cb.Tag is string)
                        cb.IsChecked = true;
                }
                UpdateProcessDropdownDisplayText();
            });
        }

        #endregion

        private static List<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            var children = new List<T>();
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T t)
                    children.Add(t);
                children.AddRange(FindVisualChildren<T>(child));
            }
            return children;
        }
    }
}
