using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Normalization;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CanonicalVehicleCleanerTests
{
    [Fact]
    public void Preserves_raw_source_and_normalizes_commercial_fields()
    {
        var raw = new AuctionVehicle
        {
            Platform = " IAAI ",
            LotNumber = "Lot 123 456",
            Vin = " 1hgcm82633a123456 ",
            Make = "  chev ",
            Model = "  tahoe  ",
            Damage = " front   end ",
            Pricing = new PricingInfo { CurrentBidUsd = -1, BuyNowUsd = 1_000 },
            OdometerInfo = new OdometerInfo { Miles = 25_000, Status = " actual " }
        };

        var cleaned = CanonicalVehicleCleaner.Clean(raw);

        Assert.Equal("iaai", cleaned.Platform);
        Assert.Equal("123456", cleaned.LotNumber);
        Assert.Equal("1HGCM82633A123456", cleaned.Vin);
        Assert.Equal("CHEVROLET", cleaned.Make);
        Assert.Equal("TAHOE", cleaned.Model);
        Assert.Equal("Front End", cleaned.Damage);
        Assert.Null(cleaned.Pricing?.CurrentBidUsd);
        Assert.Equal(1_000, cleaned.Pricing?.BuyNowUsd);
        Assert.Equal("ACTUAL", cleaned.OdometerInfo?.Status);
        Assert.NotNull(cleaned.RawSource);
        Assert.Equal(" IAAI ", cleaned.RawSource?.GetProperty("platform").GetString());
    }

    [Theory]
    [InlineData("ALL MODELS")]
    [InlineData("unknown")]
    [InlineData(" N/A ")]
    public void Does_not_invent_unresolved_models(string model) =>
        Assert.Null(CanonicalVehicleCleaner.Clean(new AuctionVehicle { Model = model }).Model);
}
