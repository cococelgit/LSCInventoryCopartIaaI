using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class InventoryScoringV2TimestampContractTests
{
    [Fact]
    public void Scoring_queries_use_the_inventory_v2_last_seen_timestamp()
    {
        var path = LocateRepositoryFile("src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.Scoring.cs");
        var source = File.ReadAllText(path);

        Assert.DoesNotContain("current.observed_at", source, StringComparison.Ordinal);
        Assert.DoesNotContain("active.observed_at", source, StringComparison.Ordinal);
        Assert.Contains("current.last_seen_at", source, StringComparison.Ordinal);
        Assert.Contains("active.last_seen_at", source, StringComparison.Ordinal);
    }

    private static string LocateRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file: {relativePath}");
    }
}
