using System.Numerics;
using DeskPal.Core;
using DeskPal.Render;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;

namespace DeskPal;

// Everything the main window's pages need from the app.
public sealed record MainContext(
    NudgeScheduler Scheduler,
    SettingsHub Hub,
    Action<string> Navigate,
    Action ShowEyeCue);

public sealed partial class HomePage : Page
{
    private readonly MainContext _ctx;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private LiveViewport? _viewport;
    private bool _active;
    private bool _live;
    private string _style = "";
    private long _loadVersion;
    private int _debugPokes;

    public HomePage(MainContext ctx)
    {
        _ctx = ctx;
        InitializeComponent();
        _timer.Tick += (_, _) => TickGuard.Run("home-refresh", Refresh);
        _ctx.Hub.Changed += () => { if (_active) TickGuard.Run("home-settings", () => { Refresh(); _ = LoadStyleAsync(); }); };
    }

    // Called when the page is on screen and the window is showing; false lets the 3D surface go to sleep.
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active)
        {
            Refresh();
            _timer.Start();
            _ = LoadStyleAsync();
        }
        else
        {
            _timer.Stop();
            _viewport?.Hide();
        }
    }

    private string CurrentStyle => AppData.Debug3D ? "3d" : _ctx.Hub.Settings.AvatarStyle;

    private void Refresh()
    {
        var h = DateTime.Now.Hour;
        GreetingText.Text = h switch
        {
            < 5 => "Still up? Be kind to your eyes tonight.",
            < 12 => "Morning! Ready for a good day?",
            < 17 => "Afternoon! A little stretch will feel good.",
            < 22 => "Evening! You did well today.",
            _ => "Late one. Time to wind down soon?",
        };
        var s = _ctx.Scheduler;
        if (s.NextDue is { } next)
        {
            var what = next.Kind switch { NudgeKind.Eyes => "Eye break", NudgeKind.Walk => "Walk", _ => "Stretch" };
            NextText.Text = $"{what} at {next.At:HH:mm}";
        }
        else NextText.Text = "Nothing scheduled";
        var holds = s.Holds;
        HoldsText.Text = holds.Count == 0 ? "No holds" : "Holding: " + string.Join(", ", holds.Select(x => x.Reason));
        CountsText.Text = $"Stretches {s.StretchesToday}   Eye breaks {s.EyeBreaksToday}   Walks {s.WalksToday}";
        PauseButton.Content = s.ManualHold is null ? "Pause 30 min" : "Resume";
    }

    private async Task LoadStyleAsync()
    {
        var style = CurrentStyle;
        var version = ++_loadVersion;
        var wantLive = style == "3d" && !Stage3D.Failed;
        if (!wantLive) _viewport?.Hide();
        _live = wantLive;
        LivePanel.Visibility = wantLive ? Visibility.Visible : Visibility.Collapsed;
        LivePanel.Opacity = 0;
        Avatar.Opacity = 1;
        SproutImage.Opacity = 1;
        SproutHit.Visibility = Visibility.Collapsed;
        if (style != _style || Avatar.Source is null)
        {
            _style = style;
            var bloop = await NudgeCardWindow.LoadAvatarAsync(style, "bloop-idle", 640);
            var sprout = await NudgeCardWindow.LoadAvatarAsync(style, "sprout-mood-happy", 192);
            if (version != _loadVersion) return;
            Avatar.Source = bloop;
            SproutImage.Source = sprout;
        }
        if (wantLive && _active)
        {
            if (_viewport is null)
            {
                _viewport = new LiveViewport(LivePanel, home: true, centerX: 160);
                _viewport.FirstFrame += OnFirstFrame;
                _viewport.Failed += () =>
                {
                    _live = false;
                    LivePanel.Visibility = Visibility.Collapsed;
                    SproutHit.Visibility = Visibility.Collapsed;
                    Avatar.Opacity = 1;
                    SproutImage.Opacity = 1;
                };
                ActualThemeChanged += (_, _) => TickGuard.Run("home-theme", () => { if (_viewport is { } v) v.Backdrop = Backdrop(); });
            }
            _viewport.Backdrop = Backdrop();
            _viewport.SproutStage = _ctx.Hub.Settings.SproutStage;
            _viewport.Show(XamlRoot?.RasterizationScale ?? 1.0);
        }
    }

    // The swap chain area shows the window backdrop directly: the NavigationView content layer (LayerFillColorDefault,
    // translucent) is not composed underneath it, so the empty surface looked darker than the page. Clearing to that
    // layer colour, premultiplied, makes the surface match the page around it.
    private Vortice.Mathematics.Color4 Backdrop() => ActualTheme switch
    {
        ElementTheme.Dark => new(0x3A / 255f * 0x4C / 255f, 0x3A / 255f * 0x4C / 255f, 0x3A / 255f * 0x4C / 255f, 0x4C / 255f),
        ElementTheme.Light => new(0x80 / 255f, 0x80 / 255f, 0x80 / 255f, 0x80 / 255f),
        _ => new(0, 0, 0, 0),
    };

    private void OnFirstFrame()
    {
        var d = new Duration(TimeSpan.FromMilliseconds(150));
        var sb = new Storyboard();
        Fade(LivePanel, 0, 1);
        Fade(Avatar, 1, 0);
        var sprout3d = _viewport?.HasSprout == true;
        if (sprout3d) Fade(SproutImage, 1, 0);
        SproutHit.Visibility = sprout3d ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetZIndex(SproutImage, sprout3d ? 0 : 1);
        sb.Begin();

        void Fade(UIElement target, double from, double to)
        {
            var a = new DoubleAnimation { From = from, To = to, Duration = d };
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, "Opacity");
            sb.Children.Add(a);
        }

        if (!AppData.DebugPoke || _debugPokes > 0) return;
        foreach (var at in new[] { 2.0, 5, 8, 11, 15 })
        {
            var n = ++_debugPokes;
            var t = DispatcherQueue.CreateTimer();
            t.IsRepeating = false;
            t.Interval = TimeSpan.FromSeconds(at);
            t.Tick += (_, _) => TickGuard.Run("debug-poke", () =>
            {
                Logger.Log($"debug poke {n} (main window)");
                if (n == 4) Celebrate();
                else if (n == 5) _viewport?.SproutHappy();
                else Poke();
            });
            t.Start();
        }
    }

    private void OnStagePressed(object sender, PointerRoutedEventArgs e) => Poke();

    private void OnStageDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Celebrate();

    private void OnSproutPressed(object sender, PointerRoutedEventArgs e) => _viewport?.SproutHappy();

    private void Celebrate()
    {
        if (_live && LivePanel.Visibility == Visibility.Visible) _viewport?.Play("celebrate");
    }

    private void Poke()
    {
        if (_live && LivePanel.Visibility == Visibility.Visible)
        {
            _viewport?.Poke(withClip: true);
            return;
        }
        var d = new Duration(TimeSpan.FromMilliseconds(650));
        var ease = new ElasticEase { EasingMode = EasingMode.EaseOut, Oscillations = 3, Springiness = 4 };
        var sy = new DoubleAnimation { From = 0.78, To = 1, Duration = d, EasingFunction = ease };
        var sx = new DoubleAnimation { From = 1.14, To = 1, Duration = d, EasingFunction = ease };
        Storyboard.SetTarget(sy, Squish);
        Storyboard.SetTargetProperty(sy, "ScaleY");
        Storyboard.SetTarget(sx, Squish);
        Storyboard.SetTargetProperty(sx, "ScaleX");
        var sb = new Storyboard();
        sb.Children.Add(sy);
        sb.Children.Add(sx);
        sb.Begin();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_viewport is null || !_live || LivePanel.Visibility != Visibility.Visible) return;
        var p = e.GetCurrentPoint(LivePanel).Position;
        _viewport.LookAt(new Vector2((float)((p.X - 160) / 450), (float)((p.Y - 140) / 300)));
    }

    private void OnPointerExited(object sender, PointerRoutedEventArgs e) => _viewport?.LookAway();

    private void OnStretchNow(object sender, RoutedEventArgs e) => _ctx.Scheduler.Execute(new TestNudgeCommand());

    private void OnEyeNow(object sender, RoutedEventArgs e) => _ctx.ShowEyeCue();

    private void OnPause(object sender, RoutedEventArgs e)
    {
        _ctx.Scheduler.Execute(_ctx.Scheduler.ManualHold is null ? new PauseCommand(Minutes: 30) : new ResumeCommand());
        Refresh();
    }

    private void OnSettings(object sender, RoutedEventArgs e) => _ctx.Navigate("settings");
}
