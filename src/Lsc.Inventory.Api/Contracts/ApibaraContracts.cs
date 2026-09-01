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

    // Internal provenance only. API projections must gate this field by role.
    [JsonPropertyName("source_provider")]
    public string? SourceProvider { get; init; }

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

    [JsonPropertyName("details")]
    public VehicleDetails? Details { get; init; }

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

    [JsonPropertyName("is_buy_now")]
    [JsonConverter(typeof(NullableBooleanJsonConverter))]
    public bool? IsBuyNow { get; init; }

    [JsonPropertyName("is_timed")]
    [JsonConverter(typeof(NullableBooleanJsonConverter))]
    public bool? IsTimed { get; init; }

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

    [JsonPropertyName("sale_price_usd")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? SalePriceUsd { get; init; }

    [JsonPropertyName("current_bid2_usd")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? PreBidUsd { get; init; }

    [JsonPropertyName("estimated_cost")]
    public EstimatedCostInfo? EstimatedCost { get; init; }
}

public sealed record EstimatedCostInfo
{
    [JsonPropertyName("from")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? FromUsd { get; init; }

    [JsonPropertyName("to")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? ToUsd { get; init; }

    [JsonPropertyName("text")]
    public string? Text { get; init; }
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

    [JsonPropertyName("send_from")]
    public string? SendFrom { get; init; }
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

    [JsonPropertyName("raw_type")]
    public string? RawType { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("class")]
    public string? Class { get; init; }

    [JsonPropertyName("text_class")]
    public string? TextClass { get; init; }
}

public sealed record MediaInfo
{
    [JsonPropertyName("thumbs_count")]
    public int? ThumbnailsCount { get; init; }

    [JsonPropertyName("has_360")]
    [JsonConverter(typeof(NullableBooleanJsonConverter))]
    public bool? Has360 { get; init; }

    [JsonPropertyName("has_video")]
    [JsonConverter(typeof(NullableBooleanJsonConverter))]
    public bool? HasVideo { get; init; }

    [JsonPropertyName("thumbs")]
    public IReadOnlyList<string>? Photos { get; init; }

    [JsonPropertyName("items")]
    public IReadOnlyList<AuctionMediaItem>? Items { get; init; }
}

public sealed record AuctionMediaItem
{
    [JsonPropertyName("large")]
    public string? Large { get; init; }

    [JsonPropertyName("thumb")]
    public string? Thumb { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }
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
    public VehicleEngine? Engine { get; init; }

    [JsonPropertyName("airbags")]
    public string? Airbags { get; init; }

    [JsonPropertyName("restraint_system")]
    public string? RestraintSystem { get; init; }
}

public sealed record VehicleEngine
{
    [JsonPropertyName("size_l")]
    public string? SizeLiters { get; init; }

    [JsonPropertyName("hp")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? Horsepower { get; init; }

    [JsonPropertyName("layout")]
    public string? Layout { get; init; }

    [JsonPropertyName("raw")]
    public string? Raw { get; init; }
}

public sealed record VehicleCondition
{
    [JsonPropertyName("primary_damage")]
    public string? PrimaryDamage { get; init; }

    [JsonPropertyName("secondary_damage")]
    public string? SecondaryDamage { get; init; }

    [JsonPropertyName("loss")]
    public string? Loss { get; init; }

    [JsonPropertyName("has_key")]
    [JsonConverter(typeof(NullableBooleanJsonConverter))]
    public bool? HasKey { get; init; }

    [JsonPropertyName("run_condition")]
    public RunConditionInfo? RunCondition { get; init; }
}

public sealed record RunConditionInfo
{
    [JsonPropertyName("value")]
    public string? Value { get; init; }

    [JsonPropertyName("label")]
    public string? Label { get; init; }

    [JsonPropertyName("class_hint")]
    public string? ClassHint { get; init; }
}

public sealed record OdometerInfo
{
    [JsonPropertyName("mi")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? Miles { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("km")]
    [JsonConverter(typeof(NullableDecimalJsonConverter))]
    public decimal? Kilometers { get; init; }
}

public sealed record SaleDocument
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("is_pending")]
    [JsonConverter(typeof(NullableBooleanJsonConverter))]
    public bool? IsPending { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("sale_document_group")]
    public string? Group { get; init; }

    [JsonPropertyName("export")]
    [JsonConverter(typeof(NullableBooleanJsonConverter))]
    public bool? Export { get; init; }

    [JsonPropertyName("registration")]
    [JsonConverter(typeof(NullableBooleanJsonConverter))]
    public bool? Registration { get; init; }

    [JsonPropertyName("page_id")]
    [JsonConverter(typeof(StringOrNumberJsonConverter))]
    public string? PageId { get; init; }
}

public sealed record VehicleDetails
{
    [JsonPropertyName("sale_information")]
    public VehicleSaleInformation? SaleInformation { get; init; }

    [JsonPropertyName("vehicle_description")]
    public VehicleDescriptionDetails? VehicleDescription { get; init; }

    [JsonPropertyName("vehicle_information")]
    public VehicleInformationDetails? VehicleInformation { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalData { get; init; }
}

public sealed record VehicleSaleInformation
{
    [JsonPropertyName("ActualCashValue")]
    public string? ActualCashValue { get; init; }

    [JsonPropertyName("EstimatedRepairCost")]
    public string? EstimatedRepairCost { get; init; }

    [JsonPropertyName("Lane")]
    public string? Lane { get; init; }

    [JsonPropertyName("Aisle")]
    public string? Aisle { get; init; }

    [JsonPropertyName("SellingBranch")]
    public string? SellingBranch { get; init; }

    [JsonPropertyName("Seller")]
    public string? Seller { get; init; }

    [JsonPropertyName("SellerType")]
    public string? SellerType { get; init; }

    [JsonPropertyName("Notes")]
    public string? Notes { get; init; }
}

public sealed record VehicleDescriptionDetails
{
    [JsonPropertyName("BodyStyle")]
    public string? BodyStyle { get; init; }

    [JsonPropertyName("Series")]
    public string? Series { get; init; }

    [JsonPropertyName("Cylinders")]
    public string? Cylinders { get; init; }

    [JsonPropertyName("ManufacturedIn")]
    public string? ManufacturedIn { get; init; }

    [JsonPropertyName("Options")]
    public string? Options { get; init; }

    [JsonPropertyName("VehicleClass")]
    public string? VehicleClass { get; init; }

    [JsonPropertyName("VehicleScore")]
    public string? VehicleScore { get; init; }

    [JsonPropertyName("VINStatus")]
    public string? VinStatus { get; init; }
}

public sealed record VehicleInformationDetails
{
    [JsonPropertyName("VINStatus")]
    public string? VinStatus { get; init; }

    [JsonPropertyName("TitleSaleDocBrand")]
    public string? TitleBrand { get; init; }

    [JsonPropertyName("TitleSaleDocNotes")]
    public string? TitleNotes { get; init; }

    [JsonPropertyName("Notes")]
    public string? Notes { get; init; }
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
