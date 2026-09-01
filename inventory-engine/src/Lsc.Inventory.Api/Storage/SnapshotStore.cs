using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Scoring;
using Lsc.Inventory.Api.Sources;

namespace Lsc.Inventory.Api.Storage;

public interface IInventorySnapshotStore
{
    Task<Guid> StartSyncRunAsync(InventorySyncRunStart start, CancellationToken cancellationToken);
    Task CompleteSyncRunAsync(Guid runId, InventorySyncRunCompletion completion, CancellationToken cancellationToken);
    Task<CopartSnapshotRegistration> TryRegisterCopartSnapshotAsync(CopartSnapshotReceipt receipt, decimal minimumRowCountRatio, int baselineSnapshotCount, bool allowInterruptedSnapshotRetry, CancellationToken cancellationToken);
    Task CompleteCopartSnapshotAsync(Guid runId, CopartSnapshotCompletion completion, CancellationToken cancellationToken);
    Task PersistProviderUsageAsync(string provider, JsonElement usage, DateTimeOffset capturedAt, CancellationToken cancellationToken);
    Task PersistEligibilityDecisionAsync(EligibilityEvaluation evaluation, DateTimeOffset evaluatedAt, CancellationToken cancellationToken);
    Task<EligibilityAuditPage> GetDiscardedEligibilityDecisionsAsync(int page, int pageSize, string? ruleCode, string? query, CancellationToken cancellationToken);
    Task<InventoryValidationReport> GetValidationReportAsync(CancellationToken cancellationToken);
    Task PersistAsync(AuctionVehicle vehicle, DateTimeOffset observedAt, CancellationToken cancellationToken);
    /// <summary>
    /// Persists an already eligible Copart lot together with its canonical LSC pre-grade.
    /// The production implementation commits the visible snapshot and score in one transaction.
    /// </summary>
    Task<CopartInlineScoringPersistenceResult> PersistCopartAcceptedWithScoringAsync(AuctionVehicle vehicle, EligibilityEvaluation eligibility, DateTimeOffset observedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartScoringBackfillCandidatesAsync(int maximum, CancellationToken cancellationToken);
    Task<CopartScoringCoverageReport> GetCopartScoringCoverageReportAsync(CancellationToken cancellationToken);
    Task<InventoryScoringBackfillResult> EnqueueScoringBackfillAsync(int maximum, CancellationToken cancellationToken);
    Task<InventoryScoringBatchResult> ProcessScoringBatchAsync(int maximum, CancellationToken cancellationToken);
    Task<InventoryScoringOperationalStatus> GetScoringOperationalStatusAsync(CancellationToken cancellationToken);
    Task<Guid> StartScoringRunAsync(string trigger, CancellationToken cancellationToken);
    Task CompleteScoringRunAsync(Guid runId, InventoryScoringRunCompletion completion, CancellationToken cancellationToken);
    Task<IReadOnlyList<InventoryScoringRunSummary>> GetRecentScoringRunsAsync(int maximum, CancellationToken cancellationToken);
    Task<LscVehicleScoringResult?> GetScoreByLotAsync(string lotNumber, CancellationToken cancellationToken);
    Task<LscVehicleScoringResult> PersistScoringResultAsync(AuctionVehicle vehicle, EligibilityEvaluation eligibility, DateTimeOffset sourceObservedAt, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartMediaCandidatesAsync(int maximum, CancellationToken cancellationToken);
    Task<bool> UpdateCopartMediaAsync(string identity, DateTimeOffset expectedObservedAt, AuctionVehicle vehicle, string resolutionStatus, CancellationToken cancellationToken);
    Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartTitleMappingCandidatesAsync(int maximum, CancellationToken cancellationToken);
    Task<bool> UpdateCopartTitleMappingAsync(string identity, DateTimeOffset expectedObservedAt, AuctionVehicle vehicle, CancellationToken cancellationToken);
    Task<int> RecordCopartAuctionObservationsAsync(IReadOnlyList<CopartAuctionObservation> observations, CancellationToken cancellationToken);
    Task FinalizeCopartAuctionAttemptsAsync(string snapshotSha256, DateTimeOffset finalizedAt, CancellationToken cancellationToken);
    Task<CopartAuctionHistoryBackfillResult> BackfillCopartAuctionObservationsAsync(int maximum, CancellationToken cancellationToken);
    Task<CopartAuctionHistoryReport> GetCopartAuctionHistoryReportAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<StoredVehicleSnapshot>> GetRecentAsync(int maximum, CancellationToken cancellationToken);
    Task<StoredVehicleSnapshot?> GetByPlatformAndLotAsync(string platform, string lotNumber, CancellationToken cancellationToken);
    Task<InventoryPage> GetPageAsync(InventoryBrowseQuery query, CancellationToken cancellationToken);
    Task<InventoryReconciliationResult> ReconcileSourceAsync(string platform, IReadOnlyCollection<string> observedLotKeys, bool isCompleteSnapshot, DateTimeOffset observedAt, CancellationToken cancellationToken);
}

public sealed record InventorySyncRunStart(
    string Provider,
    string Platform,
    string State,
    int PagesRequested,
    int PageSize,
    DateTimeOffset StartedAt,
    Guid? RunId = null);

public sealed record InventorySyncRunCompletion(
    DateTimeOffset FinishedAt,
    int VehiclesObserved,
    int RequestsIssued,
    IReadOnlyList<string> Failures);

public sealed record CopartSnapshotReceipt(
    string FileName,
    string Sha256,
    DateTimeOffset DownloadedAt,
    long FileSizeBytes,
    int RowCount,
    int ProcessingBatchSize);

public sealed record CopartSnapshotRegistration(
    bool Accepted,
    bool IsDuplicate,
    Guid? RunId,
    int? BaselineMedianRowCount,
    string? RejectionReason);

public sealed record CopartSnapshotCompletion(
    DateTimeOffset FinishedAt,
    int Observed,
    int Accepted,
    int Discarded,
    int Quarantined,
    int Marked,
    int Errors,
    bool IsComplete,
    IReadOnlyList<string> Failures,
    CopartInlineScoringMetrics? InlineScoring = null);

public sealed record StoredVehicleSnapshot(
    string Identity,
    DateTimeOffset ObservedAt,
    AuctionVehicle Vehicle,
    string RawJson);

public sealed record CopartInlineScoringPersistenceResult(
    string SnapshotChange,
    bool ScoredInline,
    bool ScoreSkippedUnchanged,
    LscVehicleScoringResult? Score,
    TimeSpan ScoringDuration);

/// <summary>
/// Copart-only metrics captured from facts observed during one complete snapshot run.
/// Counters are not synthesized for prior runs.
/// </summary>
public sealed record CopartScoringCoverageReport(
    long ActiveCopartLots,
    long CurrentPolicyScores,
    long PendingScores,
    IReadOnlyDictionary<string, long> StatusCounts);

public sealed record CopartScoringBackfillResult(
    int Scanned,
    int Scored,
    int ScoreSkippedUnchanged,
    int SkippedIneligible,
    int Failed,
    int Remaining,
    TimeSpan Duration,
    IReadOnlyList<string> Failures);

public sealed record CopartInlineScoringMetrics(
    int Created,
    int Updated,
    int Unchanged,
    int ScoredInline,
    int ScoreSkippedUnchanged,
    int ScoreFailed,
    long InlineScoringDurationMs,
    long? InlineScoringP50Ms,
    long? InlineScoringP95Ms);

public sealed record CopartMediaEnrichmentResult(
    bool Processed,
    int Candidates,
    int Resolved,
    int AlreadyComplete,
    int Failed,
    TimeSpan Duration,
    IReadOnlyList<string> Failures);

public sealed record CopartTitleBackfillResult(
    bool Processed,
    int Candidates,
    int Mapped,
    int Unmapped,
    int Skipped,
    int Failed,
    TimeSpan Duration,
    IReadOnlyList<string> Failures);

public sealed record CopartAuctionHistoryBackfillResult(
    bool Processed,
    int Candidates,
    int ObservationsInserted,
    int AttemptsDerived,
    int Failed,
    TimeSpan Duration,
    IReadOnlyList<string> Failures);

/// <summary>
/// Aggregate-only, internal verification report. It intentionally carries no VIN, seller, URL or raw payload data.
/// </summary>
public sealed record CopartAuctionHistoryReport(
    long Observations,
    long DistinctSnapshots,
    long Attempts,
    long Signals,
    IReadOnlyDictionary<string, long> AttemptsByOutcome,
    IReadOnlyDictionary<string, long> AttemptsByEvidenceLevel,
    IReadOnlyDictionary<string, long> SignalsByLevel);

public sealed record InventoryBrowseQuery(
    string? Platform,
    string? Search,
    int Page,
    int PageSize,
    string Sort,
    int? YearFrom = null,
    int? YearTo = null,
    decimal? MaximumBid = null,
    bool RequireBid = false,
    bool RequirePhotos = false,
    bool IncludeSpecialTitles = false,
    IReadOnlyList<string>? Makes = null,
    IReadOnlyList<string>? Models = null,
    IReadOnlyList<string>? Facilities = null,
    IReadOnlyList<string>? States = null,
    IReadOnlyList<string>? VehicleTypes = null,
    IReadOnlyList<string>? Damages = null,
    IReadOnlyList<string>? TitleTypes = null,
    IReadOnlyList<string>? Drives = null,
    IReadOnlyList<string>? Transmissions = null,
    IReadOnlyList<string>? Fuels = null,
    decimal? OdometerFrom = null,
    decimal? OdometerTo = null,
    DateOnly? AuctionFrom = null,
    DateOnly? AuctionTo = null,
    decimal? EstimatedTotalFrom = null,
    decimal? EstimatedTotalTo = null);

public sealed record InventoryPage(
    int Page,
    int PageSize,
    long Total,
    int TotalPages,
    IReadOnlyList<StoredVehicleSnapshot> Vehicles);

public sealed record InventorySampleLot(
    string LotKey,
    string? Vin,
    string? Title,
    string? LocationState,
    decimal? CurrentBidUsd,
    DateTimeOffset? AuctionAt,
    string? Damage,
    decimal? Odometer,
    int? MediaPhotosCount);

public sealed record InventoryValidationReport(
    long Lots,
    long Versions,
    long VinPresent,
    long TitlePresent,
    long DamagePresent,
    long OdometerPresent,
    long CurrentBidPresent,
    long AuctionDatePresent,
    long LotsWithPhotos,
    IReadOnlyList<InventorySampleLot> Samples);

public sealed record EligibilityAuditItem(
    DateTimeOffset EvaluatedAt,
    EligibilityEvaluation Evaluation);

public sealed record EligibilityRuleSummary(
    string Code,
    string Name,
    long Count);

public sealed record EligibilityAuditPage(
    int Page,
    int PageSize,
    long Total,
    int TotalPages,
    IReadOnlyList<EligibilityAuditItem> Items,
    IReadOnlyList<EligibilityRuleSummary> RuleSummary);

public sealed record InventoryReconciliationResult(
    string Platform,
    bool Applied,
    int Observed,
    int Reactivated,
    int MissesIncremented,
    int Deactivated);

public sealed record InventoryScoringBackfillResult(int Requested, int Enqueued, int AlreadyCurrent);

public sealed record InventoryScoringBatchResult(
    int Claimed,
    int Completed,
    int Failed,
    int Skipped,
    int Remaining,
    int HighPriorityClaimed = 0,
    int BackfillClaimed = 0);

public sealed record InventoryScoringPlatformStatus(
    string Platform,
    long Active,
    long Current,
    long Queued,
    long Processing,
    long Failed,
    long Pending,
    long HighPriorityQueued,
    DateTimeOffset? OldestQueuedAt,
    DateTimeOffset? LastScoredAt);

public sealed record InventoryScoringRunSummary(
    Guid RunId,
    string Trigger,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int BackfillRequested,
    int BackfillEnqueued,
    int Claimed,
    int Completed,
    int Failed,
    int Skipped,
    int Remaining,
    int HighPriorityClaimed,
    int BackfillClaimed,
    string? Error);

public sealed record InventoryScoringRunCompletion(
    string Status,
    DateTimeOffset FinishedAt,
    int BackfillRequested,
    int BackfillEnqueued,
    int Claimed,
    int Completed,
    int Failed,
    int Skipped,
    int Remaining,
    int HighPriorityClaimed,
    int BackfillClaimed,
    string? Error = null);

public sealed record InventoryScoringOperationalStatus(
    string PolicyVersion,
    long Queued,
    long Processing,
    long Completed,
    long Failed,
    DateTimeOffset? LastScoredAt,
    IReadOnlyList<InventoryScoringPlatformStatus>? Platforms = null,
    DateTimeOffset? OldestQueuedAt = null,
    IReadOnlyList<InventoryScoringRunSummary>? RecentRuns = null);

public sealed class InMemorySnapshotStore : IInventorySnapshotStore
{
    private readonly ConcurrentDictionary<string, StoredVehicleSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, EligibilityAuditItem> _eligibility = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Platform, bool Active, int MissingCount)> _lifecycle = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CopartSnapshotReceipt> _copartSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _copartSnapshotStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> _copartRuns = new();
    private readonly ConcurrentDictionary<Guid, (InventorySyncRunStart Start, InventorySyncRunCompletion? Completion)> _syncRuns = new();
    private readonly ConcurrentDictionary<string, CopartAuctionObservation> _copartAuctionObservations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (AuctionVehicle Vehicle, DateTimeOffset ObservedAt, int Attempts, int Priority)> _scoringQueue = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LscVehicleScoringResult> _scores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, (string Trigger, DateTimeOffset StartedAt, InventoryScoringRunCompletion? Completion)> _scoringRuns = new();

    public IReadOnlyDictionary<Guid, (InventorySyncRunStart Start, InventorySyncRunCompletion? Completion)> SyncRuns => _syncRuns;
    public int CopartAuctionObservationCount => _copartAuctionObservations.Count;
    public bool FailNextCopartInlineScoringPersistence { get; set; }
    public bool FailNextScoringPersistence { get; set; }

    public Task<Guid> StartSyncRunAsync(InventorySyncRunStart start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runId = start.RunId ?? Guid.NewGuid();
        _syncRuns[runId] = (start, null);
        return Task.FromResult(runId);
    }

    public Task CompleteSyncRunAsync(Guid runId, InventorySyncRunCompletion completion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _syncRuns.AddOrUpdate(
            runId,
            _ => (new InventorySyncRunStart("unknown", "unknown", "unknown", 0, 0, completion.FinishedAt, runId), completion),
            (_, existing) => (existing.Start, completion));
        return Task.CompletedTask;
    }

    public Task<CopartSnapshotRegistration> TryRegisterCopartSnapshotAsync(CopartSnapshotReceipt receipt, decimal minimumRowCountRatio, int baselineSnapshotCount, bool allowInterruptedSnapshotRetry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_copartSnapshots.ContainsKey(receipt.Sha256) &&
            (!_copartSnapshotStatuses.TryGetValue(receipt.Sha256, out var status) ||
             (!string.Equals(status, "completed_with_errors", StringComparison.OrdinalIgnoreCase) &&
              !(allowInterruptedSnapshotRetry && string.Equals(status, "running", StringComparison.OrdinalIgnoreCase)))))
        {
            return Task.FromResult(new CopartSnapshotRegistration(false, true, null, null, "F02: Copart snapshot hash was already processed."));
        }

        var historicalRows = _copartSnapshots
            .Where(snapshot => _copartSnapshotStatuses.TryGetValue(snapshot.Key, out var snapshotStatus) &&
                string.Equals(snapshotStatus, "succeeded", StringComparison.OrdinalIgnoreCase))
            .Select(snapshot => snapshot.Value)
            .OrderByDescending(snapshot => snapshot.DownloadedAt)
            .Take(Math.Max(1, baselineSnapshotCount))
            .Select(snapshot => snapshot.RowCount)
            .OrderBy(value => value)
            .ToArray();
        var median = historicalRows.Length == 0 ? (int?)null : historicalRows[historicalRows.Length / 2];
        if (median is > 0 && receipt.RowCount < decimal.Ceiling(median.Value * minimumRowCountRatio))
            return Task.FromResult(new CopartSnapshotRegistration(false, false, null, median, "F05: Copart snapshot row count is below the accepted baseline."));

        _copartSnapshots[receipt.Sha256] = receipt;
        _copartSnapshotStatuses[receipt.Sha256] = "running";
        var runId = Guid.NewGuid();
        _copartRuns[runId] = receipt.Sha256;
        return Task.FromResult(new CopartSnapshotRegistration(true, false, runId, median, null));
    }

    public Task CompleteCopartSnapshotAsync(Guid runId, CopartSnapshotCompletion completion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_copartRuns.TryGetValue(runId, out var sha256))
        {
            _copartSnapshotStatuses[sha256] = completion.Errors == 0 && completion.IsComplete
                ? "succeeded"
                : "completed_with_errors";
        }
        return Task.CompletedTask;
    }

    public Task PersistProviderUsageAsync(string provider, JsonElement usage, DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task PersistEligibilityDecisionAsync(EligibilityEvaluation evaluation, DateTimeOffset evaluatedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = $"{evaluation.AuctionSource ?? "unknown"}:{evaluation.LotNumber ?? "unknown"}";
        _eligibility[identity] = new EligibilityAuditItem(evaluatedAt, evaluation);
        return Task.CompletedTask;
    }

    public Task<EligibilityAuditPage> GetDiscardedEligibilityDecisionsAsync(int page, int pageSize, string? ruleCode, string? query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        var normalizedRule = string.IsNullOrWhiteSpace(ruleCode) ? null : ruleCode.Trim().ToUpperInvariant();
        var normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var discarded = _eligibility.Values
            .Where(item => item.Evaluation.Decision == "DESCARTAR")
            .Where(item => normalizedRule is null || item.Evaluation.DiscardReasons.Any(reason => reason.Code == normalizedRule))
            .Where(item => normalizedQuery is null ||
                (item.Evaluation.LotNumber?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (item.Evaluation.VinMasked?.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(item => item.EvaluatedAt)
            .ToArray();
        var summary = discarded
            .SelectMany(item => item.Evaluation.DiscardReasons)
            .GroupBy(reason => new { reason.Code, reason.Name })
            .Select(group => new EligibilityRuleSummary(group.Key.Code, group.Key.Name, group.LongCount()))
            .OrderBy(item => item.Code)
            .ToArray();
        var items = discarded.Skip((safePage - 1) * safePageSize).Take(safePageSize).ToArray();
        return Task.FromResult(new EligibilityAuditPage(safePage, safePageSize, discarded.LongLength, Math.Max(1, (int)Math.Ceiling(discarded.LongLength / (double)safePageSize)), items, summary));
    }

    public Task<InventoryValidationReport> GetValidationReportAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = GetRecentAsync(100, cancellationToken).GetAwaiter().GetResult();
        var samples = snapshots
            .Take(5)
            .Select(snapshot => new InventorySampleLot(
                snapshot.Identity,
                snapshot.Vehicle.Vin,
                snapshot.Vehicle.Title,
                snapshot.Vehicle.Location?.State,
                snapshot.Vehicle.Pricing?.CurrentBidUsd,
                snapshot.Vehicle.Auction?.AuctionAt,
                snapshot.Vehicle.Damage,
                snapshot.Vehicle.Odometer,
                snapshot.Vehicle.Media?.ThumbnailsCount))
            .ToArray();
        return Task.FromResult(new InventoryValidationReport(
            snapshots.Count,
            snapshots.Count,
            snapshots.Count(snapshot => !string.IsNullOrWhiteSpace(snapshot.Vehicle.Vin)),
            snapshots.Count(snapshot => !string.IsNullOrWhiteSpace(snapshot.Vehicle.Title)),
            snapshots.Count(snapshot => !string.IsNullOrWhiteSpace(snapshot.Vehicle.Damage)),
            snapshots.Count(snapshot => snapshot.Vehicle.Odometer.HasValue),
            snapshots.Count(snapshot => snapshot.Vehicle.Pricing?.CurrentBidUsd.HasValue == true),
            snapshots.Count(snapshot => snapshot.Vehicle.Auction?.AuctionAt.HasValue == true),
            snapshots.Count(snapshot => snapshot.Vehicle.Media?.ThumbnailsCount > 0),
            samples));
    }

    public Task PersistAsync(AuctionVehicle vehicle, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var identity = string.Join(':',
            vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown",
            vehicle.LotNumber?.Trim() ?? vehicle.Vin?.Trim() ?? Guid.NewGuid().ToString("N"));

        var snapshot = new StoredVehicleSnapshot(
            identity,
            observedAt,
            vehicle,
            JsonSerializer.Serialize(vehicle));

        _snapshots[identity] = snapshot;
        _lifecycle[identity] = (vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown", true, 0);
        _scoringQueue[identity] = (vehicle, observedAt, 0, 100);
        return Task.CompletedTask;
    }

    public Task<CopartInlineScoringPersistenceResult> PersistCopartAcceptedWithScoringAsync(AuctionVehicle vehicle, EligibilityEvaluation eligibility, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(vehicle.Platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Inline scoring persistence is restricted to Copart Excel lots.");
        if (!eligibility.LoadToSystem)
            throw new InvalidOperationException("Only eligible Copart lots may be persisted with inline scoring.");
        if (FailNextCopartInlineScoringPersistence)
        {
            FailNextCopartInlineScoringPersistence = false;
            throw new InvalidOperationException("Injected Copart inline scoring persistence failure.");
        }

        var identity = string.Join(':', vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown", vehicle.LotNumber?.Trim() ?? vehicle.Vin?.Trim() ?? "unknown");
        var rawJson = JsonSerializer.Serialize(vehicle);
        var snapshotChange = !_snapshots.TryGetValue(identity, out var existing)
            ? "created"
            : string.Equals(existing.RawJson, rawJson, StringComparison.Ordinal) ? "unchanged" : "updated";
        var inputHash = LscVehicleScoringEngine.CreateInputHash(vehicle, eligibility);

        _snapshots[identity] = new StoredVehicleSnapshot(identity, observedAt, vehicle, rawJson);
        _lifecycle[identity] = (InventorySourcePolicy.CopartExcelSource, true, 0);
        if (_scores.TryGetValue(identity, out var current) &&
            string.Equals(current.PolicyVersion, LscScoringPolicy.Version, StringComparison.Ordinal) &&
            string.Equals(current.InputHash, inputHash, StringComparison.Ordinal))
        {
            return Task.FromResult(new CopartInlineScoringPersistenceResult(snapshotChange, false, true, current, TimeSpan.Zero));
        }

        var startedAt = Stopwatch.GetTimestamp();
        var score = LscVehicleScoringEngine.Evaluate(vehicle, eligibility, observedAt);
        var duration = Stopwatch.GetElapsedTime(startedAt);
        _scores[identity] = score;
        _scoringQueue.TryRemove(identity, out _);
        return Task.FromResult(new CopartInlineScoringPersistenceResult(snapshotChange, true, false, score, duration));
    }

    public Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartScoringBackfillCandidatesAsync(int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = _snapshots.Values
            .Where(snapshot => string.Equals(snapshot.Vehicle.Platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase))
            .Where(snapshot =>
            {
                var eligibility = AuctionEligibilityEvaluator.Evaluate(snapshot.Vehicle, snapshot.ObservedAt);
                if (!eligibility.LoadToSystem) return false;
                var inputHash = LscVehicleScoringEngine.CreateInputHash(snapshot.Vehicle, eligibility);
                return !_scores.TryGetValue(snapshot.Identity, out var score) ||
                       !string.Equals(score.PolicyVersion, LscScoringPolicy.Version, StringComparison.Ordinal) ||
                       !string.Equals(score.InputHash, inputHash, StringComparison.Ordinal) ||
                       score.ScoredAt < snapshot.ObservedAt;
            })
            .OrderBy(snapshot => snapshot.ObservedAt)
            .ThenBy(snapshot => snapshot.Identity, StringComparer.Ordinal)
            .Take(Math.Clamp(maximum, 1, 10_000))
            .ToArray();
        return Task.FromResult<IReadOnlyList<StoredVehicleSnapshot>>(candidates);
    }

    public Task<CopartScoringCoverageReport> GetCopartScoringCoverageReportAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = _snapshots.Values
            .Where(snapshot => string.Equals(snapshot.Vehicle.Platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase))
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .ToArray();
        var scores = active
            .Select(snapshot => new { snapshot, score = _scores.TryGetValue(snapshot.Identity, out var stored) ? stored : null })
            .Where(item => item.score is not null)
            .Where(item =>
            {
                var eligibility = AuctionEligibilityEvaluator.Evaluate(item.snapshot.Vehicle, item.snapshot.ObservedAt);
                return eligibility.LoadToSystem &&
                       string.Equals(item.score!.PolicyVersion, LscScoringPolicy.Version, StringComparison.Ordinal) &&
                       string.Equals(item.score.InputHash, LscVehicleScoringEngine.CreateInputHash(item.snapshot.Vehicle, eligibility), StringComparison.Ordinal);
            })
            .ToArray();
        var statuses = scores
            .GroupBy(item => item.score!.Status, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (long)group.Count(), StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(new CopartScoringCoverageReport(active.LongLength, scores.LongLength, active.LongLength - scores.LongLength, statuses));
    }

    public Task<InventoryScoringBackfillResult> EnqueueScoringBackfillAsync(int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .OrderBy(snapshot => snapshot.ObservedAt)
            .Take(Math.Clamp(maximum, 1, 10_000))
            .ToArray();
        var enqueued = 0;
        var current = 0;
        foreach (var snapshot in candidates)
        {
            var eligibility = AuctionEligibilityEvaluator.Evaluate(snapshot.Vehicle);
            var inputHash = LscVehicleScoringEngine.CreateInputHash(snapshot.Vehicle, eligibility);
            if (_scores.TryGetValue(snapshot.Identity, out var score) &&
                score.PolicyVersion == LscScoringPolicy.Version && score.InputHash == inputHash)
            {
                current++;
                continue;
            }
            _scoringQueue.AddOrUpdate(snapshot.Identity,
                _ => (snapshot.Vehicle, snapshot.ObservedAt, 0, 10),
                (_, existing) => existing.Priority >= 100 ? existing : (snapshot.Vehicle, snapshot.ObservedAt, existing.Attempts, 10));
            enqueued++;
        }
        return Task.FromResult(new InventoryScoringBackfillResult(candidates.Length, enqueued, current));
    }

    public Task<InventoryScoringBatchResult> ProcessScoringBatchAsync(int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = _scoringQueue
            .OrderByDescending(item => item.Value.Priority)
            .ThenBy(item => item.Value.ObservedAt)
            .Take(Math.Clamp(maximum, 1, 500))
            .ToArray();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eligibility = AuctionEligibilityEvaluator.Evaluate(candidate.Value.Vehicle);
            _scores[candidate.Key] = LscVehicleScoringEngine.Evaluate(candidate.Value.Vehicle, eligibility);
            _scoringQueue.TryRemove(candidate.Key, out _);
        }
        return Task.FromResult(new InventoryScoringBatchResult(
            candidates.Length, candidates.Length, 0, 0, _scoringQueue.Count,
            candidates.Count(item => item.Value.Priority >= 100), candidates.Count(item => item.Value.Priority < 100)));
    }

    public Task<InventoryScoringOperationalStatus> GetScoringOperationalStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = _snapshots.Values.Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active).ToArray();
        var platforms = active.GroupBy(snapshot => snapshot.Vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var items = group.ToArray();
                var current = items.Count(item => _scores.TryGetValue(item.Identity, out var score)
                    && score.PolicyVersion == LscScoringPolicy.Version
                    && score.InputHash == LscVehicleScoringEngine.CreateInputHash(item.Vehicle, AuctionEligibilityEvaluator.Evaluate(item.Vehicle)));
                var queued = items.Where(item => _scoringQueue.ContainsKey(item.Identity)).ToArray();
                return new InventoryScoringPlatformStatus(group.Key, items.Length, current, queued.Length, 0, 0, items.Length - current,
                    queued.Count(item => _scoringQueue[item.Identity].Priority >= 100),
                    queued.Select(item => (DateTimeOffset?)_scoringQueue[item.Identity].ObservedAt).DefaultIfEmpty().Min(),
                    items.Select(item => _scores.TryGetValue(item.Identity, out var score) ? score.ScoredAt : (DateTimeOffset?)null).Max());
            }).ToArray();
        var runs = _scoringRuns.Select(item => ToScoringRunSummary(item.Key, item.Value)).OrderByDescending(item => item.StartedAt).Take(10).ToArray();
        return Task.FromResult(new InventoryScoringOperationalStatus(LscScoringPolicy.Version, _scoringQueue.Count, 0, _scores.Count, 0,
            _scores.Values.Select(score => (DateTimeOffset?)score.ScoredAt).DefaultIfEmpty().Max(), platforms,
            _scoringQueue.Values.Select(item => (DateTimeOffset?)item.ObservedAt).DefaultIfEmpty().Min(), runs));
    }

    public Task<Guid> StartScoringRunAsync(string trigger, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runId = Guid.NewGuid();
        _scoringRuns[runId] = (trigger, DateTimeOffset.UtcNow, null);
        return Task.FromResult(runId);
    }

    public Task CompleteScoringRunAsync(Guid runId, InventoryScoringRunCompletion completion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_scoringRuns.TryGetValue(runId, out var run)) _scoringRuns[runId] = (run.Trigger, run.StartedAt, completion);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<InventoryScoringRunSummary>> GetRecentScoringRunsAsync(int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<InventoryScoringRunSummary>>(_scoringRuns
            .Select(item => ToScoringRunSummary(item.Key, item.Value))
            .OrderByDescending(item => item.StartedAt).Take(Math.Clamp(maximum, 1, 100)).ToArray());
    }

    public Task<LscVehicleScoringResult?> GetScoreByLotAsync(string lotNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _snapshots.Values.Where(item => string.Equals(item.Vehicle.LotNumber, lotNumber, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ObservedAt).FirstOrDefault();
        return Task.FromResult(snapshot is null || !_scores.TryGetValue(snapshot.Identity, out var score) ? null : score);
    }

    public Task<LscVehicleScoringResult> PersistScoringResultAsync(AuctionVehicle vehicle, EligibilityEvaluation eligibility, DateTimeOffset sourceObservedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailNextScoringPersistence)
        {
            FailNextScoringPersistence = false;
            throw new InvalidOperationException("Injected scoring persistence failure.");
        }
        var outcome = LscVehicleScoringEngine.Evaluate(vehicle, eligibility, sourceObservedAt);
        var identity = string.Join(':', vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown", vehicle.LotNumber?.Trim() ?? vehicle.Vin?.Trim() ?? "unknown");
        _scores[identity] = outcome;
        return Task.FromResult(outcome);
    }

    private static InventoryScoringRunSummary ToScoringRunSummary(Guid runId, (string Trigger, DateTimeOffset StartedAt, InventoryScoringRunCompletion? Completion) run)
    {
        var completion = run.Completion;
        return new InventoryScoringRunSummary(runId, run.Trigger, completion?.Status ?? "running", run.StartedAt, completion?.FinishedAt,
            completion?.BackfillRequested ?? 0, completion?.BackfillEnqueued ?? 0, completion?.Claimed ?? 0, completion?.Completed ?? 0,
            completion?.Failed ?? 0, completion?.Skipped ?? 0, completion?.Remaining ?? 0, completion?.HighPriorityClaimed ?? 0,
            completion?.BackfillClaimed ?? 0, completion?.Error);
    }

    public Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartMediaCandidatesAsync(int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = _snapshots.Values
            .Where(snapshot => string.Equals(snapshot.Vehicle.Platform, "copart", StringComparison.OrdinalIgnoreCase))
            .Where(snapshot => snapshot.Vehicle.AdditionalData is null || !snapshot.Vehicle.AdditionalData.ContainsKey("copart_media_resolution"))
            .Where(snapshot => snapshot.Vehicle.Media?.Photos?.Count is <= 1 or null)
            .OrderByDescending(snapshot => snapshot.ObservedAt)
            .Take(Math.Clamp(maximum, 1, 10000))
            .ToArray();
        return Task.FromResult<IReadOnlyList<StoredVehicleSnapshot>>(candidates);
    }

    public Task<bool> UpdateCopartMediaAsync(string identity, DateTimeOffset expectedObservedAt, AuctionVehicle vehicle, string resolutionStatus, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_snapshots.TryGetValue(identity, out var snapshot) || snapshot.ObservedAt != expectedObservedAt) return Task.FromResult(false);
        var additional = vehicle.AdditionalData is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(vehicle.AdditionalData);
        additional["copart_media_resolution"] = JsonSerializer.SerializeToElement(resolutionStatus);
        var enriched = vehicle with { AdditionalData = additional };
        _snapshots[identity] = snapshot with { Vehicle = enriched, RawJson = JsonSerializer.Serialize(enriched) };
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartTitleMappingCandidatesAsync(int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidates = _snapshots.Values
            .Where(snapshot => string.Equals(snapshot.Vehicle.Platform, "copart", StringComparison.OrdinalIgnoreCase))
            .Where(snapshot => snapshot.Vehicle.AdditionalData is null ||
                !snapshot.Vehicle.AdditionalData.TryGetValue("source_title_mapping_version", out var mappingVersion) ||
                mappingVersion.ValueKind != JsonValueKind.String ||
                !string.Equals(mappingVersion.GetString(), CopartTitleCatalog.Version, StringComparison.Ordinal) ||
                !snapshot.Vehicle.AdditionalData.TryGetValue("title_taxonomy_version", out var taxonomyVersion) ||
                taxonomyVersion.ValueKind != JsonValueKind.String ||
                !string.Equals(taxonomyVersion.GetString(), CopartTitleTaxonomy.Version, StringComparison.Ordinal))
            .OrderBy(snapshot => snapshot.Identity, StringComparer.Ordinal)
            .Take(Math.Clamp(maximum, 1, 10_000))
            .ToArray();
        return Task.FromResult<IReadOnlyList<StoredVehicleSnapshot>>(candidates);
    }

    public Task<bool> UpdateCopartTitleMappingAsync(string identity, DateTimeOffset expectedObservedAt, AuctionVehicle vehicle, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(vehicle.Platform, "copart", StringComparison.OrdinalIgnoreCase) ||
            !_snapshots.TryGetValue(identity, out var current) || current.ObservedAt != expectedObservedAt)
            return Task.FromResult(false);
        _snapshots[identity] = current with { Vehicle = vehicle, RawJson = JsonSerializer.Serialize(vehicle) };
        return Task.FromResult(true);
    }

    public Task<int> RecordCopartAuctionObservationsAsync(IReadOnlyList<CopartAuctionObservation> observations, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inserted = 0;
        foreach (var observation in observations)
            if (_copartAuctionObservations.TryAdd($"{observation.SnapshotSha256}:{observation.LotKey}", observation)) inserted++;
        return Task.FromResult(inserted);
    }

    public Task<CopartAuctionHistoryReport> GetCopartAuctionHistoryReportAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CopartAuctionHistoryReport(
            _copartAuctionObservations.Count,
            _copartAuctionObservations.Keys.Select(key => key[..key.IndexOf(':')]).Distinct(StringComparer.OrdinalIgnoreCase).LongCount(),
            0,
            0,
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)));
    }

    public Task FinalizeCopartAuctionAttemptsAsync(string snapshotSha256, DateTimeOffset finalizedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task<CopartAuctionHistoryBackfillResult> BackfillCopartAuctionObservationsAsync(int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new CopartAuctionHistoryBackfillResult(true, 0, 0, 0, 0, TimeSpan.Zero, []));
    }

    public Task<IReadOnlyCollection<StoredVehicleSnapshot>> GetRecentAsync(int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyCollection<StoredVehicleSnapshot> result = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .OrderByDescending(snapshot => snapshot.ObservedAt)
            .Take(Math.Clamp(maximum, 1, 5000))
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<StoredVehicleSnapshot?> GetByPlatformAndLotAsync(string platform, string lotNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _snapshots.Values.FirstOrDefault(item =>
            string.Equals(item.Vehicle.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(item.Vehicle.LotNumber, lotNumber, StringComparison.OrdinalIgnoreCase) &&
            (!_lifecycle.TryGetValue(item.Identity, out var lifecycle) || lifecycle.Active));
        return Task.FromResult(snapshot);
    }

    public Task<InventoryPage> GetPageAsync(InventoryBrowseQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safePage = Math.Max(1, query.Page);
        var safePageSize = Math.Clamp(query.PageSize, 1, 100);
        var all = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .Where(snapshot => MatchesBrowseQuery(snapshot.Vehicle, query));
        var ordered = query.Sort switch
        {
            "bid-low" => all.OrderBy(snapshot => snapshot.Vehicle.Pricing?.CurrentBidUsd ?? decimal.MaxValue).ThenByDescending(snapshot => snapshot.ObservedAt),
            "bid-high" => all.OrderByDescending(snapshot => snapshot.Vehicle.Pricing?.CurrentBidUsd ?? decimal.MinValue).ThenByDescending(snapshot => snapshot.ObservedAt),
            _ => all.OrderBy(snapshot => snapshot.Vehicle.Auction?.AuctionAt ?? DateTimeOffset.MaxValue).ThenByDescending(snapshot => snapshot.ObservedAt)
        };
        var snapshots = ordered.ToArray();
        var total = snapshots.LongLength;
        return Task.FromResult(new InventoryPage(safePage, safePageSize, total, Math.Max(1, (int)Math.Ceiling(total / (double)safePageSize)), snapshots.Skip((safePage - 1) * safePageSize).Take(safePageSize).ToArray()));
    }

    private static bool MatchesBrowseQuery(AuctionVehicle vehicle, InventoryBrowseQuery query)
    {
        static bool AnyMatch(IReadOnlyList<string>? values, string? candidate) => values is null || values.Count == 0 || values.Any(value => string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase));
        var platform = vehicle.Platform?.Trim();
        if (!string.IsNullOrWhiteSpace(query.Platform) && !string.Equals(query.Platform, "all", StringComparison.OrdinalIgnoreCase) && !string.Equals(platform, query.Platform, StringComparison.OrdinalIgnoreCase)) return false;
        var search = query.Search?.Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var searchable = string.Join(' ', vehicle.LotNumber, vehicle.Title, vehicle.Make, vehicle.Model, vehicle.SaleDocument?.Name);
            if (!searchable.Contains(search, StringComparison.OrdinalIgnoreCase)) return false;
        }
        if (query.YearFrom is not null && (vehicle.Year is null || vehicle.Year < query.YearFrom)) return false;
        if (query.YearTo is not null && (vehicle.Year is null || vehicle.Year > query.YearTo)) return false;
        var currentBid = vehicle.Pricing?.CurrentBidUsd;
        if (query.MaximumBid is not null && currentBid is not null && currentBid > query.MaximumBid) return false;
        if (query.RequireBid && vehicle.Pricing?.CurrentBidUsd is null) return false;
        if (query.RequirePhotos && vehicle.Media?.ThumbnailsCount is not > 0) return false;
        if (query.OdometerFrom is not null && (vehicle.Odometer is null || vehicle.Odometer < query.OdometerFrom)) return false;
        if (query.OdometerTo is not null && (vehicle.Odometer is null || vehicle.Odometer > query.OdometerTo)) return false;
        if (query.AuctionFrom is not null && (vehicle.Auction?.AuctionAt is null || DateOnly.FromDateTime(vehicle.Auction.AuctionAt.Value.UtcDateTime) < query.AuctionFrom)) return false;
        if (query.AuctionTo is not null && (vehicle.Auction?.AuctionAt is null || DateOnly.FromDateTime(vehicle.Auction.AuctionAt.Value.UtcDateTime) > query.AuctionTo)) return false;
        if (query.EstimatedTotalFrom is not null && (vehicle.Pricing?.CurrentBidUsd is null || vehicle.Pricing.CurrentBidUsd + 699m < query.EstimatedTotalFrom)) return false;
        if (query.EstimatedTotalTo is not null && (vehicle.Pricing?.CurrentBidUsd is null || vehicle.Pricing.CurrentBidUsd + 399m > query.EstimatedTotalTo)) return false;
        if (!AnyMatch(query.Makes, vehicle.Make) || !AnyMatch(query.Models, vehicle.Model) || !AnyMatch(query.States, vehicle.Location?.State) || !AnyMatch(query.VehicleTypes, vehicle.VehicleType) || !AnyMatch(query.Damages, vehicle.Damage) || !AnyMatch(query.TitleTypes, vehicle.SaleDocument?.Name) || !AnyMatch(query.Drives, vehicle.DriveType) || !AnyMatch(query.Transmissions, vehicle.Transmission) || !AnyMatch(query.Fuels, vehicle.FuelType)) return false;
        if (query.Facilities is { Count: > 0 } && !query.Facilities.Any(value => string.Equals(value, vehicle.Location?.FacilityId, StringComparison.OrdinalIgnoreCase) || string.Equals(value, vehicle.Location?.Display, StringComparison.OrdinalIgnoreCase))) return false;
        if (!query.IncludeSpecialTitles && (vehicle.SaleDocument?.Name?.Contains("CERTIFICATE OF DESTRUCTION", StringComparison.OrdinalIgnoreCase) == true || vehicle.SaleDocument?.Name?.Contains("JUNK", StringComparison.OrdinalIgnoreCase) == true || vehicle.SaleDocument?.Name?.Contains("NON REPAIRABLE", StringComparison.OrdinalIgnoreCase) == true || vehicle.SaleDocument?.Name?.Contains("PARTS ONLY", StringComparison.OrdinalIgnoreCase) == true)) return false;
        return true;
    }

    public Task<InventoryReconciliationResult> ReconcileSourceAsync(string platform, IReadOnlyCollection<string> observedLotKeys, bool isCompleteSnapshot, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPlatform = platform.Trim().ToLowerInvariant();
        if (!isCompleteSnapshot)
            return Task.FromResult(new InventoryReconciliationResult(normalizedPlatform, false, observedLotKeys.Count, 0, 0, 0));

        var observed = observedLotKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reactivated = 0;
        var incremented = 0;
        var deactivated = 0;
        foreach (var entry in _lifecycle.ToArray().Where(entry => entry.Value.Platform == normalizedPlatform))
        {
            if (observed.Contains(entry.Key))
            {
                if (!entry.Value.Active) reactivated++;
                _lifecycle[entry.Key] = (normalizedPlatform, true, 0);
                continue;
            }

            var missingCount = entry.Value.MissingCount + 1;
            var active = missingCount < 3;
            incremented++;
            if (entry.Value.Active && !active) deactivated++;
            _lifecycle[entry.Key] = (normalizedPlatform, active, missingCount);
        }

        return Task.FromResult(new InventoryReconciliationResult(normalizedPlatform, true, observed.Count, reactivated, incremented, deactivated));
    }
}
