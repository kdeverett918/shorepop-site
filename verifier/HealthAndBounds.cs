using System.Collections.Concurrent;

namespace Shorepop.Verifier;

/// <summary>
/// Tracks background side-job health separately from /healthz (WO-20261118-396).
/// A failed retention, export or backup job must raise an alert and surface on /status,
/// never fail the routing health check that serves player purchases.
/// </summary>
public sealed class SideJobStatusTracker
{
    private readonly object gate = new();
    private readonly Dictionary<string, JobState> jobs = new(StringComparer.Ordinal);

    private sealed class JobState
    {
        public string Status = "idle";
        public long LastRunUnixMs;
        public long LastSuccessUnixMs;
        public long LastFailureUnixMs;
        public int ConsecutiveFailures;
        public string LastErrorCode = "";
    }

    public void RecordSuccess(string jobName, DateTimeOffset now)
    {
        lock (gate)
        {
            var state = GetOrCreate(jobName);
            long ms = now.ToUnixTimeMilliseconds();
            state.Status = "ok";
            state.LastRunUnixMs = ms;
            state.LastSuccessUnixMs = ms;
            state.ConsecutiveFailures = 0;
            state.LastErrorCode = "";
        }
    }

    public void RecordFailure(string jobName, string errorCode, DateTimeOffset now)
    {
        lock (gate)
        {
            var state = GetOrCreate(jobName);
            long ms = now.ToUnixTimeMilliseconds();
            state.Status = "degraded";
            state.LastRunUnixMs = ms;
            state.LastFailureUnixMs = ms;
            state.ConsecutiveFailures++;
            state.LastErrorCode = errorCode;
        }
    }

    public IReadOnlyDictionary<string, SideJobSnapshot> Snapshot()
    {
        lock (gate)
        {
            return jobs.ToDictionary(
                kv => kv.Key,
                kv => new SideJobSnapshot(kv.Value.Status, kv.Value.LastRunUnixMs, kv.Value.LastSuccessUnixMs,
                    kv.Value.LastFailureUnixMs, kv.Value.ConsecutiveFailures, kv.Value.LastErrorCode),
                StringComparer.Ordinal);
        }
    }

    private JobState GetOrCreate(string jobName)
    {
        if (!jobs.TryGetValue(jobName, out var state)) jobs[jobName] = state = new JobState();
        return state;
    }
}

public sealed record SideJobSnapshot(
    string Status, long LastRunUnixMs, long LastSuccessUnixMs, long LastFailureUnixMs,
    int ConsecutiveFailures, string LastErrorCode);

public sealed record BoundWarning(
    string Name, long Current, long Limit, int Percent, long RecordedAtUnixMs);

public sealed record StoreStorageMetrics(long PurchaseRows, long SqliteFileBytes);

/// <summary>
/// Watches size and count bounds that a growing purchase store or request payload can approach
/// (WO-20261118-396). Logs a warning and exposes active alerts on /status once usage crosses
/// <see cref="WarningFraction"/> (70%), without ever failing /healthz or blocking purchases.
/// </summary>
public sealed class GrowingBoundsMonitor(ILogger<GrowingBoundsMonitor>? log = null)
{
    public const double WarningFraction = 0.70;
    public const long MaxRequestBodyBytes = 64 * 1024;
    public const long MaxStoreApiResponseBytes = 1024 * 1024;
    public const long SoftLedgerRowsLimit = 1_000_000;
    public const long SoftSqliteBytesLimit = 256L * 1024 * 1024;

    private readonly ConcurrentDictionary<string, BoundWarning> activeWarnings = new(StringComparer.Ordinal);

    public bool Observe(string name, long current, long limit, DateTimeOffset? now = null)
    {
        if (limit <= 0 || current < 0) return false;
        double ratio = (double)current / limit;
        if (ratio < WarningFraction)
        {
            activeWarnings.TryRemove(name, out _);
            return false;
        }
        int pct = (int)Math.Min(999, Math.Round(ratio * 100.0, MidpointRounding.AwayFromZero));
        var warning = new BoundWarning(name, current, limit, pct, (now ?? DateTimeOffset.UtcNow).ToUnixTimeMilliseconds());
        bool newlyWarned = !activeWarnings.ContainsKey(name);
        activeWarnings[name] = warning;
        if (newlyWarned)
            log?.LogWarning("bound_warning name={Bound} current={Current} limit={Limit} pct={Percent}", name, current, limit, pct);
        return true;
    }

    public void ObserveStorage(StoreStorageMetrics metrics, DateTimeOffset? now = null)
    {
        Observe("ledger_purchase_rows", metrics.PurchaseRows, SoftLedgerRowsLimit, now);
        if (metrics.SqliteFileBytes > 0)
            Observe("sqlite_ledger_bytes", metrics.SqliteFileBytes, SoftSqliteBytesLimit, now);
    }

    public IReadOnlyList<BoundWarning> Warnings =>
        activeWarnings.Values.OrderBy(w => w.Name, StringComparer.Ordinal).ToList();
}

/// <summary>
/// Isolated routing readiness probe for /healthz (WO-20261118-396). Checks ONLY verifier configuration
/// and canonical settlement ledger reachability — never side jobs, proof caches, or bound warnings.
/// </summary>
public static class VerifierHealth
{
    public static async Task<(bool Available, string Storage)> CheckRoutingReadinessAsync(
        bool configured, IPurchaseStore? store, CancellationToken cancel)
    {
        if (!configured) return (false, "unconfigured");
        if (store == null) return (false, "unavailable");
        if (store is PostgresPurchaseStore postgres)
        {
            await postgres.CheckReadinessAsync(cancel);
            return (true, "reachable");
        }
        if (store is SqlitePurchaseStore sqlite)
        {
            sqlite.CheckReadiness();
            return (true, "reachable");
        }
        return (true, "in_memory");
    }
}
