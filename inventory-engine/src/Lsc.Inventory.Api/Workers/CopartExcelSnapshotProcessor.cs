using System.Diagnostics;
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
    IReadOnlyList<string> Failures,
    CopartInlineScoringMetrics? InlineScoring = null,
    CopartTitleTaxonomyMetrics? TitleTaxonomy = null,
    CopartIncrementalMetrics? Incremental = null);

public interface ICopartExcelSnapshotProcessor
{
    Task<CopartExcelProcessingResult> RunLatestAsync(CancellationToken cancellationToken);
    Task<CopartExcelProcessingResult> RunLatestFutureOnlyAsync(CancellationToken cancellationToken);
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
        await using var processingLease = await snapshotStore.TryAcquireCopartProcessingLeaseAsync(cancellationToken);
        if (processingLease is null)
            return await SkippedForProcessingLeaseAsync(cancellationToken);

        await using var snapshotLease = await snapshotSource.OpenLatestAsync(cancellationToken);
        return await ProcessCoreAsync(snapshotLease.Snapshot, cancellationToken, futureOnly: false);
    }

    public async Task<CopartExcelProcessingResult> RunLatestFutureOnlyAsync(CancellationToken cancellationToken)
    {
        await using var processingLease = await snapshotStore.TryAcquireCopartProcessingLeaseAsync(cancellationToken);
        if (processingLease is null)
            return await SkippedForProcessingLeaseAsync(cancellationToken);

        await using var snapshotLease = await snapshotSource.OpenLatestAsync(cancellationToken);
        return await ProcessCoreAsync(snapshotLease.Snapshot, cancellationToken, futureOnly: true);
    }

    public async Task<CopartExcelProcessingResult> ProcessAsync(CopartSnapshotEnvelope snapshot, CancellationToken cancellationToken)
    {
        await using var processingLease = await snapshotStore.TryAcquireCopartProcessingLeaseAsync(cancellationToken);
        if (processingLease is null)
            return await SkippedForProcessingLeaseAsync(cancellationToken);

        return await ProcessCoreAsync(snapshot, cancellationToken, futureOnly: false);
    }

    private async Task<CopartExcelProcessingResult> ProcessCoreAsync(CopartSnapshotEnvelope snapshot, CancellationToken cancellationToken, bool futureOnly)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var validation = await adapter.ValidateAsync(snapshot, cancellationToken);
        if (!validation.IsComplete)
        {
            var reason = "Copart snapshot validation failed; no rows were persisted or reconciled.";
            var validationRunId = await snapshotStore.StartSyncRunAsync(
                new InventorySyncRunStart("copart-excel", InventorySourcePolicy.CopartExcelSource, "snapshot-validation", 1, _options.ProcessingBatchSize, startedAt),
                cancellationToken);
            var finishedAt = DateTimeOffset.UtcNow;
            await snapshotStore.CompleteSyncRunAsync(
                validationRunId,
                new InventorySyncRunCompletion(finishedAt, validation.RowCount, 1, validation.Failures.Append(reason).ToArray()),
                cancellationToken);
            return Failed(validation.Failures, validation.RowCount, startedAt, reason);
        }

        var registration = await snapshotStore.TryRegisterCopartSnapshotAsync(
            new CopartSnapshotReceipt(snapshot.FileName, snapshot.Sha256, snapshot.DownloadedAt, validation.FileSizeBytes, validation.RowCount, _options.ProcessingBatchSize),
            _options.MinimumRowCountRatioToRecentMedian,
            _options.RecentSnapshotCountForBaseline,
            _options.AllowInterruptedSnapshotRetry,
            cancellationToken);
        if (!registration.Accepted)
        {
            var reason = registration.RejectionReason ?? "Copart snapshot registration was rejected.";
            var registrationState = registration.IsDuplicate ? "duplicate" : "snapshot-registration";
            var registrationRunId = await snapshotStore.StartSyncRunAsync(
                new InventorySyncRunStart("copart-excel", InventorySourcePolicy.CopartExcelSource, registrationState, 1, _options.ProcessingBatchSize, startedAt),
                cancellationToken);
            var finishedAt = DateTimeOffset.UtcNow;
            await snapshotStore.CompleteSyncRunAsync(
                registrationRunId,
                new InventorySyncRunCompletion(finishedAt, validation.RowCount, 1, registration.IsDuplicate ? [] : [reason]),
                cancellationToken);
            return new CopartExcelProcessingResult(false, registration.IsDuplicate, false, reason, 0, 0, 0, 0, 0, 0, finishedAt - startedAt, null, new Dictionary<string, int>(), new Dictionary<string, int>(), [reason]);
        }

        var executionRunId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart(
                Provider: "copart-excel",
                Platform: InventorySourcePolicy.CopartExcelSource,
                State: "all",
                PagesRequested: 1,
                PageSize: _options.ProcessingBatchSize,
                StartedAt: startedAt,
                RunId: registration.RunId),
            cancellationToken);

        var state = new ProcessingState();
        var batch = new List<AuctionVehicle>(_options.ProcessingBatchSize);
        var observedLotKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batchNumber = 0;
        logger.LogInformation("Copart snapshot {FileName} entering row processing; expected rows {ExpectedRows}, batch size {BatchSize}, persistence concurrency {PersistenceConcurrency}.", snapshot.FileName, validation.RowCount, _options.ProcessingBatchSize, _options.PersistenceConcurrency);
        InventoryReconciliationResult? reconciliation = null;

        try
        {
            var cutoffDate = futureOnly ? GetTomorrowLocalDate() : (DateOnly?)null;
            await foreach (var row in adapter.ReadAcceptedSnapshotAsync(snapshot, cancellationToken))
            {
                if (cutoffDate is not null && !IsOnOrAfterLocalDate(row.Auction?.AuctionAt, cutoffDate.Value))
                {
                    state.FutureDateSkipped++;
                    continue;
                }
                batch.Add(row);
                if (batch.Count >= _options.ProcessingBatchSize)
                {
                    batchNumber++;
                    var batchStartedAt = Stopwatch.GetTimestamp();
                    logger.LogInformation("Copart snapshot {FileName} starting batch {BatchNumber}; observed before batch {Observed}.", snapshot.FileName, batchNumber, state.Observed);
                    await ProcessBatchAsync(batch, observedLotKeys, state, snapshot.Sha256, snapshot.DownloadedAt, cancellationToken);
                    logger.LogInformation("Copart snapshot {FileName} completed batch {BatchNumber}; observed {Observed}, accepted {Accepted}, discarded {Discarded}, watermark candidates {WatermarkCandidates}, skipped {WatermarkSkipped}, fallback {WatermarkFallback}, errors {Errors}, duration {DurationMs} ms.", snapshot.FileName, batchNumber, state.Observed, state.Accepted, state.Discarded, state.WatermarkCandidates, state.WatermarkSkipped, state.WatermarkFallback, state.Errors, Stopwatch.GetElapsedTime(batchStartedAt).TotalMilliseconds);
                    batch.Clear();
                }
            }
            if (batch.Count > 0)
            {
                batchNumber++;
                var batchStartedAt = Stopwatch.GetTimestamp();
                logger.LogInformation("Copart snapshot {FileName} starting final batch {BatchNumber}; observed before batch {Observed}.", snapshot.FileName, batchNumber, state.Observed);
                await ProcessBatchAsync(batch, observedLotKeys, state, snapshot.Sha256, snapshot.DownloadedAt, cancellationToken);
                logger.LogInformation("Copart snapshot {FileName} completed final batch {BatchNumber}; observed {Observed}, accepted {Accepted}, discarded {Discarded}, watermark candidates {WatermarkCandidates}, skipped {WatermarkSkipped}, fallback {WatermarkFallback}, errors {Errors}, duration {DurationMs} ms.", snapshot.FileName, batchNumber, state.Observed, state.Accepted, state.Discarded, state.WatermarkCandidates, state.WatermarkSkipped, state.WatermarkFallback, state.Errors, Stopwatch.GetElapsedTime(batchStartedAt).TotalMilliseconds);
            }

            var isComplete = state.Errors == 0 && state.Failures.Count == 0 && (futureOnly || state.Observed == validation.RowCount);
            if (isComplete && !futureOnly)
                reconciliation = await snapshotStore.ReconcileSourceAsync(InventorySourcePolicy.CopartExcelSource, observedLotKeys, true, snapshot.DownloadedAt, cancellationToken);
            else
                logger.LogWarning("Copart snapshot {FileName} will not reconcile because it was not fully processed or is future-only: observed {Observed} of {Expected}, future-date skipped {FutureDateSkipped}, errors {Errors}.", snapshot.FileName, state.Observed, validation.RowCount, state.FutureDateSkipped, state.Errors);

            var finishedAt = DateTimeOffset.UtcNow;
            await snapshotStore.CompleteCopartSnapshotAsync(registration.RunId!.Value,
                new CopartSnapshotCompletion(finishedAt, state.Observed, state.Accepted, state.Discarded, state.Quarantined, state.Marked, state.Errors, isComplete, state.Failures, state.BuildInlineScoringMetrics(), state.BuildTaxonomyMetrics(), state.BuildIncrementalMetrics()),
                cancellationToken);
            if (isComplete)
            {
                try
                {
                    await snapshotStore.FinalizeCopartAuctionAttemptsAsync(snapshot.Sha256, finishedAt, cancellationToken);
                }
                catch (Exception historyException) when (historyException is not OperationCanceledException)
                {
                    logger.LogError(historyException, "Copart auction-history derivation failed after snapshot {FileName} completed; the next history backfill can recover it.", snapshot.FileName);
                }
            }
            await snapshotStore.CompleteSyncRunAsync(
                executionRunId,
                new InventorySyncRunCompletion(finishedAt, state.Observed, 1, state.Failures),
                cancellationToken);

            return new CopartExcelProcessingResult(true, false, isComplete, null, state.Observed, state.Accepted, state.Discarded, state.Quarantined, state.Marked, state.Errors, finishedAt - startedAt, reconciliation, state.DiscardRuleCounts, state.FlagRuleCounts, state.Failures, state.BuildInlineScoringMetrics(), state.BuildTaxonomyMetrics(), state.BuildIncrementalMetrics());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            state.Errors++;
            state.Failures.Add($"processing: {exception.Message}");
            var finishedAt = DateTimeOffset.UtcNow;
            await snapshotStore.CompleteCopartSnapshotAsync(registration.RunId!.Value,
                new CopartSnapshotCompletion(finishedAt, state.Observed, state.Accepted, state.Discarded, state.Quarantined, state.Marked, state.Errors, false, state.Failures, state.BuildInlineScoringMetrics(), state.BuildTaxonomyMetrics(), state.BuildIncrementalMetrics()),
                cancellationToken);
            await snapshotStore.CompleteSyncRunAsync(
                executionRunId,
                new InventorySyncRunCompletion(finishedAt, state.Observed, 1, state.Failures),
                cancellationToken);
            logger.LogError(exception, "Copart snapshot {FileName} failed after {Observed} observed rows.", snapshot.FileName, state.Observed);
            return new CopartExcelProcessingResult(false, false, false, "Copart processing failed; reconciliation was blocked.", state.Observed, state.Accepted, state.Discarded, state.Quarantined, state.Marked, state.Errors, finishedAt - startedAt, null, state.DiscardRuleCounts, state.FlagRuleCounts, state.Failures, state.BuildInlineScoringMetrics(), state.BuildTaxonomyMetrics(), state.BuildIncrementalMetrics());
        }
    }

    private async Task<CopartExcelProcessingResult> SkippedForProcessingLeaseAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        const string reason = "SKIPPED_LOCK_HELD: another Copart snapshot processor is active.";
        var runId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart(
                Provider: "copart-excel",
                Platform: InventorySourcePolicy.CopartExcelSource,
                State: "skipped_lock_held",
                PagesRequested: 0,
                PageSize: _options.ProcessingBatchSize,
                StartedAt: startedAt),
            cancellationToken);
        var finishedAt = DateTimeOffset.UtcNow;
        await snapshotStore.CompleteSyncRunAsync(
            runId,
            new InventorySyncRunCompletion(finishedAt, 0, 0, []),
            cancellationToken);
        logger.LogInformation("Copart snapshot invocation skipped because another Copart processor holds the distributed lease.");
        return new CopartExcelProcessingResult(false, false, false, reason, 0, 0, 0, 0, 0, 0, finishedAt - startedAt, null, new Dictionary<string, int>(), new Dictionary<string, int>(), [reason]);
    }

    private async Task ProcessBatchAsync(IReadOnlyList<AuctionVehicle> batch, ISet<string> observedLotKeys, ProcessingState state, string snapshotSha256, DateTimeOffset snapshotDownloadedAt, CancellationToken cancellationToken)
    {
        var observations = new List<CopartAuctionObservation>(batch.Count);
        var watermarkUpdates = new List<CopartLotWatermarkUpdate>(batch.Count);
        var concurrency = Math.Clamp(_options.PersistenceConcurrency, 1, 64);
        var processingVersion = CopartLotWatermarkPolicy.CurrentProcessingVersion;
        var lotKeys = batch.Select(BuildCopartLotKey).Where(static key => key is not null).Cast<string>().ToArray();
        var watermarkStates = await snapshotStore.GetCopartLotWatermarkStatesAsync(lotKeys, processingVersion, cancellationToken);

        for (var offset = 0; offset < batch.Count; offset += concurrency)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var window = batch.Skip(offset).Take(concurrency).ToArray();
            var firstRowNumber = state.Observed + 1;
            var outcomes = await Task.WhenAll(window.Select((row, index) =>
            {
                var rowNumber = firstRowNumber + index;
                var lotKey = BuildCopartLotKey(row);
                var rowFingerprint = CopartLotWatermarkPolicy.ComputeRowFingerprint(row);
                var sourceUpdatedAt = CopartLotWatermarkPolicy.GetSourceUpdatedAt(row);
                var fallback = lotKey is null || sourceUpdatedAt is null;
                if (!fallback && watermarkStates.TryGetValue(lotKey!, out var existing) &&
                    sourceUpdatedAt!.Value <= existing.SourceUpdatedAt &&
                    string.Equals(rowFingerprint, existing.RowFingerprint, StringComparison.Ordinal))
                {
                    return Task.FromResult(new RowProcessingOutcome(
                        rowNumber,
                        row,
                        existing.Eligibility,
                        null,
                        null,
                        rowFingerprint,
                        WatermarkSkipped: true,
                        WatermarkFallback: false));
                }
                return ProcessRowAsync(row, rowNumber, fallback, rowFingerprint, cancellationToken);
            }));

            foreach (var outcome in outcomes)
            {
                state.Observed++;
                if (outcome.WatermarkSkipped) state.RecordWatermarkSkipped();
                else state.RecordWatermarkCandidate(outcome.WatermarkFallback);

                if (outcome.Evaluation is not null)
                {
                    foreach (var reason in outcome.Evaluation.DiscardReasons) state.IncrementDiscardRule(reason.Code);
                    foreach (var flag in outcome.Evaluation.Flags) state.IncrementFlagRule(flag.Code);
                }

                if (outcome.Exception is not null)
                {
                    state.Errors++;
                    if (outcome.Evaluation?.LoadToSystem == true) state.ScoreFailed++;
                    state.Failures.Add($"row {outcome.RowNumber}: {outcome.Exception.Message}");
                    logger.LogError(outcome.Exception, "Copart row {RowNumber} could not be persisted; snapshot reconciliation will be blocked.", outcome.RowNumber);
                    continue;
                }

                if (outcome.Evaluation is null) continue;
                if (!outcome.Evaluation.LoadToSystem)
                {
                    if (outcome.Evaluation.Decision == "CUARENTENA") state.Quarantined++;
                    else state.Discarded++;
                }
                else
                {
                    state.Accepted++;
                    if (outcome.WatermarkSkipped)
                    {
                        state.RecordWatermarkSkippedAccepted();
                        state.RecordTaxonomy(CopartTitleMapper.ApplyTaxonomy(outcome.Vehicle!));
                    }
                    else
                    {
                        state.RecordInlineScoring(outcome.Persistence!);
                        state.RecordTaxonomy(outcome.Vehicle!);
                    }
                    if (outcome.Evaluation.Decision == "MARCAR") state.Marked++;
                    if (!string.IsNullOrWhiteSpace(outcome.Vehicle!.LotNumber))
                        observedLotKeys.Add($"{InventorySourcePolicy.CopartExcelSource}:{outcome.Vehicle.LotNumber}");
                }

                if (!string.IsNullOrWhiteSpace(outcome.Vehicle?.LotNumber))
                {
                    var observation = CopartAuctionObservationFactory.Create(outcome.Vehicle, snapshotSha256, snapshotDownloadedAt);
                    if (observation is not null && outcome.Evaluation.LoadToSystem) observations.Add(observation);
                }

                var outcomeSourceUpdatedAt = outcome.Vehicle is null ? null : CopartLotWatermarkPolicy.GetSourceUpdatedAt(outcome.Vehicle);
                if (!outcome.WatermarkSkipped && outcomeSourceUpdatedAt is not null)
                {
                    var lotKey = BuildCopartLotKey(outcome.Vehicle!);
                    if (lotKey is not null)
                    {
                        var sourceUpdatedAt = outcomeSourceUpdatedAt.Value;
                        if (watermarkStates.TryGetValue(lotKey, out var existing) && existing.SourceUpdatedAt > sourceUpdatedAt)
                            sourceUpdatedAt = existing.SourceUpdatedAt;
                        watermarkUpdates.Add(new CopartLotWatermarkUpdate(lotKey, sourceUpdatedAt, outcome.RowFingerprint, processingVersion, outcome.Evaluation));
                    }
                }
            }
        }

        await snapshotStore.RecordCopartAuctionObservationsAsync(observations, cancellationToken);
        await snapshotStore.PersistCopartLotWatermarksAsync(watermarkUpdates, cancellationToken);
    }

    private async Task<RowProcessingOutcome> ProcessRowAsync(AuctionVehicle row, int rowNumber, bool watermarkFallback, string rowFingerprint, CancellationToken cancellationToken)
    {
        AuctionVehicle? vehicle = null;
        EligibilityEvaluation? evaluation = null;
        using var rowCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        rowCts.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            vehicle = CanonicalVehicleCleaner.Clean(row);
            evaluation = AuctionEligibilityEvaluator.Evaluate(vehicle);
            logger.LogDebug("Copart row {RowNumber} evaluated as {Decision}; persisting eligibility.", rowNumber, evaluation.Decision);
            await snapshotStore.PersistEligibilityDecisionAsync(evaluation, DateTimeOffset.UtcNow, rowCts.Token);
            CopartInlineScoringPersistenceResult? persistence = null;
            if (evaluation.LoadToSystem)
            {
                vehicle = CopartTitleMapper.ApplyTaxonomy(vehicle);
                logger.LogDebug("Copart row {RowNumber} persisting accepted lot and inline scoring.", rowNumber);
                persistence = await snapshotStore.PersistCopartAcceptedWithScoringAsync(vehicle, evaluation, DateTimeOffset.UtcNow, rowCts.Token);
            }
            return new RowProcessingOutcome(rowNumber, vehicle, evaluation, persistence, null, rowFingerprint, WatermarkSkipped: false, WatermarkFallback: watermarkFallback);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RowProcessingOutcome(rowNumber, vehicle, evaluation, null, new TimeoutException($"Copart row {rowNumber} exceeded the 2-minute persistence timeout."), rowFingerprint, WatermarkSkipped: false, WatermarkFallback: watermarkFallback);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new RowProcessingOutcome(rowNumber, vehicle, evaluation, null, exception, rowFingerprint, WatermarkSkipped: false, WatermarkFallback: watermarkFallback);
        }
    }

    private static CopartExcelProcessingResult Failed(IReadOnlyList<string> failures, int rows, DateTimeOffset startedAt, string reason) =>
        new(false, false, false, reason, rows, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow - startedAt, null, new Dictionary<string, int>(), new Dictionary<string, int>(), failures.Append(reason).ToArray());

    private static string? BuildCopartLotKey(AuctionVehicle vehicle)
    {
        var lotNumber = vehicle.LotNumber?.Trim();
        return string.IsNullOrWhiteSpace(lotNumber) || !lotNumber.All(char.IsDigit)
            ? null
            : $"{InventorySourcePolicy.CopartExcelSource}:{lotNumber}";
    }

    private sealed record RowProcessingOutcome(
        int RowNumber,
        AuctionVehicle? Vehicle,
        EligibilityEvaluation? Evaluation,
        CopartInlineScoringPersistenceResult? Persistence,
        Exception? Exception,
        string RowFingerprint,
        bool WatermarkSkipped,
        bool WatermarkFallback);

    private DateOnly GetTomorrowLocalDate()
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(_options.BackfillTimeZoneId);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone).DateTime).AddDays(1);
    }

    private static bool IsOnOrAfterLocalDate(DateTimeOffset? auctionAt, DateOnly cutoffDate)
    {
        if (auctionAt is null) return false;
        var date = DateOnly.FromDateTime(auctionAt.Value.DateTime);
        return date >= cutoffDate;
    }

    private sealed class ProcessingState
    {
        public int Observed { get; set; }
        public int FutureDateSkipped { get; set; }
        public int Accepted { get; set; }
        public int Discarded { get; set; }
        public int Quarantined { get; set; }
        public int Marked { get; set; }
        public int Errors { get; set; }
        public Dictionary<string, int> DiscardRuleCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, int> FlagRuleCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Failures { get; } = [];
        public int Created { get; private set; }
        public int Updated { get; private set; }
        public int Unchanged { get; private set; }
        public int ScoredInline { get; private set; }
        public int ScoreSkippedUnchanged { get; private set; }
        public int ScoreFailed { get; set; }
        public int TaxonomyClassified { get; private set; }
        public int TaxonomyUnverified { get; private set; }
        public int TaxonomyReviewRequired { get; private set; }
        public int WatermarkCandidates { get; private set; }
        public int WatermarkSkipped { get; private set; }
        public int WatermarkFallback { get; private set; }
        private readonly Dictionary<string, int> _taxonomyCategoryCounts = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<long> _inlineScoringDurationsMs = [];

        public void RecordWatermarkCandidate(bool fallback)
        {
            WatermarkCandidates++;
            if (fallback) WatermarkFallback++;
        }

        public void RecordWatermarkSkipped() => WatermarkSkipped++;

        public void RecordWatermarkSkippedAccepted()
        {
            Unchanged++;
            ScoreSkippedUnchanged++;
        }

        public void RecordInlineScoring(CopartInlineScoringPersistenceResult result)
        {
            switch (result.SnapshotChange)
            {
                case "created": Created++; break;
                case "updated": Updated++; break;
                case "unchanged": Unchanged++; break;
            }
            if (result.ScoredInline)
            {
                ScoredInline++;
                _inlineScoringDurationsMs.Add(Math.Max(0L, (long)Math.Ceiling(result.ScoringDuration.TotalMilliseconds)));
            }
            if (result.ScoreSkippedUnchanged) ScoreSkippedUnchanged++;
        }

        public void RecordTaxonomy(AuctionVehicle vehicle)
        {
            if (vehicle.AdditionalData is null ||
                !vehicle.AdditionalData.TryGetValue("title_category", out var categoryElement) ||
                categoryElement.ValueKind != System.Text.Json.JsonValueKind.String ||
                string.IsNullOrWhiteSpace(categoryElement.GetString())) return;

            var category = categoryElement.GetString()!;
            _taxonomyCategoryCounts[category] = _taxonomyCategoryCounts.GetValueOrDefault(category) + 1;
            var reviewStatus = vehicle.AdditionalData.TryGetValue("title_review_status", out var reviewElement) &&
                               reviewElement.ValueKind == System.Text.Json.JsonValueKind.String
                ? reviewElement.GetString()
                : null;
            if (string.Equals(reviewStatus, CopartTitleMapper.UnverifiedReviewStatus, StringComparison.OrdinalIgnoreCase)) TaxonomyUnverified++;
            else if (string.Equals(reviewStatus, CopartTitleMapper.ReviewRequiredStatus, StringComparison.OrdinalIgnoreCase)) TaxonomyReviewRequired++;
            else TaxonomyClassified++;
        }

        public CopartTitleTaxonomyMetrics BuildTaxonomyMetrics() =>
            new(TaxonomyClassified, TaxonomyUnverified, TaxonomyReviewRequired,
                new Dictionary<string, int>(_taxonomyCategoryCounts, StringComparer.OrdinalIgnoreCase));

        public CopartIncrementalMetrics BuildIncrementalMetrics() =>
            new(WatermarkCandidates, WatermarkSkipped, WatermarkFallback, CopartLotWatermarkPolicy.CurrentProcessingVersion);

        public CopartInlineScoringMetrics BuildInlineScoringMetrics()
        {
            var sorted = _inlineScoringDurationsMs.Order().ToArray();
            long? Percentile(decimal percent) => sorted.Length == 0
                ? null
                : sorted[(int)Math.Ceiling((sorted.Length - 1) * percent)];
            return new CopartInlineScoringMetrics(
                Created, Updated, Unchanged, ScoredInline, ScoreSkippedUnchanged, ScoreFailed,
                sorted.Sum(), Percentile(0.50m), Percentile(0.95m));
        }

        public void IncrementDiscardRule(string code) => DiscardRuleCounts[code] = DiscardRuleCounts.GetValueOrDefault(code) + 1;
        public void IncrementFlagRule(string code) => FlagRuleCounts[code] = FlagRuleCounts.GetValueOrDefault(code) + 1;
    }
}
