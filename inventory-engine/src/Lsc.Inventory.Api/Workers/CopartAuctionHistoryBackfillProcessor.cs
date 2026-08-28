using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public interface ICopartAuctionHistoryBackfillProcessor
{
    Task<CopartAuctionHistoryBackfillResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Seeds Copart auction-history evidence from existing Copart lot versions only.
/// It does not download data, change a vehicle, evaluate eligibility, reconcile lifecycle,
/// resolve media, query Apibara, or process IAAI.
/// </summary>
public sealed class CopartAuctionHistoryBackfillProcessor(
    IInventorySnapshotStore snapshotStore,
    IOptions<CopartExcelOptions> options,
    ILogger<CopartAuctionHistoryBackfillProcessor> logger) : ICopartAuctionHistoryBackfillProcessor
{
    private readonly CopartExcelOptions _options = options.Value;

    public async Task<CopartAuctionHistoryBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var batchSize = Math.Clamp(_options.AuctionHistoryBackfillBatchSize, 1, 250_000);
        var runId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart("copart-auction-history-backfill", InventorySourcePolicy.CopartExcelSource, "legacy-versions", 1, batchSize, startedAt),
            cancellationToken);
        try
        {
            var result = await snapshotStore.BackfillCopartAuctionObservationsAsync(batchSize, cancellationToken);
            var finishedAt = DateTimeOffset.UtcNow;
            var failures = result.Failed == 0
                ? Array.Empty<string>()
                : new[] { $"Copart auction history could not convert {result.Failed} legacy versions; no inventory, eligibility, lifecycle or media data was changed." };
            await snapshotStore.CompleteSyncRunAsync(
                runId,
                new InventorySyncRunCompletion(finishedAt, result.Candidates, 1, failures),
                cancellationToken);
            logger.LogInformation("Copart auction-history backfill completed: {Candidates} candidates, {Observations} observations, {Failed} failed.", result.Candidates, result.ObservationsInserted, result.Failed);
            return result with { Duration = finishedAt - startedAt };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var finishedAt = DateTimeOffset.UtcNow;
            var failures = new[] { $"auction-history-backfill: {exception.Message}" };
            await snapshotStore.CompleteSyncRunAsync(
                runId,
                new InventorySyncRunCompletion(finishedAt, 0, 1, failures),
                cancellationToken);
            logger.LogError(exception, "Copart auction-history backfill failed before completion.");
            return new CopartAuctionHistoryBackfillResult(false, 0, 0, 0, 1, finishedAt - startedAt, failures);
        }
    }
}
