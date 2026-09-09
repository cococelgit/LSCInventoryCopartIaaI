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
        Assert.NotNull(mapped.Engine);
        Assert.Equal(4, mapped.Cylinders);
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

    private static JsonElement ReadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }
}
