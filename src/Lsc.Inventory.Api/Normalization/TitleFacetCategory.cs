using Lsc.Inventory.Api.Contracts;
using System.Text.RegularExpressions;

namespace Lsc.Inventory.Api.Normalization;

/// <summary>
/// Preserves the provider document while exposing a compact operational category.
/// The explicit code dictionary is approved only for Copart Excel values; IAAI remains text-based.
/// </summary>
public static partial class TitleFacetCategory
{
    public const string Clean = "CLEAN";
    public const string Salvage = "SALVAGE";
    public const string Rebuilt = "REBUILT";
    public const string Special = "SPECIAL";
    public const string Unverified = "UNVERIFIED";
    public const string Other = "OTHER";

    public sealed record TitleDocumentDescriptor(string Category, string DisplayLabel, IReadOnlyList<string> Flags)
    {
        public bool DefaultVisible => !string.Equals(Category, Special, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly IReadOnlyDictionary<string, TitleDocumentDescriptor> CopartCodeMap = CreateCopartCodeMap();

    public static string SourceTitle(AuctionVehicle vehicle)
    {
        if (!string.IsNullOrWhiteSpace(vehicle.SaleDocument?.Name)) return vehicle.SaleDocument.Name.Trim();
        if (string.Equals(vehicle.Platform, "copart", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(vehicle.Title)) return vehicle.Title.Trim();
        return "NO REPORTADO";
    }

    public static string Classify(AuctionVehicle vehicle) => Describe(vehicle).Category;

    public static string Classify(string? sourceTitle) => Classify(null, sourceTitle);

    public static string Classify(string? platform, string? sourceTitle) => Describe(platform, sourceTitle).Category;

    public static TitleDocumentDescriptor Describe(AuctionVehicle vehicle) => Describe(vehicle.Platform, SourceTitle(vehicle));

    public static TitleDocumentDescriptor Describe(string? platform, string? sourceTitle)
    {
        var normalized = Normalize(sourceTitle);
        if (string.Equals(platform, "copart", StringComparison.OrdinalIgnoreCase) && CopartCodeMap.TryGetValue(normalized, out var mapped)) return mapped;
        return DescribeText(normalized, sourceTitle);
    }

    public static bool IsSpecial(string? category) => string.Equals(category, Special, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Uses the same explicit Copart code dictionary as the incremental projection when historical rows are rebuilt.
    /// Inputs are SQL identifiers/expressions owned by the engine, never user data.
    /// </summary>
    public static string BuildSqlCaseExpression(string normalizedSourceExpression, string normalizedPlatformExpression)
    {
        static string List(IEnumerable<string> values) => string.Join(", ", values.Order(StringComparer.Ordinal).Select(value => $"'{value.Replace("'", "''", StringComparison.Ordinal)}'"));
        var specialCodes = List(CopartCodeMap.Where(item => item.Value.Category == Special).Select(item => item.Key));
        var rebuiltCodes = List(CopartCodeMap.Where(item => item.Value.Category == Rebuilt).Select(item => item.Key));
        var salvageCodes = List(CopartCodeMap.Where(item => item.Value.Category == Salvage).Select(item => item.Key));
        var cleanCodes = List(CopartCodeMap.Where(item => item.Value.Category == Clean).Select(item => item.Key));
        var otherCodes = List(CopartCodeMap.Where(item => item.Value.Category == Other).Select(item => item.Key));

        return $"""
            case
                when {normalizedPlatformExpression} = 'copart' and {normalizedSourceExpression} in ({specialCodes}) then 'SPECIAL'
                when {normalizedPlatformExpression} = 'copart' and {normalizedSourceExpression} in ({rebuiltCodes}) then 'REBUILT'
                when {normalizedPlatformExpression} = 'copart' and {normalizedSourceExpression} in ({salvageCodes}) then 'SALVAGE'
                when {normalizedPlatformExpression} = 'copart' and {normalizedSourceExpression} in ({cleanCodes}) then 'CLEAN'
                when {normalizedPlatformExpression} = 'copart' and {normalizedSourceExpression} in ({otherCodes}) then 'OTHER'
                when {normalizedSourceExpression} = 'SPECIAL' then 'SPECIAL'
                when {normalizedSourceExpression} ~ '(^| )(CERTIFICATE OF DESTRUCTION|JUNK|NON REPAIRABLE|PARTS ONLY|SCRAP|CRUSHED|DESTROYED)( |$)' then 'SPECIAL'
                when {normalizedSourceExpression} ~ '(^| )(REBUILT|RECONSTRUCTED|RECON)( |$)' then 'REBUILT'
                when {normalizedSourceExpression} ~ '(^| )(SALVAGE|REBUILDABLE)( |$)' then 'SALVAGE'
                when {normalizedSourceExpression} ~ '(^| )(CLEAR|CLEAN)( |$)' then 'CLEAN'
                when {normalizedSourceExpression} in ('', 'UNKNOWN', 'NO REPORTADO', 'NOT REPORTED', 'N A', 'NA') then 'UNVERIFIED'
                else 'OTHER'
            end
            """;
    }

    private static TitleDocumentDescriptor DescribeText(string normalized, string? sourceTitle)
    {
        var source = string.IsNullOrWhiteSpace(sourceTitle) ? "NO REPORTADO" : sourceTitle.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized is "UNKNOWN" or "NO REPORTADO" or "NOT REPORTED" or "N A" or "NA") return new(Unverified, "Documento por verificar", []);
        if (normalized == Special) return new(Special, source, ["Especial"]);
        if (ContainsAny(normalized, "CERTIFICATE OF DESTRUCTION", "JUNK", "NON REPAIRABLE", "PARTS ONLY", "SCRAP", "CRUSHED", "DESTROYED")) return new(Special, source, ["Especial"]);
        if (ContainsAny(normalized, "REBUILT", "RECONSTRUCTED", "RECON")) return new(Rebuilt, source, ["Rebuilt"]);
        if (ContainsAny(normalized, "SALVAGE", "REBUILDABLE")) return new(Salvage, source, ["Salvage"]);
        if (ContainsAny(normalized, "CLEAR", "CLEAN")) return new(Clean, source, ["Clean"]);
        return new(Other, source, []);
    }

    private static IReadOnlyDictionary<string, TitleDocumentDescriptor> CreateCopartCodeMap()
    {
        var map = new Dictionary<string, TitleDocumentDescriptor>(StringComparer.OrdinalIgnoreCase);
        void Add(string category, string displayLabel, string codes, params string[] flags)
        {
            var descriptor = new TitleDocumentDescriptor(category, displayLabel, flags);
            foreach (var code in codes.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) map.Add(code, descriptor);
        }

        // Categories are sourced from the LSC title table. State-dependent variants remain OTHER by design.
        Add(Clean, "Clean Title", "AQ AV CC CF", "Clean");
        Add(Clean, "Clean Title · Theft Recovery", "CT", "Clean", "Theft Recovery");
        Add(Rebuilt, "Rebuilt Title", "AR BR CD JR MR R1 RD RG RH RP RR RT RV RW UR", "Rebuilt");
        Add(Rebuilt, "Rebuilt / Rebuildable document", "CR", "Rebuilt", "Rebuildable");
        Add(Special, "Certificate of Destruction", "AD", "Destroyed");
        Add(Special, "Junk / Scrap", "AT BC JK KC", "Junk", "Scrap");
        Add(Special, "Parts Only", "AM BP DP IP PC PP PS PV PX SP", "Parts Only");
        Add(Special, "Non-Repairable", "AN CQ NF NQ NU RU SN", "Non-Repairable");
        Add(Special, "Destroyed vehicle", "VD", "Destroyed");
        Add(Salvage, "Salvage Certificate", "AC AL AY BF BI BL BS BT BV CA CH CI CM CS CV CZ DA DC DE DL DM DN DQ DS DT DU DV DY DZ EN ET EU F1 FC FL FN FR FS FT FV GM GS HD HF HS HT IA IC IR JB JC JT KL KR KV LB LC LL LP LQ LS LU MB MF MS MT MU NB NH NR NS NT NX OA OH OS OT PB PD PL PO PT S1 S2 SB SC SD SF SH SK SL SM SQ SR SS ST SV SW TA TB TC TE TH TL TR TS UC UL UN US UT WD WF WS WT", "Salvage");
        Add(Salvage, "Salvage Certificate · Rebuildable", "DR", "Salvage", "Rebuildable");
        Add(Salvage, "Salvage Certificate · Flood/Rebuildable", "RF", "Salvage", "Flood", "Rebuildable");
        Add(Salvage, "Salvage Certificate · Restored", "R2 RA RB RC RS", "Salvage", "Restored");
        Add(Other, "Documento por revisar", "B1 B2 B3 C1 C4 D1 D2", "State-dependent");
        Add(Other, "Flood Title", "CW", "Water Damage");
        Add(Other, "Documento de propiedad", "BB BE C0 CE CL CO", "Document review");

        return map;
    }

    private static bool ContainsAny(string normalized, params string[] phrases) => phrases.Any(phrase => $" {normalized} ".Contains($" {phrase} ", StringComparison.Ordinal));

    private static string Normalize(string? value) => Whitespace().Replace(Separators().Replace((value ?? string.Empty).Trim().ToUpperInvariant(), " "), " ").Trim();

    [GeneratedRegex("[-/_,.]+")]
    private static partial Regex Separators();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();
}
