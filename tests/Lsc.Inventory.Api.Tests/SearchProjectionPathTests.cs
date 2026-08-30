using System.Reflection;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class SearchProjectionPathTests
{
    [Theory]
    [InlineData(null, null, true)]
    [InlineData("pregrade-desc", null, true)]
    [InlineData("pregrade-desc", "copart", true)]
    [InlineData("auction-desc", null, false)]
    [InlineData("pregrade-asc", null, false)]
    public void Uses_pregrade_baseline_path_only_for_unfiltered_descending_pregrade_searches(string? sort, string? platform, bool expected)
    {
        var request = new InventorySearchRequest(Page: 1, PageSize: 20, Sort: sort, Platform: platform);

        Assert.Equal(expected, InvokeBoolean("IsPreGradeBaselineSearch", request));
    }

    [Fact]
    public void Keeps_filtered_searches_out_of_the_pregrade_baseline_path()
    {
        var request = new InventorySearchRequest(Page: 1, PageSize: 20, Sort: "pregrade-desc", Makes: ["Toyota"]);

        Assert.False(InvokeBoolean("IsPreGradeBaselineSearch", request));
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
}
