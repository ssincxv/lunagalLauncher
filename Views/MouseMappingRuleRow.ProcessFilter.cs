using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using lunagalLauncher.Utils;
using static lunagalLauncher.Utils.VisualTreeExtensions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.UI;
using WinRT.Interop;

namespace lunagalLauncher.Views
{
    /// <summary>
    /// <see cref="MouseMappingRuleRow"/> 的"过滤名单（进程路径多选 UI）"部分。
    /// 只从主文件 <c>MouseMappingRuleRow.xaml.cs</c> 切出来物理拆分，字段仍共享于同一 partial class。
    /// </summary>
    public sealed partial class MouseMappingRuleRow : UserControl
    {
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
            ScrollProcessFilterListToTop();
        }

        private void ProcessClearChecks_Click(object sender, RoutedEventArgs e)
        {
            foreach (var cb in FindVisualChildren<CheckBox>(ProcessItemsControl))
                cb.IsChecked = false;
            UpdateProcessDropdownDisplayText();
            ScrollProcessFilterListToTop();
        }

        private void ScrollProcessFilterListToTop()
        {
            try
            {
                ProcessListScrollViewer?.ChangeView(null, 0, null);
                DispatcherQueue.GetForCurrentThread()?.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    ProcessListScrollViewer?.ChangeView(null, 0, null));
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "过滤名单滚动回顶失败（忽略）");
            }
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
                _ = await new ContentDialog
                {
                    Title = "提示",
                    Content = "请先勾选要删除的过滤名单项",
                    CloseButtonText = "确定",
                    XamlRoot = XamlRoot
                }.ShowAsync();
                return;
            }

            Log.Information("规则行进程过滤：立即删除 {Count} 项（无确认框）", remove.Count);
            foreach (var p in remove)
                _processItems.Remove(p);
            SyncFromUi();
            RefreshProcessList();
            ProcessDropdown.IsOpen = false;
        }

        private async void BrowseExe_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool dropdownWasOpen = ProcessDropdown.IsOpen;
                ProcessDropdown.IsOpen = false;
                await Task.Yield();
                await Task.Delay(dropdownWasOpen ? 550 : 0);

                var app = (App)App.Current;
                if (app?.window == null)
                {
                    Log.Warning("浏览 exe：主窗口为空");
                    return;
                }

                var hwnd = WindowNative.GetWindowHandle(app.window);
                var initDir = Win32FileDialog.TryGetInitialDirectoryFromExistingPaths(_processItems);
                string? path = await Win32FileDialog.ShowOpenFileDialogAsync(hwnd, "可执行文件|*.exe", "选择要加入列表的程序", initDir);

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
                ScrollProcessFilterListToTop();
            });
        }
    }
}
