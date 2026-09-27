using Microsoft.Data.Sqlite;

namespace Shorepop.Verifier;

public sealed record PurchaseRecord(
    string Store, string ApplicationId, string TransactionId, string PlayerId, string ProductId, string Environment,
    bool Refunded, long PurchasedPeriodExpiryUtcTicks, string? OriginalRef, string? SignedPayload, long SignedDateUnixMs);

/// <summary>
/// Transaction state + player claims + subscription aggregate. Implementations must make <see cref="Bind"/> atomic.
/// One row per (store, applicationId, transactionId) holds the store's state (refunds sticky); a separate claim
/// set records every player that presented it. Consumables (<c>singleOwner</c>) accept only the first player's claim;
/// entitlements (non-consumables, subscriptions) accept every authenticated player that presents a valid transaction.
/// A Postgres implementation (env DATABASE_URL) would slot in here; only SQLite and memory ship today.
/// </summary>
public interface IPurchaseStore
{
    /// <summary>
    /// Inserts or refreshes the row. When <paramref name="singleOwner"/> and another player already claimed the
    /// transaction, nothing is written and that player is returned; otherwise the claim is recorded and record.PlayerId returned.
    /// </summary>
    string Bind(PurchaseRecord record, bool singleOwner);
    /// <summary>Rows this player has claimed; <see cref="PurchaseRecord.PlayerId"/> is the requesting player.</summary>
    IReadOnlyList<PurchaseRecord> List(string playerId, string store, string applicationId);
    /// <summary>Every player that claimed the transaction, first claimant first.</summary>
    IReadOnlyList<string> Claimants(string store, string applicationId, string transactionId);
    /// <summary>Recomputes the player's aggregate subscription expiry; the revision increases whenever it changes.</summary>
    (long Revision, long CurrentExpiryUtcTicks) Subscription(string playerId, string store, string applicationId);
    string Kind { get; }
}

internal static class StoreRules
{
    /// <summary>Refunds are sticky; expiry and payload follow the most recently signed evidence.</summary>
    public static PurchaseRecord Merge(PurchaseRecord existing, PurchaseRecord incoming)
    {
        bool newer = incoming.SignedDateUnixMs >= existing.SignedDateUnixMs;
        return existing with
        {
            Refunded = existing.Refunded || incoming.Refunded,
            PurchasedPeriodExpiryUtcTicks = newer ? incoming.PurchasedPeriodExpiryUtcTicks : existing.PurchasedPeriodExpiryUtcTicks,
            SignedPayload = newer && incoming.SignedPayload != null ? incoming.SignedPayload : existing.SignedPayload,
            SignedDateUnixMs = Math.Max(existing.SignedDateUnixMs, incoming.SignedDateUnixMs),
            Environment = incoming.Environment,
            OriginalRef = incoming.OriginalRef ?? existing.OriginalRef,
        };
    }

    public static long Aggregate(IEnumerable<PurchaseRecord> rows) => rows
        .Where(r => ShorepopCatalog.IsSubscription(r.ProductId) && !r.Refunded)
        .Select(r => r.PurchasedPeriodExpiryUtcTicks).DefaultIfEmpty(0).Max();
}

public sealed class InMemoryPurchaseStore : IPurchaseStore
{
    private readonly object gate = new();
    private readonly Dictionary<(string, string, string), PurchaseRecord> rows = new();
    private readonly Dictionary<(string, string, string), List<string>> claims = new();
    private readonly Dictionary<(string, string, string), (long, long)> subscriptions = new();
    public string Kind => "memory";

    public string Bind(PurchaseRecord record, bool singleOwner)
    {
        lock (gate)
        {
            var key = (record.Store, record.ApplicationId, record.TransactionId);
            if (rows.TryGetValue(key, out var existing))
            {
                var players = claims[key];
                if (singleOwner && players[0] != record.PlayerId) return players[0];
                rows[key] = StoreRules.Merge(existing, record);
                if (!players.Contains(record.PlayerId)) players.Add(record.PlayerId);
                return record.PlayerId;
            }
            rows[key] = record;
            claims[key] = [record.PlayerId];
            return record.PlayerId;
        }
    }

    public IReadOnlyList<PurchaseRecord> List(string playerId, string store, string applicationId)
    {
        lock (gate) return rows.Where(r => r.Key.Item1 == store && r.Key.Item2 == applicationId && claims[r.Key].Contains(playerId))
            .Select(r => r.Value with { PlayerId = playerId })
            .OrderBy(r => r.TransactionId, StringComparer.Ordinal).ToList();
    }

    public IReadOnlyList<string> Claimants(string store, string applicationId, string transactionId)
    {
        lock (gate) return claims.TryGetValue((store, applicationId, transactionId), out var players) ? players.ToList() : [];
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
}

/// <summary>
/// SQLite on the container disk. On Render's free plan the disk is ephemeral: the file (and therefore
/// every transaction-to-player claim) resets on each deploy/restart. Attach a persistent disk
/// (paid plan) or implement the Postgres store before real revenue depends on replay protection.
/// </summary>
public sealed class SqlitePurchaseStore : IPurchaseStore
{
    private readonly string connectionString;
    private readonly object gate = new();
    public string Kind => "sqlite";

    public SqlitePurchaseStore(string path)
    {
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));
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
            """);
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        Execute(connection, null, "PRAGMA busy_timeout=3000;");
        return connection;
    }

    public string Bind(PurchaseRecord record, bool singleOwner)
    {
        lock (gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction(deferred: false);
            var existing = Read(connection, tx, "WHERE store=$s AND app_id=$a AND transaction_id=$t",
                ("$s", record.Store), ("$a", record.ApplicationId), ("$t", record.TransactionId)).FirstOrDefault();
            if (existing != null && singleOwner)
            {
                string first = ClaimantList(connection, tx, record.Store, record.ApplicationId, record.TransactionId).FirstOrDefault() ?? existing.PlayerId;
                if (first != record.PlayerId) { tx.Commit(); return first; }
            }
            var row = existing == null ? record : StoreRules.Merge(existing, record);
            string now = DateTime.UtcNow.ToString("O");
            Execute(connection, tx, """
                INSERT INTO purchases(store, app_id, transaction_id, player_id, product_id, environment, refunded, expiry_ticks,
                  original_ref, signed_payload, signed_date_ms, created_utc, updated_utc)
                VALUES($s,$a,$t,$p,$prod,$env,$ref,$exp,$orig,$payload,$sd,$now,$now)
                ON CONFLICT(store, app_id, transaction_id) DO UPDATE SET environment=$env, refunded=$ref, expiry_ticks=$exp,
                  original_ref=$orig, signed_payload=$payload, signed_date_ms=$sd, updated_utc=$now
                """,
                ("$s", row.Store), ("$a", row.ApplicationId), ("$t", row.TransactionId), ("$p", row.PlayerId), ("$prod", row.ProductId),
                ("$env", row.Environment), ("$ref", row.Refunded ? 1 : 0), ("$exp", row.PurchasedPeriodExpiryUtcTicks),
                ("$orig", (object?)row.OriginalRef ?? DBNull.Value), ("$payload", (object?)row.SignedPayload ?? DBNull.Value),
                ("$sd", row.SignedDateUnixMs), ("$now", now));
            Execute(connection, tx, "INSERT OR IGNORE INTO claims(store, app_id, transaction_id, player_id, created_utc) VALUES($s,$a,$t,$p,$now)",
                ("$s", row.Store), ("$a", row.ApplicationId), ("$t", row.TransactionId), ("$p", record.PlayerId), ("$now", now));
            tx.Commit();
            return record.PlayerId;
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

    public IReadOnlyList<string> Claimants(string store, string applicationId, string transactionId)
    {
        lock (gate)
        {
            using var connection = Open();
            return ClaimantList(connection, null, store, applicationId, transactionId);
        }
    }

    private static List<PurchaseRecord> ClaimedBy(SqliteConnection connection, SqliteTransaction? tx, string playerId, string store, string applicationId) =>
        Read(connection, tx, "WHERE store=$s AND app_id=$a AND EXISTS(SELECT 1 FROM claims c WHERE c.store=purchases.store AND " +
            "c.app_id=purchases.app_id AND c.transaction_id=purchases.transaction_id AND c.player_id=$p) ORDER BY transaction_id",
            ("$p", playerId), ("$s", store), ("$a", applicationId))
            .Select(r => r with { PlayerId = playerId }).ToList();

    private static List<string> ClaimantList(SqliteConnection connection, SqliteTransaction? tx, string store, string applicationId, string transactionId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT player_id FROM claims WHERE store=$s AND app_id=$a AND transaction_id=$t ORDER BY seq";
        command.Parameters.AddWithValue("$s", store); command.Parameters.AddWithValue("$a", applicationId); command.Parameters.AddWithValue("$t", transactionId);
        using var reader = command.ExecuteReader();
        var list = new List<string>();
        while (reader.Read()) list.Add(reader.GetString(0));
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

    private static List<PurchaseRecord> Read(SqliteConnection connection, SqliteTransaction? tx, string where, params (string, object)[] args)
    {
        using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = "SELECT store, app_id, transaction_id, player_id, product_id, environment, refunded, expiry_ticks, original_ref, signed_payload, signed_date_ms FROM purchases " + where;
        foreach (var (name, value) in args) command.Parameters.AddWithValue(name, value);
        using var reader = command.ExecuteReader();
        var list = new List<PurchaseRecord>();
        while (reader.Read())
            list.Add(new PurchaseRecord(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                reader.GetString(5), reader.GetInt64(6) != 0, reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9), reader.GetInt64(10)));
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
