namespace Lsc.Inventory.Api.Normalization;

public static class RunConditionTaxonomy
{
    public const string RunsAndDrives = "RUNS_AND_DRIVES";
    public const string Starts = "STARTS";
    public const string Stationary = "STATIONARY";
    public const string Unverified = "UNVERIFIED";

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Unverified;

        var normalized = string.Join(' ', value.Trim().ToUpperInvariant()
            .Replace("&", " AND ", StringComparison.Ordinal)
            .Replace("/", " AND ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Replace("_", " ", StringComparison.Ordinal)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        if (normalized.Contains("RUNS AND DRIVES", StringComparison.Ordinal)
            || normalized.Contains("RUN AND DRIVE", StringComparison.Ordinal))
            return RunsAndDrives;
        if (normalized.Contains("START", StringComparison.Ordinal)) return Starts;
        if (normalized.Contains("STATIONARY", StringComparison.Ordinal)) return Stationary;
        if (normalized.Contains("NO INFORMATION", StringComparison.Ordinal)
            || normalized.Contains("NOT REPORTED", StringComparison.Ordinal)
            || normalized.Contains("UNKNOWN", StringComparison.Ordinal))
            return Unverified;
        return Unverified;
    }
}
