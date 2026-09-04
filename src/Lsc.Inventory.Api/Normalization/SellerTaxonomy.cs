namespace Lsc.Inventory.Api.Normalization;

public sealed record SellerClassification(
    string Category,
    decimal Confidence,
    bool NeedsReview,
    string Evidence);

/// <summary>
/// Categoría operativa común para filtros y scoring. La clasificación derivada
/// es inclusiva: sirve para descubrir vehículos y conserva marca de revisión.
/// </summary>
public static class SellerTaxonomy
{
    public const string Version = "seller_taxonomy_v2";
    public const decimal InclusionThreshold = 0.50m;
    public const string Insurance = "insurance";
    public const string Dealer = "dealer";
    public const string Finance = "finance";
    public const string RentalFleet = "rental_fleet";
    public const string Government = "government";
    public const string RepossessionBank = "repossession_bank";
    public const string Other = "other";
    public const string Unknown = "unknown";
    public const string Unclassified = "unclassified";

    public static string Normalize(string? sourceType)
    {
        if (string.IsNullOrWhiteSpace(sourceType)) return Unclassified;
        return ClassifyValue(sourceType) ?? Other;
    }

    public static SellerClassification ClassifyDetailed(string? rawType, string? rawClass, string? rawTextClass, string? sellerName)
    {
        foreach (var evidence in new[]
        {
            (Value: rawType, Source: "raw_type"),
            (Value: rawClass, Source: "class"),
            (Value: rawTextClass, Source: "text_class")
        })
        {
            var category = ClassifyValue(evidence.Value);
            if (category is not null && category != Unknown && category != Unclassified)
                return new SellerClassification(category, 1.00m, false, evidence.Source);
        }

        var name = NormalizeText(sellerName);
        if (string.IsNullOrWhiteSpace(name))
            return new SellerClassification(Unknown, 0m, true, "missing_name");

        var nameClassification = ClassifyName(name);
        if (nameClassification is not null) return nameClassification;

        var hadUnknownEvidence = new[] { rawType, rawClass, rawTextClass }.Any(value => ClassifyValue(value) == Unknown);
        return new SellerClassification(hadUnknownEvidence ? Unknown : Other, hadUnknownEvidence ? 0.25m : 0.35m, true, "name_unmatched");
    }

    private static string? ClassifyValue(string? value)
    {
        var normalized = NormalizeText(value);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (normalized is "UNKNOWN" or "UNAVAILABLE" or "NO INFORMATION" or "NO INFO" or "NOT REPORTED" or "N/A" or "NA") return Unknown;
        return ClassifyKnown(normalized);
    }

    private static string? ClassifyKnown(string normalized)
    {
        if (ContainsAny(normalized, "INSURANCE", "INSURER", "CASUALTY", "CLAIMS", "INDEMNITY", "UNDERWRIT")) return Insurance;
        if (ContainsAny(normalized, "DEALER", "AUTO GROUP", "MOTOR GROUP")) return Dealer;
        if (ContainsAny(normalized, "RENTAL", "FLEET", "RENT A CAR")) return RentalFleet;
        if (ContainsAny(normalized, "REPOSSESSION", "REPO", "BANK", "CREDIT UNION", "LENDER")) return RepossessionBank;
        if (ContainsAny(normalized, "FINANCE", "FINANCIAL", "LEASING", "MOTOR CREDIT", "AUTO CREDIT")) return Finance;
        if (ContainsAny(normalized, "GOVERNMENT", "GOVT", "MUNICIPAL", "COUNTY", "CITY")) return Government;
        if (normalized == "OTHER") return Other;
        return null;
    }

    private static SellerClassification? ClassifyName(string normalized)
    {
        var category = ClassifyKnown(normalized);
        return category is null ? null : new SellerClassification(category, 0.75m, true, "seller_name_pattern");
    }

    private static bool ContainsAny(string value, params string[] terms) => terms.Any(value.Contains);
    private static string NormalizeText(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;
}
