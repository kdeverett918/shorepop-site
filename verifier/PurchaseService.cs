using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shorepop.Verifier;

public sealed class PurchaseService(
    AppleJwsVerifier appleVerifier, AppleTransactionChecker apple, IAppleServerApi appleApi, IGooglePlayApi google,
    IPurchaseStore store, string bundleId, ILogger<PurchaseService> log, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public async Task<ValidatedNativePurchase> ValidateAsync(string playerId, NativePurchaseProof? proof, CancellationToken cancel)
    {
        if (proof == null) throw new VerifierRefusal(400, "body_required");
        string storeName = RequireStore(proof.Store, proof.ApplicationId);
        string productId = proof.ProductId ?? "";
        if (!ShorepopCatalog.ProductIds.Contains(productId)) throw new VerifierRefusal(422, "unknown_product");
        string transactionId = proof.TransactionId ?? "";
        if (string.IsNullOrWhiteSpace(transactionId) || transactionId.Length > 8192) throw new VerifierRefusal(422, "transaction_missing");

        StoreVerdict verdict;
        if (storeName == ShorepopCatalog.AppleStore)
        {
            if (string.IsNullOrEmpty(proof.AppleSignedTransaction)) throw new VerifierRefusal(422, "apple_signed_transaction_required");
            verdict = apple.Check(proof.AppleSignedTransaction, productId, transactionId, now());
        }
        else verdict = await GoogleVerdict(productId, transactionId, cancel);

        var record = Record(playerId, verdict);
        string owner = store.Bind(record);
        if (owner != playerId)
        {
            log.LogWarning("validate refused {Store} {Product} tx={Tx}: owned by another player", storeName, productId, Digest(transactionId));
            throw new VerifierRefusal(409, "transaction_owned_by_another_player");
        }
        var stored = store.List(playerId, storeName, bundleId).First(r => r.TransactionId == transactionId);
        log.LogInformation("validate ok {Store} {Product} env={Environment} refunded={Refunded} tx={Tx}",
            storeName, productId, stored.Environment, stored.Refunded, Digest(transactionId));
        return Result(stored);
    }

    public async Task<IReadOnlyList<ValidatedNativePurchase>> ReconcileAsync(string playerId, ReconcileRequest? request, CancellationToken cancel)
    {
        if (request == null) throw new VerifierRefusal(400, "body_required");
        string storeName = RequireStore(request.Store, request.ApplicationId);
        var rows = store.List(playerId, storeName, bundleId);
        if (storeName == ShorepopCatalog.AppleStore) await RefreshApple(playerId, rows, cancel);
        else await RefreshGoogle(playerId, rows, cancel);
        var results = store.List(playerId, storeName, bundleId).Select(Result).ToList();
        log.LogInformation("reconcile ok {Store} rows={Count} serverApi={ServerApi}", storeName, results.Count,
            storeName == ShorepopCatalog.AppleStore ? appleApi.Configured : google.Configured);
        return results;
    }

    private async Task RefreshApple(string playerId, IReadOnlyList<PurchaseRecord> rows, CancellationToken cancel)
    {
        if (!appleApi.Configured)
        {
            // No In-App Purchase key: re-verify the stored signed transactions (at their own signedDate, since
            // the leaf may have rotated since) and return stored state. Refunds after purchase stay invisible.
            foreach (var row in rows)
            {
                try
                {
                    if (row.SignedPayload == null) throw new AppleVerificationException("missing");
                    var payload = appleVerifier.VerifyAndDecode(row.SignedPayload, DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(1, row.SignedDateUnixMs)));
                    var verdict = apple.Interpret(payload, row.SignedPayload);
                    if (verdict.TransactionId != row.TransactionId) throw new AppleVerificationException("mismatch");
                }
                catch (Exception error) when (error is AppleVerificationException or VerifierRefusal)
                {
                    log.LogError("reconcile: stored Apple transaction failed re-verification tx={Tx}", Digest(row.TransactionId));
                    throw new VerifierRefusal(503, "stored_transaction_unverifiable");
                }
            }
            return;
        }
        var subscriptionOriginals = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            string? signed = await appleApi.GetTransactionInfoAsync(row.TransactionId, row.Environment, cancel);
            if (signed != null) Ingest(playerId, ServerVerdict(signed), row.TransactionId);
            if (ShorepopCatalog.IsSubscription(row.ProductId) && subscriptionOriginals.Add(row.OriginalRef ?? row.TransactionId))
            {
                // Renewals are new transactions under the same originalTransactionId: bind them to this player.
                foreach (var latest in await appleApi.GetSubscriptionLastTransactionsAsync(row.TransactionId, row.Environment, cancel))
                {
                    var verdict = ServerVerdict(latest);
                    if (verdict.OriginalTransactionId != null && verdict.OriginalTransactionId == (row.OriginalRef ?? row.TransactionId))
                        Ingest(playerId, verdict, null);
                }
            }
        }
    }

    private StoreVerdict ServerVerdict(string signed)
    {
        try { return apple.Interpret(appleVerifier.VerifyAndDecode(signed, now()), signed); }
        catch (AppleVerificationException) { throw new VerifierRefusal(503, "apple_server_response_unverifiable"); }
    }

    private void Ingest(string playerId, StoreVerdict verdict, string? expectedTransactionId)
    {
        if (expectedTransactionId != null && verdict.TransactionId != expectedTransactionId) throw new VerifierRefusal(503, "apple_server_response_mismatch");
        if (store.Bind(Record(playerId, verdict)) != playerId)
            log.LogWarning("reconcile: renewal tx={Tx} already owned by another player; skipped", Digest(verdict.TransactionId));
    }

    private async Task RefreshGoogle(string playerId, IReadOnlyList<PurchaseRecord> rows, CancellationToken cancel)
    {
        if (!google.Configured) throw new VerifierRefusal(503, "google_validation_not_configured");
        foreach (var row in rows)
        {
            StoreVerdict verdict;
            try { verdict = await GoogleVerdict(row.ProductId, row.TransactionId, cancel); }
            catch (VerifierRefusal refusal) when (refusal.Status == 422)
            {
                // Google no longer returns this token (purged): keep the last verified state.
                continue;
            }
            store.Bind(Record(playerId, verdict));
        }
    }

    private async Task<StoreVerdict> GoogleVerdict(string productId, string token, CancellationToken cancel)
    {
        if (!google.Configured) throw new VerifierRefusal(503, "google_validation_not_configured");
        bool subscription = ShorepopCatalog.IsSubscription(productId);
        JsonElement? body = subscription
            ? await google.GetSubscriptionPurchaseAsync(bundleId, token, cancel)
            : await google.GetProductPurchaseAsync(bundleId, token, cancel);
        if (body == null) throw new VerifierRefusal(422, "purchase_not_found");
        return subscription
            ? GooglePlayInterpreter.Subscription(body.Value, bundleId, productId, token)
            : GooglePlayInterpreter.Product(body.Value, bundleId, productId, token);
    }

    private string RequireStore(string? storeName, string? applicationId)
    {
        if (storeName != ShorepopCatalog.AppleStore && storeName != ShorepopCatalog.GoogleStore) throw new VerifierRefusal(422, "unknown_store");
        if (applicationId != bundleId) throw new VerifierRefusal(422, "application_mismatch");
        return storeName;
    }

    private static PurchaseRecord Record(string playerId, StoreVerdict v) => new(v.Store, v.ApplicationId, v.TransactionId, playerId, v.ProductId,
        v.Environment, v.Refunded, v.PurchasedPeriodExpiryUtcTicks, v.OriginalTransactionId, v.SignedPayload, v.SignedDateUnixMs);

    private ValidatedNativePurchase Result(PurchaseRecord row)
    {
        var result = new ValidatedNativePurchase
        {
            Store = row.Store, ApplicationId = row.ApplicationId, ProductId = row.ProductId, TransactionId = row.TransactionId,
            Refunded = row.Refunded, Environment = row.Environment,
        };
        if (ShorepopCatalog.IsSubscription(row.ProductId))
        {
            var (revision, current) = store.Subscription(row.PlayerId, row.Store, row.ApplicationId);
            result.PurchasedPeriodExpiryUtcTicks = row.PurchasedPeriodExpiryUtcTicks;
            result.SubscriptionEntitlementRevision = revision;
            result.CurrentSubscriptionExpiryUtcTicks = current;
        }
        return result;
    }

    /// <summary>Log-safe transaction reference: 12 hex chars of SHA-256. Raw ids/tokens are never logged.</summary>
    public static string Digest(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
}
