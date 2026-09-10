using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class SoldLotRetentionDryRunTests
{
    [Fact]
    public void DryRunSql_UsesInactiveCutoffAndContainsNoDestructiveStatement()
    {
        var sql = PostgresSnapshotStore.SoldLotRetentionDryRunSql.ToLowerInvariant();
        var historicalSql = PostgresSnapshotStore.HistoricalLotStatusInventorySql.ToLowerInvariant();

        Assert.Contains("not lifecycle.is_active", sql);
        Assert.Contains("lifecycle.deactivated_at <= @cutoff_at", sql);
        Assert.Contains("auction_lot_versions", sql);
        Assert.DoesNotContain("delete ", sql);
        Assert.DoesNotContain("update ", sql);
        Assert.DoesNotContain("insert ", sql);
        Assert.DoesNotContain("alter ", sql);
        Assert.Contains("lots.auction_at <= @cutoff_at", historicalSql);
        Assert.Contains("group by lot_status, lot_sub_status", historicalSql);
        Assert.DoesNotContain("delete ", historicalSql);
        Assert.DoesNotContain("update ", historicalSql);
        Assert.DoesNotContain("insert ", historicalSql);
    }
}
