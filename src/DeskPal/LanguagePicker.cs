using DeskPal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace DeskPal;

// The language list: flag, the language's own name, and its English name under it. The user's own language comes first,
// then English, then a divider, then the rest A-Z. Select() never raises Chosen; only a click or key press by the user does.
internal sealed class LanguagePicker : ListView
{
    private readonly List<LanguageInfo?> _rows = [];
    private bool _selecting;
    private string _current;

    public event Action<LanguageInfo>? Chosen;

    public LanguagePicker(string currentTag)
    {
        _current = currentTag;
        SelectionMode = ListViewSelectionMode.Single;
        MaxHeight = 330;
        AutomationProperties.SetName(this, Loc.Get("Language_PickerAccessible"));

        var userTag = Loc.WindowsLanguages().FirstOrDefault();
        var region = Loc.WindowsRegion();
        foreach (var row in LanguageList.Order(SupportedLanguages.All, userTag))
        {
            _rows.Add(row.Language);
            Items.Add(row.Language is null ? Divider() : Item(row.Language, LanguageList.FlagCode(row.Language, userTag, region)));
        }
        Select(currentTag);
        SelectionChanged += (_, _) =>
        {
            if (_selecting || SelectedIndex < 0) return;
            if (_rows[SelectedIndex] is { } chosen && chosen.Tag != _current) Chosen?.Invoke(chosen);
        };
    }

    // Moves the highlight without raising Chosen (also used to snap back after a cancelled switch).
    public void Select(string tag)
    {
        _current = tag;
        _selecting = true;
        try { SelectedIndex = _rows.FindIndex(l => l?.Tag == tag); }
        finally { _selecting = false; }
    }

    private static ListViewItem Divider() => new()
    {
        IsEnabled = false,
        IsTabStop = false,
        MinHeight = 0,
        Padding = new Thickness(0, 4, 0, 4),
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Content = new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"],
        },
    };

    private static ListViewItem Item(LanguageInfo language, string flagCode)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 4, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var flagPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Flags", flagCode + ".png");
        var flag = new Image
        {
            Width = 32,
            Height = 24,
            Stretch = Stretch.UniformToFill,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetAccessibilityView(flag, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        if (File.Exists(flagPath)) flag.Source = new BitmapImage(new Uri(flagPath)) { DecodePixelWidth = 80 };
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = language.NativeName });
        if (language.EnglishName != language.NativeName)
            names.Children.Add(new TextBlock
            {
                Text = language.EnglishName,
                FontSize = 12,
                Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
        Grid.SetColumn(names, 1);
        grid.Children.Add(flag);
        grid.Children.Add(names);
        var item = new ListViewItem { Content = grid };
        AutomationProperties.SetName(item, language.NativeName == language.EnglishName
            ? language.NativeName : language.NativeName + ", " + language.EnglishName);
        return item;
    }
}
