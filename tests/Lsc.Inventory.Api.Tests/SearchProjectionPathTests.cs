using System.Reflection;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class SearchProjectionPathTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("updated-desc", null)]
    [InlineData("pregrade-desc", null)]
    [InlineData("pregrade-desc", "copart")]
    [InlineData("auction-desc", null)]
    [InlineData("pregrade-asc", null)]
    public void Uses_unified_projection_path_for_all_sort_modes(string? sort, string? platform)
    {
        var request = new InventorySearchRequest(Page: 1, PageSize: 20, Sort: sort, Platform: platform);

        Assert.False(InvokeBoolean("IsPreGradeBaselineSearch", request));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("copart")]
    [InlineData("iaai")]
    public void Keeps_default_special_title_exclusion_on_the_pregrade_baseline_path(string? platform)
    {
        var request = new InventorySearchRequest(
            Page: 1,
            PageSize: 20,
            Sort: "pregrade-desc",
            Platform: platform,
            ExcludeSpecialTitles: true);

        Assert.False(InvokeBoolean("IsPreGradeBaselineSearch", request));
        Assert.Equal(" and not latest.is_special_title", InvokeString("ProjectionVisibilityClause", request));
    }

    [Fact]
    public void Keeps_filtered_searches_out_of_the_pregrade_baseline_path()
    {
        var request = new InventorySearchRequest(Page: 1, PageSize: 20, Sort: "pregrade-desc", Makes: ["Toyota"]);

        Assert.False(InvokeBoolean("IsPreGradeBaselineSearch", request));
    }

    [Theory]
    [InlineData(null, "latest.observed_at desc nulls last")]
    [InlineData("updated-desc", "latest.observed_at desc nulls last")]
    [InlineData("buy-desc", "latest.buy_now_usd desc nulls last")]
    [InlineData("auction", "latest.auction_at asc nulls last")]
    public void Always_orders_by_grading_first_and_uses_requested_sort_as_secondary(string? sort, string secondary)
    {
        var ordering = InvokeString("GetProjectionOrdering", sort);

        Assert.Equal($"score.pre_grade desc nulls last, {secondary}", ordering);
    }

    [Theory]
    [InlineData(null, "latest.observed_at desc nulls last")]
    [InlineData("updated-desc", "latest.observed_at desc nulls last")]
    [InlineData("buy-desc", "latest.buy_now_usd desc nulls last")]
    public void Fallback_search_also_orders_by_grading_first(string? sort, string secondary)
    {
        var ordering = InvokeString("GetSearchOrdering", sort);

        Assert.Equal($"score.pre_grade desc nulls last, {secondary}", ordering);
    }

    [Fact]
    public void Reuses_cached_total_for_an_unfiltered_search_regardless_of_sort()
    {
        var request = new InventorySearchRequest(Page: 1, PageSize: 20, Sort: "bid-desc");

        Assert.True(InvokeBoolean("IsDefaultVisibleSearch", request));
    }

    private static bool InvokeBoolean(string methodName, InventorySearchRequest request)
    {
        var method = typeof(PostgresSnapshotStore).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<bool>(method!.Invoke(null, [request]));
    }

    private static string InvokeString(string methodName, InventorySearchRequest request)
    {
        var method = typeof(PostgresSnapshotStore).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [request]));
    }

    private static string InvokeString(string methodName, string? sort)
    {
        var method = typeof(PostgresSnapshotStore).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsType<string>(method!.Invoke(null, [sort]));
    }
}
