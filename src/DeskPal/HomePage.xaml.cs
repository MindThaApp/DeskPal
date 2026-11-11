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

    private string Style => AppData.Debug3D ? "3d" : _ctx.Hub.Settings.AvatarStyle;

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
        var style = Style;
        var version = ++_loadVersion;
        var wantLive = style == "3d" && !Stage3D.Failed;
        if (!wantLive) _viewport?.Hide();
        _live = wantLive;
        LivePanel.Visibility = wantLive ? Visibility.Visible : Visibility.Collapsed;
        LivePanel.Opacity = 0;
        Avatar.Opacity = 1;
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
                _viewport = new LiveViewport(LivePanel);
                _viewport.FirstFrame += OnFirstFrame;
                _viewport.Failed += () =>
                {
                    _live = false;
                    LivePanel.Visibility = Visibility.Collapsed;
                    Avatar.Opacity = 1;
                };
            }
            _viewport.Show(XamlRoot?.RasterizationScale ?? 1.0);
        }
    }

    private void OnFirstFrame()
    {
        var d = new Duration(TimeSpan.FromMilliseconds(150));
        var fadeIn = new DoubleAnimation { From = 0, To = 1, Duration = d };
        var fadeOut = new DoubleAnimation { From = 1, To = 0, Duration = d };
        Storyboard.SetTarget(fadeIn, LivePanel);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        Storyboard.SetTarget(fadeOut, Avatar);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fadeIn);
        sb.Children.Add(fadeOut);
        sb.Begin();

        if (!AppData.DebugPoke || _debugPokes > 0) return;
        foreach (var at in new[] { 2.0, 5, 8 })
        {
            var n = ++_debugPokes;
            var t = DispatcherQueue.CreateTimer();
            t.IsRepeating = false;
            t.Interval = TimeSpan.FromSeconds(at);
            t.Tick += (_, _) => TickGuard.Run("debug-poke", () =>
            {
                Logger.Log($"debug poke {n} (main window)");
                Poke();
            });
            t.Start();
        }
    }

    private void OnStagePressed(object sender, PointerRoutedEventArgs e) => Poke();

    private void Poke()
    {
        if (_live && LivePanel.Visibility == Visibility.Visible)
        {
            _viewport?.Poke();
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
