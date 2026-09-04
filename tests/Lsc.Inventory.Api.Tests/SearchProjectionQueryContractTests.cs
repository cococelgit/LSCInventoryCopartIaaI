using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class SearchProjectionQueryContractTests
{
    [Fact]
    public void ProjectionBrowseFiltersOnProjectionActiveFlag()
    {
        var sourcePath = FindRepositoryFile("PostgresSnapshotStore.cs");
        var source = File.ReadAllText(sourcePath);
        var methodStart = source.IndexOf("private async Task<InventorySearchPage> SearchProjectionAsync", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "SearchProjectionAsync must remain present.");

        var methodEnd = source.IndexOf("private async Task<InventorySearchSummary?> GetCachedProjectionSummaryAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "SearchProjectionAsync boundary must remain discoverable.");

        var method = source[methodStart..methodEnd];
        Assert.Contains("var where = new List<string> { \"latest.is_active\" };", method);
        Assert.Contains("var itemWhere = new List<string> { \"latest.is_active\" };", method);
        Assert.DoesNotContain("left join inventory_lot_lifecycle lifecycle", method, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DefaultVisibleBrowseFallsBackWhenTheVisibleCountCacheIsStale()
    {
        var sourcePath = FindRepositoryFile("PostgresSnapshotStore.cs");
        var source = File.ReadAllText(sourcePath);
        var methodStart = source.IndexOf("private async Task<int> GetProjectionTotalAsync", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "GetProjectionTotalAsync must remain present.");
        var methodEnd = source.IndexOf("private static void AddPlatformParameter", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "GetProjectionTotalAsync boundary must remain discoverable.");
        var method = source[methodStart..methodEnd];

        Assert.Contains("select row_count, visible_row_count", method);
        Assert.Contains("request.ExcludeSpecialTitles || rowCount == 0 || visibleRowCount > 0", method);
        Assert.Contains("stale cache report zero", method, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProjectionRebuildUsesCopartBodyStyleAsCanonicalVehicleType()
    {
        var sourcePath = FindRepositoryFile("PostgresSnapshotStore.cs");
        var source = File.ReadAllText(sourcePath);
        var methodStart = source.IndexOf("public async Task<InventorySearchProjectionStatus> RebuildSearchProjectionAsync", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "RebuildSearchProjectionAsync must remain present.");
        var methodEnd = source.IndexOf("public async Task<InventorySearchProjectionStatus> GetSearchProjectionStatusAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "RebuildSearchProjectionAsync boundary must remain discoverable.");
        var method = source[methodStart..methodEnd];

        Assert.Contains("lower(lots.platform) = 'copart'", method);
        Assert.Contains("latest.payload #>> '{vehicle_specs,body_style}'", method);
        Assert.Contains("latest.payload #>> '{details,vehicle_description,BodyStyle}'", method);
        Assert.Contains("else lots.vehicle_type end", method);
    }

    [Fact]
    public void ProjectionRebuildPersistsTheVisibleActiveCount()
    {
        var sourcePath = FindRepositoryFile("PostgresSnapshotStore.cs");
        var source = File.ReadAllText(sourcePath);
        var methodStart = source.IndexOf("public async Task<InventorySearchProjectionStatus> RebuildSearchProjectionAsync", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "RebuildSearchProjectionAsync must remain present.");
        var methodEnd = source.IndexOf("public async Task<InventorySearchProjectionStatus> GetSearchProjectionStatusAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "RebuildSearchProjectionAsync boundary must remain discoverable.");
        var method = source[methodStart..methodEnd];

        Assert.Contains("count(*) filter (where not is_special_title)::bigint as visible_rows", method);
        Assert.Contains("visible_row_count = stats.visible_rows", method);
        Assert.Contains("from inventory_search_current where is_active", method);
    }

    private static string FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Lsc.Inventory.Api", "Storage", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {fileName} from {AppContext.BaseDirectory}");
    }
}
