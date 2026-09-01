using System.Text.Json.Serialization;
using Lsc.Inventory.Api.Storage;

namespace Lsc.Inventory.Api.Contracts;

public sealed record PublicInventoryVehicle
{
    [JsonPropertyName("lot")] public required string Lot { get; init; }
    [JsonPropertyName("vin")] public string? Vin { get; init; }
    [JsonPropertyName("platform")] public required string Platform { get; init; }
    [JsonPropertyName("observedAt")] public required DateTimeOffset ObservedAt { get; init; }
    [JsonPropertyName("title")] public string? Title { get; init; }
    [JsonPropertyName("year")] public int? Year { get; init; }
    [JsonPropertyName("make")] public string? Make { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
    [JsonPropertyName("series")] public string? Series { get; init; }
    [JsonPropertyName("vehicleType")] public string? VehicleType { get; init; }
    [JsonPropertyName("bodyStyle")] public string? BodyStyle { get; init; }
    [JsonPropertyName("color")] public string? Color { get; init; }
    [JsonPropertyName("fuelType")] public string? FuelType { get; init; }
    [JsonPropertyName("transmission")] public string? Transmission { get; init; }
    [JsonPropertyName("driveType")] public string? DriveType { get; init; }
    [JsonPropertyName("odometer")] public decimal? Odometer { get; init; }
    [JsonPropertyName("odometerKm")] public decimal? OdometerKm { get; init; }
    [JsonPropertyName("odometerStatus")] public string? OdometerStatus { get; init; }
    [JsonPropertyName("damage")] public string? Damage { get; init; }
    [JsonPropertyName("secondaryDamage")] public string? SecondaryDamage { get; init; }
    [JsonPropertyName("lossType")] public string? LossType { get; init; }
    [JsonPropertyName("startCode")] public string? StartCode { get; init; }
    [JsonPropertyName("runCondition")] public string? RunCondition { get; init; }
    [JsonPropertyName("runConditionRaw")] public string? RunConditionRaw { get; init; }
    [JsonPropertyName("hasKey")] public bool? HasKey { get; init; }
    [JsonPropertyName("auctionAt")] public DateTimeOffset? AuctionAt { get; init; }
    [JsonPropertyName("lotStatus")] public string? LotStatus { get; init; }
    [JsonPropertyName("lotSubStatus")] public string? LotSubStatus { get; init; }
    [JsonPropertyName("isBuyNow")] public bool? IsBuyNow { get; init; }
    [JsonPropertyName("isTimed")] public bool? IsTimed { get; init; }
    [JsonPropertyName("currentBidUsd")] public decimal? CurrentBidUsd { get; init; }
    [JsonPropertyName("preBidUsd")] public decimal? PreBidUsd { get; init; }
    [JsonPropertyName("buyNowUsd")] public decimal? BuyNowUsd { get; init; }
    [JsonPropertyName("estimatedPriceFromUsd")] public decimal? EstimatedPriceFromUsd { get; init; }
    [JsonPropertyName("estimatedPriceToUsd")] public decimal? EstimatedPriceToUsd { get; init; }
    [JsonPropertyName("estimatedPriceText")] public string? EstimatedPriceText { get; init; }
    [JsonPropertyName("actualCashValueUsd")] public decimal? ActualCashValueUsd { get; init; }
    [JsonPropertyName("estimatedRepairCostUsd")] public decimal? EstimatedRepairCostUsd { get; init; }
    [JsonPropertyName("location")] public string? Location { get; init; }
    [JsonPropertyName("sendFrom")] public string? SendFrom { get; init; }
    [JsonPropertyName("state")] public string? State { get; init; }
    [JsonPropertyName("facilityId")] public string? FacilityId { get; init; }
    [JsonPropertyName("sellingBranch")] public string? SellingBranch { get; init; }
    [JsonPropertyName("lane")] public string? Lane { get; init; }
    [JsonPropertyName("aisle")] public string? Aisle { get; init; }
    [JsonPropertyName("sellerName")] public string? SellerName { get; init; }
    [JsonPropertyName("sellerType")] public string? SellerType { get; init; }
    [JsonPropertyName("titleType")] public string? TitleType { get; init; }
    [JsonPropertyName("titleCategory")] public string? TitleCategory { get; init; }
    [JsonPropertyName("titleDisplayLabel")] public string? TitleDisplayLabel { get; init; }
    [JsonPropertyName("titleFlags")] public IReadOnlyList<string> TitleFlags { get; init; } = [];
    [JsonPropertyName("titleReviewStatus")] public string? TitleReviewStatus { get; init; }
    [JsonPropertyName("titleTaxonomyVersion")] public string? TitleTaxonomyVersion { get; init; }
    [JsonPropertyName("saleDocumentType")] public string? SaleDocumentType { get; init; }
    [JsonPropertyName("saleDocumentGroup")] public string? SaleDocumentGroup { get; init; }
    [JsonPropertyName("saleDocumentPending")] public bool? SaleDocumentPending { get; init; }
    [JsonPropertyName("saleDocumentExport")] public bool? SaleDocumentExport { get; init; }
    [JsonPropertyName("saleDocumentRegistration")] public bool? SaleDocumentRegistration { get; init; }
    [JsonPropertyName("titleBrand")] public string? TitleBrand { get; init; }
    [JsonPropertyName("titleNotes")] public string? TitleNotes { get; init; }
    [JsonPropertyName("engineSizeLiters")] public string? EngineSizeLiters { get; init; }
    [JsonPropertyName("engineHorsepower")] public decimal? EngineHorsepower { get; init; }
    [JsonPropertyName("engineLayout")] public string? EngineLayout { get; init; }
    [JsonPropertyName("engineDescription")] public string? EngineDescription { get; init; }
    [JsonPropertyName("cylinders")] public string? Cylinders { get; init; }
    [JsonPropertyName("airbags")] public string? Airbags { get; init; }
    [JsonPropertyName("restraintSystem")] public string? RestraintSystem { get; init; }
    [JsonPropertyName("vinStatus")] public string? VinStatus { get; init; }
    [JsonPropertyName("vehicleClass")] public string? VehicleClass { get; init; }
    [JsonPropertyName("vehicleScore")] public string? VehicleScore { get; init; }
    [JsonPropertyName("manufacturedIn")] public string? ManufacturedIn { get; init; }
    [JsonPropertyName("options")] public string? Options { get; init; }
    [JsonPropertyName("has360")] public bool? Has360 { get; init; }
    [JsonPropertyName("hasVideo")] public bool? HasVideo { get; init; }
    [JsonPropertyName("photos")] public IReadOnlyList<string> Photos { get; init; } = [];
    [JsonPropertyName("media")] public IReadOnlyList<PublicMediaItem> Media { get; init; } = [];
    [JsonPropertyName("scoring")] public PublicLscScoring? Scoring { get; init; }
}

public sealed record PublicLscScoring
{
    [JsonPropertyName("status")] public required string Status { get; init; }
    [JsonPropertyName("preGrade")] public decimal? PreGrade { get; init; }
    [JsonPropertyName("buyScore")] public decimal? BuyScore { get; init; }
    [JsonPropertyName("maxPointsEvaluable")] public decimal MaxPointsEvaluable { get; init; }
    [JsonPropertyName("coveragePercent")] public decimal CoveragePercent { get; init; }
    [JsonPropertyName("confidencePercent")] public decimal ConfidencePercent { get; init; }
    [JsonPropertyName("category")] public string? Category { get; init; }
    [JsonPropertyName("policyVersion")] public required string PolicyVersion { get; init; }
    [JsonPropertyName("scoredAt")] public required DateTimeOffset ScoredAt { get; init; }
    [JsonPropertyName("reasonCodes")] public IReadOnlyList<string> ReasonCodes { get; init; } = [];
    [JsonPropertyName("missingFields")] public IReadOnlyList<string> MissingFields { get; init; } = [];
    [JsonPropertyName("factors")] public IReadOnlyList<PublicLscScoreFactor> Factors { get; init; } = [];
    [JsonPropertyName("penalties")] public IReadOnlyList<PublicLscScorePenalty> Penalties { get; init; } = [];
}

public sealed record PublicLscScoreFactor(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("points")] decimal Points,
    [property: JsonPropertyName("maxPointsEvaluable")] decimal MaxPointsEvaluable,
    [property: JsonPropertyName("evaluated")] bool Evaluated,
    [property: JsonPropertyName("explanation")] string Explanation);

public sealed record PublicLscScorePenalty(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("points")] decimal Points,
    [property: JsonPropertyName("explanation")] string Explanation);

public sealed record PublicMediaItem(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("type")] string? Type);

public sealed record PublicInventoryResponse(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("vehicles")] IReadOnlyList<PublicInventoryVehicle> Vehicles);

public sealed record PublicInventorySearchResponse(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("page")] int Page,
    [property: JsonPropertyName("pageSize")] int PageSize,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("vehicles")] IReadOnlyList<PublicInventoryVehicle> Vehicles);

public sealed record PublicInventorySummaryResponse(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("facets")] IReadOnlyDictionary<string, IReadOnlyList<InventoryFacetValue>> Facets);
