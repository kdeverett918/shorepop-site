using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shorepop.Verifier;

public sealed class PurchaseService(
    AppleJwsVerifier appleVerifier, AppleTransactionChecker apple, IAppleServerApi appleApi, IGooglePlayApi google,
    IPurchaseStore store, string bundleId, ILogger<PurchaseService> log, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    /// <summary>An unacked Apple consumable may be taken over by any player this long after its last claim.</summary>
    public static readonly TimeSpan ReclaimAfter = TimeSpan.FromHours(24);
    /// <summary>Distinct players that may hold one entitlement (per original transaction); the token-matched player is never capped.</summary>
    public const int MaxEntitlementClaimants = 5;
    /// <summary>A restore proof for a token-bound entitlement must be signed by Apple within this window.</summary>
    public static readonly TimeSpan RestoreFreshness = TimeSpan.FromHours(24);
    private static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(5);

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

        string derived = AppAccountTokens.Derive(playerId);
        string? claimed = AppAccountTokens.Normalize(proof.AppAccountToken);
        if (!string.IsNullOrEmpty(proof.AppAccountToken) && claimed != derived)
            log.LogWarning("validate: client AppAccountToken differs from the server derivation (client derivation bug?) tx={Tx}", Digest(transactionId));

        var at = now();
        var result = store.Bind(Record(playerId, verdict), at, context => Decide(context, playerId, verdict, at, allowTransfer: true));
        if (result.Decision.Action == ClaimAction.Refuse)
        {
            log.LogWarning("validate refused {Store} {Product} kind={Kind} tx={Tx} code={Code} tokenInJws={Token} claimants={Claimants}",
                storeName, productId, verdict.Kind, Digest(transactionId), result.Decision.RefusalCode, verdict.AppAccountToken != null, result.PreviousClaimants.Count);
            throw new VerifierRefusal(409, result.Decision.RefusalCode ?? "transaction_owned_by_another_player");
        }
        if (result.Decision.Action == ClaimAction.Transfer)
            log.LogWarning("validate transferred unacked consumable {Store} {Product} tx={Tx} reason={Reason} from={From} to={To}",
                storeName, productId, Digest(transactionId), result.Decision.Reason,
                string.Join(",", result.PreviousClaimants.Select(Digest)), Digest(playerId));
        var stored = store.List(playerId, storeName, bundleId).First(r => r.TransactionId == transactionId);
        log.LogInformation("validate ok {Store} {Product} kind={Kind} env={Environment} refunded={Refunded} tx={Tx}",
            storeName, productId, verdict.Kind, stored.Environment, stored.Refunded, Digest(transactionId));
        return Result(stored);
    }

    /// <summary>
    /// Who may hold a transaction. Consumables: one owner. A second player may take over an Apple consumable
    /// only while the owner has not acked a durable grant, and only when (a) the verified JWS carries an
    /// appAccountToken equal to the requester's derivation (any time), or (b) the last claim is at least
    /// <see cref="ReclaimAfter"/> old (any player, token or not). Entitlements: any number of claims up to
    /// <see cref="MaxEntitlementClaimants"/> distinct players per original transaction; when the JWS carries a token,
    /// a new claimant must match it or present a restore (a JWS for the same original transaction freshly re-signed
    /// by Apple, newer than any the verifier has stored).
    /// </summary>
    public static ClaimDecision Decide(ClaimContext context, string playerId, StoreVerdict verdict, DateTimeOffset at, bool allowTransfer)
    {
        string derived = AppAccountTokens.Derive(playerId);
        bool tokenMatches = verdict.AppAccountToken != null && verdict.AppAccountToken == derived;
        if (verdict.Kind == ProductKind.Consumable)
        {
            if (context.TransactionClaims.Count == 0 || context.TransactionClaims.Any(c => c.PlayerId == playerId)) return ClaimDecision.Claimed;
            const string owned = "transaction_owned_by_another_player";
            if (!allowTransfer || verdict.Store != ShorepopCatalog.AppleStore) return ClaimDecision.Refused(owned);
            if (context.TransactionClaims.Any(c => c.AckedUtc != null)) return ClaimDecision.Refused(owned);
            if (tokenMatches) return ClaimDecision.Transferred("app_account_token");
            // Unacked 24 h after the last claim: the owner never durably granted (e.g. killed before commit, then
            // reinstalled as a new anonymous player whose derived token cannot match), so any player may take over.
            var bound = context.TransactionClaims.Max(c => c.BoundUtc);
            if (at - bound < ReclaimAfter) return ClaimDecision.Refused(owned);
            return ClaimDecision.Transferred(verdict.AppAccountToken != null ? "unacked_24h_mismatch" : "unacked_24h");
        }

        if (context.GroupClaimants.Contains(playerId) || context.TransactionClaims.Any(c => c.PlayerId == playerId)) return ClaimDecision.Claimed;
        if (tokenMatches) return ClaimDecision.Claimed;
        if (verdict.Store == ShorepopCatalog.AppleStore && verdict.AppAccountToken != null)
        {
            var signed = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(0, verdict.SignedDateUnixMs));
            bool restore = verdict.SignedDateUnixMs > context.GroupLatestSignedDateMs &&
                signed <= at + ClockSkew && at - signed <= RestoreFreshness;
            if (!restore) return ClaimDecision.Refused("app_account_token_mismatch");
        }
        if (context.GroupClaimants.Count >= MaxEntitlementClaimants) return ClaimDecision.Refused("claimant_limit_reached");
        return ClaimDecision.Claimed;
    }

    /// <summary>The client durably committed the grant: an acked consumable can never move to another player.</summary>
    public AckResult Ack(string playerId, AckRequest? request)
    {
        if (request == null) throw new VerifierRefusal(400, "body_required");
        string storeName = string.IsNullOrEmpty(request.Store) ? ShorepopCatalog.AppleStore : request.Store;
        RequireStore(storeName, string.IsNullOrEmpty(request.ApplicationId) ? bundleId : request.ApplicationId);
        string transactionId = request.TransactionId ?? "";
        if (string.IsNullOrWhiteSpace(transactionId) || transactionId.Length > 8192) throw new VerifierRefusal(422, "transaction_missing");
        switch (store.Ack(storeName, bundleId, transactionId, playerId, now()))
        {
            case AckOutcome.NotFound: throw new VerifierRefusal(404, "transaction_not_found");
            case AckOutcome.NotClaimant:
                log.LogWarning("ack refused {Store} tx={Tx}: player is not a claimant", storeName, Digest(transactionId));
                throw new VerifierRefusal(409, "transaction_owned_by_another_player");
        }
        log.LogInformation("ack ok {Store} tx={Tx}", storeName, Digest(transactionId));
        return new AckResult { TransactionId = transactionId, Acked = true };
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
                // Renewals are new transactions under the same originalTransactionId: claim them for this player.
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
        var at = now();
        var result = store.Bind(Record(playerId, verdict), at, context => Decide(context, playerId, verdict, at, allowTransfer: false));
        if (result.Decision.Action == ClaimAction.Refuse)
            log.LogWarning("reconcile: tx={Tx} not bound ({Code}); skipped", Digest(verdict.TransactionId), result.Decision.RefusalCode);
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
            var at = now();
            store.Bind(Record(playerId, verdict), at, context => Decide(context, playerId, verdict, at, allowTransfer: false));
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
