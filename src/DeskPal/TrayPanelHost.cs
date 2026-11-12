using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace DeskPal;

// Where the tray icon is, in physical screen pixels.
public readonly record struct TrayAnchor(int Left, int Top, int Right, int Bottom)
{
    // The icon's own rect when the shell knows it, else the cursor (the click that opened the panel was on the icon).
    public static TrayAnchor Get(Guid iconId)
    {
        if (iconId != Guid.Empty)
        {
            var id = new NotifyIconIdentifier { CbSize = (uint)Marshal.SizeOf<NotifyIconIdentifier>(), GuidItem = iconId };
            if (Shell_NotifyIconGetRect(ref id, out var r) == 0 && r.Right > r.Left && r.Bottom > r.Top)
                return new TrayAnchor(r.Left, r.Top, r.Right, r.Bottom);
        }
        GetCursorPos(out var p);
        return new TrayAnchor(p.X, p.Y, p.X + 1, p.Y + 1);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public uint CbSize;
        public IntPtr Wnd;
        public uint Id;
        public Guid GuidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out Point point);
}

public enum TaskbarEdge { Bottom, Top, Left, Right }

// The card's rect, its monitor's work area and the tray icon, all in physical pixels, plus the monitor's scale.
public sealed record PanelPlacement(RectInt32 Card, RectInt32 Work, TaskbarEdge Edge, double Scale, RectInt32 Anchor);

// Shared behaviour of the two tray panels: borderless acrylic popup that opens next to the tray icon, takes focus,
// and hides on click outside, on Esc and when told to. The window is created once and only ever hidden.
public sealed class TrayPanelHost
{
    private const int GapDip = 8;

    private readonly Window _window;
    private readonly IntPtr _hwnd;
    private readonly FrameworkElement _root;
    private readonly int _widthDip;
    private TrayAnchor _anchor;
    private bool _activatedOnce;
    private long _deactivatedTick;

    public TrayPanelHost(Window window, FrameworkElement root, int widthDip)
    {
        _window = window;
        _root = root;
        _widthDip = widthDip;
        _hwnd = WindowNative.GetWindowHandle(window);

        var presenter = OverlappedPresenter.Create();
        // A border without a title bar keeps DWM's rounded corners and soft shadow; the hard edge line is then
        // switched off below, so the card has no light outline around it.
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        window.AppWindow.SetPresenter(presenter);
        window.AppWindow.IsShownInSwitchers = false;

        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TOOLWINDOW));
        var round = 2; // DWMWCP_ROUND
        DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        var noBorder = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        DwmSetWindowAttribute(_hwnd, 34 /* DWMWA_BORDER_COLOR */, ref noBorder, sizeof(int));

        window.Activated += (_, e) => TickGuard.Run("tray-panel-activation", () => OnActivated(e.WindowActivationState));
        root.KeyDown += OnKeyDown;
    }

    public bool IsOpen { get; private set; }

    public IntPtr Hwnd => _hwnd;

    // Where the card last went, the work area around it and which edge the taskbar is on.
    public PanelPlacement? Placement { get; private set; }

    public event Action<PanelPlacement>? Placed;

    public event Action? Hidden;

    // A click on the tray icon deactivates an open panel before the icon's own click arrives; that click is the "toggle off".
    public bool JustDeactivated => Environment.TickCount64 - _deactivatedTick < 400;

    public void Show(TrayAnchor anchor)
    {
        _anchor = anchor;
        _activatedOnce = false;
        Layout();
        IsOpen = true;
        _window.AppWindow.Show(true);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        Layout();
        _window.Activate();
        SetForegroundWindow(_hwnd);
    }

    public void Hide()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _window.AppWindow.Hide();
        Hidden?.Invoke();
    }

    // Re-measures the content (for panels whose height changes) and keeps the same edge against the taskbar.
    public void Relayout()
    {
        if (IsOpen) Layout();
    }

    private void OnActivated(WindowActivationState state)
    {
        if (state != WindowActivationState.Deactivated) { _activatedOnce = true; return; }
        if (!IsOpen || !_activatedOnce) return;
        _deactivatedTick = Environment.TickCount64;
        Hide();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape) return;
        e.Handled = true;
        TickGuard.Run("tray-panel-esc", Hide);
    }

    private void Layout()
    {
        var anchor = new Rect { Left = _anchor.Left, Top = _anchor.Top, Right = _anchor.Right, Bottom = _anchor.Bottom };
        var monitor = MonitorFromRect(ref anchor, 2 /* MONITOR_DEFAULTTONEAREST */);
        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        var scale = GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX / 96.0 : 1.0;

        _root.Measure(new Windows.Foundation.Size(_widthDip, double.PositiveInfinity));
        var work = info.Work;
        var gap = (int)Math.Round(GapDip * scale);
        var w = (int)Math.Round(_widthDip * scale);
        var h = Math.Min((int)Math.Ceiling(_root.DesiredSize.Height * scale), work.Bottom - work.Top - 2 * gap);
        var cx = (anchor.Left + anchor.Right) / 2;
        var cy = (anchor.Top + anchor.Bottom) / 2;
        var mon = info.Monitor;

        int x, y;
        TaskbarEdge edge;
        if (work.Bottom < mon.Bottom) edge = TaskbarEdge.Bottom;
        else if (work.Top > mon.Top) edge = TaskbarEdge.Top;
        else if (work.Right < mon.Right) edge = TaskbarEdge.Right;
        else if (work.Left > mon.Left) edge = TaskbarEdge.Left;
        else
        {
            // Auto-hidden taskbar: the whole monitor is work area, so go by the nearest screen edge.
            var dl = cx - mon.Left; var dr = mon.Right - cx; var dt = cy - mon.Top; var db = mon.Bottom - cy;
            var min = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));
            edge = min == db ? TaskbarEdge.Bottom : min == dt ? TaskbarEdge.Top : min == dr ? TaskbarEdge.Right : TaskbarEdge.Left;
        }
        switch (edge)
        {
            case TaskbarEdge.Bottom: x = cx - w / 2; y = work.Bottom - h - gap; break;
            case TaskbarEdge.Top: x = cx - w / 2; y = work.Top + gap; break;
            case TaskbarEdge.Right: x = work.Right - w - gap; y = cy - h / 2; break;
            default: x = work.Left + gap; y = cy - h / 2; break;
        }
        x = Math.Max(work.Left + gap, Math.Min(x, work.Right - w - gap));
        y = Math.Max(work.Top + gap, Math.Min(y, work.Bottom - h - gap));
        _window.AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
        var placement = new PanelPlacement(new RectInt32(x, y, w, h),
            new RectInt32(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), edge, scale,
            new RectInt32(anchor.Left, anchor.Top, anchor.Right - anchor.Left, anchor.Bottom - anchor.Top));
        Placement = placement;
        Placed?.Invoke(placement);
    }

    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x80;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_SHOWWINDOW = 0x40;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int CbSize;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref Rect rect, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hWnd, int attribute, ref int value, int size);
}
