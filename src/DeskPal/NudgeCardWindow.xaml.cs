using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using DeskPal.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace DeskPal;

public sealed partial class NudgeCardWindow : Window
{
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

    public NudgeCardWindow(ICommandRouter router)
    {
        _router = router;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _timer.Tick += OnTimer;

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
        UpdateCountdown();

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
    }

    public void HideCard()
    {
        _timer.Stop();
        _nudge = null;
        _loadVersion++;
        AppWindow.Hide();
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
        if (style == "soft3d")
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
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
}
