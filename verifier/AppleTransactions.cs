using System.Text.Json;

namespace Shorepop.Verifier;

/// <summary>Turns a verified StoreKit 2 JWSTransaction payload into a <see cref="StoreVerdict"/>.</summary>
public sealed class AppleTransactionChecker(AppleJwsVerifier verifier, string bundleId, IReadOnlySet<string> allowedEnvironments)
{
    public static readonly IReadOnlySet<string> DefaultEnvironments = new HashSet<string>(StringComparer.Ordinal) { "Production", "Sandbox" };

    /// <summary>Verifies a JWS signed transaction for the product/transaction the client claims.</summary>
    public StoreVerdict Check(string jws, string expectedProductId, string expectedTransactionId, DateTimeOffset effectiveTime)
    {
        JsonElement payload;
        try { payload = verifier.VerifyAndDecode(jws, effectiveTime); }
        catch (AppleVerificationException) { throw new VerifierRefusal(422, "apple_signature_invalid"); }
        var verdict = Interpret(payload, jws);
        if (verdict.ProductId != expectedProductId) throw new VerifierRefusal(422, "product_mismatch");
        if (verdict.TransactionId != expectedTransactionId) throw new VerifierRefusal(422, "transaction_mismatch");
        return verdict;
    }

    /// <summary>Field checks on an already signature-verified payload.</summary>
    public StoreVerdict Interpret(JsonElement payload, string jws)
    {
        string bundle = Text(payload, "bundleId");
        if (bundle != bundleId) throw new VerifierRefusal(422, "bundle_mismatch");
        string environment = Text(payload, "environment");
        if (!allowedEnvironments.Contains(environment)) throw new VerifierRefusal(422, "environment_not_allowed");
        string productId = Text(payload, "productId");
        if (!ShorepopCatalog.ProductIds.Contains(productId)) throw new VerifierRefusal(422, "unknown_product");
        string transactionId = Text(payload, "transactionId");
        if (transactionId.Length == 0 || transactionId.Length > 64) throw new VerifierRefusal(422, "transaction_missing");
        if (!payload.TryGetProperty("quantity", out var quantity) || quantity.ValueKind != JsonValueKind.Number ||
            !quantity.TryGetInt32(out int q) || q != 1) throw new VerifierRefusal(422, "quantity_not_one");
        string type = Text(payload, "type");
        bool subscription = ShorepopCatalog.IsSubscription(productId);
        if (subscription && type != "Auto-Renewable Subscription") throw new VerifierRefusal(422, "type_mismatch");
        if (!subscription && type is not ("Consumable" or "Non-Consumable")) throw new VerifierRefusal(422, "type_mismatch");
        long expiry = 0;
        if (subscription)
        {
            long expiresMs = Millis(payload, "expiresDate");
            if (expiresMs <= 0) throw new VerifierRefusal(422, "expiry_missing");
            expiry = ShorepopCatalog.UnixMsToTicks(expiresMs);
        }
        bool refunded = Millis(payload, "revocationDate") > 0;
        string original = Text(payload, "originalTransactionId");
        return new StoreVerdict(ShorepopCatalog.AppleStore, bundleId, productId, transactionId, environment, refunded, expiry,
            original.Length == 0 ? null : original, jws, Millis(payload, "signedDate"));
    }

    private static string Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static long Millis(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long ms) && ms > 0 &&
        ms < 253402300799000 ? ms : 0;
}
