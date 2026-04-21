using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// 可视化树查找工具。
    ///
    /// <para>
    /// 历史上 <c>FindVisualChildren&lt;T&gt;</c> 的递归实现在 <c>LlamaServicePage</c>、
    /// <c>MouseMappingPage</c>、<c>MouseMappingRuleRow</c>、<c>CustomDropdown</c> 共 4 处
    /// 各自复制了一份语义完全一致（`VisualTreeHelper.GetChildrenCount` + 递归）。
    /// 这里收拢为唯一实现，调用点仍用静态调用语法 <c>FindVisualChildren&lt;T&gt;(parent)</c>，
    /// 只要 <c>using lunagalLauncher.Utils;</c> 即可命中，不需要改 call site。
    /// </para>
    ///
    /// <para>
    /// 方案 C Phase 3/4 起：部分 <see cref="ItemsControl"/> 改为 <see cref="ItemsRepeater"/> 做虚拟化。
    /// 虚拟化下 <see cref="VisualTreeHelper"/> 只能访问到"当前可见"的项容器；原代码里
    /// "全选/清空/删除勾选"等批量 CheckBox 操作会漏掉屏外项，导致功能错误。
    /// 因此本工具在遇到 <see cref="ItemsRepeater"/> 时自动走另一条路径：按 <c>ItemsSourceView</c>
    /// 索引 + <see cref="ItemsRepeater.GetOrCreateElement"/> 强制实例化所有项并递归搜集。
    /// 日常 Popup 未打开/只滚动时仍保留虚拟化收益——只有用户点击批量操作触发 FindVisualChildren
    /// 才会一次性物化所有项，属按需代价。
    /// </para>
    /// </summary>
    public static class VisualTreeExtensions
    {
        /// <summary>
        /// 递归查找 <paramref name="parent"/> 下所有类型为 <typeparamref name="T"/> 的子元素。
        /// 遇到 <see cref="ItemsRepeater"/> 会自动展开全部项（含屏外虚拟化未实例化的），保证批量遍历语义正确。
        /// </summary>
        public static List<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            var children = new List<T>();
            if (parent == null) return children;

            // 虚拟化兼容：ItemsRepeater 下的项可能还没实例化，按索引强制实例化
            if (parent is ItemsRepeater repeater)
            {
                var view = repeater.ItemsSourceView;
                if (view != null)
                {
                    for (int i = 0; i < view.Count; i++)
                    {
                        // TryGetElement 返回 null 表示该项尚未实例化；此时用 GetOrCreateElement
                        // 强制创建（这会触发 DataTemplate 的一次 instantiation）。
                        var element = repeater.TryGetElement(i) ?? repeater.GetOrCreateElement(i);
                        if (element == null) continue;

                        if (element is T directTyped)
                            children.Add(directTyped);

                        children.AddRange(FindVisualChildren<T>(element));
                    }
                }
                return children;
            }

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                    children.Add(typed);

                children.AddRange(FindVisualChildren<T>(child));
            }
            return children;
        }

        /// <summary>
        /// 深度优先查找 <paramref name="parent"/> 下第一个类型为 <typeparamref name="T"/> 的子元素。
        /// </summary>
        public static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) return null;

            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                    return typed;

                var nested = FindVisualChild<T>(child);
                if (nested != null)
                    return nested;
            }
            return null;
        }
    }
}
