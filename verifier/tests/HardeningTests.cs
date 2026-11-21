using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Shorepop.Verifier;

internal static class HardeningTests
{
    private const string Product = "gg.shorepop.gems.80";
    private const string NonConsumable = "gg.shorepop.noads";
    private const string SubProduct = ShorepopCatalog.SubscriptionProductId;

    public static async Task RunAsync(Action<string, Action> test, Func<string, Func<Task>, Task> testAsync)
    {
        var chain = TestChain.Create();
        var verifier = new AppleJwsVerifier([chain.RootDer]);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        static void Expect(bool condition, string detail)
        {
            if (!condition) throw new Exception(detail);
        }

        static async Task RejectsAsync(int status, string code, Func<Task> body)
        {
            try { await body(); }
            catch (VerifierRefusal r)
            {
                Expect(r.Status == status && r.Code == code, $"refusal {r.Status} {r.Code}, wanted {status} {code}");
                return;
            }
            throw new Exception($"accepted known-bad input (wanted {status} {code})");
        }

        static void Rejects(int status, string code, Action body)
        {
            try { body(); }
            catch (VerifierRefusal r)
            {
                Expect(r.Status == status && r.Code == code, $"refusal {r.Status} {r.Code}, wanted {status} {code}");
                return;
            }
            throw new Exception($"accepted known-bad input (wanted {status} {code})");
        }

        Dictionary<string, object> AppleTx(string product, string id, string env, string type = "Consumable", long priceMilliunits = 1990L) => new()
        {
            ["transactionId"] = id,
            ["originalTransactionId"] = id,
            ["bundleId"] = ShorepopCatalog.AppleBundleId,
            ["productId"] = product,
            ["purchaseDate"] = nowMs - 1000,
            ["quantity"] = 1,
            ["type"] = type,
            ["environment"] = env,
            ["signedDate"] = nowMs,
            ["price"] = priceMilliunits,
            ["currency"] = "USD",
        };

        // =========================================================================
        // WO-20261114-810: Apple one-way Production -> Sandbox fallback
        // =========================================================================
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        string p8Pem = ecdsa.ExportPkcs8PrivateKeyPem();

        await testAsync("WO-20261114-810: AppStoreServerApi Production OK uses Production only", async () =>
        {
            var urls = new List<string>();
            var handler = new ScriptedHttpHandler(req =>
            {
                urls.Add(req.RequestUri!.ToString());
                return JsonResp(HttpStatusCode.OK, """{"signedTransactionInfo":"prod.jws.token"}""");
            });
            var api = new AppStoreServerApi(new HttpClient(handler), "KID", "ISS", p8Pem, ShorepopCatalog.AppleBundleId);
            var lookup = await api.LookupTransactionInfoAsync("tx-prod-1", "Production", default);
            Expect(lookup.SignedTransactionInfo == "prod.jws.token" && lookup.ResolvedEnvironment == "Production" && !lookup.UsedSandboxFallback, "prod lookup");
            Expect(urls.Count == 1 && urls[0].StartsWith(AppStoreServerApi.ProductionBase, StringComparison.Ordinal), "only production queried");
        });

        await testAsync("WO-20261114-810: AppStoreServerApi Production 4040010 / 21007 / 503 / 401 / network falls back one-way to Sandbox", async () =>
        {
            var prodErrors = new Func<HttpResponseMessage>[]
            {
                () => JsonResp(HttpStatusCode.NotFound, """{"errorCode":4040010,"errorMessage":"Transaction id not found."}"""),
                () => JsonResp(HttpStatusCode.BadRequest, """{"status":21007}"""),
                () => JsonResp(HttpStatusCode.ServiceUnavailable, """{"errorCode":5000001}"""),
                () => JsonResp(HttpStatusCode.Unauthorized, ""),
                () => throw new HttpRequestException("simulated production outage"),
            };
            foreach (var prodFail in prodErrors)
            {
                var urls = new List<string>();
                var handler = new ScriptedHttpHandler(req =>
                {
                    string url = req.RequestUri!.ToString();
                    urls.Add(url);
                    if (url.StartsWith(AppStoreServerApi.ProductionBase, StringComparison.Ordinal)) return prodFail();
                    return JsonResp(HttpStatusCode.OK, """{"signedTransactionInfo":"sandbox.jws.token"}""");
                });
                var api = new AppStoreServerApi(new HttpClient(handler), "KID", "ISS", p8Pem, ShorepopCatalog.AppleBundleId);
                var lookup = await api.LookupTransactionInfoAsync("tx-appreview-1", "Production", default);
                Expect(lookup.SignedTransactionInfo == "sandbox.jws.token" && lookup.ResolvedEnvironment == "Sandbox" && lookup.UsedSandboxFallback, "fallback result");
                Expect(urls.Count == 2 && urls[0].StartsWith(AppStoreServerApi.ProductionBase) && urls[1].StartsWith(AppStoreServerApi.SandboxBase), "one-way prod->sandbox order");
            }

            // Subscription lastTransactions endpoint also performs one-way Production -> Sandbox fallback.
            var subUrls = new List<string>();
            var subHandler = new ScriptedHttpHandler(req =>
            {
                string url = req.RequestUri!.ToString();
                subUrls.Add(url);
                if (url.StartsWith(AppStoreServerApi.ProductionBase, StringComparison.Ordinal))
                    return JsonResp(HttpStatusCode.NotFound, """{"errorCode":4040008,"errorMessage":"Original transaction id not found."}""");
                return JsonResp(HttpStatusCode.OK, """{"data":[{"lastTransactions":[{"signedTransactionInfo":"sandbox.sub.jws"}]}]}""");
            });
            var subApi = new AppStoreServerApi(new HttpClient(subHandler), "KID", "ISS", p8Pem, ShorepopCatalog.AppleBundleId);
            var subLookup = await subApi.LookupSubscriptionLastTransactionsAsync("tx-sub-orig", "Production", default);
            Expect(subLookup.UsedSandboxFallback && subLookup.ResolvedEnvironment == "Sandbox" &&
                subLookup.SignedTransactions.SequenceEqual(["sandbox.sub.jws"]), "subscription one-way fallback");
            Expect(subUrls.Count == 2 && subUrls[0].StartsWith(AppStoreServerApi.ProductionBase) && subUrls[1].StartsWith(AppStoreServerApi.SandboxBase), "sub fallback order");
        });

        await testAsync("WO-20261114-810: AppStoreServerApi refuses 403/non-sandbox 400 with NO Sandbox fallback and fails closed when both unavailable", async () =>
        {
            foreach (var status in new[] { HttpStatusCode.Forbidden, HttpStatusCode.BadRequest })
            {
                var urls = new List<string>();
                var handler = new ScriptedHttpHandler(req =>
                {
                    urls.Add(req.RequestUri!.ToString());
                    return JsonResp(status, """{"errorCode":4000001}""");
                });
                var api = new AppStoreServerApi(new HttpClient(handler), "KID", "ISS", p8Pem, ShorepopCatalog.AppleBundleId);
                await RejectsAsync(422, "apple_server_api_rejected", () => api.LookupTransactionInfoAsync("tx-bad", "Production", default));
                Expect(urls.Count == 1 && urls[0].StartsWith(AppStoreServerApi.ProductionBase), "no sandbox fallback on non-sandbox client rejection");
            }

            // Both unavailable -> 503
            var bothDown = new ScriptedHttpHandler(_ => JsonResp(HttpStatusCode.ServiceUnavailable, ""));
            var apiDown = new AppStoreServerApi(new HttpClient(bothDown), "KID", "ISS", p8Pem, ShorepopCatalog.AppleBundleId);
            await RejectsAsync(503, "apple_server_api_unavailable", () => apiDown.LookupTransactionInfoAsync("tx-down", "Production", default));

            // Production unavailable + Sandbox 404 -> still 503 fail-closed (never pretend not found during Production outage)
            var prodDownSbx404 = new ScriptedHttpHandler(req =>
                req.RequestUri!.ToString().StartsWith(AppStoreServerApi.ProductionBase)
                    ? JsonResp(HttpStatusCode.ServiceUnavailable, "")
                    : JsonResp(HttpStatusCode.NotFound, """{"errorCode":4040010}"""));
            var apiMixed = new AppStoreServerApi(new HttpClient(prodDownSbx404), "KID", "ISS", p8Pem, ShorepopCatalog.AppleBundleId);
            await RejectsAsync(503, "apple_server_api_unavailable", () => apiMixed.LookupTransactionInfoAsync("tx-mixed", "Production", default));
        });

        await testAsync("WO-20261114-810: Sandbox scope NEVER queries Production API and rejects Production JWS", async () =>
        {
            var urls = new List<string>();
            var handler = new ScriptedHttpHandler(req =>
            {
                urls.Add(req.RequestUri!.ToString());
                return JsonResp(HttpStatusCode.NotFound, """{"errorCode":4040010}""");
            });
            var api = new AppStoreServerApi(new HttpClient(handler), "KID", "ISS", p8Pem, ShorepopCatalog.AppleBundleId);
            var lookup = await api.LookupTransactionInfoAsync("tx-sbx-only", "Sandbox", default);
            Expect(lookup.SignedTransactionInfo == null && !lookup.UsedSandboxFallback, "sandbox 404 returns null");
            Expect(urls.Count == 1 && urls[0].StartsWith(AppStoreServerApi.SandboxBase), "sandbox never queries production");

            var sbxChecker = new AppleTransactionChecker(verifier, ShorepopCatalog.AppleBundleId, AppleTransactionChecker.SandboxOnlyEnvironments);
            string prodJws = chain.Sign(AppleTx(Product, "tx-prod-in-sbx", "Production"));
            Rejects(422, "environment_not_allowed", () => sbxChecker.Check(prodJws, Product, "tx-prod-in-sbx", DateTimeOffset.UtcNow));
        });

        await testAsync("WO-20261114-810: Production-only PurchaseService settles App Review Sandbox order stamped TestOrder with zero revenue, restores cleanly, refuses mismatches", async () =>
        {
            var store = new InMemoryPurchaseStore();
            var prodChecker = new AppleTransactionChecker(verifier, ShorepopCatalog.AppleBundleId, AppleTransactionChecker.ProductionOnlyEnvironments);
            string prodJws = chain.Sign(AppleTx(Product, "810-prod-1", "Production"));
            string sbxClientJws = chain.Sign(AppleTx(NonConsumable, "810-sbx-1", "Sandbox", "Non-Consumable"));
            var canonicalSbxTx = AppleTx(NonConsumable, "810-sbx-1", "Sandbox", "Non-Consumable");
            canonicalSbxTx["signedDate"] = nowMs + 50;
            string sbxServerJws = chain.Sign(canonicalSbxTx);

            var urls = new List<string>();
            var handler = new ScriptedHttpHandler(req =>
            {
                string url = req.RequestUri!.ToString();
                urls.Add(url);
                if (url.Contains("810-sbx-1"))
                {
                    if (url.StartsWith(AppStoreServerApi.ProductionBase))
                        return JsonResp(HttpStatusCode.NotFound, """{"errorCode":4040010,"errorMessage":"Transaction id not found."}""");
                    return JsonResp(HttpStatusCode.OK, JsonSerializer.Serialize(new { signedTransactionInfo = sbxServerJws }));
                }
                return JsonResp(HttpStatusCode.NotFound, """{"errorCode":4040010}""");
            });
            var appleApi = new AppStoreServerApi(new HttpClient(handler), "KID", "ISS", p8Pem, ShorepopCatalog.AppleBundleId);
            var service = new PurchaseService(verifier, prodChecker, appleApi, new FakeGoogle(false), store,
                ShorepopCatalog.GooglePackageId, NullLogger<PurchaseService>.Instance, appleBundleId: ShorepopCatalog.AppleBundleId);

            // Production OK: revenue verified, TestOrder false and omitted from wire JSON.
            var prodRes = await service.ValidateAsync("pProd", new NativePurchaseProof
            {
                Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.AppleBundleId,
                ProductId = Product, TransactionId = "810-prod-1", AppleSignedTransaction = prodJws,
            }, default);
            Expect(prodRes.Environment == "Production" && !prodRes.TestOrder && prodRes.RevenueVerified && prodRes.GrossRevenueMicros == 1_990_000, "production revenue intact");
            string prodWire = JsonSerializer.Serialize(prodRes);
            Expect(!prodWire.Contains("TestOrder", StringComparison.Ordinal), "production wire JSON must not add extra TestOrder key");

            // App Review Sandbox order in Production-only scope: settles via one-way fallback, stamped TestOrder, ZERO revenue, no revenue proof retained.
            var sbxProof = new NativePurchaseProof
            {
                Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.AppleBundleId,
                ProductId = NonConsumable, TransactionId = "810-sbx-1", AppleSignedTransaction = sbxClientJws,
            };
            var sbxRes = await service.ValidateAsync("pReviewer", sbxProof, default);
            Expect(sbxRes.Environment == "Sandbox" && sbxRes.TestOrder && !sbxRes.Refunded, "sandbox fallback settled & stamped test_order");
            Expect(!sbxRes.RevenueVerified && sbxRes.GrossRevenueMicros == 0 && sbxRes.RevenueCurrency == "" &&
                sbxRes.RevenueTransactionId == "" && sbxRes.RevenuePurchasedAtUnixMs == 0, "sandbox fallback emits zero revenue");
            Expect(!RevenueProofBinding.TryBind(sbxProof, sbxRes, null, out _, out _), "sandbox fallback never caches revenue proof");
            var stored = store.List("pReviewer", ShorepopCatalog.AppleStore, ShorepopCatalog.AppleBundleId).Single(r => r.TransactionId == "810-sbx-1");
            Expect(stored.SignedPayload == sbxServerJws && stored.TestOrder, "canonical server-verified Sandbox reference stored");

            // Restore / reconcile accepts stored canonical Sandbox reference cleanly (still zero revenue).
            var reconciled = await service.ReconcileAsync("pReviewer", new ReconcileRequest
            {
                Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.AppleBundleId,
            }, default);
            Expect(reconciled.Count == 1 && reconciled[0].TransactionId == "810-sbx-1" && reconciled[0].TestOrder && !reconciled[0].RevenueVerified, "reconcile restores canonical sandbox grant");

            // Negative control: unverified client-only Sandbox reference not on Apple Server API is refused.
            string phantomJws = chain.Sign(AppleTx(Product, "810-phantom", "Sandbox"));
            await RejectsAsync(422, "purchase_not_found", () => service.ValidateAsync("pReviewer", new NativePurchaseProof
            {
                Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.AppleBundleId,
                ProductId = Product, TransactionId = "810-phantom", AppleSignedTransaction = phantomJws,
            }, default));

            // Negative control: bundle mismatch refuses before any API call (no fallback).
            int beforeCalls = urls.Count;
            var wrongBundle = AppleTx(Product, "810-wrong-bundle", "Sandbox");
            wrongBundle["bundleId"] = "com.other.app";
            await RejectsAsync(422, "bundle_mismatch", () => service.ValidateAsync("pReviewer", new NativePurchaseProof
            {
                Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.AppleBundleId,
                ProductId = Product, TransactionId = "810-wrong-bundle", AppleSignedTransaction = chain.Sign(wrongBundle),
            }, default));
            Expect(urls.Count == beforeCalls, "bundle mismatch must not call Apple Server API");
        });

        // =========================================================================
        // WO-20261118-898: Play missing autoRenewing -> false & 10-min order slack
        // =========================================================================
        test("WO-20261118-898: Play subscription missing autoRenewing reads as false and 10-min order-period slack accepts 1s & 9m before start", () =>
        {
            // Missing autoRenewing / empty autoRenewingPlan -> accepted with AutoRenewing = false.
            var subNoRenew = Parse("""
                {
                  "startTime": "2026-11-18T01:00:00Z",
                  "subscriptionState": "SUBSCRIPTION_STATE_ACTIVE",
                  "lineItems": [{
                    "productId": "gg.shorepop.pass.monthly",
                    "expiryTime": "2026-12-18T01:00:00Z",
                    "autoRenewingPlan": {},
                    "latestSuccessfulOrderId": "GPA.898-0001"
                  }]
                }
                """);
            var v = GooglePlayInterpreter.Subscription(subNoRenew, ShorepopCatalog.GooglePackageId, SubProduct, "tok-sub-898");
            Expect(!v.AutoRenewing && v.PurchasedPeriodExpiryUtcTicks > 0 && !v.Refunded, "missing autoRenewing accepted as false");

            // Explicit autoRenewEnabled: true -> AutoRenewing = true.
            var subRenew = Parse("""
                {
                  "startTime": "2026-11-18T01:00:00Z",
                  "subscriptionState": "SUBSCRIPTION_STATE_ACTIVE",
                  "lineItems": [{
                    "productId": "gg.shorepop.pass.monthly",
                    "expiryTime": "2026-12-18T01:00:00Z",
                    "autoRenewingPlan": { "autoRenewEnabled": true },
                    "latestSuccessfulOrderId": "GPA.898-0001"
                  }]
                }
                """);
            Expect(GooglePlayInterpreter.Subscription(subRenew, ShorepopCatalog.GooglePackageId, SubProduct, "tok-sub-898").AutoRenewing, "explicit autoRenewEnabled true");

            // Malformed non-boolean autoRenewing -> refused.
            var badBool = Parse("""
                {
                  "autoRenewing": "yes",
                  "subscriptionState": "SUBSCRIPTION_STATE_ACTIVE",
                  "lineItems": [{"productId": "gg.shorepop.pass.monthly", "expiryTime": "2026-12-18T01:00:00Z"}]
                }
                """);
            Rejects(422, "purchase_state_unknown", () => GooglePlayInterpreter.Subscription(badBool, ShorepopCatalog.GooglePackageId, SubProduct, "tok-sub-bad"));

            // Cross-record time slack: 1 second and 9 minutes before subscription startTime accepted; 11 minutes refused.
            JsonElement MakeOrder(string eventTime, string orderId = "GPA.898-0001", string token = "tok-sub-898", string prod = SubProduct) => Parse(JsonSerializer.Serialize(new
            {
                orderId,
                purchaseToken = token,
                state = "PROCESSED",
                orderHistory = new { processedEvent = new { eventTime } },
                lineItems = new[] { new { productId = prod } },
                total = new { currencyCode = "USD", units = "2", nanos = 990000000 },
            }));

            var oneSecEarly = GooglePlayInterpreter.WithOrderRevenue(v, MakeOrder("2026-11-18T00:59:59Z"), "GPA.898-0001", "tok-sub-898", subNoRenew);
            Expect(oneSecEarly.RevenueVerified && oneSecEarly.GrossRevenueMicros == 2_990_000, "1s before startTime accepted");

            var nineMinEarly = GooglePlayInterpreter.WithOrderRevenue(v, MakeOrder("2026-11-18T00:51:00Z"), "GPA.898-0001", "tok-sub-898", subNoRenew);
            Expect(nineMinEarly.RevenueVerified && nineMinEarly.GrossRevenueMicros == 2_990_000, "9m before startTime accepted");

            var elevenMinEarly = GooglePlayInterpreter.WithOrderRevenue(v, MakeOrder("2026-11-18T00:49:00Z"), "GPA.898-0001", "tok-sub-898", subNoRenew);
            Expect(!elevenMinEarly.RevenueVerified, "11m before startTime outside 10m slack refused");

            // Identity mismatches still refused even inside slack.
            Expect(!GooglePlayInterpreter.WithOrderRevenue(v, MakeOrder("2026-11-18T00:59:59Z", orderId: "GPA.other"), "GPA.898-0001", "tok-sub-898", subNoRenew).RevenueVerified, "wrong orderId refused");
            Expect(!GooglePlayInterpreter.WithOrderRevenue(v, MakeOrder("2026-11-18T00:59:59Z", token: "tok-other"), "GPA.898-0001", "tok-sub-898", subNoRenew).RevenueVerified, "wrong token refused");
            Expect(!GooglePlayInterpreter.WithOrderRevenue(v, MakeOrder("2026-11-18T00:59:59Z", prod: Product), "GPA.898-0001", "tok-sub-898", subNoRenew).RevenueVerified, "wrong product refused");
        });

        // =========================================================================
        // WO-20261120-650: Play acknowledge/consume throw -> re-read recovery
        // =========================================================================
        await testAsync("WO-20261120-650: Play acknowledge/consume throw recovers via Publisher API re-read and stays fail-closed on mismatch/unfinished", async () =>
        {
            var store = new InMemoryPurchaseStore();
            var google = new HardeningGoogleApi();
            var service = new PurchaseService(verifier,
                new AppleTransactionChecker(verifier, ShorepopCatalog.AppleBundleId, AppleTransactionChecker.DefaultEnvironments),
                new FakeApple(), google, store, ShorepopCatalog.GooglePackageId, NullLogger<PurchaseService>.Instance);

            // Seed validated consumable, non-consumable, and subscription claims.
            google.Products["tok-cons"] = Parse("""{"productLineItem":[{"productId":"gg.shorepop.gems.80"}],"purchaseStateContext":{"purchaseState":"PURCHASED"}}""");
            google.Products["tok-noncons"] = Parse("""{"productLineItem":[{"productId":"gg.shorepop.noads"}],"purchaseStateContext":{"purchaseState":"PURCHASED"}}""");
            google.Subscriptions["tok-sub"] = Parse("""{"subscriptionState":"SUBSCRIPTION_STATE_ACTIVE","acknowledgementState":"ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED","externalAccountIdentifiers":{"obfuscatedExternalAccountId":"acct-1"},"lineItems":[{"productId":"gg.shorepop.pass.monthly","expiryTime":"2030-01-02T03:04:05Z"}]}""");

            await service.ValidateAsync("p650", new NativePurchaseProof { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, ProductId = Product, TransactionId = "tok-cons" }, default);
            await service.ValidateAsync("p650", new NativePurchaseProof { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, ProductId = NonConsumable, TransactionId = "tok-noncons" }, default);
            await service.ValidateAsync("p650", new NativePurchaseProof { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, ProductId = SubProduct, TransactionId = "tok-sub" }, default);

            // (a) Acknowledge throws -> re-read shows acknowledgementState == 1 (non-consumable) and ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED (subscription) -> finished!
            google.FinishError = new VerifierRefusal(503, "google_finish_unavailable");
            google.LegacyProducts["tok-noncons"] = Parse("""{"packageName":"com.shorepop.game","productId":"gg.shorepop.noads","purchaseToken":"tok-noncons","purchaseState":0,"acknowledgementState":1,"quantity":1,"obfuscatedExternalAccountId":"acct-1"}""");
            var ackRes = await service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest
            {
                ProductId = NonConsumable, TransactionId = "tok-noncons", AccountId = "acct-1",
            }, default);
            Expect(ackRes.Finished && ackRes.RecoveredFromReread && ackRes.State == "acknowledged", "non-consumable ack recovered");
            Expect(store.Claims(ShorepopCatalog.GoogleStore, ShorepopCatalog.GooglePackageId, "tok-noncons").Single().AckedUtc != null, "local ack stamped");

            var subAckRes = await service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest
            {
                ProductId = SubProduct, TransactionId = "tok-sub", AccountId = "acct-1",
            }, default);
            Expect(subAckRes.Finished && subAckRes.RecoveredFromReread && subAckRes.State == "acknowledged", "subscription ack recovered");

            // (b) Consume throws -> re-read shows consumptionState == 1 -> finished!
            google.LegacyProducts["tok-cons"] = Parse("""{"packageName":"com.shorepop.game","productId":"gg.shorepop.gems.80","purchaseToken":"tok-cons","purchaseState":0,"consumptionState":1,"acknowledgementState":1,"quantity":1,"obfuscatedExternalAccountId":"acct-1"}""");
            var consRes = await service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest
            {
                ProductId = Product, TransactionId = "tok-cons", AccountId = "acct-1",
            }, default);
            Expect(consRes.Finished && consRes.RecoveredFromReread && consRes.State == "consumed", "consumable consume recovered");

            // (b2) v2 productsv2 re-read shape with CONSUMPTION_STATE_CONSUMED also recovers when v1 returns null.
            google.LegacyProducts.Remove("tok-cons");
            google.Products["tok-cons"] = Parse("""{"productLineItem":[{"productId":"gg.shorepop.gems.80","consumptionState":"CONSUMPTION_STATE_CONSUMED","productOfferDetails":{"quantity":1}}],"purchaseStateContext":{"purchaseState":"PURCHASED"},"obfuscatedExternalAccountId":"acct-1"}""");
            var v2ConsRes = await service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest
            {
                ProductId = Product, TransactionId = "tok-cons", AccountId = "acct-1",
            }, default);
            Expect(v2ConsRes.Finished && v2ConsRes.RecoveredFromReread && v2ConsRes.State == "consumed", "v2 consumable recovered");

            // (c) Negative controls: package, product, token, account, purchaseState != 0, quantity != 1 -> 403 final refusal.
            google.LegacyProducts["tok-cons"] = Parse("""{"packageName":"com.other.app","productId":"gg.shorepop.gems.80","purchaseToken":"tok-cons","purchaseState":0,"consumptionState":1,"obfuscatedExternalAccountId":"acct-1"}""");
            await RejectsAsync(403, "google_finish_package_mismatch", () => service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest { ProductId = Product, TransactionId = "tok-cons", AccountId = "acct-1" }, default));

            google.LegacyProducts["tok-cons"] = Parse("""{"packageName":"com.shorepop.game","productId":"gg.shorepop.gems.260","purchaseToken":"tok-cons","purchaseState":0,"consumptionState":1,"obfuscatedExternalAccountId":"acct-1"}""");
            await RejectsAsync(403, "google_finish_product_mismatch", () => service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest { ProductId = Product, TransactionId = "tok-cons", AccountId = "acct-1" }, default));

            google.LegacyProducts["tok-cons"] = Parse("""{"packageName":"com.shorepop.game","productId":"gg.shorepop.gems.80","purchaseToken":"tok-other","purchaseState":0,"consumptionState":1,"obfuscatedExternalAccountId":"acct-1"}""");
            await RejectsAsync(403, "google_finish_token_mismatch", () => service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest { ProductId = Product, TransactionId = "tok-cons", AccountId = "acct-1" }, default));

            google.LegacyProducts["tok-cons"] = Parse("""{"packageName":"com.shorepop.game","productId":"gg.shorepop.gems.80","purchaseToken":"tok-cons","purchaseState":0,"consumptionState":1,"obfuscatedExternalAccountId":"acct-OTHER"}""");
            await RejectsAsync(403, "google_finish_account_mismatch", () => service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest { ProductId = Product, TransactionId = "tok-cons", AccountId = "acct-1" }, default));

            google.LegacyProducts["tok-cons"] = Parse("""{"packageName":"com.shorepop.game","productId":"gg.shorepop.gems.80","purchaseToken":"tok-cons","purchaseState":1,"consumptionState":1,"obfuscatedExternalAccountId":"acct-1"}""");
            await RejectsAsync(403, "google_finish_purchase_state_invalid", () => service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest { ProductId = Product, TransactionId = "tok-cons", AccountId = "acct-1" }, default));

            google.LegacyProducts["tok-cons"] = Parse("""{"packageName":"com.shorepop.game","productId":"gg.shorepop.gems.80","purchaseToken":"tok-cons","purchaseState":0,"quantity":2,"consumptionState":1,"obfuscatedExternalAccountId":"acct-1"}""");
            await RejectsAsync(403, "google_finish_quantity_invalid", () => service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest { ProductId = Product, TransactionId = "tok-cons", AccountId = "acct-1" }, default));

            // (d) Negative controls: not yet consumed/acknowledged -> fail closed 503 with distinct code.
            google.LegacyProducts["tok-cons"] = Parse("""{"packageName":"com.shorepop.game","productId":"gg.shorepop.gems.80","purchaseToken":"tok-cons","purchaseState":0,"consumptionState":0,"acknowledgementState":1,"obfuscatedExternalAccountId":"acct-1"}""");
            await RejectsAsync(503, "google_finish_not_consumed", () => service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest { ProductId = Product, TransactionId = "tok-cons", AccountId = "acct-1" }, default));

            google.LegacyProducts["tok-noncons"] = Parse("""{"packageName":"com.shorepop.game","productId":"gg.shorepop.noads","purchaseToken":"tok-noncons","purchaseState":0,"acknowledgementState":0,"obfuscatedExternalAccountId":"acct-1"}""");
            await RejectsAsync(503, "google_finish_not_acknowledged", () => service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest { ProductId = NonConsumable, TransactionId = "tok-noncons", AccountId = "acct-1" }, default));

            // (e) Re-read API failure -> fail closed 503.
            google.ReadError = new VerifierRefusal(503, "google_api_unavailable");
            await RejectsAsync(503, "google_finish_reread_unavailable", () => service.FinishGooglePurchaseAsync("p650", new GoogleFinishRequest { ProductId = Product, TransactionId = "tok-cons", AccountId = "acct-1" }, default));
        });

        await testAsync("WO-20261120-650: real GooglePlayApi HTTP client hits :consume/:acknowledge and recovers via v1 GET re-read", async () =>
        {
            using var rsa = RSA.Create(2048);
            string saJson = JsonSerializer.Serialize(new
            {
                client_email = "verifier@test.invalid",
                private_key = rsa.ExportPkcs8PrivateKeyPem(),
                token_uri = "https://oauth2.googleapis.com/token",
            });
            var requests = new List<string>();
            var handler = new ScriptedHttpHandler(req =>
            {
                string line = req.Method.Method + " " + req.RequestUri!.AbsoluteUri;
                requests.Add(line);
                if (req.RequestUri.Host == "oauth2.googleapis.com")
                    return JsonResp(HttpStatusCode.OK, """{"access_token":"test-bearer","expires_in":3600}""");
                if (req.Method == HttpMethod.Post && req.RequestUri.AbsolutePath.EndsWith(":consume", StringComparison.Ordinal))
                    return JsonResp(HttpStatusCode.InternalServerError, """{"error":{"code":500}}""");
                if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.Contains("/purchases/products/", StringComparison.Ordinal))
                    return JsonResp(HttpStatusCode.OK, """{"packageName":"com.shorepop.game","productId":"gg.shorepop.gems.80","purchaseToken":"tok-http-cons","purchaseState":0,"consumptionState":1,"quantity":1,"obfuscatedExternalAccountId":"acct-http"}""");
                if (req.Method == HttpMethod.Get && req.RequestUri.AbsolutePath.Contains("/purchases/productsv2/", StringComparison.Ordinal))
                    return JsonResp(HttpStatusCode.OK, """{"productLineItem":[{"productId":"gg.shorepop.gems.80"}],"purchaseStateContext":{"purchaseState":"PURCHASED"}}""");
                return JsonResp(HttpStatusCode.NotFound, "{}");
            });
            var store = new InMemoryPurchaseStore();
            var realGoogle = new GooglePlayApi(new HttpClient(handler), saJson);
            var service = new PurchaseService(verifier,
                new AppleTransactionChecker(verifier, ShorepopCatalog.AppleBundleId, AppleTransactionChecker.DefaultEnvironments),
                new FakeApple(), realGoogle, store, ShorepopCatalog.GooglePackageId, NullLogger<PurchaseService>.Instance);

            await service.ValidateAsync("pHttp", new NativePurchaseProof
            {
                Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId,
                ProductId = Product, TransactionId = "tok-http-cons",
            }, default);
            var finished = await service.FinishGooglePurchaseAsync("pHttp", new GoogleFinishRequest
            {
                ProductId = Product, TransactionId = "tok-http-cons", AccountId = "acct-http",
            }, default);
            Expect(finished.Finished && finished.RecoveredFromReread && finished.State == "consumed", "real HTTP GooglePlayApi recovered consume");
            Expect(requests.Any(r => r.StartsWith("POST ") && r.Contains("/purchases/products/gg.shorepop.gems.80/tokens/tok-http-cons:consume")), "called :consume endpoint");
            Expect(requests.Any(r => r.StartsWith("GET ") && r.Contains("/purchases/products/gg.shorepop.gems.80/tokens/tok-http-cons")), "re-read v1 purchases.products endpoint");
        });

        // =========================================================================
        // WO-20261020-14: Play purchaseType classification & Unity IAP 5 bare receipt
        // =========================================================================
        await testAsync("WO-20261020-14: Play purchaseType key-presence classification suppresses test/promo/malformed revenue without blocking grant", async () =>
        {
            Expect(GooglePlayInterpreter.ClassifyPurchaseType(Parse("""{"purchaseState":0}""")) == GooglePurchaseType.Charged, "absent = Charged");
            Expect(GooglePlayInterpreter.ClassifyPurchaseType(Parse("""{"purchaseType":0}""")) == GooglePurchaseType.Test, "0 = Test");
            Expect(GooglePlayInterpreter.ClassifyPurchaseType(Parse("""{"purchaseType":1}""")) == GooglePurchaseType.Promo, "1 = Promo");
            Expect(GooglePlayInterpreter.ClassifyPurchaseType(Parse("""{"purchaseType":2}""")) == GooglePurchaseType.Rewarded, "2 = Rewarded");
            foreach (string bad in new[] { """{"purchaseType":null}""", """{"purchaseType":"0"}""", """{"purchaseType":true}""", """{"purchaseType":99}""" })
                Expect(GooglePlayInterpreter.ClassifyPurchaseType(Parse(bad)) == GooglePurchaseType.Unverified, "malformed purchaseType never collapses to Charged: " + bad);

            var store = new InMemoryPurchaseStore();
            var google = new HardeningGoogleApi();
            var service = new PurchaseService(verifier,
                new AppleTransactionChecker(verifier, ShorepopCatalog.AppleBundleId, AppleTransactionChecker.DefaultEnvironments),
                new FakeApple(), google, store, ShorepopCatalog.GooglePackageId, NullLogger<PurchaseService>.Instance);

            JsonElement PaidOrder(string orderId, string token) => Parse(JsonSerializer.Serialize(new
            {
                orderId,
                purchaseToken = token,
                state = "PROCESSED",
                orderHistory = new { processedEvent = new { eventTime = "2026-10-20T12:00:00Z" } },
                lineItems = new[] { new { productId = Product, oneTimePurchaseDetails = new { quantity = 1 } } },
                total = new { currencyCode = "USD", units = "1", nanos = 990000000 },
            }));

            // License-test order with ordinary GPA orderId and NO testPurchaseContext in v2, caught by v1 purchaseType == 0!
            google.Products["tok-v1-test"] = Parse("""{"orderId":"GPA.1020-0001","productLineItem":[{"productId":"gg.shorepop.gems.80"}],"purchaseStateContext":{"purchaseState":"PURCHASED"}}""");
            google.LegacyProducts["tok-v1-test"] = Parse("""{"orderId":"GPA.1020-0001","purchaseState":0,"purchaseType":0}""");
            google.Orders["GPA.1020-0001"] = PaidOrder("GPA.1020-0001", "tok-v1-test");
            var testRes = await service.ValidateAsync("pTester", new NativePurchaseProof
            {
                Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, ProductId = Product, TransactionId = "tok-v1-test",
            }, default);
            Expect(testRes.Environment == "LicenseTest" && testRes.TestOrder && !testRes.Refunded, "v1 purchaseType=0 classified LicenseTest and still grants");
            Expect(!testRes.RevenueVerified && testRes.GrossRevenueMicros == 0 && testRes.RevenueTransactionId == "", "test order has zero revenue");

            // Promo order (purchaseType == 1): keeps Production (not internal test), zero cash revenue.
            google.Products["tok-v1-promo"] = Parse("""{"orderId":"GPA.1020-0002","productLineItem":[{"productId":"gg.shorepop.gems.80"}],"purchaseStateContext":{"purchaseState":"PURCHASED"}}""");
            google.LegacyProducts["tok-v1-promo"] = Parse("""{"orderId":"GPA.1020-0002","purchaseState":0,"purchaseType":1}""");
            google.Orders["GPA.1020-0002"] = PaidOrder("GPA.1020-0002", "tok-v1-promo");
            var promoRes = await service.ValidateAsync("pPromo", new NativePurchaseProof
            {
                Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, ProductId = Product, TransactionId = "tok-v1-promo",
            }, default);
            Expect(promoRes.Environment == "Production" && !promoRes.TestOrder && !promoRes.RevenueVerified, "promo is Production with zero cash revenue");

            // Charged order (purchaseType absent): verified revenue forwards cleanly!
            google.Products["tok-v1-charged"] = Parse("""{"orderId":"GPA.1020-0003","productLineItem":[{"productId":"gg.shorepop.gems.80"}],"purchaseStateContext":{"purchaseState":"PURCHASED"}}""");
            google.LegacyProducts["tok-v1-charged"] = Parse("""{"orderId":"GPA.1020-0003","purchaseState":0}""");
            google.Orders["GPA.1020-0003"] = PaidOrder("GPA.1020-0003", "tok-v1-charged");
            var chargedRes = await service.ValidateAsync("pBuyer", new NativePurchaseProof
            {
                Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, ProductId = Product, TransactionId = "tok-v1-charged",
            }, default);
            Expect(chargedRes.Environment == "Production" && !chargedRes.TestOrder && chargedRes.RevenueVerified && chargedRes.GrossRevenueMicros == 1_990_000, "charged order verified");
        });

        test("WO-20261020-14: RevenueProofBinding parses Unity IAP 5 bare Google receipt and rejects LicenseTest orders", () =>
        {
            using var rsa = RSA.Create(2048);
            string pubKey = Convert.ToBase64String(rsa.ExportSubjectPublicKeyInfo());
            var verified = new ValidatedNativePurchase
            {
                Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId,
                ProductId = Product, TransactionId = "iap5-bare-token",
                Environment = "Production", RevenueTransactionId = "GPA.iap5-1", RevenueVerified = true,
            };
            string purchaseJson = JsonSerializer.Serialize(new
            {
                packageName = ShorepopCatalog.GooglePackageId,
                productId = Product,
                purchaseToken = "iap5-bare-token",
                orderId = "GPA.iap5-1",
                quantity = 1,
                purchaseState = 0,
            });
            string sig = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(purchaseJson), HashAlgorithmName.SHA1, RSASignaturePadding.Pkcs1));
            // Unity IAP 5 bare Google receipt (no Store, no Payload wrapper).
            string bareReceipt = JsonSerializer.Serialize(new { json = purchaseJson, signature = sig, skuDetails = new[] { "{}" } });
            var bareProof = new NativePurchaseProof
            {
                Store = verified.Store, ApplicationId = verified.ApplicationId, ProductId = verified.ProductId,
                TransactionId = verified.TransactionId, Receipt = bareReceipt,
            };
            Expect(RevenueProofBinding.TryBind(bareProof, verified, pubKey, out var outReceipt, out var outSig), "Unity IAP 5 bare Google receipt accepted");
            Expect(outReceipt == purchaseJson && outSig == sig, "extracted exact json and signature");

            // Negative controls: tampered bare receipt & LicenseTest order refused.
            var tampered = new NativePurchaseProof { Store = verified.Store, ApplicationId = verified.ApplicationId, ProductId = verified.ProductId, TransactionId = verified.TransactionId, Receipt = bareReceipt.Replace("GPA.iap5-1", "GPA.tampered") };
            Expect(!RevenueProofBinding.TryBind(tampered, verified, pubKey, out _, out _), "tampered bare receipt refused");
            verified.Environment = "LicenseTest";
            verified.TestOrder = true;
            Expect(!RevenueProofBinding.TryBind(bareProof, verified, pubKey, out _, out _), "LicenseTest order never retains revenue proof");
        });

        // =========================================================================
        // WO-20261113-653: Install-bound subscription revenue confirm/retry/re-offer
        // =========================================================================
        string subDbPath = Path.Combine(Path.GetTempPath(), "shorepop-subrev-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            foreach (var (label, makeStore) in new (string, Func<IPurchaseStore>)[]
                { ("memory", () => new InMemoryPurchaseStore()), ("sqlite", () => new SqlitePurchaseStore(subDbPath)) })
            {
                await testAsync($"WO-20261113-653 ({label}): subscription revenue confirm/retry honors only owning installId; anonymous delivers at most once", async () =>
                {
                    var store = makeStore();
                    var google = new HardeningGoogleApi();
                    var service = new PurchaseService(verifier,
                        new AppleTransactionChecker(verifier, ShorepopCatalog.AppleBundleId, AppleTransactionChecker.DefaultEnvironments),
                        new FakeApple(), google, store, ShorepopCatalog.GooglePackageId, NullLogger<PurchaseService>.Instance);

                    const string installA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
                    const string installB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

                    google.Subscriptions["tok-sub-653"] = Parse("""{"subscriptionState":"SUBSCRIPTION_STATE_ACTIVE","lineItems":[{"productId":"gg.shorepop.pass.monthly","expiryTime":"2030-01-02T03:04:05Z","latestSuccessfulOrderId":"GPA.653-0001"}]}""");
                    google.Orders["GPA.653-0001"] = Parse("""{"orderId":"GPA.653-0001","purchaseToken":"tok-sub-653","state":"PROCESSED","orderHistory":{"processedEvent":{"eventTime":"2026-11-13T10:00:00Z"}},"lineItems":[{"productId":"gg.shorepop.pass.monthly"}],"total":{"currencyCode":"USD","units":"2","nanos":990000000}}""");

                    await service.ValidateAsync("subPlayer", new NativePurchaseProof
                    {
                        Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId,
                        ProductId = SubProduct, TransactionId = "tok-sub-653", InstallId = installA.ToUpperInvariant(),
                    }, default);

                    // Cross-install confirm is ignored and charge stays pending for installA.
                    var wrongConfirm = service.ConfirmSubscriptionRevenue("subPlayer", new SubscriptionRevenueConfirmRequest
                    {
                        Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId,
                        RevenueOrderId = "GPA.653-0001", InstallId = installB,
                    });
                    Expect(!wrongConfirm.Accepted && !wrongConfirm.Deliver && wrongConfirm.Status == "ignored_install_mismatch", "cross-install confirm ignored");

                    // Anonymous confirm on an install-bound charge is also ignored.
                    var anonWrong = service.ConfirmSubscriptionRevenue("subPlayer", new SubscriptionRevenueConfirmRequest
                    {
                        Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId,
                        RevenueOrderId = "GPA.653-0001", InstallId = "",
                    });
                    Expect(!anonWrong.Accepted && !anonWrong.Deliver && anonWrong.Status == "ignored_install_mismatch", "anonymous confirm ignored on bound charge");

                    // Pending re-offer and revenue-proof authorization serve ONLY installA.
                    Expect(service.GetPendingSubscriptionRevenueCharges("subPlayer", new SubscriptionRevenuePendingRequest { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, InstallId = installB }).Count == 0, "installB gets no offer");
                    Expect(service.GetPendingSubscriptionRevenueCharges("subPlayer", new SubscriptionRevenuePendingRequest { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, InstallId = null }).Count == 0, "anonymous gets no offer");
                    Expect(!service.AuthorizeSubscriptionRevenueProof("subPlayer", ShorepopCatalog.GoogleStore, ShorepopCatalog.GooglePackageId, "GPA.653-0001", installB), "proof denied for installB");
                    Expect(service.AuthorizeSubscriptionRevenueProof("subPlayer", ShorepopCatalog.GoogleStore, ShorepopCatalog.GooglePackageId, "GPA.653-0001", installA), "proof allowed for installA");
                    Expect(service.GetPendingSubscriptionRevenueCharges("subPlayer", new SubscriptionRevenuePendingRequest { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, InstallId = installA }).Count == 1, "installA sees pending charge");

                    // First confirm from installA delivers once; retry delivers zero times.
                    var firstConfirm = service.ConfirmSubscriptionRevenue("subPlayer", new SubscriptionRevenueConfirmRequest
                    {
                        Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId,
                        RevenueOrderId = "GPA.653-0001", InstallId = installA,
                    });
                    Expect(firstConfirm.Accepted && firstConfirm.Deliver && firstConfirm.Status == "confirmed", "owning install delivers once");
                    var retryConfirm = service.ConfirmSubscriptionRevenue("subPlayer", new SubscriptionRevenueConfirmRequest
                    {
                        Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId,
                        RevenueOrderId = "GPA.653-0001", InstallId = installA,
                    });
                    Expect(retryConfirm.Accepted && !retryConfirm.Deliver && retryConfirm.Status == "already_confirmed", "retry delivers zero additional times");
                    Expect(service.GetPendingSubscriptionRevenueCharges("subPlayer", new SubscriptionRevenuePendingRequest { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, InstallId = installA }).Count == 0, "confirmed charge no longer pending");

                    // Anonymous charge delivers at most once (offer + confirm).
                    google.Subscriptions["tok-sub-anon"] = Parse("""{"subscriptionState":"SUBSCRIPTION_STATE_ACTIVE","lineItems":[{"productId":"gg.shorepop.pass.monthly","expiryTime":"2030-01-02T03:04:05Z","latestSuccessfulOrderId":"GPA.653-ANON"}]}""");
                    google.Orders["GPA.653-ANON"] = Parse("""{"orderId":"GPA.653-ANON","purchaseToken":"tok-sub-anon","state":"PROCESSED","orderHistory":{"processedEvent":{"eventTime":"2026-11-13T10:00:00Z"}},"lineItems":[{"productId":"gg.shorepop.pass.monthly"}],"total":{"currencyCode":"USD","units":"2","nanos":990000000}}""");
                    await service.ValidateAsync("subPlayer", new NativePurchaseProof
                    {
                        Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId,
                        ProductId = SubProduct, TransactionId = "tok-sub-anon", InstallId = "",
                    }, default);
                    Expect(service.GetPendingSubscriptionRevenueCharges("subPlayer", new SubscriptionRevenuePendingRequest { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, InstallId = "" }).Count == 1, "anonymous first offer");
                    Expect(service.GetPendingSubscriptionRevenueCharges("subPlayer", new SubscriptionRevenuePendingRequest { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, InstallId = "" }).Count == 0, "anonymous second offer suppressed");
                    var anonFirst = service.ConfirmSubscriptionRevenue("subPlayer", new SubscriptionRevenueConfirmRequest { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, RevenueOrderId = "GPA.653-ANON", InstallId = "" });
                    var anonSecond = service.ConfirmSubscriptionRevenue("subPlayer", new SubscriptionRevenueConfirmRequest { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId, RevenueOrderId = "GPA.653-ANON", InstallId = "" });
                    Expect(anonFirst.Accepted && anonFirst.Deliver && !anonSecond.Accepted && !anonSecond.Deliver, "anonymous confirm at-most-once");

                    // Malformed installId refused.
                    Rejects(422, "invalid_install_id", () => service.ConfirmSubscriptionRevenue("subPlayer", new SubscriptionRevenueConfirmRequest
                    {
                        Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.GooglePackageId,
                        RevenueOrderId = "GPA.653-0001", InstallId = "not-a-32-hex-install-id",
                    }));
                });
            }
            test("WO-20261113-653 (sqlite): install-bound charge persists across store reopen", () =>
            {
                var reopened = new SqlitePurchaseStore(subDbPath);
                var loaded = reopened.GetSubscriptionRevenueCharge(ShorepopCatalog.GoogleStore, ShorepopCatalog.GooglePackageId, "GPA.653-0001");
                Expect(loaded != null && loaded.OwningInstallId == "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" && loaded.Confirmed, "charge persisted");
            });
        }
        finally
        {
            foreach (string s in new[] { "", "-wal", "-shm" }) File.Delete(subDbPath + s);
        }

        // =========================================================================
        // WO-20261118-396: Health isolation, growing bounds, and render.yaml plan
        // =========================================================================
        await testAsync("WO-20261118-396: side-job failures and growing-bound warnings never fail /healthz routing readiness", async () =>
        {
            var sideJobs = new SideJobStatusTracker();
            var bounds = new GrowingBoundsMonitor();
            var store = new InMemoryPurchaseStore();

            // Simulate repeated retention/side-job failures.
            sideJobs.RecordFailure(RevenueProofRetentionService.JobName, "retention_purge_failed", DateTimeOffset.UtcNow);
            sideJobs.RecordFailure(RevenueProofRetentionService.JobName, "retention_purge_failed", DateTimeOffset.UtcNow);
            var snap = sideJobs.Snapshot();
            Expect(snap[RevenueProofRetentionService.JobName].Status == "degraded" && snap[RevenueProofRetentionService.JobName].ConsecutiveFailures == 2, "side job failure recorded");

            // Growing bounds: below 70% clear, >= 70% warns, > 100% warns without affecting healthz.
            Expect(!bounds.Observe("ledger_purchase_rows", 699_999, GrowingBoundsMonitor.SoftLedgerRowsLimit) && bounds.Warnings.Count == 0, "below 70% no warning");
            Expect(bounds.Observe("ledger_purchase_rows", 700_000, GrowingBoundsMonitor.SoftLedgerRowsLimit) && bounds.Warnings.Count == 1 && bounds.Warnings[0].Percent == 70, "70% warns");
            Expect(bounds.Observe("sqlite_ledger_bytes", GrowingBoundsMonitor.SoftSqliteBytesLimit + 1024, GrowingBoundsMonitor.SoftSqliteBytesLimit) && bounds.Warnings.Count == 2, "over-bound warns");

            // Routing health check stays healthy despite degraded side job and active bound warnings!
            var (available, storage) = await VerifierHealth.CheckRoutingReadinessAsync(true, store, default);
            Expect(available && storage == "in_memory", "healthz remains 200 ok despite side-job failures & bound warnings");

            // Negative control: unconfigured verifier reports unavailable.
            var (unconfAvail, unconfStorage) = await VerifierHealth.CheckRoutingReadinessAsync(false, store, default);
            Expect(!unconfAvail && unconfStorage == "unconfigured", "unconfigured healthz fails closed");

            // Drop below threshold clears warning.
            Expect(!bounds.Observe("ledger_purchase_rows", 10, GrowingBoundsMonitor.SoftLedgerRowsLimit) && bounds.Warnings.Count == 1, "resolved warning clears");
        });

        // =========================================================================
        // WO-20261013-64: Durable storage restart, render.yaml plan & catalog parity
        // =========================================================================
        string durableDb = Path.Combine(Path.GetTempPath(), "shorepop-durable-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            await testAsync("WO-20261013-64: granted receipt stays refused after restart while new receipt grants; render.yaml off free plan", async () =>
            {
                var appleChecker = new AppleTransactionChecker(verifier, ShorepopCatalog.AppleBundleId, AppleTransactionChecker.DefaultEnvironments);
                string jws1 = chain.Sign(AppleTx(Product, "64-tx-1", "Production"));
                string jws2 = chain.Sign(AppleTx(Product, "64-tx-2", "Production"));
                var store1 = new SqlitePurchaseStore(durableDb);
                var svc1 = new PurchaseService(verifier, appleChecker, new FakeApple(), new FakeGoogle(false), store1, ShorepopCatalog.GooglePackageId, NullLogger<PurchaseService>.Instance);
                await svc1.ValidateAsync("buyerA", new NativePurchaseProof { Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.AppleBundleId, ProductId = Product, TransactionId = "64-tx-1", AppleSignedTransaction = jws1 }, default);
                svc1.Ack("buyerA", new AckRequest { Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.AppleBundleId, TransactionId = "64-tx-1" });

                // Restart: brand-new store & service instance pointing at the same durable ledger.
                var store2 = new SqlitePurchaseStore(durableDb);
                var svc2 = new PurchaseService(verifier, appleChecker, new FakeApple(), new FakeGoogle(false), store2, ShorepopCatalog.GooglePackageId, NullLogger<PurchaseService>.Instance);
                await RejectsAsync(409, "transaction_owned_by_another_player", () => svc2.ValidateAsync("buyerB", new NativePurchaseProof { Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.AppleBundleId, ProductId = Product, TransactionId = "64-tx-1", AppleSignedTransaction = jws1 }, default));
                var fresh = await svc2.ValidateAsync("buyerB", new NativePurchaseProof { Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.AppleBundleId, ProductId = Product, TransactionId = "64-tx-2", AppleSignedTransaction = jws2 }, default);
                Expect(fresh.TransactionId == "64-tx-2", "new receipt still grants after restart");

                // Check render.yaml uses paid non-sleeping plan and /healthz.
                string renderYaml = File.ReadAllText(Path.Combine(FindVerifierDir(), "render.yaml"));
                Expect(!Regex.IsMatch(renderYaml, @"^\s*plan:\s*free\b", RegexOptions.Multiline), "render.yaml must not declare plan: free");
                Expect(Regex.IsMatch(renderYaml, @"^\s*plan:\s*starter\b", RegexOptions.Multiline), "render.yaml declares paid always-on starter plan");
                Expect(Regex.IsMatch(renderYaml, @"^\s*healthCheckPath:\s*/healthz\b", RegexOptions.Multiline), "render.yaml healthCheckPath is /healthz");
            });
        }
        finally
        {
            foreach (string s in new[] { "", "-wal", "-shm" }) File.Delete(durableDb + s);
        }

        test("WO-20261013-64: live client NativeProductTable matches server ShorepopCatalog 17/17 with negative controls", () =>
        {
            string? policyPath = TryFindClientPolicyPath();
            IReadOnlyCollection<string> baselineSkus;
            if (policyPath != null)
            {
                string text = File.ReadAllText(policyPath);
                var matches = Regex.Matches(text, @"new\s+NativeProductRow\([^,]+,\s*""(gg\.shorepop\.[^""]+)"",\s*NativeProductKind\.(Consumable|NonConsumable|Subscription)\)");
                Expect(matches.Count > 0, "failed to parse NativeProductTable.All");
                var clientSkus = new Dictionary<string, ProductKind>(StringComparer.Ordinal);
                foreach (Match m in matches)
                {
                    clientSkus[m.Groups[1].Value] = Enum.Parse<ProductKind>(m.Groups[2].Value);
                }
                Expect(ShorepopCatalog.ValidateCatalogParity(clientSkus.Keys, out var missing, out var extra),
                    $"catalog parity mismatch missing=[{string.Join(",", missing)}] extra=[{string.Join(",", extra)}]");
                foreach (var (sku, kind) in clientSkus)
                    Expect(ShorepopCatalog.CatalogKind(sku) == kind, $"kind mismatch for {sku}");
                baselineSkus = clientSkus.Keys.ToList();
            }
            else
            {
                Expect(ShorepopCatalog.ProductIds.Count == 17, $"expected 17 catalog products, got {ShorepopCatalog.ProductIds.Count}");
                baselineSkus = ShorepopCatalog.ProductIds.ToList();
            }

            // Negative controls: empty, missing SKU, extra SKU all fail parity check.
            Expect(!ShorepopCatalog.ValidateCatalogParity([], out _, out _), "empty catalog must fail");
            Expect(!ShorepopCatalog.ValidateCatalogParity(baselineSkus.Concat(["gg.shorepop.missing_from_verifier"]), out var m2, out _) && m2.Contains("gg.shorepop.missing_from_verifier"), "missing SKU must fail");
            Expect(!ShorepopCatalog.ValidateCatalogParity(baselineSkus.Where(k => k != "gg.shorepop.fishing.rod_ocean"), out _, out var e2) && e2.Contains("gg.shorepop.fishing.rod_ocean"), "extra SKU must fail");
        });

        // =========================================================================
        // WO-20261118-899: Same-timestamp refund + replacement deterministic tiebreak
        // =========================================================================
        string tieDb = Path.Combine(Path.GetTempPath(), "shorepop-tie-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            foreach (var (label, makeStore) in new (string, Func<IPurchaseStore>)[]
                { ("memory", () => new InMemoryPurchaseStore()), ("sqlite", () => new SqlitePurchaseStore(tieDb)) })
            {
                test($"WO-20261118-899 ({label}): same-timestamp refund and replacement resolve deterministically by order/transaction id in either arrival order", () =>
                {
                    var store = makeStore();
                    const long sameTime = 1795000000000;
                    foreach (bool refundFirst in new[] { true, false })
                    {
                        string token = "tie-sub-token-" + refundFirst;
                        var refundedOld = new PurchaseRecord(ShorepopCatalog.GoogleStore, ShorepopCatalog.GooglePackageId, token, "pTie", SubProduct,
                            "Production", false, ShorepopCatalog.UnixMsToTicks(sameTime + 86400000L), null, null, sameTime,
                            false, 0, "", "GPA.899-0001", sameTime, true);
                        var paidReplacement = new PurchaseRecord(ShorepopCatalog.GoogleStore, ShorepopCatalog.GooglePackageId, token, "pTie", SubProduct,
                            "Production", false, ShorepopCatalog.UnixMsToTicks(sameTime + 30L * 86400000L), null, null, sameTime,
                            true, 2_990_000, "USD", "GPA.899-0002", sameTime, false);

                        if (refundFirst)
                        {
                            store.Bind(refundedOld, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                            store.Bind(paidReplacement, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                        }
                        else
                        {
                            store.Bind(paidReplacement, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                            store.Bind(refundedOld, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                        }

                        var row = store.List("pTie", ShorepopCatalog.GoogleStore, ShorepopCatalog.GooglePackageId).Single(r => r.TransactionId == token);
                        Expect(row.RevenueTransactionId == "GPA.899-0002" && row.RevenueVerified && !row.RevenueRefunded && row.GrossRevenueMicros == 2_990_000,
                            $"{label} arrival refundFirst={refundFirst}: replacement order lost to same-timestamp refund");
                        Expect(row.RefundedRevenueOrderIdsJson.Contains("GPA.899-0001", StringComparison.Ordinal) &&
                            !row.RefundedRevenueOrderIdsJson.Contains("GPA.899-0002", StringComparison.Ordinal),
                            $"{label} arrival refundFirst={refundFirst}: refund must apply only to its own order id");
                    }

                    // Distinct subscription transactions on identical timestamp: refund on tx-1 never zeroes replacement tx-2's active expiry.
                    long expiry1 = ShorepopCatalog.UnixMsToTicks(sameTime + 10L * 86400000L);
                    long expiry2 = ShorepopCatalog.UnixMsToTicks(sameTime + 30L * 86400000L);
                    foreach (bool refundFirst in new[] { true, false })
                    {
                        string player = "pTieSub-" + refundFirst;
                        var tx1Refunded = new PurchaseRecord(ShorepopCatalog.AppleStore, ShorepopCatalog.AppleBundleId, "sub-tx-" + refundFirst + "-1", player, SubProduct,
                            "Production", true, expiry1, null, null, sameTime);
                        var tx2Active = new PurchaseRecord(ShorepopCatalog.AppleStore, ShorepopCatalog.AppleBundleId, "sub-tx-" + refundFirst + "-2", player, SubProduct,
                            "Production", false, expiry2, null, null, sameTime);
                        if (refundFirst)
                        {
                            store.Bind(tx1Refunded, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                            store.Bind(tx2Active, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                        }
                        else
                        {
                            store.Bind(tx2Active, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                            store.Bind(tx1Refunded, DateTimeOffset.UtcNow, _ => ClaimDecision.Claimed);
                        }
                        var (_, currentExpiry) = store.Subscription(player, ShorepopCatalog.AppleStore, ShorepopCatalog.AppleBundleId);
                        Expect(currentExpiry == expiry2, $"{label} refundFirst={refundFirst}: replacement subscription expiry zeroed");
                    }
                });
            }
        }
        finally
        {
            foreach (string s in new[] { "", "-wal", "-shm" }) File.Delete(tieDb + s);
        }
    }

    private static string FindVerifierDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "render.yaml")) &&
                File.Exists(Path.Combine(dir.FullName, "Shorepop.Verifier.csproj")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not locate verifier directory from " + AppContext.BaseDirectory);
    }

    private static string? TryFindClientPolicyPath()
    {
        string? envRepo = Environment.GetEnvironmentVariable("SHOREPOP_CLIENT_REPO");
        if (!string.IsNullOrWhiteSpace(envRepo))
        {
            string p = Path.Combine(envRepo, "Assets", "Scripts", "Services", "Commerce", "NativePurchasePolicy.cs");
            if (File.Exists(p)) return p;
        }
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string p = Path.Combine(dir.FullName, "Assets", "Scripts", "Services", "Commerce", "NativePurchasePolicy.cs");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        return null;
    }

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static HttpResponseMessage JsonResp(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class ScriptedHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class HardeningGoogleApi : IGooglePlayApi
    {
        public bool Configured => true;
        public Dictionary<string, JsonElement> Products { get; } = new();
        public Dictionary<string, JsonElement> LegacyProducts { get; } = new();
        public Dictionary<string, JsonElement> Subscriptions { get; } = new();
        public Dictionary<string, JsonElement> Orders { get; } = new();
        public Exception? FinishError { get; set; }
        public Exception? ReadError { get; set; }

        public Task<JsonElement?> GetProductPurchaseAsync(string packageName, string token, CancellationToken cancel) =>
            ReadError != null ? Task.FromException<JsonElement?>(ReadError) : Task.FromResult(Products.TryGetValue(token, out var v) ? (JsonElement?)v : null);

        public Task<JsonElement?> GetLegacyProductPurchaseAsync(string packageName, string productId, string token, CancellationToken cancel) =>
            ReadError != null ? Task.FromException<JsonElement?>(ReadError) : Task.FromResult(LegacyProducts.TryGetValue(token, out var v) ? (JsonElement?)v : null);

        public Task<JsonElement?> GetSubscriptionPurchaseAsync(string packageName, string token, CancellationToken cancel) =>
            ReadError != null ? Task.FromException<JsonElement?>(ReadError) : Task.FromResult(Subscriptions.TryGetValue(token, out var v) ? (JsonElement?)v : null);

        public Task<JsonElement?> GetOrderAsync(string packageName, string orderId, CancellationToken cancel) =>
            Task.FromResult(Orders.TryGetValue(orderId, out var v) ? (JsonElement?)v : null);

        public Task AcknowledgeProductAsync(string packageName, string productId, string token, CancellationToken cancel) =>
            FinishError != null ? Task.FromException(FinishError) : Task.CompletedTask;

        public Task ConsumeProductAsync(string packageName, string productId, string token, CancellationToken cancel) =>
            FinishError != null ? Task.FromException(FinishError) : Task.CompletedTask;

        public Task AcknowledgeSubscriptionAsync(string packageName, string productId, string token, CancellationToken cancel) =>
            FinishError != null ? Task.FromException(FinishError) : Task.CompletedTask;
    }
}
