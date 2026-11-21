using System.Text.Json;
using System.Text.Json.Serialization;
using DeskPal.Core;

namespace DeskPal.Core.Tests;

public class ProLicenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    private sealed class FakeStore : IProStore
    {
        public StoreOwnership Answer { get; set; } = StoreOwnership.NotOwned;
        public ProPurchaseOutcome PurchaseResult { get; set; } = ProPurchaseOutcome.NotPurchased;
        public string? Price { get; set; } = "£3.99";
        public int Queries { get; private set; }

        public Task<StoreOwnership> QueryOwnershipAsync(CancellationToken cancellationToken)
        {
            Queries++;
            return Task.FromResult(Answer);
        }

        public Task<ProPurchaseOutcome> PurchaseAsync(CancellationToken cancellationToken) => Task.FromResult(PurchaseResult);

        public Task<string?> GetFormattedPriceAsync(CancellationToken cancellationToken) => Task.FromResult(Price);
    }

    private static (ProLicence licence, ProLicenceRecord record, FakeStore store, FakeTime time, Func<int> saves) Make(
        ProLicenceRecord? record = null, bool forceOwned = false)
    {
        var r = record ?? new ProLicenceRecord();
        var store = new FakeStore();
        var time = new FakeTime(Start);
        var saves = 0;
        var licence = new ProLicence(r, store, time, () => saves++, forceOwned);
        return (licence, r, store, time, () => saves);
    }

    [Fact]
    public void New_install_is_free_with_trial_available()
    {
        var state = ProRules.Evaluate(new ProLicenceRecord(), Start);
        Assert.Equal(ProTier.Free, state.Tier);
        Assert.False(state.IsUnlocked);
        Assert.True(state.CanStartTrial);
    }

    [Theory]
    [InlineData(0, 7)]
    [InlineData(1, 7)]
    [InlineData(24, 6)]
    [InlineData(25, 6)]
    [InlineData(6 * 24, 1)]
    [InlineData(7 * 24 - 1, 1)]
    [InlineData(7 * 24, 0)]
    [InlineData(30 * 24, 0)]
    public void Trial_days_left_counts_whole_days_up(int hoursIn, int expected)
    {
        var record = new ProLicenceRecord { TrialStartedAt = Start };
        Assert.Equal(expected, ProRules.TrialDaysLeft(record, Start.AddHours(hoursIn)));
    }

    [Fact]
    public void Trial_is_unlocked_until_seven_days_pass()
    {
        var record = new ProLicenceRecord { TrialStartedAt = Start };
        var during = ProRules.Evaluate(record, Start.AddDays(3));
        Assert.Equal(ProTier.Trial, during.Tier);
        Assert.Equal(4, during.TrialDaysLeft);
        Assert.True(during.IsUnlocked);

        var after = ProRules.Evaluate(record, Start.AddDays(ProRules.TrialDays));
        Assert.Equal(ProTier.Free, after.Tier);
        Assert.False(after.IsUnlocked);
        Assert.False(after.CanStartTrial);
    }

    [Fact]
    public void Clock_set_back_never_gives_more_than_seven_days()
    {
        var record = new ProLicenceRecord { TrialStartedAt = Start };
        Assert.Equal(ProRules.TrialDays, ProRules.TrialDaysLeft(record, Start.AddDays(-30)));
    }

    [Fact]
    public void Trial_cannot_restart_once_used()
    {
        var (licence, record, _, time, saves) = Make();
        Assert.True(licence.TryStartTrial());
        Assert.Equal(1, saves());
        Assert.Equal(Start, record.TrialStartedAt);

        time.Advance(TimeSpan.FromDays(2));
        Assert.False(licence.TryStartTrial());
        Assert.Equal(Start, record.TrialStartedAt);

        time.Advance(TimeSpan.FromDays(10));
        Assert.Equal(ProTier.Free, licence.Current.Tier);
        Assert.False(licence.TryStartTrial());
        Assert.False(licence.Current.CanStartTrial);
        Assert.Equal(Start, record.TrialStartedAt);
        Assert.Equal(1, saves());
    }

    [Fact]
    public void Trial_start_survives_a_settings_round_trip()
    {
        var settings = new DeskPalSettings();
        Assert.True(ProRules.TryStartTrial(settings.Pro, Start));
        var options = new JsonSerializerOptions { Converters = { new JsonStringEnumConverter() } };
        var loaded = JsonSerializer.Deserialize<DeskPalSettings>(JsonSerializer.Serialize(settings, options), options)!;
        loaded.Migrate();
        Assert.Equal(Start, loaded.Pro.TrialStartedAt);
        Assert.False(ProRules.TryStartTrial(loaded.Pro, Start.AddDays(20)));
    }

    [Fact]
    public void Settings_without_a_pro_record_load_as_free()
    {
        var loaded = JsonSerializer.Deserialize<DeskPalSettings>("{\"Pro\":null}")!;
        Assert.True(loaded.Migrate());
        Assert.NotNull(loaded.Pro);
        Assert.Equal(ProTier.Free, ProRules.Evaluate(loaded.Pro, Start).Tier);
    }

    [Fact]
    public void Owned_overrides_trial_and_expired_trial()
    {
        var record = new ProLicenceRecord { TrialStartedAt = Start, StoreOwned = true };
        Assert.Equal(ProTier.Owned, ProRules.Evaluate(record, Start.AddDays(1)).Tier);
        Assert.Equal(ProTier.Owned, ProRules.Evaluate(record, Start.AddDays(100)).Tier);
        Assert.False(ProRules.TryStartTrial(new ProLicenceRecord { StoreOwned = true }, Start));
    }

    [Fact]
    public void Force_owned_unlocks_without_touching_the_record()
    {
        var (licence, record, _, _, saves) = Make(forceOwned: true);
        Assert.Equal(ProTier.Owned, licence.Current.Tier);
        Assert.False(record.StoreOwned);
        Assert.Equal(0, saves());
    }

    [Fact]
    public async Task Offline_refresh_keeps_the_cached_owned_answer()
    {
        var (licence, record, store, _, saves) = Make(new ProLicenceRecord { StoreOwned = true });
        store.Answer = StoreOwnership.Unknown;
        await licence.RefreshAsync(force: true);
        Assert.True(record.StoreOwned);
        Assert.Equal(ProTier.Owned, licence.Current.Tier);
        Assert.Equal(0, saves());
    }

    [Fact]
    public async Task Clear_store_answer_updates_the_cache()
    {
        var (licence, record, store, _, saves) = Make();
        var changes = 0;
        licence.Changed += () => changes++;
        store.Answer = StoreOwnership.Owned;
        await licence.RefreshAsync(force: true);
        Assert.True(record.StoreOwned);
        Assert.Equal(ProTier.Owned, licence.Current.Tier);
        Assert.Equal(1, saves());
        Assert.Equal(1, changes);
        Assert.Equal("£3.99", licence.Price);

        store.Answer = StoreOwnership.NotOwned;
        await licence.RefreshAsync(force: true);
        Assert.False(record.StoreOwned);
        Assert.Equal(ProTier.Free, licence.Current.Tier);
        Assert.Equal(2, changes);
    }

    [Fact]
    public async Task Licence_lost_after_trial_returns_to_free_but_keeps_the_trial_record()
    {
        var (licence, record, store, _, _) = Make(new ProLicenceRecord { TrialStartedAt = Start.AddDays(-20), StoreOwned = true });
        store.Answer = StoreOwnership.NotOwned;
        await licence.RefreshAsync(force: true);
        Assert.Equal(ProTier.Free, licence.Current.Tier);
        Assert.Equal(Start.AddDays(-20), record.TrialStartedAt);
        Assert.False(licence.TryStartTrial());
    }

    [Fact]
    public async Task Refresh_is_throttled_unless_forced()
    {
        var (licence, _, store, time, _) = Make();
        await licence.RefreshAsync();
        await licence.RefreshAsync();
        Assert.Equal(1, store.Queries);
        time.Advance(ProLicence.RefreshThrottle);
        await licence.RefreshAsync();
        Assert.Equal(2, store.Queries);
        await licence.RefreshAsync(force: true);
        Assert.Equal(3, store.Queries);
    }

    [Fact]
    public async Task Purchase_unlocks_even_while_the_store_licence_lags()
    {
        var (licence, record, store, time, _) = Make();
        store.PurchaseResult = ProPurchaseOutcome.Succeeded;
        store.Answer = StoreOwnership.NotOwned;
        Assert.Equal(ProPurchaseOutcome.Succeeded, await licence.PurchaseAsync());
        Assert.True(record.StoreOwned);
        Assert.Equal(ProTier.Owned, licence.Current.Tier);

        time.Advance(ProLicence.PurchaseGrace);
        await licence.RefreshAsync(force: true);
        Assert.False(record.StoreOwned);
    }

    [Fact]
    public async Task Cancelled_purchase_changes_nothing()
    {
        var (licence, record, store, _, saves) = Make();
        store.PurchaseResult = ProPurchaseOutcome.NotPurchased;
        await licence.PurchaseAsync();
        Assert.False(record.StoreOwned);
        Assert.Equal(ProTier.Free, licence.Current.Tier);
        Assert.Equal(0, saves());
    }

    [Fact]
    public void Trial_running_out_raises_changed()
    {
        var (licence, _, _, time, _) = Make();
        licence.TryStartTrial();
        var changes = 0;
        licence.Changed += () => changes++;
        licence.Reevaluate();
        Assert.Equal(0, changes);
        time.Advance(TimeSpan.FromDays(ProRules.TrialDays));
        licence.Reevaluate();
        Assert.Equal(1, changes);
        Assert.Equal(ProTier.Free, licence.Current.Tier);
    }

    [Fact]
    public void Pick_falls_back_to_standard_without_losing_the_choice()
    {
        const string saved = "custom";
        Assert.Equal(saved, new ProState(ProTier.Trial, 3, true).Pick(saved, "standard"));
        Assert.Equal(saved, new ProState(ProTier.Owned, 0, false).Pick(saved, "standard"));
        Assert.Equal("standard", ProState.Free.Pick(saved, "standard"));
    }
}
