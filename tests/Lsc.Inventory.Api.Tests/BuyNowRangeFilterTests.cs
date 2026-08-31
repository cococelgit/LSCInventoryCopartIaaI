using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class BuyNowRangeFilterTests
{
    [Fact]
    public async Task BuyNowRangeFiltersOnlyVehiclesWithBuyNowValueInsideRange()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.Parse("2026-08-31T18:00:00Z");
        await store.PersistAsync(new AuctionVehicle
        {
            Platform = "copart",
            LotNumber = "with-buy-now",
            Pricing = new PricingInfo { CurrentBidUsd = 500m, BuyNowUsd = 8_000m }
        }, observedAt, CancellationToken.None);
        await store.PersistAsync(new AuctionVehicle
        {
            Platform = "iaai",
            LotNumber = "bid-only",
            Pricing = new PricingInfo { CurrentBidUsd = 8_000m }
        }, observedAt, CancellationToken.None);
        await store.PersistAsync(new AuctionVehicle
        {
            Platform = "copart",
            LotNumber = "outside-range",
            Pricing = new PricingInfo { CurrentBidUsd = 50m, BuyNowUsd = 12_000m }
        }, observedAt, CancellationToken.None);

        var result = await store.SearchAsync(new InventorySearchRequest(1, 20, BuyNowFrom: 7_000m, BuyNowTo: 9_000m), CancellationToken.None);

        var matching = Assert.Single(result.Items);
        Assert.Equal("with-buy-now", matching.Vehicle.LotNumber);
    }

    [Fact]
    public void ProjectionFallbackAndFacetsApplyDedicatedBuyNowColumns()
    {
        var root = FindRepositoryRoot();
        var postgres = File.ReadAllText(Path.Combine(root, "src", "Lsc.Inventory.Api", "Storage", "PostgresSnapshotStore.cs"));
        var facets = File.ReadAllText(Path.Combine(root, "src", "Lsc.Inventory.Api", "Storage", "PostgresSnapshotStore.FacetsV2.cs"));
        var program = File.ReadAllText(Path.Combine(root, "src", "Lsc.Inventory.Api", "Program.cs"));

        Assert.True(Count(postgres, "latest.buy_now_usd >= @buy_now_from") >= 2);
        Assert.True(Count(postgres, "latest.buy_now_usd <= @buy_now_to") >= 2);
        Assert.Contains("latest.buy_now_usd >= @facet_buy_now_from", facets);
        Assert.Contains("latest.buy_now_usd <= @facet_buy_now_to", facets);
        Assert.Contains("decimal? buyNowFrom", program);
        Assert.Contains("decimal? buyNowTo", program);
        Assert.Contains("BuyNowFrom: buyNowFrom", program);
        Assert.Contains("BuyNowTo: buyNowTo", program);
    }

    private static int Count(string source, string value) => source.Split(value, StringSplitOptions.None).Length - 1;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Lsc.Inventory.sln"))) return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the API repository root.");
    }
}
