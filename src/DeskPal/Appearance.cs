using DeskPal.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI.ViewManagement;

namespace DeskPal;

// Applies Settings > Appearance to the app chrome. Pet art is content and is never touched here.
// Windows register once (after their content exists); Apply() re-reads the settings and updates every live window.
internal static class Appearance
{
    // What a window may do with Material: Chrome follows Mica / Acrylic / Solid; Surface (pop-ups, cards) keeps its own
    // translucent look and only changes for Solid.
    internal enum Role { Chrome, Surface }

    private sealed class Entry(Window window, FrameworkElement root, Role role)
    {
        public readonly WeakReference<Window> Window = new(window);
        public readonly WeakReference<FrameworkElement> Root = new(root);
        public readonly Role Role = role;
        public readonly SystemBackdrop? OriginalBackdrop = window.SystemBackdrop;
        public readonly Brush? OriginalBackground = root is Control c ? c.Background : root is Panel p ? p.Background : null;
    }

    private static readonly List<Entry> Entries = [];
    private static readonly List<WeakReference<TextBlock>> Secondary = [];
    private static DeskPalSettings? _settings;
    private static AppearanceSettings? _applied;
    private static bool? _appliedDark;

    private static AppearanceSettings Current => _settings?.Appearance ?? new AppearanceSettings();

    // Fired after Apply so pages can restyle their own cards.
    public static event Action? Applied;

    public static void Attach(DeskPalSettings settings)
    {
        _settings = settings;
        Motion.UserMode = settings.Appearance.Motion;
    }

    public static void Register(Window window, FrameworkElement root, Role role)
    {
        lock (Entries) Entries.Add(new Entry(window, root, role));
        Apply(force: true);
    }

    public static void TrackSecondary(TextBlock t)
    {
        lock (Secondary) Secondary.Add(new WeakReference<TextBlock>(t));
        t.FontSize = SmallSize;
    }

    public static double BaseSize => 14 + Current.TextStep;
    public static double SmallSize => 12 + Current.TextStep;

    public static CornerRadius CardRadius => Current.Corners switch
    {
        CornerChoice.Square => new CornerRadius(0),
        CornerChoice.ExtraRound => new CornerRadius(12),
        _ => new CornerRadius(8),
    };

    private static CornerRadius ControlRadius => Current.Corners switch
    {
        CornerChoice.Square => new CornerRadius(0),
        CornerChoice.ExtraRound => new CornerRadius(8),
        _ => new CornerRadius(4),
    };

    public static Thickness CardPadding => Current.Density switch
    {
        DensityChoice.Comfortable => new Thickness(20, 16, 20, 16),
        DensityChoice.Compact => new Thickness(12, 6, 12, 6),
        _ => new Thickness(16, 12, 16, 12),
    };

    public static double RowSpacing => Current.Density switch
    {
        DensityChoice.Comfortable => 12,
        DensityChoice.Compact => 4,
        _ => 8,
    };

    private static bool HighContrast()
    {
        try { return new AccessibilitySettings().HighContrast; } catch { return false; }
    }

    private static bool SystemIsDark()
    {
        try
        {
            var c = new UISettings().GetColorValue(UIColorType.Background);
            return c.R + c.G + c.B < 384;
        }
        catch { return false; }
    }

    private static bool EffectiveDark(AppearanceSettings a) =>
        a.Theme == ThemeChoice.Dark || (a.Theme == ThemeChoice.System && SystemIsDark());

    public static void Apply() => Apply(force: false);

    private static void Apply(bool force)
    {
        try
        {
            var a = Current;
            var hc = HighContrast();
            var dark = EffectiveDark(a);
            var themeNeedsRefresh = force || _applied is null || _applied.Accent != a.Accent || _applied.Corners != a.Corners
                || _appliedDark != dark;

            Motion.UserMode = a.Motion;
            if (themeNeedsRefresh) ApplyResources(a, hc, dark);

            List<Entry> live;
            lock (Entries)
            {
                Entries.RemoveAll(e => !e.Window.TryGetTarget(out _) || !e.Root.TryGetTarget(out _));
                live = [.. Entries];
            }
            foreach (var e in live)
            {
                if (!e.Window.TryGetTarget(out var w) || !e.Root.TryGetTarget(out var root)) continue;
                ApplyWindow(e, w, root, a, hc, themeNeedsRefresh);
            }

            lock (Secondary)
            {
                Secondary.RemoveAll(r => !r.TryGetTarget(out _));
                foreach (var r in Secondary) if (r.TryGetTarget(out var t)) t.FontSize = SmallSize;
            }

            _applied = new AppearanceSettings
            {
                Theme = a.Theme, Accent = a.Accent, Material = a.Material, Corners = a.Corners,
                Density = a.Density, TextStep = a.TextStep, Motion = a.Motion,
            };
            _appliedDark = dark;
            Applied?.Invoke();
        }
        catch (Exception ex) { Logger.Log("appearance apply failed: " + ex.Message); }
    }

    private static void ApplyWindow(Entry e, Window w, FrameworkElement root, AppearanceSettings a, bool hc, bool refresh)
    {
        var wanted = a.Theme switch
        {
            ThemeChoice.Light => ElementTheme.Light,
            ThemeChoice.Dark => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        if (refresh && root.RequestedTheme == wanted && root.IsLoaded)
        {
            // Theme-bound brushes and radii only re-read their resources when the theme changes, so nudge it and put it back.
            root.RequestedTheme = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
        }
        root.RequestedTheme = wanted;

        var solid = hc || a.Material == MaterialChoice.Solid;
        if (solid)
        {
            if (w.SystemBackdrop is not null) w.SystemBackdrop = null;
            SetBackground(root, (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"]);
        }
        else if (e.Role == Role.Chrome)
        {
            SetBackground(root, e.OriginalBackground);
            var wantAcrylic = a.Material == MaterialChoice.Acrylic;
            if (wantAcrylic && w.SystemBackdrop is not DesktopAcrylicBackdrop) w.SystemBackdrop = new DesktopAcrylicBackdrop();
            else if (!wantAcrylic && w.SystemBackdrop is not MicaBackdrop) w.SystemBackdrop = new MicaBackdrop();
        }
        else
        {
            SetBackground(root, e.OriginalBackground);
            if (w.SystemBackdrop is null) w.SystemBackdrop = e.OriginalBackdrop;
        }

        if (root is Control c) c.FontSize = BaseSize;
    }

    private static void SetBackground(FrameworkElement root, Brush? brush)
    {
        if (root is Control c) c.Background = brush;
        else if (root is Panel p) p.Background = brush;
    }

    private static readonly Dictionary<AccentChoice, (string Light, string Dark)> Palette = new()
    {
        [AccentChoice.Blue] = ("0F6CBD", "479EF5"),
        [AccentChoice.Teal] = ("0B7A75", "3CC4BC"),
        [AccentChoice.Violet] = ("6B4FBB", "A58BF0"),
        [AccentChoice.Rose] = ("B4325A", "F07A9C"),
        [AccentChoice.Orange] = ("B5530F", "F2A15B"),
    };

    private static readonly string[] AccentKeys =
    [
        "SystemAccentColor", "SystemAccentColorLight1", "SystemAccentColorLight2", "SystemAccentColorLight3",
        "SystemAccentColorDark1", "SystemAccentColorDark2", "SystemAccentColorDark3",
        "AccentFillColorDefaultBrush", "AccentFillColorSecondaryBrush", "AccentFillColorTertiaryBrush",
        "AccentTextFillColorPrimaryBrush", "AccentTextFillColorSecondaryBrush", "AccentTextFillColorTertiaryBrush",
    ];

    private static Windows.UI.Color Hex(string rgb, byte alpha = 255) =>
        Windows.UI.Color.FromArgb(alpha, Convert.ToByte(rgb[..2], 16), Convert.ToByte(rgb[2..4], 16), Convert.ToByte(rgb[4..6], 16));

    private static void ApplyResources(AppearanceSettings a, bool hc, bool dark)
    {
        var res = Application.Current.Resources;
        // Windows accent (and the high-contrast palette) is the framework's own, so the overrides are simply removed.
        foreach (var k in AccentKeys) res.Remove(k);
        if (a.Accent != AccentChoice.Windows && !hc && Palette.TryGetValue(a.Accent, out var p))
        {
            // The light theme uses the darker shade so white text on accent fills keeps 4.5:1; the dark theme uses the lighter one.
            var fill = Hex(dark ? p.Dark : p.Light);
            res["SystemAccentColor"] = fill;
            res["SystemAccentColorLight1"] = res["SystemAccentColorLight2"] = res["SystemAccentColorLight3"] = Hex(p.Dark);
            res["SystemAccentColorDark1"] = res["SystemAccentColorDark2"] = res["SystemAccentColorDark3"] = Hex(p.Light);
            res["AccentFillColorDefaultBrush"] = new SolidColorBrush(fill);
            res["AccentFillColorSecondaryBrush"] = new SolidColorBrush(Hex(dark ? p.Dark : p.Light, 230));
            res["AccentFillColorTertiaryBrush"] = new SolidColorBrush(Hex(dark ? p.Dark : p.Light, 204));
            res["AccentTextFillColorPrimaryBrush"] = new SolidColorBrush(Hex(dark ? p.Dark : p.Light));
            res["AccentTextFillColorSecondaryBrush"] = new SolidColorBrush(Hex(dark ? p.Dark : p.Light));
            res["AccentTextFillColorTertiaryBrush"] = new SolidColorBrush(Hex(dark ? p.Dark : p.Light, 204));
        }
        res["ControlCornerRadius"] = ControlRadius;
        res["OverlayCornerRadius"] = CardRadius;
    }
}
