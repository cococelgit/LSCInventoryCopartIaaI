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

    [Theory]
    [InlineData("copart:64206406", "copart-64206406")]
    [InlineData("iaai:12345678", "iaai-12345678")]
    [InlineData("copart-41621576", "copart-41621576")]
    public void SafeBlobIdentity_NormalizesLifecycleKeysToBlobPathConvention(string lotKey, string expected)
    {
        Assert.Equal(expected, PostgresSnapshotStore.ToSafeBlobIdentity(lotKey));
    }

    [Theory]
    [InlineData(false, false, false, true)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    public void RawSnapshotUpload_RecreatesOnlyPurgedSnapshotOnReactivation(
        bool versionExists, bool reactivatingInactiveLot, bool canonicalBlobExists, bool expected)
    {
        Assert.Equal(expected, PostgresSnapshotStore.ShouldUploadRawSnapshot(versionExists, reactivatingInactiveLot, canonicalBlobExists));
    }
}
