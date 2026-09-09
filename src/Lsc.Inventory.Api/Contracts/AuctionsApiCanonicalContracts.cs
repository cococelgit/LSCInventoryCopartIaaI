using System.Text.Json;

namespace Lsc.Inventory.Api.Contracts;

/// <summary>
/// Version of the provider-to-canonical mapping contract. Increment only when
/// the meaning of a mapped field changes, not for a provider content update.
/// </summary>
public static class AuctionsApiCanonicalContract
{
    public const string Version = "auctionsapi-v2";
    public const string Source = "auctionsapi";
}

public sealed record AuctionsApiEnumValue(
    string? Id,
    string? Name,
    string? NormalizedValue,
    JsonElement? Raw,
    string Source = AuctionsApiCanonicalContract.Source,
    string ContractVersion = AuctionsApiCanonicalContract.Version);

public sealed record AuctionsApiMoneyValue(
    decimal? Value,
    DateTimeOffset? UpdatedAt,
    JsonElement? Raw,
    string Source = AuctionsApiCanonicalContract.Source,
    string ContractVersion = AuctionsApiCanonicalContract.Version);

public sealed record AuctionsApiDateValue(
    DateTimeOffset? Value,
    DateTimeOffset? UpdatedAt,
    JsonElement? Raw,
    string Source = AuctionsApiCanonicalContract.Source,
    string ContractVersion = AuctionsApiCanonicalContract.Version);

public sealed record AuctionsApiProviderLot(
    string Platform,
    string? DomainId,
    string? LotNumber,
    string? ExternalId,
    string? Vin,
    AuctionsApiEnumValue? Status,
    AuctionsApiEnumValue? SellerType,
    string? SellerName,
    IReadOnlyDictionary<string, bool?> SellerFlags,
    AuctionsApiEnumValue? Title,
    AuctionsApiEnumValue? DetailedTitle,
    AuctionsApiEnumValue? Condition,
    AuctionsApiEnumValue? DamageMain,
    AuctionsApiEnumValue? DamageSecond,
    AuctionsApiEnumValue? OdometerStatus,
    AuctionsApiEnumValue? Airbags,
    AuctionsApiMoneyValue? Bid,
    AuctionsApiMoneyValue? FinalBid,
    AuctionsApiMoneyValue? BuyNow,
    AuctionsApiMoneyValue? SalePrice,
    AuctionsApiDateValue? SaleDate,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? UpdatedAt,
    JsonElement Raw);

public sealed record AuctionsApiProviderVehicle(
    string Platform,
    string? DomainId,
    string? ProviderVehicleId,
    int? Year,
    AuctionsApiEnumValue? Manufacturer,
    AuctionsApiEnumValue? Model,
    AuctionsApiEnumValue? Generation,
    AuctionsApiEnumValue? VehicleType,
    AuctionsApiEnumValue? BodyType,
    AuctionsApiEnumValue? Fuel,
    AuctionsApiEnumValue? Engine,
    int? Cylinders,
    AuctionsApiEnumValue? Transmission,
    AuctionsApiEnumValue? DriveWheel,
    IReadOnlyList<AuctionsApiProviderLot> Lots,
    JsonElement Raw);

public sealed record AuctionsApiArchivedOutcome(
    string Platform,
    string? DomainId,
    string? LotNumber,
    string? ExternalLotId,
    AuctionsApiEnumValue? Status,
    AuctionsApiMoneyValue? Bid,
    AuctionsApiMoneyValue? FinalBid,
    AuctionsApiMoneyValue? BuyNow,
    AuctionsApiDateValue? SaleDate,
    DateTimeOffset? ArchivedAt,
    JsonElement Raw);
