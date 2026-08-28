using System.Text.Json;
using Lsc.Inventory.Api.Contracts;

namespace Lsc.Inventory.Api.Sources;

/// <summary>
/// Applies the user-approved Copart title catalog without making an eligibility decision.
/// Unknown codes remain auditable as unmapped and are never discarded by this mapper.
/// </summary>
public static class CopartTitleMapper
{
    public static AuctionVehicle Apply(AuctionVehicle vehicle)
    {
        if (!string.Equals(vehicle.Platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase))
            return vehicle;

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

    private static Dictionary<string, string?> ReadObject(JsonElement? value)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (value is not { ValueKind: JsonValueKind.Object } objectValue) return values;
        foreach (var property in objectValue.EnumerateObject())
            values[property.Name] = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.GetRawText();
        return values;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();
}
