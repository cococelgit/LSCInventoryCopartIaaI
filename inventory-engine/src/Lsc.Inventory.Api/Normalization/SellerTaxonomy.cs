namespace Lsc.Inventory.Api.Normalization;

public sealed record SellerClassification(
    string Category,
    decimal Confidence,
    bool NeedsReview,
    string Evidence);

public static class SellerTaxonomy
{
    public const string Version = "seller_taxonomy_v2";
    public const decimal ReviewThreshold = 0.90m;
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

    public static string Classify(string? rawType, string? rawClass, string? rawTextClass, string? sellerName) =>
        ClassifyDetailed(rawType, rawClass, rawTextClass, sellerName).Category;

    public static SellerClassification ClassifyDetailed(string? rawType, string? rawClass, string? rawTextClass, string? sellerName)
    {
        string? unknownEvidenceSource = null;
        (string? Value, string Source)[] evidences = [(rawType, "raw_type"), (rawClass, "class"), (rawTextClass, "text_class")];
        foreach (var evidence in evidences)
        {
            var result = ClassifyEvidence(evidence.Value);
            if (result is null) continue;
            if (result.Category == Unknown)
            {
                unknownEvidenceSource ??= evidence.Source;
                continue;
            }
            return result with { Evidence = evidence.Source + ":" + result.Evidence };
        }

        var name = Normalize(sellerName);
        if (string.IsNullOrWhiteSpace(name))
            return new SellerClassification(Unknown, 0m, true, unknownEvidenceSource is null ? "missing_name" : unknownEvidenceSource + ":unknown_value");

        var nameResult = ClassifyName(name);
        if (nameResult is not null) return nameResult;
        return new SellerClassification(unknownEvidenceSource is null ? Other : Unknown, unknownEvidenceSource is null ? 0.35m : 0.25m, true, unknownEvidenceSource is null ? "name_unmatched" : unknownEvidenceSource + ":name_unmatched");
    }

    private static SellerClassification? ClassifyEvidence(string? value)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (normalized is "UNKNOWN" or "UNAVAILABLE" or "NO INFORMATION" or "NO INFO" or "NOT REPORTED" or "N/A" or "NA")
            return new SellerClassification(Unknown, 0m, true, "unknown_value");
        if (ContainsAny(normalized, "INSURANCE", "INSURER", "CASUALTY", "INDEMNITY")) return Strong(Insurance, "insurance_keyword");
        if (ContainsAny(normalized, "RENTAL", "FLEET")) return Strong(RentalFleet, "rental_fleet_keyword");
        if (ContainsAny(normalized, "REPOSSESSION", "REPO", "BANK", "CREDIT UNION", "LENDER")) return Strong(RepossessionBank, "lender_keyword");
        if (ContainsAny(normalized, "FINANCE", "FINANCIAL", "LEASING", "MOTOR CREDIT", "AUTO CREDIT")) return Strong(Finance, "finance_keyword");
        if (ContainsAny(normalized, "GOVERNMENT", "GOVT", "MUNICIPAL")) return Strong(Government, "government_keyword");
        if (ContainsAny(normalized, "DEALER", "AUTO GROUP", "MOTOR GROUP")) return Strong(Dealer, "dealer_keyword");
        return null;
    }

    private static SellerClassification? ClassifyName(string name)
    {
        if (ContainsAny(name, "INSURANCE", "INSURER", "CASUALTY", "INDEMNITY", "FARM BUREAU", "GEICO", "PROGRESSIVE", "TRAVELERS", "ALLSTATE", "STATE FARM", "NATIONWIDE")) return NameMatch(Insurance, "insurance_name");
        if (ContainsAny(name, "RENT A CAR", "RENTAL", "FLEET", "U-HAUL", "RYDER", "HERTZ", "ENTERPRISE", "AVIS", "BUDGET")) return NameMatch(RentalFleet, "rental_fleet_name");
        if (ContainsAny(name, "BANK", "CREDIT UNION", "LENDER", "REPOSSESSION", "REPO ", "CHASE", "WESTLAKE", "ALLY")) return NameMatch(RepossessionBank, "lender_name");
        if (ContainsAny(name, "FINANCE", "FINANCIAL", "LEASING", "CREDIT CORPORATION", "MOTOR CREDIT", "AUTO CREDIT")) return NameMatch(Finance, "finance_name");
        if (ContainsAny(name, "GOVERNMENT", "GOVT", "MUNICIPAL", "COUNTY", "CITY ", "STATE OF ")) return NameMatch(Government, "government_name");
        if (ContainsAny(name, "DEALER", "AUTO GROUP", "MOTOR GROUP", "MOTORS LLC", "MOTOR SALES")) return NameMatch(Dealer, "dealer_name");
        return null;
    }

    private static SellerClassification Strong(string category, string evidence) => new(category, 1.0m, false, evidence);
    private static SellerClassification NameMatch(string category, string evidence) => new(category, 0.75m, true, evidence);
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(' ', value.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    private static bool ContainsAny(string value, params string[] phrases) => phrases.Any(value.Contains);
}
