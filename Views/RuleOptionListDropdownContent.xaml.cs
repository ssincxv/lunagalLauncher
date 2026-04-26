using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace lunagalLauncher.Views
{
    public enum RuleOptionDropdownKind
    {
        Trigger,
        Behavior,
        Action,
        SimMouse,
        ContextMode
    }

    public sealed partial class RuleOptionListDropdownContent : UserControl
    {
        private readonly MouseMappingRuleRow _host;
        private readonly RuleOptionDropdownKind _kind;

        public ItemsRepeater ItemsRepeaterHost => OptionItemsRepeater;

        public RuleOptionListDropdownContent(MouseMappingRuleRow host, RuleOptionDropdownKind kind, double maxScrollHeight)
        {
            _host = host;
            _kind = kind;
            InitializeComponent();
            ScrollRoot.MaxHeight = maxScrollHeight;
        }

        private void OptionItemBorder_Tapped(object sender, TappedRoutedEventArgs e)
        {
            switch (_kind)
            {
                case RuleOptionDropdownKind.Trigger:
                    _host.TriggerDropdownItem_Tapped(sender, e);
                    break;
                case RuleOptionDropdownKind.Behavior:
                    _host.BehaviorDropdownItem_Tapped(sender, e);
                    break;
                case RuleOptionDropdownKind.Action:
                    _host.ActionDropdownItem_Tapped(sender, e);
                    break;
                case RuleOptionDropdownKind.SimMouse:
                    _host.SimMouseDropdownItem_Tapped(sender, e);
                    break;
                case RuleOptionDropdownKind.ContextMode:
                    _host.ContextModeDropdownItem_Tapped(sender, e);
                    break;
            }
        }

        private void OptionItemBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            _host.DropdownItemBorder_PointerEntered(sender, e);
        }

        private void OptionItemBorder_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            _host.DropdownItemBorder_PointerExited(sender, e);
        }
    }
}
