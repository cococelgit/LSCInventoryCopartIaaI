using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lsc.Inventory.Api.Contracts;

public sealed record VehicleSearchRequest(
    string Platform,
    string? LotSubStatus = null,
    int PerPage = 20,
    string? Cursor = null,
    string? State = null,
    string? FacilityId = null,
    int? YearFrom = null,
    int? YearTo = null,
    decimal? PriceMin = null,
    decimal? PriceMax = null,
    string? Make = null,
    string? Model = null);

public sealed record VehicleListResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<AuctionVehicle> Data,
    [property: JsonPropertyName("meta")] CursorMeta Meta);

public sealed record VehicleDetailsResponse(
    [property: JsonPropertyName("data")] AuctionVehicle Data);

public sealed record LocationsResponse(
    [property: JsonPropertyName("data")] IReadOnlyList<AuctionLocation> Data,
    [property: JsonPropertyName("meta")] CursorMeta Meta);

public sealed record UsageResponse(
    [property: JsonPropertyName("data")] JsonElement Data);

public sealed record CursorMeta(
    [property: JsonPropertyName("per_page")] int PerPage,
    [property: JsonPropertyName("next_cursor")] string? NextCursor,
    [property: JsonPropertyName("prev_cursor")] string? PreviousCursor);

public sealed record AuctionVehicle
{
    [JsonPropertyName("platform")]
    public string? Platform { get; init; }

    [JsonPropertyName("lot_number")]
    public string? LotNumber { get; init; }

    [JsonPropertyName("vin")]
    public string? Vin { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("year")]
    public int? Year { get; init; }

    [JsonPropertyName("make")]
    public string? Make { get; init; }

    [JsonPropertyName("model")]
    public string? Model { get; init; }

    [JsonPropertyName("type")]
    public string? VehicleType { get; init; }

    [JsonPropertyName("color")]
    public string? Color { get; init; }

    [JsonPropertyName("fuel_type")]
    public string? FuelType { get; init; }

    [JsonPropertyName("transmission")]
    public string? Transmission { get; init; }

    [JsonPropertyName("drive_type")]
    public string? DriveType { get; init; }

    [JsonPropertyName("vehicle_specs")]
    public VehicleSpecs? VehicleSpecs { get; init; }

    [JsonPropertyName("condition")]
    public VehicleCondition? Condition { get; init; }

    [JsonPropertyName("facility")]
    public AuctionFacility? Facility { get; init; }

    [JsonPropertyName("seller")]
    public AuctionSeller? Seller { get; init; }

    [JsonPropertyName("odometer")]
    public OdometerInfo? OdometerInfo { get; init; }

    [JsonIgnore]
    public decimal? Odometer => OdometerInfo?.Miles;

    [JsonPropertyName("sale_document")]
    public SaleDocument? SaleDocument { get; init; }

    [JsonPropertyName("title_notes")]
    public JsonElement? TitleNotes { get; init; }

    [JsonPropertyName("special_note")]
    public JsonElement? SpecialNote { get; init; }

    [JsonPropertyName("announcements")]
    public JsonElement? Announcements { get; init; }

    [JsonPropertyName("damage")]
    public string? Damage { get; init; }

    [JsonPropertyName("auction")]
    public AuctionInfo? Auction { get; init; }

    [JsonPropertyName("pricing")]
    public PricingInfo? Pricing { get; init; }

    [JsonPropertyName("location")]
    public VehicleLocation? Location { get; init; }

    [JsonPropertyName("media")]
    public MediaInfo? Media { get; init; }

    [JsonPropertyName("_raw_source")]
    public JsonElement? RawSource { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record AuctionInfo
{
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("auction_at")]
    public DateTimeOffset? AuctionAt { get; init; }

    [JsonPropertyName("lot_status")]
    public string? LotStatus { get; init; }

    [JsonPropertyName("lot_sub_status")]
    public string? LotSubStatus { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record PricingInfo
{
    [JsonPropertyName("current_bid_usd")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? CurrentBidUsd { get; init; }

    [JsonPropertyName("buy_now_usd")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? BuyNowUsd { get; init; }

    [JsonPropertyName("estimated_retail_value_usd")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? EstimatedRetailValueUsd { get; init; }

    [JsonPropertyName("repair_cost_usd")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? RepairCostUsd { get; init; }

    [JsonPropertyName("sale_price_usd")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? SalePriceUsd { get; init; }
}

public sealed record VehicleLocation
{
    [JsonPropertyName("display")]
    public string? Display { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("facility_id")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? FacilityId { get; init; }
}

public sealed record AuctionLocation
{
    [JsonPropertyName("platform")]
    public string? Platform { get; init; }

    [JsonPropertyName("facility_id")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? FacilityId { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("city")]
    public string? City { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }
}

public sealed record AuctionFacility
{
    [JsonPropertyName("id")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? Id { get; init; }

    [JsonPropertyName("office_name")]
    public string? OfficeName { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("zip")]
    public string? Zip { get; init; }
}

public sealed record AuctionSeller
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("raw_type")]
    public string? RawType { get; init; }

    [JsonPropertyName("class")]
    public string? Class { get; init; }

    [JsonPropertyName("text_class")]
    public string? TextClass { get; init; }

    [JsonPropertyName("taxonomy_version")]
    public string? TaxonomyVersion { get; init; }
}

public sealed record MediaInfo
{
    [JsonPropertyName("thumbs_count")]
    public int? ThumbnailsCount { get; init; }

    [JsonPropertyName("has_360")]
    public bool? Has360 { get; init; }

    [JsonPropertyName("thumbs")]
    public IReadOnlyList<string>? Photos { get; init; }
}

public sealed record VehicleSpecs
{
    [JsonPropertyName("exterior_color")]
    public string? ExteriorColor { get; init; }

    [JsonPropertyName("fuel_type")]
    public string? FuelType { get; init; }

    [JsonPropertyName("transmission")]
    public string? Transmission { get; init; }

    [JsonPropertyName("drive_type")]
    public string? DriveType { get; init; }

    [JsonPropertyName("body_style")]
    public string? BodyStyle { get; init; }

    [JsonPropertyName("engine")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? Engine { get; init; }

    [JsonPropertyName("cylinders")]
    [JsonConverter(typeof(FlexibleStringJsonConverter))]
    public string? Cylinders { get; init; }

    [JsonPropertyName("airbags")]
    public string? Airbags { get; init; }

    [JsonPropertyName("trim")]
    public string? Trim { get; init; }
}

public sealed record VehicleCondition
{
    [JsonPropertyName("primary_damage")]
    public string? PrimaryDamage { get; init; }

    [JsonPropertyName("secondary_damage")]
    public string? SecondaryDamage { get; init; }

    [JsonPropertyName("has_key")]
    public bool? HasKey { get; init; }

    [JsonPropertyName("run_condition")]
    public RunConditionInfo? RunCondition { get; init; }

    [JsonPropertyName("lot_condition_code")]
    public string? LotConditionCode { get; init; }
}

public sealed record RunConditionInfo
{
    private string? _normalized;
    private string? _raw;

    [JsonPropertyName("run_condition")]
    public string? Normalized
    {
        get => _normalized;
        init => _normalized = value;
    }

    [JsonPropertyName("run_condition_raw")]
    public string? Raw
    {
        get => _raw;
        init => _raw = value;
    }

    // Backward-compatible C# aliases. They are intentionally excluded from serialized payloads.
    [JsonIgnore]
    public string? Value
    {
        get => _normalized;
        init => _normalized = value;
    }

    [JsonIgnore]
    public string? Label
    {
        get => _raw;
        init => _raw = value;
    }

    // Accept legacy nested payloads from existing sources while serializing only the explicit contract above.
    [JsonPropertyName("value")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyValue
    {
        get => null;
        init
        {
            if (_normalized is null) _normalized = value;
        }
    }

    [JsonPropertyName("label")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LegacyLabel
    {
        get => null;
        init
        {
            if (_raw is null) _raw = value;
        }
    }
}

public sealed record OdometerInfo
{
    [JsonPropertyName("mi")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? Miles { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }
}

public sealed record SaleDocument
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("is_pending")]
    public bool? IsPending { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }
}

public static partial class AuctionVehicleNormalizer
{
    public static AuctionVehicle Normalize(AuctionVehicle vehicle, AuctionLocation? scopeLocation, string? scopeState)
    {
        var normalizedLocation = NormalizeLocation(vehicle.Location, vehicle.Facility, scopeLocation, scopeState);
        return vehicle with
        {
            Color = Prefer(vehicle.Color, vehicle.VehicleSpecs?.ExteriorColor),
            FuelType = Prefer(vehicle.FuelType, vehicle.VehicleSpecs?.FuelType),
            Transmission = Prefer(vehicle.Transmission, vehicle.VehicleSpecs?.Transmission),
            DriveType = Prefer(vehicle.DriveType, vehicle.VehicleSpecs?.DriveType),
            Damage = Prefer(vehicle.Damage, vehicle.Condition?.PrimaryDamage),
            Location = normalizedLocation
        };
    }

    private static VehicleLocation? NormalizeLocation(VehicleLocation? location, AuctionFacility? facility, AuctionLocation? scopeLocation, string? scopeState)
    {
        var state = Prefer(location?.State, Prefer(facility?.State, Prefer(scopeLocation?.State, Prefer(scopeState, ExtractStateFromDisplay(location?.Display)))));
        var display = Prefer(location?.Display, BuildLocationDisplay(scopeLocation, state));
        var facilityId = Prefer(location?.FacilityId, Prefer(facility?.Id, scopeLocation?.FacilityId));

        return string.IsNullOrWhiteSpace(display) && string.IsNullOrWhiteSpace(state) && string.IsNullOrWhiteSpace(facilityId)
            ? null
            : new VehicleLocation { Display = display, State = state, FacilityId = facilityId };
    }

    private static string? BuildLocationDisplay(AuctionLocation? location, string? state)
    {
        var name = Prefer(location?.Name, location?.City);
        if (string.IsNullOrWhiteSpace(name)) return null;
        return string.IsNullOrWhiteSpace(state) ? name : $"{name} ({state.Trim().ToUpperInvariant()})";
    }

    private static string? Prefer(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static string? ExtractStateFromDisplay(string? display)
    {
        if (string.IsNullOrWhiteSpace(display)) return null;
        var match = StateSuffixRegex().Match(display.Trim());
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    [GeneratedRegex("\\(([A-Z]{2})\\)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex StateSuffixRegex();
}
