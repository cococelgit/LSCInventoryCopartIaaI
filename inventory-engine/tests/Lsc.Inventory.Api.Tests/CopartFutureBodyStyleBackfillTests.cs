using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Workers;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartFutureBodyStyleBackfillTests
{
    [Fact]
    public void Body_style_prefers_canonical_vehicle_specs_value()
    {
        var vehicle = new AuctionVehicle
        {
            VehicleSpecs = new VehicleSpecs { BodyStyle = "SUV" },
            RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["Body Style"] = "Sedan" }),
            AdditionalData = new Dictionary<string, JsonElement>
            {
                ["source_body_style"] = JsonSerializer.SerializeToElement("Truck")
            }
        };

        Assert.Equal("SUV", CopartFutureBodyStyleBackfillProcessor.ReadBodyStyle(vehicle));
    }

    [Fact]
    public void Body_style_falls_back_to_raw_source_labels()
    {
        var vehicle = new AuctionVehicle
        {
            RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["Body Type"] = "Pickup" })
        };

        Assert.Equal("Pickup", CopartFutureBodyStyleBackfillProcessor.ReadBodyStyle(vehicle));
    }

    [Fact]
    public void Body_style_falls_back_to_flattened_source_metadata()
    {
        var vehicle = new AuctionVehicle
        {
            AdditionalData = new Dictionary<string, JsonElement>
            {
                ["source_body_style"] = JsonSerializer.SerializeToElement("Coupe")
            }
        };

        Assert.Equal("Coupe", CopartFutureBodyStyleBackfillProcessor.ReadBodyStyle(vehicle));
    }

    [Fact]
    public void Blank_body_style_is_not_treated_as_a_valid_value()
    {
        var vehicle = new AuctionVehicle
        {
            VehicleSpecs = new VehicleSpecs { BodyStyle = "  " },
            RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["Body Style"] = "" }),
            AdditionalData = new Dictionary<string, JsonElement>
            {
                ["source_body_style"] = JsonSerializer.SerializeToElement("   ")
            }
        };

        Assert.Null(CopartFutureBodyStyleBackfillProcessor.ReadBodyStyle(vehicle));
    }
}

