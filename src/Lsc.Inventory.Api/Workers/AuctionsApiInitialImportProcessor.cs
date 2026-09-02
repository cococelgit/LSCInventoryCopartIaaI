using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record AuctionsApiInitialImportResult(
    Guid RunId,
    string Platform,
    bool Persisted,
    int RequestedMaximum,
    int Observed,
    int Loaded,
    int Marked,
    int Discarded,
    int Quarantined,
    int PagesProcessed,
    int RequestsIssued,
    IReadOnlyList<string> Failures,
    IReadOnlyDictionary<string, int> DiscardReasonCounts,
    int SourceRowsScanned,
    int MissingSaleDateSkipped,
    int SaleDateMatchesSkipped);

public interface IAuctionsApiInitialImportProcessor
{
    Task<AuctionsApiInitialImportResult> RunAsync(string platform, int maximumLots, bool persist, CancellationToken cancellationToken, int startPage = 1, bool requireSaleDate = false, int skipSaleDateMatches = 0, bool requireFutureSaleDate = false, Guid? requestedRunId = null);
}

/// <summary>
/// Initial import only. It pages through active /cars without minutes and
/// never deactivates rows absent from a partial import. The canonical pipeline
/// remains the only business-processing and persistence boundary.
/// </summary>
public sealed class AuctionsApiInitialImportProcessor(
    IAuctionsApiClient client,
    IInventorySnapshotStore snapshotStore,
    ICanonicalInventoryIngestionPipeline canonicalPipeline,
    IOptions<AuctionsApiOptions> options,
    ILogger<AuctionsApiInitialImportProcessor> logger) : IAuctionsApiInitialImportProcessor
{
    private readonly AuctionsApiOptions _options = options.Value;

    public async Task<AuctionsApiInitialImportResult> RunAsync(string platform, int maximumLots, bool persist, CancellationToken cancellationToken, int startPage = 1, bool requireSaleDate = false, int skipSaleDateMatches = 0, bool requireFutureSaleDate = false, Guid? requestedRunId = null)
    {
        var normalizedPlatform = platform.Trim().ToLowerInvariant();
        if (normalizedPlatform is not ("copart" or "iaai")) throw new ArgumentOutOfRangeException(nameof(platform));
        if (maximumLots is < 1 or > 100000) throw new ArgumentOutOfRangeException(nameof(maximumLots), "Initial import must be between 1 and 100000 lots.");
        if (startPage is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(startPage));
        if (skipSaleDateMatches is < 0 or > 100000) throw new ArgumentOutOfRangeException(nameof(skipSaleDateMatches));
        if (!_options.IsConfigured) throw new InvalidOperationException("AuctionsAPI initial import is disabled until the production configuration is explicitly enabled.");
        maximumLots = Math.Min(maximumLots, _options.InitialImportMaxLots);
        if (persist && !_options.AllowWrites) throw new InvalidOperationException("AuctionsAPI canonical writes are disabled until the Owner explicitly approves activation.");

        var startedAt = DateTimeOffset.UtcNow;
        var runId = requestedRunId ?? await snapshotStore.StartSyncRunAsync(new InventorySyncRunStart("auctions_api", normalizedPlatform, persist ? "initial-import" : "initial-import-shadow", maximumLots, _options.PageSize, startedAt), cancellationToken);
        var failures = new List<string>();
        var observed = 0;
        var sourceRowsScanned = 0;
        var missingSaleDateSkipped = 0;
        var saleDateMatchesSkipped = 0;
        var loaded = 0;
        var marked = 0;
        var discarded = 0;
        var quarantined = 0;
        var discardReasonCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var saleDateNotFutureSkipped = 0;
        var easternZone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var businessDate = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, easternZone).Date;
        var pages = 0;
        var requests = 0;
        var page = startPage;

        try
        {
            while (observed < maximumLots && page <= 1000 && requests < _options.InitialImportMaxRequests)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var response = await client.GetChangedLotsAsync(new AuctionsApiWindowRequest(DomainId(normalizedPlatform), null, page, _options.PageSize), cancellationToken);
                requests++;
                pages++;
                foreach (var vehicle in AuctionsApiIncrementalSyncProcessor.MapRows(AuctionsApiIncrementalSyncProcessor.ExtractRows(response.Data), normalizedPlatform))
                {
                    sourceRowsScanned++;
                    if ((requireSaleDate || requireFutureSaleDate) && vehicle.Auction?.AuctionAt is null)
                    {
                        missingSaleDateSkipped++;
                        continue;
                    }
                    if (requireFutureSaleDate && vehicle.Auction!.AuctionAt!.Value.Date <= businessDate)
                    {
                        saleDateNotFutureSkipped++;
                        continue;
                    }
                    if ((requireSaleDate || requireFutureSaleDate) && saleDateMatchesSkipped < skipSaleDateMatches)
                    {
                        saleDateMatchesSkipped++;
                        continue;
                    }
                    if (observed >= maximumLots) break;
                    cancellationToken.ThrowIfCancellationRequested();
                    observed++;
                    var result = await canonicalPipeline.ProcessAsync(vehicle, DateTimeOffset.UtcNow, cancellationToken, runId, persist: persist);
                    if (!result.Loaded)
                    {
                        if (result.Quarantined) quarantined++;
                        else
                        {
                            discarded++;
                            foreach (var reason in result.Eligibility.DiscardReasons)
                            {
                                var key = $"{reason.Code}: {reason.Name}";
                                discardReasonCounts[key] = discardReasonCounts.GetValueOrDefault(key) + 1;
                            }
                        }
                        continue;
                    }
                    loaded++;
                    if (result.Marked) marked++;
                }
                if (response.NextPage is null || response.NextPage <= page)
                {
                    if (observed < maximumLots) failures.Add("initial-import:source-ended-before-limit");
                    break;
                }
                page = response.NextPage.Value;
            }
            if (requests >= _options.InitialImportMaxRequests && observed < maximumLots) failures.Add("initial-import:request-cap-reached");
            var finishedAt = DateTimeOffset.UtcNow;
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(finishedAt, observed, requests, failures, loaded, marked, discarded, quarantined, failures.Count, pages, false), cancellationToken);
            return new(runId, normalizedPlatform, persist, maximumLots, observed, loaded, marked, discarded, quarantined, pages, requests, failures, discardReasonCounts, sourceRowsScanned, missingSaleDateSkipped, saleDateMatchesSkipped + saleDateNotFutureSkipped);
        }
        catch (OperationCanceledException exception)
        {
            failures.Add("initial-import:cancelled");
            logger.LogWarning("AuctionsAPI initial import {RunId} cancelled for {Platform} after {Observed} lots.", runId, normalizedPlatform, observed);
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(DateTimeOffset.UtcNow, observed, requests, failures, loaded, marked, discarded, quarantined, failures.Count, pages, false, null, true), CancellationToken.None);
            throw new OperationCanceledException($"AuctionsAPI initial import {runId} cancelled after {observed} lots.", exception, CancellationToken.None);
        }
        catch (Exception exception)
        {
            failures.Add(exception.Message);
            logger.LogError(exception, "AuctionsAPI initial import {RunId} failed for {Platform}.", runId, normalizedPlatform);
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(DateTimeOffset.UtcNow, observed, requests, failures, loaded, marked, discarded, quarantined, failures.Count, pages, false), CancellationToken.None);
            throw;
        }
    }

    private static int DomainId(string platform) => platform == "iaai" ? 1 : 3;
}
