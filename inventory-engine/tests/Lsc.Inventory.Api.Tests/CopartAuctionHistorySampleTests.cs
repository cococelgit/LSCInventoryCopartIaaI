using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartAuctionHistorySampleTests
{
    [Fact]
    public void Sample_preserves_evidence_fields_without_raw_payload()
    {
        var sample = new CopartAuctionHistorySample(
            "copart:12345678",
            3,
            DateTimeOffset.Parse("2026-08-01T14:00:00Z"),
            DateTimeOffset.Parse("2026-08-29T14:00:00Z"),
            true,
            65,
            "high");

        Assert.Equal("copart:12345678", sample.LotKey);
        Assert.Equal(3, sample.AttemptCount);
        Assert.True(sample.HasSignal);
        Assert.Equal(65, sample.SignalScore);
        Assert.Equal("high", sample.SignalLevel);
    }
}
