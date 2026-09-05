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
    IReadOnlyList<string> Failures,
    bool DryRun);

public interface IAuctionsApiIaaIConditionBackfillProcessor
{
    Task<AuctionsApiIaaIBackfillResult> RunAsync(int maximum, DateTimeOffset cutoff, CancellationToken cancellationToken, bool dryRun = false);
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

    public async Task<AuctionsApiIaaIBackfillResult> RunAsync(int maximum, DateTimeOffset cutoff, CancellationToken cancellationToken, bool dryRun = false)
    {
        var hasApiKey = !string.IsNullOrWhiteSpace(_options.ApiKey);
        if (dryRun ? !hasApiKey : !_options.IsConfigured)
            throw new InvalidOperationException("AuctionsAPI is not configured for IAAI backfill.");
        if (!dryRun && !_options.AllowWrites)
            throw new InvalidOperationException("AuctionsAPI canonical writes are disabled for IAAI backfill.");

        var candidateQueryStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        logger.LogInformation("IAAI backfill candidate query starting. Maximum={Maximum}, Cutoff={Cutoff:o}, DryRun={DryRun}.", maximum, cutoff, dryRun);
        var candidates = await snapshotStore.GetIaaIConditionBackfillCandidatesAsync(maximum, cutoff, cancellationToken);
        logger.LogInformation("IAAI backfill candidate query completed. Candidates={CandidateCount}, ElapsedMs={ElapsedMs}.", candidates.Count, System.Diagnostics.Stopwatch.GetElapsedTime(candidateQueryStarted).TotalMilliseconds);
        var byLot = candidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Vehicle.LotNumber))
            .ToDictionary(candidate => candidate.Vehicle.LotNumber!, StringComparer.OrdinalIgnoreCase);
        var runId = dryRun
            ? Guid.Empty
            : await snapshotStore.StartSyncRunAsync(
                new InventorySyncRunStart("auctions_api_backfill", "iaai", "condition", maximum, _options.PageSize, DateTimeOffset.UtcNow),
                cancellationToken);

        var failures = new List<string>();
        var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var updated = 0;
        var noEvidence = 0;
        var failed = 0;
        var requests = 0;
        var calls = 0;

        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lot = candidate.Vehicle.LotNumber!;
                try
                {
                    requests++;
                    calls++;
                    var requestStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    logger.LogInformation("IAAI backfill AuctionsAPI request starting. Lot={LotNumber}, Domain=1, SearchById=false, RequestNumber={RequestNumber}, DryRun={DryRun}.", lot, requests, dryRun);
                    var response = await auctionsApiClient.GetLotAsync(lot, 1, searchById: false, includePricesHistory: false, cancellationToken);
                    logger.LogInformation("IAAI backfill AuctionsAPI request completed. Lot={LotNumber}, DataKind={DataKind}, ElapsedMs={ElapsedMs}.", lot, response.Data.ValueKind, System.Diagnostics.Stopwatch.GetElapsedTime(requestStarted).TotalMilliseconds);
                    var rows = response.Data.ValueKind == System.Text.Json.JsonValueKind.Object && response.Data.TryGetProperty("lots", out var nestedLots) && nestedLots.ValueKind == System.Text.Json.JsonValueKind.Array
                        ? new List<System.Text.Json.JsonElement> { response.Data }
                        : AuctionsApiIncrementalSyncProcessor.ExtractRows(response.Data).ToList();
                    if (rows.Count == 0 && response.Data.ValueKind == System.Text.Json.JsonValueKind.Object)
                        rows.Add(response.Data);
                    var vehicle = AuctionsApiIncrementalSyncProcessor.MapRows(rows, "iaai", trustRequestedDomain: true)
                        .FirstOrDefault(mapped => string.Equals(mapped.LotNumber, lot, StringComparison.OrdinalIgnoreCase));
                    if (vehicle is null)
                    {
                        noEvidence++;
                        failures.Add($"{lot}:auctionsapi:not-found");
                        continue;
                    }

                    matched.Add(lot);
                    var result = await PersistMappedAsync(vehicle, candidate, runId, cancellationToken, dryRun);
                    updated += result.Updated;
                    noEvidence += result.NoEvidence;
                    failed += result.Failed;
                    failures.AddRange(result.Failures);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    noEvidence++;
                    failures.Add($"{lot}:auctionsapi:{exception.Message}");
                    logger.LogWarning(exception, "AuctionsAPI directed lookup failed for IAAI lot {LotNumber}.", lot);
                }
            }

            foreach (var missingLot in dryRun ? Enumerable.Empty<string>() : byLot.Keys.Except(matched, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    requests++;
                    var fallbackStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    logger.LogInformation("IAAI backfill Apibara fallback request starting. Lot={LotNumber}, RequestNumber={RequestNumber}.", missingLot, requests);
                    var detail = await apibaraClient.GetVehicleAsync(missingLot, cancellationToken);
                    logger.LogInformation("IAAI backfill Apibara fallback request completed. Lot={LotNumber}, ElapsedMs={ElapsedMs}.", missingLot, System.Diagnostics.Stopwatch.GetElapsedTime(fallbackStarted).TotalMilliseconds);
                    var result = await PersistMappedAsync(detail.Data with { SourceProvider = "apibara" }, byLot[missingLot], runId, cancellationToken, dryRun);
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

            if (!dryRun)
            {
                await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(
                    DateTimeOffset.UtcNow, candidates.Count, requests, failures, updated, 0, 0, 0, failed, calls, true), CancellationToken.None);
            }
            return new(runId, candidates.Count, matched.Count, dryRun ? 0 : byLot.Count - matched.Count, updated, noEvidence, failed, requests, failures.Take(20).ToArray(), dryRun);
        }
        catch (OperationCanceledException)
        {
            if (!dryRun)
            {
                await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(
                    DateTimeOffset.UtcNow, candidates.Count, requests, ["cancelled"], updated, 0, 0, 0, failed + 1, calls, false, null, true), CancellationToken.None);
            }
            throw;
        }
    }

    private async Task<SingleLotResult> PersistMappedAsync(
        AuctionVehicle providerVehicle,
        StoredVehicleSnapshot existing,
        Guid runId,
        CancellationToken cancellationToken,
        bool dryRun)
    {
        var preferred = providerVehicle with { SourceProvider = providerVehicle.SourceProvider ?? "auctionsapi" };
        if (dryRun) return new(0, 0, 0, []);
        var merged = AuctionVehicleMerger.Merge(preferred, existing.Vehicle);
        var ingestion = await canonicalPipeline.ProcessAsync(preferred, DateTimeOffset.UtcNow, cancellationToken, runId, merged, persist: true);
        if (!ingestion.Loaded)
            return new(0, 1, 0, []);
        return new(1, 0, 0, []);
    }

    private sealed record SingleLotResult(int Updated, int NoEvidence, int Failed, IReadOnlyList<string> Failures);
}
