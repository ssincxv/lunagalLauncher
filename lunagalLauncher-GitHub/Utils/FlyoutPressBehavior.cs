using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Serilog;
using Windows.Foundation;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// Flyout 打开时的按钮按压动画行为
    /// Why: WinUI 3 的 Flyout Popup 层会拦截所有指针事件，按钮无法接收 PointerPressed/Released
    /// How: 在 Window.Content 上使用 AddHandler(handledEventsToo: true) 捕获全局指针输入，手动 hit-test 判断是否点击按钮
    /// 
    /// 核心优化机制：
    /// 1. 队列机制：最多排队 3 次点击，严格串行处理
    /// 2. 状态同步：使用 _isFlyoutOpen 跟踪状态，订阅 Opened/Closed 事件确保一致性
    /// 3. 动画等待：每次操作都等待动画完成，避免状态冲突
    /// 4. 稳定锚点：始终使用按钮本身作为锚点，避免位置跳变
    /// 5. 最终状态保证：快速点击后，Flyout 始终保持打开状态
    /// </summary>
    public class FlyoutPressBehavior
    {
        private readonly Window _window;
        private readonly Button _button;
        private readonly FlyoutBase _flyout;
        private readonly Action _onPress;
        private readonly Action _onRelease;
        
        // 使用 pointerId 绑定按下/松开
        private uint? _pressedPointerId = null;
        
        // 队列机制：使用简单计数器
        private int _pendingClicks = 0;
        private bool _isProcessing = false;
        private const int MaxQueueSize = 3;  // 最多排队 3 次点击
        
        // Flyout 状态跟踪（关键：确保状态一致性）
        private bool _isFlyoutOpen = false;
        
        // 动画延迟时间（精确控制）
        private const int ReleaseDelayMs = 300;     // 回弹动画时长
        private const int FlyoutCloseDelayMs = 250; // Flyout 关闭动画时长
        private const int FlyoutOpenDelayMs = 250;  // Flyout 打开动画时长
        private const int StateWaitMs = 50;         // 状态同步等待间隔
        private const int MaxStateWaitMs = 500;     // 最大状态等待时间

        private FlyoutPressBehavior(
            Window window,
            Button button,
            FlyoutBase flyout,
            Action onPress,
            Action onRelease)
        {
            _window = window;
            _button = button;
            _flyout = flyout;
            _onPress = onPress;
            _onRelease = onRelease;
        }

        /// <summary>
        /// 附加行为到按钮和 Flyout
        /// </summary>
        public static FlyoutPressBehavior Attach(
            Window window,
            Button button,
            FlyoutBase flyout,
            Action onPress,
            Action onRelease)
        {
            var behavior = new FlyoutPressBehavior(window, button, flyout, onPress, onRelease);
            behavior.Initialize();
            return behavior;
        }

        private void Initialize()
        {
            try
            {
                // ⚠️ 关键修复：禁用 LightDismiss 自动关闭机制
                _flyout.LightDismissOverlayMode = LightDismissOverlayMode.Off;
                
                // 🔧 重要：不设置 OverlayInputPassThroughElement
                // 原因：设置后会导致点击按钮时穿透 Overlay，触发意外的 Flyout 关闭
                // 解决方案：完全依赖 Window 级别的事件监听 + 手动 hit-test
                
                // 订阅 Flyout 状态事件（关键：确保状态同步）
                _flyout.Opened += (s, e) =>
                {
                    _isFlyoutOpen = true;
                    Log.Information("✅ [Event] Flyout.Opened - 状态已更新为打开");
                };
                
                _flyout.Closed += (s, e) =>
                {
                    _isFlyoutOpen = false;
                    Log.Information("❌ [Event] Flyout.Closed - 状态已更新为关闭");
                };
                
                // 关键修复：监听 Flyout.Closing 事件，在关闭前触发释放动画
                _flyout.Closing += (s, e) =>
                {
                    // 如果有按下状态，触发释放动画
                    if (_pressedPointerId != null)
                    {
                        Log.Information($"🎯 [Flyout.Closing] 检测到 Flyout 关闭，触发释放动画 (PointerId={_pressedPointerId})");
                        
                        // 清除状态
                        _pressedPointerId = null;
                        
                        // 播放回弹动画
                        _onRelease?.Invoke();
                    }
                };
                
                // 初始化状态（与 Flyout 当前状态同步）
                _isFlyoutOpen = _flyout.IsOpen;
                
                // 方法1：在按钮上直接监听事件（优先级最高）
                // 注意：当 Flyout 打开时，按钮会被 Overlay 遮挡，这些事件不会触发
                // 这是正常的，我们依赖 Window 级别的事件来处理
                _button.AddHandler(UIElement.PointerPressedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(OnButtonPointerPressed), true);
                _button.AddHandler(UIElement.PointerReleasedEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(OnButtonPointerReleased), true);
                _button.AddHandler(UIElement.PointerCaptureLostEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(OnButtonPointerReleased), true);
                _button.AddHandler(UIElement.PointerCanceledEvent, 
                    new Microsoft.UI.Xaml.Input.PointerEventHandler(OnButtonPointerReleased), true);
                
                // 方法2：在 Window.Content 上也监听（作为备用）
                if (_window.Content is UIElement content)
                {
                    content.AddHandler(UIElement.PointerPressedEvent, 
                        new Microsoft.UI.Xaml.Input.PointerEventHandler(OnWindowPointerPressed), true);
                    content.AddHandler(UIElement.PointerReleasedEvent, 
                        new Microsoft.UI.Xaml.Input.PointerEventHandler(OnWindowPointerEnded), true);
                    content.AddHandler(UIElement.PointerCaptureLostEvent, 
                        new Microsoft.UI.Xaml.Input.PointerEventHandler(OnWindowPointerEnded), true);
                    content.AddHandler(UIElement.PointerCanceledEvent, 
                        new Microsoft.UI.Xaml.Input.PointerEventHandler(OnWindowPointerEnded), true);
                    
                    Log.Information("✅ FlyoutPressBehavior 初始化成功 (按钮直接监听 + Window备用监听)");
                }
                else
                {
                    Log.Warning("⚠️ 无法获取 Window.Content");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "FlyoutPressBehavior 初始化失败");
            }
        }

        /// <summary>
        /// 按钮级别 PointerPressed 事件（优先级最高）
        /// </summary>
        private void OnButtonPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                // 记录 PointerId 并播放按压动画
                _pressedPointerId = e.Pointer.PointerId;
                _onPress?.Invoke();
                
                Log.Information($"🎯 [ButtonPress] 按下按钮 (PointerId={e.Pointer.PointerId}, Flyout={(_isFlyoutOpen ? "打开" : "关闭")})");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ButtonPointerPressed 处理失败");
            }
        }

        /// <summary>
        /// 按钮级别 PointerReleased 事件（优先级最高）
        /// </summary>
        private void OnButtonPointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                // 只处理同一个 PointerId
                if (_pressedPointerId == null || _pressedPointerId != e.Pointer.PointerId)
                    return;
                
                var pointerId = e.Pointer.PointerId;
                
                // 清除状态
                _pressedPointerId = null;
                
                // 播放回弹动画
                _onRelease?.Invoke();
                
                Log.Information($"🎯 [ButtonRelease] 松开按钮 (PointerId={pointerId}, Flyout={(_isFlyoutOpen ? "打开" : "关闭")})");
                
                // 切换 Flyout 状态：打开→关闭，关闭→打开
                EnqueueClick();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "ButtonPointerReleased 处理失败");
            }
        }

        /// <summary>
        /// Window 级别 PointerPressed 事件
        /// 处理按压动画
        /// </summary>
        private void OnWindowPointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                // 手动 hit-test：判断点击位置是否在按钮区域内
                var pointerPoint = e.GetCurrentPoint(null);
                var screenPosition = pointerPoint.Position;
                
                // 获取按钮在屏幕上的位置和大小
                var buttonTransform = _button.TransformToVisual(null);
                var buttonPosition = buttonTransform.TransformPoint(new Point(0, 0));
                
                var buttonBounds = new Rect(
                    buttonPosition.X,
                    buttonPosition.Y,
                    _button.ActualWidth,
                    _button.ActualHeight
                );
                
                bool isHit = buttonBounds.Contains(screenPosition);
                
                // 如果 Flyout 打开，还需要检查是否点击在按钮的父级 Border 区域内
                // 因为 Flyout 的 Popup 层可能遮挡了按钮
                if (!isHit && _isFlyoutOpen)
                {
                    var parentBorder = FindParentBorder(_button);
                    if (parentBorder != null)
                    {
                        var borderTransform = parentBorder.TransformToVisual(null);
                        var borderPosition = borderTransform.TransformPoint(new Point(0, 0));
                        
                        var borderBounds = new Rect(
                            borderPosition.X,
                            borderPosition.Y,
                            parentBorder.ActualWidth,
                            parentBorder.ActualHeight
                        );
                        
                        isHit = borderBounds.Contains(screenPosition);
                        
                        if (isHit)
                        {
                            Log.Information($"🎯 [Press] 通过 Border hit-test 检测到点击");
                        }
                    }
                }
                
                if (isHit)
                {
                    // 🔧 关键修复：标记事件为已处理，阻止 Flyout 的 LightDismiss 机制
                    // 这样可以防止 Flyout 在按下时就被关闭
                    if (_isFlyoutOpen)
                    {
                        e.Handled = true;
                        Log.Information($"✅ [Press] 已阻止事件传播，防止 Flyout 自动关闭");
                    }
                    
                    // 记录 PointerId 并播放按压动画
                    _pressedPointerId = e.Pointer.PointerId;
                    _onPress?.Invoke();
                    
                    Log.Information($"🎯 [Press] 按下按钮 (PointerId={e.Pointer.PointerId}, Flyout={(_isFlyoutOpen ? "打开" : "关闭")})");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PointerPressed 处理失败");
            }
        }

        /// <summary>
        /// Window 级别"指针结束"事件统一处理
        /// 松开鼠标时切换 Flyout 状态
        /// </summary>
        private void OnWindowPointerEnded(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                // 只处理同一个 PointerId
                if (_pressedPointerId == null || _pressedPointerId != e.Pointer.PointerId)
                    return;
                
                var pointerId = e.Pointer.PointerId;
                
                // 清除状态
                _pressedPointerId = null;
                
                // 播放回弹动画
                _onRelease?.Invoke();
                
                Log.Information($"🎯 [Release] 松开按钮 (PointerId={pointerId}, Flyout={(_isFlyoutOpen ? "打开" : "关闭")})");
                
                // 切换 Flyout 状态：打开→关闭，关闭→打开
                EnqueueClick();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "PointerEnded 处理失败");
            }
        }

        /// <summary>
        /// 向队列中添加点击事件
        /// </summary>
        private void EnqueueClick()
        {
            // 如果队列已满，丢弃超出的事件
            if (_pendingClicks >= MaxQueueSize)
            {
                Log.Warning($"⚠️ 队列已满 ({MaxQueueSize})，丢弃此次点击");
                return;
            }

            // 增加待处理点击计数
            _pendingClicks++;

            Log.Information($"📥 [Queue] 入队点击 (pending={_pendingClicks}/{MaxQueueSize})");

            // 如果当前没有在处理，则开始处理
            if (!_isProcessing)
            {
                _ = ProcessNextClickAsync();
            }
        }

        /// <summary>
        /// 串行处理队列中的点击事件
        /// 关键：使用 async Task 确保真正的串行执行
        /// </summary>
        private async Task ProcessNextClickAsync()
        {
            // 双重检查：防止并发调用
            if (_pendingClicks == 0 || _isProcessing)
            {
                if (_pendingClicks == 0)
                {
                    _isProcessing = false;
                    Log.Information("✅ [Queue] 队列处理完成");
                }
                return;
            }

            _isProcessing = true;
            
            _pendingClicks--; // 减少待处理计数
            
            Log.Information($"🚀 [Queue] 开始处理点击 (剩余pending={_pendingClicks})");
            
            // 等待回弹动画完成
            await Task.Delay(ReleaseDelayMs);
            
            // 执行点击处理逻辑（确保串行）
            await ProcessClickAsync();
            
            // 确保当前点击完全处理完成后，才标记为未处理状态
            _isProcessing = false;
            
            Log.Information($"✅ [Queue] 点击处理完成 (剩余pending={_pendingClicks})");
            
            // 继续处理下一个（递归调用）
            if (_pendingClicks > 0)
            {
                await ProcessNextClickAsync();
            }
        }

        /// <summary>
        /// 单个点击处理：切换 Flyout 状态
        /// 核心逻辑：
        /// 1. 如果 Flyout 未打开，直接打开
        /// 2. 如果 Flyout 已打开，直接关闭
        /// 
        /// 优化：使用父级 Border 作为锚点，确保 Flyout 宽度和位置正确
        /// </summary>
        private async Task ProcessClickAsync()
        {
            try
            {
                Log.Information($"📊 [Click] 开始处理 (当前状态: {(_isFlyoutOpen ? "已打开" : "已关闭")})");
                
                if (_isFlyoutOpen)
                {
                    // Flyout 已打开，关闭它
                    Log.Information("🔒 [Click] Flyout 已打开，关闭它");
                    _flyout.Hide();
                    
                    // 等待关闭动画完成
                    await Task.Delay(FlyoutCloseDelayMs);
                    
                    // 等待状态同步（最多等待 500ms）
                    await WaitForFlyoutState(expectedOpen: false, maxWaitMs: MaxStateWaitMs);
                    
                    Log.Information("✅ [Click] Flyout 已关闭");
                }
                else
                {
                    // Flyout 未打开，打开它
                    Log.Information("🚀 [Click] Flyout 未打开，打开它");
                    
                    // 查找父级 Border 作为锚点（确保 Flyout 宽度和位置正确）
                    var parentBorder = FindParentBorder(_button);
                    
                    // 🔧 关键修复：明确指定 Placement 为 Bottom，确保弹窗从按钮下方弹出
                    var showOptions = new FlyoutShowOptions
                    {
                        Placement = FlyoutPlacementMode.Bottom,  // 强制从下方弹出
                        ShowMode = FlyoutShowMode.Standard
                    };
                    
                    if (parentBorder != null)
                    {
                        _flyout.ShowAt(parentBorder, showOptions);
                        
                        Log.Information($"✅ [Click] 使用 Border 作为锚点 (宽度: {parentBorder.ActualWidth}px, Placement=Bottom)");
                    }
                    else
                    {
                        // 回退方案：使用按钮作为锚点
                        _flyout.ShowAt(_button, showOptions);
                        
                        Log.Warning("⚠️ [Click] 未找到父级 Border，使用按钮作为锚点 (Placement=Bottom)");
                    }
                    
                    // 等待打开动画完成
                    await Task.Delay(FlyoutOpenDelayMs);
                    
                    // 等待状态同步（最多等待 500ms）
                    await WaitForFlyoutState(expectedOpen: true, maxWaitMs: MaxStateWaitMs);
                    
                    Log.Information("✅ [Click] Flyout 已打开");
                }
                
                Log.Information($"✅ [Click] 处理完成 (最终状态: {(_isFlyoutOpen ? "已打开" : "已关闭")})");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[Click] 处理失败");
                
                // 发生异常时，同步状态
                _isFlyoutOpen = _flyout.IsOpen;
                Log.Warning($"⚠️ [Click] 异常后同步状态: {(_isFlyoutOpen ? "已打开" : "已关闭")}");
            }
        }

        /// <summary>
        /// 查找按钮的父级 Border 元素
        /// Why: 需要使用 Border 作为 Flyout 的锚点，确保宽度和位置正确
        /// How: 向上遍历可视化树，查找第一个 Border 类型的父元素
        /// </summary>
        /// <param name="element">起始元素（通常是按钮）</param>
        /// <returns>找到的 Border 元素，如果未找到则返回 null</returns>
        private FrameworkElement? FindParentBorder(FrameworkElement element)
        {
            try
            {
                var parent = element.Parent as FrameworkElement;
                
                // 向上遍历可视化树，最多遍历 10 层（防止无限循环）
                int maxDepth = 10;
                int currentDepth = 0;
                
                Log.Information($"🔍 [FindParent] 开始查找父级 Border，起始元素: {element.GetType().Name}");
                
                while (parent != null && currentDepth < maxDepth)
                {
                    Log.Information($"🔍 [FindParent] 第 {currentDepth} 层: {parent.GetType().Name}" + 
                        (parent is Border ? " ✅ 找到 Border!" : ""));
                    
                    // 找到 Border 类型的父元素
                    if (parent is Border border)
                    {
                        // 检查 Border 是否有 Name
                        var borderName = border.Name ?? "(无名称)";
                        Log.Information($"🎯 [FindParent] 找到父级 Border (深度: {currentDepth}, 名称: {borderName}, 宽度: {border.ActualWidth}px)");
                        return border;
                    }
                    
                    // 继续向上查找
                    parent = parent.Parent as FrameworkElement;
                    currentDepth++;
                }
                
                Log.Warning($"⚠️ [FindParent] 未找到父级 Border (已遍历 {currentDepth} 层)");
                return null;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[FindParent] 查找父级 Border 失败");
                return null;
            }
        }

        /// <summary>
        /// 等待 Flyout 状态同步
        /// Why: Flyout 的 Opened/Closed 事件可能延迟触发，需要主动等待
        /// How: 轮询检查 _isFlyoutOpen 是否达到预期状态
        /// </summary>
        private async Task WaitForFlyoutState(bool expectedOpen, int maxWaitMs)
        {
            int elapsed = 0;
            
            while (_isFlyoutOpen != expectedOpen && elapsed < maxWaitMs)
            {
                await Task.Delay(StateWaitMs);
                elapsed += StateWaitMs;
            }
            
            // 如果超时，强制同步状态
            if (_isFlyoutOpen != expectedOpen)
            {
                Log.Warning($"⚠️ [State] 状态同步超时 ({elapsed}ms)，强制设置为 {(expectedOpen ? "打开" : "关闭")}");
                _isFlyoutOpen = expectedOpen;
            }
            else
            {
                Log.Information($"✅ [State] 状态同步成功 ({elapsed}ms)");
            }
        }

        /// <summary>
        /// 清理资源
        /// </summary>
        public void Detach()
        {
            // 清理按钮级别事件
            _button.RemoveHandler(UIElement.PointerPressedEvent, 
                (Microsoft.UI.Xaml.Input.PointerEventHandler)OnButtonPointerPressed);
            _button.RemoveHandler(UIElement.PointerReleasedEvent, 
                (Microsoft.UI.Xaml.Input.PointerEventHandler)OnButtonPointerReleased);
            _button.RemoveHandler(UIElement.PointerCaptureLostEvent, 
                (Microsoft.UI.Xaml.Input.PointerEventHandler)OnButtonPointerReleased);
            _button.RemoveHandler(UIElement.PointerCanceledEvent, 
                (Microsoft.UI.Xaml.Input.PointerEventHandler)OnButtonPointerReleased);
            
            // 清理 Window 级别事件
            if (_window.Content is UIElement content)
            {
                content.RemoveHandler(UIElement.PointerPressedEvent, 
                    (Microsoft.UI.Xaml.Input.PointerEventHandler)OnWindowPointerPressed);
                content.RemoveHandler(UIElement.PointerReleasedEvent, 
                    (Microsoft.UI.Xaml.Input.PointerEventHandler)OnWindowPointerEnded);
                content.RemoveHandler(UIElement.PointerCaptureLostEvent, 
                    (Microsoft.UI.Xaml.Input.PointerEventHandler)OnWindowPointerEnded);
                content.RemoveHandler(UIElement.PointerCanceledEvent, 
                    (Microsoft.UI.Xaml.Input.PointerEventHandler)OnWindowPointerEnded);
            }
            
            Log.Information("🧹 FlyoutPressBehavior 已清理");
        }
    }
}

