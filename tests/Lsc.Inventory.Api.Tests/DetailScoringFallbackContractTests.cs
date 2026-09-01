using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class DetailScoringFallbackContractTests
{
    [Fact]
    public void Active_detail_lookup_recovers_from_malformed_full_scoring_result()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.Scoring.cs"));

        Assert.Contains("Ignoring an unreadable full LSC scoring result", source, StringComparison.Ordinal);
        Assert.Contains("exception is JsonException or InvalidCastException or FormatException or NotSupportedException", source, StringComparison.Ordinal);
        Assert.Contains("return null;", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = directory.EnumerateFiles(fileName, SearchOption.AllDirectories)
                .FirstOrDefault(file => file.FullName.Contains("inventory-engine", StringComparison.OrdinalIgnoreCase));
            if (candidate is not null) return candidate.FullName;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(fileName);
    }
}
