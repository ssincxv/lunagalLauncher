using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Serilog;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// 动画辅助类
    /// Animation Helper - provides reusable animation methods for UI elements
    /// </summary>
    public static class AnimationHelper
    {
        /// <summary>
        /// 播放按压动画（向下移动）
        /// Plays press animation (move down)
        /// </summary>
        /// <param name="translateTransform">要动画的 TranslateTransform</param>
        /// <param name="offsetY">Y 轴偏移量（像素）</param>
        /// <param name="durationMs">动画时长（毫秒）</param>
        public static void PlayPressAnimation(TranslateTransform translateTransform, double offsetY, int durationMs)
        {
            try
            {
                if (translateTransform == null)
                {
                    Log.Warning("⚠️ PlayPressAnimation: translateTransform 为 null");
                    return;
                }

                var animation = new DoubleAnimation
                {
                    To = offsetY,
                    Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                var storyboard = new Storyboard();
                storyboard.Children.Add(animation);
                Storyboard.SetTarget(animation, translateTransform);
                Storyboard.SetTargetProperty(animation, "Y");
                storyboard.Begin();

                Log.Debug("✅ 播放按压动画: offsetY={OffsetY}, duration={Duration}ms", offsetY, durationMs);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "播放按压动画失败");
            }
        }

        /// <summary>
        /// 播放释放动画（回弹）
        /// Plays release animation (bounce back)
        /// </summary>
        /// <param name="translateTransform">要动画的 TranslateTransform</param>
        /// <param name="offsetY">Y 轴偏移量（像素，通常为负值以产生回弹效果）</param>
        /// <param name="durationMs">动画时长（毫秒）</param>
        public static void PlayReleaseAnimation(TranslateTransform translateTransform, double offsetY, int durationMs)
        {
            try
            {
                if (translateTransform == null)
                {
                    Log.Warning("⚠️ PlayReleaseAnimation: translateTransform 为 null");
                    return;
                }

                var animation = new DoubleAnimation
                {
                    To = offsetY,
                    Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                    EasingFunction = new ElasticEase 
                    { 
                        EasingMode = EasingMode.EaseOut,
                        Oscillations = 1,
                        Springiness = 3
                    }
                };

                var storyboard = new Storyboard();
                storyboard.Children.Add(animation);
                Storyboard.SetTarget(animation, translateTransform);
                Storyboard.SetTargetProperty(animation, "Y");
                storyboard.Begin();

                Log.Debug("✅ 播放释放动画: offsetY={OffsetY}, duration={Duration}ms", offsetY, durationMs);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "播放释放动画失败");
            }
        }

        /// <summary>
        /// 播放淡入动画
        /// Plays fade in animation
        /// </summary>
        /// <param name="element">要动画的元素</param>
        /// <param name="durationMs">动画时长（毫秒）</param>
        public static void PlayFadeInAnimation(UIElement element, int durationMs = 300)
        {
            try
            {
                if (element == null)
                {
                    Log.Warning("⚠️ PlayFadeInAnimation: element 为 null");
                    return;
                }

                var animation = new DoubleAnimation
                {
                    From = 0,
                    To = 1,
                    Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
                };

                var storyboard = new Storyboard();
                storyboard.Children.Add(animation);
                Storyboard.SetTarget(animation, element);
                Storyboard.SetTargetProperty(animation, "Opacity");
                storyboard.Begin();

                Log.Debug("✅ 播放淡入动画: duration={Duration}ms", durationMs);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "播放淡入动画失败");
            }
        }

        /// <summary>
        /// 播放淡出动画
        /// Plays fade out animation
        /// </summary>
        /// <param name="element">要动画的元素</param>
        /// <param name="durationMs">动画时长（毫秒）</param>
        public static void PlayFadeOutAnimation(UIElement element, int durationMs = 300)
        {
            try
            {
                if (element == null)
                {
                    Log.Warning("⚠️ PlayFadeOutAnimation: element 为 null");
                    return;
                }

                var animation = new DoubleAnimation
                {
                    From = 1,
                    To = 0,
                    Duration = new Duration(TimeSpan.FromMilliseconds(durationMs)),
                    EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn }
                };

                var storyboard = new Storyboard();
                storyboard.Children.Add(animation);
                Storyboard.SetTarget(animation, element);
                Storyboard.SetTargetProperty(animation, "Opacity");
                storyboard.Begin();

                Log.Debug("✅ 播放淡出动画: duration={Duration}ms", durationMs);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "播放淡出动画失败");
            }
        }
    }
}

