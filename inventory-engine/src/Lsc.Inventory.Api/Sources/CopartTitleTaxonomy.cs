using System.Collections.Frozen;

namespace Lsc.Inventory.Api.Sources;

/// <summary>
/// Classifies the user-approved Copart title-code catalog for search and disclosure only.
/// It never decides whether a lot is eligible to load or whether it can be titled in a jurisdiction.
/// </summary>
public sealed record CopartTitleTaxonomyDefinition(
    string Category,
    IReadOnlyList<string> Flags,
    string ReviewStatus);

public static class CopartTitleTaxonomy
{
    public const string Version = "copart-title-taxonomy-v1";

    public const string Clean = "CLEAN";
    public const string BrandedTitle = "BRANDED_TITLE";
    public const string Salvage = "SALVAGE";
    public const string RebuiltReconstructed = "REBUILT_RECONSTRUCTED";
    public const string NonRepairablePartsScrap = "NON_REPAIRABLE_PARTS_SCRAP";
    public const string ExportOnly = "EXPORT_ONLY";
    public const string DocumentOnly = "DOCUMENT_ONLY";
    public const string StateVariantVerify = "STATE_VARIANT_VERIFY";
    public const string OtherUnverified = "OTHER_UNVERIFIED";

    private static readonly FrozenSet<string> CleanCodes = Codes("AQ", "AV", "CC");
    private static readonly FrozenSet<string> BrandedTitleCodes = Codes("AY", "CF", "CR", "CT", "CW");
    private static readonly FrozenSet<string> RebuiltCodes = Codes("AR", "BR", "CD", "JR", "MR", "OH", "R1", "R2", "RB", "RD", "RG", "RH", "RP", "RR", "RT", "RV", "RW", "UR");
    private static readonly FrozenSet<string> NonRepairableCodes = Codes("AD", "AM", "AN", "AT", "BC", "BP", "CQ", "DP", "KC", "NF", "NQ", "NU", "PC", "PS", "SN");
    private static readonly FrozenSet<string> DocumentOnlyCodes = Codes("BB", "C0", "CE", "CL", "CO");
    private static readonly FrozenSet<string> StateVariantCodes = Codes("B1", "B2", "B3", "C1", "C4", "D1", "D2");

    private static readonly FrozenSet<string> WaterFloodCodes = Codes("AY", "BL", "CW", "DY", "FL", "HF", "MF", "RF", "WD", "WF", "WS", "WT");
    private static readonly FrozenSet<string> FireCodes = Codes("BS", "FC", "MF");
    private static readonly FrozenSet<string> StructuralCodes = Codes("CS", "DS", "EU", "F1", "NQ", "NU", "SD", "SF", "SH", "SK", "SN", "SP", "SS", "SW", "US");
    private static readonly FrozenSet<string> TheftCodes = Codes("BI", "CT", "DT", "ET", "FT", "HT", "KL", "KV", "LS", "NH", "NS", "NT", "SB", "SC", "SL", "SM", "SQ", "SR", "ST", "TA", "TB", "TC", "TE", "TH", "TL", "TR", "TS", "UT");
    private static readonly FrozenSet<string> OdometerCodes = Codes("DN", "OT");
    private static readonly FrozenSet<string> LemonBuybackCodes = Codes("CM", "LB", "LC", "MB");
    private static readonly FrozenSet<string> MechanicalCodes = Codes("CV", "DM", "EN", "MT", "RT", "TE");
    private static readonly FrozenSet<string> DealerRestrictionCodes = Codes("DA");
    private static readonly FrozenSet<string> ReviewRequiredCodes = Codes("LU", "OS", "UC", "UL", "UN", "UR", "US", "UT");

    public static CopartTitleTaxonomyDefinition Resolve(string? code, bool isMapped)
    {
        var normalizedCode = Normalize(code);
        if (normalizedCode is null || !isMapped)
            return new CopartTitleTaxonomyDefinition(OtherUnverified, ["TITLE_CODE_UNMAPPED"], "DOCUMENT_REVIEW");

        var category = ResolveCategory(normalizedCode);
        var flags = ResolveFlags(normalizedCode);
        return new CopartTitleTaxonomyDefinition(category, flags, ResolveReviewStatus(category, flags));
    }

    private static string ResolveCategory(string code)
    {
        if (string.Equals(code, "BE", StringComparison.Ordinal)) return ExportOnly;
        if (StateVariantCodes.Contains(code)) return StateVariantVerify;
        if (NonRepairableCodes.Contains(code)) return NonRepairablePartsScrap;
        if (DocumentOnlyCodes.Contains(code)) return DocumentOnly;
        if (RebuiltCodes.Contains(code)) return RebuiltReconstructed;
        if (BrandedTitleCodes.Contains(code)) return BrandedTitle;
        if (CleanCodes.Contains(code)) return Clean;
        return Salvage;
    }

    private static IReadOnlyList<string> ResolveFlags(string code)
    {
        var flags = new List<string>(4);
        if (WaterFloodCodes.Contains(code)) flags.Add("WATER_FLOOD");
        if (FireCodes.Contains(code)) flags.Add("FIRE");
        if (StructuralCodes.Contains(code)) flags.Add("STRUCTURAL_FRAME_UNIBODY");
        if (TheftCodes.Contains(code)) flags.Add("THEFT");
        if (OdometerCodes.Contains(code)) flags.Add("ODOMETER");
        if (LemonBuybackCodes.Contains(code)) flags.Add("LEMON_MANUFACTURER_BUYBACK");
        if (MechanicalCodes.Contains(code)) flags.Add("MECHANICAL");
        if (DealerRestrictionCodes.Contains(code)) flags.Add("DEALER_RESTRICTION");
        if (StateVariantCodes.Contains(code) || ReviewRequiredCodes.Contains(code)) flags.Add("TITLE_REVIEW_REQUIRED");
        return flags;
    }

    private static string ResolveReviewStatus(string category, IReadOnlyList<string> flags) =>
        category is NonRepairablePartsScrap or ExportOnly or DocumentOnly or StateVariantVerify or OtherUnverified
            ? "DOCUMENT_REVIEW"
            : flags.Contains("DEALER_RESTRICTION", StringComparer.Ordinal) || flags.Contains("TITLE_REVIEW_REQUIRED", StringComparer.Ordinal)
                ? "ADVISOR_REVIEW"
                : "STANDARD";

    private static FrozenSet<string> Codes(params string[] codes) =>
        codes.ToFrozenSet(StringComparer.Ordinal);

    private static string? Normalize(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null : code.Trim().ToUpperInvariant();
}
