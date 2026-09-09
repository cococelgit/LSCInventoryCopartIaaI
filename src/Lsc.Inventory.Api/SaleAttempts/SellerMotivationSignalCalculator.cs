using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lsc.Inventory.Api.SaleAttempts;

public sealed class SellerMotivationSignalCalculator
{
    public SellerMotivationSignal Calculate(AuctionSaleHistorySnapshot snapshot, string policyVersion, DateTimeOffset calculatedAt)
    {
        if (string.IsNullOrWhiteSpace(policyVersion)) throw new ArgumentException("A policy version is required.", nameof(policyVersion));

        var attempts = snapshot.Attempts
            .OrderBy(item => item.SaleDate)
            .ThenBy(item => item.AttemptKey, StringComparer.Ordinal)
            .ToArray();
        var notSold = attempts.Where(item => item.Status == "not_sold").ToArray();
        var historicalMaxBid = notSold.Where(item => item.BidUsd > 0).Select(item => item.BidUsd).Max();
        var currentAsk = snapshot.Current.CurrentBuyNowUsd ?? snapshot.Current.SellerReserveUsd;
        decimal? askGap = currentAsk is > 0 && historicalMaxBid is > 0 ? currentAsk - historicalMaxBid : null;
        decimal? askGapPercent = askGap is not null && historicalMaxBid is > 0
            ? decimal.Round(askGap.Value / historicalMaxBid.Value * 100m, 2, MidpointRounding.AwayFromZero)
            : null;
        var historicalBuyNowMax = attempts.Where(item => item.BuyNowUsd > 0).Select(item => item.BuyNowUsd).Max();
        decimal? buyNowDropPercent = snapshot.Current.CurrentBuyNowUsd is > 0 && historicalBuyNowMax is > 0
            ? decimal.Round((historicalBuyNowMax.Value - snapshot.Current.CurrentBuyNowUsd.Value) / historicalBuyNowMax.Value * 100m, 2, MidpointRounding.AwayFromZero)
            : null;
        DateTimeOffset? firstAttemptAt = attempts.Length == 0 ? null : attempts[0].SaleDate;
        DateTimeOffset? lastAttemptAt = attempts.Length == 0 ? null : attempts[^1].SaleDate;
        DateTimeOffset? lastNotSoldAt = notSold.Length == 0 ? null : notSold.Max(item => item.SaleDate);
        var daysInCycle = firstAttemptAt is null ? 0 : Math.Max(0, (int)(calculatedAt.UtcDateTime.Date - firstAttemptAt.Value.UtcDateTime.Date).TotalDays);
        var reasons = new List<string>();

        string level;
        if (snapshot.HistoryAvailability == SaleAttemptHistoryAvailability.NotRequested)
        {
            level = SaleAttemptSignalLevels.None;
            reasons.Add("HISTORY_NOT_REQUESTED");
        }
        else if (snapshot.HistoryAvailability == SaleAttemptHistoryAvailability.Empty)
        {
            level = SaleAttemptSignalLevels.None;
            reasons.Add("HISTORY_EMPTY");
        }
        else if (snapshot.Current.Archived == true || snapshot.Current.Status == "sold")
        {
            level = SaleAttemptSignalLevels.None;
            reasons.Add(snapshot.Current.Archived == true ? "CURRENTLY_ARCHIVED" : "CURRENTLY_SOLD");
        }
        else if (notSold.Length >= 5 && currentAsk is > 0 && (askGapPercent is <= 10m || buyNowDropPercent is >= 10m))
        {
            level = SaleAttemptSignalLevels.High;
            reasons.Add("FIVE_PLUS_NOT_SOLD");
            if (askGapPercent is <= 0m) reasons.Add("ASK_AT_OR_BELOW_PRIOR_MAX_BID");
            else if (askGapPercent is <= 10m) reasons.Add("ASK_WITHIN_TEN_PERCENT_OF_PRIOR_MAX_BID");
            if (buyNowDropPercent is >= 10m) reasons.Add("BUY_NOW_DROPPED_TEN_PERCENT");
        }
        else if (notSold.Length >= 3 && currentAsk is > 0 && askGapPercent is <= 20m)
        {
            level = SaleAttemptSignalLevels.Medium;
            reasons.Add("THREE_PLUS_NOT_SOLD");
            reasons.Add("ASK_WITHIN_TWENTY_PERCENT_OF_PRIOR_MAX_BID");
        }
        else if (notSold.Length >= 2)
        {
            level = SaleAttemptSignalLevels.FollowUp;
            reasons.Add("MULTIPLE_NOT_SOLD");
            if (currentAsk is null) reasons.Add("CURRENT_ASK_MISSING");
            else if (historicalMaxBid is null) reasons.Add("HISTORICAL_BID_MISSING");
            else reasons.Add("ASK_GAP_ABOVE_THRESHOLD");
        }
        else
        {
            level = SaleAttemptSignalLevels.None;
            reasons.Add("INSUFFICIENT_NOT_SOLD_HISTORY");
        }

        var confidence = Confidence(snapshot.HistoryAvailability, notSold.Length, historicalMaxBid, currentAsk);
        var inputHash = InputHash(snapshot, policyVersion, calculatedAt);
        return new SellerMotivationSignal(
            snapshot.Platform,
            snapshot.LotKey,
            snapshot.LotNumber,
            snapshot.HistoryAvailability,
            attempts.Length,
            notSold.Length,
            historicalMaxBid,
            snapshot.Current.CurrentBuyNowUsd,
            snapshot.Current.SellerReserveUsd,
            currentAsk,
            askGap,
            askGapPercent,
            buyNowDropPercent,
            firstAttemptAt,
            lastAttemptAt,
            lastNotSoldAt,
            daysInCycle,
            level,
            confidence,
            reasons,
            policyVersion,
            inputHash,
            snapshot.SourceObservedAt,
            calculatedAt);
    }

    private static decimal Confidence(string availability, int notSoldCount, decimal? historicalMaxBid, decimal? currentAsk)
    {
        if (availability != SaleAttemptHistoryAvailability.Available) return 0m;
        var score = 40m;
        if (notSoldCount >= 2) score += 20m;
        if (notSoldCount >= 5) score += 10m;
        if (historicalMaxBid is > 0) score += 15m;
        if (currentAsk is > 0) score += 15m;
        return Math.Min(100m, score);
    }

    private static string InputHash(AuctionSaleHistorySnapshot snapshot, string policyVersion, DateTimeOffset calculatedAt)
    {
        var parts = new List<string>
        {
            policyVersion,
            snapshot.Platform,
            snapshot.LotKey,
            snapshot.HistoryAvailability,
            calculatedAt.UtcDateTime.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Money(snapshot.Current.CurrentBidUsd),
            Money(snapshot.Current.CurrentBuyNowUsd),
            Money(snapshot.Current.FinalBidUsd),
            Money(snapshot.Current.SellerReserveUsd),
            snapshot.Current.Status ?? "null",
            snapshot.Current.Archived?.ToString(CultureInfo.InvariantCulture) ?? "null"
        };
        foreach (var attempt in snapshot.Attempts.OrderBy(item => item.AttemptKey, StringComparer.Ordinal))
            parts.Add($"{attempt.AttemptKey}:{attempt.InputHash}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', parts)))).ToLowerInvariant();
    }

    private static string Money(decimal? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "null";
}

internal static class NullableDecimalSequenceExtensions
{
    public static decimal? Max(this IEnumerable<decimal?> values)
    {
        decimal? maximum = null;
        foreach (var value in values)
            if (value is not null && (maximum is null || value > maximum)) maximum = value;
        return maximum;
    }
}
