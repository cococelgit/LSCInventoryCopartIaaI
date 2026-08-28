using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Lsc.Inventory.Api.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartTitleBackfillProcessorTests
{
    [Fact]
    public async Task Backfill_persists_mapped_title_and_records_a_dedicated_run()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero);
        var vehicle = new AuctionVehicle
        {
            Platform = "copart",
            LotNumber = "12345678",
            Title = "AQ",
            SaleDocument = new SaleDocument { Name = "AQ", State = "FL" },
            RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["Sale Title Type"] = "AQ" })
        };
        await store.PersistAsync(vehicle, observedAt, CancellationToken.None);
        var processor = new CopartTitleBackfillProcessor(
            store,
            new OptionsWrapper<CopartExcelOptions>(new CopartExcelOptions { TitleBackfillBatchSize = 10, TitleBackfillConcurrency = 2 }),
            NullLogger<CopartTitleBackfillProcessor>.Instance);

        var result = await processor.RunAsync(CancellationToken.None);
        var persisted = Assert.Single(await store.GetRecentAsync(10, CancellationToken.None));
        var run = Assert.Single(store.SyncRuns.Values);

        Assert.True(result.Processed);
        Assert.Equal(1, result.Mapped);
        Assert.Equal(0, result.Unmapped);
        Assert.Equal("Clear Title", persisted.Vehicle.Title);
        Assert.Equal("mapped", persisted.Vehicle.AdditionalData!["source_title_mapping"].GetString());
        Assert.Equal("copart-title-backfill", run.Start.Provider);
        Assert.NotNull(run.Completion);
    }

    [Fact]
    public async Task Backfill_never_selects_iaai_records()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistAsync(new AuctionVehicle { Platform = "iaai", LotNumber = "76543210", Title = "AQ" }, DateTimeOffset.UtcNow, CancellationToken.None);
        var processor = new CopartTitleBackfillProcessor(
            store,
            new OptionsWrapper<CopartExcelOptions>(new CopartExcelOptions()),
            NullLogger<CopartTitleBackfillProcessor>.Instance);

        var result = await processor.RunAsync(CancellationToken.None);

        Assert.True(result.Processed);
        Assert.Equal(0, result.Candidates);
        Assert.Equal(0, result.Mapped);
        Assert.Equal(0, result.Unmapped);
    }
}
