using System.Text.Json;
using System.Text.RegularExpressions;
using Lsc.Inventory.Api.Contracts;

namespace Lsc.Inventory.Api.Normalization;

public static partial class CanonicalVehicleCleaner
{
    public const string AliasVersion = "make_model_aliases_v1";

    private static readonly IReadOnlyDictionary<string, string> MakeAliases = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["CHEV"] = "CHEVROLET",
        ["MERCEDES BENZ"] = "MERCEDES-BENZ",
        ["MERCEDESBENZ"] = "MERCEDES-BENZ",
        ["VW"] = "VOLKSWAGEN"
    };

    public static AuctionVehicle Clean(AuctionVehicle vehicle)
    {
        var rawSource = vehicle.RawSource ?? JsonSerializer.SerializeToElement(vehicle);
        var make = Upper(vehicle.Make);
        if (make is not null && MakeAliases.TryGetValue(make, out var alias)) make = alias;
        var model = Upper(vehicle.Model);
        if (model is "ALL MODELS" or "UNKNOWN" or "N/A" or "NA") model = null;

        return vehicle with
        {
            Platform = Lower(vehicle.Platform),
            LotNumber = DigitsOrTrimmed(vehicle.LotNumber),
            Vin = Upper(vehicle.Vin)?.Replace(" ", string.Empty, StringComparison.Ordinal),
            Title = Compact(vehicle.Title),
            Make = make,
            Model = model,
            VehicleType = Upper(vehicle.VehicleType),
            Color = TitleCase(vehicle.Color),
            FuelType = TitleCase(vehicle.FuelType),
            Transmission = TitleCase(vehicle.Transmission),
            DriveType = TitleCase(vehicle.DriveType),
            Damage = TitleCase(vehicle.Damage),
            VehicleSpecs = vehicle.VehicleSpecs is null ? null : vehicle.VehicleSpecs with
            {
                ExteriorColor = TitleCase(vehicle.VehicleSpecs.ExteriorColor),
                FuelType = TitleCase(vehicle.VehicleSpecs.FuelType),
                Transmission = TitleCase(vehicle.VehicleSpecs.Transmission),
                DriveType = TitleCase(vehicle.VehicleSpecs.DriveType)
            },
            Condition = vehicle.Condition is null ? null : vehicle.Condition with
            {
                PrimaryDamage = TitleCase(vehicle.Condition.PrimaryDamage),
                SecondaryDamage = TitleCase(vehicle.Condition.SecondaryDamage),
                RunCondition = vehicle.Condition.RunCondition is null ? null : vehicle.Condition.RunCondition with
                {
                    Normalized = Upper(vehicle.Condition.RunCondition.Normalized),
                    Raw = Compact(vehicle.Condition.RunCondition.Raw)
                }
            },
            Seller = vehicle.Seller is null ? null : vehicle.Seller with { Name = Compact(vehicle.Seller.Name), Type = Lower(vehicle.Seller.Type) },
            SaleDocument = vehicle.SaleDocument is null ? null : vehicle.SaleDocument with { Name = Upper(vehicle.SaleDocument.Name) },
            Location = vehicle.Location is null ? null : vehicle.Location with
            {
                Display = Compact(vehicle.Location.Display),
                State = Upper(vehicle.Location.State),
                FacilityId = DigitsOrTrimmed(vehicle.Location.FacilityId)
            },
            Facility = vehicle.Facility is null ? null : vehicle.Facility with
            {
                Id = DigitsOrTrimmed(vehicle.Facility.Id),
                OfficeName = Compact(vehicle.Facility.OfficeName),
                State = Upper(vehicle.Facility.State),
                Zip = Compact(vehicle.Facility.Zip)
            },
            Pricing = vehicle.Pricing is null ? null : vehicle.Pricing with
            {
                CurrentBidUsd = NonNegative(vehicle.Pricing.CurrentBidUsd),
                BuyNowUsd = NonNegative(vehicle.Pricing.BuyNowUsd),
                SalePriceUsd = NonNegative(vehicle.Pricing.SalePriceUsd)
            },
            OdometerInfo = vehicle.OdometerInfo is null ? null : vehicle.OdometerInfo with
            {
                Miles = NonNegative(vehicle.OdometerInfo.Miles),
                Status = Upper(vehicle.OdometerInfo.Status)
            },
            RawSource = rawSource
        };
    }

    private static decimal? NonNegative(decimal? value) => value is >= 0 ? value : null;
    private static string? Lower(string? value) => Compact(value)?.ToLowerInvariant();
    private static string? Upper(string? value) => Compact(value)?.ToUpperInvariant();
    private static string? TitleCase(string? value)
    {
        var compact = Compact(value)?.ToLowerInvariant();
        return compact is null ? null : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(compact);
    }
    private static string? DigitsOrTrimmed(string? value)
    {
        var compact = Compact(value);
        if (compact is null) return null;
        var candidate = compact.StartsWith("LOT ", StringComparison.OrdinalIgnoreCase) ? compact[4..].Trim() : compact;
        var digits = DigitsRegex().Replace(candidate, string.Empty);
        return digits.Length > 0 && candidate.Any(char.IsDigit) && !candidate.Any(char.IsLetter) ? digits : compact;
    }
    private static string? Compact(string? value) => string.IsNullOrWhiteSpace(value) ? null : WhitespaceRegex().Replace(value.Trim(), " ");

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex("[^0-9]")]
    private static partial Regex DigitsRegex();
}
