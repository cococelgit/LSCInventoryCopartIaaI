using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class TitleFacetCategoryTests
{
    [Theory]
    [InlineData("CLEAR", TitleFacetCategory.Clean)]
    [InlineData("SALVAGE", TitleFacetCategory.Salvage)]
    [InlineData("SALVAGE - REBUILT", TitleFacetCategory.Rebuilt)]
    [InlineData("CERTIFICATE OF DESTRUCTION", TitleFacetCategory.Special)]
    [InlineData("CT", TitleFacetCategory.Other)]
    [InlineData("NO REPORTADO", TitleFacetCategory.Unverified)]
    public void Classifies_only_document_meanings_explicitly_present_in_the_source(string source, string expected)
    {
        Assert.Equal(expected, TitleFacetCategory.Classify(source));
    }

    [Fact]
    public void Applies_the_approved_code_dictionary_only_to_copart()
    {
        var copart = new AuctionVehicle { Platform = "copart", LotNumber = "copart-ct", Title = "CT" };
        var iaai = new AuctionVehicle { Platform = "iaai", LotNumber = "iaai-ct", SaleDocument = new SaleDocument { Name = "CT" } };

        Assert.Equal(TitleFacetCategory.Clean, TitleFacetCategory.Classify(copart));
        Assert.Equal("Clean Title · Theft Recovery", TitleFacetCategory.Describe(copart).DisplayLabel);
        Assert.Equal(TitleFacetCategory.Other, TitleFacetCategory.Classify(iaai));
    }

    [Theory]
    [InlineData("CQ", TitleFacetCategory.Special)]
    [InlineData("CD", TitleFacetCategory.Rebuilt)]
    [InlineData("CW", TitleFacetCategory.Other)]
    [InlineData("B1", TitleFacetCategory.Other)]
    public void Uses_protective_precedence_for_conflicting_or_state_dependent_copart_codes(string code, string expected)
    {
        Assert.Equal(expected, TitleFacetCategory.Classify("copart", code));
    }

    [Fact]
    public void Generates_rebuild_sql_from_the_same_copart_dictionary_with_protective_precedence()
    {
        var sql = TitleFacetCategory.BuildSqlCaseExpression("document", "platform");

        Assert.Contains("platform = 'copart'", sql, StringComparison.Ordinal);
        Assert.Contains("'CT'", sql, StringComparison.Ordinal);
        Assert.Contains("'CQ'", sql, StringComparison.Ordinal);
        Assert.Contains("'CD'", sql, StringComparison.Ordinal);
        Assert.True(sql.IndexOf("then 'SPECIAL'", StringComparison.Ordinal) < sql.IndexOf("then 'REBUILT'", StringComparison.Ordinal));
        Assert.True(sql.IndexOf("then 'REBUILT'", StringComparison.Ordinal) < sql.IndexOf("then 'SALVAGE'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Uses_operational_title_categories_for_summary_filters_and_default_special_exclusion()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.Parse("2026-08-29T00:00:00Z");
        await store.PersistAsync(new AuctionVehicle { Platform = "copart", LotNumber = "1", Title = "CLEAR" }, observedAt, CancellationToken.None);
        await store.PersistAsync(new AuctionVehicle { Platform = "copart", LotNumber = "2", Title = "SALVAGE" }, observedAt, CancellationToken.None);
        await store.PersistAsync(new AuctionVehicle { Platform = "iaai", LotNumber = "3", SaleDocument = new SaleDocument { Name = "SALVAGE - REBUILT" } }, observedAt, CancellationToken.None);
        await store.PersistAsync(new AuctionVehicle { Platform = "iaai", LotNumber = "4", SaleDocument = new SaleDocument { Name = "JUNK" } }, observedAt, CancellationToken.None);
        await store.PersistAsync(new AuctionVehicle { Platform = "iaai", LotNumber = "5", SaleDocument = new SaleDocument { Name = "CT" } }, observedAt, CancellationToken.None);

        var summary = await store.GetInventorySearchSummaryAsync(new InventorySearchRequest(1, 20), CancellationToken.None);
        var categories = summary.Facets["titles"].Select(item => item.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var salvage = await store.SearchAsync(new InventorySearchRequest(1, 20, Titles: [TitleFacetCategory.Salvage]), CancellationToken.None);
        var withoutSpecial = await store.SearchAsync(new InventorySearchRequest(1, 20, ExcludeSpecialTitles: true), CancellationToken.None);

        Assert.Equal(
            new[] { TitleFacetCategory.Clean, TitleFacetCategory.Salvage, TitleFacetCategory.Rebuilt, TitleFacetCategory.Special, TitleFacetCategory.Other }.Order(),
            categories.Order());
        Assert.Equal(1, salvage.Total);
        Assert.Equal(4, withoutSpecial.Total);
    }
}
