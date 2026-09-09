using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Lsc.Inventory.Api.SaleAttempts;

public static class AuctionsApiSaleAttemptParser
{
    public static SaleAttemptParseResult Parse(JsonElement envelope, string platform, DateTimeOffset sourceObservedAt)
    {
        var normalizedPlatform = NormalizePlatform(platform);
        var expectedDomain = normalizedPlatform == "iaai" ? 1 : 3;
        var root = envelope.ValueKind == JsonValueKind.Object && envelope.TryGetProperty("data", out var data)
            ? data
            : envelope;
        var snapshots = new List<AuctionSaleHistorySnapshot>();
        var lotsExamined = 0;
        var attemptsExamined = 0;
        var attemptsAccepted = 0;
        var missingLot = 0;
        var crossPlatform = 0;
        var missingSaleDate = 0;
        var duplicates = 0;

        foreach (var (vehicle, lot) in ExtractLots(root))
        {
            lotsExamined++;
            var domain = Integer(lot, "domain.id", "domain_id") ?? Integer(vehicle, "domain.id", "domain_id");
            if (domain is not null && domain != expectedDomain)
            {
                crossPlatform++;
                continue;
            }

            var lotNumber = Scalar(lot, "lot", "lot_number", "external_id")?.Trim();
            if (string.IsNullOrWhiteSpace(lotNumber))
            {
                missingLot++;
                continue;
            }

            var providerVehicleId = Scalar(vehicle, "id");
            var providerLotId = Scalar(lot, "id");
            var lotKey = $"{normalizedPlatform}:{lotNumber}";
            JsonElement prices = default;
            var hasPricesProperty = lot.ValueKind == JsonValueKind.Object && lot.TryGetProperty("prices", out prices);
            var historyAvailability = !hasPricesProperty
                ? SaleAttemptHistoryAvailability.NotRequested
                : prices.ValueKind == JsonValueKind.Array && prices.GetArrayLength() > 0
                    ? SaleAttemptHistoryAvailability.Available
                    : SaleAttemptHistoryAvailability.Empty;
            var attemptsByKey = new Dictionary<string, AuctionSaleAttempt>(StringComparer.Ordinal);

            if (hasPricesProperty && prices.ValueKind == JsonValueKind.Array)
            {
                foreach (var price in prices.EnumerateArray())
                {
                    attemptsExamined++;
                    var saleDate = Timestamp(price, "sale_date");
                    if (saleDate is null)
                    {
                        missingSaleDate++;
                        continue;
                    }

                    var providerAttemptId = Scalar(price, "id", "price_id");
                    var status = NormalizeStatus(Scalar(price, "status.name", "status"));
                    var bid = PositiveMoney(price, "bid", "final_bid");
                    var buyNow = PositiveMoney(price, "buy_now_price", "buy_now");
                    var finalBidUpdatedAt = Timestamp(price, "final_bid_updated_at", "bid_updated_at");
                    var attemptKey = BuildAttemptKey(normalizedPlatform, lotKey, providerAttemptId, saleDate.Value, status, bid, buyNow);
                    var inputHash = Hash(string.Join('|', normalizedPlatform, lotKey, providerAttemptId, saleDate.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), status, Money(bid), Money(buyNow), finalBidUpdatedAt?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));
                    var attempt = new AuctionSaleAttempt(
                        normalizedPlatform,
                        lotKey,
                        lotNumber,
                        providerVehicleId,
                        providerLotId,
                        providerAttemptId,
                        attemptKey,
                        saleDate.Value,
                        status,
                        Integer(price, "status.id", "status_id"),
                        bid,
                        buyNow,
                        finalBidUpdatedAt,
                        sourceObservedAt,
                        inputHash);

                    if (attemptsByKey.TryGetValue(attemptKey, out var existing))
                    {
                        duplicates++;
                        if ((attempt.FinalBidUpdatedAt ?? attempt.SourceObservedAt) > (existing.FinalBidUpdatedAt ?? existing.SourceObservedAt))
                            attemptsByKey[attemptKey] = attempt;
                        continue;
                    }

                    attemptsByKey.Add(attemptKey, attempt);
                    attemptsAccepted++;
                }
            }

            var current = new CurrentAuctionPriceState(
                PositiveMoney(lot, "bid"),
                PositiveMoney(lot, "buy_now"),
                PositiveMoney(lot, "final_bid"),
                PositiveMoney(lot, "seller_reserve"),
                Scalar(lot, "auction_type.name", "auction_type"),
                NormalizeNullableStatus(Scalar(lot, "status.name", "status")),
                Timestamp(lot, "sale_date"),
                Timestamp(lot, "final_bid_updated_at", "bid_updated_at"),
                Boolean(lot, "archived", "is_archived"));
            snapshots.Add(new AuctionSaleHistorySnapshot(
                normalizedPlatform,
                lotKey,
                lotNumber,
                providerVehicleId,
                providerLotId,
                historyAvailability,
                current,
                attemptsByKey.Values.OrderBy(item => item.SaleDate).ThenBy(item => item.AttemptKey, StringComparer.Ordinal).ToArray(),
                sourceObservedAt));
        }

        return new SaleAttemptParseResult(
            snapshots,
            new SaleAttemptParseDiagnostics(lotsExamined, snapshots.Count, attemptsExamined, attemptsAccepted, missingLot, crossPlatform, missingSaleDate, duplicates));
    }

    private static IEnumerable<(JsonElement Vehicle, JsonElement Lot)> ExtractLots(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in root.EnumerateArray())
                foreach (var item in ExtractLots(row))
                    yield return item;
            yield break;
        }

        if (root.ValueKind != JsonValueKind.Object) yield break;
        if (root.TryGetProperty("lots", out var lots) && lots.ValueKind == JsonValueKind.Array)
        {
            foreach (var lot in lots.EnumerateArray()) yield return (root, lot);
            yield break;
        }

        foreach (var key in new[] { "cars", "results", "data" })
        {
            if (!root.TryGetProperty(key, out var nested) || nested.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in nested.EnumerateArray())
                foreach (var item in ExtractLots(row))
                    yield return item;
            yield break;
        }

        yield return (root, root);
    }

    private static string NormalizePlatform(string platform) => platform.Trim().ToLowerInvariant() switch
    {
        "iaai" => "iaai",
        "copart" => "copart",
        _ => throw new ArgumentOutOfRangeException(nameof(platform), "Only IAAI and Copart are supported.")
    };

    private static string NormalizeStatus(string? status) => string.IsNullOrWhiteSpace(status)
        ? "unknown"
        : status.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    private static string? NormalizeNullableStatus(string? status) => string.IsNullOrWhiteSpace(status) ? null : NormalizeStatus(status);

    private static string BuildAttemptKey(string platform, string lotKey, string? providerAttemptId, DateTimeOffset saleDate, string status, decimal? bid, decimal? buyNow)
    {
        if (!string.IsNullOrWhiteSpace(providerAttemptId)) return $"{platform}:price:{providerAttemptId.Trim()}";
        var fingerprint = string.Join('|', platform, lotKey, saleDate.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), status, Money(bid), Money(buyNow));
        return $"{platform}:fallback:{Hash(fingerprint)}";
    }

    private static string Money(decimal? value) => value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "null";

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static JsonElement? At(JsonElement value, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return null;
        }
        return value;
    }

    private static string? Scalar(JsonElement value, params string[] paths)
    {
        foreach (var path in paths)
        {
            var found = At(value, path);
            if (found is null) continue;
            var item = found.Value;
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())) return item.GetString();
            if (item.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return item.ToString();
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) return name.GetString();
        }
        return null;
    }

    private static int? Integer(JsonElement value, params string[] paths) => int.TryParse(Scalar(value, paths), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static decimal? PositiveMoney(JsonElement value, params string[] paths)
    {
        foreach (var path in paths)
        {
            var raw = Scalar(value, path);
            if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed > 0) return parsed;
        }
        return null;
    }

    private static DateTimeOffset? Timestamp(JsonElement value, params string[] paths)
    {
        foreach (var path in paths)
        {
            var raw = Scalar(value, path);
            if (DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)) return parsed;
        }
        return null;
    }

    private static bool? Boolean(JsonElement value, params string[] paths)
    {
        foreach (var path in paths)
        {
            var raw = Scalar(value, path);
            if (bool.TryParse(raw, out var parsed)) return parsed;
            if (string.Equals(raw, "1", StringComparison.Ordinal)) return true;
            if (string.Equals(raw, "0", StringComparison.Ordinal)) return false;
        }
        return null;
    }
}
