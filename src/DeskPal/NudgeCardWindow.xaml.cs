using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DeskPal.Core;
using DeskPal.Render;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace DeskPal;

// A small reminder card near the tray corner: a title, a one or two line hint, Done and Snooze, and the rest under
// "...". Bloop stands beside it in the shared Bloop window (BloopStage), on the side away from the taskbar, and plays
// the exercise there. A thin line along the bottom counts down; when nobody uses the card it fades out by itself.
public sealed partial class NudgeCardWindow : Window
{
    private const int WidthDip = 300;
    private const int MarginDip = 12;
    private const double BloopSize = 0.65;

    private readonly ICommandRouter _router;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly IntPtr _hwnd;
    private readonly NudgeAutoHide _autoHide = new();
    private Nudge? _nudge;
    private long _shownAtTick;
    private long _timedEndTick;
    private int _timedSeconds;
    private long _loadVersion;
    private bool _repeating;
    private long _repeatUntilTick;
    private bool _hovered;
    private bool _bloopBusy;
    private string _style = "flat";
    private PanelPlacement? _placement;
    private IntPtr _foregroundBefore;
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _debugTimers = [];

    public NudgeCardWindow(ICommandRouter router)
    {
        _router = router;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _timer.Tick += (s, e) => TickGuard.Run("nudge-card", () => OnTimer(s, e));
        if (AppData.Debug3D) After(10, "debug-3d-stats", LogStats, repeat: true);

        // Never takes focus from the user's app; same soft card as the quick panel, with no light outline.
        CardChrome.Apply(this, _hwnd, CardChrome.Round, noActivate: true);
        Root.PointerEntered += (_, _) => TickGuard.Run("nudge-hover-in", () => { _hovered = true; UpdatePause(); });
        Root.PointerExited += (_, _) => TickGuard.Run("nudge-hover-out", () => { _hovered = false; UpdatePause(); });
    }

    public event Action<Guid>? Expired;

    public Guid? CurrentId => _nudge?.Id;

    // Seconds a card left alone stays up; read from the settings when each card opens.
    public Func<int> HideSeconds { get; set; } = () => DeskPalSettings.RecommendedHideNudgeSeconds;

    public async Task ShowNudgeAsync(Nudge nudge, string style)
    {
        _nudge = nudge;
        _style = style;
        var version = ++_loadVersion;
        TitleText.Text = nudge.Title;
        var hint = string.IsNullOrWhiteSpace(nudge.Instruction) ? nudge.Message : nudge.Instruction!;
        HintText.Text = hint;
        HintText.Visibility = string.IsNullOrWhiteSpace(hint) ? Visibility.Collapsed : Visibility.Visible;
        ToolTipService.SetToolTip(HintText, string.IsNullOrWhiteSpace(nudge.Instruction) ? null : nudge.Message + "\n" + nudge.Instruction);

        _shownAtTick = Environment.TickCount64;
        _timedSeconds = nudge.TimedSeconds ?? 0;
        _timedEndTick = _shownAtTick + _timedSeconds * 1000L;
        CountdownPanel.Visibility = _timedSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneButton.IsEnabled = _timedSeconds == 0;
        SnoozeButton.IsEnabled = MoreButton.IsEnabled = true;
        _repeating = false;
        _hovered = _bloopBusy = false;
        _autoHide.Start(_shownAtTick, HideSeconds(), _timedSeconds);
        UpdateCountdown();

        if (AppData.IsDebugRun)
        {
            _foregroundBefore = GetForegroundWindow();
            LogStats("before card");
        }

        await Task.Yield();
        if (version != _loadVersion) return;

        Place();
        Root.Opacity = 0;
        Slide.Y = 8;
        AppWindow.Show(false);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        Place();
        PlayEntrance();
        _timer.Start();
        if (_placement is { } placement)
            BloopStage.Claim(new BloopClaim(this, _hwnd, placement, style, nudge.AssetName, v => StartClips(nudge, v), BloopSize));

        if (AppData.IsDebugRun)
        {
            var r = AppWindow.Position;
            var s = AppWindow.Size;
            Logger.Log($"card rect {r.X} {r.Y} {s.Width} {s.Height}");
            if (BloopStage.Window is { } w) Logger.Log($"card bloop hwnd 0x{w.Hwnd:X}");
            After(2, "debug-card-fg", () => LogForeground("after card shown"), once: true);
            if (AppData.DebugPoke)
                foreach (var at in new[] { 2.0, 5, 8 })
                    After(at, "debug-poke", () => BloopStage.LiveFor(this)?.Poke(withClip: false), once: true);
            // Debug only: drives the shared Bloop through its own drag code while the card is up.
            if (AppData.DebugBloopDrag) After(4, "debug-card-bloop-drag", () =>
            {
                BloopStage.Window?.DebugDrag(new System.Numerics.Vector2(-300, -180));
                After(0.6, "debug-card-bloop-check", () => Logger.Log($"card during bloop drag: open={_nudge is not null} paused={_autoHide.Paused}"), once: true);
            }, once: true);
        }
    }

    // A wave on arrival, then the clip that matches the nudge, repeated (or looped) while its countdown runs.
    private void StartClips(Nudge nudge, LiveViewport viewport)
    {
        if (_nudge != nudge) return;
        var (clip, loop, gap) = nudge switch
        {
            _ when nudge.Message == NudgeScheduler.LongCallMessage => ("star_jump", false, 0.8f),
            _ when nudge.Kinds.Contains(NudgeKind.Walk) => ("walk", true, 0f),
            { Stretch: { } s } => (s.Id switch
            {
                "side-bend" => "side_bend",
                "shoulder-shrug" => "shoulder_shrug",
                _ => "stretch_overhead",
            }, false, 1.5f),
            _ when nudge.Kinds.Contains(NudgeKind.Stretch) => ("stretch_overhead", false, 1.5f),
            _ => ((string?)null, false, 0f),
        };
        viewport.PlaySequence("wave");
        if (clip is null) return;
        if (loop) viewport.Loop(clip);
        else viewport.Repeat(clip, gap);
        _repeating = true;
        _repeatUntilTick = _timedSeconds > 0 ? _timedEndTick : _shownAtTick + 12_000;
    }

    // Done: a high five from Bloop beside the card, then a little celebration, then the card goes.
    public void CelebrateThenHide()
    {
        if (BloopStage.LiveFor(this) is not { } viewport || Stage3D.Failed)
        {
            HideCard();
            return;
        }
        _nudge = null;
        _repeating = false;
        _autoHide.Stop();
        DoneButton.IsEnabled = SnoozeButton.IsEnabled = MoreButton.IsEnabled = false;
        viewport.PlaySequence("high_five", "celebrate");
        var version = _loadVersion;
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromSeconds(3.9);
        timer.Tick += (_, _) => TickGuard.Run("card-celebrate-hide", () =>
        {
            timer.Stop();
            if (version == _loadVersion) HideCard();
        });
        timer.Start();
    }

    // Hides the card; Bloop hops back to the card's side, fades and hides (or goes back to the quick panel).
    public void HideCard()
    {
        _repeating = false;
        _timer.Stop();
        _autoHide.Stop();
        _nudge = null;
        _loadVersion++;
        BloopStage.Release(this);
        AppWindow.Hide();
        if (AppData.IsDebugRun) LogStats("card hidden");
    }

    // Nobody used the card: it fades out, then hides, and the scheduler hears it went unanswered.
    private void FadeOutAndExpire()
    {
        if (_nudge is not { } nudge) return;
        Logger.Log("card countdown ran out");
        _repeating = false;
        _timer.Stop();
        _autoHide.Stop();
        var version = ++_loadVersion;
        BloopStage.Release(this);
        var fade = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(200)) };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Completed += (_, _) => TickGuard.Run("card-fade-out", () =>
        {
            if (version != _loadVersion) return;
            _nudge = null;
            AppWindow.Hide();
            Expired?.Invoke(nudge.Id);
        });
        sb.Begin();
    }

    private void LogForeground(string when)
    {
        var now = GetForegroundWindow();
        Logger.Log($"foreground {when}: before=0x{_foregroundBefore:X} now=0x{now:X} same={now == _foregroundBefore} card=0x{_hwnd:X}");
    }

    private void LogStats() => LogStats("3d presents/10s: " + Interlocked.Exchange(ref LiveViewport.Presents, 0));

    private static void LogStats(string label)
    {
        using var p = Process.GetCurrentProcess();
        Logger.Log($"{label}; private {p.PrivateMemorySize64 / 1048576.0:F1} MB, working set {p.WorkingSet64 / 1048576.0:F1} MB");
    }

    private void After(double seconds, string name, Action action, bool repeat = false, bool once = false)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = repeat;
        timer.Interval = TimeSpan.FromSeconds(seconds);
        timer.Tick += (_, _) => TickGuard.Run(name, () =>
        {
            if (once) timer.Stop();
            action();
        });
        _debugTimers.Add(timer);
        timer.Start();
    }

    // In the tray corner of the main screen, as tall as the content needs. The taskbar edge decides the corner and
    // which side Bloop stands on.
    private void Place()
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var area = DisplayArea.Primary;
        var work = area.WorkArea;
        var mon = area.OuterBounds;
        var edge = work.Y + work.Height < mon.Y + mon.Height ? TaskbarEdge.Bottom
            : work.Y > mon.Y ? TaskbarEdge.Top
            : work.X + work.Width < mon.X + mon.Width ? TaskbarEdge.Right
            : work.X > mon.X ? TaskbarEdge.Left
            : TaskbarEdge.Bottom;
        Root.Measure(new Windows.Foundation.Size(WidthDip, double.PositiveInfinity));
        var w = (int)Math.Round(WidthDip * scale);
        var hDip = Root.DesiredSize.Height is > 40 and var d ? d : 110;
        var h = (int)Math.Ceiling(hDip * scale);
        var margin = (int)(MarginDip * scale);
        var x = edge == TaskbarEdge.Left ? work.X + margin : work.X + work.Width - w - margin;
        var y = edge == TaskbarEdge.Top ? work.Y + margin : work.Y + work.Height - h - margin;
        var rect = new RectInt32(x, y, w, h);
        AppWindow.MoveAndResize(rect);
        var changed = _placement?.Card != rect;
        _placement = new PanelPlacement(rect, work, edge, scale, rect);
        if (changed && _nudge is not null) BloopStage.Move(this, _placement);
    }

    // A short, gentle fade and rise: no bounce, no flash.
    private void PlayEntrance()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(200));
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = ease };
        var slide = new DoubleAnimation { From = 8, To = 0, Duration = duration, EasingFunction = ease };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        Storyboard.SetTarget(slide, Slide);
        Storyboard.SetTargetProperty(slide, "Y");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Children.Add(slide);
        sb.Begin();
    }

    private void OnTimer(object? sender, object e)
    {
        if (_nudge is null) return;
        var now = Environment.TickCount64;
        var busy = BloopStage.Owns(this) && BloopStage.Window is { IsBusy: true };
        if (busy != _bloopBusy) { _bloopBusy = busy; UpdatePause(); }
        if (_autoHide.Expired(now))
        {
            FadeOutAndExpire();
            return;
        }
        UpdateCountdown();
        if (_repeating && now >= _repeatUntilTick)
        {
            _repeating = false;
            BloopStage.LiveFor(this)?.StopRepeat();
        }
    }

    // The countdown waits while the pointer is on the card or Bloop is pressed, dragged or away.
    private void UpdatePause()
    {
        if (_nudge is null) return;
        var now = Environment.TickCount64;
        if (_hovered || _bloopBusy) _autoHide.Pause(now);
        else _autoHide.Resume(now);
        HideLine.Opacity = _autoHide.Paused ? 0.25 : 0.6;
    }

    private void UpdateCountdown()
    {
        var now = Environment.TickCount64;
        HideLineScale.ScaleX = _autoHide.Fraction(now);
        if (_timedSeconds <= 0) return;
        var leftMs = Math.Max(0, _timedEndTick - now);
        CountdownText.Text = $"{(int)Math.Ceiling(leftMs / 1000.0)} s";
        if (leftMs == 0) DoneButton.IsEnabled = true;
        Ring.Value = 100.0 * (1 - leftMs / (_timedSeconds * 1000.0));
    }

    private void OnDone(object sender, RoutedEventArgs e) => Send(id => new DoneCommand(id));
    private void OnSnooze(object sender, RoutedEventArgs e) => Send(id => new SnoozeCommand(id));
    private void OnSkip(object sender, RoutedEventArgs e) => Send(id => new SkipCommand(id));

    private void Send(Func<Guid, Command> make)
    {
        if (_nudge is { } n) _router.Execute(make(n.Id));
    }

    public static string? ResolveAvatarPath(string style, string name)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "Assets", "Characters");
        if (style is "soft3d" or "3d")
        {
            var soft = Path.Combine(root, "soft3d", name + ".svg");
            if (File.Exists(soft)) return soft;
        }
        var flat = Path.Combine(root, name + ".svg");
        return File.Exists(flat) ? flat : null;
    }

    internal static async Task<SvgImageSource?> LoadAvatarAsync(string style, string name, int pixels = 160)
    {
        var path = ResolveAvatarPath(style, name);
        if (path is null) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var source = new SvgImageSource { RasterizePixelWidth = pixels, RasterizePixelHeight = pixels };
            var status = await source.SetSourceAsync(stream);
            return status == SvgImageSourceLoadStatus.Success ? source : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}