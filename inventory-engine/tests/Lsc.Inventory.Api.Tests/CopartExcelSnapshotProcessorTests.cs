using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;
using Lsc.Inventory.Api.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartExcelSnapshotProcessorTests
{
    [Fact]
    public async Task Complete_snapshot_persists_eligible_rows_and_second_hash_is_idempotent()
    {
        var options = new OptionsWrapper<CopartExcelOptions>(new CopartExcelOptions
        {
            MinimumFileSizeKilobytes = 0,
            MaximumFileSizeMegabytes = 8,
            MinimumRowsForCompleteSnapshot = 1,
            ProcessingBatchSize = 2,
            MinimumRowCountRatioToRecentMedian = 0.70m,
            RecentSnapshotCountForBaseline = 3
        });
        var adapter = new CopartExcelSnapshotAdapter(options);
        var store = new InMemorySnapshotStore();
        var processor = new CopartExcelSnapshotProcessor(new ThrowingSnapshotSource(), adapter, store, options, NullLogger<CopartExcelSnapshotProcessor>.Instance);
        var csv = BuildCsv(4);

        var first = CreateSnapshot(csv);
        var result = await processor.ProcessAsync(first, CancellationToken.None);
        var persisted = await store.GetRecentAsync(10, CancellationToken.None);

        var duplicate = CreateSnapshot(csv);
        var duplicateResult = await processor.ProcessAsync(duplicate, CancellationToken.None);

        Assert.True(result.Processed);
        Assert.True(result.IsComplete);
        Assert.Equal(4, result.Observed);
        Assert.Equal(4, result.Accepted);
        Assert.Equal(0, result.Discarded);
        Assert.Equal(4, persisted.Count);
        Assert.Equal(4, store.CopartAuctionObservationCount);
        Assert.True(duplicateResult.IsDuplicate);
        Assert.False(duplicateResult.Processed);

        var runs = store.SyncRuns.Values.OrderBy(run => run.Start.StartedAt).ToArray();
        Assert.Equal(2, runs.Length);
        Assert.Equal("copart-excel", runs[0].Start.Provider);
        Assert.Equal("copart", runs[0].Start.Platform);
        Assert.Equal("all", runs[0].Start.State);
        Assert.Equal(result.Observed, runs[0].Completion!.VehiclesObserved);
        Assert.Equal("duplicate", runs[1].Start.State);
        Assert.Equal(4, runs[1].Completion!.VehiclesObserved);
        Assert.Empty(runs[1].Completion!.Failures);
    }

    [Fact]
    public async Task Failed_snapshot_hash_can_retry_but_successful_hash_remains_idempotent()
    {
        var store = new InMemorySnapshotStore();
        var receipt = new CopartSnapshotReceipt("salesdata.csv", "retryable-sha", DateTimeOffset.UtcNow, 2048, 1000, 100);

        var first = await store.TryRegisterCopartSnapshotAsync(receipt, 0.70m, 3, false, CancellationToken.None);
        await store.CompleteCopartSnapshotAsync(first.RunId!.Value,
            new CopartSnapshotCompletion(DateTimeOffset.UtcNow, 10, 0, 0, 0, 0, 1, false, ["row 10 failed"]),
            CancellationToken.None);

        var retry = await store.TryRegisterCopartSnapshotAsync(receipt, 0.70m, 3, false, CancellationToken.None);
        await store.CompleteCopartSnapshotAsync(retry.RunId!.Value,
            new CopartSnapshotCompletion(DateTimeOffset.UtcNow, 1000, 900, 100, 0, 0, 0, true, []),
            CancellationToken.None);
        var duplicate = await store.TryRegisterCopartSnapshotAsync(receipt, 0.70m, 3, false, CancellationToken.None);

        Assert.True(first.Accepted);
        Assert.True(retry.Accepted);
        Assert.False(retry.IsDuplicate);
        Assert.False(duplicate.Accepted);
        Assert.True(duplicate.IsDuplicate);

        var interruptedReceipt = receipt with { Sha256 = "interrupted-sha" };
        var interrupted = await store.TryRegisterCopartSnapshotAsync(interruptedReceipt, 0.70m, 3, false, CancellationToken.None);
        var blocked = await store.TryRegisterCopartSnapshotAsync(interruptedReceipt, 0.70m, 3, false, CancellationToken.None);
        var recovered = await store.TryRegisterCopartSnapshotAsync(interruptedReceipt, 0.70m, 3, true, CancellationToken.None);

        Assert.True(interrupted.Accepted);
        Assert.False(blocked.Accepted);
        Assert.True(blocked.IsDuplicate);
        Assert.True(recovered.Accepted);
    }

    [Fact]
    public async Task Copart_run_condition_is_preserved_in_persisted_snapshot_payload()
    {
        var options = new OptionsWrapper<CopartExcelOptions>(new CopartExcelOptions
        {
            MinimumFileSizeKilobytes = 0,
            MaximumFileSizeMegabytes = 8,
            MinimumRowsForCompleteSnapshot = 1,
            ProcessingBatchSize = 10
        });
        var adapter = new CopartExcelSnapshotAdapter(options);
        var store = new InMemorySnapshotStore();
        var processor = new CopartExcelSnapshotProcessor(new ThrowingSnapshotSource(), adapter, store, options, NullLogger<CopartExcelSnapshotProcessor>.Instance);

        var result = await processor.ProcessAsync(CreateSnapshot(BuildCsv(1, runDrives: "Engine Start Program")), CancellationToken.None);
        var snapshot = Assert.Single(await store.GetRecentAsync(10, CancellationToken.None));
        using var payload = JsonDocument.Parse(snapshot.RawJson);
        var runCondition = payload.RootElement.GetProperty("condition").GetProperty("run_condition");

        Assert.True(result.Processed);
        Assert.Equal("STARTS", snapshot.Vehicle.Condition!.RunCondition!.Normalized);
        Assert.Equal("Engine Start Program", snapshot.Vehicle.Condition.RunCondition.Raw);
        Assert.Equal("STARTS", runCondition.GetProperty("run_condition").GetString());
        Assert.Equal("Engine Start Program", runCondition.GetProperty("run_condition_raw").GetString());
    }

    [Fact]
    public async Task Eligible_copart_lot_is_persisted_with_canonical_pregrade_inline()
    {
        var options = TestOptions();
        var store = new InMemorySnapshotStore();
        var processor = new CopartExcelSnapshotProcessor(new ThrowingSnapshotSource(), new CopartExcelSnapshotAdapter(options), store, options, NullLogger<CopartExcelSnapshotProcessor>.Instance);

        var result = await processor.ProcessAsync(CreateSnapshot(BuildCsv(1)), CancellationToken.None);
        var score = await store.GetScoreByLotAsync("12345678", CancellationToken.None);

        Assert.True(result.Processed);
        Assert.True(result.IsComplete);
        Assert.Equal(1, result.Accepted);
        Assert.NotNull(score);
        Assert.Equal("lsc_pre_grade_v1", score!.PolicyVersion);
        Assert.Equal("PRE_GRADED", score.Status);
        Assert.Equal(1, result.InlineScoring!.Created);
        Assert.Equal(1, result.InlineScoring.ScoredInline);
        Assert.Equal(0, result.InlineScoring.ScoreFailed);
    }

    [Fact]
    public async Task Marked_copart_lot_is_persisted_with_manual_review_score()
    {
        var options = TestOptions();
        var store = new InMemorySnapshotStore();
        var processor = new CopartExcelSnapshotProcessor(new ThrowingSnapshotSource(), new CopartExcelSnapshotAdapter(options), store, options, NullLogger<CopartExcelSnapshotProcessor>.Instance);

        var result = await processor.ProcessAsync(CreateSnapshot(BuildCsv(1, runDrives: "No Information")), CancellationToken.None);
        var score = await store.GetScoreByLotAsync("12345678", CancellationToken.None);

        Assert.True(result.Processed);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, result.Marked);
        Assert.NotNull(score);
        Assert.Equal("MANUAL_REVIEW", score!.Status);
        Assert.Equal(1, result.InlineScoring!.ScoredInline);
    }

    [Fact]
    public async Task Reprocessed_equivalent_copart_payload_skips_scoring_without_changing_scored_at()
    {
        var options = TestOptions();
        var store = new InMemorySnapshotStore();
        var processor = new CopartExcelSnapshotProcessor(new ThrowingSnapshotSource(), new CopartExcelSnapshotAdapter(options), store, options, NullLogger<CopartExcelSnapshotProcessor>.Instance);
        var csv = BuildCsv(1);

        await processor.ProcessAsync(CreateSnapshot(csv), CancellationToken.None);
        var before = await store.GetScoreByLotAsync("12345678", CancellationToken.None);
        var second = await processor.ProcessAsync(CreateSnapshot(csv.Replace("\n", "\r\n", StringComparison.Ordinal)), CancellationToken.None);
        var after = await store.GetScoreByLotAsync("12345678", CancellationToken.None);

        Assert.True(second.Processed);
        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(before!.InputHash, after!.InputHash);
        Assert.Equal(before.ScoredAt, after.ScoredAt);
        Assert.Equal(1, second.InlineScoring!.Unchanged);
        Assert.Equal(1, second.InlineScoring.ScoreSkippedUnchanged);
        Assert.Equal(0, second.InlineScoring.ScoredInline);
    }

    [Fact]
    public async Task Relevant_copart_input_change_recalculates_inline_score()
    {
        var options = TestOptions();
        var store = new InMemorySnapshotStore();
        var processor = new CopartExcelSnapshotProcessor(new ThrowingSnapshotSource(), new CopartExcelSnapshotAdapter(options), store, options, NullLogger<CopartExcelSnapshotProcessor>.Instance);

        await processor.ProcessAsync(CreateSnapshot(BuildCsv(1, runDrives: "Runs and Drives")), CancellationToken.None);
        var before = await store.GetScoreByLotAsync("12345678", CancellationToken.None);
        var second = await processor.ProcessAsync(CreateSnapshot(BuildCsv(1, runDrives: "Starts")), CancellationToken.None);
        var after = await store.GetScoreByLotAsync("12345678", CancellationToken.None);

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.NotEqual(before!.InputHash, after!.InputHash);
        Assert.Equal(1, second.InlineScoring!.Updated);
        Assert.Equal(1, second.InlineScoring.ScoredInline);
        Assert.Equal(0, second.InlineScoring.ScoreSkippedUnchanged);
    }

    [Fact]
    public async Task Inline_scoring_persistence_failure_does_not_publish_row_and_blocks_reconciliation()
    {
        var options = TestOptions();
        var store = new InMemorySnapshotStore { FailNextCopartInlineScoringPersistence = true };
        var processor = new CopartExcelSnapshotProcessor(new ThrowingSnapshotSource(), new CopartExcelSnapshotAdapter(options), store, options, NullLogger<CopartExcelSnapshotProcessor>.Instance);

        var result = await processor.ProcessAsync(CreateSnapshot(BuildCsv(1)), CancellationToken.None);

        Assert.False(result.IsComplete);
        Assert.Equal(1, result.Errors);
        Assert.Equal(0, result.Accepted);
        Assert.Null(result.Reconciliation);
        Assert.Equal(1, result.InlineScoring!.ScoreFailed);
        Assert.Empty(await store.GetRecentAsync(10, CancellationToken.None));
        Assert.Null(await store.GetScoreByLotAsync("12345678", CancellationToken.None));
    }

    [Fact]
    public async Task Missing_vin_is_audited_and_not_persisted()
    {
        var options = new OptionsWrapper<CopartExcelOptions>(new CopartExcelOptions
        {
            MinimumFileSizeKilobytes = 0,
            MaximumFileSizeMegabytes = 8,
            MinimumRowsForCompleteSnapshot = 1,
            ProcessingBatchSize = 10
        });
        var adapter = new CopartExcelSnapshotAdapter(options);
        var store = new InMemorySnapshotStore();
        var processor = new CopartExcelSnapshotProcessor(new ThrowingSnapshotSource(), adapter, store, options, NullLogger<CopartExcelSnapshotProcessor>.Instance);

        var snapshot = CreateSnapshot(BuildCsv(1, vin: string.Empty));
        var result = await processor.ProcessAsync(snapshot, CancellationToken.None);
        var persisted = await store.GetRecentAsync(10, CancellationToken.None);
        var audit = await store.GetDiscardedEligibilityDecisionsAsync(1, 10, "D00A", null, CancellationToken.None);

        Assert.True(result.Processed);
        Assert.Equal(1, result.Discarded);
        Assert.Empty(persisted);
        Assert.Equal(0, store.CopartAuctionObservationCount);
        Assert.Null(await store.GetScoreByLotAsync("12345678", CancellationToken.None));
        Assert.Equal(1, audit.Total);
    }

    private static IOptions<CopartExcelOptions> TestOptions() => new OptionsWrapper<CopartExcelOptions>(new CopartExcelOptions
    {
        MinimumFileSizeKilobytes = 0,
        MaximumFileSizeMegabytes = 8,
        MinimumRowsForCompleteSnapshot = 1,
        ProcessingBatchSize = 10,
        MinimumRowCountRatioToRecentMedian = 0.70m,
        RecentSnapshotCountForBaseline = 3
    });

    private sealed class ThrowingSnapshotSource : ICopartExcelSnapshotSource
    {
        public Task<CopartSnapshotLease> OpenLatestAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("This test provides snapshots directly.");
    }

    private static CopartSnapshotEnvelope CreateSnapshot(string csv)
    {
        var bytes = Encoding.UTF8.GetBytes(csv);
        return new CopartSnapshotEnvelope("salesdata.csv", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), DateTimeOffset.UtcNow, new MemoryStream(bytes));
    }

    private static string BuildCsv(int rows, string vin = "1HGCM82633A004352", string runDrives = "Runs and Drives")
    {
        const string header = "Lot number,VIN,Year,Make,Model Group,Model Detail,Vehicle Type,Sale Date M/D/CY,Sale time (HHMM),Time Zone,Damage Description,Secondary Damage,Sale Title Type,Special Note,Announcements,Location state,Location city,Location ZIP,Yard number,Yard name,Seller Name,Has Keys-Yes or No,Runs/Drives,Odometer,Odometer Brand,Sale Status,\"High Bid =non-vix,Sealed=Vix\",Buy-It-Now Price,Image Thumbnail\n";
        var builder = new StringBuilder(header);
        for (var index = 0; index < rows; index++)
            builder.Append($"{12345678 + index},{vin},2025,Honda,Accord,Accord LX,Automobile,12/31/2099,1300,EST,Normal Wear,Minor Dent,AQ,none,none,FL,Miami,33101,100,Miami Yard,Good Seller,Yes,{runDrives},10000,Actual,Open,5000,0,https://cs.copart.com/v1/AUTH_svc.pdoc00001/lpp/123.jpg\n");
        return builder.ToString();
    }
}
