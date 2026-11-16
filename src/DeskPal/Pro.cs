using DeskPal.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskPal;

// Desk Pal Evolution (Pro) for the UI. How to gate a Pro feature:
//
//   Pro.IsUnlocked                  true during the trial and once the add-on is owned (or with --debug-pro).
//   Pro.State                       Free / Trial (TrialDaysLeft) / Owned, plus TrialUsed and CanStartTrial.
//   Pro.Pick(saved, standard)       what to show: the user's saved Pro choice while unlocked, the standard one otherwise.
//                                   Never overwrite or clear the saved choice when Pro is locked; it comes back on purchase.
//   Pro.Changed                     raised on the UI thread when the state changes (trial start or end, purchase, licence
//                                   lost). SettingsHub.Changed is raised too, so views that follow the hub refresh already.
//   Pro.ShowOffer()                 opens the Desk Pal Evolution page; use it when a locked Pro item is tapped.
//   new ProBadge()                  the small "Pro" pill to put next to a Pro item; hides itself while Pro is unlocked
//                                   (set HideWhenUnlocked = false to keep it).
//
// Pro items stay visible with the badge and a preview. No pop-ups, and nothing ever interrupts a reminder to sell.
internal static class Pro
{
    private static ProLicence? _licence;

    public static event Action? Changed;

    public static ProLicence? Licence => _licence;
    public static ProState State => _licence?.Current ?? (AppData.DebugPro ? new ProState(ProTier.Owned, 0, false) : ProState.Free);
    public static bool IsUnlocked => State.IsUnlocked;

    // Set by the app: opens the main window on the Pro page.
    public static Action? OpenPage { get; set; }

    public static T Pick<T>(T saved, T standard) => State.Pick(saved, standard);

    public static void ShowOffer() => OpenPage?.Invoke();

    public static void Attach(ProLicence licence)
    {
        if (_licence is not null) _licence.Changed -= Raise;
        _licence = licence;
        licence.Changed += Raise;
    }

    private static void Raise() => Changed?.Invoke();
}

// The small "Pro" pill shown next to Pro items.
internal sealed partial class ProBadge : UserControl
{
    private bool _hideWhenUnlocked = true;

    public ProBadge()
    {
        VerticalAlignment = VerticalAlignment.Center;
        HorizontalAlignment = HorizontalAlignment.Left;
        IsTabStop = false;
        var text = Loc.Get("Pro_Badge");
        Content = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 1, 7, 2),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
            },
        };
        AutomationProperties.SetName(this, text);
        Loaded += (_, _) =>
        {
            Pro.Changed -= Update;
            Pro.Changed += Update;
            Update();
        };
        Unloaded += (_, _) => Pro.Changed -= Update;
        Update();
    }

    public bool HideWhenUnlocked
    {
        get => _hideWhenUnlocked;
        set { _hideWhenUnlocked = value; Update(); }
    }

    private void Update() => Visibility = _hideWhenUnlocked && Pro.IsUnlocked ? Visibility.Collapsed : Visibility.Visible;
}
