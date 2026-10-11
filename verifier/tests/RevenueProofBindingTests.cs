using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shorepop.Verifier;

internal static class RevenueProofBindingTests
{
    public static void Run(Action<string, Action> test)
    {
        using var key = RSA.Create(2048);
        string publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var result = new ValidatedNativePurchase { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.BundleId,
            ProductId = "gg.shorepop.gems.80", TransactionId = "test-purchase-token", RevenueTransactionId = "GPA.test", RevenueVerified = true };
        NativePurchaseProof Proof(string app = ShorepopCatalog.BundleId, string product = "gg.shorepop.gems.80", string token = "test-purchase-token")
        {
            string json = JsonSerializer.Serialize(new { packageName = app, productId = product, purchaseToken = token, orderId = "GPA.test", quantity = 1, purchaseState = 0 });
            string signature = Convert.ToBase64String(key.SignData(Encoding.UTF8.GetBytes(json), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
            string payload = JsonSerializer.Serialize(new { json, signature });
            return new NativePurchaseProof { Store = result.Store, ApplicationId = result.ApplicationId, ProductId = result.ProductId,
                TransactionId = result.TransactionId, Receipt = JsonSerializer.Serialize(new { Store = result.Store, Payload = payload }) };
        }
        void Expect(bool value) { if (!value) throw new Exception("proof binding expectation failed"); }
        test("revenue proof: signed Google receipt binds exact store, app, product and purchase token", () =>
        {
            Expect(RevenueProofBinding.TryBind(Proof(), result, publicKey, out var receipt, out var signature));
            Expect(receipt.Contains("test-purchase-token") && signature.Length > 0);
        });
        test("revenue proof: cross-app, cross-product and cross-transaction receipts are rejected", () =>
        {
            foreach (var proof in new[] { Proof(app: "other.app"), Proof(product: "gg.shorepop.gems.260"), Proof(token: "other-token") })
                Expect(!RevenueProofBinding.TryBind(proof, result, publicKey, out _, out _));
        });
        test("revenue proof: wrong licensing key and altered payload signature are rejected", () =>
        {
            using var other = RSA.Create(2048);
            Expect(!RevenueProofBinding.TryBind(Proof(), result, Convert.ToBase64String(other.ExportSubjectPublicKeyInfo()), out _, out _));
            var proof = Proof(); proof.Receipt = proof.Receipt!.Replace("GPA.test", "GPA.other");
            Expect(!RevenueProofBinding.TryBind(proof, result, publicKey, out _, out _));
        });
        test("revenue proof: cached prior renewal cannot accompany a later order's economics", () =>
        {
            Expect(RevenueProofBinding.TryBind(Proof(), result, publicKey, out var receipt, out var signature));
            var recovered = new RevenueProof(result.Store, result.ApplicationId, result.ProductId,
                RevenueProofStore.LedgerKey(result.Store, result.ApplicationId, result.TransactionId), receipt, signature, DateTimeOffset.UtcNow.AddDays(1));
            Expect(RevenueProofBinding.MatchesRecovered(recovered, result, publicKey));
            result.RevenueTransactionId = "GPA.next-renewal";
            Expect(!RevenueProofBinding.MatchesRecovered(recovered, result, publicKey));
            result.RevenueTransactionId = "GPA.test";
        });
        test("revenue proof: empty, malformed, oversized and missing-key proof is ignored", () =>
        {
            foreach (string receipt in new[] { "", "not-json", "{}", new string('x', RevenueProofBinding.MaximumProofBytes + 1) })
            {
                var proof = Proof(); proof.Receipt = receipt;
                Expect(!RevenueProofBinding.TryBind(proof, result, publicKey, out _, out _));
            }
            Expect(!RevenueProofBinding.TryBind(Proof(), result, null, out _, out _));
        });
        test("revenue proof: refund and mismatching transport identity are rejected", () =>
        {
            var proof = Proof(); proof.Store = ShorepopCatalog.AppleStore;
            Expect(!RevenueProofBinding.TryBind(proof, result, publicKey, out _, out _));
            result.Refunded = true;
            Expect(!RevenueProofBinding.TryBind(Proof(), result, publicKey, out _, out _));
            result.Refunded = false;
        });
        test("revenue proof: Apple requires the verified JWS and never retains unverified wrapper receipt", () =>
        {
            var apple = new ValidatedNativePurchase { Store = ShorepopCatalog.AppleStore, ApplicationId = result.ApplicationId,
                ProductId = result.ProductId, TransactionId = "apple-tx" };
            var proof = new NativePurchaseProof { Store = apple.Store, ApplicationId = apple.ApplicationId,
                ProductId = apple.ProductId, TransactionId = apple.TransactionId, Receipt = "unverified-wrapper" };
            Expect(!RevenueProofBinding.TryBind(proof, apple, null, out _, out _));
        });
    }
}
