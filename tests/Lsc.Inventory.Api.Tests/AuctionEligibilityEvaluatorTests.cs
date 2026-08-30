using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionEligibilityEvaluatorTests
{
    [Fact]
    public void Loads_vehicle_when_no_discard_rule_is_active()
    {
        var result = AuctionEligibilityEvaluator.Evaluate(ValidVehicle());
        Assert.Equal("CARGAR", result.Decision);
        Assert.True(result.LoadToSystem);
        Assert.Empty(result.DiscardReasons);
        Assert.Empty(result.Flags);
        Assert.Equal("***4352", result.VinMasked);
    }

    [Theory]
    [InlineData(null, null, "D00A")]
    [InlineData("1HGCM82633A123456", null, "D00B")]
    public void Rejects_missing_required_base_fields(string? vin, DateTimeOffset? saleDate, string expectedCode)
    {
        var vehicle = ValidVehicle() with { Vin = vin, Auction = new AuctionInfo { AuctionAt = saleDate } };
        Assert.Contains(AuctionEligibilityEvaluator.Evaluate(vehicle).DiscardReasons, reason => reason.Code == expectedCode);
    }

    [Theory]
    [InlineData("WI")]
    [InlineData("AL")]
    [InlineData("MI")]
    public void Rejects_banned_yard_states(string state)
    {
        var vehicle = ValidVehicle() with { Location = new VehicleLocation { State = state } };
        Assert.Single(AuctionEligibilityEvaluator.Evaluate(vehicle).DiscardReasons, reason => reason.Code == "D01");
    }

    [Theory]
    [InlineData("Wheelzy LLC")]
    [InlineData("MARESTAR AUTO RECYCLING")]
    [InlineData("TitleMax of Florida")]
    [InlineData("CarBrain Holdings")]
    public void Rejects_banned_sellers(string seller)
    {
        var vehicle = ValidVehicle() with { Seller = new AuctionSeller { Name = seller } };
        Assert.Single(AuctionEligibilityEvaluator.Evaluate(vehicle).DiscardReasons, reason => reason.Code == "D02");
    }

    [Theory]
    [InlineData("Undercarriage", "D03")]
    [InlineData("Burn - Engine", "D04")]
    [InlineData("Water/Flood", "D05")]
    [InlineData("Frame_Damage", "D06")]
    [InlineData("Missing/Altered VIN", "D07")]
    [InlineData("Biohazard/Chemical", "D08")]
    public void Rejects_explicit_damage_descriptions(string damage, string expectedCode)
    {
        var vehicle = ValidVehicle() with { Condition = new VehicleCondition { PrimaryDamage = damage } };
        Assert.Single(AuctionEligibilityEvaluator.Evaluate(vehicle).DiscardReasons, reason => reason.Code == expectedCode);
    }

    [Fact]
    public void Returns_all_matching_rules_in_code_order()
    {
        var vehicle = ValidVehicle() with
        {
            Vin = null,
            Location = new VehicleLocation { State = "MI" },
            Condition = new VehicleCondition { PrimaryDamage = "Flood", SecondaryDamage = "Replaced VIN" }
        };
        Assert.Equal(["D00A", "D01", "D05", "D07"], AuctionEligibilityEvaluator.Evaluate(vehicle).DiscardReasons.Select(reason => reason.Code));
    }

    [Fact]
    public void Rejects_pending_title_using_provider_boolean()
    {
        var vehicle = ValidVehicle() with { SaleDocument = new SaleDocument { Name = "Certificate of Title", IsPending = true } };
        Assert.Single(AuctionEligibilityEvaluator.Evaluate(vehicle).DiscardReasons, reason => reason.Code == "D10");
    }

    [Theory]
    [InlineData("REBUILT")]
    [InlineData("CERTIFICATE OF DESTRUCTION")]
    [InlineData("JUNK")]
    [InlineData("NON-REPAIRABLE")]
    [InlineData("PARTS ONLY")]
    public void Loads_every_title_type_when_no_non_title_rule_is_active(string title)
    {
        var vehicle = ValidVehicle() with { SaleDocument = new SaleDocument { Name = title, IsPending = false } };
        var result = AuctionEligibilityEvaluator.Evaluate(vehicle);
        Assert.Equal("CARGAR", result.Decision);
        Assert.True(result.LoadToSystem);
    }

    [Fact]
    public void Rejects_invalid_modern_vin_with_d00c()
    {
        var result = AuctionEligibilityEvaluator.Evaluate(ValidVehicle() with { Vin = "1HGCM82633A004353" }, DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        Assert.Contains(result.DiscardReasons, reason => reason.Code == "D00C");
        Assert.DoesNotContain(result.DiscardReasons.SelectMany(reason => reason.ObservedValues.Values), value => Equals(value, "1HGCM82633A004353"));
    }

    [Fact]
    public void Marks_valid_legacy_vin_without_discarding()
    {
        var result = AuctionEligibilityEvaluator.Evaluate(ValidVehicle() with { Year = 1972, Vin = "ABC1234567" }, DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        Assert.Equal("MARCAR", result.Decision);
        Assert.True(result.LoadToSystem);
        Assert.Contains(result.Flags, flag => flag.Code == "M00");
    }

    [Fact]
    public void Rejects_past_sale_date_with_d00d()
    {
        var result = AuctionEligibilityEvaluator.Evaluate(ValidVehicle() with { Auction = new AuctionInfo { AuctionAt = DateTimeOffset.Parse("2026-08-24T14:00:00Z") } }, DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        Assert.Contains(result.DiscardReasons, reason => reason.Code == "D00D");
    }

    [Theory]
    [InlineData(null, 2020, "Q01")]
    [InlineData("ABC", 2020, "Q01")]
    [InlineData("12345678", null, "Q04")]
    [InlineData("12345678", 1899, "Q04")]
    public void Quarantines_invalid_identity_or_year(string? lotNumber, int? year, string code)
    {
        var result = AuctionEligibilityEvaluator.Evaluate(ValidVehicle() with { LotNumber = lotNumber, Year = year }, DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        Assert.Equal("CUARENTENA", result.Decision);
        Assert.False(result.LoadToSystem);
        Assert.Contains(result.DiscardReasons, reason => reason.Code == code);
    }

    [Theory]
    [InlineData("M01")]
    [InlineData("M02")]
    [InlineData("M03")]
    [InlineData("M04")]
    [InlineData("M05")]
    [InlineData("M06")]
    [InlineData("M07")]
    [InlineData("M08")]
    public void Loads_and_marks_informational_conditions(string expectedCode)
    {
        var vehicle = expectedCode switch
        {
            "M01" => ValidVehicle() with { Seller = null },
            "M02" => ValidVehicle() with { Platform = "copart", SaleDocument = new SaleDocument { Name = null, IsPending = false } },
            "M03" => ValidVehicle() with { Condition = ValidVehicle().Condition! with { HasKey = false } },
            "M04" => ValidVehicle() with { Condition = ValidVehicle().Condition! with { RunCondition = null } },
            "M05" => ValidVehicle() with { OdometerInfo = new OdometerInfo { Miles = 0, Status = "NOT ACTUAL" } },
            "M06" => ValidVehicle() with { Media = new MediaInfo { Photos = [] } },
            "M07" => ValidVehicle() with { Auction = new AuctionInfo { AuctionAt = DateTimeOffset.Parse("2026-08-31T14:00:00Z"), LotStatus = "On Approval" } },
            "M08" => ValidVehicle() with { Model = "ALL MODELS" },
            _ => throw new InvalidOperationException()
        };
        var result = AuctionEligibilityEvaluator.Evaluate(vehicle, DateTimeOffset.Parse("2026-08-25T12:00:00Z"));
        Assert.True(result.LoadToSystem);
        Assert.Equal("MARCAR", result.Decision);
        Assert.Contains(result.Flags, flag => flag.Code == expectedCode);
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
        Seller = new AuctionSeller { Name = "Insurance Company" },
        Condition = new VehicleCondition { PrimaryDamage = "Front End", HasKey = true, RunCondition = new RunConditionInfo { Value = "RUNS AND DRIVES" } },
        OdometerInfo = new OdometerInfo { Miles = 50_000, Status = "ACTUAL" },
        Media = new MediaInfo { Photos = ["https://images.example.test/lot.jpg"] },
        SaleDocument = new SaleDocument { Name = "Certificate of Title", IsPending = false }
    };
}
