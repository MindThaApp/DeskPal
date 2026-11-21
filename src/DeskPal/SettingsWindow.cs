using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace DeskPal;

// Thin host for SettingsPage. Closing only hides it, so the tray app is never left without a window.
public sealed class SettingsWindow : Window
{
    private bool _quit;

    public SettingsWindow(SettingsHub hub)
    {
        Title = Loc.Get("Settings_WindowTitle");
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        var page = new SettingsPage(hub);
        Content = page;
        Keys.Wire(page, () => AppWindow.Hide(), null);
        AppWindow.Resize(new SizeInt32(820, 900));
        AppWindow.Closing += (_, e) =>
        {
            if (_quit) return;
            e.Cancel = true;
            AppWindow.Hide();
        };
    }

    public void ShowSettings()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        AppWindow.Show(true);
        Activate();
    }

    public void Quit()
    {
        _quit = true;
        Close();
    }
}
