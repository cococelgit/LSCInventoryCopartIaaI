using Lsc.Inventory.Api.Storage;

namespace Lsc.Inventory.Api.Workers;

public sealed class SearchProjectionWarmupWorker(
    IInventorySnapshotStore store,
    ILogger<SearchProjectionWarmupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var current = await store.GetSearchProjectionStatusAsync(stoppingToken);
            if (current.Ready)
            {
                logger.LogInformation("Search projection already ready. Rows={Rows}", current.Rows);
                return;
            }
            var status = await store.RebuildSearchProjectionAsync(stoppingToken);
            logger.LogInformation(
                "Search projection ready. Rows={Rows} DurationMs={DurationMs}",
                status.Rows,
                status.Duration.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Search projection warmup failed; legacy search remains available.");
        }
    }
}
