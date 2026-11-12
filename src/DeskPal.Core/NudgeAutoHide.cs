namespace DeskPal.Core;

// The countdown that hides a reminder card nobody is using. It starts as soon as the card appears, timed exercise
// or not. A timed exercise only starts once the user engages (a card button, hovering the card or touching Bloop);
// from then the card stays until the exercise is over, and the hide countdown runs again after it. Hovering the card
// or touching Bloop pauses the countdown; when that ends it resumes with what was left, but never less than
// MinResume. Times are milliseconds on any steady clock.
public sealed class NudgeAutoHide
{
    public const long MinResumeMs = 5000;

    private long _hideMs;
    private long _timedMs;
    private long _deadline;
    private long _pausedLeft = -1;
    private long _exerciseEnd = -1;

    public bool Running { get; private set; }

    public bool Paused => _pausedLeft >= 0;

    // The user engaged with a timed exercise: its own countdown is running or done.
    public bool Engaged => _exerciseEnd >= 0;

    // When the engaged exercise ends (-1 before the user engages).
    public long ExerciseEndMs => _exerciseEnd;

    public void Start(long now, int hideSeconds, int timedSeconds = 0)
    {
        _hideMs = Math.Max(1, hideSeconds) * 1000L;
        _timedMs = Math.Max(0, timedSeconds) * 1000L;
        _deadline = now + _hideMs;
        _pausedLeft = -1;
        _exerciseEnd = -1;
        Running = true;
    }

    // The user engaged with the card. For a timed exercise the first engagement starts the exercise; the card then
    // stays until it ends, plus the hide time. Returns true when this call started the exercise.
    public bool Engage(long now)
    {
        if (!Running || _timedMs <= 0 || Engaged) return false;
        _exerciseEnd = now + _timedMs;
        if (Paused) _pausedLeft = _timedMs + _hideMs;
        else _deadline = _exerciseEnd + _hideMs;
        return true;
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
        _deadline = Engaged && now < _exerciseEnd
            ? _exerciseEnd + _hideMs
            : now + Math.Max(_pausedLeft, Math.Min(MinResumeMs, _hideMs));
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
