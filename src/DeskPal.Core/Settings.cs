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
    public static readonly int[] StretchChoices = [20, 30, 45, 60];

    public int StretchMinutes { get; set; } = 30;
    public int EyesMinutes { get; set; } = 20;
    public int WaterMinutes { get; set; } = 45;
    public WorkingHours WorkingHours { get; set; } = new();
    public string AvatarStyle { get; set; } = "flat";
    public bool UseNudgeCard { get; set; } = true;

    public TimeSpan IntervalFor(NudgeKind kind) => TimeSpan.FromMinutes(kind switch
    {
        NudgeKind.Stretch or NudgeKind.Walk => Array.IndexOf(StretchChoices, StretchMinutes) >= 0 ? StretchMinutes : 30,
        NudgeKind.Eyes => EyesMinutes > 0 ? EyesMinutes : 20,
        NudgeKind.Water => WaterMinutes > 0 ? WaterMinutes : 45,
        _ => 30,
    });
}

public sealed class DeskPalState
{
    public List<Hold> Holds { get; set; } = [];
    public int StretchesCompleted { get; set; }
    public string? LastStretchId { get; set; }
}
