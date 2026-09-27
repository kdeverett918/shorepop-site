using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shorepop.Verifier;

// Console suite for the purchase verifier. Exit 0 = every case passed. Every "must fail" case is
// a known-bad input fired at the real verifier (tampered payload, wrong root, wrong bundle, bad OIDs).
int passed = 0, failed = 0;
string res = Path.Combine(AppContext.BaseDirectory, "Resources", "apple-library");
var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(res, "x509-vectors.json"))).RootElement;
string V(string name) => vectors.GetProperty(name).GetString()!;
var effective = DateTimeOffset.FromUnixTimeSeconds(vectors.GetProperty("EFFECTIVE_DATE").GetInt64());
byte[] testRoot = File.ReadAllBytes(Path.Combine(res, "testCA.der"));
byte[] x509TestRoot = Convert.FromBase64String(V("ROOT_CA_BASE64_ENCODED"));
string transactionInfo = File.ReadAllText(Path.Combine(res, "transactionInfo")).Trim();
var production = AppleJwsVerifier.Production();
var appleTest = new AppleJwsVerifier([testRoot]);

void Case(string name, Action body)
{
    try { body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + " " + error.Message); }
}
async Task CaseAsync(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine("PASS " + name); }
    catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error.GetType().Name + " " + error.Message); }
}
void Expect(bool condition, string message) { if (!condition) throw new Exception(message); }
void Rejects(string reasonPrefix, Action body)
{
    try { body(); }
    catch (AppleVerificationException e) { Expect(e.Reason.StartsWith(reasonPrefix), $"reason {e.Reason}, wanted {reasonPrefix}"); return; }
    catch (VerifierRefusal r) { Expect(r.Code == reasonPrefix, $"refusal {r.Status} {r.Code}, wanted {reasonPrefix}"); return; }
    throw new Exception("accepted a known-bad input (wanted " + reasonPrefix + ")");
}
async Task RejectsAsync(int status, string code, Func<Task> body)
{
    try { await body(); }
    catch (VerifierRefusal r) { Expect(r.Status == status && r.Code == code, $"refusal {r.Status} {r.Code}, wanted {status} {code}"); return; }
    throw new Exception($"accepted a known-bad input (wanted {status} {code})");
}

// ---- Apple's published vectors (app-store-server-library-python, MIT) ----
Case("apple vector: signed transaction verifies against Apple's TEST root", () =>
{
    var payload = appleTest.VerifyAndDecode(transactionInfo, DateTimeOffset.UtcNow);
    Expect(payload.GetProperty("environment").GetString() == "Sandbox", "environment");
    Expect(payload.GetProperty("bundleId").GetString() == "com.example", "bundle");
});
Case("apple vector: same JWS REJECTED by the production verifier (wrong root)", () =>
    Rejects("chain_intermediate_issuer", () => production.VerifyAndDecode(transactionInfo, DateTimeOffset.UtcNow)));
Case("apple vector: tampered payload REJECTED", () =>
{
    var p = transactionInfo.Split('.');
    string forged = B64(Encoding.UTF8.GetBytes("{\"environment\":\"Production\",\"bundleId\":\"com.shorepop.game\",\"signedDate\":1672956154000}"));
    Rejects("jws_signature_invalid", () => appleTest.VerifyAndDecode(p[0] + "." + forged + "." + p[2], DateTimeOffset.UtcNow));
});
Case("apple vector: tampered signature REJECTED", () =>
{
    var p = transactionInfo.Split('.');
    var sig = AppleJwsVerifier.Base64Url(p[2]); sig[10] ^= 1;
    Rejects("jws_signature_invalid", () => appleTest.VerifyAndDecode(p[0] + "." + p[1] + "." + B64(sig), DateTimeOffset.UtcNow));
});
Case("apple vector: wrong bundle id (com.example) REJECTED by the transaction checker", () =>
{
    var checker = new AppleTransactionChecker(appleTest, ShorepopCatalog.BundleId, AppleTransactionChecker.DefaultEnvironments);
    Rejects("bundle_mismatch", () => checker.Interpret(appleTest.VerifyAndDecode(transactionInfo, DateTimeOffset.UtcNow), transactionInfo));
});
Case("apple vector: missing x5c header REJECTED", () =>
    Rejects("jws_x5c_missing", () => appleTest.VerifyAndDecode(File.ReadAllText(Path.Combine(res, "missingX5CHeaderClaim")).Trim(), DateTimeOffset.UtcNow)));
Case("apple vector: malformed JWS REJECTED", () =>
{
    Rejects("jws_format", () => appleTest.VerifyAndDecode("a.b.c.d", DateTimeOffset.UtcNow));
    Rejects("jws_header", () => appleTest.VerifyAndDecode("a.b.c", DateTimeOffset.UtcNow));
});
var x509Test = new AppleJwsVerifier([x509TestRoot]);
Case("apple x509 vector: valid test chain returns the expected leaf key", () =>
{
    using var key = x509Test.VerifyChain([V("LEAF_CERT_BASE64_ENCODED"), V("INTERMEDIATE_CA_BASE64_ENCODED"), V("ROOT_CA_BASE64_ENCODED")], effective);
    string pem = key.ExportSubjectPublicKeyInfoPem().Replace("\r", "");
    Expect(pem.Trim() == V("LEAF_CERT_PUBLIC_KEY").Trim(), "leaf key");
});
Case("apple x509 vector: intermediate without the WWDR OID REJECTED", () => Rejects("chain_intermediate_oid", () =>
    x509Test.VerifyChain([V("LEAF_CERT_FOR_INTERMEDIATE_CA_INVALID_OID_BASE64_ENCODED"), V("INTERMEDIATE_CA_INVALID_OID_BASE64_ENCODED"), V("ROOT_CA_BASE64_ENCODED")], effective)));
Case("apple x509 vector: leaf without the receipt-signing OID REJECTED", () => Rejects("chain_leaf_oid", () =>
    x509Test.VerifyChain([V("LEAF_CERT_INVALID_OID_BASE64_ENCODED"), V("INTERMEDIATE_CA_BASE64_ENCODED"), V("ROOT_CA_BASE64_ENCODED")], effective)));
Case("apple x509 vector: two-certificate chain REJECTED", () => Rejects("chain_length", () =>
    x509Test.VerifyChain([V("INTERMEDIATE_CA_BASE64_ENCODED"), V("ROOT_CA_BASE64_ENCODED")], effective)));
Case("apple x509 vector: garbage certificate REJECTED", () => Rejects("chain_certificate", () =>
    x509Test.VerifyChain(["abc", V("INTERMEDIATE_CA_BASE64_ENCODED"), V("ROOT_CA_BASE64_ENCODED")], effective)));
Case("apple x509 vector: expired chain REJECTED", () => Rejects("chain_not_valid_at_time", () =>
    x509Test.VerifyChain([V("LEAF_CERT_BASE64_ENCODED"), V("INTERMEDIATE_CA_BASE64_ENCODED"), V("ROOT_CA_BASE64_ENCODED")], DateTimeOffset.FromUnixTimeSeconds(2280946846))));
Case("apple x509 vector: test chain REJECTED when only the real Apple root is trusted", () => Rejects("chain_intermediate_issuer", () =>
    production.VerifyChain([V("LEAF_CERT_BASE64_ENCODED"), V("INTERMEDIATE_CA_BASE64_ENCODED"), V("ROOT_CA_BASE64_ENCODED")], effective)));
Case("apple x509 vector: test leaf/intermediate presented with the real G3 root REJECTED (signature)", () => Rejects("chain_intermediate_issuer", () =>
    production.VerifyChain([V("LEAF_CERT_BASE64_ENCODED"), V("INTERMEDIATE_CA_BASE64_ENCODED"), V("REAL_APPLE_ROOT_BASE64_ENCODED")], effective)));
Case("apple REAL chain (App Store signing cert <- WWDR G6 <- Root G3) verifies with the embedded G3 root", () =>
{
    Expect(Convert.FromBase64String(V("REAL_APPLE_ROOT_BASE64_ENCODED")).SequenceEqual(AppleJwsVerifier.AppleRootG3()), "embedded root differs from Apple's");
    using var key = production.VerifyChain([V("REAL_APPLE_SIGNING_CERTIFICATE_BASE64_ENCODED"), V("REAL_APPLE_INTERMEDIATE_BASE64_ENCODED"), V("REAL_APPLE_ROOT_BASE64_ENCODED")], effective);
    Expect(key.KeySize == 256, "leaf key size");
});

// ---- Locally generated Apple-shaped chain for full Shorepop transactions ----
var chain = TestChain.Create();
var localVerifier = new AppleJwsVerifier([chain.RootDer]);
var checker = new AppleTransactionChecker(localVerifier, ShorepopCatalog.BundleId, AppleTransactionChecker.DefaultEnvironments);
long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
Dictionary<string, object> Tx(string product, string id, string type = "Consumable") => new()
{
    ["transactionId"] = id, ["originalTransactionId"] = id, ["bundleId"] = ShorepopCatalog.BundleId, ["productId"] = product,
    ["purchaseDate"] = nowMs - 1000, ["quantity"] = 1, ["type"] = type, ["environment"] = "Sandbox", ["signedDate"] = nowMs,
};
Case("local chain: valid consumable accepted with environment", () =>
{
    var v = checker.Check(chain.Sign(Tx("gg.shorepop.gems.80", "2000000001")), "gg.shorepop.gems.80", "2000000001", DateTimeOffset.UtcNow);
    Expect(v.Environment == "Sandbox" && !v.Refunded && v.PurchasedPeriodExpiryUtcTicks == 0, "verdict");
});
Case("local chain: subscription expiresDate -> PurchasedPeriodExpiryUtcTicks; revocationDate -> Refunded", () =>
{
    var tx = Tx(ShorepopCatalog.SubscriptionProductId, "2000000002", "Auto-Renewable Subscription");
    long expires = nowMs + 30L * 86400000; tx["expiresDate"] = expires;
    var v = checker.Check(chain.Sign(tx), ShorepopCatalog.SubscriptionProductId, "2000000002", DateTimeOffset.UtcNow);
    Expect(v.PurchasedPeriodExpiryUtcTicks == DateTimeOffset.FromUnixTimeMilliseconds(expires).UtcTicks && !v.Refunded, "expiry");
    tx["revocationDate"] = nowMs;
    Expect(checker.Check(chain.Sign(tx), ShorepopCatalog.SubscriptionProductId, "2000000002", DateTimeOffset.UtcNow).Refunded, "refund");
});
Case("local chain: wrong bundle id REJECTED", () =>
{
    var tx = Tx("gg.shorepop.gems.80", "1"); tx["bundleId"] = "com.shorepop.gamex";
    Rejects("bundle_mismatch", () => checker.Check(chain.Sign(tx), "gg.shorepop.gems.80", "1", DateTimeOffset.UtcNow));
});
Case("local chain: product mismatch vs the claim REJECTED", () =>
    Rejects("product_mismatch", () => checker.Check(chain.Sign(Tx("gg.shorepop.gems.80", "1")), "gg.shorepop.gems.2800", "1", DateTimeOffset.UtcNow)));
Case("local chain: transaction id mismatch vs the claim REJECTED", () =>
    Rejects("transaction_mismatch", () => checker.Check(chain.Sign(Tx("gg.shorepop.gems.80", "1")), "gg.shorepop.gems.80", "2", DateTimeOffset.UtcNow)));
Case("local chain: quantity 2 REJECTED", () =>
{
    var tx = Tx("gg.shorepop.gems.80", "1"); tx["quantity"] = 2;
    Rejects("quantity_not_one", () => checker.Check(chain.Sign(tx), "gg.shorepop.gems.80", "1", DateTimeOffset.UtcNow));
});
Case("local chain: Xcode environment REJECTED", () =>
{
    var tx = Tx("gg.shorepop.gems.80", "1"); tx["environment"] = "Xcode";
    Rejects("environment_not_allowed", () => checker.Check(chain.Sign(tx), "gg.shorepop.gems.80", "1", DateTimeOffset.UtcNow));
});
Case("local chain: subscription without expiresDate REJECTED", () =>
    Rejects("expiry_missing", () => checker.Check(chain.Sign(Tx(ShorepopCatalog.SubscriptionProductId, "1", "Auto-Renewable Subscription")), ShorepopCatalog.SubscriptionProductId, "1", DateTimeOffset.UtcNow)));
Case("local chain: tampered payload REJECTED (signature)", () =>
{
    var p = chain.Sign(Tx("gg.shorepop.gems.80", "1")).Split('.');
    string forged = B64(JsonSerializer.SerializeToUtf8Bytes(Tx("gg.shorepop.gems.2800", "1")));
    Rejects("apple_signature_invalid", () => checker.Check(p[0] + "." + forged + "." + p[2], "gg.shorepop.gems.2800", "1", DateTimeOffset.UtcNow));
});
Case("local chain: signed by a key other than the leaf REJECTED", () =>
{
    using var rogue = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Rejects("jws_signature_invalid", () => localVerifier.VerifyAndDecode(chain.Sign(Tx("gg.shorepop.gems.80", "1"), rogue), DateTimeOffset.UtcNow));
});
Case("local chain: a different trusted root REJECTS the chain", () =>
{
    var other = TestChain.Create();
    Rejects("chain_untrusted_root", () => new AppleJwsVerifier([other.RootDer]).VerifyAndDecode(chain.Sign(Tx("gg.shorepop.gems.80", "1")), DateTimeOffset.UtcNow));
});
Case("local chain: forged look-alike chain presenting the genuine root bytes REJECTED", () =>
{
    var forger = TestChain.Create();
    Rejects("chain_untrusted_root", () => localVerifier.VerifyAndDecode(forger.Sign(Tx("gg.shorepop.gems.80", "1"), rootOverride: chain.RootDer), DateTimeOffset.UtcNow));
});
Case("local chain: leaf without Apple OID / intermediate not a CA REJECTED", () =>
{
    var noLeafOid = TestChain.Create(leafOid: false);
    Rejects("chain_leaf_oid", () => new AppleJwsVerifier([noLeafOid.RootDer]).VerifyAndDecode(noLeafOid.Sign(Tx("gg.shorepop.gems.80", "1")), DateTimeOffset.UtcNow));
    var notCa = TestChain.Create(intermediateCa: false);
    Rejects("chain_intermediate_not_ca", () => new AppleJwsVerifier([notCa.RootDer]).VerifyAndDecode(notCa.Sign(Tx("gg.shorepop.gems.80", "1")), DateTimeOffset.UtcNow));
});
Case("local chain: alg none/HS256 header REJECTED", () =>
{
    var p = chain.Sign(Tx("gg.shorepop.gems.80", "1")).Split('.');
    var header = JsonSerializer.Deserialize<Dictionary<string, object>>(AppleJwsVerifier.Base64Url(p[0]))!; header["alg"] = "none";
    Rejects("jws_alg", () => localVerifier.VerifyAndDecode(B64(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + p[1] + "." + p[2], DateTimeOffset.UtcNow));
});

// ---- Service: player binding, subscription revisions, reconcile, Google fail-closed ----
PurchaseService Service(IPurchaseStore store, IGooglePlayApi? google = null, IAppleServerApi? appleApi = null, Func<DateTimeOffset>? clock = null) =>
    new(localVerifier, checker, appleApi ?? new FakeApple(), google ?? new FakeGoogle(false), store, ShorepopCatalog.BundleId, NullLogger<PurchaseService>.Instance, clock);
NativePurchaseProof AppleProof(string product, string id, string jws) => new()
    { Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.BundleId, ProductId = product, TransactionId = id, Receipt = "", AppleSignedTransaction = jws };

string dbPath = Path.Combine(Path.GetTempPath(), "shorepop-verifier-test-" + Guid.NewGuid().ToString("N") + ".db");
foreach (var (label, makeStore) in new (string, Func<IPurchaseStore>)[] { ("memory", () => new InMemoryPurchaseStore()), ("sqlite", () => new SqlitePurchaseStore(dbPath)) })
{
    var store = makeStore();
    var service = Service(store);
    string jws = chain.Sign(Tx("gg.shorepop.noads", "3000000001", "Non-Consumable"));
    await CaseAsync($"{label}: non-consumable restore by a second player (reinstall) -> 200 with the entitlement, both claims recorded", async () =>
    {
        var first = await service.ValidateAsync("playerA", AppleProof("gg.shorepop.noads", "3000000001", jws), default);
        Expect(first.TransactionId == "3000000001" && first.Store == "AppleAppStore" && first.ApplicationId == ShorepopCatalog.BundleId && first.Environment == "Sandbox", "result");
        var again = await service.ValidateAsync("playerA", AppleProof("gg.shorepop.noads", "3000000001", jws), default);
        Expect(again.TransactionId == first.TransactionId, "retry");
        var restored = await service.ValidateAsync("playerB", AppleProof("gg.shorepop.noads", "3000000001", jws), default);
        Expect(restored.TransactionId == "3000000001" && restored.ProductId == "gg.shorepop.noads" && !restored.Refunded, "restore result");
        var bRows = await service.ReconcileAsync("playerB", new ReconcileRequest { Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.BundleId }, default);
        Expect(bRows.Count == 1 && bRows[0].ProductId == "gg.shorepop.noads", "playerB reconcile sees the entitlement");
        var claimants = store.Claimants(ShorepopCatalog.AppleStore, ShorepopCatalog.BundleId, "3000000001");
        Expect(claimants.SequenceEqual(["playerA", "playerB"]), "claimants " + string.Join(",", claimants));
        string starter = chain.Sign(Tx("gg.shorepop.starter", "3000000002", "Non-Consumable"));
        await service.ValidateAsync("playerA", AppleProof("gg.shorepop.starter", "3000000002", starter), default);
        Expect((await service.ValidateAsync("playerB", AppleProof("gg.shorepop.starter", "3000000002", starter), default)).ProductId == "gg.shorepop.starter", "starter restore");
    });
    await CaseAsync($"{label}: consumable presented by a second player -> 409, not claimed", async () =>
    {
        string[] consumables = ["gg.shorepop.gems.80", "gg.shorepop.coins.600", "gg.shorepop.lives.refill", "gg.shorepop.abilities.bundle", "gg.shorepop.welcome", "gg.shorepop.shellbank"];
        for (int i = 0; i < consumables.Length; i++)
        {
            string product = consumables[i], id = "310000000" + i;
            string gems = chain.Sign(Tx(product, id));
            await service.ValidateAsync("playerC", AppleProof(product, id, gems), default);
            await RejectsAsync(409, "transaction_owned_by_another_player", () => service.ValidateAsync("playerD", AppleProof(product, id, gems), default));
            Expect(store.Claimants(ShorepopCatalog.AppleStore, ShorepopCatalog.BundleId, id).SequenceEqual(["playerC"]), "only the owner claims " + product);
        }
        Expect(store.List("playerD", ShorepopCatalog.AppleStore, ShorepopCatalog.BundleId).Count == 0, "playerD has nothing");
    });
    await CaseAsync($"{label}: catalog consumable signed as Non-Consumable stays single-owner", async () =>
    {
        string mislabeled = chain.Sign(Tx("gg.shorepop.gems.550", "3200000001", "Non-Consumable"));
        await service.ValidateAsync("playerC", AppleProof("gg.shorepop.gems.550", "3200000001", mislabeled), default);
        await RejectsAsync(409, "transaction_owned_by_another_player", () => service.ValidateAsync("playerD", AppleProof("gg.shorepop.gems.550", "3200000001", mislabeled), default));
    });
    await CaseAsync($"{label}: consumable with its type field tampered to Non-Consumable fails the signature", async () =>
    {
        var p = chain.Sign(Tx("gg.shorepop.gems.1200", "3300000001")).Split('.');
        string forged = p[0] + "." + B64(JsonSerializer.SerializeToUtf8Bytes(Tx("gg.shorepop.gems.1200", "3300000001", "Non-Consumable"))) + "." + p[2];
        await RejectsAsync(422, "apple_signature_invalid", () => service.ValidateAsync("playerE", AppleProof("gg.shorepop.gems.1200", "3300000001", forged), default));
        Expect(store.Claimants(ShorepopCatalog.AppleStore, ShorepopCatalog.BundleId, "3300000001").Count == 0, "nothing bound");
    });
    await CaseAsync($"{label}: refund on a shared entitlement is sticky for every claimant", async () =>
    {
        var tx = Tx("gg.shorepop.noads", "3400000001", "Non-Consumable");
        string clean = chain.Sign(tx);
        await service.ValidateAsync("playerF", AppleProof("gg.shorepop.noads", "3400000001", clean), default);
        tx["revocationDate"] = nowMs; tx["signedDate"] = nowMs + 5;
        Expect((await service.ValidateAsync("playerG", AppleProof("gg.shorepop.noads", "3400000001", chain.Sign(tx)), default)).Refunded, "refund via second player");
        Expect((await service.ValidateAsync("playerF", AppleProof("gg.shorepop.noads", "3400000001", clean), default)).Refunded, "older clean JWS cannot un-refund");
        Expect((await service.ValidateAsync("playerH", AppleProof("gg.shorepop.noads", "3400000001", clean), default)).Refunded, "new claimant sees the refund");
    });
    await CaseAsync($"{label}: subscription restore by a second player; revision monotonic per player", async () =>
    {
        var t = Tx(ShorepopCatalog.SubscriptionProductId, "4100000001", "Auto-Renewable Subscription"); t["expiresDate"] = nowMs + 3 * 86400000L;
        string signed = chain.Sign(t);
        var a1 = await service.ValidateAsync("playerSA", AppleProof(ShorepopCatalog.SubscriptionProductId, "4100000001", signed), default);
        var b1 = await service.ValidateAsync("playerSB", AppleProof(ShorepopCatalog.SubscriptionProductId, "4100000001", signed), default);
        Expect(b1.CurrentSubscriptionExpiryUtcTicks == a1.CurrentSubscriptionExpiryUtcTicks && b1.CurrentSubscriptionExpiryUtcTicks > 0 && b1.SubscriptionEntitlementRevision >= 1, "restored pass");
        var b1b = await service.ValidateAsync("playerSB", AppleProof(ShorepopCatalog.SubscriptionProductId, "4100000001", signed), default);
        Expect(b1b.SubscriptionEntitlementRevision == b1.SubscriptionEntitlementRevision, "unchanged state keeps playerSB's revision");
        t["revocationDate"] = nowMs; t["signedDate"] = nowMs + 5;
        var b2 = await service.ValidateAsync("playerSB", AppleProof(ShorepopCatalog.SubscriptionProductId, "4100000001", chain.Sign(t)), default);
        Expect(b2.Refunded && b2.CurrentSubscriptionExpiryUtcTicks == 0 && b2.SubscriptionEntitlementRevision == b1.SubscriptionEntitlementRevision + 1, "refund bumps playerSB");
        var a2 = await service.ValidateAsync("playerSA", AppleProof(ShorepopCatalog.SubscriptionProductId, "4100000001", signed), default);
        Expect(a2.Refunded && a2.CurrentSubscriptionExpiryUtcTicks == 0 && a2.SubscriptionEntitlementRevision == a1.SubscriptionEntitlementRevision + 1, "refund bumps playerSA too");
    });
    await CaseAsync($"{label}: subscription revision is monotonic and tracks the aggregate expiry", async () =>
    {
        var t1 = Tx(ShorepopCatalog.SubscriptionProductId, "4000000001", "Auto-Renewable Subscription"); t1["expiresDate"] = nowMs + 86400000L;
        var r1 = await service.ValidateAsync("playerS", AppleProof(ShorepopCatalog.SubscriptionProductId, "4000000001", chain.Sign(t1)), default);
        Expect(r1.SubscriptionEntitlementRevision >= 1 && r1.CurrentSubscriptionExpiryUtcTicks == r1.PurchasedPeriodExpiryUtcTicks && r1.PurchasedPeriodExpiryUtcTicks > 0, "first");
        var r1b = await service.ValidateAsync("playerS", AppleProof(ShorepopCatalog.SubscriptionProductId, "4000000001", chain.Sign(t1)), default);
        Expect(r1b.SubscriptionEntitlementRevision == r1.SubscriptionEntitlementRevision, "unchanged state must keep the revision");
        var t2 = Tx(ShorepopCatalog.SubscriptionProductId, "4000000002", "Auto-Renewable Subscription");
        t2["originalTransactionId"] = "4000000001"; t2["expiresDate"] = nowMs + 2 * 86400000L;
        var r2 = await service.ValidateAsync("playerS", AppleProof(ShorepopCatalog.SubscriptionProductId, "4000000002", chain.Sign(t2)), default);
        Expect(r2.SubscriptionEntitlementRevision == r1.SubscriptionEntitlementRevision + 1 && r2.CurrentSubscriptionExpiryUtcTicks == r2.PurchasedPeriodExpiryUtcTicks, "renewal");
        t2["revocationDate"] = nowMs;
        var r3 = await service.ValidateAsync("playerS", AppleProof(ShorepopCatalog.SubscriptionProductId, "4000000002", chain.Sign(t2)), default);
        Expect(r3.Refunded && r3.SubscriptionEntitlementRevision == r2.SubscriptionEntitlementRevision + 1 && r3.CurrentSubscriptionExpiryUtcTicks == r1.PurchasedPeriodExpiryUtcTicks, "refund lowers aggregate");
    });
    await CaseAsync($"{label}: refund is sticky (older unrevoked JWS cannot un-refund)", async () =>
    {
        var tx = Tx("gg.shorepop.gems.260", "5000000001");
        string clean = chain.Sign(tx);
        tx["revocationDate"] = nowMs; tx["signedDate"] = nowMs + 5;
        Expect((await service.ValidateAsync("playerR", AppleProof("gg.shorepop.gems.260", "5000000001", chain.Sign(tx)), default)).Refunded, "refund");
        Expect((await service.ValidateAsync("playerR", AppleProof("gg.shorepop.gems.260", "5000000001", clean), default)).Refunded, "sticky");
    });
    await CaseAsync($"{label}: reconcile (no Apple key) returns only this player's rows", async () =>
    {
        var rows = await service.ReconcileAsync("playerA", new ReconcileRequest { Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.BundleId }, default);
        Expect(rows.Count == 2 && rows.Select(r => r.ProductId).SequenceEqual(["gg.shorepop.noads", "gg.shorepop.starter"]), "rows " + rows.Count);
        var none = await service.ReconcileAsync("playerZ", new ReconcileRequest { Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.BundleId }, default);
        Expect(none.Count == 0, "empty");
    });
    await CaseAsync($"{label}: wrong application id / unknown store / unknown product / missing JWS REJECTED", async () =>
    {
        var bad = AppleProof("gg.shorepop.noads", "1", jws); bad.ApplicationId = "com.other";
        await RejectsAsync(422, "application_mismatch", () => service.ValidateAsync("p", bad, default));
        bad = AppleProof("gg.shorepop.noads", "1", jws); bad.Store = "Amazon";
        await RejectsAsync(422, "unknown_store", () => service.ValidateAsync("p", bad, default));
        await RejectsAsync(422, "unknown_product", () => service.ValidateAsync("p", AppleProof("gg.other", "1", jws), default));
        await RejectsAsync(422, "apple_signed_transaction_required", () => service.ValidateAsync("p", AppleProof("gg.shorepop.noads", "1", ""), default));
    });
    await CaseAsync($"{label}: Google fails closed with 503 when GOOGLE_PLAY_SA_JSON is unset", async () =>
    {
        var proof = new NativePurchaseProof { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.BundleId, ProductId = "gg.shorepop.gems.80", TransactionId = "token" };
        await RejectsAsync(503, "google_validation_not_configured", () => service.ValidateAsync("p", proof, default));
        await RejectsAsync(503, "google_validation_not_configured", () => service.ReconcileAsync("p", new ReconcileRequest { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.BundleId }, default));
        Expect(!new GooglePlayApi(new HttpClient(), null).Configured && !new GooglePlayApi(new HttpClient(), "not json").Configured, "configured flag");
    });

    // ---- appAccountToken binding, ack, reclaim, claimant cap ----
    var clockNow = DateTimeOffset.UtcNow;
    var timed = Service(store, clock: () => clockNow);
    const string A = ShorepopCatalog.AppleStore, B = ShorepopCatalog.BundleId;
    Dictionary<string, object> TokTx(string product, string id, string? token, string type = "Consumable")
    {
        var t = Tx(product, id, type);
        if (token != null) t["appAccountToken"] = token;
        return t;
    }
    await CaseAsync($"{label}: consumable with token: squatter binds first, token-matched player reclaims before ack (transfer recorded)", async () =>
    {
        clockNow = DateTimeOffset.UtcNow;
        string signed = chain.Sign(TokTx("gg.shorepop.gems.80", "7000000001", AppAccountTokens.Derive("buyer1")));
        await timed.ValidateAsync("squatter1", AppleProof("gg.shorepop.gems.80", "7000000001", signed), default);
        await RejectsAsync(409, "transaction_owned_by_another_player", () => timed.ValidateAsync("stranger1", AppleProof("gg.shorepop.gems.80", "7000000001", signed), default));
        var proof = AppleProof("gg.shorepop.gems.80", "7000000001", signed); proof.AppAccountToken = AppAccountTokens.Derive("buyer1");
        var won = await timed.ValidateAsync("buyer1", proof, default);
        Expect(won.TransactionId == "7000000001", "result");
        Expect(store.Claimants(A, B, "7000000001").SequenceEqual(["buyer1"]), "owner is buyer1: " + string.Join(",", store.Claimants(A, B, "7000000001")));
        var moves = store.Transfers(A, B, "7000000001");
        Expect(moves.Count == 1 && moves[0].FromPlayer == "squatter1" && moves[0].ToPlayer == "buyer1" && moves[0].Reason == "app_account_token", "transfer log");
        Expect(store.List("squatter1", A, B).All(r => r.TransactionId != "7000000001"), "squatter lost the row");
        await RejectsAsync(409, "transaction_owned_by_another_player", () => timed.ValidateAsync("squatter1", AppleProof("gg.shorepop.gems.80", "7000000001", signed), default));
    });
    await CaseAsync($"{label}: consumable with token: 409 after ack even for the token-matched player", async () =>
    {
        clockNow = DateTimeOffset.UtcNow;
        string signed = chain.Sign(TokTx("gg.shorepop.gems.260", "7000000002", AppAccountTokens.Derive("buyer2")));
        await timed.ValidateAsync("squatter2", AppleProof("gg.shorepop.gems.260", "7000000002", signed), default);
        var ack = timed.Ack("squatter2", new AckRequest { TransactionId = "7000000002" });
        Expect(ack.Acked && ack.TransactionId == "7000000002", "ack result");
        Expect(store.Claims(A, B, "7000000002").Single().AckedUtc != null, "acked stamp");
        await RejectsAsync(409, "transaction_owned_by_another_player", () => timed.ValidateAsync("buyer2", AppleProof("gg.shorepop.gems.260", "7000000002", signed), default));
        clockNow = clockNow.AddDays(30);
        await RejectsAsync(409, "transaction_owned_by_another_player", () => timed.ValidateAsync("buyer2", AppleProof("gg.shorepop.gems.260", "7000000002", signed), default));
        Expect((await timed.ValidateAsync("squatter2", AppleProof("gg.shorepop.gems.260", "7000000002", signed), default)).TransactionId == "7000000002", "owner retry still 200");
        Expect(store.Transfers(A, B, "7000000002").Count == 0, "no transfer");
    });
    await CaseAsync($"{label}: consumable with token (killed before commit, reinstalled as a new player): mismatched player 409 inside 24 h, takeover after (unacked_24h_mismatch), 409 after ack", async () =>
    {
        var t0 = DateTimeOffset.UtcNow; clockNow = t0;
        string signed = chain.Sign(TokTx("gg.shorepop.coins.600", "7000000003", AppAccountTokens.Derive("buyer3")));
        await timed.ValidateAsync("buyer3", AppleProof("gg.shorepop.coins.600", "7000000003", signed), default);
        clockNow = t0.AddHours(23);
        var forged = AppleProof("gg.shorepop.coins.600", "7000000003", signed); forged.AppAccountToken = AppAccountTokens.Derive("buyer3");
        await RejectsAsync(409, "transaction_owned_by_another_player", () => timed.ValidateAsync("reinstall3", forged, default));
        Expect(store.Claimants(A, B, "7000000003").SequenceEqual(["buyer3"]), "request field alone never binds");
        clockNow = t0.AddHours(24).AddMinutes(1);
        Expect((await timed.ValidateAsync("reinstall3", AppleProof("gg.shorepop.coins.600", "7000000003", signed), default)).TransactionId == "7000000003", "takeover");
        var moves = store.Transfers(A, B, "7000000003");
        Expect(moves.Count == 1 && moves[0].FromPlayer == "buyer3" && moves[0].ToPlayer == "reinstall3" && moves[0].Reason == "unacked_24h_mismatch", "transfer log");
        Expect(store.List("buyer3", A, B).All(r => r.TransactionId != "7000000003"), "old owner lost the row");
        clockNow = t0.AddHours(30);
        await RejectsAsync(409, "transaction_owned_by_another_player", () => timed.ValidateAsync("third3", AppleProof("gg.shorepop.coins.600", "7000000003", signed), default));
        timed.Ack("reinstall3", new AckRequest { TransactionId = "7000000003" });
        clockNow = t0.AddDays(20);
        await RejectsAsync(409, "transaction_owned_by_another_player", () => timed.ValidateAsync("third3", AppleProof("gg.shorepop.coins.600", "7000000003", signed), default));
        await RejectsAsync(409, "transaction_owned_by_another_player", () => timed.ValidateAsync("buyer3", AppleProof("gg.shorepop.coins.600", "7000000003", signed), default));
    });
    await CaseAsync($"{label}: consumable without token (killed before commit, reinstalled): 409 inside 24 h, reclaim after, 409 after the new owner acks", async () =>
    {
        var t0 = DateTimeOffset.UtcNow; clockNow = t0;
        string signed = chain.Sign(TokTx("gg.shorepop.gems.550", "7000000004", null));
        await timed.ValidateAsync("oldAnon", AppleProof("gg.shorepop.gems.550", "7000000004", signed), default);
        clockNow = t0.AddHours(23);
        await RejectsAsync(409, "transaction_owned_by_another_player", () => timed.ValidateAsync("newAnon", AppleProof("gg.shorepop.gems.550", "7000000004", signed), default));
        clockNow = t0.AddHours(24).AddMinutes(1);
        Expect((await timed.ValidateAsync("newAnon", AppleProof("gg.shorepop.gems.550", "7000000004", signed), default)).ProductId == "gg.shorepop.gems.550", "reclaimed");
        var moves = store.Transfers(A, B, "7000000004");
        Expect(moves.Count == 1 && moves[0].FromPlayer == "oldAnon" && moves[0].ToPlayer == "newAnon" && moves[0].Reason == "unacked_24h", "transfer log");
        await RejectsAsync(409, "transaction_owned_by_another_player", () => Task.FromResult(timed.Ack("oldAnon", new AckRequest { TransactionId = "7000000004" })));
        timed.Ack("newAnon", new AckRequest { TransactionId = "7000000004", Store = A, ApplicationId = B });
        clockNow = t0.AddDays(10);
        await RejectsAsync(409, "transaction_owned_by_another_player", () => timed.ValidateAsync("thirdAnon", AppleProof("gg.shorepop.gems.550", "7000000004", signed), default));
        await RejectsAsync(409, "transaction_owned_by_another_player", () => timed.ValidateAsync("oldAnon", AppleProof("gg.shorepop.gems.550", "7000000004", signed), default));
    });
    await CaseAsync($"{label}: ack: unknown transaction 404, wrong app 422, idempotent", async () =>
    {
        await RejectsAsync(404, "transaction_not_found", () => Task.FromResult(timed.Ack("p", new AckRequest { TransactionId = "7999999999" })));
        await RejectsAsync(422, "transaction_missing", () => Task.FromResult(timed.Ack("p", new AckRequest { TransactionId = "" })));
        await RejectsAsync(422, "application_mismatch", () => Task.FromResult(timed.Ack("p", new AckRequest { TransactionId = "7000000004", ApplicationId = "com.other" })));
        var first = store.Claims(A, B, "7000000002").Single().AckedUtc;
        timed.Ack("squatter2", new AckRequest { TransactionId = "7000000002" });
        Expect(store.Claims(A, B, "7000000002").Single().AckedUtc == first, "first ack time kept");
    });
    await CaseAsync($"{label}: entitlement with token: mismatched player 409 on a replayed JWS, restore (fresh re-signed JWS) 200, replay of that restore 409", async () =>
    {
        clockNow = DateTimeOffset.UtcNow;
        long signedAt = clockNow.AddHours(-2).ToUnixTimeMilliseconds();
        var tx = TokTx("gg.shorepop.noads", "7100000001", AppAccountTokens.Derive("owner6"), "Non-Consumable"); tx["signedDate"] = signedAt;
        string original = chain.Sign(tx);
        await timed.ValidateAsync("owner6", AppleProof("gg.shorepop.noads", "7100000001", original), default);
        await RejectsAsync(409, "app_account_token_mismatch", () => timed.ValidateAsync("other6", AppleProof("gg.shorepop.noads", "7100000001", original), default));
        tx["signedDate"] = clockNow.AddMinutes(-1).ToUnixTimeMilliseconds();
        string restored = chain.Sign(tx);
        Expect((await timed.ValidateAsync("reinstall6", AppleProof("gg.shorepop.noads", "7100000001", restored), default)).ProductId == "gg.shorepop.noads", "restore granted");
        await RejectsAsync(409, "app_account_token_mismatch", () => timed.ValidateAsync("other6", AppleProof("gg.shorepop.noads", "7100000001", restored), default));
        Expect((await timed.ValidateAsync("reinstall6", AppleProof("gg.shorepop.noads", "7100000001", restored), default)).ProductId == "gg.shorepop.noads", "restorer retry");
        var stale = TokTx("gg.shorepop.starter", "7100000002", AppAccountTokens.Derive("owner6"), "Non-Consumable"); stale["signedDate"] = clockNow.AddHours(-25).ToUnixTimeMilliseconds();
        await RejectsAsync(409, "app_account_token_mismatch", () => timed.ValidateAsync("other6", AppleProof("gg.shorepop.starter", "7100000002", chain.Sign(stale)), default));
        Expect(store.Claimants(A, B, "7100000001").SequenceEqual(["owner6", "reinstall6"]), "claimants " + string.Join(",", store.Claimants(A, B, "7100000001")));
    });
    await CaseAsync($"{label}: entitlement cap: 5 distinct claimants per original transaction, 6th 409, existing claimants and the token owner unaffected", async () =>
    {
        clockNow = DateTimeOffset.UtcNow;
        string open = chain.Sign(TokTx("gg.shorepop.noads", "7200000001", null, "Non-Consumable"));
        for (int i = 1; i <= 5; i++) await timed.ValidateAsync("cap" + i, AppleProof("gg.shorepop.noads", "7200000001", open), default);
        await RejectsAsync(409, "claimant_limit_reached", () => timed.ValidateAsync("cap6", AppleProof("gg.shorepop.noads", "7200000001", open), default));
        Expect((await timed.ValidateAsync("cap3", AppleProof("gg.shorepop.noads", "7200000001", open), default)).TransactionId == "7200000001", "existing claimant ok");
        Expect(store.Claimants(A, B, "7200000001").Count == 5, "five");

        var sub = TokTx(ShorepopCatalog.SubscriptionProductId, "7300000001", AppAccountTokens.Derive("subOwner"), "Auto-Renewable Subscription");
        sub["expiresDate"] = nowMs + 30 * 86400000L;
        long baseMs = clockNow.AddHours(-3).ToUnixTimeMilliseconds();
        for (int i = 1; i <= 5; i++)
        {
            sub["signedDate"] = baseMs + i * 1000;
            await timed.ValidateAsync("subCap" + i, AppleProof(ShorepopCatalog.SubscriptionProductId, "7300000001", chain.Sign(sub)), default);
        }
        var renewal = TokTx(ShorepopCatalog.SubscriptionProductId, "7300000002", AppAccountTokens.Derive("subOwner"), "Auto-Renewable Subscription");
        renewal["originalTransactionId"] = "7300000001"; renewal["expiresDate"] = nowMs + 60 * 86400000L; renewal["signedDate"] = baseMs + 10000;
        await RejectsAsync(409, "claimant_limit_reached", () => timed.ValidateAsync("subCap6", AppleProof(ShorepopCatalog.SubscriptionProductId, "7300000002", chain.Sign(renewal)), default));
        Expect((await timed.ValidateAsync("subOwner", AppleProof(ShorepopCatalog.SubscriptionProductId, "7300000002", chain.Sign(renewal)), default)).TransactionId == "7300000002", "token owner never capped");
        Expect((await timed.ValidateAsync("subCap2", AppleProof(ShorepopCatalog.SubscriptionProductId, "7300000002", chain.Sign(renewal)), default)).TransactionId == "7300000002", "group claimant takes the renewal");
    });
    await CaseAsync($"{label}: entitlement without token keeps multi-claim (restore by a new player, no freshness needed)", async () =>
    {
        clockNow = DateTimeOffset.UtcNow;
        var tx = TokTx("gg.shorepop.starter", "7400000001", null, "Non-Consumable"); tx["signedDate"] = clockNow.AddDays(-3).ToUnixTimeMilliseconds();
        string old = chain.Sign(tx);
        await timed.ValidateAsync("nt1", AppleProof("gg.shorepop.starter", "7400000001", old), default);
        Expect((await timed.ValidateAsync("nt2", AppleProof("gg.shorepop.starter", "7400000001", old), default)).ProductId == "gg.shorepop.starter", "second player");
    });
}
Case("sqlite: claims survive a new store instance on the same file", () =>
{
    var reopened = new SqlitePurchaseStore(dbPath);
    Expect(reopened.List("playerA", ShorepopCatalog.AppleStore, ShorepopCatalog.BundleId).Count == 2, "persisted");
    Expect(reopened.List("playerB", ShorepopCatalog.AppleStore, ShorepopCatalog.BundleId).Count == 2, "restore claims persisted");
    var rec = new PurchaseRecord(ShorepopCatalog.AppleStore, ShorepopCatalog.BundleId, "3000000001", "playerX", "gg.shorepop.noads", "Sandbox", false, 0, null, null, 0);
    Expect(reopened.Bind(rec, singleOwner: true) == "playerA", "single-owner bind reports the first claimant");
    Expect(reopened.Bind(rec, singleOwner: false) == "playerX", "entitlement bind adds a claim");
    Expect(reopened.Claimants(ShorepopCatalog.AppleStore, ShorepopCatalog.BundleId, "3000000001").SequenceEqual(["playerA", "playerB", "playerX"]), "claim order");
});

await CaseAsync("google (fake API): product states, quantity, test purchases, subscription expiry", async () =>
{
    var google = new FakeGoogle(true);
    var service = Service(new InMemoryPurchaseStore(), google);
    NativePurchaseProof G(string product, string token) => new() { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.BundleId, ProductId = product, TransactionId = token };
    google.Products["tok-ok"] = """{"productLineItem":[{"productId":"gg.shorepop.gems.80","productOfferDetails":{"quantity":1}}],"purchaseStateContext":{"purchaseState":"PURCHASED"}}""";
    var ok = await service.ValidateAsync("g1", G("gg.shorepop.gems.80", "tok-ok"), default);
    Expect(!ok.Refunded && ok.Environment == "Production" && ok.TransactionId == "tok-ok", "purchased");
    google.Products["tok-test"] = """{"productLineItem":[{"productId":"gg.shorepop.gems.80"}],"purchaseStateContext":{"purchaseState":"PURCHASED"},"testPurchaseContext":{"fopType":"TEST"}}""";
    Expect((await service.ValidateAsync("g1", G("gg.shorepop.gems.80", "tok-test"), default)).Environment == "LicenseTest", "license test");
    google.Products["tok-void"] = """{"productLineItem":[{"productId":"gg.shorepop.gems.80"}],"purchaseStateContext":{"purchaseState":"CANCELLED"}}""";
    Expect((await service.ValidateAsync("g1", G("gg.shorepop.gems.80", "tok-void"), default)).Refunded, "voided");
    google.Products["tok-pending"] = """{"productLineItem":[{"productId":"gg.shorepop.gems.80"}],"purchaseStateContext":{"purchaseState":"PENDING"}}""";
    await RejectsAsync(409, "purchase_pending", () => service.ValidateAsync("g1", G("gg.shorepop.gems.80", "tok-pending"), default));
    google.Products["tok-q2"] = """{"productLineItem":[{"productId":"gg.shorepop.gems.80","productOfferDetails":{"quantity":2}}],"purchaseStateContext":{"purchaseState":"PURCHASED"}}""";
    await RejectsAsync(422, "quantity_not_one", () => service.ValidateAsync("g1", G("gg.shorepop.gems.80", "tok-q2"), default));
    await RejectsAsync(422, "product_mismatch", () => service.ValidateAsync("g1", G("gg.shorepop.gems.2800", "tok-ok"), default));
    await RejectsAsync(422, "purchase_not_found", () => service.ValidateAsync("g1", G("gg.shorepop.gems.80", "tok-unknown"), default));
    await RejectsAsync(409, "transaction_owned_by_another_player", () => service.ValidateAsync("g2", G("gg.shorepop.gems.80", "tok-ok"), default));
    google.Products["tok-noads"] = """{"productLineItem":[{"productId":"gg.shorepop.noads"}],"purchaseStateContext":{"purchaseState":"PURCHASED"}}""";
    await service.ValidateAsync("g1", G("gg.shorepop.noads", "tok-noads"), default);
    Expect((await service.ValidateAsync("g2", G("gg.shorepop.noads", "tok-noads"), default)).ProductId == "gg.shorepop.noads", "google non-consumable restore (catalog type)");
    google.Subscriptions["tok-sub"] = """{"subscriptionState":"SUBSCRIPTION_STATE_ACTIVE","lineItems":[{"productId":"gg.shorepop.pass.monthly","expiryTime":"2030-01-02T03:04:05.678Z"}]}""";
    var sub = await service.ValidateAsync("g1", G(ShorepopCatalog.SubscriptionProductId, "tok-sub"), default);
    Expect(sub.PurchasedPeriodExpiryUtcTicks == new DateTime(2030, 1, 2, 3, 4, 5, 678, DateTimeKind.Utc).Ticks && sub.SubscriptionEntitlementRevision == 1, "sub expiry");
    google.Products["tok-ok"] = """{"productLineItem":[{"productId":"gg.shorepop.gems.80"}],"purchaseStateContext":{"purchaseState":"CANCELLED"}}""";
    var rows = await service.ReconcileAsync("g1", new ReconcileRequest { Store = ShorepopCatalog.GoogleStore, ApplicationId = ShorepopCatalog.BundleId }, default);
    Expect(rows.Count == 5 && rows.Single(r => r.TransactionId == "tok-ok").Refunded, "reconcile sees the later refund");
});

await CaseAsync("apple reconcile (fake App Store Server API): later refund and renewal are picked up", async () =>
{
    var api = new FakeApple { IsConfigured = true };
    var store = new InMemoryPurchaseStore();
    var service = Service(store, appleApi: api);
    var t1 = Tx(ShorepopCatalog.SubscriptionProductId, "6000000001", "Auto-Renewable Subscription"); t1["expiresDate"] = nowMs + 86400000L;
    var first = await service.ValidateAsync("pa", AppleProof(ShorepopCatalog.SubscriptionProductId, "6000000001", chain.Sign(t1)), default);
    var coins = Tx("gg.shorepop.coins.600", "6000000009");
    await service.ValidateAsync("pa", AppleProof("gg.shorepop.coins.600", "6000000009", chain.Sign(coins)), default);
    coins["revocationDate"] = nowMs; coins["signedDate"] = nowMs + 10;
    api.Transactions["6000000009"] = chain.Sign(coins);
    api.Transactions["6000000001"] = chain.Sign(t1);
    var t2 = Tx(ShorepopCatalog.SubscriptionProductId, "6000000002", "Auto-Renewable Subscription");
    t2["originalTransactionId"] = "6000000001"; t2["expiresDate"] = nowMs + 31 * 86400000L;
    var foreign = Tx(ShorepopCatalog.SubscriptionProductId, "6000000003", "Auto-Renewable Subscription");
    foreign["originalTransactionId"] = "9999"; foreign["expiresDate"] = nowMs + 90 * 86400000L;
    api.LastTransactions["6000000001"] = [chain.Sign(t2), chain.Sign(foreign)];
    var rows = await service.ReconcileAsync("pa", new ReconcileRequest { Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.BundleId }, default);
    Expect(rows.Single(r => r.TransactionId == "6000000009").Refunded, "refund picked up");
    Expect(rows.Count == 3 && rows.Any(r => r.TransactionId == "6000000002"), "renewal bound, foreign original ignored");
    var pass = rows.First(r => r.ProductId == ShorepopCatalog.SubscriptionProductId);
    Expect(pass.CurrentSubscriptionExpiryUtcTicks == DateTimeOffset.FromUnixTimeMilliseconds(nowMs + 31 * 86400000L).UtcTicks &&
        pass.SubscriptionEntitlementRevision > first.SubscriptionEntitlementRevision, "aggregate moved to the renewal");
    api.Transactions["6000000009"] = new AppleJwsVerifierTamper().Tamper(chain.Sign(coins));
    await RejectsAsync(503, "apple_server_response_unverifiable", () => service.ReconcileAsync("pa", new ReconcileRequest { Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.BundleId }, default));
});

Case("catalog kinds mirror IapCatalog; Apple type never loosens a consumable", () =>
{
    var consumables = ShorepopCatalog.ProductIds.Where(id => ShorepopCatalog.CatalogKind(id) == ProductKind.Consumable).OrderBy(x => x, StringComparer.Ordinal).ToArray();
    Expect(consumables.Length == 11 && consumables.All(id => id.Contains(".gems.") || id.Contains(".coins.") || id is "gg.shorepop.lives.refill"
        or "gg.shorepop.abilities.bundle" or "gg.shorepop.welcome" or "gg.shorepop.shellbank"), "consumable set " + string.Join(",", consumables));
    Expect(ShorepopCatalog.CatalogKind("gg.shorepop.noads") == ProductKind.NonConsumable && ShorepopCatalog.CatalogKind("gg.shorepop.starter") == ProductKind.NonConsumable, "non-consumables");
    Expect(ShorepopCatalog.CatalogKind(ShorepopCatalog.SubscriptionProductId) == ProductKind.Subscription, "subscription");
    Expect(ShorepopCatalog.AppleKind("Non-Consumable", "gg.shorepop.gems.80") == ProductKind.Consumable, "mislabeled consumable");
    Expect(ShorepopCatalog.AppleKind("Consumable", "gg.shorepop.noads") == ProductKind.Consumable, "Apple's stricter type wins");
    Expect(ShorepopCatalog.AppleKind("Non-Consumable", "gg.shorepop.noads") == ProductKind.NonConsumable, "noads");
});

Case("appAccountToken: UUIDv5 matches RFC 4122 / Python uuid5 vectors; derivation and normalization", () =>
{
    Expect(AppAccountTokens.Uuid5("6ba7b810-9dad-11d1-80b4-00c04fd430c8", "python.org") == "886313e1-3b8a-5372-9b90-0c9aee199e5d", "uuid5(DNS, python.org)");
    Expect(AppAccountTokens.Derive("abc123") == "0f752aa9-33c5-54b1-b936-68d3b28f2043", "derive(abc123) " + AppAccountTokens.Derive("abc123"));
    Expect(AppAccountTokens.Derive("abc123") != AppAccountTokens.Derive("abc124"), "distinct players");
    Expect(AppAccountTokens.Normalize("0F752AA9-33C5-54B1-B936-68D3B28F2043") == "0f752aa9-33c5-54b1-b936-68d3b28f2043", "normalize upper");
    Expect(AppAccountTokens.Normalize("") == null && AppAccountTokens.Normalize("not-a-uuid") == null, "normalize junk");
    var t = Tx("gg.shorepop.gems.80", "7500000001"); t["appAccountToken"] = "0F752AA9-33C5-54B1-B936-68D3B28F2043";
    Expect(checker.Check(chain.Sign(t), "gg.shorepop.gems.80", "7500000001", DateTimeOffset.UtcNow).AppAccountToken == "0f752aa9-33c5-54b1-b936-68d3b28f2043", "JWS token read + normalized");
    t["appAccountToken"] = "junk";
    Expect(checker.Check(chain.Sign(t), "gg.shorepop.gems.80", "7500000001", DateTimeOffset.UtcNow).AppAccountToken == "invalid:junk", "non-UUID token never matches");
    Expect(checker.Check(chain.Sign(Tx("gg.shorepop.gems.80", "7500000001")), "gg.shorepop.gems.80", "7500000001", DateTimeOffset.UtcNow).AppAccountToken == null, "absent token");
});
Case("ugs claims: project id, single subject, nbf required", () =>
{
    const string project = "26418e78-9eb7-42c5-955c-b4c141443252";
    ClaimsPrincipal P(params (string, string)[] claims) => new(new ClaimsIdentity(claims.Select(c => new Claim(c.Item1, c.Item2)), "test"));
    Expect(UgsAuthentication.ValidClaims(P(("sub", "abc123"), ("project_id", project), ("nbf", "1")), project), "valid");
    Expect(!UgsAuthentication.ValidClaims(P(("sub", "abc123"), ("project_id", Guid.NewGuid().ToString()), ("nbf", "1")), project), "wrong project");
    Expect(!UgsAuthentication.ValidClaims(P(("sub", "a"), ("sub", "b"), ("project_id", project), ("nbf", "1")), project), "two subjects");
    Expect(!UgsAuthentication.ValidClaims(P(("sub", "abc/../x"), ("project_id", project), ("nbf", "1")), project), "bad subject");
    Expect(!UgsAuthentication.ValidClaims(P(("sub", "abc123"), ("project_id", project)), project), "missing nbf");
});

try { File.Delete(dbPath); File.Delete(dbPath + "-wal"); File.Delete(dbPath + "-shm"); } catch (IOException) { }
Console.WriteLine($"RESULT passed={passed} failed={failed}");
return failed == 0 ? 0 : 1;

static string B64(byte[] data) => Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

sealed class AppleJwsVerifierTamper
{
    public string Tamper(string jws)
    {
        var p = jws.Split('.');
        var bytes = AppleJwsVerifier.Base64Url(p[1]); bytes[^2] ^= 1;
        return p[0] + "." + Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_') + "." + p[2];
    }
}

sealed class TestChain
{
    public required byte[] RootDer, IntermediateDer, LeafDer;
    public required ECDsa LeafKey;

    public static TestChain Create(bool leafOid = true, bool intermediateCa = true)
    {
        var notBefore = DateTimeOffset.UtcNow.AddDays(-1); var notAfter = DateTimeOffset.UtcNow.AddYears(2);
        var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var rootReq = new CertificateRequest("CN=Test Root, O=Shorepop Tests", rootKey, HashAlgorithmName.SHA384);
        rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var root = rootReq.CreateSelfSigned(notBefore, notAfter);
        var interKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var interReq = new CertificateRequest("CN=Test WWDR, O=Shorepop Tests", interKey, HashAlgorithmName.SHA384);
        interReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(intermediateCa, true, 0, true));
        interReq.CertificateExtensions.Add(new X509Extension(new Oid(AppleJwsVerifier.IntermediateOid), [0x05, 0x00], false));
        using var inter = interReq.Create(root.SubjectName, X509SignatureGenerator.CreateForECDsa(rootKey), notBefore, notAfter, [1]);
        var leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafReq = new CertificateRequest("CN=Test App Store Signing, O=Shorepop Tests", leafKey, HashAlgorithmName.SHA256);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        if (leafOid) leafReq.CertificateExtensions.Add(new X509Extension(new Oid(AppleJwsVerifier.LeafOid), [0x05, 0x00], false));
        using var leaf = leafReq.Create(inter.SubjectName, X509SignatureGenerator.CreateForECDsa(interKey), notBefore, notAfter, [2]);
        return new TestChain { RootDer = root.RawData, IntermediateDer = inter.RawData, LeafDer = leaf.RawData, LeafKey = leafKey };
    }

    public string Sign(object payload, ECDsa? signer = null, byte[]? rootOverride = null)
    {
        var header = new Dictionary<string, object> { ["alg"] = "ES256", ["x5c"] = new[] {
            Convert.ToBase64String(LeafDer), Convert.ToBase64String(IntermediateDer), Convert.ToBase64String(rootOverride ?? RootDer) } };
        string input = E(JsonSerializer.SerializeToUtf8Bytes(header)) + "." + E(JsonSerializer.SerializeToUtf8Bytes(payload));
        byte[] sig = (signer ?? LeafKey).SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return input + "." + E(sig);
    }
    static string E(byte[] d) => Convert.ToBase64String(d).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

sealed class FakeGoogle(bool configured) : IGooglePlayApi
{
    public Dictionary<string, string> Products { get; } = new();
    public Dictionary<string, string> Subscriptions { get; } = new();
    public bool Configured => configured;
    public Task<JsonElement?> GetProductPurchaseAsync(string packageName, string token, CancellationToken cancel) => Get(Products, packageName, token);
    public Task<JsonElement?> GetSubscriptionPurchaseAsync(string packageName, string token, CancellationToken cancel) => Get(Subscriptions, packageName, token);
    static Task<JsonElement?> Get(Dictionary<string, string> map, string packageName, string token)
    {
        if (packageName != ShorepopCatalog.BundleId) throw new Exception("package");
        return Task.FromResult(map.TryGetValue(token, out var json) ? JsonDocument.Parse(json).RootElement.Clone() : (JsonElement?)null);
    }
}

sealed class FakeApple : IAppleServerApi
{
    public bool IsConfigured { get; set; }
    public Dictionary<string, string> Transactions { get; } = new();
    public Dictionary<string, List<string>> LastTransactions { get; } = new();
    public bool Configured => IsConfigured;
    public Task<string?> GetTransactionInfoAsync(string transactionId, string environment, CancellationToken cancel) =>
        Task.FromResult(Transactions.TryGetValue(transactionId, out var s) ? s : null);
    public Task<IReadOnlyList<string>> GetSubscriptionLastTransactionsAsync(string transactionId, string environment, CancellationToken cancel) =>
        Task.FromResult<IReadOnlyList<string>>(LastTransactions.TryGetValue(transactionId, out var l) ? l : []);
}
