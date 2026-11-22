using System.Runtime.InteropServices;
using DeskPal.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
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
    private readonly NavigationViewItem _homeItem = new() { Content = Loc.Get("Main_Nav_Home"), Icon = new SymbolIcon(Symbol.Home), Tag = "home" };
    private readonly HomePage _home;
    private SettingsPage? _settingsPage;
    private string _page = "home";
    private bool _quit;
    private string? _pendingPage;

    public MainWindow(MainContext ctx, MainWindowUi ui, Action saveUi, Action firstClose)
    {
        _ctx = ctx;
        _ui = ui;
        _saveUi = saveUi;
        _firstClose = firstClose;
        _home = new HomePage(ctx);

        Title = Loc.Get("Main_Title");
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "deskpal.ico"));
        RestoreBounds();

        _nav.PaneDisplayMode = NavigationViewPaneDisplayMode.Auto;
        _nav.IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed;
        _nav.IsSettingsVisible = true;
        _nav.MenuItems.Add(_homeItem);
        _nav.MenuItems.Add(new NavigationViewItem { Content = Loc.Get("Main_Nav_Today"), Icon = new SymbolIcon(Symbol.Calendar), Tag = "today" });
        _nav.MenuItems.Add(new NavigationViewItem { Content = Loc.Get("Main_Nav_Bloop"), Icon = new SymbolIcon(Symbol.Emoji2), Tag = "bloop" });
        _nav.MenuItems.Add(new NavigationViewItem { Content = Loc.Get("Main_Nav_Garden"), Icon = new SymbolIcon(Symbol.Favorite), Tag = "garden" });
        _nav.FooterMenuItems.Add(new NavigationViewItem { Content = Loc.Get("Main_Nav_Pro"), Icon = new SymbolIcon(Symbol.OutlineStar), Tag = "pro" });
        _nav.FooterMenuItems.Add(new NavigationViewItem { Content = Loc.Get("Main_Nav_About"), Icon = new SymbolIcon(Symbol.Help), Tag = "about" });
        _nav.SelectionChanged += (_, e) =>
            TickGuard.Run("main-nav", () => Show(e.IsSettingsSelected ? "settings" : (e.SelectedItem as NavigationViewItem)?.Tag as string ?? "home"));
        Content = _nav;
        Appearance.Register(this, _nav, Appearance.Role.Chrome);
        _nav.SelectedItem = _homeItem;
        AddShortcuts();

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

    // Ctrl+1..9 jump to the nav pages in order (top items, then Pro and About); Ctrl+, opens Settings.
    private void AddShortcuts()
    {
        var items = _nav.MenuItems.Concat(_nav.FooterMenuItems).OfType<NavigationViewItem>().Take(9).ToList();
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var key = VirtualKey.Number1 + i;
            var accel = new KeyboardAccelerator { Key = key, Modifiers = VirtualKeyModifiers.Control };
            accel.Invoked += (_, e) => { e.Handled = true; TickGuard.Run("main-shortcut", () => _nav.SelectedItem = item); };
            _nav.KeyboardAccelerators.Add(accel);
            Keys.Hint(item, $"{item.Content} (Ctrl+{i + 1})");
        }
        var settings = new KeyboardAccelerator { Key = (VirtualKey)0xBC /* VK_OEM_COMMA */, Modifiers = VirtualKeyModifiers.Control };
        settings.Invoked += (_, e) => { e.Handled = true; TickGuard.Run("main-shortcut", () => Navigate("settings")); };
        _nav.KeyboardAccelerators.Add(settings);
        _nav.Loaded += (_, _) =>
        {
            if (_pendingPage is { } pending)
            {
                _pendingPage = null;
                TickGuard.Run("main-pending-nav", () => Navigate(pending));
            }
            if (_nav.SettingsItem is NavigationViewItem s) Keys.Hint(s, $"{s.Content} (Ctrl+,)");
        };
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
        // The settings item only exists once the NavigationView has loaded, so an early request waits for Loaded.
        if (!_nav.IsLoaded) { _pendingPage = tag; return; }
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
            "pro" => new ProPage(_ctx),
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
        if (AppData.DebugGesture is not null)
        {
            // Scripted gesture runs stay off the desktop; frames are checked through --debug-3d-dump.
            AppWindow.MoveAndResize(new RectInt32(-6000, 100, (int)(1000 * scale), (int)(700 * scale)));
            return;
        }
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
