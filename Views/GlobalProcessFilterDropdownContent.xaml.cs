using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace lunagalLauncher.Views
{
    public sealed partial class GlobalProcessFilterDropdownContent : UserControl
    {
        private readonly MouseMappingPage _host;

        public ItemsControl GlobalProcessItemsControlHost => GlobalProcessItemsControl;

        public ScrollViewer GlobalProcessListScrollViewerHost => GlobalProcessListScrollViewer;

        public GlobalProcessFilterDropdownContent(MouseMappingPage host)
        {
            _host = host;
            InitializeComponent();
        }

        private void GlobalProcessItemBorder_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _host.GlobalProcessItemBorder_Tapped(sender, e);
        }

        private void GlobalProcessItemBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _host.GlobalProcessItemBorder_PointerEntered(sender, e);
        }

        private void GlobalProcessItemBorder_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _host.GlobalProcessItemBorder_PointerExited(sender, e);
        }

        private void GlobalProcessSelectAll_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            _host.GlobalProcessSelectAll_Click(sender, e);
        }

        private void GlobalProcessClearChecks_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            _host.GlobalProcessClearChecks_Click(sender, e);
        }

        private void GlobalProcessDeleteSelected_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            _host.GlobalProcessDeleteSelected_Click(sender, e);
        }
    }
}
