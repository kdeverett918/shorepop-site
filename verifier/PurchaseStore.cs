using Microsoft.Data.Sqlite;

namespace Shorepop.Verifier;

public sealed record PurchaseRecord(
    string Store, string ApplicationId, string TransactionId, string PlayerId, string ProductId, string Environment,
    bool Refunded, long PurchasedPeriodExpiryUtcTicks, string? OriginalRef, string? SignedPayload, long SignedDateUnixMs);

/// <summary>
/// Transaction ownership + subscription aggregate. Implementations must make <see cref="Bind"/> atomic:
/// the first player to bind a (store, applicationId, transactionId) owns it forever.
/// A Postgres implementation (env DATABASE_URL) would slot in here; only SQLite and memory ship today.
/// </summary>
public interface IPurchaseStore
{
    /// <summary>Inserts or refreshes the row and returns the owning player (which may differ from record.PlayerId).</summary>
    string Bind(PurchaseRecord record);
    IReadOnlyList<PurchaseRecord> List(string playerId, string store, string applicationId);
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
    private readonly Dictionary<(string, string, string), (long, long)> subscriptions = new();
    public string Kind => "memory";

    public string Bind(PurchaseRecord record)
    {
        lock (gate)
        {
            var key = (record.Store, record.ApplicationId, record.TransactionId);
            if (rows.TryGetValue(key, out var existing))
            {
                if (existing.PlayerId != record.PlayerId) return existing.PlayerId;
                rows[key] = StoreRules.Merge(existing, record);
                return record.PlayerId;
            }
            rows[key] = record;
            return record.PlayerId;
        }
    }

    public IReadOnlyList<PurchaseRecord> List(string playerId, string store, string applicationId)
    {
        lock (gate) return rows.Values.Where(r => r.PlayerId == playerId && r.Store == store && r.ApplicationId == applicationId)
            .OrderBy(r => r.TransactionId, StringComparer.Ordinal).ToList();
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
/// every transaction-to-player binding) resets on each deploy/restart. Attach a persistent disk
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

    public string Bind(PurchaseRecord record)
    {
        lock (gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction(deferred: false);
            var existing = Read(connection, tx, "WHERE store=$s AND app_id=$a AND transaction_id=$t",
                ("$s", record.Store), ("$a", record.ApplicationId), ("$t", record.TransactionId)).FirstOrDefault();
            if (existing != null && existing.PlayerId != record.PlayerId) { tx.Commit(); return existing.PlayerId; }
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
            tx.Commit();
            return record.PlayerId;
        }
    }

    public IReadOnlyList<PurchaseRecord> List(string playerId, string store, string applicationId)
    {
        lock (gate)
        {
            using var connection = Open();
            return Read(connection, null, "WHERE player_id=$p AND store=$s AND app_id=$a ORDER BY transaction_id",
                ("$p", playerId), ("$s", store), ("$a", applicationId));
        }
    }

    public (long Revision, long CurrentExpiryUtcTicks) Subscription(string playerId, string store, string applicationId)
    {
        lock (gate)
        {
            using var connection = Open();
            using var tx = connection.BeginTransaction(deferred: false);
            long aggregate = StoreRules.Aggregate(Read(connection, tx, "WHERE player_id=$p AND store=$s AND app_id=$a",
                ("$p", playerId), ("$s", store), ("$a", applicationId)));
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
