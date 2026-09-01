using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Lsc.Inventory.Api.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartScoringBackfillProcessorTests
{
    [Fact]
    public async Task Scores_existing_active_copart_without_using_global_queue()
    {
        var store = new InMemorySnapshotStore();
        var vehicle = ValidVehicle("copart", "70000001");
        await store.PersistAsync(vehicle, DateTimeOffset.Parse("2026-09-01T10:00:00Z"), CancellationToken.None);
        var processor = CreateProcessor(store);

        var result = await processor.RunAsync(CancellationToken.None);
        var score = await store.GetScoreByLotAsync("70000001", CancellationToken.None);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Scored);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.Remaining);
        Assert.NotNull(score);
        Assert.Equal("lsc_pre_grade_v2", score!.PolicyVersion);
    }

    [Fact]
    public async Task Repeated_backfill_is_idempotent_when_score_is_current()
    {
        var store = new InMemorySnapshotStore();
        var vehicle = ValidVehicle("copart", "70000002");
        await store.PersistAsync(vehicle, DateTimeOffset.Parse("2026-09-01T10:00:00Z"), CancellationToken.None);
        var processor = CreateProcessor(store);
        var first = await processor.RunAsync(CancellationToken.None);
        var before = await store.GetScoreByLotAsync("70000002", CancellationToken.None);
        var second = await processor.RunAsync(CancellationToken.None);
        var after = await store.GetScoreByLotAsync("70000002", CancellationToken.None);

        Assert.Equal(1, first.Scored);
        Assert.Equal(0, second.Scanned);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before!.InputHash, after!.InputHash);
        Assert.Equal(before.ScoredAt, after.ScoredAt);
    }

    [Fact]
    public async Task Does_not_select_iaai_records()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistAsync(ValidVehicle("iaai", "70000003"), DateTimeOffset.Parse("2026-09-01T10:00:00Z"), CancellationToken.None);

        var result = await CreateProcessor(store).RunAsync(CancellationToken.None);

        Assert.Equal(0, result.Scanned);
        Assert.Null(await store.GetScoreByLotAsync("70000003", CancellationToken.None));
    }

    [Fact]
    public async Task Persistence_failure_remains_pending_for_explicit_recovery()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistAsync(ValidVehicle("copart", "70000004"), DateTimeOffset.Parse("2026-09-01T10:00:00Z"), CancellationToken.None);
        store.FailNextScoringPersistence = true;

        var result = await CreateProcessor(store).RunAsync(CancellationToken.None);

        Assert.Equal(1, result.Scanned);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Remaining);
        Assert.Null(await store.GetScoreByLotAsync("70000004", CancellationToken.None));
    }

    private static CopartScoringBackfillProcessor CreateProcessor(InMemorySnapshotStore store) => new(
        store,
        new OptionsWrapper<CopartExcelOptions>(new CopartExcelOptions
        {
            ScoringBackfillBatchSize = 25,
            ScoringBackfillConcurrency = 2
        }),
        NullLogger<CopartScoringBackfillProcessor>.Instance);

    private static AuctionVehicle ValidVehicle(string platform, string lotNumber) => new()
    {
        Platform = platform,
        LotNumber = lotNumber,
        Vin = "1HGCM82633A004352",
        Year = 2018,
        Make = "Honda",
        Model = "Accord",
        Auction = new AuctionInfo { AuctionAt = DateTimeOffset.Parse("2099-12-31T14:00:00Z"), LotStatus = "Open" },
        Location = new VehicleLocation { State = "FL", FacilityId = "100" },
        Seller = new AuctionSeller { Name = "Insurance Company" },
        SaleDocument = new SaleDocument { Name = "Clear Title", IsPending = false },
        Condition = new VehicleCondition { PrimaryDamage = "Front End", HasKey = true, RunCondition = new RunConditionInfo { Normalized = "RUNS_AND_DRIVES", Raw = "RUN & DRIVE" } },
        OdometerInfo = new OdometerInfo { Miles = 50_000, Status = "ACTUAL" },
        Media = new MediaInfo { Photos = ["https://cs.copart.com/v1/example.jpg"] },
        Pricing = new PricingInfo { CurrentBidUsd = 5_000m }
    };
}
