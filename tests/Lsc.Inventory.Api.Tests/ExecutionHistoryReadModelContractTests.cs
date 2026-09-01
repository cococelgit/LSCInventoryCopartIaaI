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
        Assert.Contains("case when events.event_count = 0 then null else events.created_count end", method, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ReadNullableInt32(reader, 20)", method, StringComparison.Ordinal);
        Assert.DoesNotContain("coalesce(events.created_count, 0)", method, StringComparison.OrdinalIgnoreCase);
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
