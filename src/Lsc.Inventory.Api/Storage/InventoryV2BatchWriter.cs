using Lsc.Inventory.Api.Contracts;

namespace Lsc.Inventory.Api.Storage;

public interface IInventoryV2BatchWriter
{
    int PreferredBatchSize { get; }
    bool ShadowWriteConfigured { get; }
    Task<InventoryV2BatchWriteResult> WriteShadowBatchAsync(
        IReadOnlyCollection<InventoryV2BatchItem> items,
        CancellationToken cancellationToken);
}

public sealed record InventoryV2BatchItem(AuctionVehicle Vehicle, DateTimeOffset ObservedAt);

public sealed record InventoryV2BatchWriteResult(
    bool Attempted,
    int InputRows,
    int DistinctRows,
    int Created,
    int Updated,
    int Unchanged,
    int Stale,
    int MediaRowsWritten,
    long DurationMs,
    string? SkipReason = null)
{
    public static InventoryV2BatchWriteResult Skipped(int inputRows, string reason) =>
        new(false, inputRows, 0, 0, 0, 0, 0, 0, 0, reason);
}

public sealed class DisabledInventoryV2BatchWriter : IInventoryV2BatchWriter
{
    public static readonly DisabledInventoryV2BatchWriter Instance = new();

    private DisabledInventoryV2BatchWriter()
    {
    }

    public int PreferredBatchSize => 1000;
    public bool ShadowWriteConfigured => false;

    public Task<InventoryV2BatchWriteResult> WriteShadowBatchAsync(
        IReadOnlyCollection<InventoryV2BatchItem> items,
        CancellationToken cancellationToken) =>
        Task.FromResult(InventoryV2BatchWriteResult.Skipped(items.Count, "postgres-required"));
}
