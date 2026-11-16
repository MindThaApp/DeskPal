namespace DeskPal.Core;

public sealed class WorkingHours
{
    public DayOfWeek[] Days { get; set; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
    public TimeOnly Start { get; set; } = new(9, 0);
    public TimeOnly End { get; set; } = new(17, 30);
}

public enum WaterMode { WithBreaks, Hourly, Off }

public sealed class DeskPalSettings
{
    public const int CurrentVersion = 2;

    public const int RecommendedMovementMinutes = 30;
    public const int MovementMin = 15;
    public const int MovementMax = 120;
    public const int RecommendedEyesMinutes = 20;
    public const int RecommendedWalkAfter = 3;
    public const int RecommendedSitStandMinutes = 30;
    public const int HourlyWaterMinutes = 60;
    public static readonly int[] MovementChoices = [20, 30, 45, 60];
    public static readonly int[] EyesChoices = [20, 30];
    public static readonly int[] WalkChoices = [2, 3, 4];
    public static readonly int[] SitStandChoices = [30, 45, 60];
    public const int RecommendedHideNudgeSeconds = 20;
    public static readonly int[] HideNudgeChoices = [10, 20, 30, 60];

    // 0 means the file was written before versions existed.
    public int SettingsVersion { get; set; }

    public bool MovementEnabled { get; set; } = true;
    public int StretchMinutes { get; set; } = RecommendedMovementMinutes;
    public bool EyesEnabled { get; set; } = true;
    public int EyesMinutes { get; set; } = RecommendedEyesMinutes;
    public WaterMode Water { get; set; } = WaterMode.WithBreaks;
    // Completed stretches before a walk replaces the next one; 0 turns the walk off.
    public int WalkAfterStretches { get; set; } = RecommendedWalkAfter;
    public bool SitStandEnabled { get; set; }
    public int SitStandMinutes { get; set; } = RecommendedSitStandMinutes;

    public WorkingHours WorkingHours { get; set; } = new();
    public string AvatarStyle { get; set; } = "flat";
    // BCP-47 tag of the display language; empty follows Windows.
    public string Language { get; set; } = "";
    // Set while a language switch is unconfirmed: the tag to go back to (empty = follow Windows) and when the switch happened.
    // Both null when nothing is pending, which is also how files from before this existed load.
    public string? PendingLanguageRevert { get; set; }
    public DateTimeOffset? PendingLanguageRevertAt { get; set; }
    // The prompt wording in the previous language, captured while that language was still running.
    public LanguagePromptText? PendingLanguageRevertText { get; set; }

    // Growth stage of the 3D Sprout on Home (1 to 4). Fixed at 2 until growth is tracked.
    public int SproutStage { get; set; } = 2;
    public bool UseNudgeCard { get; set; } = true;
    // How long a reminder card stays up with nobody using it before it hides by itself.
    public int HideNudgeSeconds { get; set; } = RecommendedHideNudgeSeconds;
    public bool AnimateTray { get; set; } = true;
    public bool InteractionSounds { get; set; } = true;
    public CrashReportingMode CrashReports { get; set; } = CrashReportingMode.Ask;

    // Automation Portal: both off until the user turns them on.
    public const int MaxApprovedApps = 20;
    public bool ShareEventsWithAutomationPortal { get; set; }
    public bool AllowExternalControl { get; set; }
    // Apps the user allowed once to change Desk Pal over the control pipe: "pkg:<package family>" or a full exe path.
    public List<string> ApprovedControlApps { get; set; } = [];

    public int MovementMinutes => Math.Clamp(StretchMinutes, MovementMin, MovementMax);

    public bool IsScheduled(NudgeKind kind) => kind switch
    {
        NudgeKind.Stretch => MovementEnabled,
        NudgeKind.Eyes => EyesEnabled,
        NudgeKind.Water => Water == WaterMode.Hourly || (Water == WaterMode.WithBreaks && MovementEnabled),
        _ => false,
    };

    public TimeSpan IntervalFor(NudgeKind kind) => TimeSpan.FromMinutes(kind switch
    {
        NudgeKind.Stretch or NudgeKind.Walk => MovementMinutes,
        NudgeKind.Eyes => EyesMinutes == 30 ? 30 : RecommendedEyesMinutes,
        NudgeKind.Water => Water == WaterMode.Hourly ? HourlyWaterMinutes : MovementMinutes,
        _ => RecommendedMovementMinutes,
    });

    // Brings a loaded file up to date and repairs out-of-range values. Returns true when something changed.
    public bool Migrate()
    {
        var changed = false;
        if (SettingsVersion < CurrentVersion)
        {
            // The old default was 40; whether the user chose it cannot be told, so it moves to 30 once.
            if (StretchMinutes == 40) StretchMinutes = RecommendedMovementMinutes;
            SettingsVersion = CurrentVersion;
            changed = true;
        }
        var movement = MovementMinutes;
        if (StretchMinutes != movement) { StretchMinutes = movement; changed = true; }
        if (Array.IndexOf(EyesChoices, EyesMinutes) < 0) { EyesMinutes = RecommendedEyesMinutes; changed = true; }
        if (WalkAfterStretches != 0 && Array.IndexOf(WalkChoices, WalkAfterStretches) < 0) { WalkAfterStretches = RecommendedWalkAfter; changed = true; }
        if (Array.IndexOf(SitStandChoices, SitStandMinutes) < 0) { SitStandMinutes = RecommendedSitStandMinutes; changed = true; }
        if (Array.IndexOf(HideNudgeChoices, HideNudgeSeconds) < 0) { HideNudgeSeconds = RecommendedHideNudgeSeconds; changed = true; }
        if (!Enum.IsDefined(Water)) { Water = WaterMode.WithBreaks; changed = true; }
        if (Language is null) { Language = ""; changed = true; }
        var apps = (ApprovedControlApps ?? [])
            .Where(a => !string.IsNullOrWhiteSpace(a) && a.Length <= 1024)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxApprovedApps)
            .ToList();
        if (ApprovedControlApps is null || apps.Count != ApprovedControlApps.Count) { ApprovedControlApps = apps; changed = true; }
        return changed;
    }

    public void ResetRemindersToRecommended()
    {
        MovementEnabled = true;
        StretchMinutes = RecommendedMovementMinutes;
        EyesEnabled = true;
        EyesMinutes = RecommendedEyesMinutes;
        Water = WaterMode.WithBreaks;
        WalkAfterStretches = RecommendedWalkAfter;
        SitStandEnabled = false;
        SitStandMinutes = RecommendedSitStandMinutes;
    }
}

public sealed class DeskPalState
{
    public List<Hold> Holds { get; set; } = [];
    public int StretchesCompleted { get; set; }
    public string? LastStretchId { get; set; }
    public DateOnly CountersDate { get; set; }
    public int StretchesToday { get; set; }
    public int EyeBreaksToday { get; set; }
    public int WalksToday { get; set; }
    // Total care points earned (see NudgeReward); files from before this existed load as 0.
    public int CarePoints { get; set; }
    public MainWindowUi Ui { get; set; } = new();
}

// Main window bits that live in state.json next to the counters; the app copies them onto every snapshot it saves.
public sealed class MainWindowUi
{
    public bool FirstRunShown { get; set; }
    public bool CloseHintShown { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
