using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DeskPal.Core;
using DeskPal.Render;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace DeskPal;

public sealed partial class NudgeCardWindow : Window
{
    private static readonly RoutedEvent UIElementPointerPressed = UIElement.PointerPressedEvent;
    private const int WidthDip = 360;
    private const int HeightDip = 220;
    private const int MarginDip = 12;
    private static readonly TimeSpan AutoHide = TimeSpan.FromMinutes(2);

    private readonly ICommandRouter _router;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly IntPtr _hwnd;
    private Nudge? _nudge;
    private long _shownAtTick;
    private long _timedEndTick;
    private int _timedSeconds;
    private long _loadVersion;
    private LiveViewport? _viewport;
    private long _liveRequestedTick;
    private int _debugPokes;
    private IntPtr _foregroundBefore;
    private readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> _debugTimers = [];

    public NudgeCardWindow(ICommandRouter router)
    {
        _router = router;
        InitializeComponent();
        // handledEventsToo: a child that marks the press handled must not stop Bloop reacting.
        Root.AddHandler(UIElementPointerPressed, new PointerEventHandler(OnRootPressed), true);
        _hwnd = WindowNative.GetWindowHandle(this);
        _timer.Tick += (s, e) => TickGuard.Run("nudge-card", () => OnTimer(s, e));
        if (AppData.Debug3D) After(10, "debug-3d-stats", LogStats, repeat: true);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        // Tool window with no-activate: no taskbar button and clicks never take focus from the user's app.
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    public event Action<Guid>? Expired;

    public Guid? CurrentId => _nudge?.Id;

    public async Task ShowNudgeAsync(Nudge nudge, string style)
    {
        _nudge = nudge;
        var version = ++_loadVersion;
        TitleText.Text = nudge.Title;
        MessageText.Text = nudge.Message;
        InstructionText.Text = nudge.Instruction ?? "";
        InstructionText.Visibility = nudge.Instruction is null ? Visibility.Collapsed : Visibility.Visible;

        _shownAtTick = Environment.TickCount64;
        _timedSeconds = nudge.TimedSeconds ?? 0;
        _timedEndTick = _shownAtTick + _timedSeconds * 1000L;
        CountdownPanel.Visibility = _timedSeconds > 0 ? Visibility.Visible : Visibility.Collapsed;
        DoneButton.IsEnabled = _timedSeconds == 0;
        UpdateCountdown();

        if (AppData.IsDebugRun)
        {
            _foregroundBefore = GetForegroundWindow();
            LogStats("before card");
        }

        var live = style == "3d" && !Stage3D.Failed && nudge.AssetName.StartsWith("bloop", StringComparison.OrdinalIgnoreCase);
        var slot = style == "3d" ? 96 : 72;
        AvatarColumn.Width = new GridLength(slot);
        AvatarSlot.Width = AvatarSlot.Height = slot;
        LivePanel.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
        LivePanel.Opacity = 0;
        Avatar.Opacity = 1;

        Avatar.Source = null;
        var avatar = await LoadAvatarAsync(style, nudge.AssetName);
        if (version != _loadVersion) return;
        Avatar.Source = avatar;

        Place();
        Root.Opacity = 0;
        Slide.Y = 24;
        AppWindow.Show(false);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        PlayEntrance();
        _timer.Start();

        if (live)
        {
            _viewport ??= CreateViewport();
            _liveRequestedTick = Environment.TickCount64;
            _viewport.Show(GetDpiForWindow(_hwnd) / 96.0);
        }
        if (AppData.IsDebugRun)
        {
            var r = AppWindow.Position;
            var s = AppWindow.Size;
            Logger.Log($"card rect {r.X} {r.Y} {s.Width} {s.Height}");
        }
    }

    public void HideCard()
    {
        _timer.Stop();
        _nudge = null;
        _loadVersion++;
        _viewport?.Hide();
        AppWindow.Hide();
        if (AppData.IsDebugRun) LogStats("card hidden");
    }

    private LiveViewport CreateViewport()
    {
        var viewport = new LiveViewport(LivePanel);
        viewport.FirstFrame += OnFirstFrame;
        viewport.Failed += () =>
        {
            LivePanel.Visibility = Visibility.Collapsed;
            LivePanel.Opacity = 0;
            Avatar.Opacity = 1;
        };
        return viewport;
    }

    private void OnFirstFrame()
    {
        Logger.Log($"3d first frame {Environment.TickCount64 - _liveRequestedTick} ms after show");
        var duration = new Duration(TimeSpan.FromMilliseconds(150));
        var fadeIn = new DoubleAnimation { From = 0, To = 1, Duration = duration };
        var fadeOut = new DoubleAnimation { From = 1, To = 0, Duration = duration };
        Storyboard.SetTarget(fadeIn, LivePanel);
        Storyboard.SetTargetProperty(fadeIn, "Opacity");
        Storyboard.SetTarget(fadeOut, Avatar);
        Storyboard.SetTargetProperty(fadeOut, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fadeIn);
        sb.Children.Add(fadeOut);
        sb.Begin();

        if (!AppData.IsDebugRun) return;
        LogForeground("after card shown");
        if (AppData.DebugPoke)
        {
            // Pokes at 2, 5, 8 s, then two long gaps so the loop is fully asleep for the last two.
            foreach (var at in new[] { 2.0, 5, 8, 21, 34 })
            {
                var n = ++_debugPokes;
                After(at, "debug-poke", () =>
                {
                    Logger.Log($"debug poke {n}");
                    _viewport?.Poke();
                    if (n == 1) After(1.5, "debug-poke-fg", () => LogForeground("after poke"));
                }, once: true);
            }
        }
    }
    private void OnRootPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_viewport is null || LivePanel.Visibility != Visibility.Visible) return;
        var p = e.GetCurrentPoint(AvatarSlot).Position;
        var hit = p.X >= 0 && p.Y >= 0 && p.X <= LivePanel.Width && p.Y <= LivePanel.Height;
        Logger.Log($"3d press at {p.X:F0},{p.Y:F0} hit={hit}");
        if (hit) _viewport.Poke();
    }
    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_viewport is null || LivePanel.Visibility != Visibility.Visible) return;
        var p = e.GetCurrentPoint(LivePanel).Position;
        _viewport.LookAt(new Vector2((float)((p.X - 48) / 150), (float)((p.Y - 40) / 100)));
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e) => _viewport?.LookAway();

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
        timer.Tick += (_, _) =>
        {
            if (once) timer.Stop();
            TickGuard.Run(name, action);
        };
        _debugTimers.Add(timer);
        timer.Start();
    }

    private void Place()
    {
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var w = (int)(WidthDip * scale);
        var h = (int)(HeightDip * scale);
        var margin = (int)(MarginDip * scale);
        var work = DisplayArea.Primary.WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - w - margin, work.Y + work.Height - h - margin, w, h));
    }

    private void PlayEntrance()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(260));
        var fade = new DoubleAnimation { From = 0, To = 1, Duration = duration, EasingFunction = ease };
        var slide = new DoubleAnimation { From = 24, To = 0, Duration = duration, EasingFunction = ease };
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
        if (Environment.TickCount64 - _shownAtTick >= AutoHide.TotalMilliseconds)
        {
            var id = _nudge.Id;
            HideCard();
            Expired?.Invoke(id);
            return;
        }
        UpdateCountdown();
    }

    private void UpdateCountdown()
    {
        if (_timedSeconds <= 0) return;
        var leftMs = Math.Max(0, _timedEndTick - Environment.TickCount64);
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

    private static async Task<SvgImageSource?> LoadAvatarAsync(string style, string name)
    {
        var path = ResolveAvatarPath(style, name);
        if (path is null) return null;
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(bytes.AsBuffer());
            stream.Seek(0);
            var source = new SvgImageSource { RasterizePixelWidth = 160, RasterizePixelHeight = 160 };
            var status = await source.SetSourceAsync(stream);
            return status == SvgImageSourceLoadStatus.Success ? source : null;
        }
        catch
        {
            return null;
        }
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const long WS_EX_NOACTIVATE = 0x08000000;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
}

