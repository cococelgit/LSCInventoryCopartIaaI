using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartAuctionHistoryTests
{
    [Fact]
    public void Scores_relisted_lot_with_three_attempts_as_high_when_bids_stall_and_decline()
    {
        var first = new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);
        var attempts = new[]
        {
            Attempt(1, first, 10_000m, 10_000m, "relisted_inferred"),
            Attempt(2, first.AddDays(2), 11_000m, 11_000m, "relisted_inferred"),
            Attempt(3, first.AddDays(4), 10_500m, 11_000m, "scheduled")
        };

        var signal = CopartMotivationScorer.Score("copart:48826366", attempts, first.AddDays(20));

        Assert.Equal(3, signal.AttemptCount);
        Assert.Equal(2, signal.RelistedInferredCount);
        Assert.Equal("high", signal.Level);
        Assert.True(signal.Score >= 60);
        Assert.Contains(signal.Reasons, reason => reason.Contains("re-listing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Does_not_score_a_lot_with_confirmed_sale()
    {
        var auctionAt = new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);
        var signal = CopartMotivationScorer.Score("copart:1", [Attempt(1, auctionAt, 10_000m, 10_000m, "sold_confirmed")], auctionAt.AddDays(30));

        Assert.Equal(0, signal.Score);
        Assert.Equal("none", signal.Level);
        Assert.Contains(signal.Reasons, reason => reason.Contains("sale is confirmed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Does_not_treat_an_unknown_outcome_as_a_relist()
    {
        var auctionAt = new DateTimeOffset(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);
        var signal = CopartMotivationScorer.Score("copart:1", [Attempt(1, auctionAt, 10_000m, 10_000m, "unknown")], auctionAt.AddDays(30));

        Assert.Equal(0, signal.RelistedInferredCount);
        Assert.Equal(0, signal.Score);
        Assert.Equal("none", signal.Level);
    }

    private static CopartAuctionAttempt Attempt(int number, DateTimeOffset auctionAt, decimal lastBid, decimal maximumBid, string outcome) =>
        new("copart:48826366", number, auctionAt, auctionAt.AddHours(-1), auctionAt, lastBid, lastBid, maximumBid, null, outcome == "sold_confirmed" ? lastBid : null, outcome,
            outcome == "relisted_inferred" ? "inferred_from_reappearance" : "source_observed", null, 1);
}
