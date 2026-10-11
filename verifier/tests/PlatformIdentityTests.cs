using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Shorepop.Verifier;

internal static class PlatformIdentityTests
{
    public static void Run(Action<string, Action> test)
    {
        const string appleApp = ShorepopCatalog.AppleBundleId, googleApp = ShorepopCatalog.GooglePackageId;
        const string product = "gg.shorepop.gems.80", player = "platformPlayer";
        var chain = TestChain.Create();
        var verifier = new AppleJwsVerifier([chain.RootDer]);
        var apple = new AppleTransactionChecker(verifier, appleApp, AppleTransactionChecker.DefaultEnvironments);
        var legacyApple = new AppleTransactionChecker(verifier, googleApp, AppleTransactionChecker.DefaultEnvironments);
        var store = new InMemoryPurchaseStore();
        var google = new FakeGoogle(true);
        var service = new PurchaseService(verifier, apple, new FakeApple(), google, store, googleApp, NullLogger<PurchaseService>.Instance);
        var legacy = new PurchaseService(verifier, legacyApple, new FakeApple(), google, store, googleApp,
            NullLogger<PurchaseService>.Instance, appleBundleId: googleApp);
        NativePurchaseProof Apple(string app, string id)
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            string jws = chain.Sign(new Dictionary<string, object> { ["bundleId"] = app, ["environment"] = "Sandbox",
                ["productId"] = product, ["transactionId"] = id, ["quantity"] = 1, ["type"] = "Consumable",
                ["signedDate"] = now, ["purchaseDate"] = now - 1000, ["price"] = 1990L, ["currency"] = "USD" });
            return new NativePurchaseProof { Store = ShorepopCatalog.AppleStore, ApplicationId = app, ProductId = product,
                TransactionId = id, AppleSignedTransaction = jws };
        }
        NativePurchaseProof Google(string app, string id)
        {
            google.Products[id] = """{"productLineItem":[{"productId":"gg.shorepop.gems.80"}],"purchaseStateContext":{"purchaseState":"PURCHASED"}}""";
            return new NativePurchaseProof { Store = ShorepopCatalog.GoogleStore, ApplicationId = app, ProductId = product, TransactionId = id };
        }
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        void Refuse(string code, Action action)
        {
            try { action(); }
            catch (VerifierRefusal refused) { Check(refused.Code == code, "wrong refusal: " + refused.Code); return; }
            throw new Exception("accepted invalid platform identity");
        }
        test("fishing catalog: all products retain their exact settlement kind", () =>
        {
            Check(ShorepopCatalog.ProductIds.Count == 17, "catalog must match integrated 17-product client");
            foreach (string id in new[] { "gg.shorepop.fishing.tackle_box", "gg.shorepop.fishing.fishers_crate" })
            {
                Check(ShorepopCatalog.ProductIds.Contains(id), "fishing product absent");
                Check(ShorepopCatalog.CatalogKind(id) == ProductKind.Consumable, "fishing bundle must be single-owner");
                Check(ShorepopCatalog.AppleKind("Non-Consumable", id) == ProductKind.Consumable, "store type widened bundle ownership");
            }
            const string rod = "gg.shorepop.fishing.rod_ocean";
            Check(ShorepopCatalog.ProductIds.Contains(rod) && ShorepopCatalog.CatalogKind(rod) == ProductKind.NonConsumable,
                "permanent fishing rod must restore as entitlement");
            Check(!ShorepopCatalog.SingleOwner(ShorepopCatalog.CatalogKind(rod)), "rod restore restricted to one anonymous player");
        });
        test("platform identity: Apple uses Michael bundle and Android retains existing package", () =>
        {
            var a = service.ValidateAsync(player, Apple(appleApp, "shared-identity-transaction"), default).GetAwaiter().GetResult();
            var g = service.ValidateAsync(player, Google(googleApp, "shared-identity-transaction"), default).GetAwaiter().GetResult();
            Check(a.ApplicationId == appleApp && g.ApplicationId == googleApp, "platform identity changed");
            Check(a.RevenueVerified && a.GrossRevenueMicros == 1_990_000, "Apple verified economics lost");
            Check(service.Ack(player, new AckRequest { Store = ShorepopCatalog.AppleStore, TransactionId = a.TransactionId }).Acked, "Apple default ack failed");
            Check(service.Ack(player, new AckRequest { Store = ShorepopCatalog.GoogleStore, TransactionId = g.TransactionId }).Acked, "Google default ack failed");
            Check(store.Claimants(ShorepopCatalog.AppleStore, appleApp, a.TransactionId).SequenceEqual([player]), "Apple ownership lost");
            Check(store.Claimants(ShorepopCatalog.GoogleStore, googleApp, g.TransactionId).SequenceEqual([player]), "Google ownership lost");
        });
        test("platform identity: cross-store package and relabeled Apple receipt refused", () =>
        {
            Refuse("application_mismatch", () => service.ValidateAsync(player, Apple(googleApp, "old-apple"), default).GetAwaiter().GetResult());
            Refuse("application_mismatch", () => service.ValidateAsync(player, Google(appleApp, "wrong-google"), default).GetAwaiter().GetResult());
            var relabeled = Apple(googleApp, "relabeled-apple"); relabeled.ApplicationId = appleApp;
            Refuse("bundle_mismatch", () => service.ValidateAsync(player, relabeled, default).GetAwaiter().GetResult());
            Refuse("transaction_owned_by_another_player", () => service.ValidateAsync("otherPlayer", Apple(appleApp, "shared-identity-transaction"), default).GetAwaiter().GetResult());
        });
        test("platform identity: reconciliation and recovery select only current store application", () =>
        {
            legacy.ValidateAsync(player, Apple(googleApp, "legacy-apple-test"), default).GetAwaiter().GetResult();
            var result = service.ReconcileAsync(player, new ReconcileRequest { Store = ShorepopCatalog.AppleStore, ApplicationId = appleApp }, default).GetAwaiter().GetResult();
            Check(result.Count == 1 && result[0].ApplicationId == appleApp, "legacy Apple rows included");
            Check(store.List(player, ShorepopCatalog.AppleStore, googleApp).Count == 1, "legacy Apple row changed or deleted");
            Refuse("application_mismatch", () => service.ReconcileAsync(player, new ReconcileRequest { Store = ShorepopCatalog.AppleStore, ApplicationId = googleApp }, default).GetAwaiter().GetResult());
            Refuse("application_mismatch", () => service.RefreshOwnedRevenueAsync(player, ShorepopCatalog.AppleStore, googleApp, "legacy-apple-test", default).GetAwaiter().GetResult());
            Refuse("transaction_not_found", () => service.RefreshOwnedRevenueAsync("otherPlayer", ShorepopCatalog.AppleStore, appleApp, "shared-identity-transaction", default).GetAwaiter().GetResult());
            Check(service.RefreshOwnedRevenueAsync(player, ShorepopCatalog.AppleStore, appleApp, "shared-identity-transaction", default).GetAwaiter().GetResult().ApplicationId == appleApp, "Apple refresh lost identity");
        });
        test("platform identity: configuration defaults and exact recovery allowlist", () =>
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
                ["UGS_PROJECT_ID"] = "aea5f7f0-ebcb-4745-98c5-378bb2ad2feb", ["APP_BUNDLE_ID"] = googleApp }).Build();
            var options = VerifierOptions.FromConfiguration(config);
            Check(options.Configured && options.GooglePackageId == googleApp && options.AppleBundleId == appleApp, "platform configuration defaults");
            Check(ShorepopCatalog.MatchesApplication(ShorepopCatalog.AppleStore, appleApp), "Apple allowlist");
            Check(!ShorepopCatalog.MatchesApplication(ShorepopCatalog.AppleStore, googleApp), "old Apple accepted");
            Check(!ShorepopCatalog.MatchesApplication(ShorepopCatalog.GoogleStore, appleApp), "Apple ID accepted for Google");
            config["APPLE_BUNDLE_ID"] = googleApp;
            Check(!VerifierOptions.FromConfiguration(config).Configured, "old Apple config not refused");
        });
    }
}
