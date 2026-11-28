using SentiX.Ingestion;

namespace SentiX.Host;

public sealed class DatabaseStartupInitializer(
    IMarketEventPipeline pipeline,
    ILogger<DatabaseStartupInitializer> logger) : IHostedService
{
    private static readonly TimeSpan StartupMigrationTimeout = TimeSpan.FromSeconds(10);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!pipeline.IsConfigured)
        {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(StartupMigrationTimeout);

        try
        {
            await pipeline.EnsureReadyAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Startup PostgreSQL schema readiness check did not complete ({ErrorType}). Collectors will retry when reachable.",
                ex.GetType().Name);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
