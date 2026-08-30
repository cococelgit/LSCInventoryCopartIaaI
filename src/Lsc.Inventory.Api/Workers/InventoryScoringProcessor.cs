using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record InventoryScoringRunResult(
    InventoryScoringBackfillResult Backfill,
    int Batches,
    int Claimed,
    int Completed,
    int Failed,
    int Skipped,
    int Remaining);

public interface IInventoryScoringProcessor
{
    Task<InventoryScoringRunResult> RunBackfillAsync(int? maximum, CancellationToken cancellationToken);
    Task<InventoryScoringBatchResult> ProcessBatchAsync(int? maximum, CancellationToken cancellationToken);
}

public sealed class InventoryScoringProcessor(
    IInventorySnapshotStore snapshotStore,
    IOptions<ScoringOptions> options) : IInventoryScoringProcessor
{
    private readonly ScoringOptions _options = options.Value;

    public async Task<InventoryScoringRunResult> RunBackfillAsync(int? maximum, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maximum ?? _options.BackfillMaximumLots, 1, _options.BackfillMaximumLots);
        var backfill = await snapshotStore.EnqueueScoringBackfillAsync(limit, cancellationToken);
        var batches = 0;
        var claimed = 0;
        var completed = 0;
        var failed = 0;
        var skipped = 0;
        var remaining = 0;
        var target = backfill.Enqueued;
        while (target > 0 && completed + failed + skipped < target)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await snapshotStore.ProcessScoringBatchAsync(Math.Min(_options.BatchSize, target - (completed + failed + skipped)), cancellationToken);
            batches++;
            claimed += batch.Claimed;
            completed += batch.Completed;
            failed += batch.Failed;
            skipped += batch.Skipped;
            remaining = batch.Remaining;
            if (batch.Claimed == 0) break;
        }
        return new InventoryScoringRunResult(backfill, batches, claimed, completed, failed, skipped, remaining);
    }

    public Task<InventoryScoringBatchResult> ProcessBatchAsync(int? maximum, CancellationToken cancellationToken) =>
        snapshotStore.ProcessScoringBatchAsync(Math.Clamp(maximum ?? _options.BatchSize, 1, _options.BatchSize), cancellationToken);
}
