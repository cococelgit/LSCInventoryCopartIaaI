using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Lsc.Inventory.Api.Workers;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class InventoryScoringProcessorTests
{
    [Fact]
    public async Task Processes_persisted_lots_in_bounded_batches_and_is_idempotent_afterward()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistAsync(ValidVehicle(), DateTimeOffset.Parse("2026-08-27T12:00:00Z"), CancellationToken.None);
        var processor = new InventoryScoringProcessor(store, Microsoft.Extensions.Options.Options.Create(new ScoringOptions { BackfillMaximumLots = 10, BatchSize = 1 }));

        var first = await processor.RunBackfillAsync(10, CancellationToken.None);
        var status = await store.GetScoringOperationalStatusAsync(CancellationToken.None);
        var score = await store.GetScoreByLotAsync("12345678", CancellationToken.None);
        var second = await processor.RunBackfillAsync(10, CancellationToken.None);

        Assert.Equal(1, first.Completed);
        Assert.Equal(1, first.Batches);
        Assert.Equal(0, first.Failed);
        Assert.NotEqual(Guid.Empty, first.RunId);
        Assert.Equal(1, first.HighPriorityClaimed);
        Assert.Equal(1, status.Completed);
        Assert.Equal(0, status.Queued);
        Assert.NotNull(score);
        Assert.Equal("PRE_GRADED", score!.Status);
        Assert.Equal(0, second.Completed);
        Assert.Equal(1, second.Backfill.AlreadyCurrent);
        var runs = await store.GetRecentScoringRunsAsync(10, CancellationToken.None);
        var firstRun = Assert.Single(runs.Where(run => run.RunId == first.RunId));
        Assert.Equal("completed", firstRun.Status);
        Assert.Equal("manual-api", firstRun.Trigger);
        Assert.Equal(1, firstRun.Completed);
    }

    [Fact]
    public async Task Requeues_a_lot_when_the_persisted_scoring_input_changes()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.Parse("2026-08-27T12:00:00Z");
        var processor = new InventoryScoringProcessor(store, Microsoft.Extensions.Options.Options.Create(new ScoringOptions { BackfillMaximumLots = 10, BatchSize = 10 }));
        await store.PersistAsync(ValidVehicle(), observedAt, CancellationToken.None);
        await processor.RunBackfillAsync(10, CancellationToken.None);
        var first = await store.GetScoreByLotAsync("12345678", CancellationToken.None);

        await store.PersistAsync(ValidVehicle() with { Condition = ValidVehicle().Condition! with { PrimaryDamage = "Rear" } }, observedAt.AddMinutes(30), CancellationToken.None);
        var statusBefore = await store.GetScoringOperationalStatusAsync(CancellationToken.None);
        await processor.ProcessBatchAsync(10, CancellationToken.None);
        var second = await store.GetScoreByLotAsync("12345678", CancellationToken.None);

        Assert.Equal(1, statusBefore.Queued);
        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.InputHash, second!.InputHash);
    }

    private static AuctionVehicle ValidVehicle() => new()
    {
        Platform = "iaai",
        LotNumber = "12345678",
        Vin = "1HGCM82633A004352",
        Year = 2012,
        Make = "Honda",
        Model = "Accord",
        Auction = new AuctionInfo { AuctionAt = DateTimeOffset.Parse("2099-08-31T14:00:00Z") },
        Location = new VehicleLocation { State = "FL", FacilityId = "366" },
        Seller = new AuctionSeller { Name = "Insurance Company", Type = "Insurance" },
        Condition = new VehicleCondition { PrimaryDamage = "Front End", HasKey = true, RunCondition = new RunConditionInfo { Value = "RUNS AND DRIVES" } },
        OdometerInfo = new OdometerInfo { Miles = 50_000, Status = "ACTUAL" },
        Media = new MediaInfo { Photos = ["https://images.example.test/lot.jpg"] },
        SaleDocument = new SaleDocument { Name = "CLEAR", IsPending = false }
    };
}
