using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Storage;
using Lsc.Inventory.Api.Workers;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CanonicalInventoryIngestionPipelineTests
{
    [Fact]
    public async Task Shadow_and_active_use_the_same_eligibility_result()
    {
        var store = new InMemorySnapshotStore();
        var pipeline = new CanonicalInventoryIngestionPipeline(store);
        var vehicle = ValidVehicle();
        var observedAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z");

        var shadow = await pipeline.ProcessAsync(vehicle, observedAt, CancellationToken.None, persist: false);
        var active = await pipeline.ProcessAsync(vehicle, observedAt, CancellationToken.None, persist: true);

        Assert.Equal(shadow.Eligibility.Decision, active.Eligibility.Decision);
        Assert.Equal(shadow.Eligibility.LoadToSystem, active.Eligibility.LoadToSystem);
        Assert.True(shadow.Loaded);
        Assert.Null(shadow.Persistence);
        Assert.NotNull(active.Persistence);
        Assert.NotNull(await store.GetByPlatformAndLotAsync("iaai", "12345678", CancellationToken.None));
    }

    [Fact]
    public async Task Canonical_pipeline_preserves_provider_raw_source_on_persistence()
    {
        var store = new InMemorySnapshotStore();
        var pipeline = new CanonicalInventoryIngestionPipeline(store);
        var raw = ValidVehicle() with { RawSource = System.Text.Json.JsonDocument.Parse("{\"provider\":\"auctionsapi\"}").RootElement };

        var result = await pipeline.ProcessAsync(raw, DateTimeOffset.Parse("2026-09-01T12:00:00Z"), CancellationToken.None);

        Assert.True(result.Loaded);
        Assert.NotNull(result.Persistence);
        var stored = await store.GetByPlatformAndLotAsync("iaai", "12345678", CancellationToken.None);
        Assert.NotNull(stored);
        Assert.Equal("auctionsapi", stored!.Vehicle.RawSource?.GetProperty("provider").GetString());
    }

    private static AuctionVehicle ValidVehicle() => new()
    {
        Platform = "iaai",
        LotNumber = "12345678",
        Vin = "1HGCM82633A004352",
        Year = 2012,
        Make = "Honda",
        Model = "Accord",
        Auction = new AuctionInfo { AuctionAt = DateTimeOffset.Parse("2099-09-01T14:00:00Z") },
        Location = new VehicleLocation { State = "FL", FacilityId = "366" },
        Seller = new AuctionSeller { Name = "Insurance Company" },
        Condition = new VehicleCondition { PrimaryDamage = "Front End", HasKey = true, RunCondition = new RunConditionInfo { Value = "RUNS AND DRIVES" } },
        OdometerInfo = new OdometerInfo { Miles = 50_000, Status = "ACTUAL" },
        Media = new MediaInfo { Photos = ["https://images.example.test/lot.jpg"] },
        SaleDocument = new SaleDocument { Name = "Certificate of Title", IsPending = false }
    };
}
