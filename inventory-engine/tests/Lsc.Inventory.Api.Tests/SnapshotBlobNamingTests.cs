using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class SnapshotBlobNamingTests
{
    [Fact]
    public void BuildBlobName_UsesOnlyLotIdentityAndCompletePayloadHash()
    {
        const string payloadHash = "a3a6267d7c0f9d4ff2fb00cc46ce8fa638e2d9f5a80539b051f5c7407fad188e";

        var firstObservation = PostgresSnapshotStore.BuildBlobName("copart:63138896", payloadHash);
        var repeatedObservation = PostgresSnapshotStore.BuildBlobName("copart:63138896", payloadHash);

        Assert.Equal("snapshots/copart-63138896/a3a6267d7c0f9d4ff2fb00cc46ce8fa638e2d9f5a80539b051f5c7407fad188e.json", firstObservation);
        Assert.Equal(firstObservation, repeatedObservation);
        Assert.DoesNotContain("/2026/", firstObservation, StringComparison.Ordinal);
    }
}
