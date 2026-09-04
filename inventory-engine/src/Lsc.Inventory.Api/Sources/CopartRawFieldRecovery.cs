using System.Globalization;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Normalization;

namespace Lsc.Inventory.Api.Sources;

/// <summary>
/// Rehydrates canonical Copart fields from the original Excel row preserved in RawSource.
/// It never invents a value and never overwrites a non-empty canonical value.
/// </summary>
public static class CopartRawFieldRecovery
{
    public static AuctionVehicle Recover(AuctionVehicle vehicle)
    {
        if (!string.Equals(vehicle.Platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase))
            return vehicle;

        var raw = vehicle.RawSource;
        if (raw is not { ValueKind: JsonValueKind.Object }) return vehicle;

        var sellerName = FirstPresent(vehicle.Seller?.Name, GetString(raw.Value, "Seller Name"));
        var seller = RecoverSeller(vehicle.Seller, sellerName);

        var primaryDamage = FirstPresent(vehicle.Condition?.PrimaryDamage, vehicle.Damage, GetString(raw.Value, "Damage Description"));
        var secondaryDamage = FirstPresent(vehicle.Condition?.SecondaryDamage, GetString(raw.Value, "Secondary Damage"));
        var runRaw = FirstPresent(vehicle.Condition?.RunCondition?.Raw, GetString(raw.Value, "Runs/Drives"));
        var runNormalized = FirstPresent(
            vehicle.Condition?.RunCondition?.Normalized,
            NormalizeRunCondition(runRaw));
        var condition = vehicle.Condition ?? new VehicleCondition();
        condition = condition with
        {
            PrimaryDamage = primaryDamage,
            SecondaryDamage = secondaryDamage,
            HasKey = vehicle.Condition?.HasKey ?? ParseYesNo(GetString(raw.Value, "Has Keys-Yes or No")),
            RunCondition = vehicle.Condition?.RunCondition is not null &&
                          !string.IsNullOrWhiteSpace(vehicle.Condition.RunCondition.Normalized) &&
                          !string.IsNullOrWhiteSpace(vehicle.Condition.RunCondition.Raw)
                ? vehicle.Condition.RunCondition
                : new RunConditionInfo { Normalized = runNormalized, Raw = runRaw },
            LotConditionCode = FirstPresent(vehicle.Condition?.LotConditionCode, GetString(raw.Value, "Lot Cond. Code"))
        };

        var odometerMiles = vehicle.OdometerInfo?.Miles ?? ParseDecimal(GetString(raw.Value, "Odometer"));
        var odometer = vehicle.OdometerInfo is null && odometerMiles is null
            ? null
            : (vehicle.OdometerInfo ?? new OdometerInfo()) with
            {
                Miles = odometerMiles,
                Status = FirstPresent(vehicle.OdometerInfo?.Status, GetString(raw.Value, "Odometer Brand"))
            };

        var titleCode = FirstPresent(GetString(raw.Value, "Sale Title Type"), GetString(raw.Value, "Title"));
        var title = FirstPresent(vehicle.SaleDocument?.Name, vehicle.Title, ResolveTitleDescription(titleCode));
        var saleDocument = vehicle.SaleDocument is null && title is null
            ? null
            : (vehicle.SaleDocument ?? new SaleDocument()) with
            {
                Name = title,
                State = FirstPresent(vehicle.SaleDocument?.State, GetString(raw.Value, "Sale Title State"))
            };

        var auction = vehicle.Auction is null
            ? null
            : vehicle.Auction with
            {
                State = FirstPresent(vehicle.Auction.State, GetString(raw.Value, "Sale Status")),
                LotStatus = FirstPresent(vehicle.Auction.LotStatus, GetString(raw.Value, "Sale Status")),
                LotSubStatus = FirstPresent(vehicle.Auction.LotSubStatus, GetString(raw.Value, "Sale Light"))
            };

        var additional = vehicle.AdditionalData is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(vehicle.AdditionalData);
        AddRecoveryEvidence(additional, "source_recovery_seller_name", sellerName);
        AddRecoveryEvidence(additional, "source_recovery_run_condition_raw", runRaw);
        AddRecoveryEvidence(additional, "source_recovery_primary_damage", primaryDamage);
        AddRecoveryEvidence(additional, "source_recovery_secondary_damage", secondaryDamage);
        AddRecoveryEvidence(additional, "source_recovery_keys_raw", GetString(raw.Value, "Has Keys-Yes or No"));
        AddRecoveryEvidence(additional, "source_recovery_odometer_raw", GetString(raw.Value, "Odometer"));
        AddRecoveryEvidence(additional, "source_recovery_title_code", titleCode);

        return vehicle with
        {
            Seller = seller,
            Condition = condition,
            Damage = FirstPresent(vehicle.Damage, primaryDamage),
            OdometerInfo = odometer,
            SaleDocument = saleDocument,
            Title = FirstPresent(vehicle.Title, title),
            Auction = auction,
            AdditionalData = additional
        };
    }

    private static AuctionSeller RecoverSeller(AuctionSeller? existing, string? sellerName)
    {
        var current = existing ?? new AuctionSeller();
        var hasUsableExistingClassification = !string.IsNullOrWhiteSpace(current.Type)
            && !string.Equals(current.Type, SellerTaxonomy.Unknown, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(current.Type, SellerTaxonomy.Unclassified, StringComparison.OrdinalIgnoreCase)
            && (current.ClassificationConfidence ?? 0m) >= SellerTaxonomy.InclusionThreshold;
        if (hasUsableExistingClassification)
            return current with { Name = FirstPresent(current.Name, sellerName) };

        var classification = SellerTaxonomy.ClassifyDetailed(
            current.RawType,
            current.Class,
            current.TextClass,
            sellerName);
        return current with
        {
            Name = sellerName,
            Type = classification.Category,
            TaxonomyVersion = SellerTaxonomy.Version,
            ClassificationConfidence = classification.Confidence,
            NeedsReview = classification.NeedsReview,
            ClassificationEvidence = classification.Evidence
        };
    }

    private static string? ResolveTitleDescription(string? titleCode) =>
        CopartTitleCatalog.TryGet(titleCode, out var definition)
            ? definition.EnglishDescription
            : titleCode;

    private static string? GetString(JsonElement raw, string name)
    {
        foreach (var property in raw.EnumerateObject())
        {
            if (!string.Equals(property.Name.Trim(), name, StringComparison.OrdinalIgnoreCase)) continue;
            return property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()?.Trim() : null;
        }
        return null;
    }

    private static void AddRecoveryEvidence(IDictionary<string, JsonElement> data, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) data[key] = JsonSerializer.SerializeToElement(value);
    }

    private static string? FirstPresent(params string?[] values) => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static decimal? ParseDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().Replace(",", string.Empty, StringComparison.Ordinal);
        var numeric = new string(normalized.Where(character => char.IsDigit(character) || character is '.' or '-').ToArray());
        return decimal.TryParse(numeric, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static bool? ParseYesNo(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = string.Join(' ', value.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized switch
        {
            "YES" or "Y" or "TRUE" => true,
            "NO" or "N" or "FALSE" => false,
            _ when normalized.Contains("NO KEY", StringComparison.Ordinal) || normalized.Contains("WITHOUT KEY", StringComparison.Ordinal) => false,
            _ => null
        };
    }

    private static string NormalizeRunCondition(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "UNVERIFIED";
        var normalized = string.Join(' ', value.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return normalized switch
        {
            "RUN & DRIVE" or "RUNS AND DRIVES" => "RUNS_AND_DRIVES",
            "STARTS" or "ENGINE START PROGRAM" => "STARTS",
            "STATIONARY" => "STATIONARY",
            "NO INFORMATION" => "UNVERIFIED",
            _ => "UNVERIFIED"
        };
    }
}
