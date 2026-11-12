namespace DeskPal.Core;

// The countdown that hides a reminder card nobody is using. For a timed exercise it starts once the exercise
// countdown is over. Hovering the card or touching Bloop pauses it; when that ends it resumes with what was left,
// but never less than MinResume. Times are milliseconds on any steady clock.
public sealed class NudgeAutoHide
{
    public const long MinResumeMs = 5000;

    private long _hideMs;
    private long _deadline;
    private long _pausedLeft = -1;

    public bool Running { get; private set; }

    public bool Paused => _pausedLeft >= 0;

    public void Start(long now, int hideSeconds, int timedSeconds = 0)
    {
        _hideMs = Math.Max(1, hideSeconds) * 1000L;
        _deadline = now + Math.Max(0, timedSeconds) * 1000L + _hideMs;
        _pausedLeft = -1;
        Running = true;
    }

    public void Stop()
    {
        Running = false;
        _pausedLeft = -1;
    }

    public void Pause(long now)
    {
        if (!Running || Paused) return;
        _pausedLeft = Math.Max(0, _deadline - now);
    }

    public void Resume(long now)
    {
        if (!Running || !Paused) return;
        _deadline = now + Math.Max(_pausedLeft, Math.Min(MinResumeMs, _hideMs));
        _pausedLeft = -1;
    }

    // Time left before the card hides (frozen while paused).
    public long LeftMs(long now) => !Running ? 0 : Paused ? _pausedLeft : Math.Max(0, _deadline - now);

    // Share of the hide time still left, 1 down to 0, for the countdown line (1 while an exercise is still running).
    public double Fraction(long now) => Running ? Math.Clamp(LeftMs(now) / (double)_hideMs, 0, 1) : 0;

    // True once, when the time is up: the caller hides the card.
    public bool Expired(long now)
    {
        if (!Running || Paused || now < _deadline) return false;
        Running = false;
        return true;
    }
}
