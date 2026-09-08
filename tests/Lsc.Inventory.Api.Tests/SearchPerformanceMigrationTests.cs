using Lsc.Inventory.Api.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class SearchPerformanceMigrationTests
{
    [Fact]
    public void SearchPerformanceFlagsRemainDisabledByDefault()
    {
        var options = new PersistenceOptions();

        Assert.False(options.UseDenormalizedScoringSearch);
        Assert.False(options.RunBrowseCountAndItemsInParallel);
    }

    [Fact]
    public void MigrationIsAdditiveIndexedAndHasAnExplicitRollback()
    {
        var migration = File.ReadAllText(FindRepositoryFile("infra", "sql", "016_search_projection_denormalized_score_v1.sql"));
        var rollback = File.ReadAllText(FindRepositoryFile("infra", "sql", "016_search_projection_denormalized_score_v1_rollback.sql"));

        Assert.Contains("add column if not exists score_pre_grade numeric", migration);
        Assert.Contains("ix_inventory_search_visible_score_observed_v7", migration);
        Assert.Contains("create index concurrently if not exists", migration);
        Assert.Contains("analyze inventory_search_current", migration);
        Assert.Contains("drop index concurrently if exists ix_inventory_search_visible_score_observed_v7", rollback);
        Assert.Contains("drop column if exists score_pre_grade", rollback);
    }

    private static string FindRepositoryFile(params string[] relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine([directory.FullName, .. relativePath]);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {string.Join('/', relativePath)} from {AppContext.BaseDirectory}");
    }
}
