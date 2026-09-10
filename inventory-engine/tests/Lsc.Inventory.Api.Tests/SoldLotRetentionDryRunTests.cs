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
        Assert.DoesNotContain("payload::text", sql);
        Assert.DoesNotContain("delete ", sql);
        Assert.DoesNotContain("update ", sql);
        Assert.DoesNotContain("insert ", sql);
        Assert.DoesNotContain("alter ", sql);
        Assert.Contains("lots.auction_at <= @cutoff_at", historicalSql);
        Assert.Contains("group by lot_status, lot_sub_status", historicalSql);
        Assert.DoesNotContain("payload::text", historicalSql);
        Assert.DoesNotContain("delete ", historicalSql);
        Assert.DoesNotContain("update ", historicalSql);
        Assert.DoesNotContain("insert ", historicalSql);
        var diagnosticsSql = PostgresSnapshotStore.HistoricalRetentionDiagnosticsSql.ToLowerInvariant();
        Assert.Contains("count(*)::bigint from auction_lots", diagnosticsSql);
        Assert.Contains("lots.auction_at <= @cutoff_at", diagnosticsSql);
        Assert.DoesNotContain("delete ", diagnosticsSql);
        Assert.DoesNotContain("update ", diagnosticsSql);
        Assert.DoesNotContain("insert ", diagnosticsSql);

        var report = new SoldLotRetentionDryRunReport(
            7, DateTimeOffset.UtcNow, 0, null, null, null, null,
            Array.Empty<HistoricalLotStatusBucket>(), HistoricalDiagnostics: null, ReadOnly: true);
        Assert.Null(report.EligibleVersions);
        Assert.Null(report.ReferencedRawBlobsEligible);
        Assert.Null(report.PostgresPayloadBytesRecoverable);
        Assert.Null(report.EstimatedRawBlobBytesRecoverable);
        Assert.Null(report.HistoricalDiagnostics);
    }
}
