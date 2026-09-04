using System.Security.Cryptography;
using System.Text;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Normalization;

namespace Lsc.Inventory.Api.Scoring;

public static class LscScoringPolicy
{
    public const string IAAIPolicyVersion = "lsc_pre_grade_v1";
    // Compatibility default for existing non-Copart callers; Copart resolves explicitly to v3.
    public const string Version = IAAIPolicyVersion;
    public const string CopartPolicyVersion = "lsc_pre_grade_v3_60";
    public const decimal PreGradeMaximumPoints = 60m;
    public const decimal CopartPreGradeMaximumPoints = 60m;
    public const decimal MinimumCoveragePercent = 70m;

    // Both platforms use the same 60-point numeric factor scale. Copart retains its
    // platform-specific audit version and advisory-flag behavior for source uncertainty.
    public static readonly IReadOnlySet<string> ManualReviewFlagCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "M02", // Copart title code without approved equivalence.
        "M04", // Runs and drives is not confirmed.
        "M07"  // Sale is conditional or subject to approval/minimum bid.
    };

    public static string ResolveVersion(string? platform) =>
        string.Equals(platform?.Trim(), "copart", StringComparison.OrdinalIgnoreCase)
            ? CopartPolicyVersion
            : IAAIPolicyVersion;
}

public sealed record LscScoringFactor(
    string Code,
    string Name,
    decimal Points,
    decimal MaxPointsEvaluable,
    bool Evaluated,
    string Explanation,
    IReadOnlyList<string> SourceFields);

public sealed record LscScoringPenalty(
    string Code,
    string Name,
    decimal Points,
    string Explanation,
    IReadOnlyList<string> SourceFields);

public sealed record LscVehicleScoringResult(
    string LotKey,
    string Platform,
    string Status,
    decimal? PreGrade,
    decimal? BuyScore,
    decimal MaxPointsEvaluable,
    decimal CoveragePercent,
    decimal ConfidencePercent,
    string? Category,
    IReadOnlyList<LscScoringFactor> Factors,
    IReadOnlyList<LscScoringPenalty> Penalties,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> MissingFields,
    string PolicyVersion,
    string InputHash,
    DateTimeOffset ScoredAt);

public static class LscVehicleScoringEngine
{
    private const string StatusDiscarded = "DISCARDED";
    private const string StatusManualReview = "MANUAL_REVIEW";
    private const string StatusNeedsEnrichment = "NEEDS_ENRICHMENT";
    private const string StatusPreGraded = "PRE_GRADED";
    private const string StatusPreGradedWithFlags = "PRE_GRADED_WITH_FLAGS";

    public static LscVehicleScoringResult Evaluate(AuctionVehicle vehicle, EligibilityEvaluation eligibility, DateTimeOffset? scoredAt = null)
    {
        var now = scoredAt ?? DateTimeOffset.UtcNow;
        var lotKey = BuildLotKey(vehicle);
        var platform = Normalize(vehicle.Platform);
        var policyVersion = LscScoringPolicy.ResolveVersion(platform);
        var isCopartV3 = string.Equals(policyVersion, LscScoringPolicy.CopartPolicyVersion, StringComparison.Ordinal);
        var inputHash = CreateInputHash(vehicle, eligibility);
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "profitability.total_cost",
            "profitability.net_resale",
            "demand.market_comparables",
            "demand.turnover"
        };

        if (string.Equals(eligibility.Decision, "DESCARTAR", StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(lotKey, platform, StatusDiscarded, eligibility.DiscardReasons.Select(reason => reason.Code), missing, inputHash, now,
                "El vehículo no supera el filtro determinístico de elegibilidad.");
        }

        if (string.Equals(eligibility.Decision, "CUARENTENA", StringComparison.OrdinalIgnoreCase))
        {
            return Blocked(lotKey, platform, StatusManualReview, eligibility.DiscardReasons.Select(reason => reason.Code), missing, inputHash, now,
                "El vehículo requiere revisión manual por una alerta de cuarentena.");
        }

        var manualFlags = eligibility.Flags
            .Where(flag => LscScoringPolicy.ManualReviewFlagCodes.Contains(flag.Code))
            .Select(flag => flag.Code)
            .ToArray();
        if (manualFlags.Length > 0 && !isCopartV3)
        {
            missing.Add("manual_review.resolution");
            return Blocked(lotKey, platform, StatusManualReview, manualFlags, missing, inputHash, now,
                "Una alerta material debe ser resuelta por un asesor antes de emitir un Pre-grado.", policyVersion);
        }
        if (manualFlags.Length > 0)
            missing.Add("manual_review.resolution");

        var factors = new List<LscScoringFactor>
        {
            EvaluateSeller(vehicle, missing),
            EvaluateMechanicalCondition(vehicle, missing),
            EvaluateDamage(vehicle, missing),
            EvaluateDocumentation(vehicle, missing),
            EvaluateInformationQuality(vehicle, missing)
        };

        // Copart uses the same 60-point factor scale as IAAI. The platform-specific
        // policy version remains distinct for auditability, but no 0-100 rescaling applies.
        var penalties = EvaluatePenalties(vehicle, eligibility, factors, missing);
        var factorPoints = factors.Sum(factor => factor.Points);
        var penaltyPoints = penalties.Sum(penalty => penalty.Points);
        var maxPointsEvaluable = factors.Sum(factor => factor.MaxPointsEvaluable);
        var coverageMaximum = LscScoringPolicy.PreGradeMaximumPoints;
        var coverage = RoundPercent(maxPointsEvaluable, coverageMaximum);
        var confidence = Math.Max(0m, coverage - penalties.Where(penalty => penalty.Code == "P04").Sum(penalty => Math.Abs(penalty.Points)));
        var isVisible = coverage >= LscScoringPolicy.MinimumCoveragePercent;
        var hasAdvisoryFlags = eligibility.Flags.Count > 0;
        decimal? preGrade = isCopartV3 || isVisible ? Math.Max(0m, factorPoints + penaltyPoints) : null;
        var status = isCopartV3
            ? hasAdvisoryFlags || !isVisible ? StatusPreGradedWithFlags : StatusPreGraded
            : isVisible ? StatusPreGraded : StatusNeedsEnrichment;
        var reasons = factors.Where(factor => factor.Evaluated).Select(factor => factor.Code)
            .Concat(penalties.Select(penalty => penalty.Code))
            .Concat(isCopartV3 ? eligibility.Flags.Select(flag => flag.Code) : [])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();

        return new LscVehicleScoringResult(
            lotKey,
            platform,
            status,
            preGrade,
            null,
            maxPointsEvaluable,
            coverage,
            confidence,
            null,
            factors,
            penalties,
            reasons,
            missing.OrderBy(field => field, StringComparer.Ordinal).ToArray(),
            policyVersion,
            inputHash,
            now);
    }

    public static string CreateInputHash(AuctionVehicle vehicle, EligibilityEvaluation eligibility)
    {
        var fields = new[]
        {
            LscScoringPolicy.ResolveVersion(vehicle.Platform),
            Normalize(vehicle.Platform),
            Normalize(vehicle.LotNumber),
            Normalize(vehicle.Vin),
            Normalize(vehicle.Seller?.Name),
            Normalize(vehicle.Seller?.Type),
            Normalize(vehicle.Seller?.RawType),
            Normalize(vehicle.Seller?.ClassificationConfidence?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            vehicle.Seller?.NeedsReview?.ToString() ?? string.Empty,
            Normalize(vehicle.Seller?.ClassificationEvidence),
            Normalize(vehicle.Condition?.RunCondition?.Value ?? vehicle.Condition?.RunCondition?.Label),
            Normalize(vehicle.Condition?.PrimaryDamage ?? vehicle.Damage),
            Normalize(vehicle.Condition?.SecondaryDamage),
            Normalize(vehicle.SaleDocument?.Name ?? vehicle.Title),
            vehicle.SaleDocument?.IsPending?.ToString() ?? string.Empty,
            vehicle.Condition?.HasKey?.ToString() ?? string.Empty,
            Normalize(vehicle.VehicleSpecs?.Airbags),
            vehicle.Odometer?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
            eligibility.Decision,
            string.Join(',', eligibility.Flags.Select(flag => flag.Code).OrderBy(code => code, StringComparer.Ordinal))
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001F', fields)))).ToLowerInvariant();
    }

    private static LscVehicleScoringResult Blocked(
        string lotKey,
        string platform,
        string status,
        IEnumerable<string> reasonCodes,
        IReadOnlyCollection<string> missing,
        string inputHash,
        DateTimeOffset now,
        string explanation,
        string? policyVersion = null)
    {
        var factor = new LscScoringFactor("GATE", "Filtro de elegibilidad", 0m, 0m, false, explanation, ["eligibility.decision"]);
        return new LscVehicleScoringResult(
            lotKey,
            platform,
            status,
            null,
            null,
            0m,
            0m,
            0m,
            null,
            [factor],
            [],
            reasonCodes.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(code => code, StringComparer.Ordinal).ToArray(),
            missing.OrderBy(field => field, StringComparer.Ordinal).ToArray(),
            policyVersion ?? LscScoringPolicy.ResolveVersion(platform),
            inputHash,
            now);
    }

    private static LscScoringFactor EvaluateSeller(AuctionVehicle vehicle, ISet<string> missing)
    {
        var sellerType = Normalize(vehicle.Seller?.Type);
        var declaredCategory = !string.IsNullOrWhiteSpace(sellerType) && sellerType is not "UNKNOWN" and not "UNCLASSIFIED" and not "N A" and not "NA";
        var confidence = vehicle.Seller?.ClassificationConfidence ?? (declaredCategory ? 1.0m : 0m);
        var needsReview = vehicle.Seller?.NeedsReview == true;
        if (string.IsNullOrEmpty(sellerType) || sellerType is "UNKNOWN" or "UNCLASSIFIED" or "N A" or "NA" || confidence < SellerTaxonomy.InclusionThreshold)
        {
            missing.Add("seller.taxonomy");
            return Factor("F01", "Vendedor y trazabilidad", 0m, 0m, false,
                "El vendedor no tiene evidencia suficiente para asignar una categoría operativa; queda visible para revisión.", ["seller.type", "seller.name", "seller.classification_confidence"]);
        }

        var points = sellerType switch
        {
            "INSURANCE" => 12m,
            "DEALER" => 10m,
            "RENTAL_FLEET" => 9m,
            "FINANCE" => 7m,
            "REPOSSESSION_BANK" => 6m,
            "GOVERNMENT" => 6m,
            "OTHER" => 4m,
            _ => 0m
        };
        if (points == 0m)
        {
            missing.Add("seller.taxonomy");
            return Factor("F01", "Vendedor y trazabilidad", 0m, 0m, false,
                "La categoría no tiene una regla de scoring aprobada; queda visible para revisión.", ["seller.type", "seller.name"]);
        }

        var reviewText = needsReview ? " La asignación es provisional y requiere verificación del asesor." : string.Empty;
        return Factor("F01", "Vendedor y trazabilidad", points, 15m, true,
            $"Categoría LSC {sellerType} con confianza {confidence:P0}.{reviewText}", ["seller.type", "seller.name", "seller.classification_confidence", "seller.classification_evidence"]);
    }

    private static LscScoringFactor EvaluateMechanicalCondition(AuctionVehicle vehicle, ISet<string> missing)
    {
        var runCondition = Normalize(vehicle.Condition?.RunCondition?.Value ?? vehicle.Condition?.RunCondition?.Label);
        if (string.IsNullOrEmpty(runCondition) || runCondition.Contains("NO INFORMATION", StringComparison.Ordinal))
        {
            missing.Add("mechanical.run_condition");
            return Factor("F02", "Condición mecánica declarada", 0m, 0m, false,
                "La subasta no declaró una condición de marcha utilizable.", ["condition.run_condition"]);
        }

        var points = runCondition.Contains("RUNS AND DRIVES", StringComparison.Ordinal) || runCondition.Contains("RUNS_AND_DRIVES", StringComparison.Ordinal) ? 15m
            : runCondition.Contains("START", StringComparison.Ordinal) ? 9m
            : runCondition.Contains("STATIONARY", StringComparison.Ordinal) ? 3m
            : 5m;
        return Factor("F02", "Condición mecánica declarada", points, 15m, true,
            "Resultado basado únicamente en la condición declarada por la subasta; no sustituye un diagnóstico técnico.", ["condition.run_condition"]);
    }

    private static LscScoringFactor EvaluateDamage(AuctionVehicle vehicle, ISet<string> missing)
    {
        var damage = Normalize(vehicle.Condition?.PrimaryDamage ?? vehicle.Damage);
        if (string.IsNullOrEmpty(damage) || damage is "UNKNOWN" or "NOT REPORTED" or "NO REPORTADO" or "N A" or "NA")
        {
            missing.Add("damage.primary");
            return Factor("F03", "Daño declarado", 0m, 0m, false,
                "No existe un daño primario suficientemente declarado para graduar esta dimensión.", ["condition.primary_damage"]);
        }

        var points = damage switch
        {
            var value when value.Contains("NORMAL WEAR", StringComparison.Ordinal) || value.Contains("NO DAMAGE", StringComparison.Ordinal) => 15m,
            var value when value.Contains("HAIL", StringComparison.Ordinal) || value.Contains("MINOR DENT", StringComparison.Ordinal) || value.Contains("SCRATCH", StringComparison.Ordinal) => 12m,
            var value when value.Contains("REAR", StringComparison.Ordinal) || value.Contains("SIDE", StringComparison.Ordinal) => 10m,
            var value when value.Contains("FRONT", StringComparison.Ordinal) => 7m,
            var value when value.Contains("ALL OVER", StringComparison.Ordinal) || value.Contains("MULTIPLE", StringComparison.Ordinal) => 5m,
            _ => 6m
        };
        return Factor("F03", "Daño declarado", points, 15m, true,
            "Severidad preliminar basada en la descripción de daño de la subasta, sin inspección visual.", ["condition.primary_damage"]);
    }

    private static LscScoringFactor EvaluateDocumentation(AuctionVehicle vehicle, ISet<string> missing)
    {
        var title = Normalize(vehicle.SaleDocument?.Name ?? vehicle.Title);
        if (string.IsNullOrEmpty(title) || title is "UNKNOWN" or "NOT REPORTED" or "NO REPORTADO" or "N A" or "NA")
        {
            missing.Add("documentation.title_type");
            return Factor("F04", "Título y documentación", 0m, 0m, false,
                "La fuente no aportó un tipo de documento utilizable para esta evaluación preliminar.", ["sale_document.name", "title"]);
        }

        var points = title switch
        {
            var value when value.Contains("CLEAR", StringComparison.Ordinal) || value.Contains("ORIGINAL", StringComparison.Ordinal) => 10m,
            var value when value.Contains("SALVAGE", StringComparison.Ordinal) || value.Contains("REBUILD", StringComparison.Ordinal) => 7m,
            var value when value.Contains("BILL OF SALE", StringComparison.Ordinal) => 5m,
            var value when value.Contains("JUNK", StringComparison.Ordinal) || value.Contains("NON REPAIR", StringComparison.Ordinal) || value.Contains("PARTS ONLY", StringComparison.Ordinal) => 4m,
            _ => 6m
        };
        return Factor("F04", "Título y documentación", points, 10m, true,
            "El tipo de título no genera descarte por sí mismo; la transferibilidad final requiere reglas por jurisdicción.", ["sale_document.name", "title"]);
    }

    private static LscScoringFactor EvaluateInformationQuality(AuctionVehicle vehicle, ISet<string> missing)
    {
        var present = 0;
        if (!string.IsNullOrEmpty(Normalize(vehicle.Seller?.Type))) present++;
        if (!string.IsNullOrEmpty(Normalize(vehicle.Condition?.PrimaryDamage ?? vehicle.Damage))) present++;
        if (!string.IsNullOrEmpty(Normalize(vehicle.Condition?.RunCondition?.Value ?? vehicle.Condition?.RunCondition?.Label))) present++;
        if (!string.IsNullOrEmpty(Normalize(vehicle.SaleDocument?.Name ?? vehicle.Title))) present++;
        if (vehicle.Odometer is > 0) present++;
        if (vehicle.Condition?.HasKey is not null) present++;

        if (present < 4) missing.Add("information.completeness");
        var points = present switch
        {
            >= 6 => 5m,
            5 => 4m,
            4 => 3m,
            3 => 2m,
            _ => 1m
        };
        return Factor("F05", "Calidad de información", points, 5m, true,
            $"{present} de 6 señales estructuradas están presentes. Las fotos no se usan en esta fase.", ["seller.type", "condition.primary_damage", "condition.run_condition", "sale_document.name", "odometer", "condition.has_key"]);
    }

    private static IReadOnlyList<LscScoringPenalty> EvaluatePenalties(AuctionVehicle vehicle, EligibilityEvaluation eligibility, IReadOnlyCollection<LscScoringFactor> factors, ISet<string> missing)
    {
        var penalties = new List<LscScoringPenalty>();
        var airbags = Normalize(vehicle.VehicleSpecs?.Airbags);
        if (airbags.Contains("DEPLOY", StringComparison.Ordinal) || airbags.Contains("ACTIVATED", StringComparison.Ordinal))
        {
            penalties.Add(new LscScoringPenalty("P01", "Airbags desplegados", -5m,
                "La descripción estructurada declara airbags desplegados o activados.", ["vehicle_specs.airbags"]));
        }
        if (vehicle.Condition?.HasKey == false)
        {
            penalties.Add(new LscScoringPenalty("P02", "Sin llaves", -3m,
                "La subasta declara que el vehículo no tiene llaves.", ["condition.has_key"]));
        }

        var secondaryDamage = Normalize(vehicle.Condition?.SecondaryDamage);
        if (!string.IsNullOrEmpty(secondaryDamage) && !secondaryDamage.Contains("NONE", StringComparison.Ordinal) && !secondaryDamage.Contains("NO DAMAGE", StringComparison.Ordinal))
        {
            var points = secondaryDamage.Contains("ALL OVER", StringComparison.Ordinal) || secondaryDamage.Contains("MULTIPLE", StringComparison.Ordinal) || secondaryDamage.Contains("FRONT & REAR", StringComparison.Ordinal)
                ? -8m
                : -3m;
            penalties.Add(new LscScoringPenalty("P03", "Daño secundario", points,
                "La subasta declara daño secundario; la severidad visual y económica queda pendiente.", ["condition.secondary_damage"]));
        }

        var uncertaintySignals = eligibility.Flags.Count(flag => flag.Code is "M01" or "M05" or "M08") + factors.Count(factor => !factor.Evaluated);
        if (uncertaintySignals > 0)
        {
            missing.Add("uncertainty.resolution");
            var points = uncertaintySignals >= 3 ? -5m : uncertaintySignals == 2 ? -3m : -2m;
            penalties.Add(new LscScoringPenalty("P04", "Información incierta", points,
                "Existen señales estructuradas incompletas o no verificadas; no se infieren datos faltantes.", ["eligibility.flags", "scoring.factors"]));
        }
        return penalties;
    }

    private static LscScoringFactor Factor(string code, string name, decimal points, decimal maxPoints, bool evaluated, string explanation, IReadOnlyList<string> sourceFields) =>
        new(code, name, points, maxPoints, evaluated, explanation, sourceFields);

    private static string BuildLotKey(AuctionVehicle vehicle) => $"{Normalize(vehicle.Platform).ToLowerInvariant()}:{vehicle.LotNumber?.Trim() ?? "unknown"}";

    private static decimal RoundPercent(decimal value, decimal maximum) => maximum <= 0m ? 0m : Math.Round(value / maximum * 100m, 1, MidpointRounding.AwayFromZero);

    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : System.Text.RegularExpressions.Regex.Replace(value.Trim().ToUpperInvariant(), "\\s+", " ");
}
