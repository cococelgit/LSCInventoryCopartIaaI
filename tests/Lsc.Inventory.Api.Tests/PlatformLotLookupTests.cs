using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class PlatformLotLookupTests
{
    [Fact]
    public async Task Resolves_the_active_snapshot_by_platform_and_lot_when_lot_numbers_collide()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.Parse("2026-08-28T15:00:00Z");

        await store.PersistAsync(new AuctionVehicle { Platform = "copart", LotNumber = "48826366", Make = "Ford" }, observedAt, CancellationToken.None);
        await store.PersistAsync(new AuctionVehicle { Platform = "iaai", LotNumber = "48826366", Make = "Honda" }, observedAt.AddMinutes(1), CancellationToken.None);

        var copart = await store.GetByPlatformAndLotAsync("copart", "48826366", CancellationToken.None);
        var iaai = await store.GetByPlatformAndLotAsync("iaai", "48826366", CancellationToken.None);

        Assert.NotNull(copart);
        Assert.NotNull(iaai);
        Assert.Equal("Ford", copart.Vehicle.Make);
        Assert.Equal("Honda", iaai.Vehicle.Make);
    }

    [Fact]
    public async Task Excludes_a_lot_deactivated_after_three_misses_from_search_summary_and_exact_detail()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.Parse("2026-08-28T19:00:00Z");
        await store.PersistAsync(new AuctionVehicle
        {
            Platform = "copart",
            LotNumber = "48826366",
            Make = "Ford",
            Model = "Escape"
        }, observedAt, CancellationToken.None);

        for (var miss = 0; miss < 3; miss++)
            await store.ReconcileSourceAsync("copart", [], true, observedAt.AddMinutes(miss + 1), CancellationToken.None);

        var search = await store.SearchAsync(new InventorySearchRequest(1, 20, Query: "48826366", Platform: "copart"), CancellationToken.None);
        var summary = await store.GetInventorySearchSummaryAsync(new InventorySearchRequest(1, 20), CancellationToken.None);
        var detail = await store.GetByPlatformAndLotAsync("copart", "48826366", CancellationToken.None);

        Assert.Equal(0, search.Total);
        Assert.Empty(search.Items);
        Assert.Equal(0, summary.Total);
        Assert.Null(detail);
    }
}
