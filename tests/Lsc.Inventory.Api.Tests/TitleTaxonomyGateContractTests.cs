using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class TitleTaxonomyGateContractTests
{
    [Fact]
    public void Keeps_taxonomy_facets_disabled_by_default_and_blocks_unvalidated_requests()
    {
        var source = File.ReadAllText(FindRepositoryFile("Program.cs"));

        Assert.Contains("GetValue(\"TitleTaxonomy:FacetsEnabled\", false)", source, StringComparison.Ordinal);
        Assert.Contains("StatusCodes.Status503ServiceUnavailable", source, StringComparison.Ordinal);
        Assert.Contains("titleCategories is { Length: > 0 }", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Measures_taxonomy_coverage_from_the_active_projection_not_version_history()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.cs"));
        var coverageStart = source.IndexOf("GetCopartTitleTaxonomyCoverageAsync", StringComparison.Ordinal);
        var coverageEnd = source.IndexOf("private async Task RefreshSearchFacetsAsync", coverageStart, StringComparison.Ordinal);
        var coverage = source[coverageStart..coverageEnd];

        Assert.Contains("from inventory_search_current", coverage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("where is_active", coverage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("auction_lot_versions", coverage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("distinct on", coverage, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryFile(string fileName)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var apiProjectDirectory = Path.Combine(directory.FullName, "src", "Lsc.Inventory.Api");
                if (!Directory.Exists(apiProjectDirectory)) continue;

                var candidate = Directory.EnumerateFiles(apiProjectDirectory, fileName, SearchOption.AllDirectories).FirstOrDefault();
                if (candidate is not null) return candidate;
            }
        }
        throw new FileNotFoundException(fileName);
    }
}
