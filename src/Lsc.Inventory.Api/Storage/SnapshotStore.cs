using System.Collections.Concurrent;
using System.Text.Json;
using System.Globalization;
using System.Diagnostics;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Scoring;

namespace Lsc.Inventory.Api.Storage;

public interface IInventorySnapshotStore
{
    Task<Guid> StartSyncRunAsync(InventorySyncRunStart start, CancellationToken cancellationToken);
    Task CompleteSyncRunAsync(Guid runId, InventorySyncRunCompletion completion, CancellationToken cancellationToken);
    Task RecordSyncRunEventAsync(InventorySyncRunEvent syncEvent, CancellationToken cancellationToken);
    Task<InventoryExecutionHistoryPage> GetExecutionHistoryAsync(InventoryExecutionHistoryRequest request, CancellationToken cancellationToken);
    Task<InventoryExecutionEventPage> GetExecutionEventsAsync(Guid runId, int page, int pageSize, CancellationToken cancellationToken);
    Task<CopartSnapshotRegistration> TryRegisterCopartSnapshotAsync(CopartSnapshotReceipt receipt, decimal minimumRowCountRatio, int baselineSnapshotCount, CancellationToken cancellationToken);
    Task CompleteCopartSnapshotAsync(Guid runId, CopartSnapshotCompletion completion, CancellationToken cancellationToken);
    Task PersistProviderUsageAsync(string provider, JsonElement usage, DateTimeOffset capturedAt, CancellationToken cancellationToken);
    Task PersistEligibilityDecisionAsync(EligibilityEvaluation evaluation, DateTimeOffset evaluatedAt, CancellationToken cancellationToken);
    Task<EligibilityAuditPage> GetDiscardedEligibilityDecisionsAsync(int page, int pageSize, string? ruleCode, string? query, CancellationToken cancellationToken);
    Task<InventoryValidationReport> GetValidationReportAsync(CancellationToken cancellationToken);
    Task<InventoryLotPersistenceResult> PersistAsync(AuctionVehicle vehicle, DateTimeOffset observedAt, CancellationToken cancellationToken, Guid? runId = null);
    Task<IReadOnlyCollection<StoredVehicleSnapshot>> GetRecentAsync(int maximum, CancellationToken cancellationToken);
    Task<InventorySearchPage> SearchAsync(InventorySearchRequest request, CancellationToken cancellationToken);
    Task<InventorySearchSummary> GetInventorySearchSummaryAsync(InventorySearchRequest request, CancellationToken cancellationToken);
    Task<InventoryFacetsV2Response> GetInventoryFacetsV2Async(InventoryFacetsV2Request request, CancellationToken cancellationToken);
    Task<InventorySearchProjectionStatus> GetSearchProjectionStatusAsync(CancellationToken cancellationToken);
    Task<CopartTitleTaxonomyCoverage> GetCopartTitleTaxonomyCoverageAsync(CancellationToken cancellationToken);
    Task<SellerTaxonomyAudit> GetSellerTaxonomyAuditAsync(CancellationToken cancellationToken);
    Task<InventorySearchProjectionStatus> RebuildSearchProjectionAsync(CancellationToken cancellationToken);
    Task<InventoryScoringBackfillResult> EnqueueScoringBackfillAsync(int maximum, CancellationToken cancellationToken);
    Task<InventoryScoringBatchResult> ProcessScoringBatchAsync(int maximum, CancellationToken cancellationToken);
    Task<InventoryScoringOperationalStatus> GetScoringOperationalStatusAsync(CancellationToken cancellationToken);
    Task<Guid> StartScoringRunAsync(string trigger, CancellationToken cancellationToken);
    Task CompleteScoringRunAsync(Guid runId, InventoryScoringRunCompletion completion, CancellationToken cancellationToken);
    Task<IReadOnlyList<InventoryScoringRunSummary>> GetRecentScoringRunsAsync(int maximum, CancellationToken cancellationToken);
    Task<LscVehicleScoringResult?> GetScoreByLotAsync(string lotNumber, CancellationToken cancellationToken);
    Task<StoredVehicleSnapshot?> GetByLotAsync(string lotNumber, CancellationToken cancellationToken);
    Task<StoredVehicleSnapshot?> GetByPlatformAndLotAsync(string platform, string lotNumber, CancellationToken cancellationToken);
    Task<InventoryReconciliationResult> ReconcileSourceAsync(string platform, IReadOnlyCollection<string> observedLotKeys, bool isCompleteSnapshot, DateTimeOffset observedAt, CancellationToken cancellationToken, Guid? runId = null);
    Task<int> DeactivateArchivedLotsAsync(string platform, IReadOnlyCollection<string> lotKeys, DateTimeOffset archivedAt, CancellationToken cancellationToken, Guid? runId = null);
    Task<InventorySyncLease> TryAcquireLeaseAsync(string leaseName, Guid ownerRunId, DateTimeOffset acquiredAt, TimeSpan duration, CancellationToken cancellationToken);
    Task ReleaseLeaseAsync(string leaseName, Guid ownerRunId, DateTimeOffset releasedAt, CancellationToken cancellationToken);
    Task<NationalSyncCheckpoint> GetNationalSyncCheckpointAsync(string streamName, CancellationToken cancellationToken);
    Task<NationalSyncOperationalStatus> GetNationalSyncOperationalStatusAsync(string streamName, CancellationToken cancellationToken);
    Task PersistNationalSyncBatchAsync(NationalSyncBatch batch, CancellationToken cancellationToken);
    Task<InventoryReconciliationResult> CompleteNationalSyncCycleAsync(string streamName, Guid cycleId, DateTimeOffset completedAt, CancellationToken cancellationToken, Guid? runId = null);
}

public sealed record InventorySyncRunStart(
    string Provider,
    string Platform,
    string State,
    int PagesRequested,
    int PageSize,
    DateTimeOffset StartedAt);

public sealed record InventorySyncRunCompletion(
    DateTimeOffset FinishedAt,
    int VehiclesObserved,
    int RequestsIssued,
    IReadOnlyList<string> Failures,
    int? Loaded = null,
    int? Marked = null,
    int? Discarded = null,
    int? Quarantined = null,
    int? Errors = null,
    int? PagesProcessed = null,
    bool? CycleCompleted = null,
    InventoryReconciliationResult? Reconciliation = null,
    bool Cancelled = false);

public sealed record InventoryLotPersistenceResult(string LotKey, string Action, IReadOnlyList<string> ChangedFields);

public sealed record InventorySyncRunEvent(
    Guid RunId,
    string Platform,
    string LotKey,
    string? LotNumber,
    string? VinMasked,
    string Action,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyList<string> RuleCodes,
    DateTimeOffset OccurredAt);

public sealed record InventoryExecutionHistoryRequest(int Page, int PageSize, string? Platform = null, string? Status = null);

public sealed record InventoryExecutionSummary(
    Guid RunId,
    string Provider,
    string Platform,
    string Scope,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int Observed,
    int Requests,
    int? Loaded,
    int? Created,
    int? Updated,
    int? Unchanged,
    int? Marked,
    int? Discarded,
    int? Quarantined,
    int? Errors,
    int? Reactivated,
    int? MissesIncremented,
    int? Deactivated,
    int? PagesProcessed,
    bool? CycleCompleted,
    IReadOnlyList<string> Failures);

public sealed record InventoryExecutionHistoryPage(int Page, int PageSize, long Total, int TotalPages, IReadOnlyList<InventoryExecutionSummary> Items);

public sealed record InventoryExecutionEvent(
    DateTimeOffset OccurredAt,
    string Platform,
    string? LotNumber,
    string? VinMasked,
    string Action,
    IReadOnlyList<string> ChangedFields,
    IReadOnlyList<string> RuleCodes);

public sealed record InventoryExecutionEventPage(int Page, int PageSize, long Total, int TotalPages, IReadOnlyList<InventoryExecutionEvent> Items);

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
    IReadOnlyList<string> Failures);

public sealed record StoredVehicleSnapshot(
    string Identity,
    DateTimeOffset ObservedAt,
    AuctionVehicle Vehicle,
    string RawJson,
    LscScoringSummary? Scoring = null);

public sealed record LscScoringSummary(
    string Status,
    decimal? PreGrade,
    decimal? BuyScore,
    decimal MaxPointsEvaluable,
    decimal CoveragePercent,
    decimal ConfidencePercent,
    string? Category,
    string PolicyVersion,
    DateTimeOffset ScoredAt);

public sealed record InventorySearchRequest(
    int Page,
    int PageSize,
    string? Query = null,
    string? Platform = null,
    string? Sort = null,
    IReadOnlyCollection<string>? Makes = null,
    IReadOnlyCollection<string>? Models = null,
    IReadOnlyCollection<string>? VehicleTypes = null,
    IReadOnlyCollection<string>? Titles = null,
    IReadOnlyCollection<string>? States = null,
    IReadOnlyCollection<string>? Facilities = null,
    IReadOnlyCollection<string>? PrimaryDamages = null,
    IReadOnlyCollection<string>? SecondaryDamages = null,
    IReadOnlyCollection<string>? SellerTypes = null,
    IReadOnlyCollection<string>? EngineLayouts = null,
    IReadOnlyCollection<string>? Cylinders = null,
    int? YearFrom = null,
    int? YearTo = null,
    decimal? OdometerFrom = null,
    decimal? OdometerTo = null,
    decimal? PriceFrom = null,
    decimal? PriceTo = null,
    DateTimeOffset? AuctionFrom = null,
    DateTimeOffset? AuctionTo = null,
    bool? BuyNowOnly = null,
    IReadOnlyCollection<string>? Transmissions = null,
    IReadOnlyCollection<string>? Fuels = null,
    IReadOnlyCollection<string>? Drives = null,
    IReadOnlyCollection<string>? BodyStyles = null,
    IReadOnlyCollection<string>? Colors = null,
    IReadOnlyCollection<string>? LossTypes = null,
    IReadOnlyCollection<string>? StartCodes = null,
    IReadOnlyCollection<string>? RunConditions = null,
    bool? WithPhotosOnly = null,
    string? AuctionStatus = null,
    bool? WithBidOnly = null,
    string? KeyMode = null,
    decimal? ProviderEstimateFrom = null,
    decimal? ProviderEstimateTo = null,
    decimal? EngineSizeFrom = null,
    decimal? EngineSizeTo = null,
    decimal? HorsepowerFrom = null,
    decimal? HorsepowerTo = null,
    decimal? MaxCurrentBid = null,
    bool ExcludeSpecialTitles = false,
    decimal? PreGradeFrom = null,
    IReadOnlyCollection<string>? ScoringStatuses = null,
    IReadOnlyCollection<string>? TitleCategories = null,
    decimal? BuyNowFrom = null,
    decimal? BuyNowTo = null,
    IReadOnlyCollection<string>? SellerNames = null);

public sealed record InventorySearchProjectionStatus(
    bool Ready,
    long Rows,
    DateTimeOffset? GeneratedAt,
    DateTimeOffset? FacetsRefreshedAt,
    TimeSpan Duration);

public sealed record CopartTitleTaxonomyCoverage(
    string Version,
    long CopartActiveLots,
    long ClassifiedActiveLots,
    decimal CoveragePercent,
    bool GateEligible,
    DateTimeOffset MeasuredAt);

/// <summary>Read-only evidence for classifying seller metadata without exposing lot or VIN-level data.</summary>
public sealed record SellerTaxonomyAudit(
    long ActiveLots,
    long ProjectionSellerTypePresent,
    long SourceTypePresent,
    long SellerNamePresent,
    long SellerNamePresentSourceTypeMissing,
    IReadOnlyList<SellerTaxonomyPlatformAudit> Platforms,
    DateTimeOffset MeasuredAt);

public sealed record SellerTaxonomyPlatformAudit(
    string Platform,
    long ActiveLots,
    long ProjectionSellerTypePresent,
    long SourceTypePresent,
    long SellerNamePresent,
    long SellerNamePresentSourceTypeMissing,
    IReadOnlyList<InventoryFacetValue> SourceTypes,
    IReadOnlyList<InventoryFacetValue> SourceClasses,
    IReadOnlyList<InventoryFacetValue> SourceTextClasses,
    IReadOnlyList<InventoryFacetValue> TopSellerNamesMissingSourceType);

public sealed record InventorySearchPage(
    int Page,
    int PageSize,
    int Total,
    DateTimeOffset GeneratedAt,
    IReadOnlyCollection<StoredVehicleSnapshot> Items);

public sealed record InventoryFacetValue(string Value, int Count);

public sealed record InventorySearchSummary(
    int Total,
    DateTimeOffset GeneratedAt,
    IReadOnlyDictionary<string, IReadOnlyList<InventoryFacetValue>> Facets);

public sealed record InventoryFacetsV2Request(
    InventorySearchRequest Filters,
    IReadOnlyCollection<string>? RequestedFacets = null);

public sealed record InventoryNumericFacetRange(decimal? Min, decimal? Max);

public sealed record InventoryDateFacetRange(DateTimeOffset? Min, DateTimeOffset? Max);

public sealed record InventoryFacetsV2Ranges(
    InventoryNumericFacetRange? Year = null,
    InventoryNumericFacetRange? Odometer = null,
    InventoryNumericFacetRange? CurrentBid = null,
    InventoryNumericFacetRange? ProviderEstimate = null,
    InventoryDateFacetRange? AuctionDate = null,
    InventoryNumericFacetRange? EngineSize = null,
    InventoryNumericFacetRange? Horsepower = null,
    InventoryNumericFacetRange? PreGrade = null);

public sealed record InventorySellerFacetValue(
    string Category,
    string SellerName,
    string Platform,
    int Count,
    decimal Confidence,
    bool NeedsReview);

public sealed record InventoryFacetsV2Response(
    int Total,
    DateTimeOffset AsOf,
    string SourceVersion,
    long DurationMs,
    string Cache,
    IReadOnlyDictionary<string, IReadOnlyList<InventoryFacetValue>> Facets,
    InventoryFacetsV2Ranges Ranges,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<InventorySellerFacetValue>? SellerFacets = null);

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

public sealed record InventorySyncLease(bool Acquired, DateTimeOffset? ExpiresAt, Guid? OwnerRunId, string? SkipReason);

public sealed record NationalSyncCheckpoint(
    string StreamName,
    Guid? CycleId,
    string? Cursor,
    int PagesCompleted,
    int LotsObserved,
    bool CycleCompleted,
    bool InitialBackfillCompleted,
    DateTimeOffset? UpdatedAt);

public sealed record NationalSyncOperationalStatus(
    NationalSyncCheckpoint Checkpoint,
    Guid? LastRunId,
    DateTimeOffset? LastRunStartedAt,
    DateTimeOffset? LastRunFinishedAt,
    string? LastRunStatus,
    int? LastRunObserved,
    int? LastRunRequests,
    IReadOnlyList<string> LastRunFailures,
    bool LeaseActive,
    DateTimeOffset? LeaseExpiresAt);

public sealed record NationalSyncBatch(
    string StreamName,
    Guid CycleId,
    string? NextCursor,
    int PagesCompleted,
    int LotsObserved,
    IReadOnlyCollection<string> EligibleLotKeys,
    DateTimeOffset ObservedAt,
    bool CycleCompleted,
    bool InitialBackfillCompleted);

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
    private readonly ConcurrentDictionary<Guid, string> _copartRuns = new();
    private readonly ConcurrentDictionary<string, (Guid OwnerRunId, DateTimeOffset ExpiresAt)> _leases = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, NationalSyncCheckpoint> _nationalSync = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _nationalObservations = new();
    private readonly ConcurrentDictionary<Guid, (InventorySyncRunStart Start, InventorySyncRunCompletion? Completion)> _syncRuns = new();
    private readonly ConcurrentQueue<InventorySyncRunEvent> _syncRunEvents = new();
    private readonly ConcurrentDictionary<string, (AuctionVehicle Vehicle, DateTimeOffset ObservedAt, int Attempts, int Priority)> _scoringQueue = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, LscVehicleScoringResult> _scores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, (string Trigger, DateTimeOffset StartedAt, InventoryScoringRunCompletion? Completion)> _scoringRuns = new();

    public Task<Guid> StartSyncRunAsync(InventorySyncRunStart start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var runId = Guid.NewGuid();
        _syncRuns[runId] = (start, null);
        return Task.FromResult(runId);
    }

    public Task CompleteSyncRunAsync(Guid runId, InventorySyncRunCompletion completion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_syncRuns.TryGetValue(runId, out var current))
            _syncRuns[runId] = (current.Start, completion);
        return Task.CompletedTask;
    }

    public Task RecordSyncRunEventAsync(InventorySyncRunEvent syncEvent, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _syncRunEvents.Enqueue(syncEvent);
        return Task.CompletedTask;
    }

    public Task<InventoryExecutionHistoryPage> GetExecutionHistoryAsync(InventoryExecutionHistoryRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safePage = Math.Max(1, request.Page);
        var safePageSize = Math.Clamp(request.PageSize, 1, 100);
        var events = _syncRunEvents.ToArray();
        var summaries = _syncRuns
            .Select(entry => BuildExecutionSummary(entry.Key, entry.Value.Start, entry.Value.Completion, events))
            .Where(item => string.IsNullOrWhiteSpace(request.Platform) || string.Equals(item.Platform, request.Platform, StringComparison.OrdinalIgnoreCase))
            .Where(item => string.IsNullOrWhiteSpace(request.Status) || string.Equals(item.Status, request.Status, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.StartedAt)
            .ToArray();
        return Task.FromResult(new InventoryExecutionHistoryPage(safePage, safePageSize, summaries.LongLength, Math.Max(1, (int)Math.Ceiling(summaries.LongLength / (double)safePageSize)), summaries.Skip((safePage - 1) * safePageSize).Take(safePageSize).ToArray()));
    }

    public Task<InventoryExecutionEventPage> GetExecutionEventsAsync(Guid runId, int page, int pageSize, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        var events = _syncRunEvents
            .Where(item => item.RunId == runId)
            .OrderByDescending(item => item.OccurredAt)
            .Select(item => new InventoryExecutionEvent(item.OccurredAt, item.Platform, item.LotNumber, item.VinMasked, item.Action, item.ChangedFields, item.RuleCodes))
            .ToArray();
        return Task.FromResult(new InventoryExecutionEventPage(safePage, safePageSize, events.LongLength, Math.Max(1, (int)Math.Ceiling(events.LongLength / (double)safePageSize)), events.Skip((safePage - 1) * safePageSize).Take(safePageSize).ToArray()));
    }

    public Task<CopartSnapshotRegistration> TryRegisterCopartSnapshotAsync(CopartSnapshotReceipt receipt, decimal minimumRowCountRatio, int baselineSnapshotCount, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_copartSnapshots.ContainsKey(receipt.Sha256))
            return Task.FromResult(new CopartSnapshotRegistration(false, true, null, null, "F02: Copart snapshot hash was already processed."));

        var historicalRows = _copartSnapshots.Values
            .OrderByDescending(snapshot => snapshot.DownloadedAt)
            .Take(Math.Max(1, baselineSnapshotCount))
            .Select(snapshot => snapshot.RowCount)
            .OrderBy(value => value)
            .ToArray();
        var median = historicalRows.Length == 0 ? (int?)null : historicalRows[historicalRows.Length / 2];
        if (median is > 0 && receipt.RowCount < decimal.Ceiling(median.Value * minimumRowCountRatio))
            return Task.FromResult(new CopartSnapshotRegistration(false, false, null, median, "F05: Copart snapshot row count is below the accepted baseline."));

        _copartSnapshots[receipt.Sha256] = receipt;
        var runId = Guid.NewGuid();
        _copartRuns[runId] = receipt.Sha256;
        return Task.FromResult(new CopartSnapshotRegistration(true, false, runId, median, null));
    }

    public Task CompleteCopartSnapshotAsync(Guid runId, CopartSnapshotCompletion completion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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

    public Task<InventoryLotPersistenceResult> PersistAsync(AuctionVehicle vehicle, DateTimeOffset observedAt, CancellationToken cancellationToken, Guid? runId = null)
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

        var action = _snapshots.TryGetValue(identity, out var previous)
            ? string.Equals(previous.RawJson, snapshot.RawJson, StringComparison.Ordinal) ? "unchanged" : "updated"
            : "created";
        _snapshots[identity] = snapshot;
        _lifecycle[identity] = (vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown", true, 0);
        if (!string.Equals(action, "unchanged", StringComparison.Ordinal))
            _scoringQueue[identity] = (vehicle, observedAt, 0, 100);
        var changed = action == "unchanged" ? Array.Empty<string>() : new[] { action == "created" ? "initial" : "snapshot" };
        if (runId is not null)
        {
            var vin = vehicle.Vin?.Trim();
            var maskedVin = string.IsNullOrWhiteSpace(vin) ? null : vin.Length <= 4 ? vin : string.Concat(Enumerable.Repeat('*', vin.Length - 4)) + vin[^4..];
            _syncRunEvents.Enqueue(new InventorySyncRunEvent(runId.Value, vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown", identity, vehicle.LotNumber, maskedVin, action, changed, [], observedAt));
        }
        return Task.FromResult(new InventoryLotPersistenceResult(identity, action, changed));
    }

    public Task<InventoryScoringBackfillResult> EnqueueScoringBackfillAsync(int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = Math.Clamp(maximum, 1, 10_000);
        var candidates = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .OrderBy(snapshot => snapshot.ObservedAt)
            .Take(requested)
            .ToArray();
        var enqueued = 0;
        var current = 0;
        foreach (var snapshot in candidates)
        {
            var eligibility = AuctionEligibilityEvaluator.Evaluate(snapshot.Vehicle);
            var inputHash = LscVehicleScoringEngine.CreateInputHash(snapshot.Vehicle, eligibility);
            if (_scores.TryGetValue(snapshot.Identity, out var score) &&
                score.PolicyVersion == LscScoringPolicy.Version &&
                score.InputHash == inputHash)
            {
                current++;
                continue;
            }
            _scoringQueue.AddOrUpdate(
                snapshot.Identity,
                _ => (snapshot.Vehicle, snapshot.ObservedAt, 0, 10),
                (_, existing) => existing.Priority >= 100
                    ? existing
                    : (snapshot.Vehicle, snapshot.ObservedAt, existing.Attempts, 10));
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
        var completed = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eligibility = AuctionEligibilityEvaluator.Evaluate(candidate.Value.Vehicle);
            var score = LscVehicleScoringEngine.Evaluate(candidate.Value.Vehicle, eligibility);
            _scores[candidate.Key] = score;
            _scoringQueue.TryRemove(candidate.Key, out _);
            completed++;
        }
        return Task.FromResult(new InventoryScoringBatchResult(
            candidates.Length,
            completed,
            0,
            0,
            _scoringQueue.Count,
            candidates.Count(item => item.Value.Priority >= 100),
            candidates.Count(item => item.Value.Priority < 100)));
    }

    public Task<InventoryScoringOperationalStatus> GetScoringOperationalStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var lastScoredAt = _scores.Values.Count == 0 ? (DateTimeOffset?)null : _scores.Values.Max(score => score.ScoredAt);
        var platforms = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .GroupBy(snapshot => snapshot.Vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown")
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var items = group.ToArray();
                var queue = items.Where(item => _scoringQueue.ContainsKey(item.Identity)).ToArray();
                var current = items.Count(item => _scores.TryGetValue(item.Identity, out var score)
                    && score.PolicyVersion == LscScoringPolicy.Version
                    && score.InputHash == LscVehicleScoringEngine.CreateInputHash(item.Vehicle, AuctionEligibilityEvaluator.Evaluate(item.Vehicle)));
                return new InventoryScoringPlatformStatus(
                    group.Key, items.Length, current, queue.Length, 0, 0, items.Length - current,
                    queue.Count(item => _scoringQueue[item.Identity].Priority >= 100),
                    queue.Select(item => (DateTimeOffset?)_scoringQueue[item.Identity].ObservedAt).DefaultIfEmpty().Min(),
                    items.Select(item => _scores.TryGetValue(item.Identity, out var score) ? score.ScoredAt : (DateTimeOffset?)null).Max());
            })
            .ToArray();
        var oldestQueued = _scoringQueue.Values.Select(item => (DateTimeOffset?)item.ObservedAt).DefaultIfEmpty().Min();
        return Task.FromResult(new InventoryScoringOperationalStatus(
            LscScoringPolicy.Version, _scoringQueue.Count, 0, _scores.Count, 0, lastScoredAt,
            platforms, oldestQueued, _scoringRuns.Select(item => ToScoringRunSummary(item.Key, item.Value)).OrderByDescending(item => item.StartedAt).Take(10).ToArray()));
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
            .OrderByDescending(item => item.StartedAt)
            .Take(Math.Clamp(maximum, 1, 100))
            .ToArray());
    }

    private static InventoryScoringRunSummary ToScoringRunSummary(
        Guid runId,
        (string Trigger, DateTimeOffset StartedAt, InventoryScoringRunCompletion? Completion) run)
    {
        var completion = run.Completion;
        return new InventoryScoringRunSummary(
            runId, run.Trigger, completion?.Status ?? "running", run.StartedAt, completion?.FinishedAt,
            completion?.BackfillRequested ?? 0, completion?.BackfillEnqueued ?? 0, completion?.Claimed ?? 0,
            completion?.Completed ?? 0, completion?.Failed ?? 0, completion?.Skipped ?? 0,
            completion?.Remaining ?? 0, completion?.HighPriorityClaimed ?? 0, completion?.BackfillClaimed ?? 0,
            completion?.Error);
    }

    public Task<LscVehicleScoringResult?> GetScoreByLotAsync(string lotNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _snapshots.Values
            .Where(item => string.Equals(item.Vehicle.LotNumber, lotNumber, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.ObservedAt)
            .FirstOrDefault();
        return Task.FromResult(snapshot is null || !_scores.TryGetValue(snapshot.Identity, out var score) ? null : score);
    }

    private static InventoryExecutionSummary BuildExecutionSummary(Guid runId, InventorySyncRunStart start, InventorySyncRunCompletion? completion, IReadOnlyCollection<InventorySyncRunEvent> events)
    {
        var runEvents = events.Where(item => item.RunId == runId).ToArray();
        int Count(string action) => runEvents.Count(item => string.Equals(item.Action, action, StringComparison.OrdinalIgnoreCase));
        var status = completion is null ? "running" : completion.Cancelled ? "cancelled" : completion.Failures.Count == 0 ? "succeeded" : "completed_with_errors";
        return new InventoryExecutionSummary(
            runId, start.Provider, start.Platform, start.State, status, start.StartedAt, completion?.FinishedAt,
            completion?.VehiclesObserved ?? 0, completion?.RequestsIssued ?? 0, completion?.Loaded,
            Count("created"), Count("updated"), Count("unchanged"), completion?.Marked, completion?.Discarded,
            completion?.Quarantined, completion?.Errors, completion?.Reconciliation?.Reactivated,
            completion?.Reconciliation?.MissesIncremented, completion?.Reconciliation?.Deactivated,
            completion?.PagesProcessed, completion?.CycleCompleted, completion?.Failures ?? []);
    }

    public Task<IReadOnlyCollection<StoredVehicleSnapshot>> GetRecentAsync(int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyCollection<StoredVehicleSnapshot> result = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .OrderByDescending(snapshot => snapshot.ObservedAt)
            .Take(Math.Clamp(maximum, 1, 5000))
            .Select(AttachScoring)
            .ToArray();
        return Task.FromResult(result);
    }

    public Task<InventorySearchPage> SearchAsync(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var query = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .Where(snapshot => Matches(snapshot, request))
            .Where(snapshot => MatchesScoring(snapshot.Identity, request));
        var gradingFirst = query.OrderByDescending(snapshot => _scores.TryGetValue(snapshot.Identity, out var score) ? score.PreGrade ?? decimal.MinValue : decimal.MinValue);
        IOrderedEnumerable<StoredVehicleSnapshot> orderedQuery = request.Sort?.Trim().ToLowerInvariant() switch
        {
            "auction" => gradingFirst.ThenBy(snapshot => snapshot.Vehicle.Auction?.AuctionAt ?? DateTimeOffset.MaxValue),
            "auction-desc" => gradingFirst.ThenByDescending(snapshot => snapshot.Vehicle.Auction?.AuctionAt ?? DateTimeOffset.MinValue),
            "year-asc" => gradingFirst.ThenBy(snapshot => snapshot.Vehicle.Year ?? int.MaxValue),
            "year-desc" => gradingFirst.ThenByDescending(snapshot => snapshot.Vehicle.Year ?? 0),
            "estimate-asc" => gradingFirst.ThenBy(snapshot => snapshot.Vehicle.Pricing?.EstimatedCost?.FromUsd ?? decimal.MaxValue),
            "estimate-desc" => gradingFirst.ThenByDescending(snapshot => snapshot.Vehicle.Pricing?.EstimatedCost?.ToUsd ?? 0),
            "buy-asc" => gradingFirst.ThenBy(snapshot => snapshot.Vehicle.Pricing?.BuyNowUsd ?? decimal.MaxValue),
            "buy-desc" => gradingFirst.ThenByDescending(snapshot => snapshot.Vehicle.Pricing?.BuyNowUsd ?? 0),
            "bid-asc" => gradingFirst.ThenBy(snapshot => snapshot.Vehicle.Pricing?.CurrentBidUsd ?? decimal.MaxValue),
            "bid-desc" => gradingFirst.ThenByDescending(snapshot => snapshot.Vehicle.Pricing?.CurrentBidUsd ?? 0),
            "odometer-asc" => gradingFirst.ThenBy(snapshot => snapshot.Vehicle.Odometer ?? decimal.MaxValue),
            "odometer-desc" => gradingFirst.ThenByDescending(snapshot => snapshot.Vehicle.Odometer ?? 0),
            _ => gradingFirst.ThenByDescending(snapshot => snapshot.ObservedAt),
        };
        var ordered = orderedQuery.ThenBy(snapshot => snapshot.Identity, StringComparer.Ordinal).ToArray();
        var total = ordered.Length;
        var items = ordered.Skip((page - 1) * pageSize).Take(pageSize).Select(AttachScoring).ToArray();
        var generatedAt = ordered.Length == 0 ? DateTimeOffset.UtcNow : ordered.Max(snapshot => snapshot.ObservedAt);
        return Task.FromResult(new InventorySearchPage(page, pageSize, total, generatedAt, items));
    }

    public Task<InventorySearchSummary> GetInventorySearchSummaryAsync(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshots = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .Where(snapshot => Matches(snapshot, request))
            .ToArray();
        static IReadOnlyList<InventoryFacetValue> Count(IEnumerable<string?> values) => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new InventoryFacetValue(group.First(), group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Take(250)
            .ToArray();
        var facets = new Dictionary<string, IReadOnlyList<InventoryFacetValue>>(StringComparer.OrdinalIgnoreCase)
        {
            ["platforms"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Platform)),
            ["makes"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Make)),
            ["models"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Model)),
            ["vehicleTypes"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.VehicleType)),
            ["titles"] = Count(snapshots.Select(snapshot => TitleFacetCategory.Classify(snapshot.Vehicle))),
            ["states"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Location?.State)),
            ["facilities"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Location?.Display)),
            ["primaryDamages"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Damage)),
            ["secondaryDamages"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Condition?.SecondaryDamage)),
            ["sellerTypes"] = Count(snapshots.Select(snapshot => SellerTaxonomy.Normalize(snapshot.Vehicle.Seller?.Type))),
            ["engineLayouts"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.VehicleSpecs?.Engine?.Layout)),
            ["cylinders"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Details?.VehicleDescription?.Cylinders)),
            ["transmissions"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Transmission)),
            ["fuels"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.FuelType)),
            ["drives"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.DriveType)),
            ["bodyStyles"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.VehicleSpecs?.BodyStyle)),
            ["colors"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Color)),
            ["lossTypes"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Condition?.Loss)),
            ["startCodes"] = Count(snapshots.Select(snapshot => snapshot.Vehicle.Condition?.RunCondition?.Value)),
        };
        var generatedAt = snapshots.Length == 0 ? DateTimeOffset.UtcNow : snapshots.Max(snapshot => snapshot.ObservedAt);
        return Task.FromResult(new InventorySearchSummary(snapshots.Length, generatedAt, facets));
    }

    public Task<InventoryFacetsV2Response> GetInventoryFacetsV2Async(InventoryFacetsV2Request request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var requested = InventoryFacetsV2Groups.NormalizeRequested(request.RequestedFacets);
        var filters = request.Filters with
        {
            Page = 1,
            PageSize = 1,
            Sort = null,
            Titles = InventoryFacetsV2Fingerprint.Merge(request.Filters.Titles, request.Filters.TitleCategories),
            TitleCategories = null
        };
        var active = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .Select(AttachScoring)
            .ToArray();
        var total = active.Count(snapshot => Matches(snapshot, filters) && MatchesScoring(snapshot.Identity, filters));
        var facets = new Dictionary<string, IReadOnlyList<InventoryFacetValue>>(StringComparer.OrdinalIgnoreCase);
        var ranges = new Dictionary<string, InventoryNumericFacetRange>(StringComparer.OrdinalIgnoreCase);
        InventoryDateFacetRange? auctionDate = null;

        foreach (var group in requested)
        {
            var groupFilters = WithoutFacetsV2Group(filters, group);
            var rows = active
                .Where(snapshot => Matches(snapshot, groupFilters) && MatchesScoring(snapshot.Identity, groupFilters))
                .ToArray();
            if (InventoryFacetsV2Groups.Categorical.Contains(group))
            {
                var values = rows
                    .Select(snapshot => FacetsV2Value(snapshot, group))
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!.Trim())
                    .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .Select(valuesGroup => new InventoryFacetValue(valuesGroup.First(), valuesGroup.Count()))
                    .OrderByDescending(item => item.Count)
                    .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
                    .Take(250)
                    .ToList();
                foreach (var selected in InventoryFacetsV2Selections.Get(filters, group))
                {
                    if (!values.Any(value => string.Equals(value.Value, selected, StringComparison.OrdinalIgnoreCase)))
                        values.Add(new InventoryFacetValue(selected, 0));
                }
                facets[group] = values;
                continue;
            }

            if (string.Equals(group, InventoryFacetsV2Groups.AuctionDate, StringComparison.OrdinalIgnoreCase))
            {
                var values = rows.Select(snapshot => snapshot.Vehicle.Auction?.AuctionAt).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
                auctionDate = values.Length == 0 ? new InventoryDateFacetRange(null, null) : new InventoryDateFacetRange(values.Min(), values.Max());
                continue;
            }

            var numeric = rows.Select(snapshot => FacetsV2NumericValue(snapshot, group)).Where(value => value.HasValue).Select(value => value!.Value).ToArray();
            ranges[group] = numeric.Length == 0
                ? new InventoryNumericFacetRange(null, null)
                : new InventoryNumericFacetRange(numeric.Min(), numeric.Max());
        }

        var asOf = active.Length == 0 ? DateTimeOffset.UtcNow : active.Max(snapshot => snapshot.ObservedAt);
        var sourceVersion = $"inventory-current-v1:{active.Length}:{asOf.UtcTicks}";
        return Task.FromResult(new InventoryFacetsV2Response(
            total,
            asOf,
            sourceVersion,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            "miss",
            facets,
            new InventoryFacetsV2Ranges(
                ranges.GetValueOrDefault(InventoryFacetsV2Groups.Year),
                ranges.GetValueOrDefault(InventoryFacetsV2Groups.Odometer),
                ranges.GetValueOrDefault(InventoryFacetsV2Groups.CurrentBid),
                ranges.GetValueOrDefault(InventoryFacetsV2Groups.ProviderEstimate),
                auctionDate,
                ranges.GetValueOrDefault(InventoryFacetsV2Groups.EngineSize),
                ranges.GetValueOrDefault(InventoryFacetsV2Groups.Horsepower),
                ranges.GetValueOrDefault(InventoryFacetsV2Groups.PreGrade)),
            []));
    }

    private static InventorySearchRequest WithoutFacetsV2Group(InventorySearchRequest request, string group) => group switch
    {
        InventoryFacetsV2Groups.Platforms => request with { Platform = null },
        InventoryFacetsV2Groups.Makes => request with { Makes = null },
        InventoryFacetsV2Groups.Models => request with { Models = null },
        InventoryFacetsV2Groups.VehicleTypes => request with { VehicleTypes = null },
        InventoryFacetsV2Groups.Titles => request with { Titles = null, TitleCategories = null },
        InventoryFacetsV2Groups.States => request with { States = null },
        InventoryFacetsV2Groups.Facilities => request with { Facilities = null },
        InventoryFacetsV2Groups.PrimaryDamages => request with { PrimaryDamages = null },
        InventoryFacetsV2Groups.SecondaryDamages => request with { SecondaryDamages = null },
        InventoryFacetsV2Groups.SellerTypes => request with { SellerTypes = null },
        InventoryFacetsV2Groups.EngineLayouts => request with { EngineLayouts = null },
        InventoryFacetsV2Groups.Cylinders => request with { Cylinders = null },
        InventoryFacetsV2Groups.Transmissions => request with { Transmissions = null },
        InventoryFacetsV2Groups.Fuels => request with { Fuels = null },
        InventoryFacetsV2Groups.Drives => request with { Drives = null },
        InventoryFacetsV2Groups.BodyStyles => request with { BodyStyles = null },
        InventoryFacetsV2Groups.Colors => request with { Colors = null },
        InventoryFacetsV2Groups.LossTypes => request with { LossTypes = null },
        InventoryFacetsV2Groups.StartCodes => request with { StartCodes = null },
        InventoryFacetsV2Groups.RunConditions => request with { RunConditions = null },
        InventoryFacetsV2Groups.ScoringStatuses => request with { ScoringStatuses = null },
        InventoryFacetsV2Groups.Year => request with { YearFrom = null, YearTo = null },
        InventoryFacetsV2Groups.Odometer => request with { OdometerFrom = null, OdometerTo = null },
        InventoryFacetsV2Groups.CurrentBid => request with { PriceFrom = null, PriceTo = null, MaxCurrentBid = null },
        InventoryFacetsV2Groups.ProviderEstimate => request with { ProviderEstimateFrom = null, ProviderEstimateTo = null },
        InventoryFacetsV2Groups.AuctionDate => request with { AuctionFrom = null, AuctionTo = null },
        InventoryFacetsV2Groups.EngineSize => request with { EngineSizeFrom = null, EngineSizeTo = null },
        InventoryFacetsV2Groups.Horsepower => request with { HorsepowerFrom = null, HorsepowerTo = null },
        InventoryFacetsV2Groups.PreGrade => request with { PreGradeFrom = null },
        _ => request
    };

    private static string? FacetsV2Value(StoredVehicleSnapshot snapshot, string group)
    {
        var vehicle = snapshot.Vehicle;
        return group switch
        {
            InventoryFacetsV2Groups.Platforms => vehicle.Platform,
            InventoryFacetsV2Groups.Makes => vehicle.Make,
            InventoryFacetsV2Groups.Models => vehicle.Model,
            InventoryFacetsV2Groups.VehicleTypes => vehicle.VehicleType,
            InventoryFacetsV2Groups.Titles => TitleFacetCategory.Classify(vehicle),
            InventoryFacetsV2Groups.States => vehicle.Location?.State,
            InventoryFacetsV2Groups.Facilities => vehicle.Location?.Display,
            InventoryFacetsV2Groups.PrimaryDamages => vehicle.Damage ?? vehicle.Condition?.PrimaryDamage,
            InventoryFacetsV2Groups.SecondaryDamages => vehicle.Condition?.SecondaryDamage,
            InventoryFacetsV2Groups.SellerTypes => SellerTaxonomy.Normalize(vehicle.Seller?.Type),
            InventoryFacetsV2Groups.EngineLayouts => vehicle.VehicleSpecs?.Engine?.Layout,
            InventoryFacetsV2Groups.Cylinders => vehicle.Details?.VehicleDescription?.Cylinders,
            InventoryFacetsV2Groups.Transmissions => vehicle.Transmission ?? vehicle.VehicleSpecs?.Transmission,
            InventoryFacetsV2Groups.Fuels => vehicle.FuelType ?? vehicle.VehicleSpecs?.FuelType,
            InventoryFacetsV2Groups.Drives => vehicle.DriveType ?? vehicle.VehicleSpecs?.DriveType,
            InventoryFacetsV2Groups.BodyStyles => vehicle.VehicleSpecs?.BodyStyle ?? vehicle.Details?.VehicleDescription?.BodyStyle,
            InventoryFacetsV2Groups.Colors => vehicle.Color ?? vehicle.VehicleSpecs?.ExteriorColor,
            InventoryFacetsV2Groups.LossTypes => vehicle.Condition?.Loss,
            InventoryFacetsV2Groups.StartCodes => vehicle.Condition?.RunCondition?.Value,
            InventoryFacetsV2Groups.RunConditions => NormalizeFacetsV2RunCondition(vehicle),
            InventoryFacetsV2Groups.ScoringStatuses => snapshot.Scoring?.Status,
            _ => null
        };
    }

    private static decimal? FacetsV2NumericValue(StoredVehicleSnapshot snapshot, string group)
    {
        var vehicle = snapshot.Vehicle;
        return group switch
        {
            InventoryFacetsV2Groups.Year => vehicle.Year,
            InventoryFacetsV2Groups.Odometer => vehicle.Odometer,
            InventoryFacetsV2Groups.CurrentBid => vehicle.Pricing?.CurrentBidUsd,
            InventoryFacetsV2Groups.ProviderEstimate => vehicle.Pricing?.EstimatedCost?.FromUsd ?? vehicle.Pricing?.EstimatedCost?.ToUsd,
            InventoryFacetsV2Groups.EngineSize => decimal.TryParse(vehicle.VehicleSpecs?.Engine?.SizeLiters, NumberStyles.Any, CultureInfo.InvariantCulture, out var size) ? size : null,
            InventoryFacetsV2Groups.Horsepower => vehicle.VehicleSpecs?.Engine?.Horsepower,
            InventoryFacetsV2Groups.PreGrade => snapshot.Scoring?.PreGrade,
            _ => null
        };
    }

    private static string NormalizeFacetsV2RunCondition(AuctionVehicle vehicle) =>
        RunConditionTaxonomy.Normalize(vehicle.Condition?.RunCondition?.Value ?? vehicle.Condition?.RunCondition?.Label);

    public Task<SellerTaxonomyAudit> GetSellerTaxonomyAuditAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var active = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .ToArray();
        static IReadOnlyList<InventoryFacetValue> Count(IEnumerable<string?> values) => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim())
            .GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Select(group => new InventoryFacetValue(group.First(), group.Count()))
            .OrderByDescending(item => item.Count)
            .ThenBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        var platforms = active
            .GroupBy(snapshot => snapshot.Vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown", StringComparer.OrdinalIgnoreCase)
            .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.ToArray();
                var typed = rows.Where(row => !string.IsNullOrWhiteSpace(row.Vehicle.Seller?.Type)).ToArray();
                return new SellerTaxonomyPlatformAudit(
                    group.Key,
                    rows.LongLength,
                    typed.LongLength,
                    typed.LongLength,
                    rows.LongCount(row => !string.IsNullOrWhiteSpace(row.Vehicle.Seller?.Name)),
                    rows.LongCount(row => !string.IsNullOrWhiteSpace(row.Vehicle.Seller?.Name) && string.IsNullOrWhiteSpace(row.Vehicle.Seller?.Type)),
                    Count(rows.Select(row => row.Vehicle.Seller?.Type)),
                    Count(rows.Select(row => row.Vehicle.Seller?.Class)),
                    Count(rows.Select(row => row.Vehicle.Seller?.TextClass)),
                    Count(rows.Where(row => string.IsNullOrWhiteSpace(row.Vehicle.Seller?.Type)).Select(row => row.Vehicle.Seller?.Name)));
            })
            .ToArray();
        return Task.FromResult(new SellerTaxonomyAudit(
            active.LongLength,
            active.LongCount(row => !string.IsNullOrWhiteSpace(row.Vehicle.Seller?.Type)),
            active.LongCount(row => !string.IsNullOrWhiteSpace(row.Vehicle.Seller?.Type)),
            active.LongCount(row => !string.IsNullOrWhiteSpace(row.Vehicle.Seller?.Name)),
            active.LongCount(row => !string.IsNullOrWhiteSpace(row.Vehicle.Seller?.Name) && string.IsNullOrWhiteSpace(row.Vehicle.Seller?.Type)),
            platforms,
            DateTimeOffset.UtcNow));
    }

    public Task<InventorySearchProjectionStatus> RebuildSearchProjectionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generatedAt = _snapshots.Count == 0 ? (DateTimeOffset?)null : _snapshots.Values.Max(snapshot => snapshot.ObservedAt);
        return Task.FromResult(new InventorySearchProjectionStatus(true, _snapshots.Count, generatedAt, DateTimeOffset.UtcNow, TimeSpan.Zero));
    }

    public Task<InventorySearchProjectionStatus> GetSearchProjectionStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generatedAt = _snapshots.Count == 0 ? (DateTimeOffset?)null : _snapshots.Values.Max(snapshot => snapshot.ObservedAt);
        return Task.FromResult(new InventorySearchProjectionStatus(true, _snapshots.Count, generatedAt, DateTimeOffset.UtcNow, TimeSpan.Zero));
    }

    public Task<CopartTitleTaxonomyCoverage> GetCopartTitleTaxonomyCoverageAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const string version = "copart-title-taxonomy-v1";
        var copart = _snapshots.Values.Where(snapshot => string.Equals(snapshot.Vehicle.Platform, "copart", StringComparison.OrdinalIgnoreCase)).ToArray();
        var classified = copart.Count(snapshot => snapshot.Vehicle.AdditionalData?.TryGetValue("title_taxonomy_version", out var value) == true && value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), version, StringComparison.OrdinalIgnoreCase));
        var coverage = copart.Length == 0 ? 0m : decimal.Round(classified * 100m / copart.Length, 2);
        return Task.FromResult(new CopartTitleTaxonomyCoverage(version, copart.Length, classified, coverage, copart.Length > 0 && coverage >= 95m, DateTimeOffset.UtcNow));
    }

    public Task<StoredVehicleSnapshot?> GetByLotAsync(string lotNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .OrderByDescending(snapshot => snapshot.ObservedAt)
            .FirstOrDefault(snapshot => string.Equals(snapshot.Vehicle.LotNumber, lotNumber, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(snapshot is null ? null : AttachScoring(snapshot));
    }

    public Task<StoredVehicleSnapshot?> GetByPlatformAndLotAsync(string platform, string lotNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = _snapshots.Values
            .Where(snapshot => !_lifecycle.TryGetValue(snapshot.Identity, out var lifecycle) || lifecycle.Active)
            .OrderByDescending(snapshot => snapshot.ObservedAt)
            .FirstOrDefault(snapshot =>
                string.Equals(snapshot.Vehicle.Platform, platform, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(snapshot.Vehicle.LotNumber, lotNumber, StringComparison.OrdinalIgnoreCase));
        return Task.FromResult(snapshot is null ? null : AttachScoring(snapshot));
    }

    private static bool Matches(StoredVehicleSnapshot snapshot, InventorySearchRequest request)
    {
        var vehicle = snapshot.Vehicle;
        var normalizedQuery = request.Query?.Trim();
        if (!string.IsNullOrWhiteSpace(normalizedQuery))
        {
            var searchable = string.Join(' ', vehicle.LotNumber, vehicle.Vin, vehicle.Make, vehicle.Model, vehicle.Title, vehicle.SaleDocument?.Name);
            if (!searchable.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)) return false;
        }

        static bool Any(IReadOnlyCollection<string>? values, string? value) => values is null || values.Count == 0 || (!string.IsNullOrWhiteSpace(value) && values.Contains(value, StringComparer.OrdinalIgnoreCase));
        static string NormalizeRunCondition(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "UNVERIFIED";
            var normalized = value.Trim().ToUpperInvariant().Replace("&", " AND ", StringComparison.Ordinal);
            if (normalized.Contains("RUNS AND DRIVES", StringComparison.Ordinal)) return "RUNS_AND_DRIVES";
            if (normalized.Contains("START", StringComparison.Ordinal)) return "STARTS";
            if (normalized.Contains("STATIONARY", StringComparison.Ordinal)) return "STATIONARY";
            return "UNVERIFIED";
        }
        var titleType = TitleFacetCategory.Classify(vehicle);
        var normalizedTitleCategory = vehicle.AdditionalData is not null && vehicle.AdditionalData.TryGetValue("title_category", out var titleCategoryElement) && titleCategoryElement.ValueKind == JsonValueKind.String ? titleCategoryElement.GetString() : null;
        if (!string.IsNullOrWhiteSpace(request.Platform) && !string.Equals(vehicle.Platform, request.Platform, StringComparison.OrdinalIgnoreCase)) return false;
        if (request.TitleCategories is { Count: > 0 } && (!string.Equals(vehicle.Platform, "copart", StringComparison.OrdinalIgnoreCase) || !Any(request.TitleCategories, normalizedTitleCategory))) return false;
        if (request.ExcludeSpecialTitles && TitleFacetCategory.IsSpecial(titleType)) return false;
        if (!Any(request.Makes, vehicle.Make) || !Any(request.Models, vehicle.Model) || !Any(request.VehicleTypes, vehicle.VehicleType) || !Any(request.Titles, titleType) || !Any(request.States, vehicle.Location?.State) || !Any(request.Facilities, vehicle.Location?.Display) || !Any(request.PrimaryDamages, vehicle.Damage) || !Any(request.SecondaryDamages, vehicle.Condition?.SecondaryDamage) || !Any(request.SellerTypes, SellerTaxonomy.Normalize(vehicle.Seller?.Type)) || !Any(request.EngineLayouts, vehicle.VehicleSpecs?.Engine?.Layout) || !Any(request.Cylinders, vehicle.Details?.VehicleDescription?.Cylinders) || !Any(request.Transmissions, vehicle.Transmission) || !Any(request.Fuels, vehicle.FuelType) || !Any(request.Drives, vehicle.DriveType) || !Any(request.BodyStyles, vehicle.VehicleSpecs?.BodyStyle) || !Any(request.Colors, vehicle.Color) || !Any(request.LossTypes, vehicle.Condition?.Loss) || !Any(request.StartCodes, vehicle.Condition?.RunCondition?.Value) || !Any(request.RunConditions, NormalizeRunCondition(vehicle.Condition?.RunCondition?.Value ?? vehicle.Condition?.RunCondition?.Label))) return false;
        if (request.YearFrom.HasValue && (vehicle.Year ?? 0) < request.YearFrom.Value) return false;
        if (request.YearTo.HasValue && (vehicle.Year ?? int.MaxValue) > request.YearTo.Value) return false;
        if (request.OdometerFrom.HasValue && (vehicle.Odometer ?? decimal.MinValue) < request.OdometerFrom.Value) return false;
        if (request.OdometerTo.HasValue && (vehicle.Odometer ?? decimal.MaxValue) > request.OdometerTo.Value) return false;
        if (request.PriceFrom.HasValue && (vehicle.Pricing?.CurrentBidUsd ?? decimal.MinValue) < request.PriceFrom.Value) return false;
        if (request.PriceTo.HasValue && (vehicle.Pricing?.CurrentBidUsd ?? decimal.MaxValue) > request.PriceTo.Value) return false;
        if ((request.BuyNowOnly == true || request.BuyNowFrom.HasValue || request.BuyNowTo.HasValue) && vehicle.Pricing?.BuyNowUsd is not > 0m) return false;
        if (request.BuyNowFrom.HasValue && (vehicle.Pricing?.BuyNowUsd ?? decimal.MinValue) < request.BuyNowFrom.Value) return false;
        if (request.BuyNowTo.HasValue && (vehicle.Pricing?.BuyNowUsd ?? decimal.MaxValue) > request.BuyNowTo.Value) return false;
        if (request.MaxCurrentBid.HasValue && vehicle.Pricing?.CurrentBidUsd is decimal currentBid && currentBid > request.MaxCurrentBid.Value) return false;
        if (request.AuctionFrom.HasValue && (vehicle.Auction?.AuctionAt ?? DateTimeOffset.MinValue) < request.AuctionFrom.Value) return false;
        if (request.AuctionTo.HasValue && (vehicle.Auction?.AuctionAt ?? DateTimeOffset.MaxValue) > request.AuctionTo.Value) return false;
        if (request.WithPhotosOnly == true && (vehicle.Media?.ThumbnailsCount ?? 0) == 0 && !(vehicle.Media?.Items?.Any() ?? false)) return false;
        if (request.WithBidOnly == true && vehicle.Pricing?.CurrentBidUsd is null) return false;
        if (string.Equals(request.KeyMode, "with", StringComparison.OrdinalIgnoreCase) && vehicle.Condition?.HasKey != true) return false;
        if (string.Equals(request.KeyMode, "without", StringComparison.OrdinalIgnoreCase) && vehicle.Condition?.HasKey != false) return false;
        if (request.ProviderEstimateFrom.HasValue && (vehicle.Pricing?.EstimatedCost?.ToUsd ?? decimal.MinValue) < request.ProviderEstimateFrom.Value) return false;
        if (request.ProviderEstimateTo.HasValue && (vehicle.Pricing?.EstimatedCost?.FromUsd ?? decimal.MaxValue) > request.ProviderEstimateTo.Value) return false;
        var engineSize = decimal.TryParse(vehicle.VehicleSpecs?.Engine?.SizeLiters, CultureInfo.InvariantCulture, out var parsedEngineSize) ? parsedEngineSize : (decimal?)null;
        if (request.EngineSizeFrom.HasValue && (engineSize ?? decimal.MinValue) < request.EngineSizeFrom.Value) return false;
        if (request.EngineSizeTo.HasValue && (engineSize ?? decimal.MaxValue) > request.EngineSizeTo.Value) return false;
        if (request.HorsepowerFrom.HasValue && (vehicle.VehicleSpecs?.Engine?.Horsepower ?? decimal.MinValue) < request.HorsepowerFrom.Value) return false;
        if (request.HorsepowerTo.HasValue && (vehicle.VehicleSpecs?.Engine?.Horsepower ?? decimal.MaxValue) > request.HorsepowerTo.Value) return false;
        var status = $"{vehicle.Auction?.LotStatus} {vehicle.Auction?.LotSubStatus}";
        if (string.Equals(request.AuctionStatus, "open", StringComparison.OrdinalIgnoreCase) && !status.Contains("open", StringComparison.OrdinalIgnoreCase) && !status.Contains("active", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(request.AuctionStatus, "live", StringComparison.OrdinalIgnoreCase) && !status.Contains("live", StringComparison.OrdinalIgnoreCase)) return false;
        if (string.Equals(request.AuctionStatus, "finished", StringComparison.OrdinalIgnoreCase) && !status.Contains("finished", StringComparison.OrdinalIgnoreCase) && !status.Contains("ended", StringComparison.OrdinalIgnoreCase) && !status.Contains("sold", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private bool MatchesScoring(string lotKey, InventorySearchRequest request)
    {
        var score = _scores.TryGetValue(lotKey, out var stored) ? stored : null;
        if (request.PreGradeFrom.HasValue && (score?.PreGrade ?? decimal.MinValue) < request.PreGradeFrom.Value) return false;
        return request.ScoringStatuses is null || request.ScoringStatuses.Count == 0 ||
               (score is not null && request.ScoringStatuses.Contains(score.Status, StringComparer.OrdinalIgnoreCase));
    }

    private StoredVehicleSnapshot AttachScoring(StoredVehicleSnapshot snapshot)
    {
        if (!_scores.TryGetValue(snapshot.Identity, out var score)) return snapshot with { Scoring = null };
        return snapshot with
        {
            Scoring = new LscScoringSummary(
                score.Status, score.PreGrade, score.BuyScore, score.MaxPointsEvaluable,
                score.CoveragePercent, score.ConfidencePercent, score.Category,
                score.PolicyVersion, score.ScoredAt)
        };
    }

    public Task<InventoryReconciliationResult> ReconcileSourceAsync(string platform, IReadOnlyCollection<string> observedLotKeys, bool isCompleteSnapshot, DateTimeOffset observedAt, CancellationToken cancellationToken, Guid? runId = null)
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
                if (!entry.Value.Active)
                {
                    reactivated++;
                    if (runId is not null) _syncRunEvents.Enqueue(new InventorySyncRunEvent(runId.Value, normalizedPlatform, entry.Key, entry.Key.Split(':').LastOrDefault(), null, "reactivated", ["estado activo"], [], observedAt));
                }
                _lifecycle[entry.Key] = (normalizedPlatform, true, 0);
                continue;
            }

            var missingCount = entry.Value.MissingCount + 1;
            var active = missingCount < 3;
            incremented++;
            if (entry.Value.Active && !active)
            {
                deactivated++;
                if (runId is not null) _syncRunEvents.Enqueue(new InventorySyncRunEvent(runId.Value, normalizedPlatform, entry.Key, entry.Key.Split(':').LastOrDefault(), null, "deactivated", ["tres ausencias consecutivas"], [], observedAt));
            }
            _lifecycle[entry.Key] = (normalizedPlatform, active, missingCount);
        }

        return Task.FromResult(new InventoryReconciliationResult(normalizedPlatform, true, observed.Count, reactivated, incremented, deactivated));
    }

    public Task<int> DeactivateArchivedLotsAsync(string platform, IReadOnlyCollection<string> lotKeys, DateTimeOffset archivedAt, CancellationToken cancellationToken, Guid? runId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedPlatform = platform.Trim().ToLowerInvariant();
        var keys = lotKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var deactivated = 0;
        foreach (var entry in _lifecycle.ToArray().Where(entry => entry.Value.Platform == normalizedPlatform && keys.Contains(entry.Key)))
        {
            if (!entry.Value.Active) continue;
            _lifecycle[entry.Key] = (normalizedPlatform, false, 0);
            deactivated++;
            if (runId is not null) _syncRunEvents.Enqueue(new InventorySyncRunEvent(runId.Value, normalizedPlatform, entry.Key, entry.Key.Split(':').LastOrDefault(), null, "deactivated", ["provider-archived"], [], archivedAt));
        }
        return Task.FromResult(deactivated);
    }

    public Task<InventorySyncLease> TryAcquireLeaseAsync(string leaseName, Guid ownerRunId, DateTimeOffset acquiredAt, TimeSpan duration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expiresAt = acquiredAt.Add(duration);
        var acquired = false;
        _leases.AddOrUpdate(
            leaseName,
            _ =>
            {
                acquired = true;
                return (ownerRunId, expiresAt);
            },
            (_, current) =>
            {
                if (current.ExpiresAt <= acquiredAt || current.OwnerRunId == ownerRunId)
                {
                    acquired = true;
                    return (ownerRunId, expiresAt);
                }

                return current;
            });
        if (acquired) return Task.FromResult(new InventorySyncLease(true, expiresAt, ownerRunId, null));
        var existing = _leases[leaseName];
        return Task.FromResult(new InventorySyncLease(false, existing.ExpiresAt, existing.OwnerRunId, "lease-active"));
    }

    public Task ReleaseLeaseAsync(string leaseName, Guid ownerRunId, DateTimeOffset releasedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_leases.TryGetValue(leaseName, out var lease) && lease.OwnerRunId == ownerRunId)
            _leases.TryRemove(leaseName, out _);
        return Task.CompletedTask;
    }

    public Task<NationalSyncCheckpoint> GetNationalSyncCheckpointAsync(string streamName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_nationalSync.GetOrAdd(streamName, name => new NationalSyncCheckpoint(name, null, null, 0, 0, true, false, null)));
    }

    public async Task<NationalSyncOperationalStatus> GetNationalSyncOperationalStatusAsync(string streamName, CancellationToken cancellationToken)
    {
        var checkpoint = await GetNationalSyncCheckpointAsync(streamName, cancellationToken);
        var leaseActive = _leases.TryGetValue("iaai-national-sync", out var lease) && lease.ExpiresAt > DateTimeOffset.UtcNow;
        return new NationalSyncOperationalStatus(checkpoint, null, null, null, null, null, null, [], leaseActive, leaseActive ? lease.ExpiresAt : null);
    }

    public Task PersistNationalSyncBatchAsync(NationalSyncBatch batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observations = _nationalObservations.GetOrAdd(batch.CycleId, _ => new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase));
        foreach (var lotKey in batch.EligibleLotKeys) observations[lotKey] = 0;
        _nationalSync[batch.StreamName] = new NationalSyncCheckpoint(batch.StreamName, batch.CycleId, batch.NextCursor, batch.PagesCompleted, batch.LotsObserved, batch.CycleCompleted, batch.InitialBackfillCompleted, batch.ObservedAt);
        return Task.CompletedTask;
    }

    public async Task<InventoryReconciliationResult> CompleteNationalSyncCycleAsync(string streamName, Guid cycleId, DateTimeOffset completedAt, CancellationToken cancellationToken, Guid? runId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observed = _nationalObservations.TryRemove(cycleId, out var observations)
            ? observations.Keys.ToArray()
            : [];
        var result = await ReconcileSourceAsync("iaai", observed, true, completedAt, cancellationToken, runId);
        var initialBackfillCompleted = _nationalSync.TryGetValue(streamName, out var checkpoint) && checkpoint.InitialBackfillCompleted;
        _nationalSync[streamName] = new NationalSyncCheckpoint(streamName, cycleId, null, 0, 0, true, initialBackfillCompleted, completedAt);
        return result;
    }
}
