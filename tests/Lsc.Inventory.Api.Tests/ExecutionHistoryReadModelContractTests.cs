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
        Assert.Contains("where (@platform = '' or history.platform = @platform) and (@status = '' or history.status = @status)", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("order by history.started_at desc", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ReadNullableInt32(reader, 23)", method, StringComparison.Ordinal);
        Assert.Contains("ReadStringArray(reader, 22)", method, StringComparison.Ordinal);
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
        Assert.Contains("if ((changed % 25) == 0)", processorSource, StringComparison.Ordinal);
        Assert.Contains("InventorySyncRunProgress", processorSource, StringComparison.Ordinal);
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
