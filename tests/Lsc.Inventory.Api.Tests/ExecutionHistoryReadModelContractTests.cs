using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class ExecutionHistoryReadModelContractTests
{
    [Fact]
    public void History_consolidates_duplicate_provider_rows_and_preserves_unknown_event_metrics()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.cs"));
        var start = source.IndexOf("public async Task<InventoryExecutionHistoryPage> GetExecutionHistoryAsync", StringComparison.Ordinal);
        var end = source.IndexOf("public async Task<InventoryExecutionEventPage> GetExecutionEventsAsync", start, StringComparison.Ordinal);

        Assert.True(start >= 0 && end > start);
        var method = source[start..end];
        Assert.Contains("with raw_history as", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("group by run_id", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max(loaded_count)", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("bool_or(cycle_completed) filter (where cycle_completed is not null)", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copart_snapshot_manifests", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("where manifest.run_id = history.run_id", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copart_manifest.status = 'succeeded'", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("copart_manifest.is_complete = true", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("when history.provider <> 'copart-excel' and events.event_count > 0", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("when history.created_count is not null", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("when history.updated_count is not null", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("when history.unchanged_count is not null", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max(stale_count)", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max(archived_observed_count)", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max(scoring_completed_count)", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("where (@platform = '' or history.platform = @platform) and (@status = '' or history.status = @status)", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("order by history.started_at desc", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ReadNullableInt32(reader, 28)", method, StringComparison.Ordinal);
        Assert.Contains("ReadStringArray(reader, 27)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("coalesce(events.created_count, 0)", method, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Incremental_progress_updates_live_counts_before_completion()
    {
        var storeSource = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.cs"));
        var processorSource = File.ReadAllText(FindRepositoryFile("../Workers/AuctionsApiIncrementalSyncProcessor.cs"));
        Assert.Contains("UpdateSyncRunProgressAsync", storeSource, StringComparison.Ordinal);
        Assert.Contains("vehicles_observed = @vehicles_observed", storeSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("loaded_count = excluded.loaded_count", storeSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("created_count = excluded.created_count", storeSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("updated_count = excluded.updated_count", storeSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("unchanged_count = excluded.unchanged_count", storeSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("stale_count = excluded.stale_count", storeSource, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("if ((changed % 25) == 0)", processorSource, StringComparison.Ordinal);
        Assert.Contains("InventorySyncRunProgress", processorSource, StringComparison.Ordinal);
    }

    [Fact]
    public void V2_initial_loader_closes_runs_with_named_and_reconciled_metrics()
    {
        var source = File.ReadAllText(FindRepositoryFile("../Workers/AuctionsApiV2InitialLoadProcessor.cs"));

        Assert.Contains("Loaded: persist ? created + updated + unchanged : eligible", source, StringComparison.Ordinal);
        Assert.Contains("Created: created", source, StringComparison.Ordinal);
        Assert.Contains("Updated: updated", source, StringComparison.Ordinal);
        Assert.Contains("Unchanged: unchanged", source, StringComparison.Ordinal);
        Assert.Contains("Stale: stale", source, StringComparison.Ordinal);
        Assert.Contains("Discarded: discarded", source, StringComparison.Ordinal);
        Assert.Contains("Quarantined: quarantined", source, StringComparison.Ordinal);
        Assert.Contains("Errors: failures.Count", source, StringComparison.Ordinal);
        Assert.DoesNotContain("discarded + quarantined", source, StringComparison.Ordinal);
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
        throw new FileNotFoundException(fileName);
    }
}
