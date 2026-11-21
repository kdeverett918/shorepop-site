namespace Shorepop.Verifier;

/// <summary>
/// Remove encrypted attribution proof bytes after their fixed recovery window.
/// Failures are recorded in <see cref="SideJobStatusTracker"/> (exposed on /status) and logged as alerts;
/// they never affect /healthz purchase routing (WO-20261118-396).
/// </summary>
public sealed class RevenueProofRetentionService(Func<RevenueProofStore?> currentStore,
    ILogger<RevenueProofRetentionService> log, SideJobStatusTracker? sideJobs = null) : BackgroundService
{
    public const string JobName = "revenue_proof_retention";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
        do
        {
            RunOnce(DateTimeOffset.UtcNow);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public bool RunOnce(DateTimeOffset now)
    {
        var store = currentStore();
        if (store == null) return true;
        try
        {
            store.PurgeExpired(now);
            sideJobs?.RecordSuccess(JobName, now);
            return true;
        }
        catch (Exception error) when (error is Npgsql.NpgsqlException or TimeoutException or InvalidOperationException)
        {
            // Do not log exception text, SQL, connection details or proof material.
            sideJobs?.RecordFailure(JobName, "retention_purge_failed", now);
            log.LogWarning("side_job_alert job={Job} code=retention_purge_failed: expired revenue proof cleanup will retry", JobName);
            return false;
        }
    }
}
