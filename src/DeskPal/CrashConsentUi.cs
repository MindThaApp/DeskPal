using DeskPal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DeskPal;

// The one pre-ticked crash-report option, shared by the welcome wizard and the one-time note on Home.
// Ticking is On, unticking is Off (which also removes anything waiting); it saves at once.
internal static class CrashConsentUi
{
    public const string PrivacyUrl = "https://mindthaapp.github.io/files-portal-privacy/desk-pal#crash-reports";

    public static UIElement Build(SettingsHub hub)
    {
        var s = hub.Settings;
        var check = new CheckBox
        {
            Content = new TextBlock { Text = Loc.Get("Crash_Consent"), TextWrapping = TextWrapping.Wrap },
            IsChecked = s.CrashReports != CrashReportingMode.Never,
        };
        check.Click += (_, _) =>
        {
            s.CrashReports = check.IsChecked == true ? CrashReportingMode.Always : CrashReportingMode.Never;
            hub.Commit(false);
        };
        var link = new HyperlinkButton
        {
            Content = Loc.Get("Crash_WhatsSent"),
            NavigateUri = new Uri(PrivacyUrl),
            Padding = new Thickness(0),
        };
        var panel = new StackPanel { Spacing = 2 };
        panel.Children.Add(check);
        panel.Children.Add(link);
        return panel;
    }
}
