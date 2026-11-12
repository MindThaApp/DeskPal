using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using DeskPal.Render;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace DeskPal;

// Bloop next to the quick panel card, in a window of its own with nothing drawn around it: no frame, no backdrop,
// no box. It never takes activation, so pressing or dragging it never closes the card. Only a circle around Bloop
// takes clicks; the rest of the window lets them through. Hold and drag pulls it anywhere in the work area; a while
// after letting go it hops back home. When the card closes it hops home, fades and hides. The window is only hidden.
internal sealed class BloopBuddyWindow : Window
{
    private const double WindowDip = 150;
    private const double BodyDip = 112;
    private const double HitRadiusDip = 64;
    private const double DragThresholdDip = 6;
    private const long HoldMs = 200;
    private const double RestSeconds = 2.5;
    private const double FadeSeconds = 0.45;
    private const double PerchDip = 26;

    private enum Mode { Hidden, Home, Dragging, Resting, Hopping }

    private readonly IntPtr _hwnd;
    private readonly Func<string> _style;
    private readonly Grid _root = new();
    private readonly Grid _body = new() { Width = BodyDip, Height = BodyDip, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Image _avatar = new() { Stretch = Stretch.Uniform, IsHitTestVisible = false };
    private readonly SwapChainPanel _live = new() { Width = BodyDip, Height = BodyDip, IsHitTestVisible = false, Opacity = 0, Visibility = Visibility.Collapsed };
    private readonly MatrixTransform _shape = new();
    private readonly TranslateTransform _drift = new();
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private LiveViewport? _viewport;
    private string _loadedStyle = "";
    private long _loadVersion;

    private Mode _mode = Mode.Hidden;
    private PanelPlacement? _placement;
    private Vector2 _pos, _vel, _home, _restAt;
    private int _sizePx;
    private double _scale = 1;
    private double _last;
    private bool _exiting;
    private double _restUntil;
    private double _fadeStart = -1;
    private float _driftWeight = 1;
    private double _pokeAt = -10;
    private int _shownX = int.MinValue, _shownY = int.MinValue;

    private bool _pressed;
    private long _pressTick;
    private Vector2 _pressCursor, _grab, _dragTarget;

    private Vector2 _hopFrom, _hopTo;
    private double _hopStart, _hopDuration;
    private int _hopCount;

    private Func<Vector2>? _debugCursor;

    public BloopBuddyWindow(IntPtr owner, Func<string> style)
    {
        _style = style;
        Title = "Bloop";
        SystemBackdrop = new ClearBackdrop();

        _body.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _body.RenderTransform = new TransformGroup { Children = { _shape, _drift } };
        _body.HorizontalAlignment = HorizontalAlignment.Center;
        _body.VerticalAlignment = VerticalAlignment.Center;
        _body.Children.Add(_avatar);
        _body.Children.Add(_live);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_body, "Bloop");
        _body.PointerPressed += OnPressed;
        _body.PointerReleased += OnReleased;
        _body.PointerCaptureLost += OnCaptureLost;
        _root.Children.Add(_body);
        Content = _root;

        _hwnd = WindowNative.GetWindowHandle(this);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;
        StripFrame();

        // Owned by the card, so Bloop always stays above it, even when it is dragged over the card.
        SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, owner);
        var noRound = 1; // DWMWCP_DONOTROUND
        DwmSetWindowAttribute(_hwnd, 33, ref noRound, sizeof(int));
        var noBorder = unchecked((int)0xFFFFFFFE);
        DwmSetWindowAttribute(_hwnd, 34, ref noBorder, sizeof(int));

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(15);
        _timer.Tick += (_, _) => TickGuard.Run("bloop-buddy-tick", Tick);
    }

    public bool IsShown => _mode != Mode.Hidden;

    // The card opened or moved. A fresh open starts at home; a reopen during the exit keeps Bloop where it is
    // and sends it home from there.
    public void ShowAt(PanelPlacement placement)
    {
        _placement = placement;
        _scale = placement.Scale;
        _sizePx = (int)Math.Round(WindowDip * _scale);
        _home = HomeFor(placement);
        _exiting = false;
        _fadeStart = -1;
        _root.Opacity = 1;

        if (_mode == Mode.Hidden)
        {
            _pos = _home;
            _vel = Vector2.Zero;
            _mode = Mode.Home;
            _shownX = _shownY = int.MinValue;
            ApplyRegion();
            MoveWindow(force: true);
            AppWindow.Show(false);
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            _last = Now;
            _timer.Start();
            _ = LoadAvatarAsync();
            Log("show");
            return;
        }
        ApplyRegion();
        if (_mode == Mode.Hopping) StartHop();
        Log("reopen");
    }

    // The card moved without closing (its height changed): Home mode springs to the new spot.
    public void Replace(PanelPlacement placement)
    {
        if (_mode == Mode.Hidden) return;
        _placement = placement;
        _home = HomeFor(placement);
    }

    // The card closed: go home if away, then fade out and hide.
    public void Leave()
    {
        if (_mode == Mode.Hidden || _exiting) return;
        _exiting = true;
        Log("leave");
        switch (_mode)
        {
            case Mode.Resting: StartHop(); break;
            case Mode.Home: if (Vector2.Distance(_pos, _home) < 2 * _scale) StartFade(); else StartHop(); break;
        }
    }

    // Hides at once (app exit, or the exit animation finished).
    public void HideNow()
    {
        if (_mode == Mode.Hidden) return;
        _mode = Mode.Hidden;
        _pressed = false;
        _exiting = false;
        _fadeStart = -1;
        _timer.Stop();
        _viewport?.Hide();
        AppWindow.Hide();
        Log("hidden");
    }

    // Drags Bloop by the given physical-pixel offset through the same code a real press, hold and move uses.
    public void DebugDrag(Vector2 by)
    {
        if (_mode == Mode.Hidden) return;
        var start = _pos + new Vector2(_sizePx / 2f);
        var t0 = Now;
        _debugCursor = () =>
        {
            var u = (float)Math.Clamp((Now - t0 - 0.3) / 0.5, 0, 1);
            return start + by * (u * u * (3 - 2 * u));
        };
        Press(start);
        var release = DispatcherQueue.CreateTimer();
        release.IsRepeating = false;
        release.Interval = TimeSpan.FromMilliseconds(1100);
        release.Tick += (_, _) => TickGuard.Run("bloop-debug-release", () =>
        {
            release.Stop();
            Release();
            _debugCursor = null;
        });
        release.Start();
    }

    private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    // Beside the card on the side away from the taskbar, toward the middle of the screen, perched slightly over
    // the card's edge.
    private Vector2 HomeFor(PanelPlacement p)
    {
        var c = p.Card;
        float s = _sizePx;
        var perch = (float)(PerchDip * _scale);
        var workMidX = p.Work.X + p.Work.Width / 2f;
        var workMidY = p.Work.Y + p.Work.Height / 2f;
        var alongX = c.X + c.Width / 2f > workMidX ? c.X + c.Width * 0.25f : c.X + c.Width * 0.75f;
        var alongY = c.Y + c.Height / 2f > workMidY ? c.Y + c.Height * 0.25f : c.Y + c.Height * 0.75f;
        var at = p.Edge switch
        {
            TaskbarEdge.Bottom => new Vector2(alongX - s / 2, c.Y - s + perch),
            TaskbarEdge.Top => new Vector2(alongX - s / 2, c.Y + c.Height - perch),
            TaskbarEdge.Right => new Vector2(c.X - s + perch, alongY - s / 2),
            _ => new Vector2(c.X + c.Width - perch, alongY - s / 2),
        };
        return Clamp(at);
    }

    // Bloop's body stays inside the work area; the clear margin around it may hang over the edge.
    private Vector2 Clamp(Vector2 at)
    {
        if (_placement is not { } p) return at;
        var margin = (float)((WindowDip - BodyDip) / 2 * _scale);
        var x = Math.Clamp(at.X, p.Work.X - margin, Math.Max(p.Work.X - margin, p.Work.X + p.Work.Width - _sizePx + margin));
        var y = Math.Clamp(at.Y, p.Work.Y - margin, Math.Max(p.Work.Y - margin, p.Work.Y + p.Work.Height - _sizePx + margin));
        return new Vector2(x, y);
    }

    private void Tick()
    {
        var now = Now;
        var dt = (float)Math.Clamp(now - _last, 0, 0.05);
        _last = now;
        var stretch = 0f;
        var stretchDir = new Vector2(0, 1);

        if (_pressed && _mode != Mode.Dragging)
        {
            var cursor = Cursor();
            if (Environment.TickCount64 - _pressTick >= HoldMs && Vector2.Distance(cursor, _pressCursor) > DragThresholdDip * _scale)
            {
                _mode = Mode.Dragging;
                _grab = _pressCursor - _pos;
                Log("drag start");
            }
        }

        switch (_mode)
        {
            case Mode.Home:
                Spring(_home, 60, 0.9f, dt);
                break;
            case Mode.Dragging:
            {
                _dragTarget = Clamp(Cursor() - _grab);
                Spring(_dragTarget, 220, 0.55f, dt);
                var lag = _dragTarget - _pos;
                var len = lag.Length();
                if (len > 0.5f)
                {
                    stretchDir = lag / len;
                    stretch = Math.Min(0.18f, len / (_sizePx * 1.2f));
                }
                break;
            }
            case Mode.Resting:
                Spring(_restAt, 220, 0.55f, dt);
                if (now >= _restUntil) StartHop();
                break;
            case Mode.Hopping:
                stretch = HopStep(now);
                break;
        }

        // Idle wander and bob, faded out while dragged or hopping.
        var still = _mode is Mode.Home or Mode.Resting;
        _driftWeight += ((still ? 1f : 0f) - _driftWeight) * Math.Min(1, dt * 4);
        var t = _clock.Elapsed.TotalSeconds;
        _drift.X = _driftWeight * (5 * Math.Sin(0.7 * t) + 2 * Math.Sin(1.9 * t + 1));
        _drift.Y = _driftWeight * (3 * Math.Sin(1.3 * t));

        // A 2D poke: a quick squish that wobbles out (the 3D model wobbles itself).
        var since = now - _pokeAt;
        if (since < 1 && _live.Visibility != Visibility.Visible)
        {
            stretch = (float)(-0.2 * Math.Exp(-6 * since) * Math.Cos(18 * since));
            stretchDir = new Vector2(0, 1);
        }
        SetShape(stretchDir, stretch);

        if (_fadeStart >= 0)
        {
            var op = 1 - (now - _fadeStart) / FadeSeconds;
            if (op <= 0) { HideNow(); return; }
            _root.Opacity = op;
        }
        MoveWindow(force: false);
    }

    private void Spring(Vector2 target, float k, float zeta, float dt)
    {
        var c = 2 * zeta * MathF.Sqrt(k);
        for (var left = dt; left > 0; left -= 1 / 240f)
        {
            var h = Math.Min(left, 1 / 240f);
            _vel += (k * (target - _pos) - c * _vel) * h;
            _pos += _vel * h;
        }
    }

    // A few hops in arcs from wherever Bloop is to home: about 0.5 s close by, up to 1.5 s from far away.
    private void StartHop()
    {
        _hopFrom = _pos;
        _hopTo = _home;
        var dist = Vector2.Distance(_hopFrom, _hopTo) / (float)_scale;
        _hopCount = Math.Clamp((int)Math.Ceiling(dist / 120), 1, 5);
        _hopDuration = Math.Clamp(0.4 + dist / 900, 0.5, 1.5);
        _hopStart = Now;
        _vel = Vector2.Zero;
        _mode = Mode.Hopping;
        Log($"hop x{_hopCount} {_hopDuration:0.00}s");
    }

    private float HopStep(double now)
    {
        _hopTo = _home;
        var u = (now - _hopStart) / _hopDuration;
        if (u >= 1)
        {
            _pos = _hopTo;
            _vel = Vector2.Zero;
            _mode = Mode.Home;
            Log("home");
            if (_exiting) StartFade();
            return 0;
        }
        var k = Math.Min(_hopCount - 1, (int)(u * _hopCount));
        var f = (float)(u * _hopCount - k);
        var a = Vector2.Lerp(_hopFrom, _hopTo, (float)k / _hopCount);
        var b = Vector2.Lerp(_hopFrom, _hopTo, (float)(k + 1) / _hopCount);
        var segment = Vector2.Distance(a, b);
        var height = Math.Clamp(segment * 0.45f, (float)(18 * _scale), (float)(46 * _scale));
        var at = Vector2.Lerp(a, b, f) - new Vector2(0, height * MathF.Sin(MathF.PI * f));
        _pos = Clamp(at);
        // Squash at take-off and landing, stretch in the air.
        return -0.14f * MathF.Cos(2 * MathF.PI * f);
    }

    private void StartFade()
    {
        if (_fadeStart >= 0) return;
        _fadeStart = Now;
        Log("fade");
    }

    // Stretches by (1 + amount) along dir and narrows across it, keeping roughly the same volume.
    private void SetShape(Vector2 dir, float amount)
    {
        var along = 1 + amount;
        var across = 1 - amount * 0.5f;
        var c = dir.X;
        var s = dir.Y;
        _shape.Matrix = new Microsoft.UI.Xaml.Media.Matrix(
            along * c * c + across * s * s, (along - across) * c * s,
            (along - across) * c * s, along * s * s + across * c * c, 0, 0);
    }

    private void MoveWindow(bool force)
    {
        var x = (int)MathF.Round(_pos.X);
        var y = (int)MathF.Round(_pos.Y);
        if (!force && x == _shownX && y == _shownY) return;
        var flags = SWP_NOACTIVATE | (force ? 0 : SWP_NOSIZE);
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, _sizePx, _sizePx, flags);
        _shownX = x;
        _shownY = y;
    }

    // Only a circle around Bloop takes clicks; outside it they reach whatever is underneath.
    private void ApplyRegion()
    {
        var r = (int)Math.Round(HitRadiusDip * _scale);
        var c = _sizePx / 2;
        var region = CreateEllipticRgn(c - r, c - r, c + r, c + r);
        if (SetWindowRgn(_hwnd, region, true) == 0) DeleteObject(region);
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_mode == Mode.Hidden) return;
        _body.CapturePointer(e.Pointer);
        Press(Cursor());
        e.Handled = true;
    }

    private void OnReleased(object sender, PointerRoutedEventArgs e)
    {
        _body.ReleasePointerCapture(e.Pointer);
        Release();
        e.Handled = true;
    }

    private void OnCaptureLost(object sender, PointerRoutedEventArgs e) => Release();

    private void Press(Vector2 cursor)
    {
        _pressed = true;
        _pressTick = Environment.TickCount64;
        _pressCursor = cursor;
    }

    private void Release()
    {
        if (!_pressed) return;
        _pressed = false;
        if (_mode == Mode.Dragging)
        {
            _restAt = _dragTarget;
            _restUntil = Now + (_exiting ? 0 : RestSeconds);
            _mode = Mode.Resting;
            Log("drag end");
            return;
        }
        if (Vector2.Distance(Cursor(), _pressCursor) <= DragThresholdDip * _scale) Poke();
    }

    // A tap goes to the normal interaction: the live model's poke, or the 2D squish.
    private void Poke()
    {
        if (_viewport is not null && _live.Visibility == Visibility.Visible) _viewport.Poke(withClip: true);
        _pokeAt = Now;
    }

    private Vector2 Cursor()
    {
        if (_debugCursor is { } debug) return debug();
        GetCursorPos(out var p);
        return new Vector2(p.X, p.Y);
    }

    private async Task LoadAvatarAsync()
    {
        var style = AppData.Debug3D ? "3d" : _style();
        var live = style == "3d" && !Stage3D.Failed;
        if (style != _loadedStyle || _avatar.Source is null)
        {
            _loadedStyle = style;
            var version = ++_loadVersion;
            _avatar.Opacity = 1;
            var source = await NudgeCardWindow.LoadAvatarAsync(style, "bloop-idle", 256);
            if (version == _loadVersion) _avatar.Source = source;
        }
        if (_mode == Mode.Hidden) return;
        if (!live)
        {
            _live.Visibility = Visibility.Collapsed;
            _avatar.Opacity = 1;
            return;
        }
        _live.Visibility = Visibility.Visible;
        if (_viewport is null)
        {
            _viewport = new LiveViewport(_live);
            _viewport.FirstFrame += () => TickGuard.Run("bloop-buddy-first-frame", () =>
            {
                _live.Opacity = 1;
                _avatar.Opacity = 0;
                Log("3d first frame");
            });
            _viewport.Failed += () => TickGuard.Run("bloop-buddy-3d-failed", () =>
            {
                _live.Visibility = Visibility.Collapsed;
                _live.Opacity = 0;
                _avatar.Opacity = 1;
                Log("3d failed, 2D shown");
            });
        }
        _viewport.Show(GetDpiForWindow(_hwnd) / 96.0);
    }

    private void StripFrame()
    {
        var style = GetWindowLongPtr(_hwnd, GWL_STYLE).ToInt64();
        style &= ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
        SetWindowLongPtr(_hwnd, GWL_STYLE, new IntPtr(style));
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    private void Log(string what)
    {
        if (!AppData.IsDebugRun) return;
        Logger.Log($"bloop buddy {what} at {(int)_pos.X},{(int)_pos.Y} size {_sizePx} home {(int)_home.X},{(int)_home.Y}");
    }

    private const int GWL_STYLE = -16, GWL_EXSTYLE = -20, GWLP_HWNDPARENT = -8;
    private const long WS_CAPTION = 0xC00000, WS_THICKFRAME = 0x40000, WS_SYSMENU = 0x80000, WS_MINIMIZEBOX = 0x20000, WS_MAXIMIZEBOX = 0x10000;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20, SWP_SHOWWINDOW = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr region, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateEllipticRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
}

// A backdrop that is fully clear, so the window shows only what its content draws.
internal sealed class ClearBackdrop : SystemBackdrop
{
    private static Windows.UI.Composition.Compositor? _compositor;
    private static object? _queueController;

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        if (_compositor is null)
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is null)
            {
                var options = new DispatcherQueueOptions { Size = Marshal.SizeOf<DispatcherQueueOptions>(), ThreadType = 2, ApartmentType = 0 };
                CreateDispatcherQueueController(options, out var controller);
                _queueController = controller;
            }
            _compositor = new Windows.UI.Composition.Compositor();
        }
        connectedTarget.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options,
        [MarshalAs(UnmanagedType.IUnknown)] out object controller);
}
