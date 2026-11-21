using System.Drawing;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;

namespace DeskPal;

public sealed class TrayAnimator
{
    private readonly TaskbarIcon _tray;
    private readonly TrayIcons _icons;
    private readonly Func<bool> _animate;
    private readonly Func<bool> _sleeping;
    private readonly Func<bool> _held;
    private readonly DispatcherQueueTimer _timer;
    private readonly Random _rng = new();
    private string? _loop;
    private int _frame;
    private long _endTick;
    private bool _blinking;
    private bool _force;
    private Icon? _shown;

    public TrayAnimator(DispatcherQueue dq, TaskbarIcon tray, TrayIcons icons,
        Func<bool> animate, Func<bool> sleeping, Func<bool> held)
    {
        _tray = tray;
        _icons = icons;
        _animate = animate;
        _sleeping = sleeping;
        _held = held;
        _timer = dq.CreateTimer();
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => TickGuard.Run("tray-animator", OnTick);
    }

    public void Stop()
    {
        _timer.Stop();
        _loop = null;
        ShowRest();
    }

    public void Play(string loop, int durationMs, bool force = false)
    {
        if (!force && (!_animate() || _held() || !Motion.Enabled)) return;
        var frames = _icons.Get(loop);
        var info = _icons.Info(loop);
        if (frames is null || info is null || frames.Length == 0) return;
        _timer.Stop();
        _loop = loop;
        _force = force;
        _frame = 0;
        _blinking = false;
        _endTick = Environment.TickCount64 + durationMs;
        Show(frames[0]);
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(30, info.MsFor(0)));
        _timer.Start();
    }

    // Called from the periodic tray refresh so the resting icon follows working hours and pauses.
    public void Refresh()
    {
        if (_loop is null) ShowRest();
    }

    private void OnTick()
    {
        if (_loop is not null)
        {
            if (Environment.TickCount64 >= _endTick || (!_force && (!_animate() || _held())))
            {
                Stop();
                return;
            }
            var frames = _icons.Get(_loop);
            var info = _icons.Info(_loop);
            if (frames is null || info is null) { Stop(); return; }
            _frame = (_frame + 1) % frames.Length;
            Show(frames[_frame]);
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(30, info.MsFor(_frame)));
            _timer.Start();
            return;
        }
        if (_blinking)
        {
            _blinking = false;
            ShowRest();
            return;
        }
        BlinkOrWait();
    }

    private void ShowRest()
    {
        var rest = _sleeping() ? _icons.Get("sleep") : _icons.Get("idle-blink");
        if (rest is { Length: > 0 }) Show(rest[0]);
        if (_loop is not null) return;
        _timer.Stop();
        if (_animate() && !_sleeping() && !_held()) ScheduleBlink();
    }

    private void ScheduleBlink()
    {
        _timer.Interval = TimeSpan.FromMilliseconds(_rng.Next(6000, 10001));
        _timer.Start();
    }

    private void BlinkOrWait()
    {
        if (!_animate() || _sleeping() || _held()) { ShowRest(); return; }
        var blink = _icons.Get("idle-blink");
        var info = _icons.Info("idle-blink");
        if (blink is not { Length: > 1 } || info is null) { ScheduleBlink(); return; }
        _blinking = true;
        Show(blink[1]);
        _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(30, info.MsFor(1)));
        _timer.Start();
    }

    private void Show(Icon icon)
    {
        if (ReferenceEquals(_shown, icon)) return;
        // Assigning TaskbarIcon.Icon disposes the previous Icon, so swap the shown handle instead.
        try
        {
            _tray.UpdateIcon(icon);
            _shown = icon;
        }
        catch (Exception ex) { Logger.Log("tray icon swap failed: " + ex.Message); }
    }
}

