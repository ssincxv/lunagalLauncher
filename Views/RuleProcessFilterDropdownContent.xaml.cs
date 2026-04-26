using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace lunagalLauncher.Views
{
    public sealed partial class RuleProcessFilterDropdownContent : UserControl
    {
        private readonly MouseMappingRuleRow _host;

        public ItemsControl ProcessItemsControlHost => RuleProcessItemsControl;

        public ScrollViewer ProcessListScrollViewerHost => RuleProcessListScrollViewer;

        public RuleProcessFilterDropdownContent(MouseMappingRuleRow host)
        {
            _host = host;
            InitializeComponent();
        }

        private void RuleProcessItemBorder_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _host.ProcessItemBorder_Tapped(sender, e);
        }

        private void RuleProcessItemBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _host.ProcessItemBorder_PointerEntered(sender, e);
        }

        private void RuleProcessItemBorder_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _host.ProcessItemBorder_PointerExited(sender, e);
        }

        private void RuleProcessSelectAll_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            _host.ProcessSelectAll_Click(sender, e);
        }

        private void RuleProcessClearChecks_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            _host.ProcessClearChecks_Click(sender, e);
        }

        private void RuleProcessDeleteSelected_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            _host.ProcessDeleteSelected_Click(sender, e);
        }
    }
}
