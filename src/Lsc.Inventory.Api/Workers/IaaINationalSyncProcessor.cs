using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Lsc.Inventory.Api.Workers;

public sealed record IaaINationalSyncResult(
    Guid RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool Skipped,
    string? SkipReason,
    Guid CycleId,
    int Observed,
    int Loaded,
    int Marked,
    int Discarded,
    int Quarantined,
    int PagesProcessed,
    int RequestsIssued,
    bool CycleCompleted,
    InventoryReconciliationResult? Reconciliation,
    IReadOnlyDictionary<string, int> RuleCounts,
    IReadOnlyList<string> Failures,
    bool ShouldRetry);

public interface IIaaINationalSyncProcessor
{
    Task<IaaINationalSyncResult> RunAsync(CancellationToken cancellationToken);
}

public sealed class IaaINationalSyncProcessor(
    IApibaraClient apibaraClient,
    IInventorySnapshotStore snapshotStore,
    ICanonicalInventoryIngestionPipeline canonicalPipeline,
    IOptions<ApibaraOptions> apibaraOptions,
    IOptions<IaaINationalOptions> nationalOptions,
    ILogger<IaaINationalSyncProcessor> logger) : IIaaINationalSyncProcessor
{
    private const string StreamName = "iaai-national-open";
    private const string LeaseName = "iaai-national-sync";
    private readonly ApibaraOptions _apibara = apibaraOptions.Value;
    private readonly IaaINationalOptions _national = nationalOptions.Value;

    public async Task<IaaINationalSyncResult> RunAsync(CancellationToken cancellationToken)
    {
        if (!_national.Enabled) throw new InvalidOperationException("IAAI national sync is disabled by configuration.");

        var startedAt = DateTimeOffset.UtcNow;
        var runId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart("apibara", "iaai", "national-rotating", _national.PagesPerRun, _apibara.PageSize, startedAt),
            cancellationToken);
        var lease = await snapshotStore.TryAcquireLeaseAsync(LeaseName, runId, startedAt, TimeSpan.FromMinutes(_national.LeaseMinutes), cancellationToken);
        if (!lease.Acquired)
        {
            var skippedAt = DateTimeOffset.UtcNow;
            var skippedFailures = new[] { lease.SkipReason ?? "lease-active" };
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(skippedAt, 0, 0, skippedFailures), cancellationToken);
            return new IaaINationalSyncResult(runId, startedAt, skippedAt, true, lease.SkipReason, Guid.Empty, 0, 0, 0, 0, 0, 0, 0, false, null, new Dictionary<string, int>(), skippedFailures, false);
        }

        var failures = new List<string>();
        var rules = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var observed = 0;
        var loaded = 0;
        var marked = 0;
        var discarded = 0;
        var quarantined = 0;
        var pagesProcessed = 0;
        var requests = 0;
        var detailsRequested = 0;
        var cycleCompleted = false;
        InventoryReconciliationResult? reconciliation = null;
        var quotaBlocked = false;
        var cursorRecoveryAttempted = false;

        try
        {
            if (_national.CaptureUsage)
            {
                try
                {
                    var usage = await apibaraClient.GetUsageAsync(cancellationToken);
                    await snapshotStore.PersistProviderUsageAsync("apibara", usage.Data, DateTimeOffset.UtcNow, cancellationToken);
                    requests++;
                    var remaining = TryGetRemainingRequests(usage.Data);
                    if (remaining is not null && remaining < _national.MinimumRemainingRequests)
                    {
                        quotaBlocked = true;
                        failures.Add($"quota-guard:remaining={remaining}:minimum={_national.MinimumRemainingRequests}");
                        logger.LogWarning("IAAI national sync {RunId} stopped before paging because provider quota remaining {Remaining} is below configured minimum {Minimum}.", runId, remaining, _national.MinimumRemainingRequests);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures.Add($"usage:before:{exception.GetType().Name}");
                }
            }

            var checkpoint = await snapshotStore.GetNationalSyncCheckpointAsync(StreamName, cancellationToken);
            var isInitialBackfill = !checkpoint.InitialBackfillCompleted;
            var pagesPerRun = isInitialBackfill ? _national.BackfillPagesPerRun : _national.MaintenancePagesPerRun;
            var maxRequestsPerRun = isInitialBackfill ? _national.BackfillMaxRequestsPerRun : _national.MaintenanceMaxRequestsPerRun;
            var detailLimitPerRun = isInitialBackfill ? _national.BackfillDetailEnrichmentLimitPerRun : _national.MaintenanceDetailEnrichmentLimitPerRun;
            logger.LogInformation("IAAI national sync {RunId} started in {Mode} mode with page budget {Pages} and request budget {Requests}.", runId, isInitialBackfill ? "backfill" : "maintenance", pagesPerRun, maxRequestsPerRun);
            var cycleId = checkpoint.CycleId is null || checkpoint.CycleCompleted ? Guid.NewGuid() : checkpoint.CycleId.Value;
            var cursor = checkpoint.CycleCompleted ? null : checkpoint.Cursor;
            var totalPages = checkpoint.CycleCompleted ? 0 : checkpoint.PagesCompleted;
            var totalLots = checkpoint.CycleCompleted ? 0 : checkpoint.LotsObserved;

            for (var page = 0; page < pagesPerRun && requests < maxRequestsPerRun && !quotaBlocked; page++)
            {
                requests++;
                VehicleListResponse response;
                try
                {
                    response = await apibaraClient.SearchVehiclesAsync(
                        new VehicleSearchRequest("iaai", _national.LotSubStatus, _apibara.PageSize, cursor, UpdatedWithinMinutes: _national.UpdatedWithinMinutes),
                        cancellationToken);
                }
                catch (ApibaraInvalidCursorException exception) when (!string.IsNullOrWhiteSpace(cursor) && !cursorRecoveryAttempted)
                {
                    cursorRecoveryAttempted = true;
                    var expiredCursorCycle = cycleId;
                    cycleId = Guid.NewGuid();
                    cursor = null;
                    totalPages = 0;
                    totalLots = 0;
                    await snapshotStore.PersistNationalSyncBatchAsync(
                        new NationalSyncBatch(StreamName, cycleId, null, 0, 0, [], DateTimeOffset.UtcNow, false, checkpoint.InitialBackfillCompleted),
                        cancellationToken);
                    logger.LogWarning(
                        exception,
                        "IAAI national sync {RunId} replaced one rejected opaque cursor from cycle {ExpiredCycleId} with a fresh cycle {CycleId}; this recovery is limited to once per execution.",
                        runId,
                        expiredCursorCycle,
                        cycleId);
                    page--;
                    continue;
                }
                if (response.Data.Count == 0)
                {
                    cycleCompleted = true;
                    await snapshotStore.PersistNationalSyncBatchAsync(
                        new NationalSyncBatch(StreamName, cycleId, null, totalPages, totalLots, [], DateTimeOffset.UtcNow, true, checkpoint.InitialBackfillCompleted || isInitialBackfill),
                        cancellationToken);
                    reconciliation = await snapshotStore.CompleteNationalSyncCycleAsync(StreamName, cycleId, DateTimeOffset.UtcNow, cancellationToken, runId);
                    break;
                }

                var eligibleLotKeys = new List<string>();
                foreach (var rawVehicle in response.Data)
                {
                    var evaluatedAt = DateTimeOffset.UtcNow;
                    var providerVehicle = rawVehicle;
                    if (_national.EnrichVehicleDetails && detailsRequested < detailLimitPerRun && requests < maxRequestsPerRun)
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
                            catch (HttpRequestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                            {
                                failures.Add($"quota:detail:{lookup}");
                                quotaBlocked = true;
                                logger.LogWarning("IAAI national sync hit provider rate or quota limit during detail enrichment.");
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                failures.Add($"detail:{lookup}:{exception.GetType().Name}");
                                logger.LogWarning(exception, "IAAI detail enrichment failed for lookup {Lookup}.", lookup);
                            }
                        }
                    }

                    var ingestion = await canonicalPipeline.ProcessAsync(providerVehicle, evaluatedAt, cancellationToken, runId);
                    var vehicle = ingestion.Vehicle;
                    var eligibility = ingestion.Eligibility;
                    observed++;
                    foreach (var code in eligibility.DiscardReasons.Concat(eligibility.Flags).Select(reason => reason.Code))
                        rules[code] = rules.GetValueOrDefault(code) + 1;

                    if (ingestion.Loaded)
                    {
                        loaded++;
                        if (ingestion.Marked) marked++;
                        var lotKey = vehicle.LotNumber ?? vehicle.Vin;
                        if (!string.IsNullOrWhiteSpace(lotKey)) eligibleLotKeys.Add($"iaai:{lotKey.Trim()}");
                    }
                    else
                    {
                        var action = ingestion.Quarantined ? "quarantined" : "discarded";
                        if (ingestion.Quarantined) quarantined++;
                        else discarded++;
                        await snapshotStore.RecordSyncRunEventAsync(new InventorySyncRunEvent(
                            runId,
                            "iaai",
                            $"iaai:{eligibility.LotNumber ?? eligibility.VinMasked ?? "unknown"}",
                            eligibility.LotNumber,
                            eligibility.VinMasked,
                            action,
                            [],
                            eligibility.DiscardReasons.Concat(eligibility.Flags).Select(reason => reason.Code).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                            evaluatedAt), cancellationToken);
                    }

                    if (quotaBlocked) break;
                }

                var pageFullyProcessed = !quotaBlocked;
                if (pageFullyProcessed)
                {
                    pagesProcessed++;
                    totalPages++;
                    totalLots += response.Data.Count;
                    cursor = response.Meta.NextCursor;
                }
                cycleCompleted = pageFullyProcessed && string.IsNullOrWhiteSpace(cursor);
                await snapshotStore.PersistNationalSyncBatchAsync(
                    new NationalSyncBatch(StreamName, cycleId, cursor, totalPages, totalLots, eligibleLotKeys, DateTimeOffset.UtcNow, cycleCompleted, checkpoint.InitialBackfillCompleted || (isInitialBackfill && cycleCompleted)),
                    cancellationToken);
                if (quotaBlocked) break;
                if (cycleCompleted)
                {
                    reconciliation = await snapshotStore.CompleteNationalSyncCycleAsync(StreamName, cycleId, DateTimeOffset.UtcNow, cancellationToken, runId);
                    break;
                }
            }

            if (requests >= maxRequestsPerRun && !cycleCompleted)
                logger.LogInformation(
                    "IAAI national sync {RunId} paused normally after reaching its request budget {RequestBudget}; the persisted cursor will resume on the next schedule.",
                    runId,
                    maxRequestsPerRun);

            if (_national.CaptureUsage)
            {
                try
                {
                    var usage = await apibaraClient.GetUsageAsync(cancellationToken);
                    await snapshotStore.PersistProviderUsageAsync("apibara", usage.Data, DateTimeOffset.UtcNow, cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    failures.Add($"usage:after:{exception.GetType().Name}");
                }
            }

            var finishedAt = DateTimeOffset.UtcNow;
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(
                finishedAt, observed, requests, failures, Loaded: loaded, Marked: marked, Discarded: discarded,
                Quarantined: quarantined, Errors: failures.Count, PagesProcessed: pagesProcessed,
                CycleCompleted: cycleCompleted, Reconciliation: reconciliation), cancellationToken);
            return new IaaINationalSyncResult(runId, startedAt, finishedAt, false, null, cycleId, observed, loaded, marked, discarded, quarantined, pagesProcessed, requests, cycleCompleted, reconciliation, rules, failures, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var deterministicCursorFailure = exception is ApibaraInvalidCursorException;
            failures.Add(deterministicCursorFailure ? $"cursor-invalid:{exception.Message}" : exception.Message);
            logger.LogError(exception, "IAAI national sync {RunId} failed after {Observed} vehicles.", runId, observed);
            var finishedAt = DateTimeOffset.UtcNow;
            var checkpoint = await snapshotStore.GetNationalSyncCheckpointAsync(StreamName, cancellationToken);
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(
                finishedAt, observed, requests, failures, Loaded: loaded, Marked: marked, Discarded: discarded,
                Quarantined: quarantined, Errors: failures.Count, PagesProcessed: pagesProcessed,
                CycleCompleted: checkpoint.CycleCompleted), cancellationToken);
            return new IaaINationalSyncResult(runId, startedAt, finishedAt, false, null, checkpoint.CycleId ?? Guid.Empty, observed, loaded, marked, discarded, quarantined, pagesProcessed, requests, checkpoint.CycleCompleted, null, rules, failures, !deterministicCursorFailure);
        }
        finally
        {
            await snapshotStore.ReleaseLeaseAsync(LeaseName, runId, DateTimeOffset.UtcNow, CancellationToken.None);
        }
    }

    private static int? TryGetRemainingRequests(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var numbers = element.EnumerateObject()
                .Where(property => property.Value.ValueKind == JsonValueKind.Number)
                .ToDictionary(property => property.Name, property => property.Value.GetDecimal(), StringComparer.OrdinalIgnoreCase);

            if (numbers.TryGetValue("remaining", out var remaining)) return decimal.ToInt32(remaining);
            var quota = numbers.TryGetValue("quota", out var quotaValue)
                ? quotaValue
                : numbers.TryGetValue("limit", out var limitValue) ? limitValue : (decimal?)null;
            if (quota is not null && numbers.TryGetValue("used", out var used)) return decimal.ToInt32(quota.Value - used);

            foreach (var property in element.EnumerateObject())
            {
                var nested = TryGetRemainingRequests(property.Value);
                if (nested is not null) return nested;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = TryGetRemainingRequests(item);
                if (nested is not null) return nested;
            }
        }

        return null;
    }
}
