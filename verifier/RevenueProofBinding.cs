using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shorepop.Verifier;

/// <summary>Attribution proof is retained only after purchase verification and explicit identity binding.</summary>
public static class RevenueProofBinding
{
    public const int MaximumProofBytes = 48 * 1024;

    public static bool TryBind(NativePurchaseProof proof, ValidatedNativePurchase verified,
        string? googleLicensePublicKey, out string receipt, out string signature)
    {
        receipt = signature = "";
        if (verified.Refunded || verified.RevenueRefunded || proof.Store != verified.Store || proof.ApplicationId != verified.ApplicationId ||
            proof.ProductId != verified.ProductId || proof.TransactionId != verified.TransactionId) return false;
        if (verified.Store == ShorepopCatalog.AppleStore)
        {
            // ValidateAsync has already verified this exact JWS against Apple's configured root and
            // bound its signed bundle/product/transaction to these values. Tenjin accepts SK2 JWS.
            string jws = proof.AppleSignedTransaction ?? "";
            if (jws.Length == 0 || Encoding.UTF8.GetByteCount(jws) > MaximumProofBytes) return false;
            receipt = jws;
            return true;
        }
        if (verified.Store != ShorepopCatalog.GoogleStore || string.IsNullOrWhiteSpace(googleLicensePublicKey) ||
            string.IsNullOrEmpty(proof.Receipt) || Encoding.UTF8.GetByteCount(proof.Receipt) > MaximumProofBytes) return false;
        try
        {
            using var outer = JsonDocument.Parse(proof.Receipt, new JsonDocumentOptions { MaxDepth = 8 });
            if (Text(outer.RootElement, "Store") != ShorepopCatalog.GoogleStore) return false;
            using var payload = JsonDocument.Parse(Text(outer.RootElement, "Payload"), new JsonDocumentOptions { MaxDepth = 8 });
            string originalJson = Text(payload.RootElement, "json");
            string signed = Text(payload.RootElement, "signature");
            if (originalJson.Length == 0 || signed.Length == 0 || signed.Length > 4096) return false;
            using var data = JsonDocument.Parse(originalJson, new JsonDocumentOptions { MaxDepth = 8 });
            var purchase = data.RootElement;
            if (Text(purchase, "packageName") != verified.ApplicationId ||
                Text(purchase, "purchaseToken") != verified.TransactionId || !SingleProduct(purchase, verified.ProductId)) return false;
            if (purchase.TryGetProperty("quantity", out var quantity) && (!quantity.TryGetInt32(out int count) || count != 1)) return false;
            if (purchase.TryGetProperty("purchaseState", out var state) && (!state.TryGetInt32(out int value) || value != 0)) return false;
            string orderId = Text(purchase, "orderId");
            if (!string.IsNullOrEmpty(verified.RevenueTransactionId) && orderId != verified.RevenueTransactionId) return false;
            using var rsa = RSA.Create();
            if (googleLicensePublicKey.Contains("-----BEGIN", StringComparison.Ordinal)) rsa.ImportFromPem(googleLicensePublicKey);
            else rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(googleLicensePublicKey), out _);
            byte[] bytes = Encoding.UTF8.GetBytes(originalJson), signatureBytes = Convert.FromBase64String(signed);
            // Play Billing's original purchase signature uses SHA1withRSA; accept SHA256withRSA
            // too when verified by the same application licensing key, never a client-provided key.
            if (!rsa.VerifyData(bytes, signatureBytes, HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1) &&
                !rsa.VerifyData(bytes, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) return false;
            receipt = originalJson;
            signature = signed;
            return true;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or CryptographicException or FormatException or InvalidOperationException)
        {
            return false;
        }
    }

    public static bool MatchesRecovered(RevenueProof recovered, ValidatedNativePurchase economics, string? googleLicensePublicKey)
    {
        var proof = new NativePurchaseProof
        {
            Store = economics.Store, ApplicationId = economics.ApplicationId,
            ProductId = economics.ProductId, TransactionId = economics.TransactionId,
            AppleSignedTransaction = economics.Store == ShorepopCatalog.AppleStore ? recovered.Receipt : null,
            Receipt = economics.Store == ShorepopCatalog.GoogleStore
                ? JsonSerializer.Serialize(new { Store = economics.Store,
                    Payload = JsonSerializer.Serialize(new { json = recovered.Receipt, signature = recovered.Signature }) }) : "",
        };
        return TryBind(proof, economics, googleLicensePublicKey, out _, out _);
    }

    private static bool SingleProduct(JsonElement value, string product)
    {
        string single = Text(value, "productId");
        if (single.Length > 0) return single == product;
        return value.TryGetProperty("productIds", out var ids) && ids.ValueKind == JsonValueKind.Array &&
            ids.GetArrayLength() == 1 && ids[0].ValueKind == JsonValueKind.String && ids[0].GetString() == product;
    }

    private static string Text(JsonElement value, string key) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(key, out var text) && text.ValueKind == JsonValueKind.String ? text.GetString() ?? "" : "";
}

public sealed class RevenueProofRequest
{
    public string Store { get; set; } = "";
    public string ApplicationId { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string LedgerTransactionKey { get; set; } = "";
}

public sealed class RevenueProofResponse
{
    public string Store { get; set; } = "";
    public string ProductId { get; set; } = "";
    public string LedgerTransactionKey { get; set; } = "";
    public string Receipt { get; set; } = "";
    public string Signature { get; set; } = "";
    public bool RevenueVerified { get; set; }
    public bool RevenueRefunded { get; set; }
    public long GrossRevenueMicros { get; set; }
    public string RevenueCurrency { get; set; } = "";
    public string RevenueTransactionId { get; set; } = "";
    public long RevenuePurchasedAtUnixMs { get; set; }
}
