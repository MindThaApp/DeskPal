using DeskPal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace DeskPal;

// The one pre-ticked crash-report option, shared by the welcome wizard and the one-time note on Home.
// Ticking is On, unticking is Off (which also removes anything waiting); it saves at once.
internal static class CrashConsentUi
{
    public const string PrivacyUrl = "https://github.com/MindThaApp/desk-pal-privacy#crash-reports";

    public static UIElement Build(SettingsHub hub)
    {
        var s = hub.Settings;
        // The box sits on the first text line with an 8 px gap; "What's sent" ends the sentence.
        var check = new CheckBox
        {
            MinWidth = 0,
            Padding = new Thickness(0),
            Margin = new Thickness(0, -2, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
            IsChecked = s.CrashReports != CrashReportingMode.Never,
        };
        AutomationProperties.SetName(check, Loc.Get("Crash_Consent"));
        check.Click += (_, _) =>
        {
            s.CrashReports = check.IsChecked == true ? CrashReportingMode.Always : CrashReportingMode.Never;
            hub.Commit(false);
        };
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        text.Inlines.Add(new Run { Text = Loc.Get("Crash_Consent") + " " });
        var link = new Hyperlink { NavigateUri = new Uri(PrivacyUrl) };
        link.Inlines.Add(new Run { Text = Loc.Get("Crash_WhatsSent") });
        text.Inlines.Add(link);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(check);
        grid.Children.Add(text);
        return grid;
    }
}
