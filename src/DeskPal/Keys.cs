using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.System;

namespace DeskPal;

// Shared keyboard handling for the small popup windows: Esc closes or cancels, Enter runs the default action.
internal static class Keys
{
    // Esc and Enter on a window's root. Enter is left alone when the focused control already uses it (buttons,
    // text boxes, lists, open drop-downs), so a focused button still does its own thing.
    public static void Wire(FrameworkElement root, Action? escape, Action? enter)
    {
        root.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape && escape is not null)
            {
                if (e.OriginalSource is ComboBox { IsDropDownOpen: true }) return;
                e.Handled = true;
                TickGuard.Run("key-escape", escape);
            }
            else if (e.Key == VirtualKey.Enter && enter is not null)
            {
                if (e.OriginalSource is ButtonBase or TextBox or PasswordBox or RichEditBox or ComboBox or ListViewBase or ToggleSwitch or Slider) return;
                e.Handled = true;
                TickGuard.Run("key-enter", enter);
            }
        };
    }

    // Presses a button the way a click would, so its normal handler runs. Does nothing when it is hidden or disabled.
    public static void Invoke(Button button)
    {
        if (button.Visibility != Visibility.Visible || !button.IsEnabled) return;
        if (FrameworkElementAutomationPeer.FromElement(button) is ButtonAutomationPeer peer
            && peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider invoke)
            invoke.Invoke();
    }

    public static void Hint(DependencyObject target, string keys) => ToolTipService.SetToolTip(target, keys);
}
