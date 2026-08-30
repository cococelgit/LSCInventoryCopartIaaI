using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartTitleTaxonomyContractTests
{
    [Fact]
    public async Task Filters_by_normalized_copart_category_from_payload_metadata()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.Parse("2026-08-30T00:00:00Z");
        await store.PersistAsync(new AuctionVehicle
        {
            Platform = "copart",
            LotNumber = "bs-1",
            AdditionalData = Metadata("SALVAGE", ["FIRE"], "STANDARD", "copart-title-taxonomy-v1")
        }, observedAt, CancellationToken.None);
        await store.PersistAsync(new AuctionVehicle
        {
            Platform = "copart",
            LotNumber = "aq-1",
            AdditionalData = Metadata("CLEAN", [], "STANDARD", "copart-title-taxonomy-v1")
        }, observedAt, CancellationToken.None);

        var result = await store.SearchAsync(new InventorySearchRequest(
            1,
            20,
            TitleCategories: ["SALVAGE"]), CancellationToken.None);

        var item = Assert.Single(result.Items);
        Assert.Equal("bs-1", item.Vehicle.LotNumber);
        Assert.Equal("SALVAGE", item.Vehicle.AdditionalData!["title_category"].GetString());
    }

    [Fact]
    public async Task Never_matches_iaai_when_filtering_a_copart_normalized_category()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistAsync(new AuctionVehicle
        {
            Platform = "iaai",
            LotNumber = "iaai-1",
            SaleDocument = new SaleDocument { Name = "SALVAGE" },
            AdditionalData = Metadata("SALVAGE", ["FIRE"], "STANDARD", "copart-title-taxonomy-v1")
        }, DateTimeOffset.UtcNow, CancellationToken.None);

        var result = await store.SearchAsync(new InventorySearchRequest(
            1,
            20,
            TitleCategories: ["SALVAGE"]), CancellationToken.None);

        Assert.Empty(result.Items);
        Assert.Equal(0, result.Total);
    }

    private static Dictionary<string, JsonElement> Metadata(string category, string[] flags, string reviewStatus, string version) => new()
    {
        ["title_category"] = JsonSerializer.SerializeToElement(category),
        ["title_flags"] = JsonSerializer.SerializeToElement(flags),
        ["title_review_status"] = JsonSerializer.SerializeToElement(reviewStatus),
        ["title_taxonomy_version"] = JsonSerializer.SerializeToElement(version)
    };
}
