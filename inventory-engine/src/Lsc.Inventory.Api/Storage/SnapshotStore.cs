using System.Collections.Concurrent;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;

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
    Task<IReadOnlyCollection<StoredVehicleSnapshot>> GetRecentAsync(int maximum, CancellationToken cancellationToken);
    Task<InventoryPage> GetPageAsync(InventoryBrowseQuery query, CancellationToken cancellationToken);
    Task<InventoryReconciliationResult> ReconcileSourceAsync(string platform, IReadOnlyCollection<string> observedLotKeys, bool isCompleteSnapshot, DateTimeOffset observedAt, CancellationToken cancellationToken);
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
    IReadOnlyList<string> Failures);

public sealed record StoredVehicleSnapshot(
    string Identity,
    DateTimeOffset ObservedAt,
    AuctionVehicle Vehicle,
    string RawJson);

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

public sealed class InMemorySnapshotStore : IInventorySnapshotStore
{
    private readonly ConcurrentDictionary<string, StoredVehicleSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, EligibilityAuditItem> _eligibility = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Platform, bool Active, int MissingCount)> _lifecycle = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, CopartSnapshotReceipt> _copartSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _copartSnapshotStatuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, string> _copartRuns = new();

    public Task<Guid> StartSyncRunAsync(InventorySyncRunStart start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Guid.NewGuid());
    }

    public Task CompleteSyncRunAsync(Guid runId, InventorySyncRunCompletion completion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        return Task.CompletedTask;
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
