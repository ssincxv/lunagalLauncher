using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// CheckBox 拖拽选择行为
    /// 支持鼠标拖拽批量选中/取消选中 CheckBox
    /// </summary>
    public class CheckBoxDragSelectBehavior
    {
        private static bool _isDragging = false;
        private static bool? _dragSelectState = null;
        private static readonly HashSet<CheckBox> _attachedCheckBoxes = new();

        /// <summary>
        /// 附加到 CheckBox 的依赖属性
        /// </summary>
        public static readonly DependencyProperty IsEnabledProperty =
            DependencyProperty.RegisterAttached(
                "IsEnabled",
                typeof(bool),
                typeof(CheckBoxDragSelectBehavior),
                new PropertyMetadata(false, OnIsEnabledChanged));

        public static bool GetIsEnabled(DependencyObject obj)
        {
            return (bool)obj.GetValue(IsEnabledProperty);
        }

        public static void SetIsEnabled(DependencyObject obj, bool value)
        {
            obj.SetValue(IsEnabledProperty, value);
        }

        private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is CheckBox checkBox)
            {
                if ((bool)e.NewValue)
                {
                    AttachBehavior(checkBox);
                }
                else
                {
                    DetachBehavior(checkBox);
                }
            }
        }

        /// <summary>
        /// 附加行为到 CheckBox
        /// </summary>
        private static void AttachBehavior(CheckBox checkBox)
        {
            if (_attachedCheckBoxes.Contains(checkBox))
                return;

            _attachedCheckBoxes.Add(checkBox);

            checkBox.PointerPressed += OnPointerPressed;
            checkBox.PointerEntered += OnPointerEntered;
            checkBox.PointerReleased += OnPointerReleased;
            checkBox.PointerCaptureLost += OnPointerCaptureLost;
            checkBox.Unloaded += OnCheckBoxUnloaded;
        }

        /// <summary>
        /// 分离行为
        /// </summary>
        private static void DetachBehavior(CheckBox checkBox)
        {
            if (!_attachedCheckBoxes.Contains(checkBox))
                return;

            _attachedCheckBoxes.Remove(checkBox);

            checkBox.PointerPressed -= OnPointerPressed;
            checkBox.PointerEntered -= OnPointerEntered;
            checkBox.PointerReleased -= OnPointerReleased;
            checkBox.PointerCaptureLost -= OnPointerCaptureLost;
            checkBox.Unloaded -= OnCheckBoxUnloaded;
        }

        private static void OnCheckBoxUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox)
            {
                DetachBehavior(checkBox);
            }
        }

        private static void OnPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is CheckBox checkBox)
            {
                var pointer = e.GetCurrentPoint(checkBox);
                if (pointer.Properties.IsLeftButtonPressed)
                {
                    _isDragging = true;
                    // 记录初始状态（将要切换到的状态）
                    // 如果当前是选中，则拖拽时取消选中；如果未选中，则拖拽时选中
                    _dragSelectState = checkBox.IsChecked != true;
                    checkBox.IsChecked = _dragSelectState;
                    checkBox.CapturePointer(e.Pointer);
                }
            }
        }

        private static void OnPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (_isDragging && sender is CheckBox checkBox)
            {
                var pointer = e.GetCurrentPoint(checkBox);
                if (pointer.Properties.IsLeftButtonPressed && _dragSelectState.HasValue)
                {
                    // 反选逻辑：如果目标已经是期望状态，则反选
                    if (checkBox.IsChecked == _dragSelectState)
                    {
                        // 已经是期望状态，反选它
                        checkBox.IsChecked = !_dragSelectState;
                    }
                    else
                    {
                        // 不是期望状态，设置为期望状态
                        checkBox.IsChecked = _dragSelectState;
                    }
                }
            }
        }

        private static void OnPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is CheckBox checkBox)
            {
                _isDragging = false;
                _dragSelectState = null;
                checkBox.ReleasePointerCapture(e.Pointer);
            }
        }

        private static void OnPointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isDragging = false;
            _dragSelectState = null;
        }

        /// <summary>
        /// 重置所有状态（用于清理）
        /// </summary>
        public static void ResetAll()
        {
            _isDragging = false;
            _dragSelectState = null;
        }

        /// <summary>
        /// 附加行为到 ItemsControl（为所有 CheckBox 启用拖拽选择）
        /// </summary>
        /// <param name="itemsControl">包含 CheckBox 的 ItemsControl</param>
        public static void Attach(Microsoft.UI.Xaml.Controls.ItemsControl itemsControl)
        {
            if (itemsControl == null)
                return;

            // 监听 Loaded 事件，确保所有子元素已加载
            itemsControl.Loaded += (s, e) =>
            {
                AttachToAllCheckBoxes(itemsControl);
            };

            // 如果已经加载，立即附加
            if (itemsControl.IsLoaded)
            {
                AttachToAllCheckBoxes(itemsControl);
            }
        }

        /// <summary>
        /// 附加到 ItemsControl 中的所有 CheckBox
        /// </summary>
        private static void AttachToAllCheckBoxes(Microsoft.UI.Xaml.Controls.ItemsControl itemsControl)
        {
            foreach (var item in itemsControl.Items)
            {
                var container = itemsControl.ContainerFromItem(item);
                if (container != null)
                {
                    var checkBox = FindCheckBoxInVisualTree(container);
                    if (checkBox != null)
                    {
                        SetIsEnabled(checkBox, true);
                    }
                }
            }
        }

        /// <summary>
        /// 在可视化树中查找 CheckBox
        /// </summary>
        private static Microsoft.UI.Xaml.Controls.CheckBox? FindCheckBoxInVisualTree(DependencyObject parent)
        {
            if (parent is Microsoft.UI.Xaml.Controls.CheckBox checkBox)
                return checkBox;

            int childCount = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < childCount; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                var result = FindCheckBoxInVisualTree(child);
                if (result != null)
                    return result;
            }

            return null;
        }
    }
}

