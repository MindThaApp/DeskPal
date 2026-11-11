using System.Runtime.InteropServices;
using DeskPal.Core;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Windows.Graphics;
using WinRT.Interop;

namespace DeskPal;

public sealed partial class TodayWindow : Window
{
    private const int WidthDip = 300;
    private const int HeightDip = 250;
    private const int MarginDip = 12;

    private readonly NudgeScheduler _scheduler;
    private readonly IntPtr _hwnd;
    private long _hiddenTick;
    private bool _visible;

    public TodayWindow(NudgeScheduler scheduler)
    {
        _scheduler = scheduler;
        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
        AppWindow.IsShownInSwitchers = false;

        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        var round = 2;
        DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated) HideToday();
        };
    }

    // Clicking the tray icon deactivates the window first; this stops that click from reopening it at once.
    public void Toggle()
    {
        if (_visible)
        {
            HideToday();
            return;
        }
        if (Environment.TickCount64 - _hiddenTick < 400) return;
        Refresh();
        var scale = GetDpiForWindow(_hwnd) / 96.0;
        var w = (int)(WidthDip * scale);
        var h = (int)(HeightDip * scale);
        var margin = (int)(MarginDip * scale);
        var work = DisplayArea.Primary.WorkArea;
        AppWindow.MoveAndResize(new RectInt32(work.X + work.Width - w - margin, work.Y + work.Height - h - margin, w, h));
        _visible = true;
        AppWindow.Show(true);
        Activate();
    }

    public void HideToday()
    {
        if (!_visible) return;
        _visible = false;
        _hiddenTick = Environment.TickCount64;
        AppWindow.Hide();
    }

    private void Refresh()
    {
        if (_scheduler.NextDue is { } next)
        {
            var what = next.Kind switch
            {
                NudgeKind.Eyes => "Eye break",
                NudgeKind.Walk => "Walk",
                _ => "Stretch",
            };
            NextText.Text = $"Next: {what} at {next.At:HH:mm}";
        }
        else NextText.Text = "Next: nothing scheduled";
        StretchText.Text = $"Stretches done today: {_scheduler.StretchesToday}";
        EyesText.Text = $"Eye breaks today: {_scheduler.EyeBreaksToday}";
        var holds = _scheduler.Holds;
        HoldsText.Text = holds.Count == 0 ? "No holds" : "Holding: " + string.Join(", ", holds.Select(h => h.Reason));
        PauseButton.Content = _scheduler.ManualHold is null ? "Pause 30 min" : "Resume";
    }

    private void OnPause(object sender, RoutedEventArgs e)
    {
        _scheduler.Execute(_scheduler.ManualHold is null ? new PauseCommand(Minutes: 30) : new ResumeCommand());
        Refresh();
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
}
