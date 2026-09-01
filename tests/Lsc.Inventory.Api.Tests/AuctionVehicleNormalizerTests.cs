using Lsc.Inventory.Api.Contracts;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionVehicleNormalizerTests
{
    [Fact]
    public void Extracts_declared_state_suffix_from_iaai_location_display()
    {
        var vehicle = new AuctionVehicle { Location = new VehicleLocation { Display = "Orlando-North (FL)" } };
        var result = AuctionVehicleNormalizer.Normalize(vehicle, null, null);
        Assert.Equal("FL", result.Location?.State);
        Assert.Equal("Orlando-North (FL)", result.Location?.Display);
    }

    [Fact]
    public void Does_not_infer_state_when_display_has_no_explicit_suffix()
    {
        var vehicle = new AuctionVehicle { Location = new VehicleLocation { Display = "Unknown yard" } };
        Assert.Null(AuctionVehicleNormalizer.Normalize(vehicle, null, null).Location?.State);
    }
}
