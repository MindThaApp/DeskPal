using DeskPal.Core;
using Windows.Services.Store;
using WinRT.Interop;

namespace DeskPal;

// The Microsoft Store side of the Desk Pal Evolution add-on. Needs package identity: the portable build has no Store
// and always answers "not owned". Every call degrades to Unknown or Unavailable instead of throwing.
internal sealed class WindowsProStore(Func<nint> ownerWindow) : IProStore
{
    private const string DurableKind = "Durable";
    private readonly object _gate = new();
    private StoreContext? _context;

    public async Task<StoreOwnership> QueryOwnershipAsync(CancellationToken cancellationToken)
    {
        if (!PackagedApp.IsPackaged) return StoreOwnership.NotOwned;
        try
        {
            var context = GetContext(0);
            var license = await context.GetAppLicenseAsync().AsTask(cancellationToken).ConfigureAwait(false);
            if (license is null) return StoreOwnership.Unknown;
            foreach (var (key, addOn) in license.AddOnLicenses)
            {
                if (addOn.IsActive && key.StartsWith(ProRules.StoreId, StringComparison.OrdinalIgnoreCase))
                    return StoreOwnership.Owned;
            }

            // The user's collection also lists add-ons granted outside the app licence.
            var collection = await context.GetUserCollectionAsync([DurableKind]).AsTask(cancellationToken).ConfigureAwait(false);
            if (collection.ExtendedError is not null && collection.ExtendedError.HResult < 0) return StoreOwnership.Unknown;
            foreach (var (_, product) in collection.Products)
            {
                if (product.StoreId.StartsWith(ProRules.StoreId, StringComparison.OrdinalIgnoreCase))
                    return StoreOwnership.Owned;
            }
            return StoreOwnership.NotOwned;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Log("pro licence check failed: " + ex.Message);
            return StoreOwnership.Unknown;
        }
    }

    public async Task<ProPurchaseOutcome> PurchaseAsync(CancellationToken cancellationToken)
    {
        if (!PackagedApp.IsPackaged) return ProPurchaseOutcome.Unavailable;
        try
        {
            var context = GetContext(ownerWindow());
            var result = await context.RequestPurchaseAsync(ProRules.StoreId).AsTask(cancellationToken).ConfigureAwait(false);
            return result.Status switch
            {
                StorePurchaseStatus.Succeeded => ProPurchaseOutcome.Succeeded,
                StorePurchaseStatus.AlreadyPurchased => ProPurchaseOutcome.AlreadyPurchased,
                StorePurchaseStatus.NotPurchased => ProPurchaseOutcome.NotPurchased,
                StorePurchaseStatus.NetworkError => ProPurchaseOutcome.NetworkError,
                StorePurchaseStatus.ServerError => ProPurchaseOutcome.ServerError,
                _ => ProPurchaseOutcome.Unavailable,
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Log("pro purchase failed: " + ex.Message);
            return ProPurchaseOutcome.Unavailable;
        }
    }

    public async Task<string?> GetFormattedPriceAsync(CancellationToken cancellationToken)
    {
        if (!PackagedApp.IsPackaged) return null;
        try
        {
            var context = GetContext(0);
            var result = await context.GetStoreProductsAsync([DurableKind], [ProRules.StoreId]).AsTask(cancellationToken).ConfigureAwait(false);
            foreach (var (_, product) in result.Products)
            {
                if (product.Price is { FormattedPrice: { Length: > 0 } price }) return price;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Log("pro price lookup failed: " + ex.Message);
        }
        return null;
    }

    private StoreContext GetContext(nint owner)
    {
        lock (_gate)
        {
            _context ??= StoreContext.GetDefault();
            // A desktop app must tell the Store which window owns its dialogs (IInitializeWithWindow).
            var handle = owner != 0 ? owner : ownerWindow();
            if (handle != 0)
            {
                try { InitializeWithWindow.Initialize(_context, handle); }
                catch (Exception ex) { Logger.Log("store window init failed: " + ex.Message); }
            }
            return _context;
        }
    }
}
