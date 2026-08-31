namespace Lsc.Inventory.Api.Normalization;

/// <summary>
/// Canonical seller categories used for filtering and reporting.  They classify
/// declared provider evidence only; an absent value remains unclassified.
/// </summary>
public static class SellerTaxonomy
{
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
        var normalized = sourceType?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized)) return Unclassified;

        if (normalized.Contains("insurance", StringComparison.Ordinal) || normalized.Contains("insurer", StringComparison.Ordinal) || normalized.Contains("casualty", StringComparison.Ordinal)) return Insurance;
        if (normalized.Contains("dealer", StringComparison.Ordinal) || normalized.Contains("auto group", StringComparison.Ordinal) || normalized.Contains("motor group", StringComparison.Ordinal)) return Dealer;
        if (normalized.Contains("repo", StringComparison.Ordinal) || normalized.Contains("bank", StringComparison.Ordinal) || normalized.Contains("credit union", StringComparison.Ordinal) || normalized.Contains("lender", StringComparison.Ordinal)) return RepossessionBank;
        if (normalized.Contains("finance", StringComparison.Ordinal) || normalized.Contains("financial", StringComparison.Ordinal) || normalized.Contains("leasing", StringComparison.Ordinal) || normalized.Contains("motor credit", StringComparison.Ordinal) || normalized.Contains("auto credit", StringComparison.Ordinal)) return Finance;
        if (normalized.Contains("rental", StringComparison.Ordinal) || normalized.Contains("fleet", StringComparison.Ordinal)) return RentalFleet;
        if (normalized.Contains("government", StringComparison.Ordinal) || normalized.Contains("govt", StringComparison.Ordinal) || normalized.Contains("municipal", StringComparison.Ordinal) || normalized.Contains("county", StringComparison.Ordinal) || normalized.Contains("city", StringComparison.Ordinal)) return Government;
        if (normalized is "unknown" or "unavailable" or "no information" or "no info" or "not reported" or "n/a" or "na") return Unknown;
        return Other;
    }
}
