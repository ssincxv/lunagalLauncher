using Serilog;
using static lunagalLauncher.Utils.VisualTreeExtensions;

namespace lunagalLauncher.Views
{
    /// <summary>
    /// <see cref="LlamaServicePage"/> 的"Host 主机地址"事件处理部分：
    /// HostItemsControl 的 Tapped / DoubleTapped / Pointer 拖拽，以及
    /// HostSelectAll / HostClearAll / HostDelete / SaveHostButton 等点击处理与 RefreshHostList。
    ///
    /// 从主文件 <c>LlamaServicePage.xaml.cs</c> 切出来做物理拆分，字段仍共享于同一 partial class。
    /// </summary>
    public sealed partial class LlamaServicePage : Page
    {
        #region Host 主机地址相关事件处理

        /// <summary>
        /// 主机地址项单击事件 - 延迟 300ms 执行，避免与双击冲突
        /// </summary>
        private void HostItem_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            try
            {
                // 立即标记事件已处理，防止冒泡
                e.Handled = true;

                // 如果刚完成拖动，忽略 Tapped 事件
                if (_justFinishedDragging)
                {
                    _justFinishedDragging = false;
                    Log.Information("🖱️ 忽略主机 Tapped（刚完成拖动）");
                    return;
                }

                if (sender is not Border border)
                {
                    return;
                }

                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || checkBox.Tag is not string hostAddress)
                {
                    return;
                }

                // 获取点击位置
                var position = e.GetPosition(border);

                // 判断是否点击在多选框区域（左侧约 50px）
                if (position.X < 50)
                {
                    // 点击多选框：立即切换状态
                    _lastCheckBoxState = checkBox.IsChecked == true; // 保存初始状态
                    checkBox.IsChecked = !checkBox.IsChecked;
                    Log.Information("🖱️ 单击主机多选框，切换状态: {Host}, 新状态: {State}", hostAddress, checkBox.IsChecked);
                }
                // 点击文本区域不做任何操作（由 DoubleTapped 处理）
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理主机地址单击事件失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 主机地址项双击事件 - 直接确认选择
        /// </summary>
        private void HostItem_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
        {
            try
            {
                // 取消拖动状态
                if (_isDragging)
                {
                    _isDragging = false;
                    _draggedCheckBoxes.Clear();
                    Log.Information("🖱️ 双击主机时取消拖动状态");
                }

                if (sender is not Border border)
                {
                    return;
                }

                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || checkBox.Tag is not string hostAddress)
                {
                    return;
                }

                // 获取点击位置
                var position = e.GetPosition(border);

                // 判断是否点击在文本区域（右侧，X >= 50）
                if (position.X >= 50)
                {
                    // 双击选项文本：选取该主机地址
                    Log.Information("🖱️ 双击主机文本，选取: {Host}", hostAddress);

                    // 清空所有复选框
                    var checkBoxes = FindVisualChildren<CheckBox>(HostItemsControl);
                    foreach (var cb in checkBoxes)
                    {
                        cb.IsChecked = false;
                    }

                    // 设置主机地址并关闭下拉框
                    HostComboBox.Text = hostAddress;
                    SaveConfiguration();
                    HostComboBox.IsOpen = false;

                    Log.Information("✅ 已选取主机地址: {Host}", hostAddress);
                }
                // 双击多选框区域（X < 50）不做任何操作，让两次 Tapped 自然执行

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "双击选择主机地址失败: {Message}", ex.Message);
            }
        }

        private void HostItem_PointerPressed(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (sender is not Border border) return;

                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null) return;

                // 开始拖动
                _isDragging = true;
                _draggedCheckBoxes.Clear();

                // 确定拖动方向：如果当前未勾选，则拖动为勾选；如果已勾选，则拖动为取消勾选
                _dragToCheck = checkBox.IsChecked != true;

                // 设置起始项的状态
                checkBox.IsChecked = _dragToCheck;
                _draggedCheckBoxes.Add(checkBox);
                Log.Information("🖱️ 开始拖动主机，方向: {Direction}, 起始项: {Host}", _dragToCheck ? "勾选" : "取消勾选", checkBox.Tag);

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理主机 PointerPressed 失败: {Message}", ex.Message);
            }
        }

        private void HostItem_PointerMoved(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isDragging) return;
                if (sender is not Border border) return;

                var checkBox = FindVisualChildren<CheckBox>(border).FirstOrDefault();
                if (checkBox == null || _draggedCheckBoxes.Contains(checkBox)) return;

                // 使用统一的拖动方向设置状态
                checkBox.IsChecked = _dragToCheck;
                _draggedCheckBoxes.Add(checkBox);
                Log.Information("🖱️ 拖动主机经过，设置项: {Host}, 状态: {State}", checkBox.Tag, _dragToCheck);

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理主机 PointerMoved 失败: {Message}", ex.Message);
            }
        }

        private void HostItem_PointerReleased(object sender, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e)
        {
            try
            {
                if (_isDragging)
                {
                    _isDragging = false;
                    _draggedCheckBoxes.Clear();
                    _justFinishedDragging = true; // 标记刚完成拖动
                    Log.Information("🖱️ 主机拖动结束");
                }

                e.Handled = true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "处理主机 PointerReleased 失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 更新主机地址文本框。
        /// </summary>
        private void UpdateHostComboBox()
        {
            var selectedHosts = new List<string>();
            var checkBoxes = FindVisualChildren<CheckBox>(HostItemsControl);
            foreach (var checkBox in checkBoxes)
            {
                if (checkBox.IsChecked == true && checkBox.Tag is string host)
                {
                    selectedHosts.Add(host);
                }
            }
            HostComboBox.Text = string.Join("; ", selectedHosts);
        }

        /// <summary>
        /// 主机地址全选按钮点击事件。
        /// </summary>
        private void HostSelectAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var checkBoxes = FindVisualChildren<CheckBox>(HostItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    checkBox.IsChecked = true;
                }
                Log.Information("主机地址已全选");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "全选主机地址失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 主机地址清空按钮点击事件。
        /// </summary>
        private void HostClearAll_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var checkBoxes = FindVisualChildren<CheckBox>(HostItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    checkBox.IsChecked = false;
                }
                Log.Information("主机地址已清空");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "清空主机地址失败: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 主机地址删除按钮点击事件。
        /// </summary>
        private async void HostDelete_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var selectedHosts = new List<string>();
                var checkBoxes = FindVisualChildren<CheckBox>(HostItemsControl);
                foreach (var checkBox in checkBoxes)
                {
                    if (checkBox.IsChecked == true && checkBox.Tag is string host)
                    {
                        selectedHosts.Add(host);
                    }
                }

                if (selectedHosts.Count == 0)
                {
                    await ShowErrorDialogAsync("提示", "请先选择要删除的主机地址");
                    return;
                }

                // 检查是否包含默认地址
                var defaultHosts = new List<string> { "127.0.0.1", "0.0.0.0", "localhost" };
                var defaultHostsSelected = selectedHosts.Where(h => defaultHosts.Contains(h)).ToList();
                var customHostsSelected = selectedHosts.Where(h => !defaultHosts.Contains(h)).ToList();

                if (defaultHostsSelected.Count > 0 && customHostsSelected.Count == 0)
                {
                    // 关闭下拉栏（特例：删除默认地址失败也要关闭）
                    HostComboBox.IsOpen = false;

                    await ShowErrorDialogAsync("提示", "默认主机地址（127.0.0.1、0.0.0.0、localhost）无法删除");
                    return;
                }

                if (customHostsSelected.Count == 0)
                {
                    HostComboBox.IsOpen = false;
                    await ShowErrorDialogAsync("提示", "没有可删除的非默认主机地址");
                    return;
                }

                if (defaultHostsSelected.Count > 0)
                {
                    Log.Information("主机历史删除：将忽略不可删的默认项 {Hosts}", string.Join(", ", defaultHostsSelected));
                }

                var config = App.AppConfig.LaunchSettings.LlamaService;
                var hostHistoryProperty = config.GetType().GetProperty("HostHistory");
                if (hostHistoryProperty == null || hostHistoryProperty.GetValue(config) is not List<string> hostHistory)
                {
                    await ShowErrorDialogAsync("提示", "主机地址历史记录不存在");
                    return;
                }

                int removedCount = 0;
                foreach (var host in customHostsSelected)
                {
                    if (hostHistory.Contains(host))
                    {
                        hostHistory.Remove(host);
                        removedCount++;
                        Log.Information("已从历史记录中删除主机地址: {Host}", host);
                    }
                }

                if (customHostsSelected.Contains(HostComboBox.Text))
                {
                    HostComboBox.Text = "127.0.0.1";
                    Log.Information("当前主机地址已被删除，恢复为默认值: 127.0.0.1");
                }

                App.ConfigManager.SaveConfig(App.AppConfig);
                RefreshHostList();
                HostComboBox.IsOpen = false;

                Log.Information("主机地址下拉：已删除 {Removed} 条自定义历史（勾选共 {Total} 项）", removedCount, selectedHosts.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "删除主机地址失败: {Message}", ex.Message);
                await ShowErrorDialogAsync("错误", $"删除失败：\n{ex.Message}");
            }
        }

        /// <summary>
        /// 保存主机地址按钮点击事件。
        /// </summary>
        private async void SaveHostButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                string hostAddress = HostComboBox.Text.Trim();
                if (string.IsNullOrWhiteSpace(hostAddress))
                {
                    await ShowErrorDialogAsync("错误", "请输入主机地址");
                    return;
                }

                var config = App.AppConfig.LaunchSettings.LlamaService;

                // 获取或创建 HostHistory 属性
                var hostHistoryProperty = config.GetType().GetProperty("HostHistory");
                List<string> hostHistory;

                if (hostHistoryProperty == null)
                {
                    // 如果属性不存在，需要在 AppConfig 中添加该属性
                    await ShowErrorDialogAsync("错误", "配置文件不支持主机地址历史记录功能");
                    return;
                }

                if (hostHistoryProperty.GetValue(config) is not List<string> existingHistory)
                {
                    // 创建新的历史记录列表
                    hostHistory = new List<string>();
                    hostHistoryProperty.SetValue(config, hostHistory);
                }
                else
                {
                    hostHistory = existingHistory;
                }

                // 检查是否已存在
                if (hostHistory.Contains(hostAddress))
                {
                    await ShowErrorDialogAsync("提示", $"主机地址 \"{hostAddress}\" 已存在于历史记录中");
                    return;
                }

                // 添加到历史记录
                hostHistory.Add(hostAddress);

                // 保存配置
                App.ConfigManager.SaveConfig(App.AppConfig);

                // 刷新主机地址列表
                RefreshHostList();

                await ShowSuccessDialogAsync("成功", $"主机地址 \"{hostAddress}\" 已保存到历史记录");
                Log.Information("主机地址已保存: {Host}", hostAddress);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存主机地址失败: {Message}", ex.Message);
                await ShowErrorDialogAsync("错误", $"保存失败：\n{ex.Message}");
            }
        }

        /// <summary>
        /// 刷新主机地址列表。
        /// </summary>
        private void RefreshHostList()
        {
            try
            {
                var config = App.AppConfig.LaunchSettings.LlamaService;

                // 默认主机地址列表
                var defaultHosts = new List<string>
                {
                    "127.0.0.1",
                    "0.0.0.0",
                    "localhost"
                };

                // 如果配置中有 HostHistory，合并到列表中
                if (config.GetType().GetProperty("HostHistory")?.GetValue(config) is List<string> hostHistory)
                {
                    foreach (var host in hostHistory)
                    {
                        if (!defaultHosts.Contains(host))
                        {
                            defaultHosts.Add(host);
                        }
                    }
                }

                // 强制刷新 UI
                HostItemsControl.ItemsSource = null;
                HostItemsControl.ItemsSource = defaultHosts;
                Log.Information("主机地址列表已刷新，共 {Count} 项", defaultHosts.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "刷新主机地址列表失败: {Message}", ex.Message);
            }
        }

        #endregion
    }
}
