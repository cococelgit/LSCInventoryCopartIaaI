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
