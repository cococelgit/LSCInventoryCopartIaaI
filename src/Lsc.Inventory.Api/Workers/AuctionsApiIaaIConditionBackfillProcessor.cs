using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record AuctionsApiIaaIBackfillResult(
    Guid RunId,
    int Candidates,
    int AuctionsApiMatched,
    int ApibaraFallbacks,
    int Updated,
    int NoEvidence,
    int Failed,
    int RequestsIssued,
    IReadOnlyList<string> Failures);

public interface IAuctionsApiIaaIConditionBackfillProcessor
{
    Task<AuctionsApiIaaIBackfillResult> RunAsync(int maximum, DateTimeOffset cutoff, CancellationToken cancellationToken);
}

/// <summary>
/// Historical IAAI condition hydration. AuctionsAPI is the primary source and
/// Apibara is used only when the primary feed does not return a requested lot.
/// Both paths enter the same canonical normalization, eligibility, persistence,
/// and scoring pipeline.
/// </summary>
public sealed class AuctionsApiIaaIConditionBackfillProcessor(
    IAuctionsApiClient auctionsApiClient,
    IApibaraClient apibaraClient,
    IInventorySnapshotStore snapshotStore,
    ICanonicalInventoryIngestionPipeline canonicalPipeline,
    IOptions<AuctionsApiOptions> options,
    ILogger<AuctionsApiIaaIConditionBackfillProcessor> logger) : IAuctionsApiIaaIConditionBackfillProcessor
{
    private readonly AuctionsApiOptions _options = options.Value;

    public async Task<AuctionsApiIaaIBackfillResult> RunAsync(int maximum, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
            throw new InvalidOperationException("AuctionsAPI is not configured for IAAI backfill.");
        if (!_options.AllowWrites)
            throw new InvalidOperationException("AuctionsAPI canonical writes are disabled for IAAI backfill.");

        var candidates = await snapshotStore.GetIaaIConditionBackfillCandidatesAsync(maximum, cutoff, cancellationToken);
        var byLot = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Vehicle.LotNumber))
            .ToDictionary(candidate => candidate.Vehicle.LotNumber!, StringComparer.OrdinalIgnoreCase);
        var runId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart("auctions_api_backfill", "iaai", "condition", maximum, _options.PageSize, DateTimeOffset.UtcNow),
            cancellationToken);

        var failures = new List<string>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updated = 0;
        var noEvidence = 0;
        var failed = 0;
        var requests = 0;
        var page = 1;
        var pages = 0;

        try
        {
            while (matched.Count < byLot.Count && page <= 1000)
            {
                var response = await auctionsApiClient.GetChangedLotsAsync(
                    new AuctionsApiWindowRequest(1, null, page, _options.PageSize),
                    cancellationToken);
                requests++;
                pages++;
                foreach (var vehicle in AuctionsApiIncrementalSyncProcessor.MapRows(
                    AuctionsApiIncrementalSyncProcessor.ExtractRows(response.Data), "iaai"))
                {
                    if (string.IsNullOrWhiteSpace(vehicle.LotNumber) || !byLot.TryGetValue(vehicle.LotNumber, out var existing))
                        continue;
                    if (!matched.Add(vehicle.LotNumber)) continue;
                    var result = await PersistMappedAsync(vehicle, existing, runId, cancellationToken);
                    updated += result.Updated;
                    noEvidence += result.NoEvidence;
                    failed += result.Failed;
                    failures.AddRange(result.Failures);
                }

                if (response.NextPage is null || response.NextPage <= page) break;
                page = response.NextPage.Value;
            }

            foreach (var missingLot in byLot.Keys.Except(matched, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    requests++;
                    var detail = await apibaraClient.GetVehicleAsync(missingLot, cancellationToken);
                    var result = await PersistMappedAsync(detail.Data with { SourceProvider = "apibara" }, byLot[missingLot], runId, cancellationToken);
                    updated += result.Updated;
                    noEvidence += result.NoEvidence;
                    failed += result.Failed;
                    failures.AddRange(result.Failures);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed++;
                    failures.Add($"{missingLot}:apibara-fallback:{exception.Message}");
                    logger.LogWarning(exception, "Apibara fallback failed for IAAI lot {LotNumber}.", missingLot);
                }
            }

            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(
                DateTimeOffset.UtcNow, candidates.Count, requests, failures, updated, 0, 0, 0, failed, pages, true), CancellationToken.None);
            return new(runId, candidates.Count, matched.Count, byLot.Count - matched.Count, updated, noEvidence, failed, requests, failures.Take(20).ToArray());
        }
        catch (OperationCanceledException)
        {
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(
                DateTimeOffset.UtcNow, candidates.Count, requests, ["cancelled"], updated, 0, 0, 0, failed + 1, pages, false, null, true), CancellationToken.None);
            throw;
        }
    }

    private async Task<SingleLotResult> PersistMappedAsync(
        AuctionVehicle providerVehicle,
        StoredVehicleSnapshot existing,
        Guid runId,
        CancellationToken cancellationToken)
    {
        var preferred = providerVehicle with { SourceProvider = providerVehicle.SourceProvider ?? "auctionsapi" };
        var merged = AuctionVehicleMerger.Merge(preferred, existing.Vehicle);
        var ingestion = await canonicalPipeline.ProcessAsync(preferred, DateTimeOffset.UtcNow, cancellationToken, runId, merged, persist: true);
        if (!ingestion.Loaded)
            return new(0, 1, 0, []);
        return new(1, 0, 0, []);
    }

    private sealed record SingleLotResult(int Updated, int NoEvidence, int Failed, IReadOnlyList<string> Failures);
}
