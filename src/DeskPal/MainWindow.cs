using System.Runtime.InteropServices;
using DeskPal.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskPal;

// The app's real window. Closing only hides it, so the tray app is never left without its window object;
// the Home page's 3D surface sleeps whenever this window is hidden, minimized or on another page.
public sealed class MainWindow : Window
{
    private readonly MainContext _ctx;
    private readonly MainWindowUi _ui;
    private readonly Action _saveUi;
    private readonly Action _firstClose;
    private readonly NavigationView _nav = new();
    private readonly NavigationViewItem _homeItem = new() { Content = "Home", Icon = new SymbolIcon(Symbol.Home), Tag = "home" };
    private readonly HomePage _home;
    private SettingsPage? _settingsPage;
    private string _page = "home";
    private bool _quit;

    public MainWindow(MainContext ctx, MainWindowUi ui, Action saveUi, Action firstClose)
    {
        _ctx = ctx;
        _ui = ui;
        _saveUi = saveUi;
        _firstClose = firstClose;
        _home = new HomePage(ctx);

        Title = "Desk Pal";
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "deskpal.ico"));
        RestoreBounds();

        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.IsSettingsVisible = true;
        _nav.MenuItems.Add(_homeItem);
        _nav.MenuItems.Add(new NavigationViewItem { Content = "Today", Icon = new SymbolIcon(Symbol.Calendar), Tag = "today" });
        _nav.MenuItems.Add(new NavigationViewItem { Content = "Bloop", Icon = new SymbolIcon(Symbol.Emoji2), Tag = "bloop" });
        _nav.MenuItems.Add(new NavigationViewItem { Content = "Garden", Icon = new SymbolIcon(Symbol.Favorite), Tag = "garden" });
        _nav.FooterMenuItems.Add(new NavigationViewItem { Content = "About", Icon = new SymbolIcon(Symbol.Help), Tag = "about" });
        _nav.SelectionChanged += (_, e) =>
            TickGuard.Run("main-nav", () => Show(e.IsSettingsSelected ? "settings" : (e.SelectedItem as NavigationViewItem)?.Tag as string ?? "home"));
        Content = _nav;
        _nav.SelectedItem = _homeItem;

        AppWindow.Closing += (_, e) =>
        {
            if (_quit) return;
            e.Cancel = true;
            HideMain();
            if (!_ui.CloseHintShown)
            {
                _ui.CloseHintShown = true;
                _saveUi();
                TickGuard.Run("main-close-hint", _firstClose);
            }
        };
        AppWindow.Changed += (_, e) =>
        {
            if (e.DidVisibilityChange || e.DidPresenterChange) TickGuard.Run("main-visibility", UpdateSurface);
        };
        VisibilityChanged += (_, _) => TickGuard.Run("main-visibility", UpdateSurface);
    }

    public void ShowMain(string? page = null)
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p) p.Restore();
        if (page is not null) Navigate(page);
        AppWindow.Show(true);
        Activate();
        UpdateSurface();
    }

    public void Navigate(string tag)
    {
        if (tag == "settings") _nav.SelectedItem = _nav.SettingsItem;
        else
            foreach (var o in _nav.MenuItems.Concat(_nav.FooterMenuItems))
                if (o is NavigationViewItem { Tag: string t } item && t == tag) _nav.SelectedItem = item;
    }

    public void HideMain()
    {
        SaveBounds();
        AppWindow.Hide();
        UpdateSurface();
    }

    public void Quit()
    {
        SaveBounds();
        _quit = true;
        _home.SetActive(false);
        Close();
    }

    private void Show(string tag)
    {
        _page = tag;
        _nav.Content = tag switch
        {
            "today" => new TodayPage(_ctx),
            "bloop" => new BloopPage(_ctx),
            "garden" => new GardenPage(_ctx),
            "about" => new AboutPage(_ctx),
            "settings" => _settingsPage ??= new SettingsPage(_ctx.Hub),
            _ => _home,
        };
        UpdateSurface();
    }

    private void UpdateSurface()
    {
        var shown = AppWindow.IsVisible && AppWindow.Presenter is not OverlappedPresenter { State: OverlappedPresenterState.Minimized };
        _home.SetActive(shown && _page == "home");
    }

    private void RestoreBounds()
    {
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var rect = new RectInt32(_ui.X, _ui.Y, _ui.Width, _ui.Height);
        if (_ui.Width >= 400 && _ui.Height >= 300)
        {
            var work = DisplayArea.GetFromRect(rect, DisplayAreaFallback.Nearest).WorkArea;
            var visible = rect.X < work.X + work.Width - 100 && rect.X + rect.Width > work.X + 100
                && rect.Y >= work.Y - 10 && rect.Y < work.Y + work.Height - 100;
            if (visible)
            {
                AppWindow.MoveAndResize(rect);
                return;
            }
        }
        AppWindow.Resize(new SizeInt32((int)(1000 * scale), (int)(700 * scale)));
    }

    private void SaveBounds()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: not OverlappedPresenterState.Restored }) return;
        var pos = AppWindow.Position;
        var size = AppWindow.Size;
        _ui.X = pos.X;
        _ui.Y = pos.Y;
        _ui.Width = size.Width;
        _ui.Height = size.Height;
        _saveUi();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);
}
