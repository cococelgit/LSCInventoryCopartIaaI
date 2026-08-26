using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class InventoryReconciliationTests
{
    [Fact]
    public async Task Deactivates_after_three_complete_misses_and_reactivates_when_seen_again()
    {
        var store = new InMemorySnapshotStore();
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        await store.PersistAsync(new AuctionVehicle { Platform = "iaai", LotNumber = "100" }, now, CancellationToken.None);
        await store.PersistAsync(new AuctionVehicle { Platform = "iaai", LotNumber = "200" }, now, CancellationToken.None);

        var first = await store.ReconcileSourceAsync("iaai", ["iaai:100"], true, now.AddMinutes(30), CancellationToken.None);
        Assert.Equal(0, first.Deactivated);
        Assert.Equal(2, (await store.GetRecentAsync(10, CancellationToken.None)).Count);

        var second = await store.ReconcileSourceAsync("iaai", ["iaai:100"], true, now.AddHours(1), CancellationToken.None);
        Assert.Equal(0, second.Deactivated);
        Assert.Equal(2, (await store.GetRecentAsync(10, CancellationToken.None)).Count);

        var third = await store.ReconcileSourceAsync("iaai", ["iaai:100"], true, now.AddMinutes(90), CancellationToken.None);
        Assert.Equal(1, third.Deactivated);
        Assert.Single(await store.GetRecentAsync(10, CancellationToken.None));

        await store.PersistAsync(new AuctionVehicle { Platform = "iaai", LotNumber = "200" }, now.AddHours(2), CancellationToken.None);
        Assert.Equal(2, (await store.GetRecentAsync(10, CancellationToken.None)).Count);
    }

    [Fact]
    public async Task Does_not_change_lifecycle_for_partial_snapshot()
    {
        var store = new InMemorySnapshotStore();
        var now = DateTimeOffset.Parse("2026-08-25T12:00:00Z");
        await store.PersistAsync(new AuctionVehicle { Platform = "iaai", LotNumber = "100" }, now, CancellationToken.None);
        var result = await store.ReconcileSourceAsync("iaai", [], false, now.AddMinutes(30), CancellationToken.None);
        Assert.False(result.Applied);
        Assert.Single(await store.GetRecentAsync(10, CancellationToken.None));
    }
}
