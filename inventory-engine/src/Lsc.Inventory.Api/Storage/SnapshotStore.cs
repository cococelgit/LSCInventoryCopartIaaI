using System.Collections.Concurrent;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;

namespace Lsc.Inventory.Api.Storage;

public interface IInventorySnapshotStore
{
    Task<Guid> StartSyncRunAsync(InventorySyncRunStart start, CancellationToken cancellationToken);
    Task CompleteSyncRunAsync(Guid runId, InventorySyncRunCompletion completion, CancellationToken cancellationToken);
    Task PersistProviderUsageAsync(string provider, JsonElement usage, DateTimeOffset capturedAt, CancellationToken cancellationToken);
    Task PersistEligibilityDecisionAsync(EligibilityEvaluation evaluation, DateTimeOffset evaluatedAt, CancellationToken cancellationToken);
    Task<EligibilityAuditPage> GetDiscardedEligibilityDecisionsAsync(int page, int pageSize, string? ruleCode, string? query, CancellationToken cancellationToken);
    Task<InventoryValidationReport> GetValidationReportAsync(CancellationToken cancellationToken);
    Task PersistAsync(AuctionVehicle vehicle, DateTimeOffset observedAt, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<StoredVehicleSnapshot>> GetRecentAsync(int maximum, CancellationToken cancellationToken);
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

public sealed record StoredVehicleSnapshot(
    string Identity,
    DateTimeOffset ObservedAt,
    AuctionVehicle Vehicle,
    string RawJson);

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
