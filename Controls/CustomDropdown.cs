using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace lunagalLauncher.Controls
{
    /// <summary>
    /// 自定义下拉框控件 - 输入框内嵌按钮样式
    /// Custom dropdown control with embedded button style
    /// </summary>
    public sealed partial class CustomDropdown : Control
    {
        // 模板部件名称
        private const string PART_TextBox = "PART_TextBox";
        private const string PART_DropDownButton = "PART_DropDownButton";
        private const string PART_Popup = "PART_Popup";
        private const string PART_PopupBorder = "PART_PopupBorder";
        private const string PART_ShadowBorder = "PART_ShadowBorder";
        private const string PART_ShadowScale = "PART_ShadowScale";
        private const string PART_ContentPresenter = "PART_ContentPresenter";
        private const string PART_ScrollViewer = "PART_ScrollViewer";

        // 模板部件引用
        private TextBox? _textBox;
        private Button? _dropDownButton;
        private FontIcon? _dropDownIcon;
        private Popup? _popup;
        private Border? _popupBorder;
        private Border? _shadowBorderElement;  // 阴影 Border
        private ScaleTransform? _shadowScale;  // 阴影的 ScaleTransform
        private ContentPresenter? _contentPresenter;
        private ScrollViewer? _scrollViewer;
        private Border? _borderElement;
        private Border? _shadowBorder;

        // 动画相关
        private Visual? _borderVisual;
        private Microsoft.UI.Composition.DropShadow? _dropShadow;
        private bool _isAnimating = false;

        /// <summary>
        /// 输入框底部阴影的 Composition Visual（<see cref="_borderVisual"/> + <see cref="_dropShadow"/> + SpriteVisual）
        /// 是否已经由 <see cref="InitializeCompositionVisual"/> 初始化过。
        ///
        /// <para>
        /// 旧实现在 <see cref="OnApplyTemplate"/> 末尾同步初始化，每个 CustomDropdown 实例要花 0.5-3ms，
        /// 一页 5-6 个 CustomDropdown 就是 3-18ms 的启动阻塞。
        /// 新实现：延迟到首次 <see cref="AnimateBorderShadow"/> 被调用（即用户首次按下按钮）时才创建。
        /// </para>
        /// </summary>
        private bool _compositionInitialized;

        // 动画速率配置（像素/毫秒）
        private const double ANIMATION_VELOCITY = 1.5; // 1.5px/ms = 1500px/s

        // 指针跟踪
        private bool _isPointerInside = false;

        // 静态字段：跟踪当前打开的下拉框
        private static CustomDropdown? _currentOpenDropdown = null;

        private bool _preparingFirstOpenRaised;

        /// <summary>
        /// 在实例首次将 <see cref="IsOpen"/> 设为 true、Popup 打开之前触发一次。
        /// 用于在 XAML 不写重型 <see cref="Content"/>，仅在此时 <c>Content = BuildHeavyStuff()</c>。
        /// </summary>
        public event EventHandler? PreparingFirstOpen;

        #region 依赖属性

        /// <summary>
        /// 文本内容
        /// </summary>
        public static readonly DependencyProperty TextProperty =
            DependencyProperty.Register(
                nameof(Text),
                typeof(string),
                typeof(CustomDropdown),
                new PropertyMetadata(string.Empty, OnTextChanged));

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CustomDropdown dropdown && dropdown._textBox != null)
            {
                dropdown._textBox.Text = e.NewValue as string ?? string.Empty;
            }
        }

        /// <summary>
        /// 占位符文本
        /// </summary>
        public static readonly DependencyProperty PlaceholderTextProperty =
            DependencyProperty.Register(
                nameof(PlaceholderText),
                typeof(string),
                typeof(CustomDropdown),
                new PropertyMetadata(string.Empty));

        public string PlaceholderText
        {
            get => (string)GetValue(PlaceholderTextProperty);
            set => SetValue(PlaceholderTextProperty, value);
        }

        /// <summary>
        /// 是否打开下拉框
        /// </summary>
        public static readonly DependencyProperty IsOpenProperty =
            DependencyProperty.Register(
                nameof(IsOpen),
                typeof(bool),
                typeof(CustomDropdown),
                new PropertyMetadata(false, OnIsOpenChanged));

        public bool IsOpen
        {
            get => (bool)GetValue(IsOpenProperty);
            set => SetValue(IsOpenProperty, value);
        }

        private static void OnIsOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CustomDropdown dropdown)
            {
                bool isOpen = (bool)e.NewValue;
                dropdown.UpdatePopupState(isOpen);
            }
        }

        /// <summary>
        /// 下拉内容
        /// </summary>
        public static readonly DependencyProperty ContentProperty =
            DependencyProperty.Register(
                nameof(Content),
                typeof(object),
                typeof(CustomDropdown),
                new PropertyMetadata(null));

        public object Content
        {
            get => GetValue(ContentProperty);
            set => SetValue(ContentProperty, value);
        }

        /// <summary>
        /// 最大下拉高度
        /// </summary>
        public static readonly DependencyProperty MaxDropDownHeightProperty =
            DependencyProperty.Register(
                nameof(MaxDropDownHeight),
                typeof(double),
                typeof(CustomDropdown),
                new PropertyMetadata(300.0));

        public double MaxDropDownHeight
        {
            get => (double)GetValue(MaxDropDownHeightProperty);
            set => SetValue(MaxDropDownHeightProperty, value);
        }

        /// <summary>
        /// 是否只读（不可编辑）
        /// Whether the text box is read-only
        /// </summary>
        public static readonly DependencyProperty IsReadOnlyProperty =
            DependencyProperty.Register(
                nameof(IsReadOnly),
                typeof(bool),
                typeof(CustomDropdown),
                new PropertyMetadata(false, OnIsReadOnlyChanged));

        public bool IsReadOnly
        {
            get => (bool)GetValue(IsReadOnlyProperty);
            set => SetValue(IsReadOnlyProperty, value);
        }

        private static void OnIsReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CustomDropdown dropdown && dropdown._textBox != null)
            {
                bool isReadOnly = (bool)e.NewValue;
                dropdown._textBox.IsReadOnly = isReadOnly;

                // 🔧 修复：当设置为只读时，在控件级别设置光标为箭头
                // Fix: Set cursor to arrow at control level when read-only
                dropdown.UpdateCursorStyle(isReadOnly);
            }
        }

        #endregion

        #region 事件

        /// <summary>
        /// 文本改变事件
        /// </summary>
        public event TextChangedEventHandler? TextChanged;

        #endregion

        /// <summary>
        /// 构造函数
        /// </summary>
        public CustomDropdown()
        {
            this.DefaultStyleKey = typeof(CustomDropdown);
            Log.Debug("CustomDropdown 控件已创建");
        }

        /// <summary>
        /// 应用模板
        /// </summary>
        protected override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            try
            {
                // 取消订阅旧的事件
                UnsubscribeEvents();

                // 获取模板部件
                _textBox = GetTemplateChild(PART_TextBox) as TextBox;
                _dropDownButton = GetTemplateChild(PART_DropDownButton) as Button;
                _popup = GetTemplateChild(PART_Popup) as Popup;
                _popupBorder = GetTemplateChild(PART_PopupBorder) as Border;
                _shadowBorderElement = GetTemplateChild(PART_ShadowBorder) as Border;
                _shadowScale = GetTemplateChild(PART_ShadowScale) as ScaleTransform;
                _contentPresenter = GetTemplateChild(PART_ContentPresenter) as ContentPresenter;
                _scrollViewer = GetTemplateChild(PART_ScrollViewer) as ScrollViewer;
                _borderElement = GetTemplateChild("BorderElement") as Border;
                _shadowBorder = GetTemplateChild("ShadowBorder") as Border;

                // 直接通过 Name 获取 FontIcon（方案A：最可靠的方式）
                _dropDownIcon = GetTemplateChild("PART_DropDownIcon") as FontIcon;
                // 方案 C Phase 1c：Themes/Generic.xaml 中 CustomDropdown 模板里的 FontIcon 已带
                // x:Name="PART_DropDownIcon"，GetTemplateChild 直接命中；热路径不再走 FindVisualChild
                // 全视觉树扫描。如果未来模板改动导致 _dropDownIcon == null，会在按钮按压动画里
                // 体现为"图标不参与 TranslateY 动画"，但功能本身不受影响。
                if (_dropDownIcon == null)
                {
                    Log.Warning("🔧 PART_DropDownIcon 模板部件未命中（图标按压动画不可用）");
                }

                // 验证必需的部件
                if (_textBox == null || _dropDownButton == null || _popup == null || _popupBorder == null)
                {
                    Log.Error("CustomDropdown 模板部件缺失");
                    return;
                }

                // 关键修复：设置 Popup 的 PlacementTarget
                _popup.PlacementTarget = this;
                Log.Debug("🔧 设置 Popup.PlacementTarget = this");

                // 确认 Popup.Child 已设置
                if (_popup.Child == null && _popupBorder != null)
                {
                    Log.Warning("⚠️ Popup.Child 为 null，尝试从模板中分离并重新设置");
                    // 注意：在 XAML 中 PopupBorder 已经是 Popup 的 Child，这里只是确认
                }

                Log.Debug("🔧 Popup.Child 类型: {ChildType}", _popup.Child?.GetType().Name ?? "null");

                // 获取 ContentPresenter（在 PopupBorder 内部）
                if (_contentPresenter == null)
                {
                    Log.Error("CustomDropdown ContentPresenter 缺失");
                    return;
                }

                // 订阅事件
                SubscribeEvents();

                // Composition Visual（输入框底部阴影）改为懒加载：只在首次按下按钮触发
                // AnimateBorderShadow 时才初始化（见 CustomDropdown.Animations.cs）。
                // 每个 CustomDropdown 实例省 0.5-3ms 的 Compositor + DropShadow + SpriteVisual 构造。

                // 同步初始状态
                _textBox.Text = Text;
                _textBox.PlaceholderText = PlaceholderText;
                _textBox.IsReadOnly = IsReadOnly;

                // 🔧 同步光标样式
                // Sync cursor style based on IsReadOnly
                UpdateCursorStyle(IsReadOnly);

                Log.Debug("CustomDropdown 模板已应用");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "CustomDropdown OnApplyTemplate 失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 更新光标样式
        /// Updates cursor style based on IsReadOnly state
        /// </summary>
        /// <param name="isReadOnly">是否只读</param>
        private void UpdateCursorStyle(bool isReadOnly)
        {
            try
            {
                if (isReadOnly)
                {
                    // 只读模式：使用箭头光标
                    // Read-only mode: use arrow cursor
                    this.ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(Microsoft.UI.Input.InputSystemCursorShape.Arrow);

                    // 🔧 关键修复：禁用 TextBox 的指针事件，让控件级别的光标生效
                    // Critical fix: Disable pointer events on TextBox so control-level cursor takes effect
                    if (_textBox != null)
                    {
                        _textBox.IsHitTestVisible = false;
                    }

                    Log.Debug("🖱️ 光标已设置为箭头（只读模式）");
                }
                else
                {
                    // 可编辑模式：使用默认光标（I型光标）
                    // Editable mode: use default cursor (IBeam)
                    this.ProtectedCursor = null; // 使用默认光标

                    if (_textBox != null)
                    {
                        _textBox.IsHitTestVisible = true; // 恢复指针事件
                    }

                    Log.Debug("🖱️ 光标已恢复为默认（可编辑模式）");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新光标样式失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 订阅事件
        /// </summary>
        private void SubscribeEvents()
        {
            if (_textBox != null)
            {
                _textBox.TextChanged += TextBox_TextChanged;
                _textBox.GotFocus += TextBox_GotFocus;
                _textBox.LostFocus += TextBox_LostFocus;
            }

            if (_dropDownButton != null)
            {
                _dropDownButton.Click += DropDownButton_Click;
                // 使用 AddHandler 并设置 handledEventsToo: true，确保即使事件被 Button 处理，我们的处理器仍然会被调用
                _dropDownButton.AddHandler(PointerPressedEvent, new PointerEventHandler(DropDownButton_PointerPressed), handledEventsToo: true);
                _dropDownButton.AddHandler(PointerReleasedEvent, new PointerEventHandler(DropDownButton_PointerReleased), handledEventsToo: true);
                Log.Debug("🔧 已订阅按钮 Pointer 事件（handledEventsToo: true）");
            }

            if (_popup != null)
            {
                _popup.Opened += Popup_Opened;
                _popup.Closed += Popup_Closed;
            }

            // 弹出层与输入框不在同一命中区域：鼠标移入下拉面板时必须视为「仍在控件内」，
            // 否则 TextBox.LostFocus 会在 Low 优先级把 IsOpen 关掉，导致按钮 Click 与列表状态异常。
            // Popup content is not a visual child of the main control; track pointer inside the popup border.
            if (_popupBorder != null)
            {
                _popupBorder.PointerEntered += PopupBorder_PointerEntered;
                _popupBorder.PointerExited += PopupBorder_PointerExited;
            }

            // 订阅指针事件以跟踪鼠标位置
            this.PointerEntered += CustomDropdown_PointerEntered;
            this.PointerExited += CustomDropdown_PointerExited;
        }

        /// <summary>
        /// 取消订阅事件
        /// </summary>
        private void UnsubscribeEvents()
        {
            if (_textBox != null)
            {
                _textBox.TextChanged -= TextBox_TextChanged;
                _textBox.GotFocus -= TextBox_GotFocus;
                _textBox.LostFocus -= TextBox_LostFocus;
            }

            if (_dropDownButton != null)
            {
                _dropDownButton.Click -= DropDownButton_Click;
                // 使用 RemoveHandler 移除通过 AddHandler 添加的事件
                _dropDownButton.RemoveHandler(PointerPressedEvent, new PointerEventHandler(DropDownButton_PointerPressed));
                _dropDownButton.RemoveHandler(PointerReleasedEvent, new PointerEventHandler(DropDownButton_PointerReleased));
            }

            if (_popup != null)
            {
                _popup.Opened -= Popup_Opened;
                _popup.Closed -= Popup_Closed;
            }

            if (_popupBorder != null)
            {
                _popupBorder.PointerEntered -= PopupBorder_PointerEntered;
                _popupBorder.PointerExited -= PopupBorder_PointerExited;
            }

            this.PointerEntered -= CustomDropdown_PointerEntered;
            this.PointerExited -= CustomDropdown_PointerExited;
        }

        /// <summary>
        /// 初始化输入框底部阴影的 Composition Visual（幂等：多次调用只做一次）。
        ///
        /// <para>
        /// 调用链：由 <see cref="AnimateBorderShadow"/> 在首次播放阴影动画前触发（懒加载）。
        /// 这是方案 C Phase 1a 的核心优化——把 Compositor + DropShadow + SpriteVisual 的创建从
        /// OnApplyTemplate 热路径挪到首次按压按钮时，让每个 CustomDropdown 的模板应用省 0.5-3ms。
        /// </para>
        /// </summary>
        internal void InitializeCompositionVisual()
        {
            if (_compositionInitialized) return;
            _compositionInitialized = true;

            Log.Debug("🔧 开始初始化 Composition Visual (首次按压触发的懒加载)");

            // 初始化 ShadowBorder 的 Visual（用于输入框底部阴影动画）
            if (_shadowBorder != null)
            {
                try
                {
                    _borderVisual = ElementCompositionPreview.GetElementVisual(_shadowBorder);
                    Log.Debug("🔧 获取 ShadowBorder Visual 成功");

                    // 创建 DropShadow（用于输入框底部阴影）
                    var compositor = _borderVisual.Compositor;
                    _dropShadow = compositor.CreateDropShadow();
                    _dropShadow.BlurRadius = 0f;      // 初始模糊半径为 0（无阴影）
                    _dropShadow.Offset = new Vector3(0, 0, 0);  // 无偏移
                    _dropShadow.Opacity = 1f;         // 阴影本身不透明，通过 Border 的 Opacity 控制
                    _dropShadow.Color = Windows.UI.Color.FromArgb(255, 0, 0, 0);  // 黑色阴影

                    // 将阴影应用到 ShadowBorder
                    var shadowVisual = compositor.CreateSpriteVisual();
                    shadowVisual.Shadow = _dropShadow;
                    shadowVisual.Size = new Vector2((float)_shadowBorder.ActualWidth, (float)_shadowBorder.ActualHeight);

                    ElementCompositionPreview.SetElementChildVisual(_shadowBorder, shadowVisual);

                    // 监听尺寸变化
                    _shadowBorder.SizeChanged += (s, e) =>
                    {
                        if (shadowVisual != null)
                        {
                            shadowVisual.Size = new Vector2((float)e.NewSize.Width, (float)e.NewSize.Height);
                        }
                    };

                    Log.Debug("🔧 ShadowBorder DropShadow 已创建");
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "🔧 初始化 ShadowBorder Visual 失败: {Message}", ex.Message);
                }
            }
        }

        /// <summary>
        /// 文本框文本改变事件
        /// </summary>
        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            Text = _textBox?.Text ?? string.Empty;
            TextChanged?.Invoke(this, e);
        }

        /// <summary>
        /// 文本框获得焦点事件
        /// </summary>
        private void TextBox_GotFocus(object sender, RoutedEventArgs e)
        {
            Log.Debug("TextBox 获得焦点");
            // 不播放阴影动画，避免黑线出现
        }

        /// <summary>
        /// 文本框失去焦点事件
        /// </summary>
        private void TextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            Log.Debug("TextBox 失去焦点");

            // 延迟检查，给点击下拉内容的时间
            DispatcherQueue?.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_isPointerInside && IsOpen)
                {
                    IsOpen = false;
                }
            });
        }

        /// <summary>
        /// 下拉按钮点击事件
        /// </summary>
        private void DropDownButton_Click(object sender, RoutedEventArgs e)
        {
            Log.Information("🎯 下拉按钮点击事件触发");
            Log.Information("🎯 当前 IsOpen = {IsOpen}", IsOpen);
            Log.Information("🎯 _popup = {Popup}", _popup != null ? "已初始化" : "null");
            Log.Information("🎯 _shadowScale = {Scale}", _shadowScale != null ? "已初始化" : "null");
            Log.Information("🎯 _contentPresenter = {Presenter}", _contentPresenter != null ? "已初始化" : "null");

            IsOpen = !IsOpen;

            Log.Information("🎯 设置后 IsOpen = {IsOpen}", IsOpen);
        }

        /// <summary>
        /// 下拉按钮按下事件
        /// </summary>
        private void DropDownButton_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ 按钮按下事件触发");
            AnimateButtonPress(true);
        }

        /// <summary>
        /// 下拉按钮释放事件
        /// </summary>
        private void DropDownButton_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            Log.Information("🖱️ 按钮释放事件触发");
            AnimateButtonPress(false);
        }

        /// <summary>
        /// 指针进入事件
        /// </summary>
        private void CustomDropdown_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerInside = true;
        }

        /// <summary>
        /// 指针离开事件
        /// </summary>
        private void CustomDropdown_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerInside = false;
        }

        /// <summary>
        /// 指针进入下拉面板：标记为仍属本控件交互，避免 TextBox.LostFocus 误关 Popup。
        /// </summary>
        private void PopupBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _isPointerInside = true;
        }

        /// <summary>
        /// 指针离开下拉面板：允许 LostFocus 逻辑在适当时机关闭下拉。
        /// </summary>
        private void PopupBorder_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _isPointerInside = false;
        }

        /// <summary>
        /// Popup 打开事件
        /// </summary>
        private void Popup_Opened(object? sender, object e)
        {
            Log.Debug("Popup 已打开");

            // 动态设置下拉框宽度以匹配输入框宽度
            UpdateDropdownWidth();

            // 订阅输入框宽度变化事件
            if (_borderElement != null)
            {
                _borderElement.SizeChanged += BorderElement_SizeChanged;
                Log.Information("📏 已订阅输入框宽度变化事件");
            }

            // 订阅 PopupBorder 尺寸变化事件，用于同步阴影层
            if (_popupBorder != null)
            {
                _popupBorder.SizeChanged += PopupBorder_SizeChanged;
                Log.Information("📏 已订阅 PopupBorder 尺寸变化事件");
            }

            // 订阅全局点击事件（用于点击外部区域关闭下拉框）
            if (XamlRoot != null)
            {
                XamlRoot.Content.AddHandler(PointerPressedEvent, new PointerEventHandler(OnGlobalPointerPressed), handledEventsToo: true);
                Log.Information("🌐 已订阅全局点击事件");
            }

            // 诊断 Popup 位置
            if (_popup != null && _popupBorder != null)
            {
                var transform = _popupBorder.TransformToVisual(null);
                var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                Log.Information("📍 Popup 位置: X={X}, Y={Y}", point.X, point.Y);
                Log.Information("📍 PopupBorder 尺寸: Width={Width}, Height={Height}",
                    _popupBorder.ActualWidth, _popupBorder.ActualHeight);
                Log.Information("📍 控件位置: X={X}, Y={Y}", this.ActualOffset.X, this.ActualOffset.Y);
                Log.Information("📍 控件尺寸: Width={Width}, Height={Height}",
                    this.ActualWidth, this.ActualHeight);
            }
        }

        /// <summary>
        /// Popup 关闭事件
        /// </summary>
        private void Popup_Closed(object? sender, object e)
        {
            Log.Information("📌 Popup 已关闭事件触发");
            Log.Information("📌 当前 IsOpen = {IsOpen}, Popup.IsOpen = {PopupIsOpen}", IsOpen, _popup?.IsOpen);

            // 取消订阅输入框宽度变化事件
            if (_borderElement != null)
            {
                _borderElement.SizeChanged -= BorderElement_SizeChanged;
                Log.Information("📏 已取消订阅输入框宽度变化事件");
            }

            // 取消订阅 PopupBorder 尺寸变化事件
            if (_popupBorder != null)
            {
                _popupBorder.SizeChanged -= PopupBorder_SizeChanged;
                Log.Information("📏 已取消订阅 PopupBorder 尺寸变化事件");
            }

            // 取消订阅全局点击事件
            if (XamlRoot != null)
            {
                XamlRoot.Content.RemoveHandler(PointerPressedEvent, new PointerEventHandler(OnGlobalPointerPressed));
                Log.Information("🌐 已取消订阅全局点击事件");
            }

            // 不要在这里同步状态，会导致循环触发
            // Popup 的关闭应该只由用户操作触发，不应该自动关闭
        }

        /// <summary>
        /// 输入框宽度变化事件
        /// </summary>
        private void BorderElement_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 实时更新下拉框宽度
            UpdateDropdownWidth();
            Log.Information("📏 输入框宽度变化: {OldWidth}px → {NewWidth}px，下拉框已自适应",
                e.PreviousSize.Width, e.NewSize.Width);
        }

        /// <summary>
        /// PopupBorder 尺寸变化事件
        /// </summary>
        private void PopupBorder_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 实时更新阴影层尺寸
            UpdateShadowBorderSize();
            Log.Information("📏 PopupBorder 尺寸变化: {OldSize} → {NewSize}，阴影层已同步",
                e.PreviousSize, e.NewSize);
        }

        /// <summary>
        /// 全局点击事件处理（用于点击外部区域关闭下拉框）
        /// </summary>
        private void OnGlobalPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!IsOpen || _popup == null || _popupBorder == null)
            {
                return;
            }

            try
            {
                // 获取点击位置
                var pointerPoint = e.GetCurrentPoint(null);
                var clickPosition = pointerPoint.Position;

                // 检查点击是否在当前控件内（输入框 + 按钮）
                var controlBounds = this.TransformToVisual(null).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, this.ActualWidth, this.ActualHeight));

                bool clickedInsideControl = controlBounds.Contains(clickPosition);

                // 检查点击是否在下拉框内
                var popupBounds = _popupBorder.TransformToVisual(null).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, _popupBorder.ActualWidth, _popupBorder.ActualHeight));

                bool clickedInsidePopup = popupBounds.Contains(clickPosition);

                // 如果点击在控件外部和下拉框外部，关闭下拉框
                if (!clickedInsideControl && !clickedInsidePopup)
                {
                    Log.Information("🌐 检测到外部点击，关闭下拉框");
                    IsOpen = false;
                }
                else
                {
                    Log.Debug("🌐 点击在控件或下拉框内部，保持打开状态");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "🌐 全局点击事件处理失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 更新下拉框宽度以匹配输入框，同时更新阴影层尺寸
        /// </summary>
        private void UpdateDropdownWidth()
        {
            if (_popupBorder != null && _borderElement != null)
            {
                double inputWidth = _borderElement.ActualWidth;
                _popupBorder.Width = inputWidth;
                _popupBorder.MinWidth = inputWidth;
                _popupBorder.MaxWidth = inputWidth;
                Log.Information("📏 下拉框宽度已设置为: {Width}px（匹配输入框宽度）", inputWidth);

                // 同时更新阴影层的尺寸
                UpdateShadowBorderSize();
            }
        }

        /// <summary>
        /// 更新阴影层尺寸以匹配内容层
        /// </summary>
        private void UpdateShadowBorderSize()
        {
            if (_shadowBorderElement != null && _popupBorder != null)
            {
                _shadowBorderElement.Width = _popupBorder.ActualWidth;
                _shadowBorderElement.Height = _popupBorder.ActualHeight;
                Log.Information("📏 阴影层尺寸已更新: Width={Width}px, Height={Height}px",
                    _popupBorder.ActualWidth, _popupBorder.ActualHeight);
            }
        }

        /// <summary>
        /// 更新 Popup 状态
        /// </summary>
        private void UpdatePopupState(bool isOpen)
        {
            Log.Information("🔥 UpdatePopupState 被调用: isOpen={IsOpen}", isOpen);
            Log.Information("🔥 _popup = {Popup}", _popup != null ? "已初始化" : "null");
            Log.Information("🔥 _shadowScale = {Scale}", _shadowScale != null ? "已初始化" : "null");
            Log.Information("🔥 _contentPresenter = {Presenter}", _contentPresenter != null ? "已初始化" : "null");
            Log.Information("🔥 Content = {Content}", Content != null ? "有内容" : "null");
            Log.Information("🔥 _popup.Child = {Child}", _popup?.Child != null ? "有内容" : "null");

            if (_popup == null)
            {
                Log.Warning("🔥 _popup 为 null，退出");
                return;
            }

            try
            {
                Log.Information("🔥 _isAnimating = {IsAnimating}", _isAnimating);

                // 如果正在动画中，先停止所有动画
                if (_isAnimating)
                {
                    // Height 动画会自动停止
                    _isAnimating = false;
                    Log.Information("🔥 停止了正在进行的动画");
                }

                if (isOpen)
                {
                    if (!_preparingFirstOpenRaised)
                    {
                        _preparingFirstOpenRaised = true;
                        PreparingFirstOpen?.Invoke(this, EventArgs.Empty);
                    }

                    // 关闭之前打开的下拉框
                    if (_currentOpenDropdown != null && _currentOpenDropdown != this)
                    {
                        _currentOpenDropdown.IsOpen = false;
                        Log.Information("🔒 自动关闭之前打开的下拉框");
                    }

                    // 设置当前下拉框为打开状态
                    _currentOpenDropdown = this;

                    // 打开下拉框
                    Log.Information("🔥 准备打开 Popup");

                    // 确保内容可见
                    if (_contentPresenter != null)
                    {
                        _contentPresenter.Visibility = Visibility.Visible;
                        Log.Information("🔥 设置 ContentPresenter.Visibility = Visible");
                    }

                    _popup.IsOpen = true;
                    Log.Information("🔥 Popup.IsOpen 已设置为 true");
                    PlayExpandAnimation();
                }
                else
                {
                    // 关闭下拉框
                    Log.Information("🔥 准备关闭 Popup");

                    // 清除静态引用
                    if (_currentOpenDropdown == this)
                    {
                        _currentOpenDropdown = null;
                        Log.Information("🔒 清除当前下拉框的静态引用");
                    }

                    PlayCollapseAnimation();
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "🔥 更新 Popup 状态失败: {Message}", ex.Message);
            }
        }

        // FindVisualChild<T> 已集中到 Utils.VisualTreeExtensions（通过文件顶部 using static 裸调）。

        // 动画相关方法已集中到 CustomDropdown.Animations.cs（同一 partial class）。
        // 动画方法已全部迁移到 CustomDropdown.Animations.cs（同一 partial class）

    }
}
