using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class InventoryScoringBaselineStoreTests
{
    [Fact]
    public async Task Persisted_active_vehicle_can_be_scored_and_read_through_the_baseline_contract()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.Parse("2026-09-01T00:00:00Z");
        var vehicle = new AuctionVehicle
        {
            Platform = "iaai",
            LotNumber = "91234567",
            Vin = "1HGCM82633A004352",
            Year = 2018,
            Make = "Honda",
            Model = "Accord",
            Auction = new AuctionInfo { AuctionAt = DateTimeOffset.Parse("2026-09-01T14:00:00Z") },
            Location = new VehicleLocation { State = "FL", FacilityId = "366" },
            Seller = new AuctionSeller { Name = "Insurance Company", Type = "Insurance" },
            Condition = new VehicleCondition
            {
                PrimaryDamage = "Front End",
                HasKey = true,
                RunCondition = new RunConditionInfo { Value = "RUNS AND DRIVES" }
            },
            OdometerInfo = new OdometerInfo { Miles = 42_000, Status = "ACTUAL" },
            Media = new MediaInfo { Photos = ["https://images.example.test/lot.jpg"] },
            SaleDocument = new SaleDocument { Name = "CLEAR", IsPending = false }
        };

        await store.PersistAsync(vehicle, observedAt, CancellationToken.None);
        var batch = await store.ProcessScoringBatchAsync(10, CancellationToken.None);
        var score = await store.GetScoreByLotAsync("91234567", CancellationToken.None);
        var status = await store.GetScoringOperationalStatusAsync(CancellationToken.None);

        Assert.Equal(1, batch.Claimed);
        Assert.Equal(1, batch.Completed);
        Assert.NotNull(score);
        Assert.Equal("lsc_pre_grade_v1", score.PolicyVersion);
        Assert.Equal("PRE_GRADED", score.Status);
        Assert.Contains(status.Platforms!, platform => platform.Platform == "iaai" && platform.Current == 1);
    }

    [Fact]
    public async Task Direct_persistence_uses_the_same_lsc_pre_grade_v1_engine_contract()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.Parse("2026-09-01T01:00:00Z");
        var vehicle = new AuctionVehicle
        {
            Platform = "iaai",
            LotNumber = "91234568",
            Vin = "1HGCM82633A004353",
            Year = 2018,
            Make = "Honda",
            Model = "Accord",
            Auction = new AuctionInfo { AuctionAt = DateTimeOffset.Parse("2026-09-01T14:00:00Z") },
            Location = new VehicleLocation { State = "FL", FacilityId = "366" },
            Seller = new AuctionSeller { Name = "Insurance Company", Type = "Insurance" },
            Condition = new VehicleCondition { PrimaryDamage = "Front End", HasKey = true, RunCondition = new RunConditionInfo { Value = "RUNS AND DRIVES" } },
            OdometerInfo = new OdometerInfo { Miles = 42_000, Status = "ACTUAL" },
            SaleDocument = new SaleDocument { Name = "CLEAR", IsPending = false }
        };

        await store.PersistAsync(vehicle, observedAt, CancellationToken.None);
        var result = await store.PersistScoringResultAsync(vehicle, AuctionEligibilityEvaluator.Evaluate(vehicle, observedAt), observedAt, CancellationToken.None);
        var stored = await store.GetScoreByLotAsync(vehicle.LotNumber!, CancellationToken.None);

        Assert.Equal("lsc_pre_grade_v1", result.PolicyVersion);
        Assert.Equal(result.InputHash, stored?.InputHash);
        Assert.Equal(result.PreGrade, stored?.PreGrade);
    }
}
