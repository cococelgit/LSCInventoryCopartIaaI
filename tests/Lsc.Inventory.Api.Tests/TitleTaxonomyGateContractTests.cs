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

    private static string FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = directory.EnumerateFiles(fileName, SearchOption.AllDirectories).FirstOrDefault(file =>
                file.FullName.EndsWith($"{Path.DirectorySeparatorChar}src{Path.DirectorySeparatorChar}Lsc.Inventory.Api{Path.DirectorySeparatorChar}{fileName}", StringComparison.OrdinalIgnoreCase) &&
                !file.FullName.Contains($"{Path.DirectorySeparatorChar}inventory-engine{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
            if (candidate is not null) return candidate.FullName;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(fileName);
    }
}
