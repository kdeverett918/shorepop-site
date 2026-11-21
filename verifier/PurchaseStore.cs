using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Shorepop.Verifier;

public sealed record PurchaseRecord(
    string Store, string ApplicationId, string TransactionId, string PlayerId, string ProductId, string Environment,
    bool Refunded, long PurchasedPeriodExpiryUtcTicks, string? OriginalRef, string? SignedPayload, long SignedDateUnixMs,
    bool RevenueVerified = false, long GrossRevenueMicros = 0, string RevenueCurrency = "",
    string RevenueTransactionId = "", long RevenuePurchasedAtUnixMs = 0, bool RevenueRefunded = false,
    string RefundedRevenueOrderIdsJson = "[]", bool TestOrder = false);

/// <summary>One player's claim on one transaction.</summary>
public sealed record ClaimInfo(string PlayerId, DateTimeOffset BoundUtc, DateTimeOffset? AckedUtc);

/// <summary>
/// What the store knows when a claim is decided, read inside the same atomic step that applies the decision.
/// The group is every transaction sharing the incoming record's original transaction (OriginalRef, else its id).
/// </summary>
public sealed record ClaimContext(
    bool Exists, IReadOnlyList<ClaimInfo> TransactionClaims, IReadOnlyList<string> GroupClaimants, long GroupLatestSignedDateMs);

public enum ClaimAction { Claim, Transfer, Refuse }

/// <summary>
/// Claim: add the requester as a claimant (no-op when already one). Transfer: the requester replaces every
/// other claimant of this transaction and the move is logged. Refuse: nothing is written.
/// </summary>
public sealed record ClaimDecision(ClaimAction Action, string? RefusalCode = null, string? Reason = null)
{
    public static readonly ClaimDecision Claimed = new(ClaimAction.Claim);
    public static ClaimDecision Transferred(string reason) => new(ClaimAction.Transfer, null, reason);
    public static ClaimDecision Refused(string code) => new(ClaimAction.Refuse, code);
}

public sealed record BindResult(ClaimDecision Decision, IReadOnlyList<string> PreviousClaimants);
public sealed record TransferRecord(string FromPlayer, string ToPlayer, string Reason, DateTimeOffset AtUtc);
public enum AckOutcome { Acked, NotFound, NotClaimant }

/// <summary>
/// Transaction state + player claims + subscription aggregate. Implementations must make Bind atomic.
/// One row per (store, applicationId, transactionId) holds the store's state (refunds sticky); a separate claim
/// set records every player that holds it, with when it was bound and whether the client acked a durable grant.
/// Who may claim is decided by the caller's policy (see <see cref="PurchaseService"/>); the store only applies it.
/// </summary>
public interface IPurchaseStore
{
    /// <summary>Reads the claim context, asks <paramref name="decide"/>, and applies the decision atomically.</summary>
    BindResult Bind(PurchaseRecord record, DateTimeOffset now, Func<ClaimContext, ClaimDecision> decide);

    /// <summary>Marks this player's claim as durably granted by the client (idempotent; first ack time kept).</summary>
    AckOutcome Ack(string store, string applicationId, string transactionId, string playerId, DateTimeOffset now);
    /// <summary>Rows this player has claimed; <see cref="PurchaseRecord.PlayerId"/> is the requesting player.</summary>
    IReadOnlyList<PurchaseRecord> List(string playerId, string store, string applicationId);
    /// <summary>Every player that currently claims the transaction, first claimant first.</summary>
    IReadOnlyList<string> Claimants(string store, string applicationId, string transactionId);
    IReadOnlyList<ClaimInfo> Claims(string store, string applicationId, string transactionId);
    /// <summary>Recorded ownership moves of the transaction, oldest first.</summary>
    IReadOnlyList<TransferRecord> Transfers(string store, string applicationId, string transactionId);
    /// <summary>Recomputes the player's aggregate subscription expiry; the revision increases whenever it changes.</summary>
    (long Revision, long CurrentExpiryUtcTicks) Subscription(string playerId, string store, string applicationId);

    /// <summary>Records a pending subscription revenue charge bound to its originating install id (WO-20261113-653).</summary>
    void UpsertSubscriptionRevenueCharge(SubscriptionRevenueCharge charge);
    SubscriptionRevenueCharge? GetSubscriptionRevenueCharge(string store, string applicationId, string revenueOrderId);
    /// <summary>Returns unconfirmed charges eligible for this player + installId; anonymous offers happen at most once.</summary>
    IReadOnlyList<SubscriptionRevenueCharge> ClaimPendingSubscriptionRevenueCharges(string playerId, string store, string applicationId, string? installId);
    /// <summary>Confirms or retries a pending subscription revenue charge strictly for its owning install id.</summary>
    SubscriptionRevenueConfirmResult ConfirmSubscriptionRevenueCharge(string playerId, string store, string applicationId, string revenueOrderId, string? installId, DateTimeOffset now);
    /// <summary>Authorizes serving a subscription revenue proof to this player + installId.</summary>
    bool TryAuthorizeSubscriptionProofOffer(string playerId, string store, string applicationId, string revenueOrderId, string? installId);

    /// <summary>Row/size metrics for growing-bound warnings (WO-20261118-396).</summary>
    StoreStorageMetrics GetStorageMetrics();
    string Kind { get; }
}

public static class PurchaseStoreExtensions
{
    /// <summary>Legacy rule: single owner = first claimant only; otherwise every presenter is added. Returns the owner.</summary>
    public static string Bind(this IPurchaseStore store, PurchaseRecord record, bool singleOwner)
    {
        var result = store.Bind(record, DateTimeOffset.UtcNow, context =>
            singleOwner && context.TransactionClaims.Count > 0 && context.TransactionClaims.All(c => c.PlayerId != record.PlayerId)
                ? ClaimDecision.Refused("transaction_owned_by_another_player") : ClaimDecision.Claimed);
        return result.Decision.Action == ClaimAction.Refuse ? result.PreviousClaimants[0] : record.PlayerId;
    }
}

internal static class StoreRules
{
    /// <summary>
    /// Deterministic order comparison between two distinct economic orders on the same transaction/token:
    /// signed observation time first, then purchase settlement time, then ordinal order id tiebreak
    /// so same-timestamp refund + replacement never depends on arrival order (WO-20261118-899).
    /// </summary>
    public static bool IsNewerEconomicOrder(PurchaseRecord existing, PurchaseRecord incoming)
    {
        if (incoming.SignedDateUnixMs != existing.SignedDateUnixMs)
            return incoming.SignedDateUnixMs > existing.SignedDateUnixMs;
        if (incoming.RevenuePurchasedAtUnixMs != existing.RevenuePurchasedAtUnixMs)
            return incoming.RevenuePurchasedAtUnixMs > existing.RevenuePurchasedAtUnixMs;
        return StringComparer.Ordinal.Compare(incoming.RevenueTransactionId, existing.RevenueTransactionId) > 0;
    }

    /// <summary>Refunds are sticky per transaction/order; expiry and payload follow the most recently signed evidence.</summary>
    public static PurchaseRecord Merge(PurchaseRecord existing, PurchaseRecord incoming)
    {
        bool newer = incoming.SignedDateUnixMs >= existing.SignedDateUnixMs;
        bool testOrder = existing.TestOrder || incoming.TestOrder;
        if (testOrder)
        {
            return existing with
            {
                Refunded = existing.Refunded || incoming.Refunded,
                PurchasedPeriodExpiryUtcTicks = newer ? incoming.PurchasedPeriodExpiryUtcTicks : existing.PurchasedPeriodExpiryUtcTicks,
                SignedPayload = newer && incoming.SignedPayload != null ? incoming.SignedPayload : existing.SignedPayload,
                SignedDateUnixMs = Math.Max(existing.SignedDateUnixMs, incoming.SignedDateUnixMs),
                Environment = incoming.Environment,
                OriginalRef = incoming.OriginalRef ?? existing.OriginalRef,
                RevenueVerified = false,
                GrossRevenueMicros = 0,
                RevenueCurrency = "",
                RevenueTransactionId = "",
                RevenuePurchasedAtUnixMs = 0,
                RevenueRefunded = false,
                RefundedRevenueOrderIdsJson = "[]",
                TestOrder = true,
            };
        }
        // Reconciliation may temporarily lack order economics. Keep previously verified economics,
        // and never let an older Google renewal overwrite a newer order on the same purchase token.
        bool incomingRevenueKnown = incoming.RevenueTransactionId.Length > 0;
        bool existingRevenueKnown = existing.RevenueTransactionId.Length > 0;
        bool sameRevenueOrder = incomingRevenueKnown && existingRevenueKnown && incoming.RevenueTransactionId == existing.RevenueTransactionId;
        bool newerEconomicOrder = incomingRevenueKnown && (!existingRevenueKnown || IsNewerEconomicOrder(existing, incoming));
        // A new verified order identity must clear the old order's price even when its own price
        // lookup is unavailable. Same-order price failures keep the previously verified economics.
        bool takeRevenueValues = !sameRevenueOrder ? newerEconomicOrder
            : incoming.RevenueVerified && (!existing.RevenueVerified || incoming.RevenuePurchasedAtUnixMs >= existing.RevenuePurchasedAtUnixMs);
        var refundedRevenueOrders = RefundHistory(existing);
        refundedRevenueOrders.UnionWith(RefundHistory(incoming));
        string selectedRevenueOrder = takeRevenueValues ? incoming.RevenueTransactionId : existing.RevenueTransactionId;
        return existing with
        {
            Refunded = existing.Refunded || incoming.Refunded,
            PurchasedPeriodExpiryUtcTicks = newer ? incoming.PurchasedPeriodExpiryUtcTicks : existing.PurchasedPeriodExpiryUtcTicks,
            SignedPayload = newer && incoming.SignedPayload != null ? incoming.SignedPayload : existing.SignedPayload,
            SignedDateUnixMs = Math.Max(existing.SignedDateUnixMs, incoming.SignedDateUnixMs),
            Environment = incoming.Environment,
            OriginalRef = incoming.OriginalRef ?? existing.OriginalRef,
            RevenueVerified = takeRevenueValues ? incoming.RevenueVerified : existing.RevenueVerified,
            GrossRevenueMicros = takeRevenueValues ? incoming.GrossRevenueMicros : existing.GrossRevenueMicros,
            RevenueCurrency = takeRevenueValues ? incoming.RevenueCurrency : existing.RevenueCurrency,
            RevenueTransactionId = selectedRevenueOrder,
            RevenuePurchasedAtUnixMs = takeRevenueValues ? incoming.RevenuePurchasedAtUnixMs : existing.RevenuePurchasedAtUnixMs,
            RevenueRefunded = selectedRevenueOrder.Length > 0 && refundedRevenueOrders.Contains(selectedRevenueOrder),
            RefundedRevenueOrderIdsJson = JsonSerializer.Serialize(refundedRevenueOrders.Order(StringComparer.Ordinal)),
            TestOrder = false,
        };
    }

    private static HashSet<string> RefundHistory(PurchaseRecord row)
    {
        var ids = new HashSet<string>(JsonSerializer.Deserialize<string[]>(row.RefundedRevenueOrderIdsJson) ?? [], StringComparer.Ordinal);
        if (row.RevenueRefunded && row.RevenueTransactionId.Length > 0) ids.Add(row.RevenueTransactionId);
        return ids;
    }

    /// <summary>
    /// Orders rows deterministically by (SignedDateUnixMs, TransactionId) and computes aggregate active
    /// subscription expiry so a refunded transaction never cancels a distinct replacement transaction
    /// sharing the identical timestamp (WO-20261118-899).
    /// </summary>
    public static long Aggregate(IEnumerable<PurchaseRecord> rows) => rows
        .OrderBy(r => r.SignedDateUnixMs)
        .ThenBy(r => r.TransactionId, StringComparer.Ordinal)
        .Where(r => ShorepopCatalog.IsSubscription(r.ProductId) && !r.Refunded)
        .Select(r => r.PurchasedPeriodExpiryUtcTicks).DefaultIfEmpty(0).Max();
}

public sealed class InMemoryPurchaseStore : IPurchaseStore
{
    private sealed class Claim(string playerId, DateTimeOffset bound)
    {
        public string PlayerId { get; } = playerId;
        public DateTimeOffset BoundUtc { get; } = bound;
        public DateTimeOffset? AckedUtc { get; set; }
        public ClaimInfo Info => new(PlayerId, BoundUtc, AckedUtc);
    }

    private readonly object gate = new();
    private readonly Dictionary<(string, string, string), PurchaseRecord> rows = new();
    private readonly Dictionary<(string, string, string), List<Claim>> claims = new();
    private readonly Dictionary<(string, string, string), List<TransferRecord>> transfers = new();
    private readonly Dictionary<(string, string, string), (long, long)> subscriptions = new();
    private readonly Dictionary<(string, string, string), SubscriptionRevenueCharge> subRevenueCharges = new();
    public string Kind => "memory";

    public BindResult Bind(PurchaseRecord record, DateTimeOffset now, Func<ClaimContext, ClaimDecision> decide)
    {
        lock (gate)
        {
            var key = (record.Store, record.ApplicationId, record.TransactionId);
            bool exists = rows.TryGetValue(key, out var existing);
            var current = claims.TryGetValue(key, out var list) ? list : new List<Claim>();
            string group = record.OriginalRef ?? existing?.OriginalRef ?? record.TransactionId;
            var groupRows = rows.Where(r => r.Key.Item1 == record.Store && r.Key.Item2 == record.ApplicationId &&
                (r.Value.OriginalRef ?? r.Value.TransactionId) == group).ToList();
            var groupClaimants = groupRows.SelectMany(r => claims[r.Key]).Select(c => c.PlayerId).Distinct().ToList();
            long latest = groupRows.Select(r => r.Value.SignedDateUnixMs).DefaultIfEmpty(0).Max();
            var previous = current.Select(c => c.PlayerId).ToList();
            var decision = decide(new ClaimContext(exists, current.Select(c => c.Info).ToList(), groupClaimants, latest));
            if (decision.Action == ClaimAction.Refuse) return new BindResult(decision, previous);
            rows[key] = exists ? StoreRules.Merge(existing!, record) : record;
            claims[key] = current;
            if (decision.Action == ClaimAction.Transfer)
            {
                if (!transfers.TryGetValue(key, out var log)) transfers[key] = log = new List<TransferRecord>();
                foreach (var gone in current.Where(c => c.PlayerId != record.PlayerId))
                    log.Add(new TransferRecord(gone.PlayerId, record.PlayerId, decision.Reason ?? "", now));
                current.RemoveAll(c => c.PlayerId != record.PlayerId);
            }
            if (current.All(c => c.PlayerId != record.PlayerId)) current.Add(new Claim(record.PlayerId, now));
            return new BindResult(decision, previous);
        }
    }

    public AckOutcome Ack(string store, string applicationId, string transactionId, string playerId, DateTimeOffset now)
    {
        lock (gate)
        {
            if (!claims.TryGetValue((store, applicationId, transactionId), out var list)) return AckOutcome.NotFound;
            var claim = list.FirstOrDefault(c => c.PlayerId == playerId);
            if (claim == null) return AckOutcome.NotClaimant;
            claim.AckedUtc ??= now;
            return AckOutcome.Acked;
        }
    }

    public IReadOnlyList<PurchaseRecord> List(string playerId, string store, string applicationId)
    {
        lock (gate) return rows.Where(r => r.Key.Item1 == store && r.Key.Item2 == applicationId && claims[r.Key].Any(c => c.PlayerId == playerId))
            .Select(r => r.Value with { PlayerId = playerId })
            .OrderBy(r => r.TransactionId, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> Claimants(string store, string applicationId, string transactionId) =>
        Claims(store, applicationId, transactionId).Select(c => c.PlayerId).ToList();

    public IReadOnlyList<ClaimInfo> Claims(string store, string applicationId, string transactionId)
    {
        lock (gate) return claims.TryGetValue((store, applicationId, transactionId), out var list) ? list.Select(c => c.Info).ToList() : [];
    }

    public IReadOnlyList<TransferRecord> Transfers(string store, string applicationId, string transactionId)
    {
        lock (gate) return transfers.TryGetValue((store, applicationId, transactionId), out var list) ? list.ToList() : [];
    }

    public (long Revision, long CurrentExpiryUtcTicks) Subscription(string playerId, string store, string applicationId)
    {
        lock (gate)
        {
            long aggregate = StoreRules.Aggregate(List(playerId, store, applicationId));
            var key = (playerId, store, applicationId);
            subscriptions.TryGetValue(key, out var state);
            if (state.Item1 == 0 || state.Item2 != aggregate) state = (state.Item1 + 1, aggregate);
            subscriptions[key] = state;
            return state;
        }
    }

    public void UpsertSubscriptionRevenueCharge(SubscriptionRevenueCharge charge)
    {
        lock (gate)
        {
            var key = (charge.Store, charge.ApplicationId, charge.RevenueOrderId);
            if (!subRevenueCharges.ContainsKey(key)) subRevenueCharges[key] = charge;
        }
    }

    public SubscriptionRevenueCharge? GetSubscriptionRevenueCharge(string store, string applicationId, string revenueOrderId)
    {
        lock (gate) return subRevenueCharges.TryGetValue((store, applicationId, revenueOrderId), out var c) ? c : null;
    }

    public IReadOnlyList<SubscriptionRevenueCharge> ClaimPendingSubscriptionRevenueCharges(string playerId, string store, string applicationId, string? installId)
    {
        lock (gate)
        {
            var result = new List<SubscriptionRevenueCharge>();
            foreach (var key in subRevenueCharges.Keys.OrderBy(k => k.Item3, StringComparer.Ordinal).ToList())
            {
                var c = subRevenueCharges[key];
                if (c.Store != store || c.ApplicationId != applicationId || c.PlayerId != playerId || c.Confirmed) continue;
                if (c.OwningInstallId != null)
                {
                    if (installId != null && installId == c.OwningInstallId) result.Add(c);
                }
                else if (!c.OfferedToAnonymous)
                {
                    var updated = c with { OfferedToAnonymous = true };
                    subRevenueCharges[key] = updated;
                    result.Add(updated);
                }
            }
            return result;
        }
    }

    public SubscriptionRevenueConfirmResult ConfirmSubscriptionRevenueCharge(string playerId, string store, string applicationId, string revenueOrderId, string? installId, DateTimeOffset now)
    {
        lock (gate)
        {
            var key = (store, applicationId, revenueOrderId);
            if (!subRevenueCharges.TryGetValue(key, out var c) || c.PlayerId != playerId)
                throw new VerifierRefusal(404, "transaction_not_found");
            if (c.OwningInstallId != null)
            {
                if (installId == null || installId != c.OwningInstallId)
                    return new SubscriptionRevenueConfirmResult { RevenueOrderId = revenueOrderId, Accepted = false, Deliver = false, Status = "ignored_install_mismatch" };
                if (c.Confirmed)
                    return new SubscriptionRevenueConfirmResult { RevenueOrderId = revenueOrderId, Accepted = true, Deliver = false, Status = "already_confirmed" };
                subRevenueCharges[key] = c with { Confirmed = true, ConfirmedUtcMs = now.ToUnixTimeMilliseconds() };
                return new SubscriptionRevenueConfirmResult { RevenueOrderId = revenueOrderId, Accepted = true, Deliver = true, Status = "confirmed" };
            }
            if (c.Confirmed)
                return new SubscriptionRevenueConfirmResult { RevenueOrderId = revenueOrderId, Accepted = false, Deliver = false, Status = "already_confirmed" };
            subRevenueCharges[key] = c with { Confirmed = true, OfferedToAnonymous = true, ConfirmedUtcMs = now.ToUnixTimeMilliseconds() };
            return new SubscriptionRevenueConfirmResult { RevenueOrderId = revenueOrderId, Accepted = true, Deliver = true, Status = "confirmed" };
        }
    }

    public bool TryAuthorizeSubscriptionProofOffer(string playerId, string store, string applicationId, string revenueOrderId, string? installId)
    {
        lock (gate)
        {
            var key = (store, applicationId, revenueOrderId);
            if (!subRevenueCharges.TryGetValue(key, out var c)) return true;
            if (c.PlayerId != playerId) return false;
            if (c.OwningInstallId != null) return installId != null && installId == c.OwningInstallId;
            if (c.Confirmed || c.OfferedToAnonymous) return false;
            subRevenueCharges[key] = c with { OfferedToAnonymous = true };
            return true;
        }
    }

    public StoreStorageMetrics GetStorageMetrics()
    {
        lock (gate) return new StoreStorageMetrics(rows.Count, 0);
    }
}

/// <summary>
/// SQLite on the container disk. On Render's free plan the disk is ephemeral: the file (and therefore
/// every transaction-to-player claim) resets on each deploy/restart. Use PostgreSQL or a paid always-on
/// Render instance before real revenue depends on replay protection.
/// </summary>
public sealed class SqlitePurchaseStore : IPurchaseStore
{
    private readonly string databasePath;
    private readonly string connectionString;
    private readonly object gate = new();
    public string Kind => "sqlite";

    public SqlitePurchaseStore(string path)
    {
        databasePath = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        connectionString = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var connection = Open();
        Execute(connection, null, """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS purchases(
              store TEXT NOT NULL, app_id TEXT NOT NULL, transaction_id TEXT NOT NULL, player_id TEXT NOT NULL,
              product_id TEXT NOT NULL, environment TEXT NOT NULL, refunded INTEGER NOT NULL, expiry_ticks INTEGER NOT NULL,
              original_ref TEXT NULL, signed_payload TEXT NULL, signed_date_ms INTEGER NOT NULL,
              created_utc TEXT NOT NULL, updated_utc TEXT NOT NULL,
              PRIMARY KEY(store, app_id, transaction_id));
            CREATE INDEX IF NOT EXISTS purchases_player ON purchases(player_id, store, app_id);
            CREATE TABLE IF NOT EXISTS claims(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              store TEXT NOT NULL, app_id TEXT NOT NULL, transaction_id TEXT NOT NULL, player_id TEXT NOT NULL,
              created_utc TEXT NOT NULL,
              UNIQUE(store, app_id, transaction_id, player_id));
            CREATE INDEX IF NOT EXISTS claims_player ON claims(player_id, store, app_id);
            INSERT OR IGNORE INTO claims(store, app_id, transaction_id, player_id, created_utc)
              SELECT store, app_id, transaction_id, player_id, created_utc FROM purchases ORDER BY created_utc;
            CREATE TABLE IF NOT EXISTS subscriptions(
              player_id TEXT NOT NULL, store TEXT NOT NULL, app_id TEXT NOT NULL, revision INTEGER NOT NULL, expiry_ticks INTEGER NOT NULL,
              PRIMARY KEY(player_id, store, app_id));
            CREATE TABLE IF NOT EXISTS transfers(
              seq INTEGER PRIMARY KEY AUTOINCREMENT,
              store TEXT NOT NULL, app_id TEXT NOT NULL, transaction_id TEXT NOT NULL,
              from_player TEXT NOT NULL, to_player TEXT NOT NULL, reason TEXT NOT NULL, created_utc TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS purchases_original ON purchases(store, app_id, original_ref);
            CREATE TABLE IF NOT EXISTS subscription_revenue_charges(
              store TEXT NOT NULL, app_id TEXT NOT NULL, revenue_order_id TEXT NOT NULL,
              transaction_id TEXT NOT NULL, player_id TEXT NOT NULL, owning_install_id TEXT NULL,
              confirmed INTEGER NOT NULL DEFAULT 0, offered_anonymous INTEGER NOT NULL DEFAULT 0,
              created_ms INTEGER NOT NULL, confirmed_ms INTEGER NULL,
              PRIMARY KEY(store, app_id, revenue_order_id));
            CREATE INDEX IF NOT EXISTS sub_rev_charges_player ON subscription_revenue_charges(player_id, store, app_id);
            """);
        // Migration: claims gained an ack timestamp (a client-confirmed durable grant).
        using (var info = connection.CreateCommand())
        {
            info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('claims') WHERE name='acked_utc'";
            if (Convert.ToInt64(info.ExecuteScalar()) == 0) Execute(connection, null, "ALTER TABLE claims ADD COLUMN acked_utc TEXT NULL;");
        }
        // Additive migration preserves every pre-existing purchase and claim.
        foreach (var (name, definition) in new[]
        {
            ("revenue_verified", "INTEGER NOT NULL DEFAULT 0"),
            ("gross_revenue_micros", "INTEGER NOT NULL DEFAULT 0"),
            ("revenue_currency", "TEXT NOT NULL DEFAULT ''"),
            ("revenue_transaction_id", "TEXT NOT NULL DEFAULT ''"),
            ("revenue_purchased_at_ms", "INTEGER NOT NULL DEFAULT 0"),
            ("revenue_refunded", "INTEGER NOT NULL DEFAULT 0"),
            ("refunded_revenue_order_ids_json", "TEXT NOT NULL DEFAULT '[]'"),
            ("test_order", "INTEGER NOT NULL DEFAULT 0"),
        })
        {
            using var info = connection.CreateCommand();
            info.CommandText = "SELECT COUNT(*) FROM pragma_table_info('purchases') WHERE name=$name";
            info.Parameters.AddWithValue("$name", name);
            if (Convert.ToInt64(info.ExecuteScalar()) == 0)
                Execute(connection, null, $"ALTER TABLE purchases ADD COLUMN {name} {definition};");
        }
    }

    public void CheckReadiness()
    {
        lock (gate)
        {
            using var connection = Open();
            Execute(connection, null, """
                SELECT 1 FROM purchases WHERE 0;
                SELECT 1 FROM claims WHERE 0;
                SELECT 1 FROM subscriptions WHERE 0;
                """);
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, null, "PRAGMA busy_timeout=3000;");
        return connection;
    }

    private static string Stamp(DateTimeOffset time) => time.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseStamp(string text) =>
        DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public BindResult Bind(PurchaseRecord record, DateTimeOffset now, Func<ClaimContext, ClaimDecision> decide)
    {
        lock (gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction(deferred: false);
            var existing = Read(connection, tx, "WHERE store=$s AND app_id=$a AND transaction_id=$t",
                ("$s", record.Store), ("$a", record.ApplicationId), ("$t", record.TransactionId)).FirstOrDefault();
            var current = ClaimList(connection, tx, record.Store, record.ApplicationId, record.TransactionId);
            string group = record.OriginalRef ?? existing?.OriginalRef ?? record.TransactionId;
            var groupClaimants = new List<string>();
            long latest = 0;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "SELECT DISTINCT c.player_id FROM claims c JOIN purchases p ON c.store=p.store AND c.app_id=p.app_id AND " +
                    "c.transaction_id=p.transaction_id WHERE p.store=$s AND p.app_id=$a AND COALESCE(p.original_ref, p.transaction_id)=$g ORDER BY c.seq";
                command.Parameters.AddWithValue("$s", record.Store); command.Parameters.AddWithValue("$a", record.ApplicationId); command.Parameters.AddWithValue("$g", group);
                using var reader = command.ExecuteReader();
                while (reader.Read()) groupClaimants.Add(reader.GetString(0));
            }
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "SELECT COALESCE(MAX(signed_date_ms), 0) FROM purchases WHERE store=$s AND app_id=$a AND COALESCE(original_ref, transaction_id)=$g";
                command.Parameters.AddWithValue("$s", record.Store); command.Parameters.AddWithValue("$a", record.ApplicationId); command.Parameters.AddWithValue("$g", group);
                latest = Convert.ToInt64(command.ExecuteScalar());
            }
            var previous = current.Select(c => c.PlayerId).ToList();
            var decision = decide(new ClaimContext(existing != null, current, groupClaimants.Distinct().ToList(), latest));
            if (decision.Action == ClaimAction.Refuse) { tx.Commit(); return new BindResult(decision, previous); }

            var row = existing == null ? record : StoreRules.Merge(existing, record);
            string stamp = Stamp(now);
            Execute(connection, tx, """
                INSERT INTO purchases(store, app_id, transaction_id, player_id, product_id, environment, refunded, expiry_ticks,
                  original_ref, signed_payload, signed_date_ms, created_utc, updated_utc,
                  revenue_verified, gross_revenue_micros, revenue_currency, revenue_transaction_id, revenue_purchased_at_ms, revenue_refunded, refunded_revenue_order_ids_json, test_order)
                VALUES($s,$a,$t,$p,$prod,$env,$ref,$exp,$orig,$payload,$sd,$now,$now,$rv,$gross,$cur,$rt,$rp,$rr,$rh,$to)
                ON CONFLICT(store, app_id, transaction_id) DO UPDATE SET environment=$env, refunded=$ref, expiry_ticks=$exp,
                  original_ref=$orig, signed_payload=$payload, signed_date_ms=$sd, updated_utc=$now,
                  revenue_verified=$rv, gross_revenue_micros=$gross, revenue_currency=$cur,
                  revenue_transaction_id=$rt, revenue_purchased_at_ms=$rp, revenue_refunded=$rr, refunded_revenue_order_ids_json=$rh, test_order=$to
                """,
                ("$s", row.Store), ("$a", row.ApplicationId), ("$t", row.TransactionId), ("$p", row.PlayerId), ("$prod", row.ProductId),
                ("$env", row.Environment), ("$ref", row.Refunded ? 1 : 0), ("$exp", row.PurchasedPeriodExpiryUtcTicks),
                ("$orig", (object?)row.OriginalRef ?? DBNull.Value), ("$payload", (object?)row.SignedPayload ?? DBNull.Value),
                ("$sd", row.SignedDateUnixMs), ("$now", stamp), ("$rv", row.RevenueVerified ? 1 : 0),
                ("$gross", row.GrossRevenueMicros), ("$cur", row.RevenueCurrency),
                ("$rt", row.RevenueTransactionId), ("$rp", row.RevenuePurchasedAtUnixMs), ("$rr", row.RevenueRefunded ? 1 : 0),
                ("$rh", row.RefundedRevenueOrderIdsJson), ("$to", row.TestOrder ? 1 : 0));
            if (decision.Action == ClaimAction.Transfer)
            {
                foreach (var gone in current.Where(c => c.PlayerId != record.PlayerId))
                    Execute(connection, tx, "INSERT INTO transfers(store, app_id, transaction_id, from_player, to_player, reason, created_utc) VALUES($s,$a,$t,$f,$p,$r,$now)",
                        ("$s", row.Store), ("$a", row.ApplicationId), ("$t", row.TransactionId), ("$f", gone.PlayerId), ("$p", record.PlayerId),
                        ("$r", decision.Reason ?? ""), ("$now", stamp));
                Execute(connection, tx, "DELETE FROM claims WHERE store=$s AND app_id=$a AND transaction_id=$t AND player_id<>$p",
                    ("$s", row.Store), ("$a", row.ApplicationId), ("$t", row.TransactionId), ("$p", record.PlayerId));
                Execute(connection, tx, "UPDATE purchases SET player_id=$p WHERE store=$s AND app_id=$a AND transaction_id=$t",
                    ("$s", row.Store), ("$a", row.ApplicationId), ("$t", row.TransactionId), ("$p", record.PlayerId));
            }
            Execute(connection, tx, "INSERT OR IGNORE INTO claims(store, app_id, transaction_id, player_id, created_utc) VALUES($s,$a,$t,$p,$now)",
                ("$s", row.Store), ("$a", row.ApplicationId), ("$t", row.TransactionId), ("$p", record.PlayerId), ("$now", stamp));
            tx.Commit();
            return new BindResult(decision, previous);
        }
    }

    public AckOutcome Ack(string store, string applicationId, string transactionId, string playerId, DateTimeOffset now)
    {
        lock (gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction(deferred: false);
            var current = ClaimList(connection, tx, store, applicationId, transactionId);
            if (current.Count == 0) { tx.Commit(); return AckOutcome.NotFound; }
            if (current.All(c => c.PlayerId != playerId)) { tx.Commit(); return AckOutcome.NotClaimant; }
            Execute(connection, tx, "UPDATE claims SET acked_utc=COALESCE(acked_utc, $now) WHERE store=$s AND app_id=$a AND transaction_id=$t AND player_id=$p",
                ("$s", store), ("$a", applicationId), ("$t", transactionId), ("$p", playerId), ("$now", Stamp(now)));
            tx.Commit();
            return AckOutcome.Acked;
        }
    }

    public IReadOnlyList<PurchaseRecord> List(string playerId, string store, string applicationId)
    {
        lock (gate)
        {
            using var connection = Open();
            return ClaimedBy(connection, null, playerId, store, applicationId);
        }
    }

    public IReadOnlyList<string> Claimants(string store, string applicationId, string transactionId) =>
        Claims(store, applicationId, transactionId).Select(c => c.PlayerId).ToList();

    public IReadOnlyList<ClaimInfo> Claims(string store, string applicationId, string transactionId)
    {
        lock (gate)
        {
            using var connection = Open();
            return ClaimList(connection, null, store, applicationId, transactionId);
        }
    }

    public IReadOnlyList<TransferRecord> Transfers(string store, string applicationId, string transactionId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT from_player, to_player, reason, created_utc FROM transfers WHERE store=$s AND app_id=$a AND transaction_id=$t ORDER BY seq";
            command.Parameters.AddWithValue("$s", store); command.Parameters.AddWithValue("$a", applicationId); command.Parameters.AddWithValue("$t", transactionId);
            using var reader = command.ExecuteReader();
            var list = new List<TransferRecord>();
            while (reader.Read()) list.Add(new TransferRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseStamp(reader.GetString(3))));
            return list;
        }
    }

    private static List<PurchaseRecord> ClaimedBy(SqliteConnection connection, SqliteTransaction? tx, string playerId, string store, string applicationId) =>
        Read(connection, tx, "WHERE store=$s AND app_id=$a AND EXISTS(SELECT 1 FROM claims c WHERE c.store=purchases.store AND " +
            "c.app_id=purchases.app_id AND c.transaction_id=purchases.transaction_id AND c.player_id=$p) ORDER BY transaction_id",
            ("$p", playerId), ("$s", store), ("$a", applicationId))
            .Select(r => r with { PlayerId = playerId }).ToList();

    private static List<ClaimInfo> ClaimList(SqliteConnection connection, SqliteTransaction? tx, string store, string applicationId, string transactionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT player_id, created_utc, acked_utc FROM claims WHERE store=$s AND app_id=$a AND transaction_id=$t ORDER BY seq";
        command.Parameters.AddWithValue("$s", store); command.Parameters.AddWithValue("$a", applicationId); command.Parameters.AddWithValue("$t", transactionId);
        using var reader = command.ExecuteReader();
        var list = new List<ClaimInfo>();
        while (reader.Read())
            list.Add(new ClaimInfo(reader.GetString(0), ParseStamp(reader.GetString(1)), reader.IsDBNull(2) ? null : ParseStamp(reader.GetString(2))));
        return list;
    }

    public (long Revision, long CurrentExpiryUtcTicks) Subscription(string playerId, string store, string applicationId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction(deferred: false);
            long aggregate = StoreRules.Aggregate(ClaimedBy(connection, tx, playerId, store, applicationId));
            long revision = 0, expiry = 0;
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = "SELECT revision, expiry_ticks FROM subscriptions WHERE player_id=$p AND store=$s AND app_id=$a";
                command.Parameters.AddWithValue("$p", playerId); command.Parameters.AddWithValue("$s", store); command.Parameters.AddWithValue("$a", applicationId);
                using var reader = command.ExecuteReader();
                if (reader.Read()) { revision = reader.GetInt64(0); expiry = reader.GetInt64(1); }
            }
            if (revision == 0 || expiry != aggregate)
            {
                revision++; expiry = aggregate;
                Execute(connection, tx, """
                    INSERT INTO subscriptions(player_id, store, app_id, revision, expiry_ticks) VALUES($p,$s,$a,$r,$e)
                    ON CONFLICT(player_id, store, app_id) DO UPDATE SET revision=$r, expiry_ticks=$e
                    """, ("$p", playerId), ("$s", store), ("$a", applicationId), ("$r", revision), ("$e", expiry));
            }
            tx.Commit();
            return (revision, expiry);
        }
    }

    public void UpsertSubscriptionRevenueCharge(SubscriptionRevenueCharge charge)
    {
        lock (gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction(deferred: false);
            Execute(connection, tx, """
                INSERT OR IGNORE INTO subscription_revenue_charges
                  (store, app_id, revenue_order_id, transaction_id, player_id, owning_install_id, confirmed, offered_anonymous, created_ms, confirmed_ms)
                VALUES($s,$a,$ord,$t,$p,$inst,$conf,$off,$created,$confMs)
                """,
                ("$s", charge.Store), ("$a", charge.ApplicationId), ("$ord", charge.RevenueOrderId),
                ("$t", charge.TransactionId), ("$p", charge.PlayerId),
                ("$inst", (object?)charge.OwningInstallId ?? DBNull.Value),
                ("$conf", charge.Confirmed ? 1 : 0), ("$off", charge.OfferedToAnonymous ? 1 : 0),
                ("$created", charge.CreatedUtcMs), ("$confMs", (object?)charge.ConfirmedUtcMs ?? DBNull.Value));
            tx.Commit();
        }
    }

    public SubscriptionRevenueCharge? GetSubscriptionRevenueCharge(string store, string applicationId, string revenueOrderId)
    {
        lock (gate)
        {
            using var connection = Open();
            return ReadCharge(connection, null, store, applicationId, revenueOrderId);
        }
    }

    public IReadOnlyList<SubscriptionRevenueCharge> ClaimPendingSubscriptionRevenueCharges(string playerId, string store, string applicationId, string? installId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction(deferred: false);
            var list = new List<SubscriptionRevenueCharge>();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = tx;
                command.CommandText = """
                    SELECT store, app_id, transaction_id, revenue_order_id, player_id, owning_install_id, confirmed, offered_anonymous, created_ms, confirmed_ms
                    FROM subscription_revenue_charges
                    WHERE store=$s AND app_id=$a AND player_id=$p AND confirmed=0
                    ORDER BY revenue_order_id
                    """;
                command.Parameters.AddWithValue("$s", store); command.Parameters.AddWithValue("$a", applicationId); command.Parameters.AddWithValue("$p", playerId);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var c = MapCharge(reader);
                    if (c.OwningInstallId != null)
                    {
                        if (installId != null && installId == c.OwningInstallId) list.Add(c);
                    }
                    else if (!c.OfferedToAnonymous)
                    {
                        list.Add(c with { OfferedToAnonymous = true });
                    }
                }
            }
            foreach (var anon in list.Where(c => c.OwningInstallId == null))
            {
                Execute(connection, tx, """
                    UPDATE subscription_revenue_charges SET offered_anonymous=1
                    WHERE store=$s AND app_id=$a AND revenue_order_id=$ord
                    """, ("$s", store), ("$a", applicationId), ("$ord", anon.RevenueOrderId));
            }
            tx.Commit();
            return list;
        }
    }

    public SubscriptionRevenueConfirmResult ConfirmSubscriptionRevenueCharge(string playerId, string store, string applicationId, string revenueOrderId, string? installId, DateTimeOffset now)
    {
        lock (gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction(deferred: false);
            var c = ReadCharge(connection, tx, store, applicationId, revenueOrderId);
            if (c == null || c.PlayerId != playerId)
            {
                tx.Commit();
                throw new VerifierRefusal(404, "transaction_not_found");
            }
            if (c.OwningInstallId != null)
            {
                if (installId == null || installId != c.OwningInstallId)
                {
                    tx.Commit();
                    return new SubscriptionRevenueConfirmResult { RevenueOrderId = revenueOrderId, Accepted = false, Deliver = false, Status = "ignored_install_mismatch" };
                }
                if (c.Confirmed)
                {
                    tx.Commit();
                    return new SubscriptionRevenueConfirmResult { RevenueOrderId = revenueOrderId, Accepted = true, Deliver = false, Status = "already_confirmed" };
                }
                Execute(connection, tx, """
                    UPDATE subscription_revenue_charges SET confirmed=1, confirmed_ms=$now
                    WHERE store=$s AND app_id=$a AND revenue_order_id=$ord
                    """, ("$now", now.ToUnixTimeMilliseconds()), ("$s", store), ("$a", applicationId), ("$ord", revenueOrderId));
                tx.Commit();
                return new SubscriptionRevenueConfirmResult { RevenueOrderId = revenueOrderId, Accepted = true, Deliver = true, Status = "confirmed" };
            }
            if (c.Confirmed)
            {
                tx.Commit();
                return new SubscriptionRevenueConfirmResult { RevenueOrderId = revenueOrderId, Accepted = false, Deliver = false, Status = "already_confirmed" };
            }
            Execute(connection, tx, """
                UPDATE subscription_revenue_charges SET confirmed=1, offered_anonymous=1, confirmed_ms=$now
                WHERE store=$s AND app_id=$a AND revenue_order_id=$ord
                """, ("$now", now.ToUnixTimeMilliseconds()), ("$s", store), ("$a", applicationId), ("$ord", revenueOrderId));
            tx.Commit();
            return new SubscriptionRevenueConfirmResult { RevenueOrderId = revenueOrderId, Accepted = true, Deliver = true, Status = "confirmed" };
        }
    }

    public bool TryAuthorizeSubscriptionProofOffer(string playerId, string store, string applicationId, string revenueOrderId, string? installId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction(deferred: false);
            var c = ReadCharge(connection, tx, store, applicationId, revenueOrderId);
            if (c == null) { tx.Commit(); return true; }
            if (c.PlayerId != playerId) { tx.Commit(); return false; }
            if (c.OwningInstallId != null)
            {
                tx.Commit();
                return installId != null && installId == c.OwningInstallId;
            }
            if (c.Confirmed || c.OfferedToAnonymous) { tx.Commit(); return false; }
            Execute(connection, tx, """
                UPDATE subscription_revenue_charges SET offered_anonymous=1
                WHERE store=$s AND app_id=$a AND revenue_order_id=$ord
                """, ("$s", store), ("$a", applicationId), ("$ord", revenueOrderId));
            tx.Commit();
            return true;
        }
    }

    public StoreStorageMetrics GetStorageMetrics()
    {
        lock (gate)
        {
            long rows;
            using (var connection = Open())
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM purchases";
                rows = Convert.ToInt64(command.ExecuteScalar());
            }
            long bytes = 0;
            try
            {
                if (File.Exists(databasePath)) bytes += new FileInfo(databasePath).Length;
                if (File.Exists(databasePath + "-wal")) bytes += new FileInfo(databasePath + "-wal").Length;
            }
            catch (IOException) { }
            return new StoreStorageMetrics(rows, bytes);
        }
    }

    private static SubscriptionRevenueCharge? ReadCharge(SqliteConnection connection, SqliteTransaction? tx, string store, string appId, string orderId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = """
            SELECT store, app_id, transaction_id, revenue_order_id, player_id, owning_install_id, confirmed, offered_anonymous, created_ms, confirmed_ms
            FROM subscription_revenue_charges
            WHERE store=$s AND app_id=$a AND revenue_order_id=$ord
            """;
        command.Parameters.AddWithValue("$s", store); command.Parameters.AddWithValue("$a", appId); command.Parameters.AddWithValue("$ord", orderId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? MapCharge(reader) : null;
    }

    private static SubscriptionRevenueCharge MapCharge(SqliteDataReader reader) =>
        new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt64(6) != 0, reader.GetInt64(7) != 0,
            reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetInt64(9));

    private static List<PurchaseRecord> Read(SqliteConnection connection, SqliteTransaction? tx, string where, params (string, object)[] args)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT store, app_id, transaction_id, player_id, product_id, environment, refunded, expiry_ticks, original_ref, signed_payload, signed_date_ms, " +
            "revenue_verified, gross_revenue_micros, revenue_currency, revenue_transaction_id, revenue_purchased_at_ms, revenue_refunded, refunded_revenue_order_ids_json, test_order FROM purchases " + where;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var list = new List<PurchaseRecord>();
        while (reader.Read())
            list.Add(new PurchaseRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt64(6) != 0, reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10), reader.GetInt64(11) != 0,
                reader.GetInt64(12), reader.GetString(13), reader.GetString(14), reader.GetInt64(15), reader.GetInt64(16) != 0,
                reader.GetString(17), reader.GetInt64(18) != 0));
        return list;
    }

    private static void Execute(SqliteConnection connection, SqliteTransaction? tx, string sql, params (string, object)[] args)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        command.ExecuteNonQuery();
    }
}
