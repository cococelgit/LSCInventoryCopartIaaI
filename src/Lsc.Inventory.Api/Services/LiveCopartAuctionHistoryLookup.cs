using System.Globalization;
using System.Text.Json;
using Lsc.Inventory.Api.Options;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Services;

/// <summary>
/// Reads the current AuctionsAPI history for exactly one Copart lot. This service is
/// intentionally read-only: it never persists a snapshot, changes lifecycle state,
/// modifies scoring, or participates in search/facet requests.
/// </summary>
public interface ILiveCopartAuctionHistoryLookup
{
    Task<LiveCopartMotivationHistory?> GetAsync(string lot, CancellationToken cancellationToken);
}

public sealed record LiveCopartMotivationAttempt(
    int AttemptNumber,
    DateTimeOffset? AuctionAt,
    string Outcome,
    string EvidenceLevel,
    decimal? LastBidUsd,
    decimal? HistoricalMaximumBidUsd,
    decimal? BuyNowUsd);

public sealed record LiveCopartMotivationSignal(
    string Level,
    int Score,
    int AttemptCount,
    int RelistedInferredCount,
    int NotSoldObservedCount,
    DateTimeOffset? FirstAttemptAt,
    DateTimeOffset? LastAttemptAt,
    decimal? LastBidUsd,
    decimal? HistoricalMaximumBidUsd);

public sealed record LiveCopartMotivationHistory(
    string LotKey,
    string Source,
    DateTimeOffset FetchedAt,
    LiveCopartMotivationSignal Signal,
    IReadOnlyList<LiveCopartMotivationAttempt> Attempts);

public sealed class LiveCopartAuctionHistoryLookup(
    IAuctionsApiClient auctionsApiClient,
    IOptions<AuctionsApiOptions> auctionsApiOptions,
    IMemoryCache cache,
    ILogger<LiveCopartAuctionHistoryLookup> logger) : ILiveCopartAuctionHistoryLookup
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ProviderBudget = TimeSpan.FromSeconds(12);
    private readonly AuctionsApiOptions _options = auctionsApiOptions.Value;

    public async Task<LiveCopartMotivationHistory?> GetAsync(string lot, CancellationToken cancellationToken)
    {
        if (!LiveCopartAuctionHistoryParser.IsSupportedLot(lot) || !_options.IsConfigured) return null;

        var normalizedLot = lot.Trim();
        var cacheKey = $"motivated-seller:live:copart:{normalizedLot}";
        if (cache.TryGetValue<LiveCopartMotivationHistory>(cacheKey, out var cached)) return cached;

        try
        {
            using var providerTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            providerTimeout.CancelAfter(ProviderBudget);
            var page = await auctionsApiClient.GetLotAsync(
                normalizedLot,
                domainId: 3,
                searchById: false,
                includePricesHistory: true,
                providerTimeout.Token);
            var history = LiveCopartAuctionHistoryParser.Parse(normalizedLot, page.Data, DateTimeOffset.UtcNow);
            if (history is null) return null;
            cache.Set(cacheKey, history, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration,
                Size = 1
            });
            return history;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Live Copart motivation lookup exceeded its provider budget. Lot={LotNumber}", normalizedLot);
            return null;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "Live Copart motivation lookup was unavailable. Lot={LotNumber}", normalizedLot);
            return null;
        }
    }
}

internal static class LiveCopartAuctionHistoryParser
{
    internal const string SourceObserved = "source_observed";
    internal const string NotSoldObserved = "not_sold_observed";
    internal const string SoldConfirmed = "sold_confirmed";
    internal const string Scheduled = "scheduled";
    internal const string Unknown = "unknown";

    internal static bool IsSupportedLot(string? lot) => !string.IsNullOrWhiteSpace(lot)
        && lot.Trim().Length is >= 4 and <= 32
        && lot.Trim().All(char.IsDigit);

    internal static LiveCopartMotivationHistory? Parse(string requestedLot, JsonElement data, DateTimeOffset fetchedAt)
    {
        if (!IsSupportedLot(requestedLot) || !TryFindLot(data, requestedLot.Trim(), out var lot)) return null;
        if (!TryReadAttempts(lot, out var records) || records.Count == 0) return null;

        var ordered = records
            .OrderBy(record => record.AuctionAt ?? DateTimeOffset.MaxValue)
            .ThenBy(record => record.ProviderId, StringComparer.Ordinal)
            .ToArray();
        var historicalMaximum = (decimal?)null;
        var attempts = new List<LiveCopartMotivationAttempt>(ordered.Length);
        foreach (var record in ordered)
        {
            if (record.BidUsd is { } bid && (historicalMaximum is null || bid > historicalMaximum)) historicalMaximum = bid;
            attempts.Add(new LiveCopartMotivationAttempt(
                attempts.Count + 1,
                record.AuctionAt,
                ToOutcome(record.Status),
                SourceObserved,
                record.BidUsd,
                historicalMaximum,
                record.BuyNowUsd));
        }

        var notSold = attempts.Count(attempt => attempt.Outcome == NotSoldObserved);
        var hasConfirmedSale = attempts.Any(attempt => attempt.Outcome == SoldConfirmed);
        var first = attempts.FirstOrDefault()?.AuctionAt;
        var last = attempts.LastOrDefault()?.AuctionAt;
        var ageDays = first is { } firstAttempt ? (fetchedAt - firstAttempt).TotalDays : 0;
        var score = hasConfirmedSale ? 0 : Math.Min(notSold, 3) * 25;
        if (!hasConfirmedSale && attempts.Count >= 3) score += 20;
        if (!hasConfirmedSale && notSold > 0 && ageDays >= 14) score += 15;
        var level = score switch
        {
            >= 60 => "high",
            >= 35 => "medium",
            > 0 => "watch",
            _ => "none"
        };
        var latestBid = attempts.LastOrDefault(attempt => attempt.LastBidUsd is not null)?.LastBidUsd;
        return new LiveCopartMotivationHistory(
            $"copart:{requestedLot.Trim()}",
            "auctionsapi_live",
            fetchedAt,
            new LiveCopartMotivationSignal(
                level,
                score,
                attempts.Count,
                notSold,
                notSold,
                first,
                last,
                latestBid,
                historicalMaximum),
            attempts);
    }

    private sealed record RawAttempt(string ProviderId, DateTimeOffset? AuctionAt, string? Status, decimal? BidUsd, decimal? BuyNowUsd);

    private static bool TryFindLot(JsonElement data, string requestedLot, out JsonElement lot)
    {
        lot = default;
        if (data.ValueKind != JsonValueKind.Object) return false;
        if (data.TryGetProperty("lots", out var directLots) && directLots.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in directLots.EnumerateArray())
            {
                if (MatchesLot(candidate, requestedLot))
                {
                    lot = candidate;
                    return true;
                }
            }
        }
        if (MatchesLot(data, requestedLot))
        {
            lot = data;
            return true;
        }
        return false;
    }

    private static bool MatchesLot(JsonElement lot, string requestedLot)
    {
        foreach (var property in new[] { "lot", "lot_number", "external_id" })
        {
            if (Scalar(lot, property) is { } value && string.Equals(value, requestedLot, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static bool TryReadAttempts(JsonElement lot, out List<RawAttempt> attempts)
    {
        attempts = [];
        JsonElement records = default;
        var found = lot.TryGetProperty("prices", out records) && records.ValueKind == JsonValueKind.Array;
        if (!found) found = lot.TryGetProperty("prices_history", out records) && records.ValueKind == JsonValueKind.Array;
        if (!found) return false;

        var duplicates = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in records.EnumerateArray())
        {
            if (record.ValueKind != JsonValueKind.Object) continue;
            var id = Scalar(record, "id") ?? $"{Scalar(record, "sale_date")}|{Scalar(record, "status.name", "status")}|{Scalar(record, "bid")}|{Scalar(record, "buy_now_price", "buy_now")}";
            if (!duplicates.Add(id)) continue;
            attempts.Add(new RawAttempt(
                id,
                ParseDate(Scalar(record, "sale_date", "auction_at")),
                Scalar(record, "status.name", "status"),
                ParseMoney(Scalar(record, "bid", "final_bid")),
                ParseMoney(Scalar(record, "buy_now_price", "buy_now"))));
        }
        return true;
    }

    private static string ToOutcome(string? status)
    {
        var normalized = (status ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        if (normalized.Contains("not_sold", StringComparison.Ordinal) || normalized.Contains("unsold", StringComparison.Ordinal) || normalized.Contains("no_sale", StringComparison.Ordinal)) return NotSoldObserved;
        if (normalized.Contains("sold", StringComparison.Ordinal)) return SoldConfirmed;
        if (normalized.Contains("schedule", StringComparison.Ordinal) || normalized.Contains("upcoming", StringComparison.Ordinal)) return Scheduled;
        return Unknown;
    }

    private static string? Scalar(JsonElement element, params string[] paths)
    {
        foreach (var path in paths)
        {
            var current = element;
            var valid = true;
            foreach (var segment in path.Split('.'))
            {
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                {
                    valid = false;
                    break;
                }
            }
            if (!valid) continue;
            if (current.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(current.GetString())) return current.GetString();
            if (current.ValueKind == JsonValueKind.Number) return current.ToString();
        }
        return null;
    }

    private static DateTimeOffset? ParseDate(string? value) => DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;

    private static decimal? ParseMoney(string? value)
    {
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0) return null;
        return parsed;
    }
}
