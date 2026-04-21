using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Serilog;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// 按压动画工具（上下方向 TranslateY 按下 + 回弹）。
    ///
    /// <para>
    /// 历史上 <c>AnimatePortButton</c>（<c>LlamaServicePage</c>）与
    /// <c>AnimateSpinnerButton</c>（<c>MouseMappingRuleRow</c>）的核心 Storyboard
    /// 结构（按下 100ms 线性到 ±1.5px；释放 200ms 关键帧反弹 ±0.8 再回 0）几乎一字不差。
    /// 这里抽成单点实现 <see cref="PlayTranslateYPress"/>，两处原方法保留薄壳
    /// （`AnimatePortButton` 还顺带调阴影动画；壳不动则调用点 0 改动）。
    /// </para>
    ///
    /// <para>
    /// 对 <c>CustomDropdown.AnimateButtonPress</c>（无 button 参数、操作 `_buttonTransform`
    /// 且联动 DropShadow）**不合并**——签名和协同对象不同，强行合并回归面大于收益。
    /// </para>
    /// </summary>
    public static class PressAnimationHelper
    {
        private const double PressDistance = 1.5;
        private const double ReleaseBounce = 0.8;
        private const int PressDurationMs = 100;
        private const int ReleaseDurationMs = 200;

        /// <summary>
        /// 按下/释放时对按钮的 <see cref="CompositeTransform.TranslateY"/> 做短按动画。
        /// 按钮必须预设 <c>RenderTransform = CompositeTransform</c>，否则静默跳过。
        /// </summary>
        /// <param name="button">要动画的按钮。</param>
        /// <param name="isPressed">true = 按下；false = 释放（含回弹）。</param>
        /// <param name="isUp">
        /// true = 这是"上"方向按钮（按下向上 -1.5px，释放反弹 +0.8px 再 0）；
        /// false = 这是"下"方向按钮（按下向下 +1.5px，释放反弹 -0.8px 再 0）。
        /// </param>
        public static void PlayTranslateYPress(ButtonBase button, bool isPressed, bool isUp)
        {
            if (button == null) return;
            if (button.RenderTransform is not CompositeTransform transform) return;

            try
            {
                var storyboard = new Storyboard();

                if (isPressed)
                {
                    var animation = new DoubleAnimation
                    {
                        Duration = new Duration(TimeSpan.FromMilliseconds(PressDurationMs)),
                        To = isUp ? -PressDistance : PressDistance,
                        EasingFunction = new QuadraticEase()
                    };
                    Storyboard.SetTarget(animation, transform);
                    Storyboard.SetTargetProperty(animation, "TranslateY");
                    storyboard.Children.Add(animation);
                }
                else
                {
                    var kfAnim = new DoubleAnimationUsingKeyFrames
                    {
                        Duration = new Duration(TimeSpan.FromMilliseconds(ReleaseDurationMs))
                    };
                    kfAnim.KeyFrames.Add(new EasingDoubleKeyFrame
                    {
                        KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(PressDurationMs)),
                        Value = isUp ? ReleaseBounce : -ReleaseBounce
                    });
                    kfAnim.KeyFrames.Add(new EasingDoubleKeyFrame
                    {
                        KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ReleaseDurationMs)),
                        Value = 0
                    });
                    Storyboard.SetTarget(kfAnim, transform);
                    Storyboard.SetTargetProperty(kfAnim, "TranslateY");
                    storyboard.Children.Add(kfAnim);
                }

                storyboard.Begin();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "PressAnimationHelper: 按钮按压动画失败");
            }
        }
    }
}
