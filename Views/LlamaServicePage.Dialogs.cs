using lunagalLauncher.Utils;

namespace lunagalLauncher.Views
{
    public sealed partial class LlamaServicePage
    {
        private Task ShowErrorDialogAsync(string title, string content) =>
            UiDialogs.ShowAlertAsync(XamlRoot, title, content);

        private Task ShowSuccessDialogAsync(string title, string content) =>
            UiDialogs.ShowAlertAsync(XamlRoot, title, content);
    }
}
