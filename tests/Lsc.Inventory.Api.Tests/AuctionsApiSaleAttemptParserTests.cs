using System.Text.Json;
using Lsc.Inventory.Api.SaleAttempts;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionsApiSaleAttemptParserTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-09-08T14:00:00Z");

    [Theory]
    [InlineData("54450906", "copart", 26, 26, "49750", "47362")]
    [InlineData("53371186", "copart", 19, 19, "2800", "2000")]
    [InlineData("45785276", "iaai", 5, 5, "1500", "1500")]
    [InlineData("37887931", "iaai", 4, 4, "2150", null)]
    public void Parses_confirmed_price_histories(
        string lot,
        string platform,
        int expectedAttempts,
        int expectedNotSold,
        string expectedMaxBid,
        string? expectedCurrentBuyNow)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath($"{lot}.json")));

        var result = AuctionsApiSaleAttemptParser.Parse(document.RootElement, platform, ObservedAt);

        var snapshot = Assert.Single(result.Snapshots);
        Assert.Equal($"{platform}:{lot}", snapshot.LotKey);
        Assert.Equal(SaleAttemptHistoryAvailability.Available, snapshot.HistoryAvailability);
        Assert.Equal(expectedAttempts, snapshot.Attempts.Count);
        Assert.Equal(expectedNotSold, snapshot.Attempts.Count(item => item.Status == "not_sold"));
        Assert.Equal(decimal.Parse(expectedMaxBid, System.Globalization.CultureInfo.InvariantCulture), snapshot.Attempts.Where(item => item.Status == "not_sold").Max(item => item.BidUsd));
        Assert.Equal(expectedCurrentBuyNow is null ? null : decimal.Parse(expectedCurrentBuyNow, System.Globalization.CultureInfo.InvariantCulture), snapshot.Current.CurrentBuyNowUsd);
        Assert.Equal(expectedAttempts, snapshot.Attempts.Select(item => item.AttemptKey).Distinct(StringComparer.Ordinal).Count());
        Assert.All(snapshot.Attempts, item => Assert.StartsWith($"{platform}:price:", item.AttemptKey, StringComparison.Ordinal));
    }

    [Fact]
    public void Distinguishes_history_not_requested_from_an_empty_history()
    {
        using var notRequested = JsonDocument.Parse("""
            {"data":{"lots":[{"lot":"100","domain":{"id":1},"buy_now":1200}]}}
            """);
        using var empty = JsonDocument.Parse("""
            {"data":{"lots":[{"lot":"101","domain":{"id":1},"buy_now":1200,"prices":[]}]}}
            """);

        var notRequestedResult = AuctionsApiSaleAttemptParser.Parse(notRequested.RootElement, "iaai", ObservedAt);
        var emptyResult = AuctionsApiSaleAttemptParser.Parse(empty.RootElement, "iaai", ObservedAt);

        Assert.Equal(SaleAttemptHistoryAvailability.NotRequested, Assert.Single(notRequestedResult.Snapshots).HistoryAvailability);
        Assert.Equal(SaleAttemptHistoryAvailability.Empty, Assert.Single(emptyResult.Snapshots).HistoryAvailability);
    }

    [Fact]
    public void Skips_cross_platform_lots_missing_dates_and_duplicate_provider_attempts()
    {
        using var document = JsonDocument.Parse("""
            {
              "data": [
                {"lots":[{"lot":"200","domain":{"id":3},"prices":[
                  {"id":7,"sale_date":"2026-08-01T10:00:00Z","status":{"name":"NOT SOLD"},"bid":"900"},
                  {"id":7,"sale_date":"2026-08-01T10:00:00Z","status":{"name":"not_sold"},"bid":900},
                  {"id":8,"sale_date":null,"status":{"name":"not_sold"},"bid":1000}
                ]}]},
                {"lots":[{"lot":"201","domain":{"id":1},"prices":[]}]}
              ]
            }
            """);

        var result = AuctionsApiSaleAttemptParser.Parse(document.RootElement, "copart", ObservedAt);

        var snapshot = Assert.Single(result.Snapshots);
        var attempt = Assert.Single(snapshot.Attempts);
        Assert.Equal("not_sold", attempt.Status);
        Assert.Equal(900, attempt.BidUsd);
        Assert.Equal(2, result.Diagnostics.LotsExamined);
        Assert.Equal(1, result.Diagnostics.CrossPlatformSkipped);
        Assert.Equal(1, result.Diagnostics.MissingSaleDateSkipped);
        Assert.Equal(1, result.Diagnostics.DuplicateAttemptSkipped);
    }

    [Fact]
    public void Builds_a_stable_fallback_key_when_provider_attempt_id_is_missing()
    {
        using var document = JsonDocument.Parse("""
            {"data":{"lots":[{"lot":"300","domain":{"id":3},"prices":[
              {"sale_date":"2026-08-01T10:00:00Z","status":"not-sold","bid":"1,250","buy_now_price":1500}
            ]}]}}
            """);

        var first = AuctionsApiSaleAttemptParser.Parse(document.RootElement, "copart", ObservedAt);
        var second = AuctionsApiSaleAttemptParser.Parse(document.RootElement, "copart", ObservedAt.AddMinutes(5));

        Assert.Equal(Assert.Single(first.Snapshots).Attempts.Single().AttemptKey, Assert.Single(second.Snapshots).Attempts.Single().AttemptKey);
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SaleAttempts", name);
}
