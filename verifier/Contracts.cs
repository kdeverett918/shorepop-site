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
    string? OriginalTransactionId, string? SignedPayload, long SignedDateUnixMs);

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

    public static bool IsSubscription(string productId) => productId == SubscriptionProductId;

    public static long UnixMsToTicks(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcTicks;
}
