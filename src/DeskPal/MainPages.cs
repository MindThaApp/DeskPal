using DeskPal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace DeskPal;

internal static class PageKit
{
    public static ScrollViewer Scroll(StackPanel body)
    {
        body.Padding = new Thickness(32, 24, 32, 32);
        body.Spacing = 16;
        body.MaxWidth = 760;
        body.HorizontalAlignment = HorizontalAlignment.Left;
        return new ScrollViewer { Content = body };
    }

    public static TextBlock Title(string text) => new() { Text = text, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    public static TextBlock Heading(string text) => new() { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };

    public static TextBlock Body(string text, double opacity = 1) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = opacity };

    public static async Task<Image> ImageAsync(string style, string name, int size)
    {
        var image = new Image { Width = size, Height = size };
        image.Source = await NudgeCardWindow.LoadAvatarAsync(style, name, size * 2);
        return image;
    }
}

public sealed class TodayPage : Page
{
    private readonly MainContext _ctx;
    private readonly TextBlock _next = PageKit.Body("");
    private readonly TextBlock _holds = PageKit.Body("", 0.8);
    private readonly TextBlock _stretch = PageKit.Body("");
    private readonly TextBlock _eyes = PageKit.Body("");
    private readonly TextBlock _walks = PageKit.Body("");
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };

    public TodayPage(MainContext ctx)
    {
        _ctx = ctx;
        var history = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var day in Enumerable.Range(0, 7).Select(i => DateTime.Today.AddDays(i - 6)))
        {
            history.Children.Add(new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new Border { Width = 36, Height = 8, CornerRadius = new CornerRadius(4), Opacity = 0.25, Background = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] },
                    new TextBlock { Text = day.ToString("ddd"), FontSize = 11, Opacity = 0.7, HorizontalAlignment = HorizontalAlignment.Center },
                },
            });
        }
        var body = new StackPanel
        {
            Children =
            {
                PageKit.Title("Today"),
                PageKit.Heading("Next up"), _next, _holds,
                PageKit.Heading("Done today"), _stretch, _eyes, _walks,
                PageKit.Heading("Last 7 days"), history, PageKit.Body("History coming soon.", 0.7),
            },
        };
        Content = PageKit.Scroll(body);
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        Unloaded += (_, _) => _timer.Stop();
        _timer.Tick += (_, _) => TickGuard.Run("today-refresh", Refresh);
    }

    private void Refresh()
    {
        var s = _ctx.Scheduler;
        if (s.NextDue is { } n)
        {
            var what = n.Kind switch { NudgeKind.Eyes => "Eye break", NudgeKind.Walk => "Walk", _ => "Stretch" };
            _next.Text = $"{what} at {n.At:HH:mm}";
        }
        else _next.Text = "Nothing scheduled";
        var holds = s.Holds;
        _holds.Text = holds.Count == 0 ? "No holds" : "Holding: " + string.Join(", ", holds.Select(h => h.Reason));
        _stretch.Text = $"Stretches done today: {s.StretchesToday}";
        _eyes.Text = $"Eye breaks today: {s.EyeBreaksToday}";
        _walks.Text = $"Walks today: {s.WalksToday}";
    }
}

public sealed class BloopPage : Page
{
    public BloopPage(MainContext ctx)
    {
        var settings = ctx.Hub.Settings;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        var styles = new (string Key, string Label, string Note)[]
        {
            ("flat", "Flat", "Clean and simple"),
            ("soft3d", "Soft 3D", "Rounded and glossy"),
            ("3d", "3D (beta)", "Live and pokeable, uses the graphics card"),
        };
        var buttons = new List<(string Key, ToggleButton Button)>();
        foreach (var (key, label, note) in styles)
        {
            var preview = new Image { Width = 120, Height = 120 };
            _ = LoadAsync(preview, key);
            var button = new ToggleButton
            {
                Padding = new Thickness(14),
                IsChecked = Current(settings) == key,
                Content = new StackPanel
                {
                    Spacing = 6,
                    Width = 170,
                    Children =
                    {
                        preview,
                        new TextBlock { Text = label, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                        new TextBlock { Text = note, FontSize = 12, Opacity = 0.7, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                    },
                },
            };
            AutomationProperties_SetName(button, label);
            var k = key;
            button.Click += (_, _) =>
            {
                settings.AvatarStyle = k;
                ctx.Hub.Commit(false);
            };
            buttons.Add((key, button));
            row.Children.Add(button);
        }
        ctx.Hub.Changed += () =>
        {
            foreach (var (key, b) in buttons) b.IsChecked = Current(settings) == key;
        };

        Content = PageKit.Scroll(new StackPanel
        {
            Children =
            {
                PageKit.Title("Bloop"),
                PageKit.Heading("Look"), row,
                PageKit.Heading("Play"), PageKit.Body("Click Bloop on the Home page to poke it, and watch its eyes follow your pointer."),
                PageKit.Heading("Bond"), PageKit.Body("Bond level: coming soon.", 0.7),
                PageKit.Heading("Evolution"), PageKit.Body("Evolution: coming soon.", 0.7),
            },
        });
    }

    private static string Current(DeskPalSettings s) => s.AvatarStyle is "soft3d" or "3d" ? s.AvatarStyle : "flat";

    private static void AutomationProperties_SetName(DependencyObject o, string name) =>
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(o, name);

    private static async Task LoadAsync(Image image, string style) =>
        image.Source = await NudgeCardWindow.LoadAvatarAsync(style, "bloop-idle", 240);
}

public sealed class GardenPage : Page
{
    public GardenPage(MainContext ctx)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20 };
        var stages = new (string Asset, string Label)[]
        {
            ("sprout-growth-1-seedling", "Seedling"),
            ("sprout-growth-2-young", "Young"),
            ("sprout-growth-3-bushy", "Bushy"),
            ("sprout-growth-4-bloom", "Bloom"),
        };
        var style = ctx.Hub.Settings.AvatarStyle;
        foreach (var (asset, label) in stages)
        {
            var image = new Image { Width = 120, Height = 120 };
            _ = Load(image, style, asset);
            row.Children.Add(new StackPanel
            {
                Spacing = 6,
                Children = { image, new TextBlock { Text = label, HorizontalAlignment = HorizontalAlignment.Center } },
            });
        }
        Content = PageKit.Scroll(new StackPanel
        {
            Children =
            {
                PageKit.Title("Garden"),
                PageKit.Body("Your garden grows as you look after yourself."),
                new ScrollViewer { Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollMode = ScrollMode.Disabled },
            },
        });
    }

    private static async Task Load(Image image, string style, string asset) =>
        image.Source = await NudgeCardWindow.LoadAvatarAsync(style, asset, 240);
}

public sealed class AboutPage : Page
{
    public AboutPage(MainContext ctx)
    {
        var mode = ctx.Hub.Settings.CrashReports switch { CrashReportingMode.Always => "on", CrashReportingMode.Never => "off", _ => "ask first" };
        var crash = new HyperlinkButton { Content = $"Send crash reports: {mode} (change in Settings)", Padding = new Thickness(0) };
        crash.Click += (_, _) => ctx.Navigate("settings");
        Content = PageKit.Scroll(new StackPanel
        {
            Children =
            {
                PageKit.Title("About Desk Pal"),
                PageKit.Body("Gentle movement and eye-break reminders from Bloop."),
                PageKit.Body("Version " + CrashReporting.ReleaseText, 0.8),
                crash,
            },
        });
    }
}
