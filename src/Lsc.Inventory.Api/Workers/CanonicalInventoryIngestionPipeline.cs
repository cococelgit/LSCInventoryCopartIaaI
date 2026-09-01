using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Storage;

namespace Lsc.Inventory.Api.Workers;

public sealed record CanonicalIngestionResult(
    AuctionVehicle Vehicle,
    EligibilityEvaluation Eligibility,
    InventoryLotPersistenceResult? Persistence,
    bool Loaded,
    bool Marked,
    bool Quarantined,
    bool Discarded);

public interface ICanonicalInventoryIngestionPipeline
{
    Task<CanonicalIngestionResult> ProcessAsync(AuctionVehicle providerVehicle, DateTimeOffset observedAt, CancellationToken cancellationToken, Guid? runId = null, AuctionVehicle? persistenceVehicle = null, bool persist = true);
}

/// <summary>
/// The only business-processing boundary for provider vehicles. Providers are
/// responsible for obtaining and mapping data only; this component owns the
/// canonical normalization, eligibility, persistence, lifecycle side effects,
/// and audit path shared by every source.
/// </summary>
public sealed class CanonicalInventoryIngestionPipeline(
    IInventorySnapshotStore snapshotStore) : ICanonicalInventoryIngestionPipeline
{
    public async Task<CanonicalIngestionResult> ProcessAsync(AuctionVehicle providerVehicle, DateTimeOffset observedAt, CancellationToken cancellationToken, Guid? runId = null, AuctionVehicle? persistenceVehicle = null, bool persist = true)
    {
        var evaluatedVehicle = CanonicalVehicleCleaner.Clean(AuctionVehicleNormalizer.Normalize(providerVehicle, null, null));
        var vehicleToPersist = persistenceVehicle is null
            ? evaluatedVehicle
            : CanonicalVehicleCleaner.Clean(AuctionVehicleNormalizer.Normalize(persistenceVehicle, null, null));
        var eligibility = AuctionEligibilityEvaluator.Evaluate(evaluatedVehicle, observedAt);
        if (!persist) return new(evaluatedVehicle, eligibility, null, eligibility.LoadToSystem, eligibility.Decision == "MARCAR", eligibility.Decision == "CUARENTENA", eligibility.Decision != "CUARENTENA");
        await snapshotStore.PersistEligibilityDecisionAsync(eligibility, observedAt, cancellationToken);
        if (!eligibility.LoadToSystem)
        {
            return new(
                evaluatedVehicle,
                eligibility,
                null,
                false,
                eligibility.Decision == "MARCAR",
                eligibility.Decision == "CUARENTENA",
                eligibility.Decision != "CUARENTENA");
        }

        var persistence = await snapshotStore.PersistAsync(vehicleToPersist, observedAt, cancellationToken, runId);
        return new(evaluatedVehicle, eligibility, persistence, true, eligibility.Decision == "MARCAR", false, false);
    }
}
