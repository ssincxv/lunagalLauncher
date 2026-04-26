using lunagalLauncher.Strings;

namespace lunagalLauncher.Utils
{
    /// <summary>
    /// 统一单按钮 <see cref="ContentDialog"/>（确定/关闭），各页传入已选好的 <see cref="XamlRoot"/> 与 <see cref="ContentDialogPlacement"/>。
    /// </summary>
    public static class UiDialogs
    {
        public static async Task ShowAlertAsync(
            XamlRoot xamlRoot,
            string title,
            object content,
            string closeButtonText,
            ContentDialogPlacement placement = ContentDialogPlacement.InPlace)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = content,
                CloseButtonText = closeButtonText,
                XamlRoot = xamlRoot
            };
            await dialog.ShowAsync(placement);
        }

        public static Task ShowAlertAsync(
            XamlRoot xamlRoot,
            string title,
            object content,
            ContentDialogPlacement placement = ContentDialogPlacement.InPlace) =>
            ShowAlertAsync(xamlRoot, title, content, DialogMessages.Ok, placement);

        /// <summary>
        /// 在 file-picker 子进程异常结束或无法启动时向用户说明原因；用户取消或成功选文件时不提示。
        /// </summary>
        public static Task ShowAlertForFilePickerResultAsync(
            XamlRoot xamlRoot,
            OpenFilePickerResult result,
            ContentDialogPlacement placement = ContentDialogPlacement.Popup)
        {
            if (result.Completion is OpenFilePickerCompletion.Success or OpenFilePickerCompletion.Cancelled)
                return Task.CompletedTask;

            string title = result.Completion == OpenFilePickerCompletion.Unavailable
                ? "无法打开浏览窗口"
                : "浏览窗口已关闭";

            string message = result.Completion == OpenFilePickerCompletion.Unavailable
                ? "无法启动文件浏览子进程，请确认程序完整安装后重试。"
                : "窗口突然消失常见于：在文件列表中对某项使用右键时，不兼容的第三方资源管理器或 Shell 右键菜单扩展在独立子进程内崩溃，主程序通常仍可继续使用。可尝试重新浏览、尽量避免对文件项使用右键，或使用 ShellExView 等工具排查右键菜单扩展。杀毒软件「信任整个文件夹」一般无法阻止此类扩展被加载。";

            return ShowAlertAsync(xamlRoot, title, message, placement);
        }
    }
}
