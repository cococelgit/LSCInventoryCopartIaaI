namespace Lsc.Inventory.Api.Normalization;

public static class SellerTaxonomy
{
    public const string Version = "seller_taxonomy_v1";
    public const string Insurance = "insurance";
    public const string Dealer = "dealer";
    public const string Finance = "finance";
    public const string RentalFleet = "rental_fleet";
    public const string Government = "government";
    public const string RepossessionBank = "repossession_bank";
    public const string Other = "other";
    public const string Unknown = "unknown";
    public const string Unclassified = "unclassified";

    public static string Classify(string? rawType, string? rawClass, string? rawTextClass, string? sellerName)
    {
        string? unknownEvidence = null;
        foreach (var evidence in new[] { rawType, rawClass, rawTextClass })
        {
            var category = ClassifyEvidence(evidence);
            if (category is null) continue;
            if (category == Unknown)
            {
                unknownEvidence ??= category;
                continue;
            }
            return category;
        }

        var name = Normalize(sellerName);
        if (string.IsNullOrWhiteSpace(name)) return unknownEvidence ?? Unclassified;
        if (ContainsAny(name, "INSURANCE", "INSURER", "CASUALTY", "INDEMNITY", "FARM BUREAU", "GEICO", "PROGRESSIVE", "TRAVELERS", "ALLSTATE", "STATE FARM", "NATIONWIDE")) return Insurance;
        if (ContainsAny(name, "RENT A CAR", "RENTAL", "FLEET", "U-HAUL", "RYDER", "HERTZ", "ENTERPRISE", "AVIS", "BUDGET")) return RentalFleet;
        if (ContainsAny(name, "BANK", "CREDIT UNION", "LENDER", "REPOSSESSION", "REPO ", "CHASE", "WESTLAKE", "ALLY")) return RepossessionBank;
        if (ContainsAny(name, "FINANCE", "FINANCIAL", "LEASING", "CREDIT CORPORATION", "MOTOR CREDIT", "AUTO CREDIT")) return Finance;
        if (ContainsAny(name, "GOVERNMENT", "GOVT", "MUNICIPAL", "COUNTY", "CITY ", "STATE OF ")) return Government;
        if (ContainsAny(name, "DEALER", "AUTO GROUP", "MOTOR GROUP", "MOTORS LLC", "MOTOR SALES")) return Dealer;
        return unknownEvidence ?? Other;
    }

    private static string? ClassifyEvidence(string? value)
    {
        var normalized = Normalize(value);
        if (string.IsNullOrWhiteSpace(normalized)) return null;
        if (normalized is "UNKNOWN" or "UNAVAILABLE" or "NO INFORMATION" or "NO INFO" or "NOT REPORTED" or "N/A" or "NA") return Unknown;
        if (ContainsAny(normalized, "INSURANCE", "INSURER", "CASUALTY", "INDEMNITY")) return Insurance;
        if (ContainsAny(normalized, "RENTAL", "FLEET")) return RentalFleet;
        if (ContainsAny(normalized, "REPOSSESSION", "REPO", "BANK", "CREDIT UNION", "LENDER")) return RepossessionBank;
        if (ContainsAny(normalized, "FINANCE", "FINANCIAL", "LEASING", "MOTOR CREDIT", "AUTO CREDIT")) return Finance;
        if (ContainsAny(normalized, "GOVERNMENT", "GOVT", "MUNICIPAL")) return Government;
        if (ContainsAny(normalized, "DEALER", "AUTO GROUP", "MOTOR GROUP")) return Dealer;
        return null;
    }

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(' ', value.Trim().ToUpperInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool ContainsAny(string value, params string[] phrases) => phrases.Any(value.Contains);
}
