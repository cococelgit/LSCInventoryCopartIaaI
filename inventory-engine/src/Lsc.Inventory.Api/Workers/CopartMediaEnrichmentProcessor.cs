using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public interface ICopartMediaEnrichmentProcessor
{
    Task<CopartMediaEnrichmentResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Enriches only already-persisted active Copart listings that still have a list thumbnail.
/// It never affects eligibility, sale timestamps, reconciliation, or IAAI inventory.
/// </summary>
public sealed class CopartMediaEnrichmentProcessor(
    IInventorySnapshotStore snapshotStore,
    ICopartMediaResolver mediaResolver,
    IOptions<CopartExcelOptions> options,
    ILogger<CopartMediaEnrichmentProcessor> logger) : ICopartMediaEnrichmentProcessor
{
    private readonly CopartExcelOptions _options = options.Value;

    public async Task<CopartMediaEnrichmentResult> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var runId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart("copart-media", InventorySourcePolicy.CopartExcelSource, "media-enrichment", 1, _options.MediaEnrichmentBatchSize, startedAt),
            cancellationToken);
        var candidates = await snapshotStore.GetCopartMediaCandidatesAsync(_options.MediaEnrichmentBatchSize, cancellationToken);
        var resolved = 0;
        var alreadyComplete = 0;
        var failed = 0;
        var failures = new List<string>();
        var concurrency = Math.Clamp(_options.MediaResolutionConcurrency, 1, 32);

        try
        {
            for (var offset = 0; offset < candidates.Count; offset += concurrency)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var window = candidates.Skip(offset).Take(concurrency).ToArray();
                var outcomes = await Task.WhenAll(window.Select(candidate => ResolveCandidateAsync(candidate, cancellationToken)));
                foreach (var outcome in outcomes)
                {
                    switch (outcome.Kind)
                    {
                        case MediaOutcomeKind.Resolved:
                            resolved++;
                            break;
                        case MediaOutcomeKind.AlreadyUpdated:
                            alreadyComplete++;
                            break;
                        case MediaOutcomeKind.Failed:
                            failed++;
                            if (!string.IsNullOrWhiteSpace(outcome.Failure)) failures.Add(outcome.Failure);
                            break;
                    }
                }
            }

            var finishedAt = DateTimeOffset.UtcNow;
            var completionFailures = failed == 0
                ? Array.Empty<string>()
                : new[] { $"Copart media resolution did not produce a gallery for {failed} of {candidates.Count} candidates." };
            await snapshotStore.CompleteSyncRunAsync(runId,
                new InventorySyncRunCompletion(finishedAt, candidates.Count, candidates.Count, completionFailures), cancellationToken);
            logger.LogInformation("Copart media enrichment completed: {Candidates} candidates, {Resolved} galleries, {Unchanged} stale rows, {Failed} failures.", candidates.Count, resolved, alreadyComplete, failed);
            return new CopartMediaEnrichmentResult(true, candidates.Count, resolved, alreadyComplete, failed, finishedAt - startedAt, failures.Take(20).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var finishedAt = DateTimeOffset.UtcNow;
            failures.Add($"media-enrichment: {exception.Message}");
            await snapshotStore.CompleteSyncRunAsync(runId,
                new InventorySyncRunCompletion(finishedAt, candidates.Count, candidates.Count, failures), cancellationToken);
            logger.LogError(exception, "Copart media enrichment stopped after {Candidates} candidates.", candidates.Count);
            return new CopartMediaEnrichmentResult(false, candidates.Count, resolved, alreadyComplete, failed, finishedAt - startedAt, failures);
        }
    }

    private async Task<MediaOutcome> ResolveCandidateAsync(StoredVehicleSnapshot candidate, CancellationToken cancellationToken)
    {
        try
        {
            var resolution = await mediaResolver.ResolveAsync(candidate.Vehicle, cancellationToken);
            var status = resolution.Resolved
                ? $"resolved-hd-{resolution.HdImages}-gallery-{resolution.GalleryImages}"
                : "unavailable";
            var updated = await snapshotStore.UpdateCopartMediaAsync(candidate.Identity, candidate.ObservedAt, resolution.Vehicle, status, cancellationToken);
            if (!updated) return new MediaOutcome(MediaOutcomeKind.AlreadyUpdated, null);
            return resolution.Resolved
                ? new MediaOutcome(MediaOutcomeKind.Resolved, null)
                : new MediaOutcome(MediaOutcomeKind.Failed, resolution.Failure);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new MediaOutcome(MediaOutcomeKind.Failed, exception.GetType().Name);
        }
    }

    private enum MediaOutcomeKind { Resolved, AlreadyUpdated, Failed }
    private sealed record MediaOutcome(MediaOutcomeKind Kind, string? Failure);
}
