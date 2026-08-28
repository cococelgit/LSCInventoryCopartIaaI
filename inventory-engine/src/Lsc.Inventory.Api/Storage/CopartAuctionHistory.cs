using Lsc.Inventory.Api.Contracts;

namespace Lsc.Inventory.Api.Storage;

/// <summary>
/// Immutable evidence that a Copart lot was present in one complete Excel snapshot.
/// It does not assert a sale result.
/// </summary>
public sealed record CopartAuctionObservation(
    string SnapshotSha256,
    DateTimeOffset SnapshotDownloadedAt,
    string LotKey,
    string LotNumber,
    DateTimeOffset? AuctionAt,
    decimal? CurrentBidUsd,
    decimal? BuyNowUsd,
    decimal? SalePriceUsd,
    string? LotStatus,
    string? LotSubStatus,
    string PayloadHash);

public sealed record CopartAuctionAttempt(
    string LotKey,
    int AttemptNumber,
    DateTimeOffset AuctionAt,
    DateTimeOffset FirstObservedAt,
    DateTimeOffset LastObservedAt,
    decimal? FirstBidUsd,
    decimal? LastBidUsd,
    decimal? MaximumBidUsd,
    decimal? BuyNowUsd,
    decimal? SalePriceUsd,
    string Outcome,
    string EvidenceLevel,
    string? OutcomeEvidence,
    int ObservationCount);

public sealed record CopartMotivationSignal(
    string LotKey,
    int AttemptCount,
    int RelistedInferredCount,
    int Score,
    string Level,
    IReadOnlyList<string> Reasons,
    DateTimeOffset? FirstAttemptAt,
    DateTimeOffset? LastAttemptAt,
    decimal? LastBidUsd,
    decimal? HistoricalMaximumBidUsd);

public static class CopartMotivationScorer
{
    public static CopartMotivationSignal Score(string lotKey, IReadOnlyList<CopartAuctionAttempt> attempts, DateTimeOffset asOf)
    {
        var ordered = attempts.OrderBy(attempt => attempt.AuctionAt).ToArray();
        if (ordered.Length == 0)
            return new CopartMotivationSignal(lotKey, 0, 0, 0, "none", [], null, null, null, null);

        var relisted = ordered.Count(attempt => string.Equals(attempt.Outcome, "relisted_inferred", StringComparison.OrdinalIgnoreCase));
        var sold = ordered.Any(attempt => string.Equals(attempt.Outcome, "sold_confirmed", StringComparison.OrdinalIgnoreCase));
        var score = 0;
        var reasons = new List<string>();

        // No commercial score is emitted from age, bid movement or disappearance alone.
        // A re-listing inference from a later complete snapshot is the minimum evidence threshold.
        var hasRelistingEvidence = !sold && relisted > 0;
        if (hasRelistingEvidence)
        {
            var points = Math.Min(relisted, 3) * 25;
            score += points;
            reasons.Add($"{relisted} re-listing(s) inferred after a prior auction date passed (+{points}).");
        }

        if (hasRelistingEvidence && ordered.Length >= 3)
        {
            score += 20;
            reasons.Add("Three or more distinct auction attempts (+20).");
        }

        if (hasRelistingEvidence && (asOf - ordered[0].AuctionAt).TotalDays >= 14)
        {
            score += 15;
            reasons.Add("First observed auction attempt is at least 14 days old (+15).");
        }

        var bids = ordered.Where(attempt => attempt.MaximumBidUsd is not null).Select(attempt => attempt.MaximumBidUsd!.Value).ToArray();
        var lastBid = ordered[^1].LastBidUsd ?? ordered[^1].MaximumBidUsd;
        var historicalMaximum = bids.Length == 0 ? (decimal?)null : bids.Max();
        if (hasRelistingEvidence && lastBid is not null && historicalMaximum is not null && lastBid < historicalMaximum)
        {
            score += 15;
            reasons.Add("Latest bid is below the historical maximum bid (+15).");
        }

        if (hasRelistingEvidence && bids.Length >= 2)
        {
            var min = bids.Min();
            var max = bids.Max();
            if (max > 0 && (max - min) / max <= 0.02m)
            {
                score += 10;
                reasons.Add("Bidding remained within a 2% range across attempts (+10).");
            }
        }

        if (sold)
        {
            score = 0;
            reasons.Clear();
            reasons.Add("A sale is confirmed by the source; this lot is not scored as an active opportunity.");
        }

        var level = score switch
        {
            >= 60 => "high",
            >= 35 => "medium",
            > 0 => "watch",
            _ => "none"
        };

        return new CopartMotivationSignal(
            lotKey,
            ordered.Length,
            relisted,
            score,
            level,
            reasons,
            ordered[0].AuctionAt,
            ordered[^1].AuctionAt,
            lastBid,
            historicalMaximum);
    }
}

public static class CopartAuctionObservationFactory
{
    public static CopartAuctionObservation? Create(AuctionVehicle vehicle, string snapshotSha256, DateTimeOffset snapshotDownloadedAt)
    {
        if (!string.Equals(vehicle.Platform, "copart", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(vehicle.LotNumber))
            return null;

        var payloadHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes($"{vehicle.LotNumber}|{vehicle.Auction?.AuctionAt:O}|{vehicle.Pricing?.CurrentBidUsd}|{vehicle.Pricing?.BuyNowUsd}|{vehicle.Pricing?.SalePriceUsd}|{vehicle.Auction?.LotStatus}|{vehicle.Auction?.LotSubStatus}"))).ToLowerInvariant();

        return new CopartAuctionObservation(
            snapshotSha256,
            snapshotDownloadedAt,
            $"copart:{vehicle.LotNumber}",
            vehicle.LotNumber,
            vehicle.Auction?.AuctionAt,
            vehicle.Pricing?.CurrentBidUsd,
            vehicle.Pricing?.BuyNowUsd,
            vehicle.Pricing?.SalePriceUsd,
            vehicle.Auction?.LotStatus,
            vehicle.Auction?.LotSubStatus,
            payloadHash);
    }
}
