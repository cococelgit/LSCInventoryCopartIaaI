using System.Text.Json.Serialization;

namespace Lsc.Inventory.Api.Contracts;

public sealed record PublicInventoryVehicle(
    [property: JsonPropertyName("lot")] string Lot,
    [property: JsonPropertyName("platform")] string Platform,
    [property: JsonPropertyName("observedAt")] DateTimeOffset ObservedAt,
    [property: JsonPropertyName("title")] string? Title,
    [property: JsonPropertyName("year")] int? Year,
    [property: JsonPropertyName("make")] string? Make,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("vehicleType")] string? VehicleType,
    [property: JsonPropertyName("color")] string? Color,
    [property: JsonPropertyName("fuelType")] string? FuelType,
    [property: JsonPropertyName("transmission")] string? Transmission,
    [property: JsonPropertyName("driveType")] string? DriveType,
    [property: JsonPropertyName("odometer")] decimal? Odometer,
    [property: JsonPropertyName("damage")] string? Damage,
    [property: JsonPropertyName("auctionAt")] DateTimeOffset? AuctionAt,
    [property: JsonPropertyName("lotStatus")] string? LotStatus,
    [property: JsonPropertyName("currentBidUsd")] decimal? CurrentBidUsd,
    [property: JsonPropertyName("buyNowUsd")] decimal? BuyNowUsd,
    [property: JsonPropertyName("location")] string? Location,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("titleType")] string? TitleType,
    [property: JsonPropertyName("titleCode")] string? TitleCode,
    [property: JsonPropertyName("titleDescriptionEs")] string? TitleDescriptionEs,
    [property: JsonPropertyName("titleMappingStatus")] string? TitleMappingStatus,
    [property: JsonPropertyName("titleState")] string? TitleState,
    [property: JsonPropertyName("facilityId")] string? FacilityId,
    [property: JsonPropertyName("vinMasked")] string? VinMasked,
    [property: JsonPropertyName("sellerName")] string? SellerName,
    [property: JsonPropertyName("trim")] string? Trim,
    [property: JsonPropertyName("bodyStyle")] string? BodyStyle,
    [property: JsonPropertyName("engine")] string? Engine,
    [property: JsonPropertyName("cylinders")] string? Cylinders,
    [property: JsonPropertyName("estimatedRetailValueUsd")] decimal? EstimatedRetailValueUsd,
    [property: JsonPropertyName("repairCostUsd")] decimal? RepairCostUsd,
    [property: JsonPropertyName("lotConditionCode")] string? LotConditionCode,
    [property: JsonPropertyName("runCondition")] string? RunCondition,
    [property: JsonPropertyName("hasKeys")] bool? HasKeys,
    [property: JsonPropertyName("photos")] IReadOnlyList<string> Photos);

public sealed record PublicInventoryResponse(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("vehicles")] IReadOnlyList<PublicInventoryVehicle> Vehicles);

public sealed record PublicInventoryPageResponse(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("total")] long Total,
    [property: JsonPropertyName("totalPages")] int TotalPages,
    [property: JsonPropertyName("vehicles")] IReadOnlyList<PublicInventoryVehicle> Vehicles);
