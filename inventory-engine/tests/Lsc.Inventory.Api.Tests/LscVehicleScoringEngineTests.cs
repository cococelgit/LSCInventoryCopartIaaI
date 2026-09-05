using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Scoring;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class LscVehicleScoringEngineTests
{
    private static readonly DateTimeOffset EvaluationTime = DateTimeOffset.Parse("2026-08-27T12:00:00Z");

    [Fact]
    public void Discarded_vehicle_overrides_any_positive_factor_score()
    {
        var vehicle = ValidVehicle() with { Condition = ValidVehicle().Condition! with { PrimaryDamage = "Flood" } };
        var result = LscVehicleScoringEngine.Evaluate(vehicle, AuctionEligibilityEvaluator.Evaluate(vehicle, EvaluationTime), EvaluationTime);

        Assert.Equal("DISCARDED", result.Status);
        Assert.Null(result.PreGrade);
        Assert.Null(result.BuyScore);
        Assert.Contains("D05", result.ReasonCodes);
        Assert.Equal(0m, result.CoveragePercent);
    }

    [Fact]
    public void IAAI_material_flag_requires_manual_review_before_pre_grade()
    {
        var vehicle = ValidVehicle() with { Condition = ValidVehicle().Condition! with { RunCondition = null } };
        var result = LscVehicleScoringEngine.Evaluate(vehicle, AuctionEligibilityEvaluator.Evaluate(vehicle, EvaluationTime), EvaluationTime);

        Assert.Equal("MANUAL_REVIEW", result.Status);
        Assert.Null(result.PreGrade);
        Assert.Contains("M04", result.ReasonCodes);
        Assert.Contains("manual_review.resolution", result.MissingFields);
        Assert.Equal(LscScoringPolicy.IAAIPolicyVersion, result.PolicyVersion);
    }

    [Fact]
    public void Copart_unverified_run_condition_and_conditional_terms_receive_provisional_score_with_flags()
    {
        var vehicle = ValidVehicle() with
        {
            Platform = "copart",
            Condition = ValidVehicle().Condition! with { RunCondition = new RunConditionInfo { Normalized = "UNVERIFIED", Raw = "DEFAULT" } },
            Auction = ValidVehicle().Auction! with { State = "ON MINIMUM BID", LotStatus = "ON MINIMUM BID" }
        };
        var result = LscVehicleScoringEngine.Evaluate(vehicle, AuctionEligibilityEvaluator.Evaluate(vehicle, EvaluationTime), EvaluationTime);

        Assert.Equal("PRE_GRADED_WITH_FLAGS", result.Status);
        Assert.NotNull(result.PreGrade);
        Assert.True(result.PreGrade >= 0m);
        Assert.Contains("M04", result.ReasonCodes);
        Assert.Contains("M07", result.ReasonCodes);
        Assert.Contains("manual_review.resolution", result.MissingFields);
        Assert.Equal(LscScoringPolicy.CopartPolicyVersion, result.PolicyVersion);
    }

    [Fact]
    public void Copart_runs_and_drives_normalized_value_receives_full_mechanical_factor()
    {
        var vehicle = ValidVehicle() with
        {
            Platform = "copart",
            Condition = ValidVehicle().Condition! with { RunCondition = new RunConditionInfo { Normalized = "RUNS_AND_DRIVES", Raw = "RUN & DRIVE" } }
        };
        var result = LscVehicleScoringEngine.Evaluate(vehicle, AuctionEligibilityEvaluator.Evaluate(vehicle, EvaluationTime), EvaluationTime);

        Assert.Equal("PRE_GRADED_WITH_FLAGS", result.Status);
        Assert.Contains(result.Factors, factor => factor.Code == "F02" && factor.Evaluated && factor.Points == 15m);
        Assert.DoesNotContain("M04", result.ReasonCodes);
    }

    [Fact]
    public void Copart_low_coverage_receives_numeric_provisional_score_instead_of_needs_enrichment()
    {
        var vehicle = ValidVehicle() with
        {
            Platform = "copart",
            Seller = null,
            Condition = ValidVehicle().Condition! with { PrimaryDamage = null, RunCondition = null },
            OdometerInfo = new OdometerInfo { Miles = 0, Status = "NOT ACTUAL" }
        };
        var result = LscVehicleScoringEngine.Evaluate(vehicle, AuctionEligibilityEvaluator.Evaluate(vehicle, EvaluationTime), EvaluationTime);

        Assert.Equal("PRE_GRADED_WITH_FLAGS", result.Status);
        Assert.NotNull(result.PreGrade);
        Assert.True(result.CoveragePercent < LscScoringPolicy.MinimumCoveragePercent);
        Assert.Contains("M04", result.ReasonCodes);
        Assert.Contains("M05", result.ReasonCodes);
    }

    [Fact]
    public void Copart_discarded_vehicle_remains_without_numeric_score_under_v2()
    {
        var vehicle = ValidVehicle() with { Platform = "copart", Condition = ValidVehicle().Condition! with { PrimaryDamage = "Flood" } };
        var result = LscVehicleScoringEngine.Evaluate(vehicle, AuctionEligibilityEvaluator.Evaluate(vehicle, EvaluationTime), EvaluationTime);

        Assert.Equal("DISCARDED", result.Status);
        Assert.Null(result.PreGrade);
        Assert.Contains("D05", result.ReasonCodes);
        Assert.Equal(LscScoringPolicy.CopartPolicyVersion, result.PolicyVersion);
    }

    [Fact]
    public void Copart_v3_uses_the_same_60_point_factor_scale_as_iaai()
    {
        var vehicle = ValidVehicle() with { Platform = "copart" };
        var result = LscVehicleScoringEngine.Evaluate(vehicle, AuctionEligibilityEvaluator.Evaluate(vehicle, EvaluationTime), EvaluationTime);

        Assert.Equal("lsc_pre_grade_v3_60", result.PolicyVersion);
        Assert.Equal(60m, result.MaxPointsEvaluable);
        Assert.Equal(100m, result.CoveragePercent);
        Assert.Equal(49m, result.PreGrade);
        Assert.Contains(result.Factors, factor => factor.Code == "F01" && factor.MaxPointsEvaluable == 15m);
        Assert.Contains(result.Factors, factor => factor.Code == "F02" && factor.MaxPointsEvaluable == 15m);
        Assert.Contains(result.Factors, factor => factor.Code == "F03" && factor.MaxPointsEvaluable == 15m);
        Assert.Contains(result.Factors, factor => factor.Code == "F04" && factor.MaxPointsEvaluable == 10m);
        Assert.Contains(result.Factors, factor => factor.Code == "F05" && factor.MaxPointsEvaluable == 5m);
        Assert.InRange(result.PreGrade!.Value, 0m, 60m);
    }

    [Fact]
    public void Calculates_visible_pre_grade_only_when_observable_coverage_is_sufficient()
    {
        var vehicle = ValidVehicle();
        var result = LscVehicleScoringEngine.Evaluate(vehicle, AuctionEligibilityEvaluator.Evaluate(vehicle, EvaluationTime), EvaluationTime);

        Assert.Equal("PRE_GRADED", result.Status);
        Assert.Equal(49m, result.PreGrade);
        Assert.Equal(60m, result.MaxPointsEvaluable);
        Assert.Equal(100m, result.CoveragePercent);
        Assert.Null(result.BuyScore);
        Assert.Equal(LscScoringPolicy.IAAIPolicyVersion, result.PolicyVersion);
        Assert.Contains("profitability.total_cost", result.MissingFields);
        Assert.Contains("demand.market_comparables", result.MissingFields);
    }

    [Fact]
    public void Keeps_pre_grade_hidden_when_observable_coverage_is_below_threshold()
    {
        var vehicle = ValidVehicle() with
        {
            Seller = null,
            Condition = ValidVehicle().Condition! with { PrimaryDamage = null },
            OdometerInfo = new OdometerInfo { Miles = 0, Status = "NOT ACTUAL" }
        };
        var result = LscVehicleScoringEngine.Evaluate(vehicle, AuctionEligibilityEvaluator.Evaluate(vehicle, EvaluationTime), EvaluationTime);

        Assert.Equal("NEEDS_ENRICHMENT", result.Status);
        Assert.Null(result.PreGrade);
        Assert.True(result.CoveragePercent < LscScoringPolicy.MinimumCoveragePercent);
        Assert.Contains("seller.taxonomy", result.MissingFields);
        Assert.Contains("damage.primary", result.MissingFields);
    }

    [Fact]
    public void Applies_declared_penalties_without_using_photo_evidence()
    {
        var vehicle = ValidVehicle() with
        {
            Condition = ValidVehicle().Condition! with { HasKey = false, SecondaryDamage = "Front & Rear" },
            VehicleSpecs = new VehicleSpecs { Airbags = "DEPLOYED" }
        };
        var result = LscVehicleScoringEngine.Evaluate(vehicle, AuctionEligibilityEvaluator.Evaluate(vehicle, EvaluationTime), EvaluationTime);

        Assert.Equal("PRE_GRADED", result.Status);
        Assert.Equal(33m, result.PreGrade);
        Assert.Contains(result.Penalties, penalty => penalty.Code == "P01" && penalty.Points == -5m);
        Assert.Contains(result.Penalties, penalty => penalty.Code == "P02" && penalty.Points == -3m);
        Assert.Contains(result.Penalties, penalty => penalty.Code == "P03" && penalty.Points == -8m);
        Assert.DoesNotContain(result.Factors.SelectMany(factor => factor.SourceFields), field => field.Contains("media", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Input_hash_changes_when_the_platform_policy_changes()
    {
        var iaai = ValidVehicle();
        var copart = iaai with { Platform = "copart" };

        var iaaiHash = LscVehicleScoringEngine.CreateInputHash(iaai, AuctionEligibilityEvaluator.Evaluate(iaai, EvaluationTime));
        var copartHash = LscVehicleScoringEngine.CreateInputHash(copart, AuctionEligibilityEvaluator.Evaluate(copart, EvaluationTime));

        Assert.NotEqual(iaaiHash, copartHash);
        Assert.Equal(LscScoringPolicy.IAAIPolicyVersion, LscScoringPolicy.ResolveVersion(iaai.Platform));
        Assert.Equal(LscScoringPolicy.CopartPolicyVersion, LscScoringPolicy.ResolveVersion(copart.Platform));
    }

    [Fact]
    public void Input_hash_changes_when_a_scoring_input_changes()
    {
        var original = ValidVehicle();
        var changed = original with { Condition = original.Condition! with { PrimaryDamage = "Rear" } };
        var originalHash = LscVehicleScoringEngine.CreateInputHash(original, AuctionEligibilityEvaluator.Evaluate(original, EvaluationTime));
        var changedHash = LscVehicleScoringEngine.CreateInputHash(changed, AuctionEligibilityEvaluator.Evaluate(changed, EvaluationTime));

        Assert.NotEqual(originalHash, changedHash);
    }

    private static AuctionVehicle ValidVehicle() => new()
    {
        Platform = "iaai",
        LotNumber = "12345678",
        Vin = "1HGCM82633A004352",
        Year = 2012,
        Make = "Honda",
        Model = "Accord",
        Auction = new AuctionInfo { AuctionAt = DateTimeOffset.Parse("2026-08-31T14:00:00Z") },
        Location = new VehicleLocation { State = "FL", FacilityId = "366" },
        Seller = new AuctionSeller { Name = "Insurance Company", Type = "Insurance" },
        Condition = new VehicleCondition { PrimaryDamage = "Front End", HasKey = true, RunCondition = new RunConditionInfo { Value = "RUNS AND DRIVES" } },
        OdometerInfo = new OdometerInfo { Miles = 50_000, Status = "ACTUAL" },
        Media = new MediaInfo { Photos = ["https://images.example.test/lot.jpg"] },
        SaleDocument = new SaleDocument { Name = "CLEAR", IsPending = false }
    };
}
