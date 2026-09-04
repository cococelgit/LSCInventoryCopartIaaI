using System.Diagnostics;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
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
/// Enriches only already-persisted active Copart listings that still have an incomplete gallery.
/// It never affects eligibility, score, sale timestamps, reconciliation, lifecycle, or IAAI inventory.
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
        await using var processingLease = await snapshotStore.TryAcquireCopartProcessingLeaseAsync(cancellationToken);
        if (processingLease is null)
        {
            logger.LogWarning("Copart media enrichment skipped because another Copart processor holds the distributed PostgreSQL lease.");
            return new CopartMediaEnrichmentResult(false, 0, 0, 0, 0, TimeSpan.Zero, new[] { "COPART_PROCESSING_LOCK_NOT_ACQUIRED" });
        }

        var runId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart("copart-media", InventorySourcePolicy.CopartExcelSource, "media-enrichment", 1, _options.MediaEnrichmentBatchSize, startedAt),
            cancellationToken);
        var candidates = await snapshotStore.GetCopartMediaCandidatesAsync(_options.MediaEnrichmentBatchSize, cancellationToken);
        var metrics = new MediaMetricsCollector(candidates.Count);
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
                    metrics.Record(outcome);
                    if (outcome.Kind == MediaOutcomeKind.Failed && !string.IsNullOrWhiteSpace(outcome.FailureCode))
                        failures.Add(outcome.FailureCode);
                }
            }

            var finishedAt = DateTimeOffset.UtcNow;
            var completionFailures = metrics.Failed == 0
                ? Array.Empty<string>()
                : new[] { $"Copart media resolution did not produce a gallery for {metrics.Failed} of {candidates.Count} candidates." };
            await snapshotStore.CompleteSyncRunAsync(runId,
                new InventorySyncRunCompletion(finishedAt, candidates.Count, candidates.Count, completionFailures), cancellationToken);
            var result = metrics.Build(finishedAt - startedAt);
            logger.LogInformation(
                "Copart media enrichment completed: {Candidates} candidates, {Resolved} galleries, {AlreadyComplete} stale rows, {Failed} failures, {HdImages} HD images, {ThumbnailOnly} thumbnail-only galleries, {NotFound404} 404s and {InvalidUrl} invalid URLs.",
                result.Candidates, result.Resolved, result.AlreadyComplete, result.Failed, result.HdImages, result.ThumbnailOnly, result.NotFound404, result.InvalidUrl);
            return new CopartMediaEnrichmentResult(true, result.Candidates, result.Resolved, result.AlreadyComplete, result.Failed, finishedAt - startedAt, failures.Take(20).ToArray(), result);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var finishedAt = DateTimeOffset.UtcNow;
            failures.Add($"media-enrichment:{exception.GetType().Name}");
            await snapshotStore.CompleteSyncRunAsync(runId,
                new InventorySyncRunCompletion(finishedAt, candidates.Count, candidates.Count, failures), cancellationToken);
            logger.LogError(exception, "Copart media enrichment stopped after {Candidates} candidates.", candidates.Count);
            var result = metrics.Build(finishedAt - startedAt);
            return new CopartMediaEnrichmentResult(false, result.Candidates, result.Resolved, result.AlreadyComplete, result.Failed, finishedAt - startedAt, failures.Take(20).ToArray(), result);
        }
    }

    private async Task<MediaOutcome> ResolveCandidateAsync(StoredVehicleSnapshot candidate, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var resolution = await mediaResolver.ResolveAsync(candidate.Vehicle, cancellationToken);
            stopwatch.Stop();
            var status = resolution.Resolved
                ? $"resolved-hd-{resolution.HdImages}-gallery-{resolution.GalleryImages}"
                : $"unavailable-{resolution.FailureCode ?? "UNKNOWN"}";
            var vehicleForUpdate = PreserveMediaAudit(candidate.Vehicle, resolution.Vehicle);
            var updated = await snapshotStore.UpdateCopartMediaAsync(candidate.Identity, candidate.ObservedAt, vehicleForUpdate, status, cancellationToken);
            if (!updated) return new MediaOutcome(MediaOutcomeKind.AlreadyUpdated, resolution, stopwatch.Elapsed, null);
            return resolution.Resolved
                ? new MediaOutcome(MediaOutcomeKind.Resolved, resolution, stopwatch.Elapsed, null)
                : new MediaOutcome(MediaOutcomeKind.Failed, resolution, stopwatch.Elapsed, resolution.FailureCode);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            stopwatch.Stop();
            return new MediaOutcome(MediaOutcomeKind.Failed, null, stopwatch.Elapsed, exception.GetType().Name.ToUpperInvariant());
        }
    }

    private static AuctionVehicle PreserveMediaAudit(AuctionVehicle original, AuctionVehicle resolved)
    {
        var additional = resolved.AdditionalData is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(resolved.AdditionalData);
        if (!additional.ContainsKey("copart_media_original_photos"))
            additional["copart_media_original_photos"] = JsonSerializer.SerializeToElement(original.Media?.Photos ?? Array.Empty<string>());
        return resolved with { AdditionalData = additional };
    }

    private enum MediaOutcomeKind { Resolved, AlreadyUpdated, Failed }

    private sealed record MediaOutcome(
        MediaOutcomeKind Kind,
        CopartMediaResolution? Resolution,
        TimeSpan Duration,
        string? FailureCode);

    private sealed class MediaMetricsCollector(int candidates)
    {
        private readonly List<long> _durations = [];
        public int Candidates { get; } = candidates;
        public int Resolved { get; private set; }
        public int AlreadyComplete { get; private set; }
        public int Failed { get; private set; }
        public int GalleryCount { get; private set; }
        public int HdImages { get; private set; }
        public int ThumbnailOnly { get; private set; }
        public int NotFound404 { get; private set; }
        public int InvalidUrl { get; private set; }

        public void Record(MediaOutcome outcome)
        {
            _durations.Add(Math.Max(0L, (long)Math.Ceiling(outcome.Duration.TotalMilliseconds)));
            if (outcome.Resolution is { } resolution)
            {
                if (outcome.Kind == MediaOutcomeKind.Resolved)
                {
                    GalleryCount++;
                    HdImages += resolution.HdImages;
                    if (resolution.HdImages == 0 && resolution.GalleryImages > 0) ThumbnailOnly++;
                }
                if (string.Equals(resolution.FailureCode, "NOT_FOUND_404", StringComparison.OrdinalIgnoreCase)) NotFound404++;
                if (string.Equals(resolution.FailureCode, "INVALID_URL", StringComparison.OrdinalIgnoreCase)) InvalidUrl++;
            }
            switch (outcome.Kind)
            {
                case MediaOutcomeKind.Resolved: Resolved++; break;
                case MediaOutcomeKind.AlreadyUpdated: AlreadyComplete++; break;
                case MediaOutcomeKind.Failed: Failed++; break;
            }
        }

        public CopartMediaEnrichmentMetrics Build(TimeSpan _)
        {
            var sorted = _durations.Order().ToArray();
            long? Percentile(decimal percent) => sorted.Length == 0 ? null : sorted[(int)Math.Ceiling((sorted.Length - 1) * percent)];
            return new CopartMediaEnrichmentMetrics(
                Candidates, Resolved, AlreadyComplete, Failed, GalleryCount, HdImages, ThumbnailOnly, NotFound404, InvalidUrl,
                sorted.Sum(), Percentile(0.50m), Percentile(0.95m));
        }
    }
}
