namespace DeskPal.Core;

public enum ThemeChoice { System, Light, Dark }
public enum AccentChoice { Windows, Blue, Teal, Violet, Rose, Orange }
public enum MaterialChoice { Mica, Acrylic, Solid }
public enum CornerChoice { Rounded, Square, ExtraRound }
public enum DensityChoice { Standard, Comfortable, Compact }
public enum MotionChoice { FollowWindows, Reduced, Off }

// Look and feel of the app chrome (windows, settings, panels, cards, tray pop-up). Pet art is content and is not touched.
// Every default equals the look before this section existed. Missing fields in older files load as these defaults.
public sealed class AppearanceSettings
{
    public const int TextStepMin = -1;
    public const int TextStepMax = 3;

    public ThemeChoice Theme { get; set; } = ThemeChoice.System;
    public AccentChoice Accent { get; set; } = AccentChoice.Windows;
    public MaterialChoice Material { get; set; } = MaterialChoice.Mica;
    public CornerChoice Corners { get; set; } = CornerChoice.Rounded;
    public DensityChoice Density { get; set; } = DensityChoice.Standard;
    // In-app text size step on top of Windows text scaling; each step is +1 px on the base size.
    public int TextStep { get; set; }
    public MotionChoice Motion { get; set; } = MotionChoice.FollowWindows;

    public bool IsDefault =>
        Theme == ThemeChoice.System && Accent == AccentChoice.Windows && Material == MaterialChoice.Mica
        && Corners == CornerChoice.Rounded && Density == DensityChoice.Standard && TextStep == 0
        && Motion == MotionChoice.FollowWindows;

    // Repairs out-of-range values from a hand-edited file. Returns true when something changed.
    public bool Clamp()
    {
        var changed = false;
        if (!Enum.IsDefined(Theme)) { Theme = ThemeChoice.System; changed = true; }
        if (!Enum.IsDefined(Accent)) { Accent = AccentChoice.Windows; changed = true; }
        if (!Enum.IsDefined(Material)) { Material = MaterialChoice.Mica; changed = true; }
        if (!Enum.IsDefined(Corners)) { Corners = CornerChoice.Rounded; changed = true; }
        if (!Enum.IsDefined(Density)) { Density = DensityChoice.Standard; changed = true; }
        if (!Enum.IsDefined(Motion)) { Motion = MotionChoice.FollowWindows; changed = true; }
        var step = Math.Clamp(TextStep, TextStepMin, TextStepMax);
        if (step != TextStep) { TextStep = step; changed = true; }
        return changed;
    }

    // Appearance only: nothing outside this object is touched.
    public void Reset()
    {
        Theme = ThemeChoice.System;
        Accent = AccentChoice.Windows;
        Material = MaterialChoice.Mica;
        Corners = CornerChoice.Rounded;
        Density = DensityChoice.Standard;
        TextStep = 0;
        Motion = MotionChoice.FollowWindows;
    }
}
