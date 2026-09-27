namespace Shorepop.Verifier;

// Wire shapes mirror Assets/Scripts/Services/Commerce/NativePurchaseValidation.cs field-for-field.
// Output keeps the client's PascalCase names so Unity's JsonUtility can read it; input is
// case-insensitive. Unity's JsonUtility writes a null string as "", so empty means absent.

/// <summary>Transport-only input. Never logged, and never persisted except the Apple JWS / Google token needed to re-query.</summary>
public sealed class NativePurchaseProof
{
    public string? Store { get; set; }
    public string? ApplicationId { get; set; }
    public string? ProductId { get; set; }
    public string? TransactionId { get; set; }
    public string? Receipt { get; set; }
    public string? AppleSignedTransaction { get; set; }
    /// <summary>
    /// Optional, informational only: the client's <see cref="AppAccountTokens.Derive"/> of its own UGS player id.
    /// Binding decisions use the token inside the VERIFIED Apple JWS and the server's own derivation of the
    /// authenticated player, never this field; a mismatch with the server's derivation is only logged.
    /// </summary>
    public string? AppAccountToken { get; set; }
}

/// <summary>Body of POST /v1/purchases/ack: the client has durably committed the grant for this transaction.</summary>
public sealed class AckRequest
{
    public string? TransactionId { get; set; }
    /// <summary>Optional; default <c>AppleAppStore</c>.</summary>
    public string? Store { get; set; }
    /// <summary>Optional; default the bundle id. When present it must equal it.</summary>
    public string? ApplicationId { get; set; }
}

public sealed class AckResult
{
    public string TransactionId { get; set; } = "";
    public bool Acked { get; set; }
}

public sealed class ValidatedNativePurchase
{
    public string Store { get; set; } = "";
    public string ApplicationId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string TransactionId { get; set; } = "";
    public bool Refunded { get; set; }
    public long PurchasedPeriodExpiryUtcTicks { get; set; }
    public long SubscriptionEntitlementRevision { get; set; }
    public long CurrentSubscriptionExpiryUtcTicks { get; set; }
    public string Environment { get; set; } = "";
}

public sealed class ReconcileRequest
{
    public string? Store { get; set; }
    public string? ApplicationId { get; set; }
}

public sealed record ErrorBody(string Code);

public sealed class VerifierRefusal(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>What a store check proved about one transaction (before player binding).</summary>
public sealed record StoreVerdict(
    string Store, string ApplicationId, string ProductId, string TransactionId,
    string Environment, bool Refunded, long PurchasedPeriodExpiryUtcTicks,
    string? OriginalTransactionId, string? SignedPayload, long SignedDateUnixMs, ProductKind Kind,
    string? AppAccountToken = null);

/// <summary>
/// How a product binds to players. A consumable grants currency once, so its transaction belongs to the
/// first player who validates it (replay protection). A non-consumable or subscription is an entitlement
/// that follows the store account: any authenticated player presenting a validly signed transaction gets it.
/// </summary>
public enum ProductKind { Consumable, NonConsumable, Subscription }

public static class ShorepopCatalog
{
    public const string BundleId = "com.shorepop.game";
    public const string AppleStore = "AppleAppStore";
    public const string GoogleStore = "GooglePlay";
    public const string SubscriptionProductId = "gg.shorepop.pass.monthly";

    // Mirrors IapCatalog.All in UnityIapProvider.cs (14 rows, 2026-09-26).
    public static readonly IReadOnlySet<string> ProductIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "gg.shorepop.noads", "gg.shorepop.starter", "gg.shorepop.lives.refill",
        "gg.shorepop.coins.600", "gg.shorepop.coins.1600", "gg.shorepop.abilities.bundle",
        "gg.shorepop.gems.80", "gg.shorepop.gems.260", "gg.shorepop.gems.550",
        "gg.shorepop.gems.1200", "gg.shorepop.gems.2800", SubscriptionProductId,
        "gg.shorepop.welcome", "gg.shorepop.shellbank",
    };

    // Product types mirror IapCatalog.All (ProductType column): everything else there is Consumable.
    public static readonly IReadOnlySet<string> NonConsumableIds = new HashSet<string>(StringComparer.Ordinal)
    {
        "gg.shorepop.noads", "gg.shorepop.starter",
    };

    public static bool IsSubscription(string productId) => productId == SubscriptionProductId;

    /// <summary>Catalog type; the fallback when the store's signed data carries no type (Google).</summary>
    public static ProductKind CatalogKind(string productId) =>
        IsSubscription(productId) ? ProductKind.Subscription
        : NonConsumableIds.Contains(productId) ? ProductKind.NonConsumable
        : ProductKind.Consumable;

    /// <summary>
    /// Kind from Apple's signed <c>type</c>, never looser than the catalog: a product the catalog calls
    /// consumable stays single-owner even if App Store Connect mislabels it (the client grants currency by product id).
    /// </summary>
    public static ProductKind AppleKind(string signedType, string productId) =>
        CatalogKind(productId) == ProductKind.Consumable ? ProductKind.Consumable
        : signedType switch
        {
            "Consumable" => ProductKind.Consumable,
            "Non-Consumable" => ProductKind.NonConsumable,
            "Auto-Renewable Subscription" => ProductKind.Subscription,
            _ => CatalogKind(productId),
        };

    /// <summary>Only consumables are bound to the first player; entitlements follow the store account.</summary>
    public static bool SingleOwner(ProductKind kind) => kind == ProductKind.Consumable;

    public static long UnixMsToTicks(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcTicks;
}
