using DeskPal.Core;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DeskPal;

// The Desk Pal Evolution (Pro) page: status, what Pro adds, what always stays free, and the trial / buy / restore buttons.
public sealed class ProPage : Page
{
    private readonly TextBlock _status = Line("");
    private readonly TextBlock _message = Secondary("");
    private readonly Button _try = new() { Content = Loc.Get("Pro_TryFree"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
    private readonly Button _buy = new();
    private readonly Button _restore = new() { Content = Loc.Get("Pro_Restore") };
    private readonly ProgressRing _busy = new() { IsActive = false, Width = 20, Height = 20, Visibility = Visibility.Collapsed };
    private bool _working;

    public ProPage(MainContext ctx)
    {
        var badge = new ProBadge { HideWhenUnlocked = false };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { PageKit.Title(Loc.Get("Pro_Title")), badge } };

        // Stacked, so long translations never push a button off the card.
        foreach (var b in new[] { _try, _buy, _restore }) b.MinWidth = 220;
        var buttons = new StackPanel { Spacing = 8, Children = { _try, _buy, _restore, _busy } };
        _busy.HorizontalAlignment = HorizontalAlignment.Left;
        var statusCard = Card(new StackPanel
        {
            Spacing = 10,
            Children = { _status, buttons, _message, Secondary(Loc.Get("Pro_OneTime")) },
        });

        var adds = new StackPanel { Spacing = 6, Children = { Line(Loc.Get("Pro_Includes"), bold: true) } };
        foreach (var key in new[] { "Pro_Feature_Custom", "Pro_Feature_Stretch", "Pro_Feature_Future" })
            adds.Children.Add(Bullet(Loc.Get(key)));

        var free = new StackPanel
        {
            Spacing = 6,
            Children = { Line(Loc.Get("Pro_AlwaysFree"), bold: true), Secondary(Loc.Get("Pro_AlwaysFreeText")) },
        };

        Content = PageKit.Scroll(new StackPanel
        {
            Children = { title, PageKit.Body(Loc.Get("Pro_Intro")), statusCard, Card(adds), Card(free) },
        });

        _try.Click += (_, _) => TickGuard.Run("pro-try", StartTrial);
        _buy.Click += (_, _) => _ = BuyAsync();
        _restore.Click += (_, _) => _ = RestoreAsync();
        Loaded += (_, _) =>
        {
            Pro.Changed -= Update;
            Pro.Changed += Update;
            Update();
            _ = RefreshAsync();
        };
        Unloaded += (_, _) => Pro.Changed -= Update;
        Update();
    }

    private void Update()
    {
        var state = Pro.State;
        _status.Text = StatusText(state);
        var owned = state.Tier == ProTier.Owned;
        _try.Visibility = state.CanStartTrial ? Visibility.Visible : Visibility.Collapsed;
        _buy.Visibility = owned ? Visibility.Collapsed : Visibility.Visible;
        _restore.Visibility = owned ? Visibility.Collapsed : Visibility.Visible;
        _buy.Style = state.CanStartTrial ? null : (Style)Application.Current.Resources["AccentButtonStyle"];
        var price = Pro.Licence?.Price;
        _buy.Content = string.IsNullOrEmpty(price) ? Loc.Get("Pro_Buy") : Loc.Format("Pro_BuyPrice", price);
        AutomationProperties.SetName(_buy, (string)_buy.Content);
        _try.IsEnabled = _buy.IsEnabled = _restore.IsEnabled = !_working;
        _busy.IsActive = _working;
        _busy.Visibility = _working ? Visibility.Visible : Visibility.Collapsed;
    }

    public static string StatusText(ProState state) => state.Tier switch
    {
        ProTier.Owned => Loc.Get("Pro_Status_Owned"),
        ProTier.Trial when state.TrialDaysLeft <= 1 => Loc.Get("Pro_Status_TrialLastDay"),
        ProTier.Trial => Loc.Format("Pro_Status_Trial", state.TrialDaysLeft),
        _ when state.TrialUsed => Loc.Get("Pro_Status_TrialEnded"),
        _ => Loc.Get("Pro_Status_Free"),
    };

    private void StartTrial()
    {
        if (Pro.Licence?.TryStartTrial() == true) _message.Text = Loc.Get("Pro_Msg_TrialStarted");
        Update();
    }

    private async Task RefreshAsync()
    {
        try
        {
            if (Pro.Licence is { } licence) await licence.RefreshAsync();
        }
        catch (Exception ex) { Logger.Log("pro page refresh failed: " + ex.Message); }
        Update();
    }

    private async Task BuyAsync()
    {
        if (_working || Pro.Licence is not { } licence) return;
        if (!PackagedApp.IsPackaged)
        {
            _message.Text = Loc.Get("Pro_Msg_NoStore");
            return;
        }
        SetWorking(true);
        try
        {
            var outcome = await licence.PurchaseAsync();
            _message.Text = outcome switch
            {
                ProPurchaseOutcome.Succeeded or ProPurchaseOutcome.AlreadyPurchased => Loc.Get("Pro_Msg_Thanks"),
                ProPurchaseOutcome.NotPurchased => Loc.Get("Pro_Msg_NotPurchased"),
                _ => Loc.Get("Pro_Msg_StoreError"),
            };
        }
        catch (Exception ex)
        {
            Logger.Log("pro purchase failed: " + ex.Message);
            _message.Text = Loc.Get("Pro_Msg_StoreError");
        }
        finally { SetWorking(false); }
    }

    private async Task RestoreAsync()
    {
        if (_working || Pro.Licence is not { } licence) return;
        if (!PackagedApp.IsPackaged)
        {
            _message.Text = Loc.Get("Pro_Msg_NoStore");
            return;
        }
        SetWorking(true);
        try
        {
            await licence.RefreshAsync(force: true);
            _message.Text = licence.Current.Tier == ProTier.Owned ? Loc.Get("Pro_Msg_Restored")
                : licence.LastStoreAnswer == StoreOwnership.Unknown ? Loc.Get("Pro_Msg_StoreError")
                : Loc.Get("Pro_Msg_NothingToRestore");
        }
        catch (Exception ex)
        {
            Logger.Log("pro restore failed: " + ex.Message);
            _message.Text = Loc.Get("Pro_Msg_StoreError");
        }
        finally { SetWorking(false); }
    }

    private void SetWorking(bool working)
    {
        _working = working;
        Update();
    }

    private static Border Card(UIElement child) => new()
    {
        Style = (Style)Application.Current.Resources["CardSurfaceStyle"],
        BorderThickness = new Thickness(1),
        CornerRadius = (CornerRadius)Application.Current.Resources["OverlayCornerRadius"],
        Padding = new Thickness(16, 12, 16, 12),
        Child = child,
    };

    private static TextBlock Line(string text, bool bold = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
    };

    private static TextBlock Secondary(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Style = (Style)Application.Current.Resources["SecondaryTextStyle"],
    };

    private static Grid Bullet(string text)
    {
        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new TextBlock { Text = "•" });
        var body = Line(text);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }
}
