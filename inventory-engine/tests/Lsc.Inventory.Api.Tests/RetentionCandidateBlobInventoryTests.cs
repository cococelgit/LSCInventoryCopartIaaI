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

    [Fact]
    public void PilotManifest_OnlyAcceptsLegacyBlobForTheSelectedLot()
    {
        var selected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["copart-41621576"] = "copart:41621576"
        };

        Assert.True(PostgresSnapshotStore.TryGetPilotLegacyBlobLotKey(
            "snapshots/2026/08/25/copart-41621576/153814322-6f0b9e4203a4.json", selected, out var lotKey));
        Assert.Equal("copart:41621576", lotKey);
        Assert.False(PostgresSnapshotStore.TryGetPilotLegacyBlobLotKey(
            "snapshots/2026/08/25/copart-41623946/153814322-73d9189de460.json", selected, out _));
        Assert.False(PostgresSnapshotStore.TryGetPilotLegacyBlobLotKey(
            "snapshots/copart-41621576/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef.json", selected, out _));
    }

    [Fact]
    public void PilotPurge_ExcludesAnyLotReactivatedAfterManifestCreation()
    {
        var blobs = new[]
        {
            new RetentionPurgePilotBlob("copart:1", "snapshots/2026/08/25/copart-1/a.json", 10, DateTimeOffset.UnixEpoch),
            new RetentionPurgePilotBlob("copart:2", "snapshots/2026/08/25/copart-2/b.json", 20, DateTimeOffset.UnixEpoch)
        };
        var stillInactive = new HashSet<string>(StringComparer.Ordinal) { "copart:1" };

        var eligible = PostgresSnapshotStore.GetPilotBlobsForEligibleLots(blobs, stillInactive);

        Assert.Single(eligible);
        Assert.Equal("copart:1", eligible[0].LotKey);
    }
}
