using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Lsc.Inventory.Api.Contracts;

namespace Lsc.Inventory.Api.Eligibility;

public sealed record EligibilityReason(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("explanation")] string Explanation,
    [property: JsonPropertyName("source_fields")] IReadOnlyList<string> SourceFields,
    [property: JsonPropertyName("observed_values")] IReadOnlyDictionary<string, object?> ObservedValues);

public sealed record EligibilityEvaluation(
    [property: JsonPropertyName("decision")] string Decision,
    [property: JsonPropertyName("load_to_system")] bool LoadToSystem,
    [property: JsonPropertyName("lot_number")] string? LotNumber,
    [property: JsonPropertyName("auction_source")] string? AuctionSource,
    [property: JsonPropertyName("vin_masked")] string? VinMasked,
    [property: JsonPropertyName("discard_reasons")] IReadOnlyList<EligibilityReason> DiscardReasons,
    [property: JsonPropertyName("flags")] IReadOnlyList<EligibilityReason> Flags,
    [property: JsonPropertyName("data_quality_notes")] IReadOnlyList<string> DataQualityNotes,
    [property: JsonPropertyName("evaluated_fields")] IReadOnlyList<string> EvaluatedFields,
    [property: JsonPropertyName("rule_version")] string RuleVersion);

public static partial class AuctionEligibilityEvaluator
{
    public const string RuleVersion = "filtro_elegibilidad_subasta_v4";

    private static readonly string[] BannedStates = ["WI", "AL", "MI"];
    private static readonly string[] BannedSellers = ["WHEELZY", "MARESTAR", "TITLEMAX", "CARBRAIN"];
    private static readonly string[] UnavailableTitlePhrases = ["NOT AVAILABLE", "UNAVAILABLE", "NOT AVAIL", "NO DISPONIBLE"];

    public static EligibilityEvaluation Evaluate(AuctionVehicle vehicle, DateTimeOffset? evaluatedAt = null)
    {
        var reasons = new List<EligibilityReason>();
        var flags = new List<EligibilityReason>();
        var dataQualityNotes = new List<string>();
        var evaluatedFields = new List<string>();
        var now = evaluatedAt ?? DateTimeOffset.UtcNow;

        var lotNumber = Original(vehicle.LotNumber);
        var vin = Original(vehicle.Vin);
        var saleDate = vehicle.Auction?.AuctionAt;
        var locationState = Original(vehicle.Location?.State ?? vehicle.Facility?.State);
        var sellerName = Original(vehicle.Seller?.Name);
        var primaryDamage = Original(vehicle.Condition?.PrimaryDamage ?? vehicle.Damage);
        var secondaryDamage = Original(vehicle.Condition?.SecondaryDamage);
        var titleLabel = Original(vehicle.SaleDocument?.Name);
        var titleNotes = FlattenJson(vehicle.TitleNotes);
        var specialNote = FlattenJson(vehicle.SpecialNote);
        var announcements = FlattenJson(vehicle.Announcements);

        AddEvaluated(evaluatedFields, "lot_number", lotNumber);
        AddEvaluated(evaluatedFields, "vin", vin);
        AddEvaluated(evaluatedFields, "year", vehicle.Year);
        AddEvaluated(evaluatedFields, "sale_date", saleDate);
        AddEvaluated(evaluatedFields, "location_state", locationState);
        AddEvaluated(evaluatedFields, "seller_name", sellerName);
        AddEvaluated(evaluatedFields, "damage_description", primaryDamage);
        AddEvaluated(evaluatedFields, "secondary_damage", secondaryDamage);
        AddEvaluated(evaluatedFields, "sale_title_type_label", titleLabel);
        AddEvaluated(evaluatedFields, "sale_document.is_pending", vehicle.SaleDocument?.IsPending);
        AddEvaluated(evaluatedFields, "title_notes", titleNotes);
        AddEvaluated(evaluatedFields, "special_note", specialNote);
        AddEvaluated(evaluatedFields, "announcements", announcements);

        if (string.IsNullOrWhiteSpace(lotNumber) || !lotNumber.All(char.IsDigit))
            reasons.Add(Reason("Q01", "Lote inválido", "El número de lote no fue informado o no puede normalizarse como identificador numérico.", ["lot_number"], ("lot_number", lotNumber)));

        var maxYear = now.Year + 1;
        if (vehicle.Year is null || vehicle.Year < 1900 || vehicle.Year > maxYear)
            reasons.Add(Reason("Q04", "Año inválido", $"El año debe estar entre 1900 y {maxYear}.", ["year"], ("year", vehicle.Year)));

        if (IsMissing(vin))
        {
            reasons.Add(Reason("D00A", "VIN faltante", "El VIN no fue informado por la subasta.", ["vin"], ("vin", null)));
        }
        else if (vehicle.Year is >= 1981 && !VinValidator.IsValidModernVin(vin!))
        {
            reasons.Add(Reason("D00C", "VIN estructuralmente inválido", "El VIN moderno no cumple longitud, caracteres permitidos o check digit.", ["vin", "year"], ("vin", MaskVin(vin)), ("year", vehicle.Year)));
        }
        else if (vehicle.Year is <= 1980)
        {
            if (VinValidator.IsValidLegacyVin(vin!))
                flags.Add(Reason("M00", "VIN legacy", "El vehículo usa un VIN anterior al estándar moderno de 17 caracteres.", ["vin", "year"], ("vin", MaskVin(vin)), ("year", vehicle.Year)));
            else
                reasons.Add(Reason("D00C", "VIN legacy inválido", "El VIN legacy contiene una longitud o caracteres no aceptados.", ["vin", "year"], ("vin", MaskVin(vin)), ("year", vehicle.Year)));
        }

        if (saleDate is null)
            reasons.Add(Reason("D00B", "Fecha de venta faltante", "La fecha de subasta no fue informada o no es válida.", ["sale_date"], ("sale_date", null)));
        else if (saleDate.Value.Date < now.Date)
            reasons.Add(Reason("D00D", "Fecha de venta pasada", "La fecha de venta es anterior al día corriente y el lote no debe permanecer activo.", ["sale_date"], ("sale_date", saleDate)));

        var normalizedState = Normalize(locationState);
        if (BannedStates.Contains(normalizedState, StringComparer.Ordinal))
            reasons.Add(Reason("D01", "Ubicación vetada", $"La yarda está ubicada en el estado vetado {normalizedState}.", ["location_state"], ("location_state", locationState)));

        var normalizedSeller = Normalize(sellerName);
        var matchedSeller = BannedSellers.FirstOrDefault(name => ContainsPhrase(normalizedSeller, name));
        if (matchedSeller is not null)
            reasons.Add(Reason("D02", "Vendedor vetado", $"El vendedor declarado contiene {matchedSeller}.", ["seller_name"], ("seller_name", sellerName)));

        var damageFields = new[] { new FieldValue("damage_description", primaryDamage), new FieldValue("secondary_damage", secondaryDamage) };
        AddDamageReason(reasons, "D03", "Daño de bajos", "UNDERCARRIAGE", damageFields);
        AddDamageReason(reasons, "D04", "Quemado", "BURN", damageFields);
        AddDamageReason(reasons, "D05", "Inundado", "FLOOD", damageFields);
        AddDamageReason(reasons, "D06", "Daño de chasis declarado", "FRAME DAMAGE", damageFields);

        var vinDamageMatches = damageFields.Where(field => ContainsPhrase(Normalize(field.Value), "MISSING ALTERED VIN") || ContainsPhrase(Normalize(field.Value), "REPLACED VIN")).ToArray();
        if (vinDamageMatches.Length > 0)
            reasons.Add(ReasonFromMatches("D07", "VIN faltante, alterado o reemplazado declarado", "El daño declarado informa un VIN faltante, alterado o reemplazado.", vinDamageMatches));
        AddDamageReason(reasons, "D08", "Riesgo biológico o químico", "BIOHAZARD CHEMICAL", damageFields);

        var titleFields = new[]
        {
            new FieldValue("sale_title_type_label", titleLabel),
            new FieldValue("title_notes", titleNotes),
            new FieldValue("special_note", specialNote),
            new FieldValue("announcements", announcements)
        };
        var pendingMatches = titleFields.Where(field => ContainsPhrase(Normalize(field.Value, true), "PENDING TITLE")).ToList();
        if (vehicle.SaleDocument?.IsPending == true) pendingMatches.Add(new FieldValue("sale_document.is_pending", "true"));
        var repoMatches = titleFields.Where(field => ContainsPhrase(Normalize(field.Value, true), "REPO AFFIDAVIT")).ToArray();
        var duplicateMatches = titleFields.Where(field => ContainsPhrase(Normalize(field.Value, true), "DUPLICATE TITLE")).ToArray();
        var unavailableMatches = titleFields.Where(field => UnavailableTitlePhrases.Any(phrase => ContainsPhrase(Normalize(field.Value, true), phrase))).ToArray();
        var d10Matches = pendingMatches.Concat(repoMatches).ToList();
        if (duplicateMatches.Length > 0 && unavailableMatches.Length > 0)
        {
            d10Matches.AddRange(duplicateMatches);
            d10Matches.AddRange(unavailableMatches);
        }
        if (d10Matches.Count > 0)
            reasons.Add(ReasonFromMatches("D10", "Título que impide titular", "La documentación oficial declara un título pendiente, un duplicate title no disponible o un repo affidavit.", d10Matches.DistinctBy(match => match.Name).ToArray()));

        if (IsMissing(sellerName))
        {
            dataQualityNotes.Add("El proveedor no informó el nombre del vendedor; D02 no se activa sin evidencia explícita.");
            flags.Add(Reason("M01", "Vendedor no divulgado", "La fuente no informó el vendedor; D02 no pudo evaluarse.", ["seller_name"], ("seller_name", sellerName)));
        }
        if (titleNotes is null && specialNote is null && announcements is null)
            dataQualityNotes.Add("El proveedor no informó notas o anuncios de título; D10 se evaluó solo con el documento disponible.");
        if (vehicle.Platform?.Equals("copart", StringComparison.OrdinalIgnoreCase) == true && IsCopartTitleUnmapped(vehicle))
            flags.Add(Reason("M02", "Código de título sin mapa", "El código de título Copart no tiene una equivalencia oficial aprobada; el lote no se descarta por ello.", ["source_title_type_code", "title_mapping_status"], ("source_title_type_code", CopartTitleCode(vehicle)), ("title_mapping_status", "unmapped")));
        if (vehicle.Condition?.HasKey == false)
            flags.Add(Reason("M03", "Sin llaves", "La subasta declara que el vehículo no tiene llaves.", ["condition.has_key"], ("condition.has_key", false)));

        var runCondition = Original(vehicle.Condition?.RunCondition?.Normalized ?? vehicle.Condition?.RunCondition?.Raw);
        if (!string.Equals(runCondition, "RUNS_AND_DRIVES", StringComparison.Ordinal) &&
            !string.Equals(runCondition, "RUNS AND DRIVES", StringComparison.Ordinal))
            flags.Add(Reason("M04", "Marcha no verificada", "La fuente no declara Runs and Drives.", ["condition.run_condition"], ("condition.run_condition", runCondition)));
        if (vehicle.Odometer is null or <= 0 || ContainsPhrase(Normalize(vehicle.OdometerInfo?.Status), "NOT ACTUAL"))
            flags.Add(Reason("M05", "Odómetro no verificado", "El odómetro está ausente, es cero o no está declarado como actual.", ["odometer.mi", "odometer.status"], ("odometer.mi", vehicle.Odometer), ("odometer.status", vehicle.OdometerInfo?.Status)));
        if (vehicle.Media?.Photos is not { Count: > 0 })
            flags.Add(Reason("M06", "Sin thumbnail", "La fuente no entregó una imagen utilizable.", ["media.thumbs"], ("media.thumbs_count", vehicle.Media?.Photos?.Count ?? 0)));

        var auctionTerms = Normalize($"{vehicle.Auction?.State} {vehicle.Auction?.LotStatus} {vehicle.Auction?.LotSubStatus}");
        if (ContainsPhrase(auctionTerms, "ON APPROVAL") || ContainsPhrase(auctionTerms, "ON MINIMUM BID"))
            flags.Add(Reason("M07", "Venta condicional", "El lote está sujeto a aprobación o puja mínima.", ["auction.state", "auction.lot_status", "auction.lot_sub_status"], ("auction_terms", auctionTerms)));
        if (IsMissing(vehicle.Model) || Normalize(vehicle.Model) is "ALL MODELS" or "UNKNOWN")
            flags.Add(Reason("M08", "Modelo sin resolver", "El modelo no pudo resolverse sin inventar información.", ["model"], ("model", vehicle.Model)));

        var orderedReasons = reasons.OrderBy(reason => reason.Code, StringComparer.Ordinal).ToArray();
        var orderedFlags = flags.OrderBy(flag => flag.Code, StringComparer.Ordinal).ToArray();
        var hasQuarantine = orderedReasons.Any(reason => reason.Code.StartsWith('Q'));
        var decision = hasQuarantine ? "CUARENTENA" : orderedReasons.Length > 0 ? "DESCARTAR" : orderedFlags.Length > 0 ? "MARCAR" : "CARGAR";

        return new EligibilityEvaluation(decision, orderedReasons.Length == 0, lotNumber, Original(vehicle.Platform), MaskVin(vin), orderedReasons, orderedFlags, dataQualityNotes, evaluatedFields, RuleVersion);
    }

    private static void AddDamageReason(List<EligibilityReason> reasons, string code, string name, string phrase, IReadOnlyList<FieldValue> fields)
    {
        var matches = fields.Where(field => ContainsPhrase(Normalize(field.Value), phrase)).ToArray();
        if (matches.Length > 0) reasons.Add(ReasonFromMatches(code, name, $"El daño declarado contiene {phrase}.", matches));
    }

    private static EligibilityReason ReasonFromMatches(string code, string name, string explanation, IReadOnlyList<FieldValue> matches) =>
        new(code, name, explanation, matches.Select(match => match.Name).Distinct().ToArray(), matches.GroupBy(match => match.Name).ToDictionary(group => group.Key, group => (object?)group.First().Value));

    private static EligibilityReason Reason(string code, string name, string explanation, IReadOnlyList<string> sourceFields, params (string Name, object? Value)[] values) =>
        new(code, name, explanation, sourceFields, values.ToDictionary(value => value.Name, value => value.Value));

    private static string? CopartTitleCode(AuctionVehicle vehicle)
    {
        if (vehicle.AdditionalData is not null && vehicle.AdditionalData.TryGetValue("source_title_type_code", out var code) && code.ValueKind == JsonValueKind.String)
            return Original(code.GetString());
        return JsonProperty(vehicle.TitleNotes, "sale_title_type_code") ?? JsonProperty(vehicle.TitleNotes, "sale_title_type");
    }

    private static bool IsCopartTitleUnmapped(AuctionVehicle vehicle)
    {
        if (vehicle.AdditionalData is not null && vehicle.AdditionalData.TryGetValue("source_title_mapping", out var status) && status.ValueKind == JsonValueKind.String)
            return string.Equals(status.GetString(), "unmapped", StringComparison.OrdinalIgnoreCase);
        return IsMissing(CopartTitleCode(vehicle));
    }

    private static string? JsonProperty(JsonElement? value, string name)
    {
        if (value is not { ValueKind: JsonValueKind.Object } objectValue || !objectValue.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return null;
        return Original(property.GetString());
    }

    private static string? Original(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool IsMissing(string? value)
    {
        var normalized = Normalize(value, true);
        return normalized is "" or "N A" or "NA" or "UNKNOWN" or "NOT AVAILABLE";
    }

    private static string Normalize(string? value, bool preserveUnavailable = false)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = WhitespaceRegex().Replace(SeparatorRegex().Replace(value.Trim().ToUpperInvariant(), " "), " ");
        return !preserveUnavailable && normalized is "N A" or "NA" or "UNKNOWN" or "NOT AVAILABLE" ? string.Empty : normalized;
    }

    private static bool ContainsPhrase(string normalizedValue, string normalizedPhrase) =>
        !string.IsNullOrWhiteSpace(normalizedValue) && $" {normalizedValue} ".Contains($" {normalizedPhrase} ", StringComparison.Ordinal);

    private static void AddEvaluated(List<string> fields, string name, object? value)
    {
        if (value is not null && !string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture))) fields.Add(name);
    }

    private static string? FlattenJson(JsonElement? value)
    {
        if (value is null) return null;
        var strings = new List<string>();
        CollectStrings(value.Value, strings);
        return strings.Count == 0 ? null : string.Join(" | ", strings);
    }

    private static void CollectStrings(JsonElement element, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                if (!string.IsNullOrWhiteSpace(value)) values.Add(value);
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectStrings(item, values);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject()) CollectStrings(property.Value, values);
                break;
        }
    }

    private static string? MaskVin(string? vin) => string.IsNullOrWhiteSpace(vin) ? null : $"***{vin.Trim()[Math.Max(0, vin.Trim().Length - 4)..]}";
    private sealed record FieldValue(string Name, string? Value);

    [GeneratedRegex("[-/_,.]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRegex();

    [GeneratedRegex("\\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRegex();
}

public static class VinValidator
{
    private static readonly Dictionary<char, int> Transliteration = new()
    {
        ['A']=1,['B']=2,['C']=3,['D']=4,['E']=5,['F']=6,['G']=7,['H']=8,
        ['J']=1,['K']=2,['L']=3,['M']=4,['N']=5,['P']=7,['R']=9,
        ['S']=2,['T']=3,['U']=4,['V']=5,['W']=6,['X']=7,['Y']=8,['Z']=9
    };
    private static readonly int[] Weights = [8,7,6,5,4,3,2,10,0,9,8,7,6,5,4,3,2];

    public static bool IsValidModernVin(string vin)
    {
        var normalized = vin.Trim().ToUpperInvariant();
        if (normalized.Length != 17 || normalized.IndexOfAny(['I','O','Q']) >= 0 || normalized.Any(character => !char.IsDigit(character) && !Transliteration.ContainsKey(character))) return false;
        var sum = 0;
        for (var index = 0; index < normalized.Length; index++)
        {
            var value = char.IsDigit(normalized[index]) ? normalized[index] - '0' : Transliteration[normalized[index]];
            sum += value * Weights[index];
        }
        var remainder = sum % 11;
        return normalized[8] == (remainder == 10 ? 'X' : (char)('0' + remainder));
    }

    public static bool IsValidLegacyVin(string vin)
    {
        var normalized = vin.Trim().ToUpperInvariant();
        return normalized.Length is >= 5 and <= 17 && normalized.All(char.IsLetterOrDigit);
    }
}
