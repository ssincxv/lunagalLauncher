using lunagalLauncher.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml.Media;
using Serilog;

namespace lunagalLauncher.Views
{
    public sealed partial class LlamaServicePage
    {
        /// <summary>
        /// 服务状态改变事件处理
        /// Service status changed event handler
        /// </summary>
        private void OnServiceStatusChanged(object? sender, LlamaServiceStatus status)
        {
            if (_isUnloaded)
                return;

            try
            {
                DispatcherQueue?.TryEnqueue(() =>
                {
                    if (!_isUnloaded)
                        UpdateUIStatus(status);
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "更新服务状态时发生错误: {Message}", ex.Message);
            }
        }

        /// <summary>
        /// 更新 UI 状态
        /// Updates UI status based on service status
        /// </summary>
        private void UpdateUIStatus(LlamaServiceStatus status)
        {
            string statusText = "";

            switch (status)
            {
                case LlamaServiceStatus.NotStarted:
                    statusText = "未启动";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Gray);
                    StartButton.IsEnabled = true;
                    StopButton.IsEnabled = false;
                    RestartButton.IsEnabled = false;
                    break;

                case LlamaServiceStatus.Starting:
                    statusText = "正在启动";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Orange);
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled = false;
                    RestartButton.IsEnabled = false;
                    break;

                case LlamaServiceStatus.Running:
                    statusText = "运行中";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Green);
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled = true;
                    RestartButton.IsEnabled = true;
                    break;

                case LlamaServiceStatus.Stopping:
                    statusText = "正在停止";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Orange);
                    StartButton.IsEnabled = false;
                    StopButton.IsEnabled = false;
                    RestartButton.IsEnabled = false;
                    break;

                case LlamaServiceStatus.Stopped:
                    statusText = "已停止";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Gray);
                    StartButton.IsEnabled = true;
                    StopButton.IsEnabled = false;
                    RestartButton.IsEnabled = false;
                    break;

                case LlamaServiceStatus.Error:
                    statusText = "错误";
                    StatusText.Text = statusText;
                    StatusIndicator.Fill = new SolidColorBrush(Colors.Red);
                    StartButton.IsEnabled = true;
                    StopButton.IsEnabled = false;
                    RestartButton.IsEnabled = false;
                    break;
            }

            Log.Debug("UI 状态已更新: {Status}", status);
        }
    }
}
