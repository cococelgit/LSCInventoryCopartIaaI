using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record InventoryScoringRunResult(
    Guid RunId,
    InventoryScoringBackfillResult Backfill,
    int Batches,
    int Claimed,
    int Completed,
    int Failed,
    int Skipped,
    int Remaining,
    int HighPriorityClaimed,
    int BackfillClaimed);

public interface IInventoryScoringProcessor
{
    Task<InventoryScoringRunResult> RunBackfillAsync(int? maximum, CancellationToken cancellationToken, string trigger = "manual-api");
    Task<InventoryScoringBatchResult> ProcessBatchAsync(int? maximum, CancellationToken cancellationToken);
}

public sealed class InventoryScoringProcessor(
    IInventorySnapshotStore snapshotStore,
    IOptions<ScoringOptions> options) : IInventoryScoringProcessor
{
    private readonly ScoringOptions _options = options.Value;

    public async Task<InventoryScoringRunResult> RunBackfillAsync(int? maximum, CancellationToken cancellationToken, string trigger = "manual-api")
    {
        var runId = await snapshotStore.StartScoringRunAsync(trigger, cancellationToken);
        var limit = Math.Clamp(maximum ?? _options.BackfillMaximumLots, 1, _options.BackfillMaximumLots);
        InventoryScoringBackfillResult backfill = new(0, 0, 0);
        var batches = 0;
        var claimed = 0;
        var completed = 0;
        var failed = 0;
        var skipped = 0;
        var remaining = 0;
        var highPriorityClaimed = 0;
        var backfillClaimed = 0;
        try
        {
            backfill = await snapshotStore.EnqueueScoringBackfillAsync(limit, cancellationToken);
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
                highPriorityClaimed += batch.HighPriorityClaimed;
                backfillClaimed += batch.BackfillClaimed;
                if (batch.Claimed == 0) break;
            }
            var status = failed > 0 ? "completed_with_errors" : "completed";
            await snapshotStore.CompleteScoringRunAsync(runId, new InventoryScoringRunCompletion(
                status, DateTimeOffset.UtcNow, backfill.Requested, backfill.Enqueued, claimed, completed, failed,
                skipped, remaining, highPriorityClaimed, backfillClaimed), cancellationToken);
            return new InventoryScoringRunResult(
                runId, backfill, batches, claimed, completed, failed, skipped, remaining, highPriorityClaimed, backfillClaimed);
        }
        catch (Exception exception)
        {
            await snapshotStore.CompleteScoringRunAsync(runId, new InventoryScoringRunCompletion(
                "failed", DateTimeOffset.UtcNow, backfill.Requested, backfill.Enqueued, claimed, completed, failed,
                skipped, remaining, highPriorityClaimed, backfillClaimed, exception.Message), CancellationToken.None);
            throw;
        }
    }

    public Task<InventoryScoringBatchResult> ProcessBatchAsync(int? maximum, CancellationToken cancellationToken) =>
        snapshotStore.ProcessScoringBatchAsync(Math.Clamp(maximum ?? _options.BatchSize, 1, _options.BatchSize), cancellationToken);
}
