using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Workers;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionsApiCanonicalMapperTests
{
    [Fact]
    public void MapsOfficialBodyTypeAndDamagePathsFromCopartActiveFixture()
    {
        var payload = ReadFixture("copart_cars_page1.json");
        var row = payload.GetProperty("data")[0];

        var mapped = AuctionsApiCanonicalMapper.MapVehicle(row, "copart");
        var lot = Assert.Single(mapped!.Lots);

        Assert.Equal("3", mapped.DomainId);
        Assert.Equal("pickup", mapped.BodyType!.NormalizedValue);
        Assert.Equal("Minor Dent/Scratches", lot.DamageMain!.Name);
        Assert.Equal("64679886", lot.LotNumber);
        Assert.Equal("sold", lot.Status!.NormalizedValue);
    }

    [Fact]
    public void PreservesIdsNamesAndEngineShapeFromIaaIFixture()
    {
        var payload = ReadFixture("iaai_cars_page1.json");
        var row = payload.GetProperty("data")[0];

        var mapped = AuctionsApiCanonicalMapper.MapVehicle(row, "iaai");
        var lot = Assert.Single(mapped!.Lots);

        Assert.Equal("1", mapped.DomainId);
        Assert.Equal("Hyundai", mapped.Manufacturer!.Name);
        Assert.Equal("Sonata", mapped.Model!.Name);
        Assert.Equal("sedan", mapped.BodyType!.NormalizedValue);
        Assert.Equal("automatic", mapped.Transmission!.NormalizedValue);
        Assert.Equal("actual", lot.OdometerStatus!.NormalizedValue);
        var vehicle = Assert.Single(AuctionsApiCanonicalMapper.ToAuctionVehicles(mapped));
        Assert.Equal("intact", vehicle.VehicleSpecs!.Airbags);
        Assert.Equal("RUNS AND DRIVES", vehicle.Condition!.RunCondition!.Value);
        Assert.Equal("RUNS AND DRIVES", vehicle.Condition.RunCondition.Label);
        Assert.Equal("gardena, california", vehicle.Location!.Display);
        Assert.Equal("gardena", vehicle.Location.City);
        Assert.Equal("california", vehicle.Location.State);
        Assert.NotNull(mapped.Engine);
        Assert.Equal(4, mapped.Cylinders);
    }

    [Fact]
    public void Maps_nested_auction_images_into_canonical_media()
    {
        using var document = JsonDocument.Parse("""
        {
          "domain_id": 1,
          "year": 2023,
          "manufacturer": { "name": "Honda" },
          "model": { "name": "Odyssey" },
          "lots": [{
            "lot": "45891879",
            "vin": "1TESTVIN123456789",
            "images": [
              { "large": "https://vis.iaai.com/resizer?imageKeys=one&width=640" },
              { "thumb": "https://vis.iaai.com/resizer?imageKeys=two&width=320" },
              "https://vis.iaai.com/resizer?imageKeys=three&width=640"
            ]
          }]
        }
        """);

        var provider = AuctionsApiCanonicalMapper.MapVehicle(document.RootElement, "iaai");
        var vehicle = Assert.Single(AuctionsApiCanonicalMapper.ToAuctionVehicles(provider!));

        Assert.Equal(3, vehicle.Media!.ThumbnailsCount);
        Assert.Equal(3, vehicle.Media.Photos!.Count);
        Assert.Contains(vehicle.Media.Photos, value => value.Contains("imageKeys=one", StringComparison.Ordinal));
        Assert.Contains(vehicle.Media.Photos, value => value.Contains("imageKeys=two", StringComparison.Ordinal));
        Assert.Contains(vehicle.Media.Photos, value => value.Contains("imageKeys=three", StringComparison.Ordinal));
    }

    [Fact]
    public void MapsArchivedOutcomeWithoutTreatingArchivedAsSoldByDefault()
    {
        var payload = ReadFixture("copart_archived_page1.json");
        var row = payload.GetProperty("data")[2];

        var outcome = AuctionsApiCanonicalMapper.MapArchived(row, "copart");

        Assert.Equal("3", outcome!.DomainId);
        Assert.Equal("sold", outcome.Status!.NormalizedValue);
        Assert.Equal("57874375", outcome.LotNumber);
        Assert.Equal(1800m, outcome.FinalBid!.Value);
        Assert.NotNull(outcome.SaleDate!.Value);
        Assert.NotNull(outcome.ArchivedAt);
    }

    [Fact]
    public void MapsTenPriceHistoryEventsAndKeepsNullPricesAsEvidence()
    {
        var payload = ReadFixture("copart_detail_prices_history_known.json");
        var row = payload.GetProperty("data");

        var mapped = AuctionsApiCanonicalMapper.MapVehicle(row, "copart");
        var lot = Assert.Single(mapped!.Lots);

        Assert.Equal("45924706", lot.LotNumber);
        Assert.Equal("3", lot.DomainId);
        Assert.Equal("sale", lot.Status!.NormalizedValue);
        Assert.Equal("Side", lot.DamageMain!.Name);
        var priceHistory = row.GetProperty("lots")[0].GetProperty("prices");
        Assert.Equal(10, priceHistory.GetArrayLength());
        Assert.Equal(JsonValueKind.Null, priceHistory[1].GetProperty("bid").ValueKind);
    }

    [Fact]
    public void Shadow_comparison_identifies_legacy_damage_and_body_mapping_gaps()
    {
        var legacy = new AuctionVehicle
        {
            Platform = "copart",
            LotNumber = "64679886",
            VehicleSpecs = new VehicleSpecs { BodyStyle = "automobile" },
            VehicleType = "automobile",
            Condition = new VehicleCondition { PrimaryDamage = null, SecondaryDamage = null },
        };
        var canonical = new AuctionVehicle
        {
            Platform = "copart",
            LotNumber = "64679886",
            VehicleSpecs = new VehicleSpecs { BodyStyle = "Pickup" },
            VehicleType = "automobile",
            Condition = new VehicleCondition { PrimaryDamage = "Minor Dent/Scratches", SecondaryDamage = null },
        };

        var result = AuctionsApiCanonicalShadowComparer.Compare(legacy, canonical);

        Assert.Contains("body_style", result.Differences);
        Assert.Contains("primary_damage", result.Differences);
        Assert.DoesNotContain("vehicle_type", result.Differences);
    }

    [Fact]
    public void Maps_real_copart_odometer_and_keys_into_canonical_vehicle()
    {
        var payload = ReadFixture("copart_cars_page1.json");
        var row = payload.GetProperty("data")[0];
        var provider = AuctionsApiCanonicalMapper.MapVehicle(row, "copart");
        var lot = Assert.Single(provider!.Lots);
        var vehicle = Assert.Single(AuctionsApiCanonicalMapper.ToAuctionVehicles(provider));

        Assert.Equal(57577m, lot.OdometerMiles);
        Assert.Equal(92661m, lot.OdometerKilometers);
        Assert.True(lot.HasKey);
        Assert.Equal(57577m, vehicle.OdometerInfo!.Miles);
        Assert.Equal(92661m, vehicle.OdometerInfo.Kilometers);
        Assert.Equal("actual", vehicle.OdometerInfo.Status);
        Assert.True(vehicle.Condition!.HasKey);
    }

    [Fact]
    public void Maps_real_iaai_odometer_and_keys_into_canonical_vehicle()
    {
        var payload = ReadFixture("iaai_cars_page1.json");
        var row = payload.GetProperty("data")[0];
        var provider = AuctionsApiCanonicalMapper.MapVehicle(row, "iaai");
        var lot = Assert.Single(provider!.Lots);
        var vehicle = Assert.Single(AuctionsApiCanonicalMapper.ToAuctionVehicles(provider));

        Assert.Equal(160041m, lot.OdometerMiles);
        Assert.Equal(257561m, lot.OdometerKilometers);
        Assert.True(lot.HasKey);
        Assert.Equal(160041m, vehicle.OdometerInfo!.Miles);
        Assert.Equal(257561m, vehicle.OdometerInfo.Kilometers);
        Assert.Equal("actual", vehicle.OdometerInfo.Status);
        Assert.True(vehicle.Condition!.HasKey);
    }

    [Fact]
    public void Shadow_comparison_against_real_copart_fixture_exposes_current_mapping_gaps()
    {
        var payload = ReadFixture("copart_cars_page1.json");
        var row = payload.GetProperty("data")[0];
        var legacy = AuctionsApiIncrementalSyncProcessor.MapRows(new[] { row }, "copart").Single();
        var provider = AuctionsApiCanonicalMapper.MapVehicle(row, "copart");
        var canonical = AuctionsApiCanonicalMapper.ToAuctionVehicles(provider!).Single();

        var result = AuctionsApiCanonicalShadowComparer.Compare(legacy, canonical);

        Assert.Contains("body_style", result.Differences);
        Assert.Contains("primary_damage", result.Differences);
    }

    [Fact]
    public void Shadow_comparison_against_real_iaai_fixture_preserves_official_values()
    {
        var payload = ReadFixture("iaai_cars_page1.json");
        var row = payload.GetProperty("data")[0];
        var legacy = AuctionsApiIncrementalSyncProcessor.MapRows(new[] { row }, "iaai").Single();
        var provider = AuctionsApiCanonicalMapper.MapVehicle(row, "iaai");
        var canonical = AuctionsApiCanonicalMapper.ToAuctionVehicles(provider!).Single();

        Assert.Equal("sedan", canonical.VehicleSpecs!.BodyStyle);
        Assert.Equal("actual", provider!.Lots.Single().OdometerStatus!.NormalizedValue);
        Assert.NotNull(provider.Lots.Single().DamageMain);
        Assert.Equal(legacy.LotNumber, canonical.LotNumber);
    }

    [Theory]
    [InlineData("copart", "copart_cars_page1.json")]
    [InlineData("iaai", "iaai_cars_page1.json")]
    public void Canonical_preserves_old_mapper_critical_fields(string platform, string fixture)
    {
        var payload = ReadFixture(fixture);
        var row = payload.GetProperty("data")[0];
        var legacy = AuctionsApiIncrementalSyncProcessor.MapRows(new[] { row }, platform).Single();
        var provider = AuctionsApiCanonicalMapper.MapVehicle(row, platform);
        var canonical = AuctionsApiCanonicalMapper.ToAuctionVehicles(provider!).Single();

        Assert.Equal(legacy.FuelType, canonical.FuelType);
        Assert.Equal(legacy.Transmission, canonical.Transmission);
        Assert.Equal(legacy.DriveType, canonical.DriveType);
        Assert.Equal(legacy.Title, canonical.Title);
        Assert.Equal(legacy.SaleDocument?.Name, canonical.SaleDocument?.Name);
        Assert.Equal(legacy.Condition?.HasKey, canonical.Condition?.HasKey);
        Assert.Equal(legacy.VehicleSpecs?.Airbags, canonical.VehicleSpecs?.Airbags);
        Assert.Equal(legacy.Condition?.RunCondition?.Value, canonical.Condition?.RunCondition?.Value);
        Assert.Equal(legacy.OdometerInfo?.Miles, canonical.OdometerInfo?.Miles);
        Assert.Equal(legacy.OdometerInfo?.Status, canonical.OdometerInfo?.Status);
        Assert.Equal(legacy.Auction?.IsTimed, canonical.Auction?.IsTimed);
        Assert.Equal(legacy.Pricing?.CurrentBidUsd, canonical.Pricing?.CurrentBidUsd);
        Assert.Equal(legacy.Pricing?.BuyNowUsd, canonical.Pricing?.BuyNowUsd);
        Assert.NotNull(canonical.Location?.City);
        Assert.NotNull(canonical.Location?.State);
    }

    [Fact]
    public void Maps_engine_size_horsepower_and_cylinders_from_provider_payload()
    {
        using var document = JsonDocument.Parse("""
        {
          "year": 2010,
          "manufacturer": { "name": "Infiniti" },
          "model": { "name": "QX56" },
          "engine": { "name": "V8", "size_l": 5.6, "hp": 400 },
          "cylinders": 8,
          "fuel": { "name": "gasoline" },
          "lots": [{ "lot": "65360076", "vin": "5N3ZA0NE5AN906029", "sale_date": "2026-09-18T14:30:00Z" }]
        }
        """);

        var provider = AuctionsApiCanonicalMapper.MapVehicle(document.RootElement, "copart");
        var vehicle = Assert.Single(AuctionsApiCanonicalMapper.ToAuctionVehicles(provider!));

        Assert.Equal("V8", vehicle.VehicleSpecs!.Engine!.Layout);
        Assert.Equal("5.6", vehicle.VehicleSpecs.Engine.SizeLiters);
        Assert.Equal(400m, vehicle.VehicleSpecs.Engine.Horsepower);
        Assert.Equal("8", vehicle.Details!.VehicleDescription!.Cylinders);
    }

    private static JsonElement ReadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
