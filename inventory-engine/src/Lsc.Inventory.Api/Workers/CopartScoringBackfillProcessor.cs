using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public interface ICopartScoringBackfillProcessor
{
    Task<CopartScoringBackfillResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Scores already-persisted active Copart lots only. It never downloads a snapshot, invokes the
/// global scoring queue, changes eligibility, reconciles lifecycle, resolves media, or reads IAAI.
/// </summary>
public sealed class CopartScoringBackfillProcessor(
    IInventorySnapshotStore snapshotStore,
    IOptions<CopartExcelOptions> options,
    ILogger<CopartScoringBackfillProcessor> logger) : ICopartScoringBackfillProcessor
{
    private readonly CopartExcelOptions _options = options.Value;

    public async Task<CopartScoringBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var batchSize = Math.Clamp(_options.ScoringBackfillBatchSize, 1, 2_000);
        var concurrency = Math.Clamp(_options.ScoringBackfillConcurrency, 1, 16);
        var runId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart("copart-scoring-backfill", InventorySourcePolicy.CopartExcelSource, "scoring", 1, batchSize, startedAt),
            cancellationToken);
        var scanned = 0;
        var scored = 0;
        var scoreSkippedUnchanged = 0;
        var skippedIneligible = 0;
        var failed = 0;
        var failures = new List<string>();

        try
        {
            while (true)
            {
                var batch = await snapshotStore.GetCopartScoringBackfillCandidatesAsync(batchSize, cancellationToken);
                if (batch.Count == 0) break;
                scanned += batch.Count;
                var progressed = 0;

                for (var offset = 0; offset < batch.Count; offset += concurrency)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var window = batch.Skip(offset).Take(concurrency).ToArray();
                    var outcomes = await Task.WhenAll(window.Select(candidate => ScoreCandidateAsync(candidate, cancellationToken)));
                    foreach (var outcome in outcomes)
                    {
                        switch (outcome.Kind)
                        {
                            case ScoringOutcomeKind.Scored:
                                scored++;
                                progressed++;
                                break;
                            case ScoringOutcomeKind.Unchanged:
                                scoreSkippedUnchanged++;
                                progressed++;
                                break;
                            case ScoringOutcomeKind.Ineligible:
                                skippedIneligible++;
                                break;
                            case ScoringOutcomeKind.Failed:
                                failed++;
                                if (!string.IsNullOrWhiteSpace(outcome.Failure)) failures.Add(outcome.Failure);
                                break;
                        }
                    }
                }

                // Avoid a busy loop if candidates cannot be advanced due to a persistent failure.
                if (progressed == 0 || batch.Count < batchSize) break;
            }

            var remaining = (await snapshotStore.GetCopartScoringBackfillCandidatesAsync(1, cancellationToken)).Count;
            var finishedAt = DateTimeOffset.UtcNow;
            var completionFailures = failed == 0
                ? Array.Empty<string>()
                : new[] { $"Copart scoring backfill could not score {failed} candidates; no IAAI, lifecycle, source, or media operations were run." };
            await snapshotStore.CompleteSyncRunAsync(runId,
                new InventorySyncRunCompletion(finishedAt, scanned, scanned, completionFailures), cancellationToken);
            logger.LogInformation("Copart scoring backfill completed: {Scanned} scanned, {Scored} scored, {Unchanged} unchanged, {Ineligible} ineligible, {Failed} failed, {Remaining} remaining.",
                scanned, scored, scoreSkippedUnchanged, skippedIneligible, failed, remaining);
            return new CopartScoringBackfillResult(scanned, scored, scoreSkippedUnchanged, skippedIneligible, failed, remaining, finishedAt - startedAt, failures.Take(20).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var finishedAt = DateTimeOffset.UtcNow;
            failures.Add($"scoring-backfill: {exception.GetType().Name}");
            await snapshotStore.CompleteSyncRunAsync(runId,
                new InventorySyncRunCompletion(finishedAt, scanned, scanned, failures), cancellationToken);
            logger.LogError(exception, "Copart scoring backfill stopped after {Scanned} candidates.", scanned);
            return new CopartScoringBackfillResult(scanned, scored, scoreSkippedUnchanged, skippedIneligible, failed, 1, finishedAt - startedAt, failures.Take(20).ToArray());
        }
    }

    private async Task<ScoringOutcome> ScoreCandidateAsync(StoredVehicleSnapshot candidate, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(candidate.Vehicle.Platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase))
                return new ScoringOutcome(ScoringOutcomeKind.Ineligible, null);
            var eligibility = AuctionEligibilityEvaluator.Evaluate(candidate.Vehicle, candidate.ObservedAt);
            if (!eligibility.LoadToSystem)
                return new ScoringOutcome(ScoringOutcomeKind.Ineligible, null);
            await snapshotStore.PersistScoringResultAsync(candidate.Vehicle, eligibility, candidate.ObservedAt, cancellationToken);
            return new ScoringOutcome(ScoringOutcomeKind.Scored, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new ScoringOutcome(ScoringOutcomeKind.Failed, exception.GetType().Name);
        }
    }

    private enum ScoringOutcomeKind { Scored, Unchanged, Ineligible, Failed }
    private sealed record ScoringOutcome(ScoringOutcomeKind Kind, string? Failure);
}
