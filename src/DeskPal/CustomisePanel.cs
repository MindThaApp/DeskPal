using DeskPal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace DeskPal;

// The Customise section on the Bloop page (a Pro feature): Bloop's colour, a hat and Sprout's pot, with a preview.
// Everyone can tap every option to see it in the preview. With Pro unlocked a tap saves the choice and every Bloop
// and Sprout follows. Without it the preview changes only, and "Keep this look" saves the choice for later and opens
// the Pro page; the saved look shows once Pro is unlocked and is never cleared when it lapses.
internal sealed partial class CustomisePanel : UserControl
{
    private static readonly BloopColour[] Colours = Enum.GetValues<BloopColour>();
    private static readonly BloopHat[] Hats = Enum.GetValues<BloopHat>();
    private static readonly PotStyle[] Pots = Enum.GetValues<PotStyle>();

    private readonly MainContext _ctx;
    private readonly Image _bloop = new() { Width = 170, Height = 170 };
    private readonly Image _sprout = new() { Width = 104, Height = 104, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly List<(BloopColour Value, ToggleButton Button)> _colourButtons = [];
    private readonly List<(BloopHat Value, ToggleButton Button, Image Picture)> _hatButtons = [];
    private readonly List<(PotStyle Value, ToggleButton Button)> _potButtons = [];
    private readonly TextBlock _note = PageKit.Body("", 0.8);
    private readonly Button _keep = new() { Content = Loc.Get("Custom_Keep") };
    private readonly Button _seePro = new() { Content = Loc.Get("Pro_SeeMore") };
    private CharacterLook _preview = CharacterLook.Default;
    private long _loadVersion;
    private string _hatTileKey = "";

    public CustomisePanel(MainContext ctx)
    {
        _ctx = ctx;
        AutomationProperties.SetName(_bloop, Loc.Get("Custom_PreviewName"));

        var colourRow = Row();
        foreach (var c in Colours)
        {
            var button = Tile(Loc.Get("Custom_Colour_" + c), Swatch(Brush(ColourShift.ForBloop(c), 0x7F, 0xB3, 0xE8)));
            var value = c;
            button.Click += (_, _) => Choose(_preview with { Colour = value }, s => s.Set(value));
            _colourButtons.Add((c, button));
            colourRow.Children.Add(button);
        }

        var hatRow = Row();
        foreach (var h in Hats)
        {
            var picture = new Image { Width = 56, Height = 56 };
            var button = Tile(Loc.Get("Custom_Hat_" + h), picture);
            var value = h;
            button.Click += (_, _) => Choose(_preview with { Hat = value }, s => s.Set(value));
            _hatButtons.Add((h, button, picture));
            hatRow.Children.Add(button);
        }

        var potRow = Row();
        foreach (var p in Pots)
        {
            var button = Tile(Loc.Get("Custom_Pot_" + p), Swatch(Brush(ColourShift.ForPot(p), 0xC9, 0x7B, 0x4A)));
            var value = p;
            button.Click += (_, _) => Choose(_preview with { Pot = value }, s => s.Set(value));
            _potButtons.Add((p, button));
            potRow.Children.Add(button);
        }

        _keep.Click += (_, _) =>
        {
            // Saved now so it is there the moment Pro is unlocked; with Pro locked the default look still shows.
            var keep = _preview;
            Looks.Save(s => { s.Set(keep.Colour); s.Set(keep.Hat); s.Set(keep.Pot); });
            Pro.ShowOffer();
        };
        _seePro.Click += (_, _) => Pro.ShowOffer();

        var preview = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            Child = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _bloop, _sprout } },
        };

        Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { PageKit.Heading(Loc.Get("Custom_Title")), new ProBadge() } },
                PageKit.Body(Loc.Get("Custom_Intro"), 0.8),
                preview,
                PageKit.Heading(Loc.Get("Custom_Colour")), Scroll(colourRow),
                PageKit.Heading(Loc.Get("Custom_Hat")), Scroll(hatRow),
                PageKit.Heading(Loc.Get("Custom_Pot")), Scroll(potRow),
                _note,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _keep, _seePro } },
            },
        };

        Loaded += (_, _) =>
        {
            Pro.Changed -= OnProChanged;
            Pro.Changed += OnProChanged;
            _ctx.Hub.Changed -= OnSettingsChanged;
            _ctx.Hub.Changed += OnSettingsChanged;
            _preview = Looks.Saved;
            Refresh();
        };
        Unloaded += (_, _) =>
        {
            Pro.Changed -= OnProChanged;
            _ctx.Hub.Changed -= OnSettingsChanged;
        };
    }

    private string PictureStyle => _ctx.Hub.Settings.AvatarStyle is "soft3d" or "3d" ? "soft3d" : "flat";
    private string _shownStyle = "";

    private void OnProChanged() => TickGuard.Run("customise-pro", Refresh);

    private void OnSettingsChanged() => TickGuard.Run("customise-settings", () =>
    {
        if (PictureStyle != _shownStyle) Refresh();
    });

    private void Choose(CharacterLook next, Action<LookSettings> save)
    {
        _preview = next;
        if (Pro.IsUnlocked) Looks.Save(save);
        Refresh();
    }

    private void Refresh()
    {
        foreach (var (v, b) in _colourButtons) b.IsChecked = v == _preview.Colour;
        foreach (var (v, b, _) in _hatButtons) b.IsChecked = v == _preview.Hat;
        foreach (var (v, b) in _potButtons) b.IsChecked = v == _preview.Pot;

        var locked = !Pro.IsUnlocked;
        var saved = Looks.Saved;
        _note.Text = locked && !saved.IsDefault && saved == _preview ? Loc.Get("Custom_SavedForPro") : Loc.Get("Custom_Locked");
        _note.Visibility = locked ? Visibility.Visible : Visibility.Collapsed;
        _keep.Visibility = locked && _preview != saved ? Visibility.Visible : Visibility.Collapsed;
        _seePro.Visibility = locked && _keep.Visibility == Visibility.Collapsed ? Visibility.Visible : Visibility.Collapsed;
        _ = LoadPicturesAsync();
    }

    private async Task LoadPicturesAsync()
    {
        var version = ++_loadVersion;
        var style = PictureStyle;
        var look = _preview;
        var bloop = await NudgeCardWindow.LoadAvatarAsync(style, "bloop-idle", 340, look);
        var sprout = await NudgeCardWindow.LoadAvatarAsync(style, "sprout-mood-happy", 208, look);
        if (version != _loadVersion) return;
        _bloop.Source = bloop;
        _sprout.Source = sprout;
        _shownStyle = style;

        // The hat tiles show Bloop in the colour being previewed.
        var tileKey = style + "/" + look.Colour;
        if (tileKey == _hatTileKey) return;
        _hatTileKey = tileKey;
        foreach (var (hat, _, picture) in _hatButtons)
        {
            var source = await NudgeCardWindow.LoadAvatarAsync(style, "bloop-idle", 112, look with { Hat = hat, Pot = PotStyle.Terracotta });
            if (_hatTileKey != tileKey) return;
            picture.Source = source;
        }
    }

    private static StackPanel Row() => new() { Orientation = Orientation.Horizontal, Spacing = 8 };

    private static ScrollViewer Scroll(UIElement row) => new()
    {
        Content = row,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollMode = ScrollMode.Disabled,
    };

    private static ToggleButton Tile(string label, UIElement picture)
    {
        var button = new ToggleButton
        {
            Padding = new Thickness(8),
            Width = 104,
            Content = new StackPanel
            {
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    picture,
                    new TextBlock { Text = label, FontSize = 12, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                },
            },
        };
        AutomationProperties.SetName(button, label);
        return button;
    }

    private static Ellipse Swatch(Brush fill) => new()
    {
        Width = 36,
        Height = 36,
        Margin = new Thickness(0, 10, 0, 10),
        Fill = fill,
        Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, 0, 0, 0)),
        StrokeThickness = 1,
        HorizontalAlignment = HorizontalAlignment.Center,
    };

    private static SolidColorBrush Brush(ColourShift? shift, byte r, byte g, byte b)
    {
        if (shift is not null) (r, g, b) = shift.Apply(r, g, b);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b));
    }
}
