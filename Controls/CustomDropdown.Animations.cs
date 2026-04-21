using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Serilog;

namespace lunagalLauncher.Controls
{
    /// <summary>
    /// <see cref="CustomDropdown"/> 的动画部分：
    /// 按钮按压、输入框底部阴影、Popup 展开/收起（InsetClip）与对应阴影的 ScaleY 动画，
    /// 以及根据距离和速率计算动画时长。
    ///
    /// <para>
    /// 从主文件 <c>CustomDropdown.cs</c> 切出来只做物理拆分，字段仍共享于同一 partial class。
    /// </para>
    /// </summary>
    public sealed partial class CustomDropdown : Control
    {
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
                var storyboard = new Storyboard();
                var animation = new DoubleAnimation();

                if (isPressed)
                {
                    // 按下：向下移动 1.5px
                    animation.Duration = new Duration(TimeSpan.FromMilliseconds(100));
                    animation.To = 1.5;
                    animation.EasingFunction = new QuadraticEase();
                    Log.Information("🔽 按钮按下动画: 向下 1.5px");
                }
                else
                {
                    // 释放：回到原位（使用关键帧动画实现弹跳效果）
                    var keyFrameAnimation = new DoubleAnimationUsingKeyFrames();
                    keyFrameAnimation.Duration = new Duration(TimeSpan.FromMilliseconds(200));

                    var keyFrame1 = new EasingDoubleKeyFrame
                    {
                        KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(100)),
                        Value = -0.8
                    };

                    var keyFrame2 = new EasingDoubleKeyFrame
                    {
                        KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200)),
                        Value = 0
                    };

                    keyFrameAnimation.KeyFrames.Add(keyFrame1);
                    keyFrameAnimation.KeyFrames.Add(keyFrame2);

                    Storyboard.SetTarget(keyFrameAnimation, transform);
                    Storyboard.SetTargetProperty(keyFrameAnimation, "TranslateY");

                    storyboard.Children.Add(keyFrameAnimation);
                    storyboard.Begin();

                    Log.Information("🔼 按钮释放动画: 向上 0.8px 然后复位");
                    return;
                }

                // 按下动画
                Storyboard.SetTarget(animation, transform);
                Storyboard.SetTargetProperty(animation, "TranslateY");

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
        /// - 按下按钮时：阴影从无到有（淡入效果）
        /// - 松开按钮时：阴影从有到无（淡出效果）
        /// 实现：ShadowBorder.Opacity 控制整体可见性 + DropShadow.BlurRadius 控制模糊程度。
        /// </summary>
        private void AnimateBorderShadow(bool fadeIn)
        {
            if (_shadowBorder == null)
            {
                Log.Warning("💡 _shadowBorder 为 null，无法播放阴影动画");
                return;
            }

            // 懒加载：首次按压时才创建 Compositor + DropShadow + SpriteVisual（方案 C Phase 1a）
            InitializeCompositionVisual();

            if (_dropShadow == null)
            {
                Log.Warning("💡 _dropShadow 为 null，无法播放阴影动画");
                return;
            }

            try
            {
                var compositor = _dropShadow.Compositor;
                var borderVisual = ElementCompositionPreview.GetElementVisual(_shadowBorder);

                var borderOpacityAnimation = compositor.CreateScalarKeyFrameAnimation();
                borderOpacityAnimation.Duration = TimeSpan.FromMilliseconds(300);

                var blurAnimation = compositor.CreateScalarKeyFrameAnimation();
                blurAnimation.Duration = TimeSpan.FromMilliseconds(300);

                if (fadeIn)
                {
                    borderOpacityAnimation.InsertKeyFrame(0.0f, 0f);
                    borderOpacityAnimation.InsertKeyFrame(1.0f, 0.15f);

                    blurAnimation.InsertKeyFrame(0.0f, 0f);
                    blurAnimation.InsertKeyFrame(1.0f, 12f);

                    Log.Information("💡💡💡 阴影淡入动画：Opacity 0→0.15, BlurRadius 0→12");
                }
                else
                {
                    borderOpacityAnimation.InsertKeyFrame(0.0f, 0.15f);
                    borderOpacityAnimation.InsertKeyFrame(1.0f, 0f);

                    blurAnimation.InsertKeyFrame(0.0f, 12f);
                    blurAnimation.InsertKeyFrame(1.0f, 0f);

                    Log.Information("💡💡💡 阴影淡出动画：Opacity 0.15→0, BlurRadius 12→0");
                }

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
        /// - 内容：InsetClip.BottomInset 动画
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

                _contentPresenter.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                var contentHeight = _contentPresenter.DesiredSize.Height;
                var targetHeight = Math.Min(contentHeight, MaxDropDownHeight);

                var duration = CalculateAnimationDuration(targetHeight);

                Log.Information("🎬 内容高度: {ContentHeight}, 目标高度: {TargetHeight}, 动画时长: {Duration}ms",
                    contentHeight, targetHeight, duration);

                _popupBorder.Height = double.NaN;

                var borderVisual = ElementCompositionPreview.GetElementVisual(_popupBorder);
                var compositor = borderVisual.Compositor;

                borderVisual.Opacity = 1f;

                var clip = compositor.CreateInsetClip();
                clip.TopInset = 0;
                clip.LeftInset = 0;
                clip.RightInset = 0;
                clip.BottomInset = (float)targetHeight;  // 初始状态：完全裁剪

                borderVisual.Clip = clip;

                var linearEasing = compositor.CreateLinearEasingFunction();

                var clipAnimation = compositor.CreateScalarKeyFrameAnimation();
                clipAnimation.Duration = TimeSpan.FromMilliseconds(duration);
                clipAnimation.InsertKeyFrame(1.0f, 0f, linearEasing);
                clipAnimation.IterationBehavior = AnimationIterationBehavior.Count;
                clipAnimation.IterationCount = 1;

                var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

                batch.Completed += (s, e) =>
                {
                    if (IsOpen)
                    {
                        borderVisual.Clip = null;
                        _isAnimating = false;
                        Log.Information("🎬 展开动画完成（InsetClip 方式），移除裁剪");
                    }
                };

                clip.StartAnimation("BottomInset", clipAnimation);
                batch.End();

                Log.Information("🎬 内容展开动画已启动（InsetClip，匀速，{Duration}ms）", duration);

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
        /// - 内容：InsetClip.BottomInset 动画
        /// - 阴影：ScaleY 动画
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

                var currentHeight = _popupBorder.ActualHeight;
                if (currentHeight <= 0)
                {
                    _contentPresenter.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    currentHeight = _contentPresenter.DesiredSize.Height;
                }

                var duration = CalculateAnimationDuration(currentHeight);

                Log.Information("🎬 当前高度: {CurrentHeight}, 动画时长: {Duration}ms", currentHeight, duration);

                var borderVisual = ElementCompositionPreview.GetElementVisual(_popupBorder);
                var compositor = borderVisual.Compositor;

                var clip = compositor.CreateInsetClip();
                clip.TopInset = 0;
                clip.LeftInset = 0;
                clip.RightInset = 0;
                clip.BottomInset = 0;  // 初始状态：完全显示

                borderVisual.Clip = clip;

                var linearEasing = compositor.CreateLinearEasingFunction();

                var clipAnimation = compositor.CreateScalarKeyFrameAnimation();
                clipAnimation.Duration = TimeSpan.FromMilliseconds(duration);
                clipAnimation.InsertKeyFrame(1.0f, (float)currentHeight, linearEasing);
                clipAnimation.IterationBehavior = AnimationIterationBehavior.Count;
                clipAnimation.IterationCount = 1;

                var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);

                batch.Completed += (s, e) =>
                {
                    if (!IsOpen)
                    {
                        if (_popup != null)
                        {
                            _popup.IsOpen = false;
                        }
                        borderVisual.Clip = null;
                        _isAnimating = false;
                        Log.Information("🎬 收起动画完成（InsetClip 方式），Popup 已关闭");
                    }
                };

                clip.StartAnimation("BottomInset", clipAnimation);
                batch.End();

                Log.Information("🎬 内容收起动画已启动（InsetClip，匀速，{Duration}ms）", duration);

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
                var storyboard = new Storyboard();
                var scaleAnimation = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(duration))
                    // 不设置 EasingFunction，默认为线性（匀速）
                };

                Storyboard.SetTarget(scaleAnimation, _shadowScale);
                Storyboard.SetTargetProperty(scaleAnimation, "ScaleY");

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
                var storyboard = new Storyboard();
                var scaleAnimation = new DoubleAnimation
                {
                    From = 1,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(duration))
                    // 不设置 EasingFunction，默认为线性（匀速）
                };

                Storyboard.SetTarget(scaleAnimation, _shadowScale);
                Storyboard.SetTargetProperty(scaleAnimation, "ScaleY");

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
        /// 根据高度和速率计算动画时长。
        /// </summary>
        /// <param name="height">动画距离（像素）。</param>
        /// <returns>动画时长（毫秒），受 100-500ms 边界限制。</returns>
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
    }
}
