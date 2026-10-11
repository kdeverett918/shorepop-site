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
        byte[] key = RandomNumberGenerator.GetBytes(32);
        var canonical = new PostgresPurchaseStore(Configuration);
        var proofs = new RevenueProofStore(Configuration, key);
        string Player(string label) => run + "-" + label;
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
                DELETE FROM shorepop_verifier.revenue_proofs WHERE player_id LIKE @prefix;
                DELETE FROM shorepop_verifier.transfers WHERE transaction_id LIKE @prefix;
                DELETE FROM shorepop_verifier.claims WHERE transaction_id LIKE @prefix;
                DELETE FROM shorepop_verifier.subscriptions WHERE player_id LIKE @prefix;
                DELETE FROM shorepop_verifier.purchases WHERE transaction_id LIKE @prefix;
                """, connection, tx);
            cleanup.Parameters.AddWithValue("prefix", run + "-%"); cleanup.Parameters.AddWithValue("s", Store); cleanup.Parameters.AddWithValue("a", App);
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
