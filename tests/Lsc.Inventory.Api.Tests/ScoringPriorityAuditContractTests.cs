using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class ScoringPriorityAuditContractTests
{
    [Fact]
    public void Queue_keeps_new_or_changed_lots_ahead_of_historical_backfill_and_limits_retries()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.Scoring.cs"));

        Assert.Contains("private const int HighPriorityScoring = 100", source, StringComparison.Ordinal);
        Assert.Contains("private const int BackfillPriorityScoring = 10", source, StringComparison.Ordinal);
        Assert.Contains("private const int MaximumScoringAttempts = 3", source, StringComparison.Ordinal);
        Assert.Contains("order by priority desc, requested_at asc, lot_key asc", source, StringComparison.Ordinal);
        Assert.Contains("priority = greatest(inventory_vehicle_scoring_queue.priority, excluded.priority)", source, StringComparison.Ordinal);
        Assert.Contains("inventory_vehicle_scoring_runs", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Historical_backfill_round_robins_active_platforms_without_downgrading_high_priority_lots()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.Scoring.cs"));

        Assert.Contains("partition by current.platform", source, StringComparison.Ordinal);
        Assert.Contains("as platform_position", source, StringComparison.Ordinal);
        Assert.Contains("order by platform_position asc, platform asc, lot_key asc", source, StringComparison.Ordinal);
        Assert.Contains("order by priority desc, requested_at asc, lot_key asc", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Durable_queue_claims_historical_debt_evenly_after_reserving_capacity_for_high_priority_lots()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.Scoring.cs"));

        Assert.Contains("with high_priority as", source, StringComparison.Ordinal);
        Assert.Contains("priority >= @high_priority", source, StringComparison.Ordinal);
        Assert.Contains("priority < @high_priority", source, StringComparison.Ordinal);
        Assert.Contains("row_number() over (", source, StringComparison.Ordinal);
        Assert.Contains("partition by platform", source, StringComparison.Ordinal);
        Assert.Contains("cross join remaining", source, StringComparison.Ordinal);
        Assert.Contains("order by low.platform_position asc, low.platform asc", source, StringComparison.Ordinal);
        Assert.Contains("for update of queue skip locked", source, StringComparison.Ordinal);
        Assert.Contains("AddParameter(command, \"high_priority\", HighPriorityScoring)", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = directory.EnumerateFiles(fileName, SearchOption.AllDirectories)
                .FirstOrDefault(file => file.FullName.EndsWith(
                    Path.Combine("src", "Lsc.Inventory.Api", "Storage", fileName),
                    StringComparison.OrdinalIgnoreCase));
            if (candidate is not null) return candidate.FullName;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(fileName);
    }
}
