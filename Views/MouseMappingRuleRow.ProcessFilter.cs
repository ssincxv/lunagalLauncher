using lunagalLauncher.Strings;
using lunagalLauncher.Utils;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Serilog;
using Windows.UI;
using static lunagalLauncher.Utils.VisualTreeExtensions;
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
            if (ProcessItemsControl != null)
            {
                var selected = new List<string>();
                foreach (var cb in FindVisualChildren<CheckBox>(ProcessItemsControl))
                {
                    if (cb.IsChecked == true && cb.Tag is string path)
                        selected.Add(path);
                }
                if (selected.Count == 0)
                    ProcessDropdown.Text = string.Empty;
                else
                    ProcessDropdown.Text = $"已选 {selected.Count} 项";
            }
            else if (_processItems.Count > 0)
                ProcessDropdown.Text = $"已选 {_processItems.Count} 项";
            else
                ProcessDropdown.Text = string.Empty;
        }

        internal void ProcessItemBorder_Tapped(object sender, TappedRoutedEventArgs e)
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

        internal void ProcessItemBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
                b.Background = new SolidColorBrush(Color.FromArgb(255, 229, 229, 229));
        }

        internal void ProcessItemBorder_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border b)
                b.Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0));
        }

        internal void ProcessSelectAll_Click(object sender, RoutedEventArgs e)
        {
            if (ProcessItemsControl == null) return;
            foreach (var cb in FindVisualChildren<CheckBox>(ProcessItemsControl))
                cb.IsChecked = true;
            UpdateProcessDropdownDisplayText();
            ScrollProcessFilterListToTop();
        }

        internal void ProcessClearChecks_Click(object sender, RoutedEventArgs e)
        {
            if (ProcessItemsControl == null) return;
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

        internal async void ProcessDeleteSelected_Click(object sender, RoutedEventArgs e)
        {
            var remove = new List<string>();
            if (ProcessItemsControl == null) return;
            foreach (var cb in FindVisualChildren<CheckBox>(ProcessItemsControl))
            {
                if (cb.IsChecked == true && cb.Tag is string path)
                    remove.Add(path);
            }
            if (remove.Count == 0)
            {
                await UiDialogs.ShowAlertAsync(XamlRoot, DialogMessages.PromptTitle, DialogMessages.SelectFilterItemsToDelete);
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
                await CustomDropdownModalPrep.CloseIfOpenAndWaitForAnimationAsync(ProcessDropdown);

                if (!App.TryGetMainWindowHandle(out _))
                    Log.Warning("浏览 exe：主窗口为空，仍将使用 file-picker 子进程");

                var initDir = Win32FileDialog.TryGetInitialDirectoryFromExistingPaths(_processItems);
                string? path = await Win32FileDialog.ShowOpenFileDialogForMainWindowAsync(
                    "可执行文件|*.exe", "选择要加入列表的程序", initDir);

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
            if (ProcessItemsControl != null)
            {
                ProcessItemsControl.ItemsSource = null;
                ProcessItemsControl.ItemsSource = _processItems;
            }
            _suppressDirty = false;
            Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() =>
            {
                if (ProcessItemsControl == null)
                {
                    UpdateProcessDropdownDisplayText();
                    return;
                }
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
