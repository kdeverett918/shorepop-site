using System.Diagnostics;
using Npgsql;
using Shorepop.Verifier;

internal static class PostgresStoreTests
{
    private const string Store = "postgres-integration-test";
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);
    private static void Expect(bool value, string detail) { if (!value) throw new Exception(detail); }
    private static string Configuration => Environment.GetEnvironmentVariable("SHOREPOP_TEST_POSTGRES") ?? throw new Exception("PostgreSQL test configuration absent");
    private static PostgresPurchaseStore New() => new(Configuration);
    private static PurchaseRecord Record(string app, string transaction, string player = "player-a", string? group = null,
        string product = "gg.shorepop.gems.80", long expiry = 0, long signed = 100, bool refunded = false) =>
        new(Store, app, transaction, player, product, "Sandbox", refunded, expiry, group, null, signed);
    private static ClaimDecision Single(ClaimContext context, string player) =>
        context.TransactionClaims.Count == 0 || context.TransactionClaims.Any(c => c.PlayerId == player)
            ? ClaimDecision.Claimed : ClaimDecision.Refused("already_claimed");

    public static void Run(Action<string, Action> test)
    {
        test("postgres: connection parsing omits secrets and disables detail/parameter logging", () =>
        {
            var parsed = new NpgsqlConnectionStringBuilder(PostgresPurchaseStore.NormalizeConnectionString(
                "postgresql://user:p%40ss%3Bword@example.invalid:5433/test_db?sslmode=verify-full"));
            Expect(parsed.Host == "example.invalid" && parsed.Port == 5433 && parsed.Password == "p@ss;word", "URI decoding");
            Expect(parsed.SslMode == SslMode.VerifyFull && !parsed.IncludeErrorDetail && !parsed.LogParameters && !parsed.PersistSecurityInfo, "safe defaults");
            Expect(parsed.SearchPath == "pg_catalog" && !parsed.NoResetOnClose, "fixed search path and pool reset");
            Expect(parsed.MaxPoolSize == 4 && parsed.Timeout <= 10 && parsed.CommandTimeout <= 15, "bounded connection use");
            var defaultTls = new NpgsqlConnectionStringBuilder(PostgresPurchaseStore.NormalizeConnectionString("postgres://user:password@example.invalid/db"));
            Expect(defaultTls.SslMode == SslMode.VerifyFull, "URI default validates the host certificate");
            try { _ = PostgresPurchaseStore.NormalizeConnectionString("postgres://secret:secret@example.invalid/db?unknown=secret"); }
            catch (ArgumentException error) { Expect(!error.ToString().Contains("secret"), "parse error included supplied value"); return; }
            throw new Exception("unrecognized URI setting accepted");
        });
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SHOREPOP_TEST_POSTGRES")))
        {
            Console.WriteLine("SKIP PostgreSQL integration tests: SHOREPOP_TEST_POSTGRES is unset.");
            return;
        }

        string app = "shorepop-test-" + Guid.NewGuid().ToString("N");
        try
        {
            test("postgres: repeated delivery dedupes, refusal and callback failure leave no writes", () =>
            {
                var store = New();
                var record = Record(app, "dedupe");
                store.Bind(record, Now, _ => ClaimDecision.Claimed);
                store.Bind(record, Now.AddHours(1), _ => ClaimDecision.Claimed);
                Expect(store.Claims(Store, app, record.TransactionId).Count == 1, "duplicate claim inserted");
                Expect(store.Claims(Store, app, record.TransactionId).Single().BoundUtc == Now, "bind time changed");
                var refused = store.Bind(record with { PlayerId = "player-b", Refunded = true }, Now, _ => ClaimDecision.Refused("test"));
                Expect(refused.PreviousClaimants.SequenceEqual(["player-a"]) && !store.List("player-a", Store, app).Single().Refunded, "refusal mutated record");
                try { store.Bind(Record(app, "rolled-back"), Now, _ => throw new InvalidOperationException("test rollback")); }
                catch (InvalidOperationException) { }
                Expect(store.Claims(Store, app, "rolled-back").Count == 0, "callback failure wrote claim");
            });
            test("postgres: refunds stay set, newest signed evidence wins, null payload preserves prior evidence", () =>
            {
                var store = New();
                var row = Record(app, "sticky", signed: 200, expiry: 2000) with { SignedPayload = "new-evidence" };
                store.Bind(row, Now, _ => ClaimDecision.Claimed);
                store.Bind(row with { SignedDateUnixMs = 100, Refunded = true, PurchasedPeriodExpiryUtcTicks = 1000, SignedPayload = "old-evidence" }, Now, _ => ClaimDecision.Claimed);
                store.Bind(row with { SignedDateUnixMs = 300, SignedPayload = null, PurchasedPeriodExpiryUtcTicks = 3000 }, Now, _ => ClaimDecision.Claimed);
                var result = store.List("player-a", Store, app).Single(r => r.TransactionId == "sticky");
                Expect(result.Refunded && result.SignedDateUnixMs == 300 && result.PurchasedPeriodExpiryUtcTicks == 3000 && result.SignedPayload == "new-evidence", "merge semantics");
            });
            test("postgres: claim context includes renewal group and latest signed date", () =>
            {
                var store = New();
                store.Bind(Record(app, "group-one", "group-a", "group-original", signed: 200), Now, _ => ClaimDecision.Claimed);
                store.Bind(Record(app, "group-two", "group-b", "group-original", signed: 300), Now, context =>
                {
                    Expect(!context.Exists && context.GroupClaimants.SequenceEqual(["group-a"]) && context.GroupLatestSignedDateMs == 200, "first renewal context");
                    return ClaimDecision.Claimed;
                });
                store.Bind(Record(app, "group-two", "group-c", null), Now, context =>
                {
                    Expect(context.Exists && context.TransactionClaims.Single().PlayerId == "group-b", "existing context");
                    Expect(context.GroupClaimants.SequenceEqual(["group-a", "group-b"]) && context.GroupLatestSignedDateMs == 300, "persisted original reference fallback");
                    return ClaimDecision.Refused("test");
                });
            });
            test("postgres: verified gross revenue survives reconnect and evidence without price metadata", () =>
            {
                var store = New();
                var row = Record(app, "revenue", "revenue-player", product: ShorepopCatalog.SubscriptionProductId, expiry: 1000) with
                {
                    RevenueVerified = true, GrossRevenueMicros = 2990000, RevenueCurrency = "USD",
                    RevenueTransactionId = "GPA.test-order", RevenuePurchasedAtUnixMs = Now.ToUnixTimeMilliseconds(),
                };
                store.Bind(row, Now, _ => ClaimDecision.Claimed);
                store.Bind(Record(app, "revenue", "revenue-player", product: ShorepopCatalog.SubscriptionProductId, signed: 200), Now, _ => ClaimDecision.Claimed);
                var persisted = New().List("revenue-player", Store, app).Single();
                Expect(persisted.RevenueVerified && persisted.GrossRevenueMicros == 2990000 && persisted.RevenueCurrency == "USD" &&
                    persisted.RevenueTransactionId == "GPA.test-order" && persisted.RevenuePurchasedAtUnixMs == Now.ToUnixTimeMilliseconds(), "verified economic metadata lost");
                store.Bind(row with { RevenueRefunded = true, RevenueVerified = false }, Now, _ => ClaimDecision.Claimed);
                var refunded = New().List("revenue-player", Store, app).Single();
                Expect(refunded.RevenueRefunded && !refunded.Refunded && refunded.GrossRevenueMicros == 2990000, "money refund revoked entitlement or lost economics");
                store.Bind(row with { SignedDateUnixMs = 300 }, Now, _ => ClaimDecision.Claimed);
                Expect(New().List("revenue-player", Store, app).Single().RevenueRefunded, "same-order refund was not sticky");
                store.Bind(row with { SignedDateUnixMs = 400, RevenueTransactionId = "GPA.test-order-renewal", RevenuePurchasedAtUnixMs = Now.AddDays(30).ToUnixTimeMilliseconds() }, Now, _ => ClaimDecision.Claimed);
                var renewed = New().List("revenue-player", Store, app).Single();
                Expect(!renewed.RevenueRefunded && renewed.RevenueTransactionId == "GPA.test-order-renewal", "paid renewal retained earlier order refund");
                store.Bind(row with { SignedDateUnixMs = 200, RevenueVerified = false, RevenueRefunded = true, RevenueTransactionId = "GPA.test-missing-time-refund", RevenuePurchasedAtUnixMs = 0 }, Now, _ => ClaimDecision.Claimed);
                var historical = New().List("revenue-player", Store, app).Single();
                Expect(historical.RevenueTransactionId == "GPA.test-order-renewal" && !historical.RevenueRefunded &&
                    historical.RefundedRevenueOrderIdsJson.Contains("GPA.test-missing-time-refund", StringComparison.Ordinal), "untimed historical refund lost or changed current paid order");
                store.Bind(row with { SignedDateUnixMs = 600, RevenueTransactionId = "GPA.test-missing-time-refund", RevenuePurchasedAtUnixMs = Now.AddDays(31).ToUnixTimeMilliseconds() }, Now, _ => ClaimDecision.Claimed);
                Expect(New().List("revenue-player", Store, app).Single().RevenueRefunded, "recorded historical refund forgotten when order became current");
            });
            test("postgres: acknowledgment is scoped, idempotent and retained after reconnect", () =>
            {
                var store = New();
                var row = Record(app, "acked");
                store.Bind(row, Now, _ => ClaimDecision.Claimed);
                Expect(store.Ack(Store, app, "absent", "player-a", Now) == AckOutcome.NotFound, "missing transaction ack");
                Expect(store.Ack(Store, app, "acked", "player-b", Now) == AckOutcome.NotClaimant, "non-claimant ack");
                Expect(store.Ack(Store, app, "acked", "player-a", Now.AddMinutes(1)) == AckOutcome.Acked, "first ack");
                store.Ack(Store, app, "acked", "player-a", Now.AddMinutes(2));
                Expect(New().Claims(Store, app, "acked").Single().AckedUtc == Now.AddMinutes(1), "first ack changed or was not durable");
            });
            test("postgres: ownership transfer removes old claim and journals only actual moves", () =>
            {
                var store = New();
                var row = Record(app, "transfer", "transfer-a");
                store.Bind(row, Now, _ => ClaimDecision.Claimed);
                store.Ack(Store, app, "transfer", "transfer-a", Now);
                store.Bind(row with { PlayerId = "transfer-b" }, Now.AddMinutes(1), _ => ClaimDecision.Transferred("token match"));
                store.Bind(row with { PlayerId = "transfer-b" }, Now.AddMinutes(2), _ => ClaimDecision.Transferred("token match"));
                Expect(store.List("transfer-a", Store, app).Count == 0 && store.List("transfer-b", Store, app).Single().PlayerId == "transfer-b", "old owner retained");
                Expect(store.Ack(Store, app, "transfer", "transfer-a", Now) == AckOutcome.NotClaimant && store.Claims(Store, app, "transfer").Single().AckedUtc == null, "ack transferred to new owner");
                var move = store.Transfers(Store, app, "transfer").Single();
                Expect(move.FromPlayer == "transfer-a" && move.ToPlayer == "transfer-b" && move.Reason == "token match" && move.AtUtc == Now.AddMinutes(1), "transfer journal");
            });
            test("postgres: subscription revisions change once for renewal/refund/transfer and survive restart", () =>
            {
                var store = New();
                const string player = "subscription-a";
                Expect(store.Subscription(player, Store, app) == (1, 0), "empty revision");
                var first = Record(app, "subscription-one", player, "subscription-original", ShorepopCatalog.SubscriptionProductId, 1000);
                store.Bind(first, Now, _ => ClaimDecision.Claimed);
                Expect(store.Subscription(player, Store, app) == (2, 1000), "initial period");
                var second = first with { TransactionId = "subscription-two", PurchasedPeriodExpiryUtcTicks = 2000, SignedDateUnixMs = 200 };
                store.Bind(second, Now, _ => ClaimDecision.Claimed);
                Expect(store.Subscription(player, Store, app) == (3, 2000) && New().Subscription(player, Store, app) == (3, 2000), "renewal/restart revision");
                store.Bind(second with { Refunded = true }, Now, _ => ClaimDecision.Claimed);
                Expect(store.Subscription(player, Store, app) == (4, 1000), "refund revision");
                store.Bind(first with { PlayerId = "subscription-b" }, Now, _ => ClaimDecision.Transferred("test"));
                Expect(store.Subscription(player, Store, app) == (5, 0) && store.Subscription("subscription-b", Store, app) == (1, 1000), "transfer revisions");
            });
            test("postgres: four independent processes allow only one consumable claimant", () =>
            {
                Workers(app, "single");
                Expect(New().Claims(Store, app, "process-single").Count == 1, "concurrent claim duplication");
            });
            test("postgres: configured revision epoch applies only to the first revision and survives reopen", () =>
            {
                const long epoch = 1791666000000;
                var store = new PostgresPurchaseStore(Configuration, epoch);
                Expect(store.Subscription("epoch-player", Store, app) == (epoch, 0), "epoch initial revision");
                Expect(New().Subscription("epoch-player", Store, app) == (epoch, 0), "default reopen reset epoch");
                store.Bind(Record(app, "epoch-subscription", "epoch-player", product: ShorepopCatalog.SubscriptionProductId, expiry: 7000), Now, _ => ClaimDecision.Claimed);
                Expect(New().Subscription("epoch-player", Store, app) == (epoch + 1, 7000), "next change did not increment epoch revision");
                Expect(new PostgresPurchaseStore(Configuration, epoch + 100).Subscription("epoch-player", Store, app) == (epoch + 1, 7000), "configuration change rewrote persisted revision");
            });
            test("postgres: four independent processes enforce original-transaction group cap across renewals", () =>
            {
                Workers(app, "group");
                var store = New();
                var claimants = Enumerable.Range(0, 4).SelectMany(i => store.Claimants(Store, app, "process-renewal-" + i)).Distinct().ToList();
                Expect(claimants.Count == 3, "concurrent original-group cap");
            });
            test("postgres: concurrent acknowledgments and subscription reads keep first ack and one revision", () =>
            {
                var store = New();
                store.Bind(Record(app, "process-ack", "process-subscriber", product: ShorepopCatalog.SubscriptionProductId, expiry: 5000), Now, _ => ClaimDecision.Claimed);
                Workers(app, "ack-subscription");
                Expect(store.Claims(Store, app, "process-ack").Single().AckedUtc == Now, "concurrent ack");
                Expect(store.Subscription("process-subscriber", Store, app) == (1, 5000), "concurrent revision increments");
            });
            test("postgres: acknowledgment racing with ownership recovery never transfers an acknowledged claim", () =>
            {
                var store = New();
                store.Bind(Record(app, "process-transfer-ack", "race-a"), Now, _ => ClaimDecision.Claimed);
                Workers(app, "transfer-ack");
                var claim = store.Claims(Store, app, "process-transfer-ack").Single();
                var moves = store.Transfers(Store, app, "process-transfer-ack");
                Expect((claim.PlayerId == "race-a" && claim.AckedUtc == Now && moves.Count == 0) ||
                    (claim.PlayerId == "race-b" && claim.AckedUtc == null && moves.Count == 1), "ack and recovery both settled for different owners");
            });
        }
        finally
        {
            // This integration suite uses a unique application id and deletes only its own records.
            // Never truncate the ledger or modify real application identities.
            using var connection = new NpgsqlConnection(PostgresPurchaseStore.NormalizeConnectionString(Configuration));
            connection.Open();
            using var tx = connection.BeginTransaction();
            using var cleanup = new NpgsqlCommand("""
                DELETE FROM shorepop_verifier.transfers WHERE store=@s AND app_id=@a;
                DELETE FROM shorepop_verifier.claims WHERE store=@s AND app_id=@a;
                DELETE FROM shorepop_verifier.subscriptions WHERE store=@s AND app_id=@a;
                DELETE FROM shorepop_verifier.purchases WHERE store=@s AND app_id=@a;
                """, connection, tx);
            cleanup.Parameters.AddWithValue("s", Store); cleanup.Parameters.AddWithValue("a", app);
            cleanup.ExecuteNonQuery();
            tx.Commit();
        }
    }

    private static void Workers(string app, string mode)
    {
        var processes = new List<Process>();
        try
        {
            for (int index = 0; index < 4; index++)
            {
                var info = new ProcessStartInfo(Environment.ProcessPath!)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
                };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    info.ArgumentList.Add(typeof(PostgresStoreTests).Assembly.Location);
                foreach (string value in new[] { "--postgres-worker", app, mode, index.ToString() }) info.ArgumentList.Add(value);
                processes.Add(Process.Start(info) ?? throw new Exception("could not start PostgreSQL test worker"));
            }
            foreach (var process in processes)
            {
                if (!process.WaitForExit(60000)) throw new Exception("PostgreSQL test worker timed out");
                Expect(process.ExitCode == 0, "PostgreSQL test worker failed (diagnostics omitted to keep credentials out of output)");
            }
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
    }

    public static int RunWorker(string[] args)
    {
        try
        {
            if (args.Length != 4 || !args[1].StartsWith("shorepop-test-", StringComparison.Ordinal)) return 2;
            string app = args[1], mode = args[2], index = args[3];
            var store = New();
            for (int turn = 0; turn < 5; turn++)
            {
                string player = "process-" + index + "-" + turn;
                if (mode == "single") store.Bind(Record(app, "process-single", player), Now, context => Single(context, player));
                else if (mode == "group")
                    store.Bind(Record(app, "process-renewal-" + index, player, "process-original", ShorepopCatalog.SubscriptionProductId, 1000), Now,
                        context => context.GroupClaimants.Count < 3 || context.GroupClaimants.Contains(player)
                            ? ClaimDecision.Claimed : ClaimDecision.Refused("claimant_limit_reached"));
                else if (mode == "ack-subscription")
                {
                    Expect(store.Ack(Store, app, "process-ack", "process-subscriber", Now) == AckOutcome.Acked, "ack");
                    Expect(store.Subscription("process-subscriber", Store, app) == (1, 5000), "revision");
                }
                else if (mode == "transfer-ack")
                {
                    if (int.Parse(index) % 2 == 0)
                        store.Ack(Store, app, "process-transfer-ack", "race-a", Now);
                    else
                        store.Bind(Record(app, "process-transfer-ack", "race-b"), Now, context =>
                            context.TransactionClaims.Any(c => c.PlayerId != "race-b" && c.AckedUtc != null)
                                ? ClaimDecision.Refused("already_granted") : ClaimDecision.Transferred("recovery"));
                }
                else return 2;
            }
            return 0;
        }
        catch { return 1; }
    }
}
