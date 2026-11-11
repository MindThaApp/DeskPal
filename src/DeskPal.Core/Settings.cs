namespace DeskPal.Core;

public sealed class WorkingHours
{
    public DayOfWeek[] Days { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public TimeOnly Start { get; set; } = new(9, 0);
    public TimeOnly End { get; set; } = new(17, 30);
}

public sealed class DeskPalSettings
{
    public const int GridMinutes = 20;
    public static readonly int[] StretchChoices = [40, 60];

    public int StretchMinutes { get; set; } = 40;
    public int WaterMinutes { get; set; } = 40;
    public WorkingHours WorkingHours { get; set; } = new();
    public string AvatarStyle { get; set; } = "flat";
    public bool UseNudgeCard { get; set; } = true;

    public TimeSpan IntervalFor(NudgeKind kind) => TimeSpan.FromMinutes(kind switch
    {
        NudgeKind.Stretch or NudgeKind.Walk => Array.IndexOf(StretchChoices, StretchMinutes) >= 0 ? StretchMinutes : 40,
        NudgeKind.Eyes => GridMinutes,
        NudgeKind.Water => Math.Max(GridMinutes, WaterMinutes / GridMinutes * GridMinutes),
        _ => 40,
    });
}

public sealed class DeskPalState
{
    public List<Hold> Holds { get; set; } = [];
    public int StretchesCompleted { get; set; }
    public string? LastStretchId { get; set; }
    public DateOnly CountersDate { get; set; }
    public int StretchesToday { get; set; }
    public int EyeBreaksToday { get; set; }
}
