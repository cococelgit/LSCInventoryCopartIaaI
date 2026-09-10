using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class RetentionCandidateBlobInventoryTests
{
    [Fact]
    public void CandidateInventoryContract_IsMetadataOnly()
    {
        var report = new RetentionCandidateBlobInventoryReport(
            7, DateTimeOffset.UtcNow, 3, 8, 1024, 6, 900, 2, 124, 20, 2048,
            [new RetentionCandidateLotSummary("copart-123", 4, 512)], ReadOnly: true);

        Assert.True(report.ReadOnly);
        Assert.Equal(7, report.RetentionDays);
        Assert.Equal(8, report.MatchedPhysicalBlobs);
        Assert.Equal(1024, report.MatchedPhysicalBytes);
        Assert.Single(report.TopLots);
    }
}
