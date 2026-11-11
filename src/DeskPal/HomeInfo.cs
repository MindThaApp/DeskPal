using DeskPal.Core;

namespace DeskPal;

// The greeting, next-up and hold wording shared by the Home page and the tray panels.
public static class HomeInfo
{
    public static string Greeting(int hour) => hour switch
    {
        < 5 => "Still up? Be kind to your eyes tonight.",
        < 12 => "Morning! Ready for a good day?",
        < 17 => "Afternoon! A little stretch will feel good.",
        < 22 => "Evening! You did well today.",
        _ => "Late one. Time to wind down soon?",
    };

    public static string NextUp(NudgeScheduler s)
    {
        if (s.NextDue is not { } next) return "Nothing scheduled";
        var what = next.Kind switch { NudgeKind.Eyes => "Eye break", NudgeKind.Walk => "Walk", _ => "Stretch" };
        return $"{what} at {next.At:HH:mm}";
    }

    public static string HoldsLine(NudgeScheduler s)
    {
        var holds = s.Holds;
        return holds.Count == 0 ? "No holds" : "Holding: " + string.Join(", ", holds.Select(x => x.Reason));
    }

    public static string CountsLine(NudgeScheduler s) =>
        $"Stretches {s.StretchesToday}   Eye breaks {s.EyeBreaksToday}   Walks {s.WalksToday}";

    // The single line the quick panel leads with: the pause, else a hold, else what is next.
    public static string Status(NudgeScheduler s, DateTimeOffset now)
    {
        if (s.ManualHold is { } manual)
        {
            var when = manual.Until.Date == now.Date ? manual.Until.ToString("HH:mm") : "tomorrow " + manual.Until.ToString("HH:mm");
            return $"Paused until {when}";
        }
        if (s.Holds.FirstOrDefault() is { } hold) return $"Held: {hold.Reason}";
        return s.NextDue is null ? "Nothing scheduled" : "Next: " + NextUp(s);
    }
}
