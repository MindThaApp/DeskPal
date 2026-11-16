namespace DeskPal.Core;

// Desk Pal Evolution (Pro): one durable Store add-on that unlocks every Pro feature, plus a one-time 7-day trial.
//
// Never locked: reminders, quiet times, goal rings, streaks, Bloop and Sprout as they are, the garden's four stages and
// 7 days of history. Pro only adds things on top.
//
// Gating rules for Pro features:
// - Show Pro items with a "Pro" badge and a preview; never pop up or interrupt a reminder to sell.
// - Keep the user's Pro choices saved even when Pro is not unlocked; read them through ProState.Pick(custom, standard)
//   so a lapsed trial falls back to the standard look without deleting or overwriting anything.

public enum ProTier { Free, Trial, Owned }

// What the Store said the last time it gave a clear answer.
public enum StoreOwnership { Unknown, NotOwned, Owned }

public enum ProPurchaseOutcome { Succeeded, AlreadyPurchased, NotPurchased, NetworkError, ServerError, Unavailable }

// The local record kept in settings.json. Only ever added to: the trial start is written once and never cleared.
public sealed class ProLicenceRecord
{
    // When the user tapped "Try Pro". Null until then; once set the trial can never start again.
    public DateTimeOffset? TrialStartedAt { get; set; }
    // The last clear Store answer, so an offline start keeps what the user bought.
    public bool StoreOwned { get; set; }
    public DateTimeOffset? StoreCheckedAt { get; set; }
}

public readonly record struct ProState(ProTier Tier, int TrialDaysLeft, bool TrialUsed)
{
    public static readonly ProState Free = new(ProTier.Free, 0, false);

    public bool IsUnlocked => Tier != ProTier.Free;
    public bool CanStartTrial => Tier == ProTier.Free && !TrialUsed;

    // The user's saved Pro choice while Pro is unlocked, the standard one otherwise. The saved value is never touched.
    public T Pick<T>(T custom, T standard) => IsUnlocked ? custom : standard;
}

public static class ProRules
{
    public const int TrialDays = 7;
    public const string StoreId = "9NZ76SD8MHWX";

    public static ProState Evaluate(ProLicenceRecord record, DateTimeOffset now, bool forceOwned = false)
    {
        var trialUsed = record.TrialStartedAt is not null;
        if (forceOwned || record.StoreOwned) return new ProState(ProTier.Owned, 0, trialUsed);
        var left = TrialDaysLeft(record, now);
        return left > 0 ? new ProState(ProTier.Trial, left, true) : new ProState(ProTier.Free, 0, trialUsed);
    }

    // Whole days left, counted up: 6 days and 1 hour left shows as 7, the last hour shows as 1, the end shows as 0.
    // A clock set back before the start counts as the trial's first moment, never more than TrialDays.
    public static int TrialDaysLeft(ProLicenceRecord record, DateTimeOffset now)
    {
        if (record.TrialStartedAt is not { } start) return 0;
        var remaining = start.AddDays(TrialDays) - now;
        if (remaining <= TimeSpan.Zero) return 0;
        return Math.Min(TrialDays, (int)Math.Ceiling(remaining.TotalDays));
    }

    // Starts the trial once. Returns false (and changes nothing) when it was already used or Pro is owned.
    public static bool TryStartTrial(ProLicenceRecord record, DateTimeOffset now)
    {
        if (record.TrialStartedAt is not null || record.StoreOwned) return false;
        record.TrialStartedAt = now;
        return true;
    }

    // Folds a Store answer into the cached record. Unknown (offline, Store error) keeps the cached answer.
    // Returns true when ownership changed and the record should be saved (the check time alone is not worth a write).
    public static bool ApplyStoreAnswer(ProLicenceRecord record, StoreOwnership answer, DateTimeOffset now)
    {
        if (answer == StoreOwnership.Unknown) return false;
        var owned = answer == StoreOwnership.Owned;
        var changed = record.StoreOwned != owned;
        record.StoreOwned = owned;
        record.StoreCheckedAt = now;
        return changed;
    }
}

// The Store side, so the licence logic can be tested without Windows.
public interface IProStore
{
    Task<StoreOwnership> QueryOwnershipAsync(CancellationToken cancellationToken);
    Task<ProPurchaseOutcome> PurchaseAsync(CancellationToken cancellationToken);
    Task<string?> GetFormattedPriceAsync(CancellationToken cancellationToken);
}

// Live licence state: evaluates the record, refreshes it from the Store, starts the trial and buys the add-on.
// save is called whenever the record changes (the app saves settings.json through it); Changed fires when the state
// the user sees changes.
public sealed class ProLicence(ProLicenceRecord record, IProStore store, TimeProvider time, Action save, bool forceOwned = false)
{
    // Window activations closer together than this reuse the last Store answer.
    public static readonly TimeSpan RefreshThrottle = TimeSpan.FromMinutes(1);
    // Right after a purchase the Store licence can lag behind; a "not owned" answer in this window is not trusted.
    public static readonly TimeSpan PurchaseGrace = TimeSpan.FromMinutes(10);

    private ProState _last = ProRules.Evaluate(record, time.GetUtcNow(), forceOwned);
    private DateTimeOffset? _lastRefresh;
    private bool _refreshing;
    private DateTimeOffset? _purchasedAt;

    public event Action? Changed;

    public ProLicenceRecord Record => record;
    public ProState Current => ProRules.Evaluate(record, time.GetUtcNow(), forceOwned);
    public bool IsUnlocked => Current.IsUnlocked;
    // The Store's formatted price ("£3.99"), or null until the Store has given one.
    public string? Price { get; private set; }

    // Re-evaluates against the clock (a trial can run out while the app runs) and raises Changed if anything moved.
    public void Reevaluate()
    {
        var now = Current;
        if (now == _last) return;
        _last = now;
        Changed?.Invoke();
    }

    // Asks the Store again. force skips the throttle (startup, after a purchase, "Restore purchase").
    public async Task RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        if (_refreshing) return;
        if (!force && _lastRefresh is { } last && now - last < RefreshThrottle) return;
        _refreshing = true;
        try
        {
            _lastRefresh = now;
            StoreOwnership answer;
            try { answer = await store.QueryOwnershipAsync(cancellationToken); }
            catch (Exception ex) when (ex is not OperationCanceledException) { answer = StoreOwnership.Unknown; }
            if (answer == StoreOwnership.NotOwned && _purchasedAt is { } bought && time.GetUtcNow() - bought < PurchaseGrace)
                answer = StoreOwnership.Unknown;
            if (ProRules.ApplyStoreAnswer(record, answer, time.GetUtcNow())) save();
            if (Price is null)
            {
                try { Price = await store.GetFormattedPriceAsync(cancellationToken); }
                catch (Exception ex) when (ex is not OperationCanceledException) { }
            }
        }
        finally { _refreshing = false; }
        Reevaluate();
    }

    public bool TryStartTrial()
    {
        if (!ProRules.TryStartTrial(record, time.GetUtcNow())) return false;
        save();
        Reevaluate();
        return true;
    }

    public async Task<ProPurchaseOutcome> PurchaseAsync(CancellationToken cancellationToken = default)
    {
        ProPurchaseOutcome outcome;
        try { outcome = await store.PurchaseAsync(cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException) { outcome = ProPurchaseOutcome.Unavailable; }
        if (outcome is ProPurchaseOutcome.Succeeded or ProPurchaseOutcome.AlreadyPurchased)
        {
            _purchasedAt = time.GetUtcNow();
            if (ProRules.ApplyStoreAnswer(record, StoreOwnership.Owned, time.GetUtcNow())) save();
            Reevaluate();
        }
        await RefreshAsync(force: true, cancellationToken);
        return outcome;
    }
}
