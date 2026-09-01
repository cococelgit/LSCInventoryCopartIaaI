using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public interface ICopartTitleBackfillProcessor
{
    Task<CopartTitleBackfillResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Persists approved Copart title descriptions for existing Copart lots only.
/// This worker never evaluates eligibility, downloads a snapshot, reconciles lifecycle, resolves media, or reads IAAI.
/// </summary>
public sealed class CopartTitleBackfillProcessor(
    IInventorySnapshotStore snapshotStore,
    IOptions<CopartExcelOptions> options,
    ILogger<CopartTitleBackfillProcessor> logger) : ICopartTitleBackfillProcessor
{
    private readonly CopartExcelOptions _options = options.Value;

    public async Task<CopartTitleBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var batchSize = Math.Clamp(_options.TitleBackfillBatchSize, 1, 10_000);
        var concurrency = Math.Clamp(_options.TitleBackfillConcurrency, 1, 32);
        var runId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart("copart-title-backfill", InventorySourcePolicy.CopartExcelSource, "title-mapping", 1, batchSize, startedAt),
            cancellationToken);
        var candidates = 0;
        var mapped = 0;
        var unmapped = 0;
        var skipped = 0;
        var failed = 0;
        var failures = new List<string>();

        try
        {
            while (true)
            {
                var batch = await snapshotStore.GetCopartTitleMappingCandidatesAsync(batchSize, cancellationToken);
                if (batch.Count == 0) break;
                var updatedThisBatch = 0;
                candidates += batch.Count;

                for (var offset = 0; offset < batch.Count; offset += concurrency)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var window = batch.Skip(offset).Take(concurrency).ToArray();
                    var outcomes = await Task.WhenAll(window.Select(candidate => MapCandidateAsync(candidate, cancellationToken)));
                    foreach (var outcome in outcomes)
                    {
                        switch (outcome.Kind)
                        {
                            case TitleOutcomeKind.Mapped:
                                mapped++;
                                updatedThisBatch++;
                                break;
                            case TitleOutcomeKind.Unmapped:
                                unmapped++;
                                updatedThisBatch++;
                                break;
                            case TitleOutcomeKind.Skipped:
                                skipped++;
                                break;
                            case TitleOutcomeKind.Failed:
                                failed++;
                                if (!string.IsNullOrWhiteSpace(outcome.Failure)) failures.Add(outcome.Failure);
                                break;
                        }
                    }
                }

                // If every remaining row was concurrently updated or failed, stop instead of looping indefinitely.
                if (updatedThisBatch == 0 || batch.Count < batchSize) break;
            }

            var finishedAt = DateTimeOffset.UtcNow;
            var completionFailures = failed == 0
                ? Array.Empty<string>()
                : new[] { $"Copart title backfill could not update {failed} candidates; no eligibility or lifecycle changes were made." };
            await snapshotStore.CompleteSyncRunAsync(runId,
                new InventorySyncRunCompletion(finishedAt, candidates, candidates, completionFailures), cancellationToken);
            logger.LogInformation("Copart title backfill completed: {Candidates} candidates, {Mapped} mapped, {Unmapped} unmapped, {Skipped} stale, {Failed} failed.", candidates, mapped, unmapped, skipped, failed);
            return new CopartTitleBackfillResult(true, candidates, mapped, unmapped, skipped, failed, finishedAt - startedAt, failures.Take(20).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var finishedAt = DateTimeOffset.UtcNow;
            failures.Add($"title-backfill: {exception.Message}");
            await snapshotStore.CompleteSyncRunAsync(runId,
                new InventorySyncRunCompletion(finishedAt, candidates, candidates, failures), cancellationToken);
            logger.LogError(exception, "Copart title backfill stopped after {Candidates} candidates.", candidates);
            return new CopartTitleBackfillResult(false, candidates, mapped, unmapped, skipped, failed, finishedAt - startedAt, failures);
        }
    }

    private async Task<TitleOutcome> MapCandidateAsync(StoredVehicleSnapshot candidate, CancellationToken cancellationToken)
    {
        try
        {
            var mappedVehicle = CopartTitleMapper.ApplyTaxonomy(CopartTitleMapper.Apply(candidate.Vehicle));
            var mappingStatus = mappedVehicle.AdditionalData is not null &&
                mappedVehicle.AdditionalData.TryGetValue("source_title_mapping", out var status) &&
                status.ValueKind == System.Text.Json.JsonValueKind.String
                ? status.GetString()
                : null;
            var updated = await snapshotStore.UpdateCopartTitleMappingAsync(candidate.Identity, candidate.ObservedAt, mappedVehicle, cancellationToken);
            if (!updated) return new TitleOutcome(TitleOutcomeKind.Skipped, null);
            return new TitleOutcome(string.Equals(mappingStatus, "mapped", StringComparison.OrdinalIgnoreCase) ? TitleOutcomeKind.Mapped : TitleOutcomeKind.Unmapped, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new TitleOutcome(TitleOutcomeKind.Failed, exception.GetType().Name);
        }
    }

    private enum TitleOutcomeKind { Mapped, Unmapped, Skipped, Failed }
    private sealed record TitleOutcome(TitleOutcomeKind Kind, string? Failure);
}
