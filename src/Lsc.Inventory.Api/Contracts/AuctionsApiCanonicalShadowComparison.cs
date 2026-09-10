namespace Lsc.Inventory.Api.Contracts;

public sealed record AuctionsApiCanonicalShadowComparison(
    string Platform,
    string? LotNumber,
    IReadOnlyList<string> Differences)
{
    public bool HasDifferences => Differences.Count > 0;
}

public static class AuctionsApiCanonicalShadowComparer
{
    public static AuctionsApiCanonicalShadowComparison Compare(AuctionVehicle legacy, AuctionVehicle canonical)
    {
        var differences = new List<string>();
        CompareValue("body_style", legacy.VehicleSpecs?.BodyStyle, canonical.VehicleSpecs?.BodyStyle, differences);
        CompareValue("vehicle_type", legacy.VehicleType, canonical.VehicleType, differences);
        CompareValue("primary_damage", legacy.Condition?.PrimaryDamage, canonical.Condition?.PrimaryDamage, differences);
        CompareValue("secondary_damage", legacy.Condition?.SecondaryDamage, canonical.Condition?.SecondaryDamage, differences);
        CompareValue("seller_type", legacy.Seller?.Type, canonical.Seller?.Type, differences);
        CompareValue("current_bid_usd", legacy.Pricing?.CurrentBidUsd, canonical.Pricing?.CurrentBidUsd, differences);
        CompareValue("buy_now_usd", legacy.Pricing?.BuyNowUsd, canonical.Pricing?.BuyNowUsd, differences);
        CompareValue("lot_status", legacy.Auction?.LotStatus, canonical.Auction?.LotStatus, differences);
        return new(legacy.Platform ?? canonical.Platform ?? string.Empty, legacy.LotNumber, differences);
    }

    private static void CompareValue<T>(string name, T? legacy, T? canonical, ICollection<string> differences)
        where T : struct
    {
        if (!EqualityComparer<T?>.Default.Equals(legacy, canonical)) differences.Add(name);
    }

    private static void CompareValue(string name, string? legacy, string? canonical, ICollection<string> differences)
    {
        if (!string.Equals(legacy, canonical, StringComparison.OrdinalIgnoreCase)) differences.Add(name);
    }
}
