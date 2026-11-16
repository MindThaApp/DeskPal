using DeskPal.Core;

namespace DeskPal;

// The greeting, next-up and hold wording shared by the Home page and the tray panels.
public static class HomeInfo
{
    public static string Greeting(int hour) => hour switch
    {
        < 5 => Loc.Get("Home_Greeting_Night"),
        < 12 => Loc.Get("Home_Greeting_Morning"),
        < 17 => Loc.Get("Home_Greeting_Afternoon"),
        < 22 => Loc.Get("Home_Greeting_Evening"),
        _ => Loc.Get("Home_Greeting_Late"),
    };

    // Lower-case name of a break kind for use inside a sentence.
    public static string KindName(NudgeKind kind) => Loc.Get(kind switch
    {
        NudgeKind.Eyes => "Tray_Kind_Eyes",
        NudgeKind.Walk => "Tray_Kind_Walk",
        NudgeKind.Water => "Tray_Kind_Water",
        NudgeKind.Breathe => "Tray_Kind_Breathe",
        _ => "Tray_Kind_Stretch",
    });

    public static string NextUp(NudgeScheduler s)
    {
        if (s.NextDue is not { } next) return Loc.Get("Home_NextNothing");
        var what = next.Kind switch { NudgeKind.Eyes => Loc.Get("Home_Kind_Eyes"), NudgeKind.Walk => Loc.Get("Home_Kind_Walk"), _ => Loc.Get("Home_Kind_Stretch") };
        return Loc.Format("Home_NextAt", what, next.At.ToString("HH:mm"));
    }

    public static string HoldsLine(NudgeScheduler s)
    {
        var holds = s.Holds;
        return holds.Count == 0 ? Loc.Get("Home_NoHolds") : Loc.Format("Home_Holding", string.Join(", ", holds.Select(x => x.Reason)));
    }

    public static string CountsLine(NudgeScheduler s) =>
        Loc.Format("Home_Counts", s.StretchesToday, s.EyeBreaksToday, s.WalksToday);

    // The single line the quick panel leads with: the pause, else a hold, else what is next.
    public static string Status(NudgeScheduler s, DateTimeOffset now)
    {
        if (s.ManualHold is { } manual)
        {
            var when = manual.Until.Date == now.Date ? manual.Until.ToString("HH:mm") : Loc.Format("Home_Tomorrow", manual.Until.ToString("HH:mm"));
            return Loc.Format("Home_PausedUntil", when);
        }
        if (s.Holds.FirstOrDefault() is { } hold) return Loc.Format("Home_Held", hold.Reason);
        return s.NextDue is null ? Loc.Get("Home_NextNothing") : Loc.Format("Home_NextLine", NextUp(s));
    }
}
