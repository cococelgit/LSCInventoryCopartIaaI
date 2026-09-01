using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Normalization;

namespace Lsc.Inventory.Api.Sources;

/// <summary>
/// Maps the approved Copart title catalog while preserving source values. The compact taxonomy is
/// applied separately, after eligibility, through the shared TitleFacetCategory authority.
/// </summary>
public static class CopartTitleMapper
{
    public const string TaxonomyVersion = "copart-title-taxonomy-v1";
    public const string ClassifiedReviewStatus = "CLASSIFIED";
    public const string UnverifiedReviewStatus = "UNVERIFIED";
    public const string ReviewRequiredStatus = "REVIEW_REQUIRED";

    public static AuctionVehicle Apply(AuctionVehicle vehicle)
    {
        if (!IsCopart(vehicle)) return vehicle;

        var code = ReadCode(vehicle);
        var mapped = CopartTitleCatalog.TryGet(code, out var definition);
        var title = mapped ? definition.EnglishDescription : code ?? vehicle.SaleDocument?.Name ?? vehicle.Title;
        var notes = ReadObject(vehicle.TitleNotes);
        notes["sale_title_type_code"] = code;
        notes["sale_title_description_en"] = title;
        notes["sale_title_description_es"] = mapped ? definition.SpanishDescription : null;
        notes["title_mapping_version"] = CopartTitleCatalog.Version;
        notes["title_mapping_status"] = mapped ? "mapped" : "unmapped";
        notes["source_process_recommendation"] = mapped ? (definition.SourceProcessRecommendation ? "yes" : "no") : null;

        var additional = vehicle.AdditionalData is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(vehicle.AdditionalData);
        additional["source_title_type_code"] = JsonSerializer.SerializeToElement(code);
        additional["source_title_raw"] = JsonSerializer.SerializeToElement(code ?? vehicle.SaleDocument?.Name ?? vehicle.Title);
        additional["source_title_mapping"] = JsonSerializer.SerializeToElement(mapped ? "mapped" : "unmapped");
        additional["source_title_mapping_version"] = JsonSerializer.SerializeToElement(CopartTitleCatalog.Version);
        additional["source_title_description_es"] = JsonSerializer.SerializeToElement(mapped ? definition.SpanishDescription : null);

        return vehicle with
        {
            Title = title,
            SaleDocument = vehicle.SaleDocument is null
                ? new SaleDocument { Name = title }
                : vehicle.SaleDocument with { Name = title },
            TitleNotes = JsonSerializer.SerializeToElement(notes),
            AdditionalData = additional
        };
    }

    /// <summary>
    /// Adds only canonical title metadata for an eligible Copart snapshot. Source title fields remain
    /// untouched, and identical source/title metadata produces the same persisted payload hash.
    /// </summary>
    public static AuctionVehicle ApplyTaxonomy(AuctionVehicle vehicle)
    {
        if (!IsCopart(vehicle)) return vehicle;

        var rawTitle = ReadCode(vehicle) ?? TitleFacetCategory.SourceTitle(vehicle);
        var descriptor = TitleFacetCategory.Describe(InventorySourcePolicy.CopartExcelSource, rawTitle);
        var reviewStatus = descriptor.Category switch
        {
            TitleFacetCategory.Unverified => UnverifiedReviewStatus,
            TitleFacetCategory.Other => ReviewRequiredStatus,
            _ => ClassifiedReviewStatus
        };

        var notes = ReadObject(vehicle.TitleNotes);
        notes["title_category"] = descriptor.Category;
        notes["title_flags"] = string.Join("|", descriptor.Flags);
        notes["title_review_status"] = reviewStatus;
        notes["title_taxonomy_version"] = TaxonomyVersion;

        var additional = vehicle.AdditionalData is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(vehicle.AdditionalData);
        additional["source_title_raw"] = JsonSerializer.SerializeToElement(rawTitle);
        additional["title_category"] = JsonSerializer.SerializeToElement(descriptor.Category);
        additional["title_flags"] = JsonSerializer.SerializeToElement(descriptor.Flags);
        additional["title_review_status"] = JsonSerializer.SerializeToElement(reviewStatus);
        additional["title_taxonomy_version"] = JsonSerializer.SerializeToElement(TaxonomyVersion);

        return vehicle with
        {
            TitleNotes = JsonSerializer.SerializeToElement(notes),
            AdditionalData = additional
        };
    }

    public static string? ReadCode(AuctionVehicle vehicle)
    {
        if (vehicle.RawSource is { ValueKind: JsonValueKind.Object } raw &&
            raw.TryGetProperty("Sale Title Type", out var rawCode) && rawCode.ValueKind == JsonValueKind.String)
            return Normalize(rawCode.GetString());
        if (vehicle.AdditionalData is not null && vehicle.AdditionalData.TryGetValue("source_title_type_code", out var additionalCode) && additionalCode.ValueKind == JsonValueKind.String)
            return Normalize(additionalCode.GetString());
        if (vehicle.TitleNotes is { ValueKind: JsonValueKind.Object } notes)
        {
            foreach (var key in new[] { "sale_title_type_code", "sale_title_type" })
            {
                if (notes.TryGetProperty(key, out var noteCode) && noteCode.ValueKind == JsonValueKind.String)
                    return Normalize(noteCode.GetString());
            }
        }
        return null;
    }

    private static bool IsCopart(AuctionVehicle vehicle) =>
        string.Equals(vehicle.Platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, string?> ReadObject(JsonElement? value)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (value is not { ValueKind: JsonValueKind.Object } objectValue) return values;
        foreach (var property in objectValue.EnumerateObject())
            values[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
        return values;
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized is "NULL" or "N/A" or "NA" ? null : normalized;
    }
}
