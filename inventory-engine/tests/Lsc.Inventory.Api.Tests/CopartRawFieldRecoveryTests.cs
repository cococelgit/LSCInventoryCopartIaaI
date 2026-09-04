using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Sources;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartRawFieldRecoveryTests
{
    [Fact]
    public void Recovers_real_raw_fields_without_inventing_values()
    {
        var vehicle = new AuctionVehicle
        {
            Platform = "copart",
            LotNumber = "62830276",
            RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string?>
            {
                ["Seller Name"] = "State Farm Insurance",
                ["Runs/Drives"] = "RUN & DRIVE",
                ["Damage Description"] = "Front End",
                ["Secondary Damage"] = "Minor Dent/Scratches",
                ["Has Keys-Yes or No"] = "YES",
                ["Odometer"] = "42,100 mi",
                ["Odometer Brand"] = "Actual",
                ["Sale Title Type"] = "AQ"
            })
        };

        var recovered = CopartRawFieldRecovery.Recover(vehicle);

        Assert.Equal("State Farm Insurance", recovered.Seller!.Name);
        Assert.Equal("insurance", recovered.Seller.Type);
        Assert.Equal("RUNS_AND_DRIVES", recovered.Condition!.RunCondition!.Normalized);
        Assert.Equal("RUN & DRIVE", recovered.Condition.RunCondition.Raw);
        Assert.Equal("Front End", recovered.Condition.PrimaryDamage);
        Assert.Equal("Minor Dent/Scratches", recovered.Condition.SecondaryDamage);
        Assert.True(recovered.Condition.HasKey);
        Assert.Equal(42100m, recovered.Odometer);
        Assert.Equal("Actual", recovered.OdometerInfo!.Status);
        Assert.Equal("Clear Title", recovered.SaleDocument!.Name);
        Assert.Equal("State Farm Insurance", recovered.AdditionalData!["source_recovery_seller_name"].GetString());
    }

    [Fact]
    public void Does_not_overwrite_existing_canonical_values_with_raw_values()
    {
        var vehicle = new AuctionVehicle
        {
            Platform = "copart",
            Seller = new AuctionSeller { Name = "Existing Seller", Type = "insurance", ClassificationConfidence = 1m },
            Condition = new VehicleCondition
            {
                PrimaryDamage = "Existing Damage",
                HasKey = false,
                RunCondition = new RunConditionInfo { Normalized = "STARTS", Raw = "STARTS" }
            },
            RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string?>
            {
                ["Seller Name"] = "Different Seller",
                ["Runs/Drives"] = "RUN & DRIVE",
                ["Damage Description"] = "Front End",
                ["Has Keys-Yes or No"] = "YES"
            })
        };

        var recovered = CopartRawFieldRecovery.Recover(vehicle);

        Assert.Equal("Existing Seller", recovered.Seller!.Name);
        Assert.Equal("Existing Damage", recovered.Condition!.PrimaryDamage);
        Assert.False(recovered.Condition.HasKey);
        Assert.Equal("STARTS", recovered.Condition.RunCondition!.Normalized);
    }

    [Fact]
    public void Does_not_recover_fields_for_non_copart_sources()
    {
        var vehicle = new AuctionVehicle
        {
            Platform = "iaai",
            RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string?>
            {
                ["Seller Name"] = "State Farm Insurance",
                ["Runs/Drives"] = "RUN & DRIVE"
            })
        };

        var recovered = CopartRawFieldRecovery.Recover(vehicle);

        Assert.Same(vehicle, recovered);
    }
}
