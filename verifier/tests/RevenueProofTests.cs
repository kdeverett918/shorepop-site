using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Shorepop.Verifier;

internal static class RevenueProofTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 13, 0, 0, TimeSpan.Zero);
    private const string Store = ShorepopCatalog.GoogleStore;
    private const string App = ShorepopCatalog.BundleId;
    private const string Product = "gg.shorepop.gems.80";
    private static string Configuration => Environment.GetEnvironmentVariable("SHOREPOP_TEST_POSTGRES")!;
    private static void Expect(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    public static void Run(Action<string, Action> test)
    {
        test("revenue proof: ledger key matches the native adapter and a short encryption key is rejected", () =>
        {
            string expected = "tx-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Store + "\n" + App + "\n" + "test-native-token"))).ToLowerInvariant();
            Expect(RevenueProofStore.LedgerKey(Store, App, "test-native-token") == expected, "native ledger derivation");
            try { _ = new RevenueProofStore("", new byte[16]); }
            catch (ArgumentException) { return; }
            throw new Exception("short encryption key accepted");
        });
        if (string.IsNullOrEmpty(Configuration))
        {
            Console.WriteLine("SKIP revenue proof PostgreSQL integration tests: SHOREPOP_TEST_POSTGRES is unset.");
            return;
        }
        // Expiry tests intentionally move the clock and exercise the global ciphertext purge.
        // Never permit them to run against a production database, even with accidental env wiring.
        var configuration = new NpgsqlConnectionStringBuilder(PostgresPurchaseStore.NormalizeConnectionString(Configuration));
        if (configuration.Database?.EndsWith("_test", StringComparison.Ordinal) != true)
            throw new InvalidOperationException("Revenue proof integration tests require a dedicated _test database.");
        string run = "proof-test-" + Guid.NewGuid().ToString("N");
        string erasurePrefix = "proofErase" + Guid.NewGuid().ToString("N");
        byte[] key = RandomNumberGenerator.GetBytes(32);
        var canonical = new PostgresPurchaseStore(Configuration);
        var proofs = new RevenueProofStore(Configuration, key);
        string Player(string label) => run + "-" + label;
        string ErasurePlayer(string label) => erasurePrefix + label;
        PurchaseRecord Purchase(string label, string player = "owner", string product = Product) =>
            new(Store, App, run + "-" + label, Player(player), product, "Sandbox", false, 0, null, null, 100,
                true, 2990000, "USD", "GPA." + run + "-" + label, Now.ToUnixTimeMilliseconds());
        void Bind(PurchaseRecord row) => canonical.Bind(row, Now, _ => ClaimDecision.Claimed);
        string Receipt(PurchaseRecord row, string contents) => JsonSerializer.Serialize(new { orderId = row.RevenueTransactionId, payload = contents });
        void Save(PurchaseRecord row, string player, string receipt, string signature, DateTimeOffset at) =>
            proofs.Save(row, player, Receipt(row, receipt), signature, at);
        string? Contents(RevenueProof? proof)
        {
            if (proof == null) return null;
            using var parsed = JsonDocument.Parse(proof.Receipt);
            return parsed.RootElement.GetProperty("payload").GetString();
        }
        RevenueProof? Get(PurchaseRecord row, string? player = null, DateTimeOffset? at = null) =>
            proofs.Get(player ?? row.PlayerId, Store, App, RevenueProofStore.LedgerKey(Store, App, row.TransactionId), row.ProductId, at ?? Now);
        void Refusal(Action action)
        {
            try { action(); }
            catch (VerifierRefusal refused) { Expect(refused.Code == "revenue_proof_unavailable", "safe refusal code"); return; }
            throw new Exception("unauthorized or unreadable proof accepted");
        }

        try
        {
            test("revenue proof postgres: Michael Apple identity recovers without exposing former bundle or other claimant", () =>
            {
                var row = Purchase("apple-split") with { Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.AppleBundleId,
                    RevenueTransactionId = run + "-apple-split" };
                Bind(row);
                proofs.Save(row, row.PlayerId, "verified-apple-jws-fixture", "", Now);
                string ledger = RevenueProofStore.LedgerKey(row.Store, row.ApplicationId, row.TransactionId);
                Expect(proofs.Get(row.PlayerId, row.Store, row.ApplicationId, ledger, Product, Now)?.Receipt == "verified-apple-jws-fixture", "Apple proof did not recover");
                Expect(proofs.Get(Player("other"), row.Store, row.ApplicationId, ledger, Product, Now) == null, "other player recovered Apple proof");
                Expect(proofs.Get(row.PlayerId, row.Store, App, ledger, Product, Now) == null, "former Apple bundle recovered proof");
                Expect(proofs.Get(row.PlayerId, Store, row.ApplicationId, ledger, Product, Now) == null, "cross-store identity recovered proof");
                var old = row with { ApplicationId = App }; Bind(old);
                Refusal(() => proofs.Save(old, old.PlayerId, "legacy-jws", "", Now));
                Expect(canonical.List(old.PlayerId, old.Store, old.ApplicationId).Any(r => r.TransactionId == old.TransactionId), "legacy row was deleted");
            });
            test("revenue proof postgres: encrypted receipt/signature survives restart without plaintext columns", () =>
            {
                var row = Purchase("roundtrip"); Bind(row);
                Save(row, row.PlayerId, "private-receipt-contents", "private-signature-contents", Now);
                var restarted = new RevenueProofStore(Configuration, key);
                var result = restarted.Get(row.PlayerId, Store, App, RevenueProofStore.LedgerKey(Store, App, row.TransactionId), row.ProductId, Now.AddDays(1));
                Expect(Contents(result) == "private-receipt-contents" && result is { Signature: "private-signature-contents" } && result.ExpiresUtc == Now.AddDays(30), "proof restart/expiry");
                using var connection = Open();
                using var command = new NpgsqlCommand("SELECT ciphertext,nonce,tag FROM shorepop_verifier.revenue_proofs WHERE player_id=@p AND ledger_key=@l", connection);
                command.Parameters.AddWithValue("p", row.PlayerId); command.Parameters.AddWithValue("l", result!.LedgerKey);
                using var reader = command.ExecuteReader(); Expect(reader.Read(), "missing encrypted row");
                string bytes = Encoding.UTF8.GetString(reader.GetFieldValue<byte[]>(0));
                Expect(!bytes.Contains("private-receipt-contents") && !bytes.Contains("private-signature-contents"), "proof persisted as plaintext");
                Expect(reader.GetFieldValue<byte[]>(1).Length == 12 && reader.GetFieldValue<byte[]>(2).Length == 16, "AEAD sizes");
            });
            test("revenue proof postgres: other player, app, product, unsupported store and invented ledger cannot retrieve proof", () =>
            {
                var row = Purchase("scope"); Bind(row); Save(row, row.PlayerId, "scope-receipt", "scope-signature", Now);
                string ledger = RevenueProofStore.LedgerKey(Store, App, row.TransactionId);
                Expect(Get(row, Player("other")) == null, "other player");
                Expect(proofs.Get(row.PlayerId, Store, "another.app", ledger, Product, Now) == null, "other app");
                Expect(proofs.Get(row.PlayerId, Store, App, ledger, "gg.shorepop.gems.260", Now) == null, "other product");
                Expect(proofs.Get(row.PlayerId, "OtherStore", App, ledger, Product, Now) == null, "unknown store");
                Expect(proofs.Get(row.PlayerId, Store, App, "tx-" + new string('0', 64), Product, Now) == null, "unknown ledger");
                Refusal(() => Save(row, Player("other"), "receipt", "signature", Now));
                Refusal(() => Save(row with { ProductId = "gg.shorepop.gems.260" }, row.PlayerId, "receipt", "signature", Now));
            });
            test("revenue proof postgres: shared entitlement remains bound to original saving claimant", () =>
            {
                var row = Purchase("shared", product: "gg.shorepop.noads"); Bind(row);
                Bind(row with { PlayerId = Player("shared-second") });
                Save(row, row.PlayerId, "shared-receipt", "signature", Now);
                Expect(Get(row) != null && Get(row, Player("shared-second")) == null, "shared claimant could read proof");
                Refusal(() => Save(row with { PlayerId = Player("shared-second") }, Player("shared-second"), "receipt", "signature", Now));
            });
            test("revenue proof postgres: refund and transfer immediately remove recovery access", () =>
            {
                var refunded = Purchase("refunded"); Bind(refunded);
                Save(refunded, refunded.PlayerId, "refund-receipt", "signature", Now);
                canonical.Bind(refunded with { Refunded = true }, Now, _ => ClaimDecision.Claimed);
                Expect(Get(refunded) == null, "refunded proof readable");
                Refusal(() => Save(refunded, refunded.PlayerId, "receipt", "signature", Now));
                var moneyRefund = Purchase("money-refund") with
                {
                    RevenueVerified = true, GrossRevenueMicros = 2990000, RevenueCurrency = "USD",
                    RevenueTransactionId = "GPA.test-money-refund", RevenuePurchasedAtUnixMs = Now.ToUnixTimeMilliseconds(),
                };
                Bind(moneyRefund); Save(moneyRefund, moneyRefund.PlayerId, "money-refund-receipt", "signature", Now);
                canonical.Bind(moneyRefund with { RevenueRefunded = true }, Now, _ => ClaimDecision.Claimed);
                Expect(!canonical.List(moneyRefund.PlayerId, Store, App).Single(r => r.TransactionId == moneyRefund.TransactionId).Refunded, "money refund revoked grant");
                Expect(Get(moneyRefund) == null, "money-refunded proof readable for positive revenue");
                Refusal(() => Save(moneyRefund, moneyRefund.PlayerId, "receipt", "signature", Now));
                var transferred = Purchase("transferred"); Bind(transferred);
                Save(transferred, transferred.PlayerId, "transfer-receipt", "signature", Now);
                canonical.Bind(transferred with { PlayerId = Player("new-owner") }, Now, _ => ClaimDecision.Transferred("test recovery"));
                Expect(Get(transferred) == null && Get(transferred, Player("new-owner")) == null, "proof followed transfer");
                Refusal(() => Save(transferred with { PlayerId = Player("new-owner") }, Player("new-owner"), "receipt", "signature", Now));
            });
            test("revenue proof postgres: different key and ciphertext moved to another transaction fail authenticated decryption", () =>
            {
                var first = Purchase("cipher-first");
                var second = Purchase("cipher-second"); Bind(first); Bind(second);
                Save(first, first.PlayerId, "first-receipt", "signature", Now);
                Save(second, second.PlayerId, "second-receipt", "signature", Now);
                var wrongKey = new RevenueProofStore(Configuration, RandomNumberGenerator.GetBytes(32));
                Refusal(() => wrongKey.Get(first.PlayerId, Store, App, RevenueProofStore.LedgerKey(Store, App, first.TransactionId), Product, Now));
                using var connection = Open();
                using var command = new NpgsqlCommand("""
                    UPDATE shorepop_verifier.revenue_proofs destination SET ciphertext=source.ciphertext,nonce=source.nonce,tag=source.tag
                    FROM shorepop_verifier.revenue_proofs source WHERE destination.ledger_key=@second AND source.ledger_key=@first
                      AND destination.player_id=@p AND source.player_id=@p
                    """, connection);
                command.Parameters.AddWithValue("first", RevenueProofStore.LedgerKey(Store, App, first.TransactionId));
                command.Parameters.AddWithValue("second", RevenueProofStore.LedgerKey(Store, App, second.TransactionId)); command.Parameters.AddWithValue("p", first.PlayerId);
                Expect(command.ExecuteNonQuery() == 1, "ciphertext fixture relocation");
                Refusal(() => Get(second));
            });
            test("revenue proof postgres: four concurrent requests finish with a four-connection pool", () =>
            {
                var row = Purchase("parallel"); Bind(row); Save(row, row.PlayerId, "parallel-receipt", "signature", Now);
                var requests = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
                {
                    for (int i = 0; i < 3; i++) Expect(Contents(Get(row)) == "parallel-receipt", "parallel receipt");
                })).ToArray();
                Expect(Task.WaitAll(requests, TimeSpan.FromSeconds(45)), "pool exhaustion with nested permission lookup");
            });
            test("revenue proof postgres: delayed economics recover only the same receipt order", () =>
            {
                var known = Purchase("delayed-order");
                var unknown = known with { RevenueVerified = false, RevenueTransactionId = "", RevenuePurchasedAtUnixMs = 0 };
                Bind(unknown);
                proofs.Save(unknown, unknown.PlayerId, Receipt(known, "delayed-receipt"), "signature", Now);
                Expect(Get(unknown) == null, "unknown economic order returned proof");
                Bind(known);
                Expect(Contents(Get(known)) == "delayed-receipt" && Get(known)!.ExpiresUtc == Now.AddDays(30), "late economics did not bind original receipt/window");
            });
            test("revenue proof postgres: erasure survives reopen and preserves canonical settlement state", () =>
            {
                var row = Purchase("erase-owned", product: ShorepopCatalog.SubscriptionProductId) with
                {
                    PlayerId = ErasurePlayer("owner"), PurchasedPeriodExpiryUtcTicks = Now.AddDays(60).UtcTicks,
                    SignedPayload = "canonical-store-proof",
                };
                Bind(row); Save(row, row.PlayerId, "erase-private-receipt", "erase-private-signature", Now);
                Expect(canonical.Ack(Store, App, row.TransactionId, row.PlayerId, Now) == AckOutcome.Acked, "initial ack");
                var before = canonical.List(row.PlayerId, Store, App).ToArray();
                var claimsBefore = canonical.Claims(Store, App, row.TransactionId).ToArray();
                var revisionBefore = canonical.Subscription(row.PlayerId, Store, App);
                var erased = proofs.EraseForPlayer(row.PlayerId, Store, App, Now.AddHours(1));
                Expect(erased == new RevenueProofErasureResult(1, false, Now.AddHours(1).ToUnixTimeMilliseconds()), "first erasure result");
                Expect(Get(row) == null, "erased proof readable");
                Expect(canonical.List(row.PlayerId, Store, App).SequenceEqual(before), "canonical purchase changed");
                Expect(canonical.Claims(Store, App, row.TransactionId).SequenceEqual(claimsBefore), "claim/ack changed");
                Expect(canonical.Subscription(row.PlayerId, Store, App) == revisionBefore, "subscription revision changed");
                var reopened = new RevenueProofStore(Configuration, key);
                var repeated = reopened.EraseForPlayer(row.PlayerId, Store, App, Now.AddDays(1));
                Expect(repeated == new RevenueProofErasureResult(0, true, erased.SuppressedAtUnixMs), "repeat changed suppression identity");
                Refusal(() => reopened.Save(row, row.PlayerId, Receipt(row, "replayed-proof"), "signature", Now.AddDays(1)));
                var renewal = row with
                {
                    RevenueTransactionId = row.RevenueTransactionId + "..1", RevenuePurchasedAtUnixMs = Now.AddDays(31).ToUnixTimeMilliseconds(),
                    SignedDateUnixMs = row.SignedDateUnixMs + 1, PurchasedPeriodExpiryUtcTicks = Now.AddDays(90).UtcTicks,
                };
                Bind(renewal);
                Refusal(() => reopened.Save(renewal, renewal.PlayerId, Receipt(renewal, "renewal-proof"), "signature", Now.AddDays(31)));
                Expect(Get(renewal, at: Now.AddDays(31)) == null, "renewal repopulated erased proof");
                Expect(canonical.Subscription(row.PlayerId, Store, App) == (revisionBefore.Revision + 1, renewal.PurchasedPeriodExpiryUtcTicks), "settlement stopped updating after proof erasure");
                using var connection = Open();
                using var command = new NpgsqlCommand("SELECT octet_length(ciphertext) FROM shorepop_verifier.revenue_proofs WHERE store=@s AND app_id=@a AND ledger_key=@l", connection);
                command.Parameters.AddWithValue("s", Store); command.Parameters.AddWithValue("a", App);
                command.Parameters.AddWithValue("l", RevenueProofStore.LedgerKey(Store, App, row.TransactionId));
                Expect(Convert.ToInt32(command.ExecuteScalar()) == 0, "erasure did not remove ciphertext bytes");
            });
            test("revenue proof postgres: erasure is exact player and application scoped", () =>
            {
                string player = ErasurePlayer("scoped");
                var erased = Purchase("erase-scope") with { PlayerId = player };
                var other = Purchase("erase-other") with { PlayerId = ErasurePlayer("other") };
                var apple = Purchase("erase-apple") with
                {
                    PlayerId = player, Store = ShorepopCatalog.AppleStore, ApplicationId = ShorepopCatalog.AppleBundleId,
                    RevenueTransactionId = run + "-erase-apple",
                };
                Bind(erased); Bind(other); Bind(apple);
                Save(erased, player, "to-erase", "signature", Now);
                Save(other, other.PlayerId, "other-player-proof", "signature", Now);
                proofs.Save(apple, player, "apple-proof", "", Now);
                proofs.EraseForPlayer(player, Store, App, Now);
                Expect(Get(erased) == null && Contents(Get(other)) == "other-player-proof", "erasure crossed player scope");
                Expect(proofs.Get(player, apple.Store, apple.ApplicationId, RevenueProofStore.LedgerKey(apple.Store, apple.ApplicationId, apple.TransactionId), apple.ProductId, Now)?.Receipt == "apple-proof", "erasure crossed application scope");
                foreach (var invalid in new[]
                {
                    (Player: "", Store, App), (Player: "invalid/player", Store, App), (Player: new string('a', 129), Store, App),
                    (Player: player, Store: "unknown-store", App), (Player: player, Store, App: ShorepopCatalog.AppleBundleId),
                    (Player: player, Store: ShorepopCatalog.AppleStore, App),
                })
                {
                    try { proofs.EraseForPlayer(invalid.Player, invalid.Store, invalid.App, Now); }
                    catch (VerifierRefusal error)
                    {
                        Expect(error.Status == 422 && error.Code == "revenue_proof_erasure_request_invalid", "erasure scope refusal");
                        continue;
                    }
                    throw new Exception("invalid erasure scope accepted");
                }
                Expect(Contents(Get(other)) == "other-player-proof", "invalid requests changed another player's proof");
            });
            test("revenue proof postgres: erasure before first cache suppresses later receipts and leaves refunds and transfers intact", () =>
            {
                string player = ErasurePlayer("empty");
                Expect(proofs.EraseForPlayer(player, Store, App, Now).ClearedProofCount == 0, "empty erasure cleared rows");
                var row = Purchase("erase-later") with { PlayerId = player };
                Bind(row);
                Refusal(() => Save(row, player, "later-receipt", "signature", Now.AddDays(1)));
                Expect(Get(row) == null, "later receipt became recoverable");
                Expect(canonical.Ack(Store, App, row.TransactionId, player, Now) == AckOutcome.Acked, "erasure prevented settlement ack");
                Bind(row with { Refunded = true });
                var transferred = Purchase("erase-transfer") with { PlayerId = ErasurePlayer("previous") };
                Bind(transferred);
                canonical.Bind(transferred with { PlayerId = player }, Now, _ => ClaimDecision.Transferred("test recovery"));
                var transfersBefore = canonical.Transfers(Store, App, transferred.TransactionId).ToArray();
                var rowsBefore = canonical.List(player, Store, App).ToArray();
                proofs.EraseForPlayer(player, Store, App, Now.AddDays(1));
                Expect(canonical.List(player, Store, App).SequenceEqual(rowsBefore), "repeat erasure changed refund or purchase");
                Expect(canonical.Transfers(Store, App, transferred.TransactionId).SequenceEqual(transfersBefore), "transfer history changed");
            });
            test("revenue proof postgres: concurrent save and erasure finish permanently suppressed", () =>
            {
                var row = Purchase("erase-race") with { PlayerId = ErasurePlayer("race") };
                Bind(row); Save(row, row.PlayerId, "initial-race-proof", "signature", Now);
                var operations = Enumerable.Range(0, 6).Select(i => Task.Run(() =>
                {
                    if (i == 2) { proofs.EraseForPlayer(row.PlayerId, Store, App, Now); return; }
                    try { Save(row, row.PlayerId, "racing-proof", "signature", Now); }
                    catch (VerifierRefusal refusal) { Expect(refusal.Code == "revenue_proof_unavailable", "race refusal"); }
                })).ToArray();
                Expect(Task.WaitAll(operations, TimeSpan.FromSeconds(45)), "save/erasure lock did not finish");
                Expect(Get(row) == null, "concurrent save restored erased proof");
                Refusal(() => Save(row, row.PlayerId, "after-race-proof", "signature", Now));
                var repeated = proofs.EraseForPlayer(row.PlayerId, Store, App, Now);
                Expect(repeated.AlreadySuppressed && repeated.ClearedProofCount == 0, "concurrent save retained ciphertext");
            });
            test("privacy postgres: accepted deletion request persists pending with stable identity and proof suppression", () =>
            {
                var row = Purchase("privacy-request") with { PlayerId = ErasurePlayer("privacy") };
                Bind(row); Save(row, row.PlayerId, "privacy-receipt", "signature", Now);
                string analytics = Guid.NewGuid().ToString("D");
                var result = proofs.RequestAccountDeletion(row.PlayerId, Store, App, analytics, Now);
                Expect(Guid.TryParseExact(result.RequestId, "D", out _) && result.Status == "accepted" &&
                    result.RequestedAtUnixMs == Now.ToUnixTimeMilliseconds() && result.ClearedProofCount == 1 && !result.AlreadyRequested, "request acceptance result");
                Expect(Get(row) == null && canonical.List(row.PlayerId, Store, App).Single() == row, "request changed settlement or retained recoverable proof");
                Refusal(() => Save(row, row.PlayerId, "post-request-receipt", "signature", Now));
                var reopened = new RevenueProofStore(Configuration, key);
                var again = reopened.RequestAccountDeletion(row.PlayerId, Store, App, analytics.ToUpperInvariant(), Now.AddDays(1));
                Expect(again.RequestId == result.RequestId && again.RequestedAtUnixMs == result.RequestedAtUnixMs &&
                    again.Status == "accepted" && again.AlreadyRequested && again.AlreadySuppressed && again.ClearedProofCount == 0, "retry changed request identity");
                foreach (string? conflicting in new[] { null, Guid.NewGuid().ToString("D") })
                {
                    try { proofs.RequestAccountDeletion(row.PlayerId, Store, App, conflicting, Now); }
                    catch (VerifierRefusal error) { Expect(error.Code == "privacy_deletion_metadata_conflict" && error.Status == 422, "metadata conflict refusal"); continue; }
                    throw new Exception("request metadata was silently replaced");
                }
                using var connection = Open();
                using var command = new NpgsqlCommand("SELECT status,analytics_install_id,request_id,requested_ms FROM shorepop_verifier.privacy_deletion_requests WHERE store=@s AND app_id=@a AND player_id=@p", connection);
                command.Parameters.AddWithValue("s", Store); command.Parameters.AddWithValue("a", App); command.Parameters.AddWithValue("p", row.PlayerId);
                using var reader = command.ExecuteReader(); Expect(reader.Read(), "durable request missing");
                Expect(reader.GetString(0) == "pending" && reader.GetGuid(1) == Guid.Parse(analytics) &&
                    reader.GetGuid(2).ToString("D") == result.RequestId && reader.GetInt64(3) == result.RequestedAtUnixMs, "stored request changed or falsely completed");
            });
            test("privacy postgres: optional analytics identity and invalid request metadata are handled without partial writes", () =>
            {
                string player = ErasurePlayer("privacyEmpty");
                foreach (string invalid in new[] { "", "not-a-guid", Guid.NewGuid().ToString("N") })
                {
                    try { proofs.RequestAccountDeletion(player, Store, App, invalid, Now); }
                    catch (VerifierRefusal error) { Expect(error.Status == 422 && error.Code == "privacy_deletion_request_invalid", "invalid GUID refusal"); continue; }
                    throw new Exception("invalid analytics GUID accepted");
                }
                var result = proofs.RequestAccountDeletion(player, Store, App, null, Now);
                Expect(!result.AlreadyRequested && !result.AlreadySuppressed && result.ClearedProofCount == 0, "invalid request left partial state");
                var again = proofs.RequestAccountDeletion(player, Store, App, null, Now.AddDays(1));
                Expect(again.AlreadyRequested && again.RequestId == result.RequestId, "null metadata request not idempotent");
                var row = Purchase("privacy-later-proof") with { PlayerId = player }; Bind(row);
                Refusal(() => Save(row, player, "late-request-proof", "signature", Now));
                try { proofs.RequestAccountDeletion(ErasurePlayer("invalidScope"), ShorepopCatalog.AppleStore, App, null, Now); }
                catch (VerifierRefusal error) { Expect(error.Status == 422 && error.Code == "privacy_deletion_request_invalid", "invalid privacy scope refusal"); return; }
                throw new Exception("invalid privacy application accepted");
            });
            test("privacy postgres: Firebase identifier is normalized, bounded and immutable on repeat", () =>
            {
                string player = ErasurePlayer("firebase");
                foreach (string invalid in new[] { "", new string('a', 31), new string('b', 65), new string('x', 32), "Ｆ" + new string('a', 31) })
                {
                    try { proofs.RequestAccountDeletion(player, Store, App, null, Now, invalid); }
                    catch (VerifierRefusal error) { Expect(error.Status == 422 && error.Code == "privacy_deletion_request_invalid", "invalid Firebase ID refusal"); continue; }
                    throw new Exception("invalid Firebase ID accepted");
                }
                string firebase = new string('a', 32);
                var first = proofs.RequestAccountDeletion(player, Store, App, null, Now, firebase.ToUpperInvariant());
                Expect(!first.AlreadyRequested && !first.AlreadySuppressed, "invalid Firebase request wrote partial state");
                Expect(proofs.RequestAccountDeletion(player, Store, App, null, Now.AddDays(1), firebase).RequestId == first.RequestId, "case normalized retry changed request");
                foreach (string? conflicting in new[] { null, new string('b', 32) })
                {
                    try { proofs.RequestAccountDeletion(player, Store, App, null, Now, conflicting); }
                    catch (VerifierRefusal error) { Expect(error.Status == 422 && error.Code == "privacy_deletion_metadata_conflict", "Firebase metadata conflict refusal"); continue; }
                    throw new Exception("Firebase ID silently replaced");
                }
                using var connection = Open();
                using var command = new NpgsqlCommand("SELECT firebase_app_instance_id FROM shorepop_verifier.privacy_deletion_requests WHERE player_id=@p", connection);
                command.Parameters.AddWithValue("p", player);
                Expect((string)command.ExecuteScalar()! == firebase, "stored Firebase identity not normalized");
                var max = proofs.RequestAccountDeletion(ErasurePlayer("firebaseMax"), Store, App, null, Now, new string('C', 64));
                Expect(max.Status == "accepted", "maximum Firebase ID length rejected");
            });
            test("privacy postgres: suppression failure rolls back request acceptance and keeps original proof", () =>
            {
                var row = Purchase("privacy-rollback") with { PlayerId = ErasurePlayer("rollback") };
                Bind(row); Save(row, row.PlayerId, "rollback-original-proof", "signature", Now);
                string constraint = "privacy_fault_" + Guid.NewGuid().ToString("N");
                // Both interpolated values are generated alphanumeric test identities, never request data.
                using (var connection = Open())
                using (var command = new NpgsqlCommand("ALTER TABLE shorepop_verifier.revenue_proof_suppressions ADD CONSTRAINT " + constraint +
                    " CHECK(player_id <> '" + row.PlayerId + "') NOT VALID", connection)) command.ExecuteNonQuery();
                try
                {
                    bool refused = false;
                    try { proofs.RequestAccountDeletion(row.PlayerId, Store, App, null, Now); }
                    catch (PostgresException error) { Expect(error.SqlState == "23514", "unexpected fault-injection error"); refused = true; }
                    Expect(refused && Contents(Get(row)) == "rollback-original-proof", "failed request changed cached proof");
                    using var connection = Open();
                    using var command = new NpgsqlCommand("SELECT COUNT(*) FROM shorepop_verifier.privacy_deletion_requests WHERE player_id=@p", connection);
                    command.Parameters.AddWithValue("p", row.PlayerId);
                    Expect((long)command.ExecuteScalar()! == 0, "request was accepted without suppression");
                }
                finally
                {
                    using var connection = Open();
                    using var command = new NpgsqlCommand("ALTER TABLE shorepop_verifier.revenue_proof_suppressions DROP CONSTRAINT " + constraint, connection);
                    command.ExecuteNonQuery();
                }
            });
            test("revenue proof postgres: thirty-day expiry clears ciphertext and replay never extends or recreates its window", () =>
            {
                var row = Purchase("expiry"); Bind(row); Save(row, row.PlayerId, "initial-receipt", "signature", Now);
                Save(row, row.PlayerId, "updated-receipt", "signature", Now.AddDays(29));
                var before = Get(row, at: Now.AddDays(29));
                Expect(Contents(before) == "updated-receipt" && before!.ExpiresUtc == Now.AddDays(30), "replay extended expiry");
                Expect(Get(row, at: Now.AddDays(30)) == null, "expired proof returned");
                Save(row, row.PlayerId, "expired-replay", "signature", Now.AddDays(31));
                Expect(Get(row, at: Now.AddDays(31)) == null, "expired proof reactivated");
                using var connection = Open();
                using var command = new NpgsqlCommand("SELECT octet_length(ciphertext),created_ms,expires_ms FROM shorepop_verifier.revenue_proofs WHERE ledger_key=@l AND player_id=@p", connection);
                command.Parameters.AddWithValue("l", RevenueProofStore.LedgerKey(Store, App, row.TransactionId)); command.Parameters.AddWithValue("p", row.PlayerId);
                using var reader = command.ExecuteReader(); Expect(reader.Read(), "expiry tombstone missing");
                Expect(reader.GetInt32(0) == 0 && reader.GetInt64(1) == Now.ToUnixTimeMilliseconds() && reader.GetInt64(2) == Now.AddDays(30).ToUnixTimeMilliseconds(), "expiry/ciphertext state");
            });
            test("revenue proof postgres: renewal cannot pair an old receipt with a new order and gets its own fixed window", () =>
            {
                var original = Purchase("renewal", product: ShorepopCatalog.SubscriptionProductId); Bind(original);
                Save(original, original.PlayerId, "original-order-receipt", "signature", Now);
                var renewal = original with { RevenueTransactionId = original.RevenueTransactionId + "..1", RevenuePurchasedAtUnixMs = Now.AddDays(31).ToUnixTimeMilliseconds() };
                Bind(renewal);
                Expect(Get(renewal, at: Now.AddDays(31)) == null, "old receipt returned with new economic order");
                Refusal(() => Save(original, original.PlayerId, "original-order-replay", "signature", Now.AddDays(31)));
                Save(renewal, renewal.PlayerId, "renewal-order-receipt", "signature", Now.AddDays(31));
                var recovered = Get(renewal, at: Now.AddDays(31));
                Expect(Contents(recovered) == "renewal-order-receipt" && recovered!.ExpiresUtc == Now.AddDays(61), "new paid order did not get its own window");
                Save(renewal, renewal.PlayerId, "renewal-order-receipt", "signature", Now.AddDays(60));
                Expect(Get(renewal, at: Now.AddDays(60))!.ExpiresUtc == Now.AddDays(61), "renewal replay extended its window");
            });
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            using var connection = Open();
            using var tx = connection.BeginTransaction();
            using var cleanup = new NpgsqlCommand("""
                DELETE FROM shorepop_verifier.revenue_proofs WHERE player_id LIKE @prefix OR player_id LIKE @erasure;
                DELETE FROM shorepop_verifier.revenue_proof_suppressions WHERE player_id LIKE @erasure;
                DELETE FROM shorepop_verifier.privacy_deletion_requests WHERE player_id LIKE @erasure;
                DELETE FROM shorepop_verifier.transfers WHERE transaction_id LIKE @prefix;
                DELETE FROM shorepop_verifier.claims WHERE transaction_id LIKE @prefix;
                DELETE FROM shorepop_verifier.subscriptions WHERE player_id LIKE @prefix OR player_id LIKE @erasure;
                DELETE FROM shorepop_verifier.purchases WHERE transaction_id LIKE @prefix;
                """, connection, tx);
            cleanup.Parameters.AddWithValue("prefix", run + "-%"); cleanup.Parameters.AddWithValue("s", Store); cleanup.Parameters.AddWithValue("a", App);
            cleanup.Parameters.AddWithValue("erasure", erasurePrefix + "%");
            cleanup.ExecuteNonQuery(); tx.Commit();
        }
    }

    private static NpgsqlConnection Open()
    {
        var connection = new NpgsqlConnection(PostgresPurchaseStore.NormalizeConnectionString(Configuration));
        connection.Open();
        return connection;
    }
}
