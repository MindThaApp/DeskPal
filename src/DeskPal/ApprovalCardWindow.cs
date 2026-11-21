using System.Runtime.InteropServices;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskPal;

// Asks once whether another app may change Desk Pal over the control pipe. Shown without taking focus; closing it counts as "Not now".
// Never really closed until the app exits (closing the last window would end the tray app), only hidden.
public sealed class ApprovalCardWindow : Window
{
    private const int WidthDip = 440;
    private const int HeightDip = 250;
    private const int MarginDip = 16;
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, FontSize = 16, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _body = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = new() { Opacity = 0.7, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private bool _answered = true;
    private bool _quit;

    public ApprovalCardWindow()
    {
        Title = Loc.Get("Approval_Title");
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();

        var allow = new Button { Content = Loc.Get("Approval_Allow"), Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 96 };
        var notNow = new Button { Content = Loc.Get("Approval_NotNow"), MinWidth = 96 };
        allow.Click += (_, _) => TickGuard.Run("approval-allow", () => Finish(true));
        notNow.Click += (_, _) => TickGuard.Run("approval-not-now", () => Finish(false));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(notNow);
        buttons.Children.Add(allow);

        var panel = new StackPanel { Spacing = 8, Padding = new Thickness(20, 12, 20, 16) };
        panel.Children.Add(_title);
        panel.Children.Add(_body);
        panel.Children.Add(_detail);
        panel.Children.Add(buttons);
        Content = panel;
        // Granting access never happens from a stray Enter: Esc declines, Allow needs a deliberate click or Space on it.
        Keys.Wire(panel, () => Keys.Invoke(notNow), null);
        Keys.Hint(notNow, "Esc");

        var presenter = OverlappedPresenter.Create();
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);

        AppWindow.Closing += (_, e) =>
        {
            if (_quit) return;
            e.Cancel = true;
            TickGuard.Run("approval-close", () => Finish(false));
        };
    }

    public event Action<bool>? Answered;

    public bool IsAsking => !_answered;

    public void Ask(string appName, string? detail)
    {
        _answered = false;
        _title.Text = Loc.Format("Approval_Question", appName);
        _body.Text = Loc.Format("Approval_Body", appName);
        _detail.Text = detail ?? "";
        _detail.Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible;
        var hwnd = WindowNative.GetWindowHandle(this);
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var w = (int)(WidthDip * scale);
        var h = (int)(HeightDip * scale);
        var margin = (int)(MarginDip * scale);
        var work = DisplayArea.Primary.WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - w - margin, work.Y + work.Height - h - margin, w, h));
        AppWindow.Show(false);
    }

    private void Finish(bool allow)
    {
        AppWindow.Hide();
        if (_answered) return;
        _answered = true;
        Answered?.Invoke(allow);
    }

    public void Quit()
    {
        _quit = true;
        Close();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
