using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionsApiCanonicalMapperLocationTests
{
    [Fact]
    public void Maps_lot_seller_type_and_location_into_the_canonical_vehicle()
    {
        using var document = JsonDocument.Parse("""
        {
          "id": 123,
          "year": 2021,
          "manufacturer": { "id": 1, "name": "Toyota" },
          "model": { "id": 2, "name": "Camry" },
          "lots": [
            {
              "lot": "ABC123",
              "seller_type": { "id": 7, "name": "Insurance Company" },
              "seller": { "name": "Example Insurance" },
              "location": {
                "facility_id": 55,
                "name": "Miami South",
                "city": "Miami",
                "state": "FL"
              },
              "title": { "id": 10, "name": "Clean Title" },
              "sale_date": "2026-09-17T15:00:00Z"
            }
          ]
        }
        """);

        var provider = AuctionsApiCanonicalMapper.MapVehicle(document.RootElement, "copart");
        Assert.NotNull(provider);

        var vehicle = Assert.Single(AuctionsApiCanonicalMapper.ToAuctionVehicles(provider!));

        Assert.Equal("insurance_company", vehicle.Seller?.Type);
        Assert.Equal("Example Insurance", vehicle.Seller?.Name);
        Assert.Equal("Miami South", vehicle.Location?.Display);
        Assert.Equal("Miami", vehicle.Location?.City);
        Assert.Equal("FL", vehicle.Location?.State);
        Assert.Equal("55", vehicle.Location?.FacilityId);
    }
}
