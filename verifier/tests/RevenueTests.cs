using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shorepop.Verifier;

internal static class RevenueTests
{
    private const string Product = "gg.shorepop.gems.80";
    private const string Token = "fixture-purchase-token-not-an-analytics-id";
    private const string OrderId = "GPA.1111-2222-3333-44444";
    private const long PurchasedAt = 1791633600000; // 2026-10-10T12:00:00Z

    public static void Run(Action<bool, string> check)
    {
        var chain = TestChain.Create();
        var verifier = new AppleJwsVerifier([chain.RootDer]);
        var apple = new AppleTransactionChecker(verifier, ShorepopCatalog.BundleId, AppleTransactionChecker.DefaultEnvironments);
        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var applePayload = new Dictionary<string, object>
        {
            ["bundleId"] = ShorepopCatalog.BundleId, ["environment"] = "Sandbox", ["productId"] = Product,
            ["transactionId"] = "revenue-apple-1", ["quantity"] = 1, ["type"] = "Consumable",
            ["signedDate"] = now, ["purchaseDate"] = now - 1000, ["price"] = 1990L, ["currency"] = "USD",
        };
        StoreVerdict Apple() => apple.Check(chain.Sign(applePayload), Product, "revenue-apple-1", DateTimeOffset.UtcNow);
        var signedVerdict = Apple();
        check(signedVerdict.RevenueVerified && signedVerdict.GrossRevenueMicros == 1_990_000 &&
            signedVerdict.RevenueCurrency == "USD" && signedVerdict.RevenueTransactionId == "revenue-apple-1" &&
            signedVerdict.RevenuePurchasedAtUnixMs == now - 1000, "Apple signed milliunits map to exact micros and transaction id");
        applePayload["price"] = 300000L;
        applePayload["currency"] = "JPY";
        check(Apple().GrossRevenueMicros == 300_000_000, "Apple zero-decimal currency still uses milliunits");
        applePayload["price"] = 0L;
        check(Apple().RevenueVerified && Apple().GrossRevenueMicros == 0, "Apple free purchase remains verified zero");
        foreach (object invalid in new object[] { -1L, long.MaxValue, "1990", 1.5 })
        {
            applePayload["price"] = invalid;
            check(!Apple().RevenueVerified, "invalid or overflowing Apple price stays unknown without rejecting grant");
        }
        applePayload.Remove("price");
        check(!Apple().RevenueVerified, "older Apple transaction without price remains valid, not estimated");
        applePayload["price"] = 1990L;
        foreach (string invalid in new[] { "usd", "US", "USDD", "U$D" })
        {
            applePayload["currency"] = invalid;
            check(!Apple().RevenueVerified, "Apple currency format must be ISO alpha-3");
        }
        applePayload["currency"] = "USD";
        applePayload.Remove("purchaseDate");
        check(!Apple().RevenueVerified, "Apple missing purchase date cannot become an analytics timestamp");
        applePayload["purchaseDate"] = now - 1000;

        var purchase = ProductPurchase();
        var verdict = GooglePlayInterpreter.Product(Element(purchase), ShorepopCatalog.BundleId, Product, Token);
        check(GooglePlayInterpreter.RevenueOrderId(Element(purchase), Product, false) == OrderId, "Google uses verified product order id");
        StoreVerdict Enrich(JsonObject order) => GooglePlayInterpreter.WithOrderRevenue(verdict, Element(order), OrderId, Token, Element(purchase));
        var googleVerdict = Enrich(Order());
        check(googleVerdict.RevenueVerified && googleVerdict.GrossRevenueMicros == 2_490_000 &&
            googleVerdict.RevenueCurrency == "USD" && googleVerdict.RevenueTransactionId == OrderId &&
            googleVerdict.RevenuePurchasedAtUnixMs == PurchasedAt, "Google reports order total after discount/tax, not listed price or developer net");
        check(googleVerdict.TransactionId == Token && googleVerdict.RevenueTransactionId != Token, "purchase token stays internal to grant identity");
        var mismatch = Order(); mismatch["orderId"] = "GPA.other";
        check(!Enrich(mismatch).RevenueVerified, "different Google order cannot supply revenue");
        mismatch = Order(); mismatch["purchaseToken"] = "different-token";
        check(!Enrich(mismatch).RevenueVerified, "different Google purchase token cannot supply revenue");
        mismatch = Order(); mismatch["lineItems"]![0]!["productId"] = "gg.shorepop.gems.260";
        check(!Enrich(mismatch).RevenueVerified, "different Google product cannot supply revenue");
        mismatch = Order(); mismatch["lineItems"]!.AsArray().Add(mismatch["lineItems"]![0]!.DeepClone());
        check(!Enrich(mismatch).RevenueVerified, "multi-item Google order cannot overstate one product's revenue");
        mismatch = Order(); mismatch["lineItems"]![0]!["oneTimePurchaseDetails"]!["quantity"] = 2;
        check(!Enrich(mismatch).RevenueVerified, "multi-quantity Google order is not treated as one purchase");
        foreach (string state in new[] { "PENDING", "CANCELED", "REFUNDED", "STATE_UNSPECIFIED" })
        {
            mismatch = Order(); mismatch["state"] = state;
            check(!Enrich(mismatch).RevenueVerified, "unsettled/refunded order does not become new positive revenue");
        }
        mismatch = Order(); mismatch["state"] = "REFUNDED";
        check(!Enrich(mismatch).Refunded && Enrich(mismatch).RevenueRefunded && Enrich(mismatch).RevenueTransactionId == OrderId,
            "fully refunded Google order suppresses revenue without revoking continuing access");
        mismatch.Remove("total");
        check(Enrich(mismatch).RevenueRefunded && !Enrich(mismatch).RevenueVerified,
            "Google full refund is known even without price economics");
        mismatch["purchaseToken"] = "different-token";
        check(!Enrich(mismatch).RevenueRefunded, "a refund on another Google token cannot change this purchase economics");
        foreach (string state in new[] { "PENDING_REFUND", "PARTIALLY_REFUNDED" })
        {
            mismatch = Order(); mismatch["state"] = state;
            check(!Enrich(mismatch).Refunded && !Enrich(mismatch).RevenueRefunded,
                "partial or pending Google refund does not mark a full refund or revoke entitlement");
        }
        mismatch = Order(); mismatch.Remove("total");
        check(!Enrich(mismatch).RevenueVerified, "Google missing order total is not replaced by listing price");
        mismatch = Order(); mismatch["total"]!["currencyCode"] = "usd";
        check(!Enrich(mismatch).RevenueVerified, "Google currency format is checked");
        foreach (string units in new[] { "-1", long.MaxValue.ToString(), "1.5" })
        {
            mismatch = Order(); mismatch["total"]!["units"] = units;
            check(!Enrich(mismatch).RevenueVerified, "invalid/overflowing Google units remain unknown");
        }
        foreach (int nanos in new[] { -1, 1_000_000_000, 1 })
        {
            mismatch = Order(); mismatch["total"]!["nanos"] = nanos;
            check(!Enrich(mismatch).RevenueVerified, "Google nanos must be nonnegative, bounded and exactly representable as micros");
        }
        mismatch = Order(); mismatch["total"] = new JsonObject { ["currencyCode"] = "USD" };
        check(Enrich(mismatch).RevenueVerified && Enrich(mismatch).GrossRevenueMicros == 0, "Google protobuf omitted zeros form a verified free order");
        mismatch = Order(); mismatch.Remove("orderHistory");
        check(!Enrich(mismatch).RevenueVerified, "Google order creation time is not misreported as successful payment time");
        purchase["purchaseCompletionTime"] = "2026-10-10T12:00:00Z";
        check(Enrich(mismatch).RevenuePurchasedAtUnixMs == PurchasedAt, "verified product completion time is a valid settlement fallback");
        purchase.Remove("purchaseCompletionTime");

        var subscription = SubscriptionPurchase();
        check(GooglePlayInterpreter.RevenueOrderId(Element(subscription), ShorepopCatalog.SubscriptionProductId, true) == OrderId + "..1",
            "Google subscription uses line-item latest successful order, not root latest pending order");
        var duplicateLine = subscription["lineItems"]![0]!.DeepClone();
        duplicateLine["latestSuccessfulOrderId"] = OrderId + "..2";
        subscription["lineItems"]!.AsArray().Add(duplicateLine);
        check(GooglePlayInterpreter.RevenueOrderId(Element(subscription), ShorepopCatalog.SubscriptionProductId, true) == "",
            "ambiguous subscription item economics remain unknown");

        foreach (var (label, makeStore) in StoreFactories())
        {
            string file = Path.Combine(Path.GetTempPath(), "shorepop-revenue-" + Guid.NewGuid().ToString("N") + ".db");
            try
            {
                var store = makeStore(file);
                var row = Record("store-revenue-1", googleVerdict);
                store.Bind(row, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                var reopened = label == "sqlite" ? makeStore(file) : store;
                var loaded = reopened.List("player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId).Single();
                check(loaded.RevenueVerified && loaded.GrossRevenueMicros == 2_490_000 && loaded.RevenueTransactionId == OrderId &&
                    loaded.RevenuePurchasedAtUnixMs == PurchasedAt, label + " verified economics survive persistence/reopen");
                reopened.Bind(row with { RevenueVerified = false, GrossRevenueMicros = 0, RevenueCurrency = "", RevenueTransactionId = "", RevenuePurchasedAtUnixMs = 0 },
                    DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                check(reopened.List("player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId).Single().RevenueTransactionId == OrderId,
                    label + " unknown reconciliation retains previously verified economics");
                reopened.Bind(row with { RevenueTransactionId = OrderId + "..2", RevenuePurchasedAtUnixMs = PurchasedAt + 1000 },
                    DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                reopened.Bind(row, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                check(reopened.List("player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId).Single().RevenueTransactionId == OrderId + "..2",
                    label + " late old response cannot roll back latest renewal economics");

                var api = new RevenueGoogle { Purchase = Element(ProductPurchase()), Order = null };
                var serviceStore = makeStore(file + ".service");
                var service = new PurchaseService(verifier, apple, new FakeApple(), api, serviceStore, ShorepopCatalog.BundleId, NullLogger<PurchaseService>.Instance, appleBundleId: ShorepopCatalog.BundleId);
                var proof = new NativePurchaseProof { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.BundleId,
                    ProductId = Product, TransactionId = Token };
                var unknown = service.ValidateAsync("player", proof, default).GetAwaiter().GetResult();
                check(!unknown.RevenueVerified && unknown.ProductId == Product && !unknown.Refunded, label + " unavailable Google order does not block grant");
                api.OrderError = new VerifierRefusal(503, "google_api_unavailable");
                check(!service.RefreshOwnedRevenueAsync("player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId, Token, default)
                    .GetAwaiter().GetResult().RevenueVerified, label + " Google order outage leaves verified grant usable");
                api.OrderError = new OperationCanceledException();
                check(!service.RefreshOwnedRevenueAsync("player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId, Token, default)
                    .GetAwaiter().GetResult().RevenueVerified, label + " optional Google economics timeout does not reject verified purchase");
                api.OrderError = null; api.Order = Element(Order());
                var enriched = service.RefreshOwnedRevenueAsync("player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId, Token, default).GetAwaiter().GetResult();
                check(enriched.RevenueVerified && enriched.GrossRevenueMicros == 2_490_000 && enriched.RevenueTransactionId == OrderId && api.LastOrderId == OrderId,
                    label + " delayed order economics populate through owned-transaction refresh");
                api.Order = null;
                check(service.ReconcileAsync("player", new ReconcileRequest { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.BundleId }, default)
                    .GetAwaiter().GetResult().Single().RevenueVerified, label + " reconciliation does not erase stored verified economics");
                var refundedOrder = Order(); refundedOrder["state"] = "REFUNDED"; api.Order = Element(refundedOrder);
                var refundedRevenue = service.RefreshOwnedRevenueAsync("player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId, Token, default)
                    .GetAwaiter().GetResult();
                check(refundedRevenue.RevenueRefunded && !refundedRevenue.Refunded && refundedRevenue.RevenueVerified &&
                    refundedRevenue.GrossRevenueMicros == 2_490_000,
                    label + " price-less full refund preserves historical gross and suppresses positive revenue without revocation");
                api.Order = Element(Order());
                check(service.RefreshOwnedRevenueAsync("player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId, Token, default)
                    .GetAwaiter().GetResult().RevenueRefunded, label + " old processed order cannot clear a recorded same-order money refund");
                try
                {
                    service.RefreshOwnedRevenueAsync("other-player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId, Token, default).GetAwaiter().GetResult();
                    check(false, label + " unowned transaction refresh must refuse");
                }
                catch (VerifierRefusal refusal) { check(refusal.Status == 404, label + " unowned revenue is inaccessible"); }

                string signed = chain.Sign(applePayload);
                var legacyApple = new PurchaseRecord(ShorepopCatalog.AppleStore, ShorepopCatalog.BundleId, "revenue-apple-1", "apple-player", Product,
                    "Sandbox", false, 0, null, signed, now);
                serviceStore.Bind(legacyApple, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                var appleRecovered = service.RefreshOwnedRevenueAsync("apple-player", ShorepopCatalog.AppleStore, ShorepopCatalog.BundleId,
                    "revenue-apple-1", default).GetAwaiter().GetResult();
                check(appleRecovered.RevenueVerified && appleRecovered.GrossRevenueMicros == 1_990_000,
                    label + " existing signed Apple row enriches without a server API key");

                const string subToken = "fixture-subscription-token";
                var subOrder = Order(); subOrder["purchaseToken"] = subToken; subOrder["orderId"] = OrderId + "..1";
                subOrder["lineItems"]![0]!["productId"] = ShorepopCatalog.SubscriptionProductId;
                subOrder["lineItems"]![0]!.AsObject().Remove("oneTimePurchaseDetails");
                api.Purchase = Element(SubscriptionPurchase()); api.Order = Element(subOrder);
                var subProof = new NativePurchaseProof { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.BundleId,
                    ProductId = ShorepopCatalog.SubscriptionProductId, TransactionId = subToken };
                var activeSub = service.ValidateAsync("subscriber", subProof, default).GetAwaiter().GetResult();
                check(activeSub.CurrentSubscriptionExpiryUtcTicks > 0 && activeSub.RevenueVerified && activeSub.RevenueTransactionId == OrderId + "..1",
                    label + " verified subscription renewal has its own order revenue identity");
                subOrder["state"] = "REFUNDED"; api.Order = Element(subOrder);
                var refundedSub = service.RefreshOwnedRevenueAsync("subscriber", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId, subToken, default)
                    .GetAwaiter().GetResult();
                check(refundedSub.RevenueRefunded && !refundedSub.Refunded &&
                    refundedSub.CurrentSubscriptionExpiryUtcTicks == activeSub.CurrentSubscriptionExpiryUtcTicks,
                    label + " Google refund without revoke preserves subscription entitlement");
                var renewedPurchase = SubscriptionPurchase();
                renewedPurchase["lineItems"]![0]!["latestSuccessfulOrderId"] = OrderId + "..2";
                renewedPurchase["lineItems"]![0]!["expiryTime"] = "2026-12-10T12:00:00Z";
                subOrder["state"] = "PROCESSED"; subOrder["orderId"] = OrderId + "..2";
                subOrder["orderHistory"]!["processedEvent"]!["eventTime"] = "2026-11-10T12:00:00Z";
                api.Purchase = Element(renewedPurchase); api.Order = Element(subOrder);
                var renewed = service.RefreshOwnedRevenueAsync("subscriber", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId, subToken, default)
                    .GetAwaiter().GetResult();
                check(!renewed.Refunded && !renewed.RevenueRefunded && renewed.RevenueVerified &&
                    renewed.RevenueTransactionId == OrderId + "..2" && renewed.CurrentSubscriptionExpiryUtcTicks > activeSub.CurrentSubscriptionExpiryUtcTicks,
                    label + " later paid renewal clears only the prior order's money refund and keeps access active");
                RefundHistoryScenarios(makeStore(file + ".history"), check, label);
            }
            finally
            {
                foreach (string suffix in new[] { "", ".service", ".history" })
                    foreach (string extra in new[] { "", "-shm", "-wal" }) File.Delete(file + suffix + extra);
            }
        }
        LegacySqliteMigration(check);
    }

    private static void RefundHistoryScenarios(IPurchaseStore store, Action<bool, string> check, string label)
    {
        foreach (bool refundFirst in new[] { false, true })
        {
            string token = "history-token-" + refundFirst;
            var a = new PurchaseRecord(ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId, token, "player", Product,
                "Production", false, 0, null, null, 1000, true, 1_990_000, "USD", "order-A", 100);
            var bRefund = a with { SignedDateUnixMs = 2000, RevenueVerified = false, GrossRevenueMicros = 0,
                RevenueCurrency = "", RevenueTransactionId = "order-B", RevenuePurchasedAtUnixMs = 0, RevenueRefunded = true };
            void Bind(PurchaseRecord row) => store.Bind(row, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
            PurchaseRecord Read() => store.List("player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId).Single(r => r.TransactionId == token);
            Bind(refundFirst ? bRefund : a);
            Bind(refundFirst ? a : bRefund);
            check(Read().RevenueTransactionId == "order-B" && Read().RevenueRefunded && !Read().RevenueVerified,
                label + " timestamp-less newer refund retains canonical identity in either arrival order");
            Bind(a with { SignedDateUnixMs = 3000 }); // A later observation can return a previous order.
            check(Read().RevenueTransactionId == "order-A" && !Read().RevenueRefunded,
                label + " economic refund applies only to its own order, not an unrelated paid order");
            Bind(bRefund with { SignedDateUnixMs = 4000, RevenueVerified = true, RevenueRefunded = false,
                GrossRevenueMicros = 2_990_000, RevenueCurrency = "USD", RevenuePurchasedAtUnixMs = 200 });
            check(Read().RevenueTransactionId == "order-B" && Read().RevenueRefunded,
                label + " historical full refund survives another order and a stale pre-refund paid response");
            var c = a with { SignedDateUnixMs = 5000, RevenueTransactionId = "order-C", RevenuePurchasedAtUnixMs = 300 };
            Bind(c);
            Bind(bRefund);
            check(Read().RevenueTransactionId == "order-C" && !Read().RevenueRefunded && Read().RevenueVerified,
                label + " late older refunded order cannot replace newer paid order economics");
        }
        var first = new PurchaseRecord(ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId, "unknown-order-price", "player", Product,
            "Production", false, 0, null, null, 1000, true, 1_990_000, "USD", "old-paid-order", 100);
        store.Bind(first, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
        store.Bind(first with { SignedDateUnixMs = 2000, RevenueTransactionId = "new-order-price-unavailable", RevenueVerified = false,
            GrossRevenueMicros = 0, RevenueCurrency = "", RevenuePurchasedAtUnixMs = 0 }, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
        var unknown = store.List("player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId).Single(r => r.TransactionId == first.TransactionId);
        check(!unknown.RevenueVerified && unknown.GrossRevenueMicros == 0 && unknown.RevenueCurrency == "" &&
            unknown.RevenueTransactionId == "new-order-price-unavailable" && unknown.RevenuePurchasedAtUnixMs == 0,
            label + " new canonical order never inherits previous order's price or timestamp when lookup is unavailable");
    }

    private static void LegacySqliteMigration(Action<bool, string> check)
    {
        string file = Path.Combine(Path.GetTempPath(), "shorepop-revenue-migration-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = file, Pooling = false }.ToString()))
            {
                db.Open(); using var cmd = db.CreateCommand();
                cmd.CommandText = """
                    CREATE TABLE purchases(store TEXT NOT NULL, app_id TEXT NOT NULL, transaction_id TEXT NOT NULL, player_id TEXT NOT NULL,
                    product_id TEXT NOT NULL, environment TEXT NOT NULL, refunded INTEGER NOT NULL, expiry_ticks INTEGER NOT NULL,
                    original_ref TEXT NULL, signed_payload TEXT NULL, signed_date_ms INTEGER NOT NULL, created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
                    PRIMARY KEY(store,app_id,transaction_id));
                    INSERT INTO purchases VALUES('GooglePlay','com.shorepop.game','old-transaction','old-player','gg.shorepop.gems.80','Production',0,0,NULL,NULL,0,
                    '2026-10-10T00:00:00.0000000Z','2026-10-10T00:00:00.0000000Z');
                    """;
                cmd.ExecuteNonQuery();
            }
            var store = new SqlitePurchaseStore(file);
            var row = store.List("old-player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId).Single();
            check(row.TransactionId == "old-transaction" && !row.RevenueVerified && row.GrossRevenueMicros == 0,
                "SQLite additive migration retains legacy purchase/claim and does not invent economics");
            _ = new SqlitePurchaseStore(file);
            check(store.List("old-player", ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId).Count == 1, "SQLite revenue migration is idempotent");
        }
        finally { foreach (string suffix in new[] { "", "-shm", "-wal" }) File.Delete(file + suffix); }
    }

    private static IEnumerable<(string, Func<string, IPurchaseStore>)> StoreFactories()
    {
        yield return ("memory", _ => new InMemoryPurchaseStore());
        yield return ("sqlite", path => new SqlitePurchaseStore(path));
    }

    private static PurchaseRecord Record(string id, StoreVerdict verdict) => new(ShorepopCatalog.GoogleStore, ShorepopCatalog.BundleId,
        id, "player", Product, "Production", false, 0, null, null, 0,
        verdict.RevenueVerified, verdict.GrossRevenueMicros, verdict.RevenueCurrency, verdict.RevenueTransactionId, verdict.RevenuePurchasedAtUnixMs);

    private static JsonObject ProductPurchase() => JsonNode.Parse("""
        {"orderId":"GPA.1111-2222-3333-44444","productLineItem":[{"productId":"gg.shorepop.gems.80","productOfferDetails":{"quantity":1}}],
         "purchaseStateContext":{"purchaseState":"PURCHASED"}}
        """)!.AsObject();

    private static JsonObject SubscriptionPurchase() => JsonNode.Parse("""
        {"latestOrderId":"GPA.pending","subscriptionState":"SUBSCRIPTION_STATE_ACTIVE",
         "lineItems":[{"productId":"gg.shorepop.pass.monthly","expiryTime":"2026-11-10T12:00:00Z","latestSuccessfulOrderId":"GPA.1111-2222-3333-44444..1"}]}
        """)!.AsObject();

    private static JsonObject Order() => JsonNode.Parse("""
        {"orderId":"GPA.1111-2222-3333-44444","purchaseToken":"fixture-purchase-token-not-an-analytics-id","state":"PROCESSED",
         "createTime":"2026-10-09T12:00:00Z","orderHistory":{"processedEvent":{"eventTime":"2026-10-10T12:00:00Z"}},
         "lineItems":[{"productId":"gg.shorepop.gems.80","oneTimePurchaseDetails":{"quantity":1},"listingPrice":{"currencyCode":"USD","units":"3"}}],
         "total":{"currencyCode":"USD","units":"2","nanos":490000000},
         "developerRevenueInBuyerCurrency":{"currencyCode":"USD","units":"1","nanos":743000000}}
        """)!.AsObject();

    private static JsonElement Element(JsonNode node) { using var doc = JsonDocument.Parse(node.ToJsonString()); return doc.RootElement.Clone(); }

    private sealed class RevenueGoogle : IGooglePlayApi
    {
        public bool Configured => true;
        public JsonElement? Purchase { get; set; }
        public JsonElement? Order { get; set; }
        public Exception? OrderError { get; set; }
        public string LastOrderId { get; private set; } = "";
        public Task<JsonElement?> GetProductPurchaseAsync(string packageName, string token, CancellationToken cancel) => Task.FromResult(Purchase);
        public Task<JsonElement?> GetSubscriptionPurchaseAsync(string packageName, string token, CancellationToken cancel) => Task.FromResult(Purchase);
        public Task<JsonElement?> GetOrderAsync(string packageName, string orderId, CancellationToken cancel)
        {
            LastOrderId = orderId;
            return OrderError != null ? Task.FromException<JsonElement?>(OrderError) : Task.FromResult(Order);
        }
    }
}
