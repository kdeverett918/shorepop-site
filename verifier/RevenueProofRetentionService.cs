namespace Shorepop.Verifier;

/// <summary>Remove encrypted attribution proof bytes after their fixed recovery window.</summary>
public sealed class RevenueProofRetentionService(Func<RevenueProofStore?> currentStore,
    ILogger<RevenueProofRetentionService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
        do
        {
            var store = currentStore();
            if (store != null)
            {
                try { store.PurgeExpired(DateTimeOffset.UtcNow); }
                catch (Exception error) when (error is Npgsql.NpgsqlException or TimeoutException)
                {
                    // Do not log exception text, SQL, connection details or proof material.
                    log.LogWarning("expired revenue proof cleanup will retry");
                }
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
