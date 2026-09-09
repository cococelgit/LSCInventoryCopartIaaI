using System.Text.Json;
using Lsc.Inventory.Api.Services;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class LiveCopartAuctionHistoryLookupTests
{
    [Fact]
    public void Scores_five_source_observed_not_sold_attempts_even_when_some_prices_are_missing()
    {
        using var document = JsonDocument.Parse("""
            { "lots": [{ "lot": "45924706", "prices": [
              { "id": 1, "sale_date": "2026-08-06T19:00:00Z", "bid": null, "buy_now_price": 8200, "status": { "name": "not_sold" } },
              { "id": 2, "sale_date": "2026-08-13T19:00:00Z", "bid": 35, "buy_now_price": 7700, "status": { "name": "not_sold" } },
              { "id": 3, "sale_date": "2026-08-20T19:00:00Z", "bid": null, "buy_now_price": 7700, "status": { "name": "not_sold" } },
              { "id": 4, "sale_date": "2026-08-27T19:00:00Z", "bid": 4850, "buy_now_price": 7300, "status": { "name": "not_sold" } },
              { "id": 5, "sale_date": "2026-09-03T19:00:00Z", "bid": null, "buy_now_price": 6700, "status": { "name": "not_sold" } }
            ]}] }
            """);

        var result = LiveCopartAuctionHistoryParser.Parse("45924706", document.RootElement, new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));

        Assert.NotNull(result);
        Assert.Equal("auctionsapi_live", result.Source);
        Assert.Equal(5, result.Signal.AttemptCount);
        Assert.Equal(5, result.Signal.NotSoldObservedCount);
        Assert.Equal(5, result.Signal.RelistedInferredCount);
        Assert.Equal("high", result.Signal.Level);
        Assert.All(result.Attempts, attempt => Assert.Equal("not_sold_observed", attempt.Outcome));
        Assert.Equal(4850m, result.Signal.HistoricalMaximumBidUsd);
        Assert.Null(result.Attempts[0].LastBidUsd);
    }

    [Fact]
    public void Does_not_score_a_lot_as_active_motivation_after_a_confirmed_sale()
    {
        using var document = JsonDocument.Parse("""
            { "lots": [{ "lot": "45924706", "prices": [
              { "id": 1, "sale_date": "2026-08-06T19:00:00Z", "bid": 5000, "status": { "name": "not_sold" } },
              { "id": 2, "sale_date": "2026-08-13T19:00:00Z", "bid": 6000, "status": { "name": "sold" } }
            ]}] }
            """);

        var result = LiveCopartAuctionHistoryParser.Parse("45924706", document.RootElement, new DateTimeOffset(2026, 9, 9, 0, 0, 0, TimeSpan.Zero));

        Assert.NotNull(result);
        Assert.Equal("none", result.Signal.Level);
        Assert.Equal(0, result.Signal.Score);
        Assert.Contains(result.Attempts, attempt => attempt.Outcome == "sold_confirmed");
    }

    [Theory]
    [InlineData("45924706", true)]
    [InlineData("4592-A", false)]
    [InlineData("", false)]
    [InlineData("123", false)]
    public void Accepts_only_bounded_numeric_copart_lot_numbers(string lot, bool expected)
    {
        Assert.Equal(expected, LiveCopartAuctionHistoryParser.IsSupportedLot(lot));
    }
}
