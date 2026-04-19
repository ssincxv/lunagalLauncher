using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using System;
using System.Numerics;

namespace lunagalLauncher.Controls
{
    /// <summary>
    /// 自定义下拉框控件 - 输入框内嵌按钮样式
    /// Custom dropdown control with embedded button style
    /// </summary>
    public sealed class CustomDropdown : Control
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
        
        // 动画速率配置（像素/毫秒）
        private const double ANIMATION_VELOCITY = 1.5; // 1.5px/ms = 1500px/s

        // 指针跟踪
        private bool _isPointerInside = false;

        // 静态字段：跟踪当前打开的下拉框
        private static CustomDropdown? _currentOpenDropdown = null;

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
                Log.Information("🔧 直接获取 DropDownIcon = {Icon}", _dropDownIcon != null ? "已获取" : "null");
                
                // 如果直接获取失败，尝试使用 VisualTreeHelper 查找（备用方案）
                if (_dropDownIcon == null && _dropDownButton != null)
                {
                    Log.Warning("🔧 直接获取失败，尝试使用 VisualTreeHelper 查找");
                    _dropDownIcon = FindVisualChild<FontIcon>(_dropDownButton);
                    Log.Information("🔧 VisualTreeHelper 查找结果 = {Icon}", _dropDownIcon != null ? "已获取" : "null");
                    
                    if (_dropDownIcon == null)
                    {
                        Log.Warning("🔧 VisualTreeHelper 查找失败，尝试从 Content 获取");
                        _dropDownIcon = _dropDownButton.Content as FontIcon;
                        Log.Information("🔧 从 Content 获取 DropDownIcon = {Icon}", _dropDownIcon != null ? "已获取" : "null");
                    }
                }

                // 验证必需的部件
                if (_textBox == null || _dropDownButton == null || _popup == null || _popupBorder == null)
                {
                    Log.Error("CustomDropdown 模板部件缺失");
                    return;
                }

                // 关键修复：设置 Popup 的 PlacementTarget
                _popup.PlacementTarget = this;
                Log.Information("🔧 设置 Popup.PlacementTarget = this");
                
                // 确认 Popup.Child 已设置
                if (_popup.Child == null && _popupBorder != null)
                {
                    Log.Warning("⚠️ Popup.Child 为 null，尝试从模板中分离并重新设置");
                    // 注意：在 XAML 中 PopupBorder 已经是 Popup 的 Child，这里只是确认
                }
                
                Log.Information("🔧 Popup.Child 类型: {ChildType}", _popup.Child?.GetType().Name ?? "null");
                
                // 获取 ContentPresenter（在 PopupBorder 内部）
                if (_contentPresenter == null)
                {
                    Log.Error("CustomDropdown ContentPresenter 缺失");
                    return;
                }

                // 订阅事件
                SubscribeEvents();

                // 初始化 Composition Visual
                InitializeCompositionVisual();

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
                Log.Information("🔧 已订阅按钮 Pointer 事件（handledEventsToo: true）");
            }

            if (_popup != null)
            {
                _popup.Opened += Popup_Opened;
                _popup.Closed += Popup_Closed;
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

            this.PointerEntered -= CustomDropdown_PointerEntered;
            this.PointerExited -= CustomDropdown_PointerExited;
        }

        /// <summary>
        /// 初始化 Composition Visual
        /// 新方案：阴影使用 ScaleY 动画，内容使用 InsetClip 动画（在动画方法中创建）
        /// </summary>
        private void InitializeCompositionVisual()
        {
            Log.Information("🔧 开始初始化 Composition Visual (新方案：双动画)");
            
            // 初始化 ShadowBorder 的 Visual（用于输入框底部阴影动画）
            if (_shadowBorder != null)
            {
                try
                {
                    _borderVisual = ElementCompositionPreview.GetElementVisual(_shadowBorder);
                    Log.Information("🔧 获取 ShadowBorder Visual 成功");
                    
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
                    
                    Log.Information("🔧 ShadowBorder DropShadow 已创建");
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

        /// <summary>
        /// 查找可视化树中的子元素
        /// </summary>
        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null)
                return null;

            int childCount = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                
                if (child is T typedChild)
                {
                    return typedChild;
                }

                var result = FindVisualChild<T>(child);
                if (result != null)
                {
                    return result;
                }
            }

            return null;
        }

        /// <summary>
        /// 播放按钮按压动画
        /// </summary>
        private void AnimateButtonPress(bool isPressed)
        {
            if (_dropDownIcon == null)
            {
                Log.Warning("🔄 _dropDownIcon 为 null，无法播放动画");
                return;
            }

            try
            {
                // 确保 RenderTransform 已初始化为 CompositeTransform
                if (_dropDownIcon.RenderTransform == null || _dropDownIcon.RenderTransform is not CompositeTransform)
                {
                    _dropDownIcon.RenderTransform = new CompositeTransform();
                    Log.Information("🔄 初始化 CompositeTransform");
                }

                var transform = (CompositeTransform)_dropDownIcon.RenderTransform;
                
                // 创建 Storyboard 动画
                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation();
                
                if (isPressed)
                {
                    // 按下：向下移动 1.5px
                    animation.Duration = new Duration(TimeSpan.FromMilliseconds(100));
                    animation.To = 1.5;
                    animation.EasingFunction = new Microsoft.UI.Xaml.Media.Animation.QuadraticEase();
                    Log.Information("🔽 按钮按下动画: 向下 1.5px");
                }
                else
                {
                    // 释放：回到原位（使用关键帧动画实现弹跳效果）
                    var keyFrameAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimationUsingKeyFrames();
                    keyFrameAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(200));
                    
                    // 中间帧：向上 0.8px
                    var keyFrame1 = new Microsoft.UI.Xaml.Media.Animation.EasingDoubleKeyFrame
                    {
                        KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
                        Value = -0.8
                    };
                    
                    // 结束帧：复位到 0
                    var keyFrame2 = new Microsoft.UI.Xaml.Media.Animation.EasingDoubleKeyFrame
                    {
                        KeyTime = Microsoft.UI.Xaml.Media.Animation.KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)),
                        Value = 0
                    };
                    
                    keyFrameAnimation.KeyFrames.Add(keyFrame1);
                    keyFrameAnimation.KeyFrames.Add(keyFrame2);
                    
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(keyFrameAnimation, transform);
                    Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(keyFrameAnimation, "TranslateY");
                    
                    storyboard.Children.Add(keyFrameAnimation);
                    storyboard.Begin();
                    
                    Log.Information("🔼 按钮释放动画: 向上 0.8px 然后复位");
                    return;
                }
                
                // 按下动画
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, transform);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, "TranslateY");
                
                storyboard.Children.Add(animation);
                storyboard.Begin();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "🔄 按钮按压动画失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 播放输入框底部阴影动画
        /// 
        /// 功能说明：
        /// - 按下按钮时：阴影从无到有（淡入效果）
        /// - 松开按钮时：阴影从有到无（淡出效果）
        /// 
        /// 技术实现：
        /// - 使用 ShadowBorder.Opacity 控制整体可见性
        /// - 使用 DropShadow.BlurRadius 控制阴影模糊程度
        /// - ShadowBorder 位于输入框下方（Grid.Row="1"）
        /// 
        /// 参数配置：
        /// - 初始状态：Opacity=0, BlurRadius=0（无阴影）
        /// - 按下按钮：Opacity 0→0.6, BlurRadius 0→12（300ms）
        /// - 松开按钮：Opacity 0.6→0, BlurRadius 12→0（300ms）
        /// </summary>
        private void AnimateBorderShadow(bool fadeIn)
        {
            if (_shadowBorder == null)
            {
                Log.Warning("💡 _shadowBorder 为 null，无法播放阴影动画");
                return;
            }

            if (_dropShadow == null)
            {
                Log.Warning("💡 _dropShadow 为 null，无法播放阴影动画");
                return;
            }

            try
            {
                var compositor = _dropShadow.Compositor;
                var borderVisual = ElementCompositionPreview.GetElementVisual(_shadowBorder);

                // 创建 Border Opacity 动画
                var borderOpacityAnimation = compositor.CreateScalarKeyFrameAnimation();
                borderOpacityAnimation.Duration = TimeSpan.FromMilliseconds(300);
                
                // 创建 DropShadow BlurRadius 动画
                var blurAnimation = compositor.CreateScalarKeyFrameAnimation();
                blurAnimation.Duration = TimeSpan.FromMilliseconds(300);
                
                if (fadeIn)
                {
                    // 淡入：Border Opacity 从 0 增加到 0.15（适中的透明度）
                    borderOpacityAnimation.InsertKeyFrame(0.0f, 0f);
                    borderOpacityAnimation.InsertKeyFrame(1.0f, 0.15f);
                    
                    // 淡入：BlurRadius 从 0 增加到 12
                    blurAnimation.InsertKeyFrame(0.0f, 0f);
                    blurAnimation.InsertKeyFrame(1.0f, 12f);
                    
                    Log.Information("💡💡💡 阴影淡入动画：Opacity 0→0.15, BlurRadius 0→12");
                }
                else
                {
                    // 淡出：Border Opacity 从 0.15 恢复到 0
                    borderOpacityAnimation.InsertKeyFrame(0.0f, 0.15f);
                    borderOpacityAnimation.InsertKeyFrame(1.0f, 0f);
                    
                    // 淡出：BlurRadius 从 12 恢复到 0
                    blurAnimation.InsertKeyFrame(0.0f, 12f);
                    blurAnimation.InsertKeyFrame(1.0f, 0f);
                    
                    Log.Information("💡💡💡 阴影淡出动画：Opacity 0.15→0, BlurRadius 12→0");
                }

                // 启动动画
                borderVisual.StartAnimation("Opacity", borderOpacityAnimation);
                _dropShadow.StartAnimation("BlurRadius", blurAnimation);
                
                Log.Information("💡💡💡 阴影动画已启动！请观察输入框底部阴影变化");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "💡 阴影动画失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 播放展开动画（双动画方案 - 基于速率）
        /// - 内容：InsetClip.BottomInset 动画（beifen10 方案）
        /// - 阴影：ScaleY 动画
        /// - 动画时长根据内容高度和速率动态计算
        /// </summary>
        private void PlayExpandAnimation()
        {
            Log.Information("🎬 PlayExpandAnimation 被调用（双动画方案 - 基于速率）");
            
            if (_popupBorder == null || _contentPresenter == null)
            {
                Log.Warning("🎬 _popupBorder 或 _contentPresenter 为 null，无法播放动画");
                return;
            }

            try
            {
                _isAnimating = true;
                Log.Information("🎬 设置 _isAnimating = true");

                // 获取内容的实际高度
                _contentPresenter.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                var contentHeight = _contentPresenter.DesiredSize.Height;
                var targetHeight = Math.Min(contentHeight, MaxDropDownHeight);
                
                // 根据高度和速率计算动画时长
                var duration = CalculateAnimationDuration(targetHeight);
                
                Log.Information("🎬 内容高度: {ContentHeight}, 目标高度: {TargetHeight}, 动画时长: {Duration}ms", 
                    contentHeight, targetHeight, duration);

                // 移除 Height 限制，让内容完整显示
                _popupBorder.Height = double.NaN;

                // 获取 PopupBorder 的 Visual
                var borderVisual = ElementCompositionPreview.GetElementVisual(_popupBorder);
                var compositor = borderVisual.Compositor;

                // 确保透明度为 1
                borderVisual.Opacity = 1f;

                // 创建 InsetClip（裁剪动画）
                var clip = compositor.CreateInsetClip();
                clip.TopInset = 0;
                clip.LeftInset = 0;
                clip.RightInset = 0;
                clip.BottomInset = (float)targetHeight;  // 初始状态：完全裁剪

                borderVisual.Clip = clip;

                // 创建线性插值函数（匀速动画）
                var linearEasing = compositor.CreateLinearEasingFunction();

                // 创建 BottomInset 动画：从 targetHeight（完全裁剪）→ 0（完全显示）
                var clipAnimation = compositor.CreateScalarKeyFrameAnimation();
                clipAnimation.Duration = TimeSpan.FromMilliseconds(duration);
                clipAnimation.InsertKeyFrame(1.0f, 0f, linearEasing);
                clipAnimation.IterationBehavior = AnimationIterationBehavior.Count;
                clipAnimation.IterationCount = 1;

                // 创建动画批次（用于监听完成事件）
                var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                
                batch.Completed += (s, e) =>
                {
                    if (IsOpen)
                    {
                        // 动画完成后，移除裁剪
                        borderVisual.Clip = null;
                        _isAnimating = false;
                        Log.Information("🎬 展开动画完成（InsetClip 方式），移除裁剪");
                    }
                };

                // 启动内容裁剪动画
                clip.StartAnimation("BottomInset", clipAnimation);
                batch.End();
                
                Log.Information("🎬 内容展开动画已启动（InsetClip，匀速，{Duration}ms）", duration);

                // 同时播放阴影 ScaleY 动画（传递相同的时长）
                PlayShadowExpandAnimation(duration);
            }
            catch (Exception ex)
            {
                _isAnimating = false;
                Log.Error(ex, "🎬 播放展开动画失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 播放收起动画（双动画方案 - 基于速率）
        /// - 内容：InsetClip.BottomInset 动画（beifen10 方案）
        /// - 阴影：ScaleY 动画
        /// - 动画时长根据内容高度和速率动态计算
        /// </summary>
        private void PlayCollapseAnimation()
        {
            Log.Information("🎬 PlayCollapseAnimation 被调用（双动画方案 - 基于速率）");
            
            if (_popupBorder == null || _contentPresenter == null)
            {
                Log.Warning("🎬 _popupBorder 或 _contentPresenter 为 null，无法播放收起动画");
                if (_popup != null)
                {
                    _popup.IsOpen = false;
                }
                _isAnimating = false;
                return;
            }

            try
            {
                _isAnimating = true;
                Log.Information("🎬 设置 _isAnimating = true (收起)");

                // 获取当前高度
                var currentHeight = _popupBorder.ActualHeight;
                if (currentHeight <= 0)
                {
                    // 如果当前高度为 0，使用内容的实际高度
                    _contentPresenter.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    currentHeight = _contentPresenter.DesiredSize.Height;
                }
                
                // 根据高度和速率计算动画时长
                var duration = CalculateAnimationDuration(currentHeight);
                
                Log.Information("🎬 当前高度: {CurrentHeight}, 动画时长: {Duration}ms", currentHeight, duration);

                // 获取 PopupBorder 的 Visual
                var borderVisual = ElementCompositionPreview.GetElementVisual(_popupBorder);
                var compositor = borderVisual.Compositor;

                // 创建 InsetClip（裁剪动画）
                var clip = compositor.CreateInsetClip();
                clip.TopInset = 0;
                clip.LeftInset = 0;
                clip.RightInset = 0;
                clip.BottomInset = 0;  // 初始状态：完全显示

                borderVisual.Clip = clip;

                // 创建线性插值函数（匀速动画）
                var linearEasing = compositor.CreateLinearEasingFunction();

                // 创建 BottomInset 动画：从 0（完全显示）→ currentHeight（完全裁剪）
                var clipAnimation = compositor.CreateScalarKeyFrameAnimation();
                clipAnimation.Duration = TimeSpan.FromMilliseconds(duration);
                clipAnimation.InsertKeyFrame(1.0f, (float)currentHeight, linearEasing);
                clipAnimation.IterationBehavior = AnimationIterationBehavior.Count;
                clipAnimation.IterationCount = 1;

                // 创建动画批次（用于监听完成事件）
                var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
                
                batch.Completed += (s, e) =>
                {
                    if (!IsOpen)
                    {
                        if (_popup != null)
                        {
                            _popup.IsOpen = false;
                        }
                        // 移除裁剪
                        borderVisual.Clip = null;
                        _isAnimating = false;
                        Log.Information("🎬 收起动画完成（InsetClip 方式），Popup 已关闭");
                    }
                };

                // 启动内容裁剪动画
                clip.StartAnimation("BottomInset", clipAnimation);
                batch.End();
                
                Log.Information("🎬 内容收起动画已启动（InsetClip，匀速，{Duration}ms）", duration);

                // 同时播放阴影 ScaleY 动画（传递相同的时长）
                PlayShadowCollapseAnimation(duration);
            }
            catch (Exception ex)
            {
                _isAnimating = false;
                if (_popup != null)
                {
                    _popup.IsOpen = false;
                }
                Log.Error(ex, "🎬 播放收起动画失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 播放阴影展开动画（ScaleY 方案 - 基于速率）
        /// </summary>
        private void PlayShadowExpandAnimation(double duration)
        {
            if (_shadowScale == null)
            {
                Log.Warning("🎬 _shadowScale 为 null，无法播放阴影动画");
                return;
            }

            try
            {
                // 创建 ScaleY 动画
                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                var scaleAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(duration))
                    // 不设置 EasingFunction，默认为线性（匀速）
                };

                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(scaleAnimation, _shadowScale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(scaleAnimation, "ScaleY");
                
                storyboard.Children.Add(scaleAnimation);
                storyboard.Begin();
                
                Log.Information("🎬 阴影展开动画已启动（ScaleY: 0→1，{Duration}ms，匀速）", duration);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "🎬 播放阴影展开动画失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 播放阴影收起动画（ScaleY 方案 - 基于速率）
        /// </summary>
        private void PlayShadowCollapseAnimation(double duration)
        {
            if (_shadowScale == null)
            {
                Log.Warning("🎬 _shadowScale 为 null，无法播放阴影动画");
                return;
            }

            try
            {
                // 创建 ScaleY 动画
                var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
                var scaleAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
                {
                    From = 1,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(duration))
                    // 不设置 EasingFunction，默认为线性（匀速）
                };

                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(scaleAnimation, _shadowScale);
                Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(scaleAnimation, "ScaleY");
                
                storyboard.Children.Add(scaleAnimation);
                storyboard.Begin();
                
                Log.Information("🎬 阴影收起动画已启动（ScaleY: 1→0，{Duration}ms，匀速）", duration);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "🎬 播放阴影收起动画失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 根据高度和速率计算动画时长
        /// </summary>
        /// <param name="height">动画距离（像素）</param>
        /// <returns>动画时长（毫秒）</returns>
        private double CalculateAnimationDuration(double height)
        {
            // duration = distance / velocity
            var duration = height / ANIMATION_VELOCITY;
            
            // 设置最小和最大时长限制，避免动画过快或过慢
            const double MIN_DURATION = 100;  // 最小 100ms
            const double MAX_DURATION = 500;  // 最大 500ms
            
            duration = Math.Max(MIN_DURATION, Math.Min(MAX_DURATION, duration));
            
            Log.Information("🎬 计算动画时长: 高度={Height}px, 速率={Velocity}px/ms, 时长={Duration}ms", 
                height, ANIMATION_VELOCITY, duration);
            
            return duration;
        }

        /// <summary>
        /// 降级方案：使用 Height 动画
        /// </summary>
        private void PlayCollapseAnimationWithHeight(double currentHeight)
        {
            // ... 原有的 Height 动画代码 ...
        }
    }
}
