using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record EligibilityReasonBreakdown(
    string Code,
    string Name,
    int Count,
    IReadOnlyList<string> SampleLots);

public sealed record AuctionsApiV2InitialLoadResult(
    Guid RunId,
    string Platform,
    bool Persisted,
    int RequestedMaximum,
    int SourceRowsMapped,
    int Observed,
    int Eligible,
    int Discarded,
    int Quarantined,
    int Created,
    int Updated,
    int Unchanged,
    int MediaRowsWritten,
    int PagesProcessed,
    int RequestsIssued,
    long DurationMs,
    IReadOnlyList<string> Failures,
    IReadOnlyList<EligibilityReasonBreakdown> DiscardReasonBreakdown,
    IReadOnlyList<EligibilityReasonBreakdown> QuarantineReasonBreakdown);

public interface IAuctionsApiV2InitialLoadProcessor
{
    Task<AuctionsApiV2InitialLoadResult> RunAsync(
        string platform,
        int maximumLots,
        bool persist,
        CancellationToken cancellationToken,
        int startPage = 1,
        Guid? requestedRunId = null);
}

/// <summary>
/// Incremental initial-load runner for a clean V2 rebuild. It never calls the
/// legacy per-row persistence boundary and never writes V1. Each invocation is
/// capped at 1,000 lots so operators can inspect a block before continuing.
/// </summary>
public sealed class AuctionsApiV2InitialLoadProcessor(
    IAuctionsApiClient client,
    IInventorySnapshotStore snapshotStore,
    IInventoryV2BatchWriter batchWriter,
    IOptions<AuctionsApiOptions> options,
    ILogger<AuctionsApiV2InitialLoadProcessor> logger) : IAuctionsApiV2InitialLoadProcessor
{
    private readonly AuctionsApiOptions _options = options.Value;

    public async Task<AuctionsApiV2InitialLoadResult> RunAsync(
        string platform,
        int maximumLots,
        bool persist,
        CancellationToken cancellationToken,
        int startPage = 1,
        Guid? requestedRunId = null)
    {
        var normalizedPlatform = platform.Trim().ToLowerInvariant();
        if (normalizedPlatform is not ("copart" or "iaai"))
            throw new ArgumentOutOfRangeException(nameof(platform));
        if (maximumLots is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumLots), "V2 initial-load blocks must be between 1 and 1000 lots.");
        if (startPage < 1)
            throw new ArgumentOutOfRangeException(nameof(startPage));
        if (!_options.IsConfigured)
            throw new InvalidOperationException("AuctionsAPI V2 initial load is not configured.");
        if (persist && !_options.AllowWrites)
            throw new InvalidOperationException("AuctionsAPI writes are disabled.");
        if (persist && !batchWriter.ShadowWriteConfigured)
            throw new InvalidOperationException("Inventory V2 writer is not enabled.");

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var startedAt = DateTimeOffset.UtcNow;
        var runId = requestedRunId ?? await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart(
                "auctions_api",
                normalizedPlatform,
                persist ? "v2-initial-block" : "v2-initial-block-dry-run",
                maximumLots,
                Math.Min(_options.PageSize, maximumLots),
                startedAt),
            cancellationToken);
        var leaseName = $"auctions-api-v2-initial-{normalizedPlatform}";
        var lease = await snapshotStore.TryAcquireLeaseAsync(
            leaseName,
            runId,
            startedAt,
            TimeSpan.FromMinutes(15),
            cancellationToken);

        if (!lease.Acquired)
        {
            var skipped = new[] { lease.SkipReason ?? "lease-active" };
            await snapshotStore.CompleteSyncRunAsync(
                runId,
                new InventorySyncRunCompletion(DateTimeOffset.UtcNow, 0, 0, skipped),
                cancellationToken);
            return new(runId, normalizedPlatform, persist, maximumLots, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, stopwatch.ElapsedMilliseconds, skipped, Array.Empty<EligibilityReasonBreakdown>(), Array.Empty<EligibilityReasonBreakdown>());
        }

        var failures = new List<string>();
        var discardReasons = new Dictionary<string, (string Name, int Count, List<string> SampleLots)>(StringComparer.Ordinal);
        var quarantineReasons = new Dictionary<string, (string Name, int Count, List<string> SampleLots)>(StringComparer.Ordinal);
        var sourceRowsMapped = 0;
        var observed = 0;
        var eligible = 0;
        var discarded = 0;
        var quarantined = 0;
        var created = 0;
        var updated = 0;
        var unchanged = 0;
        var mediaRowsWritten = 0;
        var pages = 0;
        var requests = 0;
        var page = startPage;
        var batch = new List<InventoryV2BatchItem>(maximumLots);

        try
        {
            while (observed < maximumLots && page <= 10000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = maximumLots - observed;
                var request = new AuctionsApiWindowRequest(
                    DomainId(normalizedPlatform),
                    null,
                    page,
                    Math.Min(Math.Max(1, _options.PageSize), remaining));
                var response = await client.GetChangedLotsAsync(request, cancellationToken);
                requests++;
                pages++;

                var mapped = AuctionsApiIncrementalSyncProcessor
                    .MapRowsCanonical(AuctionsApiIncrementalSyncProcessor.ExtractRows(response.Data), normalizedPlatform)
                    .Take(remaining)
                    .ToArray();
                sourceRowsMapped += mapped.Length;

                foreach (var vehicle in mapped)
                {
                    observed++;
                    var eligibility = Eligibility.AuctionEligibilityEvaluator.Evaluate(vehicle, DateTimeOffset.UtcNow);
                    if (!eligibility.LoadToSystem)
                    {
                        var target = eligibility.Decision == "CUARENTENA" ? quarantineReasons : discardReasons;
                        CollectReasonBreakdown(target, eligibility.DiscardReasons, vehicle.LotNumber);
                        if (eligibility.Decision == "CUARENTENA") quarantined++;
                        else discarded++;
                        continue;
                    }

                    eligible++;
                    batch.Add(new InventoryV2BatchItem(vehicle, DateTimeOffset.UtcNow));
                }

                if (persist && batch.Count >= batchWriter.PreferredBatchSize)
                    await FlushAsync(batch, cancellationToken, result =>
                    {
                        created += result.Created;
                        updated += result.Updated;
                        unchanged += result.Unchanged;
                        mediaRowsWritten += result.MediaRowsWritten;
                    });

                await snapshotStore.UpdateSyncRunProgressAsync(
                    runId,
                    new InventorySyncRunProgress(observed, requests, eligible, created, updated, unchanged, 0, discarded, quarantined, failures.Count, pages),
                    cancellationToken);

                if (observed >= maximumLots || response.NextPage is null || response.NextPage <= page)
                    break;
                page = response.NextPage.Value;
            }

            if (persist)
            {
                await FlushAsync(batch, cancellationToken, result =>
                {
                    created += result.Created;
                    updated += result.Updated;
                    unchanged += result.Unchanged;
                    mediaRowsWritten += result.MediaRowsWritten;
                });
            }

            if (observed < maximumLots && page <= 10000)
                failures.Add("v2-initial-load:source-ended-before-block-limit");

            await snapshotStore.CompleteSyncRunAsync(
                runId,
                new InventorySyncRunCompletion(
                    DateTimeOffset.UtcNow,
                    observed,
                    requests,
                    failures,
                    eligible,
                    0,
                    created,
                    updated,
                    discarded + quarantined,
                    pages,
                    false),
                CancellationToken.None);

            return new(runId, normalizedPlatform, persist, maximumLots, sourceRowsMapped, observed, eligible, discarded, quarantined, created, updated, unchanged, mediaRowsWritten, pages, requests, stopwatch.ElapsedMilliseconds, failures, ToBreakdown(discardReasons), ToBreakdown(quarantineReasons));
        }
        catch (OperationCanceledException)
        {
            failures.Add("v2-initial-load:cancelled");
            await snapshotStore.CompleteSyncRunAsync(
                runId,
                new InventorySyncRunCompletion(DateTimeOffset.UtcNow, observed, requests, failures, eligible, 0, created, updated, discarded + quarantined, pages, false, null, true),
                CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            failures.Add(exception.Message);
            logger.LogError(exception, "AuctionsAPI V2 initial block {RunId} failed for {Platform} after {Observed} lots.", runId, normalizedPlatform, observed);
            await snapshotStore.CompleteSyncRunAsync(
                runId,
                new InventorySyncRunCompletion(DateTimeOffset.UtcNow, observed, requests, failures, eligible, 0, created, updated, discarded + quarantined, pages, false),
                CancellationToken.None);
            throw;
        }
        finally
        {
            await snapshotStore.ReleaseLeaseAsync(leaseName, runId, DateTimeOffset.UtcNow, CancellationToken.None);
        }
    }

    private async Task FlushAsync(
        List<InventoryV2BatchItem> batch,
        CancellationToken cancellationToken,
        Action<InventoryV2BatchWriteResult> collect)
    {
        if (batch.Count == 0) return;
        var snapshot = batch.ToArray();
        batch.Clear();
        var result = await batchWriter.WriteShadowBatchAsync(snapshot, cancellationToken);
        collect(result);
        logger.LogInformation(
            "V2 initial block flush input={Input} distinct={Distinct} created={Created} updated={Updated} unchanged={Unchanged} media={Media} durationMs={DurationMs} skip={SkipReason}",
            result.InputRows,
            result.DistinctRows,
            result.Created,
            result.Updated,
            result.Unchanged,
            result.MediaRowsWritten,
            result.DurationMs,
            result.SkipReason);
    }

    private static int DomainId(string platform) => platform == "iaai" ? 1 : 3;

    private static void CollectReasonBreakdown(
        Dictionary<string, (string Name, int Count, List<string> SampleLots)> target,
        IReadOnlyList<EligibilityReason> reasons,
        string? lotNumber)
    {
        foreach (var reason in reasons)
        {
            if (!target.TryGetValue(reason.Code, out var aggregate))
                aggregate = (reason.Name, 0, new List<string>());
            aggregate.Count++;
            if (!string.IsNullOrWhiteSpace(lotNumber) && aggregate.SampleLots.Count < 10 && !aggregate.SampleLots.Contains(lotNumber, StringComparer.Ordinal))
                aggregate.SampleLots.Add(lotNumber);
            target[reason.Code] = aggregate;
        }
    }

    private static IReadOnlyList<EligibilityReasonBreakdown> ToBreakdown(
        Dictionary<string, (string Name, int Count, List<string> SampleLots)> source) =>
        source.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new EligibilityReasonBreakdown(pair.Key, pair.Value.Name, pair.Value.Count, pair.Value.SampleLots))
            .ToArray();
}
