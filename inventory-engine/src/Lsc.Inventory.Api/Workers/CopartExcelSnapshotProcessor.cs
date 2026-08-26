using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record CopartExcelProcessingResult(
    bool Processed,
    bool IsDuplicate,
    bool IsComplete,
    string? RejectionReason,
    int Observed,
    int Accepted,
    int Discarded,
    int Quarantined,
    int Marked,
    int Errors,
    TimeSpan Duration,
    InventoryReconciliationResult? Reconciliation,
    IReadOnlyDictionary<string, int> DiscardRuleCounts,
    IReadOnlyDictionary<string, int> FlagRuleCounts,
    IReadOnlyList<string> Failures);

public interface ICopartExcelSnapshotProcessor
{
    Task<CopartExcelProcessingResult> RunLatestAsync(CancellationToken cancellationToken);
    Task<CopartExcelProcessingResult> ProcessAsync(CopartSnapshotEnvelope snapshot, CancellationToken cancellationToken);
}

public sealed class CopartExcelSnapshotProcessor(
    ICopartExcelSnapshotSource snapshotSource,
    ICopartExcelSnapshotAdapter adapter,
    IInventorySnapshotStore snapshotStore,
    IOptions<CopartExcelOptions> options,
    ILogger<CopartExcelSnapshotProcessor> logger) : ICopartExcelSnapshotProcessor
{
    private readonly CopartExcelOptions _options = options.Value;

    public async Task<CopartExcelProcessingResult> RunLatestAsync(CancellationToken cancellationToken)
    {
        await using var lease = await snapshotSource.OpenLatestAsync(cancellationToken);
        return await ProcessAsync(lease.Snapshot, cancellationToken);
    }

    public async Task<CopartExcelProcessingResult> ProcessAsync(CopartSnapshotEnvelope snapshot, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var validation = await adapter.ValidateAsync(snapshot, cancellationToken);
        if (!validation.IsComplete)
        {
            return Failed(validation.Failures, validation.RowCount, startedAt, "Copart snapshot validation failed; no rows were persisted or reconciled.");
        }

        var registration = await snapshotStore.TryRegisterCopartSnapshotAsync(
            new CopartSnapshotReceipt(snapshot.FileName, snapshot.Sha256, snapshot.DownloadedAt, validation.FileSizeBytes, validation.RowCount, _options.ProcessingBatchSize),
            _options.MinimumRowCountRatioToRecentMedian,
            _options.RecentSnapshotCountForBaseline,
            cancellationToken);
        if (!registration.Accepted)
        {
            var reason = registration.RejectionReason ?? "Copart snapshot registration was rejected.";
            return new CopartExcelProcessingResult(false, registration.IsDuplicate, false, reason, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow - startedAt, null, new Dictionary<string, int>(), new Dictionary<string, int>(), [reason]);
        }

        var state = new ProcessingState();
        var batch = new List<AuctionVehicle>(_options.ProcessingBatchSize);
        var observedLotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        InventoryReconciliationResult? reconciliation = null;

        try
        {
            await foreach (var row in adapter.ReadAcceptedSnapshotAsync(snapshot, cancellationToken))
            {
                batch.Add(row);
                if (batch.Count >= _options.ProcessingBatchSize)
                {
                    await ProcessBatchAsync(batch, observedLotKeys, state, cancellationToken);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
                await ProcessBatchAsync(batch, observedLotKeys, state, cancellationToken);

            var isComplete = state.Errors == 0 && state.Failures.Count == 0 && state.Observed == validation.RowCount;
            if (isComplete)
                reconciliation = await snapshotStore.ReconcileSourceAsync(InventorySourcePolicy.CopartExcelSource, observedLotKeys, true, snapshot.DownloadedAt, cancellationToken);
            else
                logger.LogWarning("Copart snapshot {FileName} will not reconcile because it was not fully processed: observed {Observed} of {Expected}, errors {Errors}.", snapshot.FileName, state.Observed, validation.RowCount, state.Errors);

            await snapshotStore.CompleteCopartSnapshotAsync(registration.RunId!.Value,
                new CopartSnapshotCompletion(DateTimeOffset.UtcNow, state.Observed, state.Accepted, state.Discarded, state.Quarantined, state.Marked, state.Errors, isComplete, state.Failures),
                cancellationToken);

            return new CopartExcelProcessingResult(true, false, isComplete, null, state.Observed, state.Accepted, state.Discarded, state.Quarantined, state.Marked, state.Errors, DateTimeOffset.UtcNow - startedAt, reconciliation, state.DiscardRuleCounts, state.FlagRuleCounts, state.Failures);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state.Errors++;
            state.Failures.Add($"processing: {exception.Message}");
            await snapshotStore.CompleteCopartSnapshotAsync(registration.RunId!.Value,
                new CopartSnapshotCompletion(DateTimeOffset.UtcNow, state.Observed, state.Accepted, state.Discarded, state.Quarantined, state.Marked, state.Errors, false, state.Failures),
                cancellationToken);
            logger.LogError(exception, "Copart snapshot {FileName} failed after {Observed} observed rows.", snapshot.FileName, state.Observed);
            return new CopartExcelProcessingResult(false, false, false, "Copart processing failed; reconciliation was blocked.", state.Observed, state.Accepted, state.Discarded, state.Quarantined, state.Marked, state.Errors, DateTimeOffset.UtcNow - startedAt, null, state.DiscardRuleCounts, state.FlagRuleCounts, state.Failures);
        }
    }

    private async Task ProcessBatchAsync(IReadOnlyList<AuctionVehicle> batch, ISet<string> observedLotKeys, ProcessingState state, CancellationToken cancellationToken)
    {
        foreach (var row in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();
            state.Observed++;
            try
            {
                var vehicle = CanonicalVehicleCleaner.Clean(row);
                var evaluation = AuctionEligibilityEvaluator.Evaluate(vehicle);
                await snapshotStore.PersistEligibilityDecisionAsync(evaluation, DateTimeOffset.UtcNow, cancellationToken);

                foreach (var reason in evaluation.DiscardReasons) state.IncrementDiscardRule(reason.Code);
                foreach (var flag in evaluation.Flags) state.IncrementFlagRule(flag.Code);

                if (!evaluation.LoadToSystem)
                {
                    if (evaluation.Decision == "CUARENTENA") state.Quarantined++;
                    else state.Discarded++;
                    continue;
                }

                await snapshotStore.PersistAsync(vehicle, DateTimeOffset.UtcNow, cancellationToken);
                state.Accepted++;
                if (evaluation.Decision == "MARCAR") state.Marked++;
                if (!string.IsNullOrWhiteSpace(vehicle.LotNumber))
                    observedLotKeys.Add($"{InventorySourcePolicy.CopartExcelSource}:{vehicle.LotNumber}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                state.Errors++;
                state.Failures.Add($"row {state.Observed}: {exception.Message}");
                logger.LogError(exception, "Copart row {RowNumber} could not be persisted; snapshot reconciliation will be blocked.", state.Observed);
            }
        }
    }

    private static CopartExcelProcessingResult Failed(IReadOnlyList<string> failures, int rows, DateTimeOffset startedAt, string reason) =>
        new(false, false, false, reason, rows, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow - startedAt, null, new Dictionary<string, int>(), new Dictionary<string, int>(), failures.Append(reason).ToArray());

    private sealed class ProcessingState
    {
        public int Observed { get; set; }
        public int Accepted { get; set; }
        public int Discarded { get; set; }
        public int Quarantined { get; set; }
        public int Marked { get; set; }
        public int Errors { get; set; }
        public Dictionary<string, int> DiscardRuleCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> FlagRuleCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Failures { get; } = [];

        public void IncrementDiscardRule(string code) => DiscardRuleCounts[code] = DiscardRuleCounts.GetValueOrDefault(code) + 1;
        public void IncrementFlagRule(string code) => FlagRuleCounts[code] = FlagRuleCounts.GetValueOrDefault(code) + 1;
    }
}
