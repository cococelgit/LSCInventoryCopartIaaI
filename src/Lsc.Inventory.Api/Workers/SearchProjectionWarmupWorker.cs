using Lsc.Inventory.Api.Storage;

using Lsc.Inventory.Api.Options;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed class SearchProjectionWarmupWorker(
    IInventorySnapshotStore store,
    IOptions<InventoryV2Options> inventoryV2,
    ILogger<SearchProjectionWarmupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            if (!inventoryV2.Value.LegacyReadFallbackEnabled)
            {
                logger.LogInformation("Search projection warmup skipped because the V1 fallback is disabled; Inventory V2 is authoritative.");
                return;
            }
            var current = await store.GetSearchProjectionStatusAsync(stoppingToken);
            if (current.Ready && current.SchemaVersion >= 3)
            {
                logger.LogInformation("Search projection already ready. Rows={Rows} SchemaVersion={SchemaVersion}", current.Rows, current.SchemaVersion);
                return;
            }
            if (current.Ready && current.SchemaVersion < 3)
                logger.LogInformation("Search projection schema is stale. Rebuilding for Seller facets. CurrentSchemaVersion={SchemaVersion}", current.SchemaVersion);
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
            logger.LogError(exception, "Search projection warmup failed.");
        }
    }
}
