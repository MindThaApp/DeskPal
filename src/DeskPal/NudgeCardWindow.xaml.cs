using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DeskPal.Core;
using DeskPal.Core.Pose;
using DeskPal.Pose;
using DeskPal.Render;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml.Automation;
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
    private const int WidthDip = 340;
    private const int MarginDip = 12;
    private const double BloopSize = 1.6; // Bloop shows each move clearly next to the card

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
    private StretchCoach? _coach;
    private int _poseVersion;
    private bool _poseActive;
    private bool _bloopForced3D;
    private bool _stretchOffered;
    private string? _lastPoseHint;

    public NudgeCardWindow(ICommandRouter router)
    {
        _router = router;
        InitializeComponent();
        StretchButtonText.Text = Loc.Get("Nudge_StretchWithBloop");
        AutomationProperties.SetName(StretchButton, StretchButtonText.Text);
        AutomationProperties.SetName(PoseRing, StretchButtonText.Text);
        StopCameraButton.Content = Loc.Get("Nudge_StopCamera");
        AutomationProperties.SetName(StopCameraButton, Loc.Get("Nudge_StopCamera"));
        PoseCameraOn.Text = Loc.Get("Pose_CameraOn");
        PoseSettingsLink.Content = Loc.Get("Settings_CameraAccess");
        Closed += (_, _) =>
        {
            StopCamera();
            if (_gateHub is { } hub) hub.Changed -= OnGateChanged;
            _gateHub = null;
        };
        Appearance.Register(this, Root, Appearance.Role.Surface);
        Keys.Wire(Root, () => Keys.Invoke(SnoozeButton), () => Keys.Invoke(DoneButton));
        Keys.Hint(DoneButton, "Enter");
        Keys.Hint(SnoozeButton, "Esc");
        _hwnd = WindowNative.GetWindowHandle(this);
        _timer.Tick += (s, e) => TickGuard.Run("nudge-card", () => OnTimer(s, e));
        if (AppData.Debug3D) After(10, "debug-3d-stats", LogStats, repeat: true);

        // Never takes focus from the user's app; same soft card as the quick panel, with no light outline.
        CardChrome.Apply(this, _hwnd, CardChrome.Round, noActivate: true);
        Root.PointerEntered += (_, _) => TickGuard.Run("nudge-hover-in", () => { _hovered = true; Engage(); UpdatePause(); });
        Root.PointerExited += (_, _) => TickGuard.Run("nudge-hover-out", () => { _hovered = false; UpdatePause(); });
    }

    public event Action<Guid>? Expired;

    public Guid? CurrentId => _nudge?.Id;

    // Seconds a card left alone stays up; read from the settings when each card opens.
    public Func<int> HideSeconds { get; set; } = () => DeskPalSettings.RecommendedHideNudgeSeconds;

    // True while Stretch with Bloop is on in Settings and Pro (or its trial) is active.
    public Func<bool> StretchWithBloopAllowed { get; set; } = () => false;

    private SettingsHub? _gateHub;

    // Follows settings and Pro changes (Pro changes raise the hub too): when Stretch with Bloop is turned off or Pro
    // and its trial end, the camera stops at once and the button goes away.
    public void WatchGate(SettingsHub hub)
    {
        if (_gateHub is { } old) old.Changed -= OnGateChanged;
        _gateHub = hub;
        hub.Changed += OnGateChanged;
    }

    private void OnGateChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnGateChanged);
            return;
        }
        TickGuard.Run("card-stretch-gate", () =>
        {
            if (!_stretchOffered || StretchWithBloopAllowed()) return;
            Logger.Log("stretch with bloop no longer allowed; camera off");
            WithdrawStretch();
        });
    }

    // The feature is not allowed (any more): camera off, no button, no camera notice.
    private void WithdrawStretch()
    {
        _stretchOffered = false;
        EndPose(null, false);
        StretchArea.Visibility = StretchButton.Visibility = Visibility.Collapsed;
        PosePanel.Visibility = Visibility.Collapsed;
        PoseNotice.Visibility = PoseSettingsLink.Visibility = Visibility.Collapsed;
        ButtonsRow.Margin = new Thickness(0, 0, 6, 0);
        if (_nudge is not null) Place();
    }

    public async Task ShowNudgeAsync(Nudge nudge, string style)
    {
        StopCamera();
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
        _timedEndTick = long.MaxValue; // set when the user engages
        UpdateCountdownVisibility();
        DoneButton.IsEnabled = true;
        SnoozeButton.IsEnabled = MoreButton.IsEnabled = true;
        _repeating = false;
        _hovered = _bloopBusy = false;
        _bloopForced3D = false;
        ShowStretchArea(nudge);
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
            // Debug only: the scripted run starts by itself, since nobody clicks the button.
            if (AppData.DebugPoseCard && _stretchOffered) After(2.5, "debug-pose-card-start", () => TickGuard.Run("debug-pose-card", () => _ = StartPoseAsync()), once: true);
            if (AppData.DebugPoseCard && _stretchOffered)
                foreach (var at in new[] { 1.5, 4.5, 6.5, 8.5, 10.5, 11.8, 13.0 })
                {
                    var label = at.ToString("00.0", System.Globalization.CultureInfo.InvariantCulture) + "s";
                    After(at, "debug-pose-card-dump", () => _ = DebugDumpAsync(label), once: true);
                }
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
            _ when nudge.IsLongCall => ("star_jump", false, 0.8f),
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
        // Debug runs only: DESKPAL_DEBUG_CLIP picks the move Bloop shows, to check each one fits on screen.
        if (AppData.IsDebugRun && Environment.GetEnvironmentVariable("DESKPAL_DEBUG_CLIP") is { Length: > 0 } debugClip) clip = debugClip;
        viewport.PlaySequence("wave");
        if (clip is null) return;
        if (loop) viewport.Loop(clip);
        else viewport.Repeat(clip, gap);
        _repeating = true;
        _repeatUntilTick = _timedSeconds > 0 && _autoHide.Engaged ? _timedEndTick
            : _shownAtTick + (_timedSeconds > 0 ? HideSeconds() * 1000L : 12_000);
    }

    // Done: a high five from Bloop beside the card, then a little celebration, then the card goes.
    public void CelebrateThenHide()
    {
        StopCamera();
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
        StopCamera();
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
        StopCamera();
        Logger.Log($"card countdown ran out after {(Environment.TickCount64 - _shownAtTick) / 1000.0:F1} s (engaged {_autoHide.Engaged})");
        _repeating = false;
        _timer.Stop();
        _autoHide.Stop();
        var version = ++_loadVersion;
        BloopStage.Release(this);
        var fade = new DoubleAnimation { To = 0, Duration = new Duration(Motion.Ms(200)) };
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
        var duration = Motion.OpenMs(200);
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(duration) };
        var slide = Motion.Settle(8, 0, duration);
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
        if (busy != _bloopBusy) { _bloopBusy = busy; if (busy) Engage(); UpdatePause(); }
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

    // The user hovered the card, touched Bloop or used a card button: a timed exercise starts its own countdown now,
    // and Bloop keeps showing the move until it ends.
    private void Engage()
    {
        if (_nudge is null) return;
        var now = Environment.TickCount64;
        if (!_autoHide.Engage(now)) return;
        _timedEndTick = _autoHide.ExerciseEndMs;
        if (_repeating) _repeatUntilTick = _timedEndTick;
        if (AppData.IsDebugRun) Logger.Log($"card engaged, exercise runs {_timedSeconds} s");
        UpdateCountdown();
    }

    // The countdown waits while the pointer is on the card or Bloop is pressed, dragged or away.
    private void UpdatePause()
    {
        if (_nudge is null) return;
        var now = Environment.TickCount64;
        if (_hovered || _bloopBusy || _poseActive) _autoHide.Pause(now);
        else _autoHide.Resume(now);
        HideLine.Opacity = _autoHide.Paused ? 0.25 : 0.6;
    }

    private void UpdateCountdown()
    {
        var now = Environment.TickCount64;
        HideLineScale.ScaleX = _autoHide.Fraction(now);
        if (_timedSeconds <= 0) return;
        var leftMs = _autoHide.Engaged ? Math.Max(0, _timedEndTick - now) : _timedSeconds * 1000L;
        CountdownText.Text = Loc.Format("Nudge_CountdownSeconds", (int)Math.Ceiling(leftMs / 1000.0));
        BonusHint.Text = leftMs == 0 ? Loc.Get("Nudge_BonusDone") : Loc.Get("Nudge_BonusHint");
        Ring.Value = 100.0 * (1 - leftMs / (_timedSeconds * 1000.0));
    }

    private void OnDone(object sender, RoutedEventArgs e)
    {
        StopCamera();
        // Time on the countdown only counts once the user engaged; Done before that earns no share of the exercise.
        var share = _timedSeconds <= 0 ? 1.0
            : !_autoHide.Engaged ? 0.0
            : Math.Clamp(1 - Math.Max(0, _timedEndTick - Environment.TickCount64) / (_timedSeconds * 1000.0), 0, 1);
        Send(id => new DoneCommand(id, share));
    }
    private void OnSnooze(object sender, RoutedEventArgs e) { StopCamera(); Send(id => new SnoozeCommand(id)); }
    private void OnSkip(object sender, RoutedEventArgs e) { StopCamera(); Send(id => new SkipCommand(id)); }

    // Debug only (--debug-pose-card): saves the card as a PNG in %TEMP% (never over an existing file), so the card can be
    // checked when the screen cannot be captured. The picture is laid on the theme's plain surface colour.
    private async Task DebugDumpAsync(string label)
    {
        try
        {
            var target = new RenderTargetBitmap();
            await target.RenderAsync(Root);
            var pixels = (await target.GetPixelsAsync()).ToArray();
            var w = target.PixelWidth;
            var h = target.PixelHeight;
            using var src = new SkiaSharp.SKBitmap(new SkiaSharp.SKImageInfo(w, h, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul));
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, src.GetPixels(), Math.Min(pixels.Length, src.ByteCount));
            using var flat = new SkiaSharp.SKBitmap(w, h);
            using (var canvas = new SkiaSharp.SKCanvas(flat))
            {
                canvas.Clear(Root.ActualTheme == ElementTheme.Dark ? new SkiaSharp.SKColor(0x20, 0x20, 0x20) : new SkiaSharp.SKColor(0xF3, 0xF3, 0xF3));
                canvas.DrawBitmap(src, 0, 0);
            }
            var path = Path.Combine(Path.GetTempPath(), $"dp-posecard-{Environment.ProcessId}-{label}.png");
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            flat.Encode(file, SkiaSharp.SKEncodedImageFormat.Png, 100);
            Logger.Log("pose card dump " + Path.GetFileName(path));
        }
        catch (Exception ex) { Logger.Log("pose card dump failed: " + ex.Message); }
    }

    // ---- Stretch with Bloop ------------------------------------------------------------------------------------

    // Shows the camera button when the stretch can be checked and the feature is allowed. The camera stays off.
    private void ShowStretchArea(Nudge nudge)
    {
        _stretchOffered = StretchWithBloopAllowed() && nudge.Stretch is { } s && (StretchCheck.IsCheckable(s.Id) || AppData.DebugPoseCard);
        StretchArea.Visibility = _stretchOffered ? Visibility.Visible : Visibility.Collapsed;
        StretchButton.Visibility = _stretchOffered ? Visibility.Visible : Visibility.Collapsed;
        PosePanel.Visibility = Visibility.Collapsed;
        PoseNotice.Visibility = PoseSettingsLink.Visibility = Visibility.Collapsed;
        ButtonsRow.Margin = new Thickness(0, _stretchOffered ? 4 : 0, 6, 0);
    }

    private void OnStretchWithBloop(object sender, RoutedEventArgs e) => _ = StartPoseAsync();

    private void OnStopCamera(object sender, RoutedEventArgs e) => EndPose(null, false);

    private void OnCameraSettings(object sender, RoutedEventArgs e) => TickGuard.Run("card-camera-settings", () => _ = Windows.System.Launcher.LaunchUriAsync(PoseSession.CameraPrivacySettings));

    private async Task StartPoseAsync()
    {
        if (_nudge is not { Stretch: { } stretch } || _coach is not null) return;
        // Checked again at the click: the setting or Pro may have changed since the card was shown.
        if (!_stretchOffered || !StretchWithBloopAllowed())
        {
            WithdrawStretch();
            return;
        }
        try
        {
            var version = ++_poseVersion;
            PoseNotice.Visibility = PoseSettingsLink.Visibility = Visibility.Collapsed;
            StretchButton.Visibility = Visibility.Collapsed;
            PosePanel.Visibility = Visibility.Visible;
            PoseSides.Visibility = Visibility.Collapsed;
            PoseRing.Value = 0;
            _lastPoseHint = Loc.Get("Pose_Starting");
            PoseHint.Text = _lastPoseHint;
            _poseActive = true;
            _repeating = false;
            Engage();
            UpdatePause();
            UpdateCountdownVisibility();
            Place();

            var coach = new StretchCoach(DispatcherQueue, stretch.Id, stretch.DurationSeconds, AppData.DebugPoseCard);
            _coach = coach;
            coach.Updated += s => OnPoseUpdate(coach, s);
            coach.Ended += r => OnPoseEnded(coach, r);
            var result = await coach.StartAsync();
            if (version != _poseVersion || _coach != coach)
            {
                coach.Stop();
                return;
            }
            if (result != PoseStartResult.Started)
            {
                var key = result switch
                {
                    PoseStartResult.NoCamera => "Pose_NoCamera",
                    PoseStartResult.CameraInUse => "Pose_CameraInUse",
                    PoseStartResult.CameraBlocked => "Pose_CameraBlocked",
                    PoseStartResult.ModelUnavailable => "Pose_ModelUnavailable",
                    _ => "Pose_Failed",
                };
                EndPose(key, result == PoseStartResult.CameraBlocked);
                return;
            }
            UseLiveBloop();
        }
        catch (Exception ex)
        {
            Logger.Log("stretch with bloop failed: " + ex.GetType().Name + " " + ex.Message);
            EndPose("Pose_Failed", false);
        }
    }

    // Mirroring needs the live model. With a flat or soft picture style Bloop switches to the live model for this
    // stretch and goes back afterwards.
    private void UseLiveBloop()
    {
        if (_nudge is not { } nudge || _placement is not { } placement) return;
        if (_style != "3d" && !AppData.Debug3D)
        {
            _bloopForced3D = true;
            BloopStage.Claim(new BloopClaim(this, _hwnd, placement, "3d", nudge.AssetName, null, BloopSize));
        }
        else BloopStage.LiveFor(this)?.StopRepeat();
    }

    private void OnPoseUpdate(StretchCoach coach, CoachSnapshot s)
    {
        if (_coach != coach || !_poseActive) return;
        PoseRing.Value = Math.Clamp(s.Progress, 0f, 1f) * 100;
        var hint = Loc.Get(s.State switch
        {
            StretchState.Done => "Pose_WellDone",
            _ when s.Reason is { } reason => reason,
            StretchState.GetReady => "Pose_GetReady",
            _ => "Pose_HoldIt",
        });
        if (hint != _lastPoseHint)
        {
            _lastPoseHint = hint;
            PoseHint.Text = hint;
        }
        if (s.Sides == 2)
        {
            PoseSides.Visibility = Visibility.Visible;
            PoseSides.Text = Loc.Format("Pose_Sides", (int)Math.Round(s.LeftSide * 100), (int)Math.Round(s.RightSide * 100));
        }
        BloopStage.LiveFor(this)?.Mirror(s.Mirror);
        if (s.State == StretchState.Done) CompletePose();
    }

    // The stretch really happened: a soft sound, the camera off, then the same path as the Done button with the full share.
    private void CompletePose()
    {
        Logger.Log("stretch with bloop done");
        BloopSounds.Play("hop");
        StopCamera();
        Send(id => new DoneCommand(id, 1.0));
    }

    private void OnPoseEnded(StretchCoach coach, PoseStopReason reason)
    {
        if (_coach != coach) return;
        switch (reason)
        {
            case PoseStopReason.TimedOut: EndPose("Pose_StoppedTimeout", false); break;
            case PoseStopReason.CameraLost: EndPose("Pose_StoppedLost", false); break;
            case PoseStopReason.CameraBlocked: EndPose("Pose_CameraBlocked", true); break;
            case PoseStopReason.PcLocked or PoseStopReason.PcSleeping: EndPose("Pose_StoppedLocked", false); break;
            case PoseStopReason.Error: EndPose("Pose_StoppedGeneric", false); break;
            default: EndPose(null, false); break;
        }
    }

    // Turns the camera off and puts the card back: the camera button returns and, when there is something to say,
    // one kind line (with the privacy settings link when Windows blocked the camera).
    private void EndPose(string? noticeKey, bool settingsLink)
    {
        var wasActive = _poseActive;
        StopCamera();
        if (_nudge is not { } nudge || !wasActive) return;
        PosePanel.Visibility = Visibility.Collapsed;
        StretchButton.Visibility = _stretchOffered ? Visibility.Visible : Visibility.Collapsed;
        PoseNotice.Visibility = noticeKey is null ? Visibility.Collapsed : Visibility.Visible;
        if (noticeKey is not null) PoseNotice.Text = Loc.Get(noticeKey);
        PoseSettingsLink.Visibility = settingsLink ? Visibility.Visible : Visibility.Collapsed;
        UpdatePause();
        if (_bloopForced3D && _placement is { } placement)
        {
            _bloopForced3D = false;
            BloopStage.Claim(new BloopClaim(this, _hwnd, placement, _style, nudge.AssetName, v => StartClips(nudge, v), BloopSize));
        }
        Place();
    }

    // Stops the camera and Bloop's mirroring. Safe to call at any time and any number of times; every way off the card
    // (Done, Snooze, Skip, hide, expire, close, a new card) goes through here.
    private void StopCamera()
    {
        _poseVersion++;
        var coach = _coach;
        _coach = null;
        _poseActive = false;
        coach?.Stop();
        if (coach is not null)
        {
            BloopStage.LiveFor(this)?.ClearMirror();
            UpdateCountdownVisibility();
        }
    }

    // While the camera check runs its hold ring stands in for the countdown ring and the bonus line; Done and Snooze
    // stay as the manual way out.
    private void UpdateCountdownVisibility() =>
        CountdownPanel.Visibility = _timedSeconds > 0 && !_poseActive ? Visibility.Visible : Visibility.Collapsed;

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

    // Draws the picture in the chosen look (Looks.Current) unless a look is given, as the Customise preview does.
    internal static async Task<SvgImageSource?> LoadAvatarAsync(string style, string name, int pixels = 160, CharacterLook? look = null)
    {
        var path = ResolveAvatarPath(style, name);
        if (path is null) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            var wear = look ?? Looks.Current;
            if (!wear.IsDefault)
                bytes = System.Text.Encoding.UTF8.GetBytes(CharacterSvg.Apply(System.Text.Encoding.UTF8.GetString(bytes), name, wear));
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