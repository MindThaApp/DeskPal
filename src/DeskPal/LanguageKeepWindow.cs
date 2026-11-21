using DeskPal.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace DeskPal;

// Shown after a language switch. The text is in the new language with the previous language under it, so someone who
// cannot read the new one can still find the way back. Closing the window counts as going back.
internal sealed class LanguageKeepWindow : Window
{
    private readonly DeskPalSettings _settings;
    private readonly LanguagePromptText? _previous;
    private readonly Action _keep;
    private readonly Action _goBack;
    private readonly TextBlock _newCountdown;
    private readonly TextBlock _oldCountdown;
    private readonly DispatcherQueueTimer _timer;
    private bool _done;

    public LanguageKeepWindow(DeskPalSettings settings, Action keep, Action goBack)
    {
        _settings = settings;
        _previous = settings.PendingLanguageRevertText;
        _keep = keep;
        _goBack = goBack;
        var newTag = string.IsNullOrEmpty(settings.Language) ? null : settings.Language;
        var newName = SupportedLanguages.All.FirstOrDefault(l => l.Tag == newTag)?.NativeName ?? settings.Language ?? "";

        Title = Loc.Get("Language_Keep_Title");
        SystemBackdrop = new MicaBackdrop();

        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var panel = new StackPanel { Spacing = 6, Padding = new Thickness(28, 24, 28, 24) };
        panel.Children.Add(new TextBlock { Text = Loc.Get("Language_Keep_Title"), Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = Loc.Format("Language_Keep_Body", newName), TextWrapping = TextWrapping.Wrap });
        _newCountdown = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(_newCountdown);

        if (_previous is not null)
        {
            panel.Children.Add(new Border { Height = 1, Margin = new Thickness(0, 10, 0, 10), Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"] });
            panel.Children.Add(new TextBlock { Text = _previous.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Foreground = secondary });
            panel.Children.Add(new TextBlock { Text = _previous.Body, TextWrapping = TextWrapping.Wrap, Foreground = secondary });
        }
        _oldCountdown = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = secondary };
        if (_previous is not null) panel.Children.Add(_oldCountdown);

        var keepButton = Choice("Language_Keep_Button", _previous?.Keep);
        keepButton.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        keepButton.Click += (_, _) => Finish(_keep);
        var backButton = Choice("Language_GoBack_Button", _previous?.GoBack);
        backButton.Click += (_, _) => Finish(_goBack);
        var buttons = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 18, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(backButton, 1);
        buttons.Children.Add(keepButton);
        buttons.Children.Add(backButton);
        panel.Children.Add(buttons);
        Content = panel;
        Keys.Wire(panel, () => Keys.Invoke(backButton), () => Keys.Invoke(keepButton));
        Keys.Hint(keepButton, "Enter");
        Keys.Hint(backButton, "Esc");

        AppWindow.Resize(new SizeInt32(520, 470));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsAlwaysOnTop = true;
            p.IsMaximizable = false;
            p.IsResizable = false;
        }
        AppWindow.Closing += (_, _) => Finish(_goBack);

        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, _) => TickGuard.Run("language-keep", Tick);
        UpdateText(LanguageRevert.Window);
    }

    // A button with the new-language text and, when it differs, the previous-language text under it.
    private Button Choice(string key, string? old)
    {
        var text = new StackPanel { Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Loc.Get(key), FontSize = 18, HorizontalAlignment = HorizontalAlignment.Center });
        if (!string.IsNullOrEmpty(old) && old != Loc.Get(key)) text.Children.Add(new TextBlock { Text = old, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.8 });
        return new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Stretch, MinHeight = 64 };
    }

    public void ShowPrompt(TimeSpan remaining)
    {
        UpdateText(remaining);
        _timer.Start();
        AppWindow.Show(true);
        Activate();
    }

    private void Tick()
    {
        var decision = LanguageRevert.Evaluate(_settings, DateTimeOffset.Now);
        if (decision.Action == RevertAction.AskToKeep) UpdateText(decision.Remaining);
        else Finish(_goBack);
    }

    private void UpdateText(TimeSpan remaining)
    {
        var seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        _newCountdown.Text = Loc.Format("Language_Keep_Countdown", seconds);
        if (_previous is not null) _oldCountdown.Text = _previous.CountdownFor(seconds);
    }

    private void Finish(Action action)
    {
        if (_done) return;
        _done = true;
        _timer.Stop();
        action();
    }
}
