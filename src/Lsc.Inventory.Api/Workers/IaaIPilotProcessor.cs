using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record IaaIPilotResult(
    Guid RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int Observed,
    int Loaded,
    int Marked,
    int Discarded,
    int Quarantined,
    int RequestsIssued,
    IReadOnlyDictionary<string, int> RuleCounts,
    IReadOnlyList<string> Failures);

public interface IIaaIPilotProcessor
{
    Task<IaaIPilotResult> RunAsync(CancellationToken cancellationToken);
}

public sealed class IaaIPilotProcessor(
    IApibaraClient apibaraClient,
    IInventorySnapshotStore snapshotStore,
    IOptions<ApibaraOptions> apibaraOptions,
    IOptions<IaaIPilotOptions> pilotOptions,
    ILogger<IaaIPilotProcessor> logger) : IIaaIPilotProcessor
{
    private readonly ApibaraOptions _apibara = apibaraOptions.Value;
    private readonly IaaIPilotOptions _pilot = pilotOptions.Value;

    public async Task<IaaIPilotResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!_pilot.Enabled) throw new InvalidOperationException("IAAI pilot is disabled by configuration.");

        var startedAt = DateTimeOffset.UtcNow;
        var runId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart("apibara", "iaai", "national-pilot", _pilot.MaxListRequests, _apibara.PageSize, startedAt),
            cancellationToken);
        var failures = new List<string>();
        var ruleCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var observed = 0;
        var loaded = 0;
        var marked = 0;
        var discarded = 0;
        var quarantined = 0;
        var requests = 0;
        var detailsRequested = 0;
        string? cursor = null;

        try
        {
            for (var page = 0; page < _pilot.MaxListRequests && loaded < _pilot.MaxVehicles; page++)
            {
                requests++;
                var response = await apibaraClient.SearchVehiclesAsync(
                    new VehicleSearchRequest("iaai", _pilot.LotSubStatus, _apibara.PageSize, cursor),
                    cancellationToken);
                if (response.Data.Count == 0) break;

                foreach (var rawVehicle in response.Data)
                {
                    if (loaded >= _pilot.MaxVehicles) break;
                    var evaluatedAt = DateTimeOffset.UtcNow;
                    var providerVehicle = rawVehicle;
                    if (_pilot.EnrichDetails && detailsRequested < _pilot.DetailEnrichmentLimit)
                    {
                        var lookup = rawVehicle.LotNumber ?? rawVehicle.Vin;
                        if (!string.IsNullOrWhiteSpace(lookup))
                        {
                            try
                            {
                                requests++;
                                detailsRequested++;
                                var detail = await apibaraClient.GetVehicleAsync(lookup, cancellationToken);
                                providerVehicle = AuctionVehicleMerger.Merge(detail.Data, rawVehicle);
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                failures.Add($"detail:{lookup}:{exception.GetType().Name}");
                                logger.LogWarning(exception, "IAAI detail enrichment failed for lookup {Lookup}.", lookup);
                            }
                        }
                    }
                    var vehicle = CanonicalVehicleCleaner.Clean(AuctionVehicleNormalizer.Normalize(providerVehicle, null, null));
                    var eligibility = AuctionEligibilityEvaluator.Evaluate(vehicle, evaluatedAt);
                    await snapshotStore.PersistEligibilityDecisionAsync(eligibility, evaluatedAt, cancellationToken);
                    observed++;

                    foreach (var code in eligibility.DiscardReasons.Concat(eligibility.Flags).Select(reason => reason.Code))
                        ruleCounts[code] = ruleCounts.GetValueOrDefault(code) + 1;

                    switch (eligibility.Decision)
                    {
                        case "CUARENTENA":
                            quarantined++;
                            break;
                        case "DESCARTAR":
                            discarded++;
                            break;
                        case "MARCAR":
                            marked++;
                            loaded++;
                            await snapshotStore.PersistAsync(vehicle, evaluatedAt, cancellationToken);
                            break;
                        default:
                            loaded++;
                            await snapshotStore.PersistAsync(vehicle, evaluatedAt, cancellationToken);
                            break;
                    }
                }

                cursor = response.Meta.NextCursor;
                if (string.IsNullOrWhiteSpace(cursor)) break;
            }
        }
        catch (OperationCanceledException exception)
        {
            failures.Add("cancelled");
            logger.LogWarning("IAAI pilot {RunId} cancelled after {Observed} vehicles.", runId, observed);
            await snapshotStore.CompleteSyncRunAsync(
                runId,
                new InventorySyncRunCompletion(DateTimeOffset.UtcNow, observed, requests, failures, Cancelled: true),
                CancellationToken.None);
            throw new OperationCanceledException($"IAAI pilot {runId} cancelled after {observed} vehicles.", exception, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add(exception.Message);
            logger.LogError(exception, "IAAI pilot {RunId} failed after {Observed} vehicles.", runId, observed);
        }

        var finishedAt = DateTimeOffset.UtcNow;
        await snapshotStore.CompleteSyncRunAsync(
            runId,
            new InventorySyncRunCompletion(finishedAt, observed, requests, failures),
            CancellationToken.None);

        return new IaaIPilotResult(runId, startedAt, finishedAt, observed, loaded, marked, discarded, quarantined, requests, ruleCounts, failures);
    }
}
