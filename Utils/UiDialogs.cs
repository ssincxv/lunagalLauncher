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
    }
}
