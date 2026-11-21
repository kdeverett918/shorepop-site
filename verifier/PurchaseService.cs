using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shorepop.Verifier;

public sealed class PurchaseService(
    AppleJwsVerifier appleVerifier, AppleTransactionChecker apple, IAppleServerApi appleApi, IGooglePlayApi google,
    IPurchaseStore store, string googlePackageId, ILogger<PurchaseService> log, Func<DateTimeOffset>? clock = null,
    string appleBundleId = ShorepopCatalog.AppleBundleId)
{
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly ConcurrentDictionary<string, byte> loggedInstallMismatchConfirms = new(StringComparer.Ordinal);

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
        string? installId = ShorepopCatalog.NormalizeInstallId(proof.InstallId);

        StoreVerdict verdict;
        if (storeName == ShorepopCatalog.AppleStore)
        {
            if (string.IsNullOrEmpty(proof.AppleSignedTransaction)) throw new VerifierRefusal(422, "apple_signed_transaction_required");
            verdict = apple.Check(proof.AppleSignedTransaction, productId, transactionId, now());
            // In Production scope, when App Review presents a Sandbox transaction and the App Store Server API
            // is configured, confirm it via one-way Production -> Sandbox lookup and persist the canonical
            // server-verified Sandbox reference stamped test_order with zero revenue (WO-20261114-810).
            if (apple.ProductionScope && verdict.TestOrder && appleApi.Configured)
            {
                var lookup = await appleApi.LookupTransactionInfoAsync(transactionId, "Production", cancel);
                if (string.IsNullOrEmpty(lookup.SignedTransactionInfo))
                    throw new VerifierRefusal(422, "purchase_not_found");
                var canonical = ServerVerdict(lookup.SignedTransactionInfo, forceTestOrder: true);
                if (canonical.ProductId != productId || canonical.TransactionId != transactionId)
                    throw new VerifierRefusal(503, "apple_server_response_mismatch");
                verdict = canonical;
            }
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
        var stored = store.List(playerId, storeName, ApplicationIdFor(storeName)).First(r => r.TransactionId == transactionId);
        RegisterSubscriptionRevenueCharge(stored, playerId, installId, at);
        log.LogInformation("validate ok {Store} {Product} kind={Kind} env={Environment} testOrder={TestOrder} refunded={Refunded} tx={Tx}",
            storeName, productId, verdict.Kind, stored.Environment, stored.TestOrder, stored.Refunded, Digest(transactionId));
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
        string applicationId = ApplicationIdFor(storeName);
        RequireStore(storeName, string.IsNullOrEmpty(request.ApplicationId) ? applicationId : request.ApplicationId);
        string transactionId = request.TransactionId ?? "";
        if (string.IsNullOrWhiteSpace(transactionId) || transactionId.Length > 8192) throw new VerifierRefusal(422, "transaction_missing");
        switch (store.Ack(storeName, applicationId, transactionId, playerId, now()))
        {
            case AckOutcome.NotFound: throw new VerifierRefusal(404, "transaction_not_found");
            case AckOutcome.NotClaimant:
                log.LogWarning("ack refused {Store} tx={Tx}: player is not a claimant", storeName, Digest(transactionId));
                throw new VerifierRefusal(409, "transaction_owned_by_another_player");
        }
        log.LogInformation("ack ok {Store} tx={Tx}", storeName, Digest(transactionId));
        return new AckResult { TransactionId = transactionId, Acked = true };
    }

    /// <summary>
    /// Server-side Google Play acknowledge/consume with re-read recovery (WO-20261120-650).
    /// When Play's acknowledge/consume endpoint throws (for example because the client already confirmed),
    /// re-reads the purchase from Play, re-validates package, product, token, purchaseState, quantity and
    /// account binding, and settles as finished only when consumptionState==1 (consumable) or
    /// acknowledgementState==1 (non-consumable/subscription). Fails closed with distinct codes otherwise.
    /// </summary>
    public async Task<GoogleFinishResult> FinishGooglePurchaseAsync(string playerId, GoogleFinishRequest? request, CancellationToken cancel)
    {
        if (request == null) throw new VerifierRefusal(400, "body_required");
        string applicationId = string.IsNullOrEmpty(request.ApplicationId) ? googlePackageId : request.ApplicationId;
        RequireStore(ShorepopCatalog.GoogleStore, applicationId);
        string productId = request.ProductId ?? "";
        if (!ShorepopCatalog.ProductIds.Contains(productId)) throw new VerifierRefusal(422, "unknown_product");
        string token = request.TransactionId ?? "";
        if (string.IsNullOrWhiteSpace(token) || token.Length > 8192) throw new VerifierRefusal(422, "transaction_missing");
        if (!google.Configured) throw new VerifierRefusal(503, "google_validation_not_configured");

        var claimants = store.Claimants(ShorepopCatalog.GoogleStore, googlePackageId, token);
        if (claimants.Count == 0) throw new VerifierRefusal(404, "transaction_not_found");
        if (!claimants.Contains(playerId, StringComparer.Ordinal))
            throw new VerifierRefusal(403, "transaction_owned_by_another_player");

        var kind = ShorepopCatalog.CatalogKind(productId);
        try
        {
            if (kind == ProductKind.Consumable)
                await google.ConsumeProductAsync(googlePackageId, productId, token, cancel);
            else if (kind == ProductKind.Subscription)
                await google.AcknowledgeSubscriptionAsync(googlePackageId, productId, token, cancel);
            else
                await google.AcknowledgeProductAsync(googlePackageId, productId, token, cancel);

            store.Ack(ShorepopCatalog.GoogleStore, googlePackageId, token, playerId, now());
            string directState = kind == ProductKind.Consumable ? "consumed" : "acknowledged";
            log.LogInformation("google finish ok {Product} state={State} tx={Tx}", productId, directState, Digest(token));
            return new GoogleFinishResult { TransactionId = token, Finished = true, RecoveredFromReread = false, State = directState };
        }
        catch (VerifierRefusal refusal) when (refusal.Code == "google_validation_not_configured")
        {
            throw;
        }
        catch (Exception error) when (error is VerifierRefusal or HttpRequestException ||
            (error is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            log.LogWarning("google finish call failed; re-reading purchase state {Product} kind={Kind} tx={Tx}",
                productId, kind, Digest(token));
            JsonElement? reread;
            try
            {
                if (kind == ProductKind.Subscription)
                    reread = await google.GetSubscriptionPurchaseAsync(googlePackageId, token, cancel);
                else
                    reread = await google.GetLegacyProductPurchaseAsync(googlePackageId, productId, token, cancel)
                        ?? await google.GetProductPurchaseAsync(googlePackageId, token, cancel);
            }
            catch (Exception rereadError) when (rereadError is VerifierRefusal or HttpRequestException or JsonException ||
                (rereadError is OperationCanceledException && !cancel.IsCancellationRequested))
            {
                log.LogWarning("google finish reread unavailable {Product} tx={Tx}", productId, Digest(token));
                throw new VerifierRefusal(503, "google_finish_reread_unavailable");
            }
            if (reread == null)
            {
                log.LogWarning("google finish reread returned not found {Product} tx={Tx}", productId, Digest(token));
                throw new VerifierRefusal(403, "google_finish_purchase_not_found");
            }

            try
            {
                string verifiedState = GooglePlayInterpreter.ValidateFinishReread(
                    reread.Value, googlePackageId, productId, token, kind, request.AccountId, playerId);
                store.Ack(ShorepopCatalog.GoogleStore, googlePackageId, token, playerId, now());
                log.LogInformation("google finish recovered via reread {Product} state={State} tx={Tx}",
                    productId, verifiedState, Digest(token));
                return new GoogleFinishResult { TransactionId = token, Finished = true, RecoveredFromReread = true, State = verifiedState };
            }
            catch (VerifierRefusal refusal)
            {
                log.LogWarning("google finish reread refused {Product} status={Status} code={Code} tx={Tx}",
                    productId, refusal.Status, refusal.Code, Digest(token));
                throw;
            }
        }
    }

    /// <summary>Returns pending subscription revenue charges eligible for this install (WO-20261113-653).</summary>
    public IReadOnlyList<SubscriptionRevenueCharge> GetPendingSubscriptionRevenueCharges(
        string playerId, SubscriptionRevenuePendingRequest? request)
    {
        if (request == null) throw new VerifierRefusal(400, "body_required");
        string storeName = RequireStore(request.Store, request.ApplicationId);
        string? installId = ShorepopCatalog.NormalizeInstallId(request.InstallId);
        return store.ClaimPendingSubscriptionRevenueCharges(playerId, storeName, ApplicationIdFor(storeName), installId);
    }

    /// <summary>
    /// Confirms or retries a pending subscription revenue charge strictly for its owning install id (WO-20261113-653).
    /// Cross-install confirms are dropped (logged once) leaving the charge pending for the owning install.
    /// </summary>
    public SubscriptionRevenueConfirmResult ConfirmSubscriptionRevenue(string playerId, SubscriptionRevenueConfirmRequest? request)
    {
        if (request == null) throw new VerifierRefusal(400, "body_required");
        string storeName = RequireStore(request.Store, request.ApplicationId);
        string orderId = request.RevenueOrderId ?? "";
        if (string.IsNullOrWhiteSpace(orderId) || orderId.Length > 256) throw new VerifierRefusal(422, "transaction_missing");
        string? installId = ShorepopCatalog.NormalizeInstallId(request.InstallId);
        var result = store.ConfirmSubscriptionRevenueCharge(playerId, storeName, ApplicationIdFor(storeName), orderId, installId, now());
        if (result.Status == "ignored_install_mismatch")
        {
            string logKey = storeName + "|" + orderId + "|" + (installId ?? "anonymous");
            if (loggedInstallMismatchConfirms.TryAdd(logKey, 0))
                log.LogWarning("subscription revenue confirm ignored for non-owning install {Store} order={Order}", storeName, Digest(orderId));
        }
        return result;
    }

    /// <summary>Authorizes subscription revenue-proof recovery strictly for the owning install (WO-20261113-653).</summary>
    public bool AuthorizeSubscriptionRevenueProof(string playerId, string storeName, string applicationId, string revenueOrderId, string? rawInstallId)
    {
        RequireStore(storeName, applicationId);
        string? installId = ShorepopCatalog.NormalizeInstallId(rawInstallId);
        if (string.IsNullOrEmpty(revenueOrderId)) return true;
        return store.TryAuthorizeSubscriptionProofOffer(playerId, storeName, applicationId, revenueOrderId, installId);
    }

    public async Task<IReadOnlyList<ValidatedNativePurchase>> ReconcileAsync(string playerId, ReconcileRequest? request, CancellationToken cancel)
    {
        if (request == null) throw new VerifierRefusal(400, "body_required");
        string storeName = RequireStore(request.Store, request.ApplicationId);
        string applicationId = ApplicationIdFor(storeName);
        var rows = store.List(playerId, storeName, applicationId);
        if (storeName == ShorepopCatalog.AppleStore) await RefreshApple(playerId, rows, cancel);
        else await RefreshGoogle(playerId, rows, cancel);
        var results = store.List(playerId, storeName, applicationId).Select(Result).ToList();
        log.LogInformation("reconcile ok {Store} rows={Count} serverApi={ServerApi}", storeName, results.Count,
            storeName == ShorepopCatalog.AppleStore ? appleApi.Configured : google.Configured);
        return results;
    }

    /// <summary>Refresh one already-owned purchase for delayed revenue enrichment without replaying the whole ledger.</summary>
    public async Task<ValidatedNativePurchase> RefreshOwnedRevenueAsync(string playerId, string storeName,
        string applicationId, string transactionId, CancellationToken cancel, string? installId = null)
    {
        RequireStore(storeName, applicationId);
        string? normalizedInstallId = ShorepopCatalog.NormalizeInstallId(installId);
        var row = store.List(playerId, storeName, applicationId).FirstOrDefault(r => r.TransactionId == transactionId);
        if (row == null) throw new VerifierRefusal(404, "transaction_not_found");
        if (storeName == ShorepopCatalog.AppleStore) await RefreshApple(playerId, [row], cancel);
        else await RefreshGoogle(playerId, [row], cancel);
        var refreshed = store.List(playerId, storeName, applicationId).First(r => r.TransactionId == transactionId);
        RegisterSubscriptionRevenueCharge(refreshed, playerId, normalizedInstallId, now());
        return Result(refreshed);
    }

    private void RegisterSubscriptionRevenueCharge(PurchaseRecord row, string playerId, string? installId, DateTimeOffset at)
    {
        if (!ShorepopCatalog.IsSubscription(row.ProductId) || row.Refunded || row.RevenueRefunded ||
            row.TestOrder || ShorepopCatalog.IsTestEnvironment(row.Environment) || row.RevenueTransactionId.Length == 0)
            return;
        store.UpsertSubscriptionRevenueCharge(new SubscriptionRevenueCharge(
            row.Store, row.ApplicationId, row.TransactionId, row.RevenueTransactionId,
            playerId, installId, false, false, at.ToUnixTimeMilliseconds(), null));
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
                    var verdict = apple.Interpret(payload, row.SignedPayload, forceTestOrder: row.TestOrder);
                    if (verdict.TransactionId != row.TransactionId) throw new AppleVerificationException("mismatch");
                    Ingest(playerId, verdict, row.TransactionId);
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
            var lookup = await appleApi.LookupTransactionInfoAsync(row.TransactionId, row.Environment, cancel);
            if (lookup.SignedTransactionInfo != null)
            {
                bool testOrder = row.TestOrder || lookup.UsedSandboxFallback || (apple.ProductionScope && lookup.ResolvedEnvironment == "Sandbox");
                Ingest(playerId, ServerVerdict(lookup.SignedTransactionInfo, forceTestOrder: testOrder), row.TransactionId);
            }
            if (ShorepopCatalog.IsSubscription(row.ProductId) && subscriptionOriginals.Add(row.OriginalRef ?? row.TransactionId))
            {
                // Renewals are new transactions under the same originalTransactionId: claim them for this player.
                var subLookup = await appleApi.LookupSubscriptionLastTransactionsAsync(row.TransactionId, row.Environment, cancel);
                bool subTestOrder = row.TestOrder || subLookup.UsedSandboxFallback || (apple.ProductionScope && subLookup.ResolvedEnvironment == "Sandbox");
                foreach (var latest in subLookup.SignedTransactions)
                {
                    var verdict = ServerVerdict(latest, forceTestOrder: subTestOrder);
                    if (verdict.OriginalTransactionId != null && verdict.OriginalTransactionId == (row.OriginalRef ?? row.TransactionId))
                        Ingest(playerId, verdict, null);
                }
            }
        }
    }

    private StoreVerdict ServerVerdict(string signed, bool forceTestOrder = false)
    {
        try { return apple.Interpret(appleVerifier.VerifyAndDecode(signed, now()), signed, forceTestOrder); }
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
            catch (VerifierRefusal refusal) when (refusal.Status == 422 && refusal.Code == "purchase_not_found")
            {
                // Google no longer returns this token (purged): keep the last verified state.
                continue;
            }
            catch (VerifierRefusal refusal) when (refusal.Status == 422)
            {
                // A malformed or mismatched provider response is not evidence that an old token
                // was purged. Keep the ledger intact, but make the caller retry the refresh.
                throw new VerifierRefusal(503, "google_server_response_unverifiable");
            }
            var at = now();
            store.Bind(Record(playerId, verdict), at, context => Decide(context, playerId, verdict, at, allowTransfer: false));
        }
    }

    private async Task<StoreVerdict> GoogleVerdict(string productId, string token, CancellationToken cancel)
    {
        if (!google.Configured) throw new VerifierRefusal(503, "google_validation_not_configured");
        bool subscription = ShorepopCatalog.IsSubscription(productId);
        long observedAt = now().ToUnixTimeMilliseconds();
        JsonElement? body = subscription
            ? await google.GetSubscriptionPurchaseAsync(googlePackageId, token, cancel)
            : await google.GetProductPurchaseAsync(googlePackageId, token, cancel);
        if (body == null) throw new VerifierRefusal(422, "purchase_not_found");
        var verdict = subscription
            ? GooglePlayInterpreter.Subscription(body.Value, googlePackageId, productId, token)
            : GooglePlayInterpreter.Product(body.Value, googlePackageId, productId, token);

        bool allowCashRevenue = !verdict.TestOrder && !ShorepopCatalog.IsTestEnvironment(verdict.Environment);
        if (!subscription && allowCashRevenue)
        {
            // Check Play v1 purchaseType by key presence (WO-20261020-14): 0 = LicenseTest,
            // 1 = Promo / 2 = Rewarded / malformed = non-cash without mislabeling a real player internal.
            try
            {
                using var legacyCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                legacyCancel.CancelAfter(TimeSpan.FromSeconds(2));
                var legacy = await google.GetLegacyProductPurchaseAsync(googlePackageId, productId, token, legacyCancel.Token);
                if (legacy != null)
                {
                    var purchaseType = GooglePlayInterpreter.ClassifyPurchaseType(legacy.Value);
                    if (purchaseType == GooglePurchaseType.Test)
                    {
                        verdict = verdict with { Environment = "LicenseTest", TestOrder = true };
                        allowCashRevenue = false;
                    }
                    else if (purchaseType != GooglePurchaseType.Charged)
                    {
                        allowCashRevenue = false;
                    }
                }
            }
            catch (Exception error) when (error is VerifierRefusal or HttpRequestException or JsonException ||
                (error is OperationCanceledException && !cancel.IsCancellationRequested))
            {
                log.LogWarning("google v1 purchaseType lookup unavailable; keeping v2 verdict tx={Tx}", Digest(token));
            }
        }

        // Internal snapshot ordering only; the authenticated response's actual settlement time is
        // reported separately, and is left unknown when the store has not supplied it.
        verdict = verdict with { SignedDateUnixMs = observedAt };
        if (!allowCashRevenue) return verdict;

        string orderId = GooglePlayInterpreter.RevenueOrderId(body.Value, productId, subscription);
        if (orderId.Length == 0 || orderId.Length > 256 || orderId == token) return verdict;
        verdict = verdict with
        {
            RevenueTransactionId = orderId,
            RevenuePurchasedAtUnixMs = GooglePlayInterpreter.RevenuePurchaseTime(body.Value, subscription),
        };
        try
        {
            // This optional lookup must not consume the entire purchase-validation request window.
            using var economicsCancel = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            economicsCancel.CancelAfter(TimeSpan.FromSeconds(2));
            var order = await google.GetOrderAsync(googlePackageId, orderId, economicsCancel.Token);
            if (order != null) verdict = GooglePlayInterpreter.WithOrderRevenue(verdict, order.Value, orderId, token, body.Value);
        }
        catch (Exception error) when (error is VerifierRefusal or HttpRequestException or JsonException ||
            (error is OperationCanceledException && !cancel.IsCancellationRequested))
        {
            // Order economics are enrichment; a permission failure, timeout or transient order API
            // outage must not prevent a separately verified purchase from granting. Reconcile retries.
            log.LogWarning("google order economics unavailable; purchase remains verified tx={Tx}", Digest(token));
        }
        return verdict;
    }

    private string RequireStore(string? storeName, string? applicationId)
    {
        if (applicationId != ApplicationIdFor(storeName)) throw new VerifierRefusal(422, "application_mismatch");
        return storeName!;
    }

    private string ApplicationIdFor(string? storeName) => storeName switch
    {
        ShorepopCatalog.AppleStore => appleBundleId,
        ShorepopCatalog.GoogleStore => googlePackageId,
        _ => throw new VerifierRefusal(422, "unknown_store"),
    };

    private static PurchaseRecord Record(string playerId, StoreVerdict v) => new(v.Store, v.ApplicationId, v.TransactionId, playerId, v.ProductId,
        v.Environment, v.Refunded, v.PurchasedPeriodExpiryUtcTicks, v.OriginalTransactionId, v.SignedPayload, v.SignedDateUnixMs,
        v.RevenueVerified, v.GrossRevenueMicros, v.RevenueCurrency, v.RevenueTransactionId, v.RevenuePurchasedAtUnixMs, v.RevenueRefunded,
        "[]", v.TestOrder);

    private ValidatedNativePurchase Result(PurchaseRecord row)
    {
        var result = new ValidatedNativePurchase
        {
            Store = row.Store, ApplicationId = row.ApplicationId, ProductId = row.ProductId, TransactionId = row.TransactionId,
            Refunded = row.Refunded, Environment = row.Environment,
            RevenueVerified = row.RevenueVerified, GrossRevenueMicros = row.GrossRevenueMicros,
            RevenueCurrency = row.RevenueCurrency, RevenueTransactionId = row.RevenueTransactionId,
            RevenuePurchasedAtUnixMs = row.RevenuePurchasedAtUnixMs,
            RevenueRefunded = row.RevenueRefunded,
            TestOrder = row.TestOrder,
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
