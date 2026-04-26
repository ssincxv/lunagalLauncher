using Microsoft.UI.Xaml.Input;

namespace lunagalLauncher.Views
{
    public sealed partial class GlobalContextModeDropdownContent : UserControl
    {
        private readonly MouseMappingPage _host;

        public ItemsRepeater GlobalContextModeDropdownItemsHost => GlobalContextModeDropdownItems;

        public GlobalContextModeDropdownContent(MouseMappingPage host)
        {
            _host = host;
            InitializeComponent();
        }

        private void GlobalContextModeDropdownItem_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _host.GlobalContextModeDropdownItem_Tapped(sender, e);
        }

        private void GlobalDropdownItemBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _host.GlobalDropdownItemBorder_PointerEntered(sender, e);
        }

        private void GlobalDropdownItemBorder_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _host.GlobalDropdownItemBorder_PointerExited(sender, e);
        }
    }
}
