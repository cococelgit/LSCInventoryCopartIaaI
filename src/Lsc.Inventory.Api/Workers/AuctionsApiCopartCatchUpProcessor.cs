using System.Diagnostics;
using System.Text.Json;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record AuctionsApiCopartCatchUpResult(
    Guid RunId,
    int Candidates,
    int Matched,
    int Updated,
    int NoEvidence,
    int Failed,
    int RequestsIssued,
    IReadOnlyList<string> Failures,
    bool DryRun);

public interface IAuctionsApiCopartCatchUpProcessor
{
    Task<AuctionsApiCopartCatchUpResult> RunAsync(int maximum, DateTimeOffset cutoff, CancellationToken cancellationToken, bool dryRun = false);
}

/// <summary>
/// Rehydrates active Copart lots with sale dates from today onward using AuctionsAPI only.
/// It uses the same canonical mapper, merge, eligibility, persistence, and scoring pipeline
/// as the automatic Copart sync. Missing seller evidence is preserved as unverified.
/// </summary>
public sealed class AuctionsApiCopartCatchUpProcessor(
    IAuctionsApiClient auctionsApiClient,
    IInventorySnapshotStore snapshotStore,
    ICanonicalInventoryIngestionPipeline canonicalPipeline,
    IOptions<AuctionsApiOptions> options,
    ILogger<AuctionsApiCopartCatchUpProcessor> logger) : IAuctionsApiCopartCatchUpProcessor
{
    private const string LeaseName = "copart-auctionsapi-catch-up";
    private readonly AuctionsApiOptions _options = options.Value;

    public async Task<AuctionsApiCopartCatchUpResult> RunAsync(
        int maximum,
        DateTimeOffset cutoff,
        CancellationToken cancellationToken,
        bool dryRun = false)
    {
        var hasApiKey = !string.IsNullOrWhiteSpace(_options.ApiKey);
        if (!hasApiKey)
            throw new InvalidOperationException("AuctionsAPI is not configured for Copart catch-up.");
        if (!dryRun && !_options.IsConfigured)
            throw new InvalidOperationException("AuctionsAPI is not enabled for Copart catch-up.");
        if (!dryRun && !_options.AllowWrites)
            throw new InvalidOperationException("AuctionsAPI canonical writes are disabled for Copart catch-up.");

        var limit = Math.Clamp(maximum, 1, 10_000);
        Console.WriteLine($"CATCHUP_PHASE=candidate_query_start maximum={limit} cutoff={cutoff:o} dryRun={dryRun}");
        logger.LogInformation("Copart catch-up candidate query starting. Maximum={Maximum}, Cutoff={Cutoff:o}, DryRun={DryRun}.", limit, cutoff, dryRun);
        var candidates = await snapshotStore.GetCopartCatchUpCandidatesAsync(limit, cutoff, cancellationToken);
        Console.WriteLine($"CATCHUP_PHASE=candidate_query_complete candidates={candidates.Count} dryRun={dryRun}");
        logger.LogInformation("Copart catch-up candidate query completed. Candidates={CandidateCount}, DryRun={DryRun}.", candidates.Count, dryRun);

        var runId = dryRun
            ? Guid.Empty
            : await snapshotStore.StartSyncRunAsync(
                new InventorySyncRunStart("auctions_api_catch_up", "copart", "pending", limit, _options.PageSize, DateTimeOffset.UtcNow),
                cancellationToken);
        InventorySyncLease? lease = null;
        if (!dryRun)
        {
            lease = await snapshotStore.TryAcquireLeaseAsync(LeaseName, runId, DateTimeOffset.UtcNow, TimeSpan.FromHours(2), cancellationToken);
            if (!lease.Acquired)
                throw new InvalidOperationException("A Copart AuctionsAPI catch-up is already running.");
        }

        var failures = new List<string>();
        var matched = 0;
        var updated = 0;
        var noEvidence = 0;
        var failed = 0;
        var requests = 0;
        var completed = 0;

        try
        {
            foreach (var candidate in candidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var lot = candidate.Vehicle.LotNumber;
                if (string.IsNullOrWhiteSpace(lot))
                {
                    noEvidence++;
                    continue;
                }

                try
                {
                    requests++;
                    var requestStopwatch = Stopwatch.StartNew();
                    Console.WriteLine($"CATCHUP_PHASE=auctionsapi_request_start lot={lot} request={requests} dryRun={dryRun}");
                    logger.LogInformation("Copart catch-up AuctionsAPI request starting. Lot={LotNumber}, Domain=3, RequestNumber={RequestNumber}, DryRun={DryRun}.", lot, requests, dryRun);
                    var response = await auctionsApiClient.GetLotAsync(lot, 3, searchById: false, includePricesHistory: false, cancellationToken);
                    requestStopwatch.Stop();
                    Console.WriteLine($"CATCHUP_PHASE=auctionsapi_request_complete lot={lot} request={requests} elapsedMs={requestStopwatch.ElapsedMilliseconds} dataKind={response.Data.ValueKind}");
                    logger.LogInformation("Copart catch-up AuctionsAPI request completed. Lot={LotNumber}, RequestNumber={RequestNumber}, ElapsedMs={ElapsedMs}, DataKind={DataKind}.", lot, requests, requestStopwatch.ElapsedMilliseconds, response.Data.ValueKind);
                    var rows = response.Data.ValueKind == JsonValueKind.Object && response.Data.TryGetProperty("lots", out var nestedLots) && nestedLots.ValueKind == JsonValueKind.Array
                        ? new List<JsonElement> { response.Data }
                        : AuctionsApiIncrementalSyncProcessor.ExtractRows(response.Data).ToList();
                    if (rows.Count == 0 && response.Data.ValueKind == JsonValueKind.Object)
                        rows.Add(response.Data);

                    var vehicle = AuctionsApiIncrementalSyncProcessor.MapRows(rows, "copart", trustRequestedDomain: true)
                        .FirstOrDefault(mapped => string.Equals(mapped.LotNumber, lot, StringComparison.OrdinalIgnoreCase));
                    if (vehicle is null)
                    {
                        noEvidence++;
                        failures.Add($"{lot}:auctionsapi:not-found");
                        continue;
                    }

                    matched++;
                    if (!dryRun)
                    {
                        var preferred = vehicle with { SourceProvider = vehicle.SourceProvider ?? "auctionsapi" };
                        var merged = AuctionVehicleMerger.Merge(preferred, candidate.Vehicle);
                        var ingestion = await canonicalPipeline.ProcessAsync(preferred, DateTimeOffset.UtcNow, cancellationToken, runId, merged, persist: true);
                        if (ingestion.Loaded)
                        {
                            updated++;
                            await snapshotStore.RecordSyncRunEventAsync(
                                new InventorySyncRunEvent(runId, "copart", $"copart:{lot}", lot, null, "updated", [], [], DateTimeOffset.UtcNow),
                                cancellationToken);
                        }
                        else
                        {
                            noEvidence++;
                        }
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failed++;
                    failures.Add($"{lot}:auctionsapi:{exception.Message}");
                    logger.LogWarning(exception, "Copart AuctionsAPI catch-up failed for lot {LotNumber} after {RequestNumber} requests.", lot, requests);
                }

                completed++;
                if (completed % 100 == 0)
                    logger.LogInformation("Copart catch-up checkpoint. Completed={Completed}, Candidates={Candidates}, Matched={Matched}, Updated={Updated}, NoEvidence={NoEvidence}, Failed={Failed}.", completed, candidates.Count, matched, updated, noEvidence, failed);
            }

            if (!dryRun)
            {
                await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(
                    DateTimeOffset.UtcNow, candidates.Count, requests, failures.Take(20).ToArray(), updated, 0, 0, 0, failed, requests, true), CancellationToken.None);
            }

            var result = new AuctionsApiCopartCatchUpResult(runId, candidates.Count, matched, updated, noEvidence, failed, requests, failures.Take(20).ToArray(), dryRun);
            logger.LogInformation("Copart catch-up completed. RunId={RunId}, Candidates={Candidates}, Matched={Matched}, Updated={Updated}, NoEvidence={NoEvidence}, Failed={Failed}, RequestsIssued={RequestsIssued}, DryRun={DryRun}.", result.RunId, result.Candidates, result.Matched, result.Updated, result.NoEvidence, result.Failed, result.RequestsIssued, result.DryRun);
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!dryRun)
            {
                await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(
                    DateTimeOffset.UtcNow, candidates.Count, requests, ["cancelled"], updated, 0, 0, 0, failed + 1, completed, false, null, true), CancellationToken.None);
            }
            throw;
        }
        finally
        {
            if (!dryRun && lease is { Acquired: true })
                await snapshotStore.ReleaseLeaseAsync(LeaseName, runId, DateTimeOffset.UtcNow, CancellationToken.None);
        }
    }
}
