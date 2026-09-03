using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Scoring;
using Lsc.Inventory.Api.Sources;

namespace Lsc.Inventory.Api.Storage;

public sealed record CopartLotWatermarkState(
    DateTimeOffset SourceUpdatedAt,
    string RowFingerprint,
    EligibilityEvaluation Eligibility);

public sealed record CopartLotWatermarkUpdate(
    string LotKey,
    DateTimeOffset SourceUpdatedAt,
    string RowFingerprint,
    string ProcessingVersion,
    EligibilityEvaluation Eligibility);

public static class CopartLotWatermarkPolicy
{
    public const string Version = "copart-last-updated-watermark-v1";

    public static string CurrentProcessingVersion =>
        $"{Version}|eligibility:{AuctionEligibilityEvaluator.RuleVersion}|title:{CopartTitleMapper.TaxonomyVersion}|scoring:{LscScoringPolicy.CopartPolicyVersion}";

    public static DateTimeOffset? GetSourceUpdatedAt(AuctionVehicle vehicle)
    {
        if (vehicle.AdditionalData is null ||
            !vehicle.AdditionalData.TryGetValue("source_updated_at", out var value) ||
            value.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(value.GetString(), out var parsed))
            return null;
        return parsed.ToUniversalTime();
    }

    public static string ComputeRowFingerprint(AuctionVehicle vehicle)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            vehicle.LotNumber,
            vehicle.Vin,
            vehicle.Title,
            vehicle.Year,
            vehicle.Make,
            vehicle.Model,
            vehicle.VehicleType,
            vehicle.Color,
            vehicle.FuelType,
            vehicle.Transmission,
            vehicle.DriveType,
            vehicle.VehicleSpecs,
            vehicle.Condition,
            vehicle.Facility,
            vehicle.Seller,
            vehicle.OdometerInfo,
            vehicle.SaleDocument,
            vehicle.TitleNotes,
            vehicle.SpecialNote,
            vehicle.Announcements,
            vehicle.Damage,
            vehicle.Auction,
            vehicle.Pricing,
            vehicle.Location,
            vehicle.Media
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
