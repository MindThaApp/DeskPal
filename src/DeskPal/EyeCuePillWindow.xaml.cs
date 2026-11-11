using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskPal;

public sealed partial class EyeCuePillWindow : Window
{
    private const int WidthDip = 220;
    private const int HeightDip = 56;
    private const int MarginDip = 12;
    private const int Seconds = 20;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly IntPtr _hwnd;
    private long _endTick;
    private bool _active;
    private bool _fading;

    public EyeCuePillWindow()
    {
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _timer.Tick += (_, _) => OnTimer();

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        // Tool window plus no-activate: no taskbar button and it never takes focus.
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
        var round = 3; // DWMWCP_ROUNDSMALL
        DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
    }

    // True when the user clicked it, false when it faded out on its own.
    public event Action<bool>? Finished;

    public void ShowCue()
    {
        _active = true;
        _fading = false;
        _endTick = Environment.TickCount64 + Seconds * 1000L;
        Update();
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var w = (int)(WidthDip * scale);
        var h = (int)(HeightDip * scale);
        var margin = (int)(MarginDip * scale);
        var work = DisplayArea.Primary.WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - w - margin, work.Y + work.Height - h - margin, w, h));
        Root.Opacity = 1;
        AppWindow.Show(false);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        _timer.Start();
    }

    public void Dismiss()
    {
        if (!_active) return;
        _active = false;
        _timer.Stop();
        AppWindow.Hide();
    }

    private void OnPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!_active) return;
        Dismiss();
        Finished?.Invoke(true);
    }

    private void OnTimer()
    {
        if (!_active || _fading) return;
        Update();
        if (Environment.TickCount64 < _endTick) return;
        _fading = true;
        var fade = new DoubleAnimation { From = 1, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(700)) };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(fade);
        sb.Completed += (_, _) =>
        {
            if (!_active) return;
            Dismiss();
            Finished?.Invoke(false);
        };
        sb.Begin();
    }

    private void Update()
    {
        var leftMs = Math.Max(0, _endTick - Environment.TickCount64);
        LabelText.Text = $"Look 20 ft away · {(int)Math.Ceiling(leftMs / 1000.0)}s";
        Bar.Value = 100.0 * leftMs / (Seconds * 1000.0);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private const long WS_EX_NOACTIVATE = 0x08000000;
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
