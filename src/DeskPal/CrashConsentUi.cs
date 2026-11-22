using DeskPal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DeskPal;

// The one pre-ticked crash-report option, shared by the welcome wizard and the one-time note on Home.
// Ticking is On, unticking is Off (which also removes anything waiting); it saves at once.
internal static class CrashConsentUi
{
    public const string PrivacyUrl = "https://github.com/MindThaApp/desk-pal-privacy#crash-reports";

    public static UIElement Build(SettingsHub hub)
    {
        var s = hub.Settings;
        // The box sits on the first text line; clicking the sentence toggles it too. "What's sent" follows below, outside
        // the box, so its own click never toggles anything.
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
        var text = new TextBlock { Text = Loc.Get("Crash_Consent"), TextWrapping = TextWrapping.Wrap };
        text.Tapped += (_, _) =>
        {
            check.IsChecked = check.IsChecked != true;
            s.CrashReports = check.IsChecked == true ? CrashReportingMode.Always : CrashReportingMode.Never;
            hub.Commit(false);
        };
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        row.Children.Add(check);
        row.Children.Add(text);
        var link = new HyperlinkButton
        {
            Content = Loc.Get("Crash_WhatsSent"),
            NavigateUri = new Uri(PrivacyUrl),
            Padding = new Thickness(0),
            Margin = new Thickness(28, 2, 0, 0),
        };
        var panel = new StackPanel { Spacing = 0 };
        panel.Children.Add(row);
        panel.Children.Add(link);
        return panel;
    }
}
