using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Lsc.Inventory.Api.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class IaaINationalSyncProcessorTests
{
    [Fact]
    public async Task Completes_the_audit_and_keeps_the_cursor_when_the_provider_failure_is_exhausted()
    {
        var client = new AlwaysFailingListClient();
        var store = new InMemorySnapshotStore();
        var processor = CreateProcessor(client, store, Microsoft.Extensions.Options.Options.Create(new IaaINationalOptions
        {
            Enabled = true,
            BackfillPagesPerRun = 1,
            BackfillMaxRequestsPerRun = 3,
            EnrichVehicleDetails = false,
            CaptureUsage = false
        }));

        var result = await processor.RunAsync(CancellationToken.None);
        var checkpoint = await store.GetNationalSyncCheckpointAsync("iaai-national-open", CancellationToken.None);
        var history = await store.GetExecutionHistoryAsync(new InventoryExecutionHistoryRequest(1, 10, "iaai"), CancellationToken.None);

        Assert.NotEmpty(result.Failures);
        Assert.Equal(0, result.PagesProcessed);
        Assert.Null(checkpoint.Cursor);
        Assert.Single(history.Items);
        Assert.Equal("completed_with_errors", history.Items[0].Status);
        Assert.NotNull(history.Items[0].FinishedAt);
    }

    [Fact]
    public async Task Resumes_from_persisted_cursor_and_reconciles_only_after_the_complete_cycle()
    {
        var client = new CursorClient();
        var store = new InMemorySnapshotStore();
        var options = Microsoft.Extensions.Options.Options.Create(new IaaINationalOptions
        {
            Enabled = true,
            PagesPerRun = 1,
            MaxRequestsPerRun = 4,
            BackfillPagesPerRun = 1,
            BackfillMaxRequestsPerRun = 4,
            EnrichVehicleDetails = false,
            CaptureUsage = false,
        });
        var processor = CreateProcessor(client, store, options);

        var first = await processor.RunAsync(CancellationToken.None);
        var checkpoint = await store.GetNationalSyncCheckpointAsync("iaai-national-open", CancellationToken.None);

        Assert.False(first.CycleCompleted);
        Assert.Equal(2, first.Loaded);
        Assert.Equal("page-2", checkpoint.Cursor);
        Assert.False(checkpoint.CycleCompleted);
        Assert.Equal(1, client.ListRequests);
        Assert.All(client.Requests, request => Assert.Equal("iaai", request.Platform));

        var second = await processor.RunAsync(CancellationToken.None);
        var completed = await store.GetNationalSyncCheckpointAsync("iaai-national-open", CancellationToken.None);

        Assert.True(second.CycleCompleted);
        Assert.NotNull(second.Reconciliation);
        Assert.Equal(2, client.ListRequests);
        Assert.Equal(4, (await store.GetRecentAsync(10, CancellationToken.None)).Count);
        Assert.True(completed.CycleCompleted);
        Assert.Null(completed.Cursor);

        var firstPage = await store.SearchAsync(new InventorySearchRequest(1, 1, Makes: ["Honda"]), CancellationToken.None);
        var secondPage = await store.SearchAsync(new InventorySearchRequest(2, 1, Makes: ["Honda"]), CancellationToken.None);
        Assert.Equal(4, firstPage.Total);
        Assert.Single(firstPage.Items);
        Assert.Single(secondPage.Items);
        Assert.NotEqual(firstPage.Items.Single().Identity, secondPage.Items.Single().Identity);

        var summary = await store.GetInventorySearchSummaryAsync(new InventorySearchRequest(1, 1), CancellationToken.None);
        Assert.Equal(4, summary.Total);
        Assert.Single(summary.Facets["platforms"]);
        Assert.Equal("iaai", summary.Facets["platforms"].Single().Value);
        Assert.Equal(4, summary.Facets["makes"].Single().Count);
    }

    [Fact]
    public async Task Treats_a_reached_request_budget_as_a_successful_bounded_batch()
    {
        var client = new CursorClient();
        var store = new InMemorySnapshotStore();
        var processor = CreateProcessor(client, store, Microsoft.Extensions.Options.Options.Create(new IaaINationalOptions
        {
            Enabled = true,
            BackfillPagesPerRun = 10,
            BackfillMaxRequestsPerRun = 1,
            EnrichVehicleDetails = false,
            CaptureUsage = false
        }));

        var result = await processor.RunAsync(CancellationToken.None);
        var checkpoint = await store.GetNationalSyncCheckpointAsync("iaai-national-open", CancellationToken.None);
        var history = await store.GetExecutionHistoryAsync(new InventoryExecutionHistoryRequest(1, 10, "iaai"), CancellationToken.None);

        Assert.Empty(result.Failures);
        Assert.False(result.CycleCompleted);
        Assert.Equal(1, result.PagesProcessed);
        Assert.Equal("page-2", checkpoint.Cursor);
        Assert.Equal("succeeded", Assert.Single(history.Items).Status);
    }

    [Fact]
    public async Task Replaces_one_expired_cursor_and_continues_with_a_fresh_cycle()
    {
        var client = new RecoveringCursorClient(failFreshCursor: false);
        var store = new InMemorySnapshotStore();
        await store.PersistNationalSyncBatchAsync(
            new NationalSyncBatch("iaai-national-open", Guid.NewGuid(), "expired-cursor", 8, 160, [], DateTimeOffset.UtcNow, false, false),
            CancellationToken.None);
        var processor = CreateProcessor(client, store, Microsoft.Extensions.Options.Options.Create(new IaaINationalOptions
        {
            Enabled = true,
            BackfillPagesPerRun = 3,
            BackfillMaxRequestsPerRun = 4,
            EnrichVehicleDetails = false,
            CaptureUsage = false
        }));

        var result = await processor.RunAsync(CancellationToken.None);
        var checkpoint = await store.GetNationalSyncCheckpointAsync("iaai-national-open", CancellationToken.None);

        Assert.True(result.CycleCompleted);
        Assert.False(result.ShouldRetry);
        Assert.Empty(result.Failures);
        Assert.Equal(3, client.ListRequests);
        Assert.Equal(new[] { "expired-cursor", null, "fresh-cursor" }, client.Cursors);
        Assert.True(checkpoint.CycleCompleted);
    }

    [Fact]
    public async Task Stops_without_azure_retry_when_the_fresh_cursor_is_also_invalid()
    {
        var client = new RecoveringCursorClient(failFreshCursor: true);
        var store = new InMemorySnapshotStore();
        await store.PersistNationalSyncBatchAsync(
            new NationalSyncBatch("iaai-national-open", Guid.NewGuid(), "expired-cursor", 8, 160, [], DateTimeOffset.UtcNow, false, false),
            CancellationToken.None);
        var processor = CreateProcessor(client, store, Microsoft.Extensions.Options.Options.Create(new IaaINationalOptions
        {
            Enabled = true,
            BackfillPagesPerRun = 3,
            BackfillMaxRequestsPerRun = 4,
            EnrichVehicleDetails = false,
            CaptureUsage = false
        }));

        var result = await processor.RunAsync(CancellationToken.None);

        Assert.False(result.ShouldRetry);
        Assert.Single(result.Failures);
        Assert.StartsWith("cursor-invalid:", result.Failures[0]);
        Assert.Equal(3, client.ListRequests);
        Assert.Equal(new[] { "expired-cursor", null, "fresh-cursor" }, client.Cursors);
    }

    [Fact]
    public async Task Skips_when_another_national_run_holds_the_distributed_lease()
    {
        var client = new CursorClient();
        var store = new InMemorySnapshotStore();
        var held = await store.TryAcquireLeaseAsync("iaai-national-sync", Guid.NewGuid(), DateTimeOffset.UtcNow, TimeSpan.FromMinutes(30), CancellationToken.None);
        Assert.True(held.Acquired);

        var processor = CreateProcessor(client, store, Microsoft.Extensions.Options.Options.Create(new IaaINationalOptions { Enabled = true, EnrichVehicleDetails = false, CaptureUsage = false }));
        var result = await processor.RunAsync(CancellationToken.None);

        Assert.True(result.Skipped);
        Assert.Equal("lease-active", result.SkipReason);
        Assert.Equal(0, client.ListRequests);
    }

    [Fact]
    public async Task Exposes_checkpoint_and_active_lease_for_operational_diagnostics()
    {
        var store = new InMemorySnapshotStore();
        var runId = Guid.NewGuid();
        var acquiredAt = DateTimeOffset.UtcNow;
        var lease = await store.TryAcquireLeaseAsync("iaai-national-sync", runId, acquiredAt, TimeSpan.FromMinutes(15), CancellationToken.None);
        Assert.True(lease.Acquired);

        await store.PersistNationalSyncBatchAsync(
            new NationalSyncBatch("iaai-national-open", Guid.NewGuid(), "cursor-42", 42, 840, [], acquiredAt, false, false),
            CancellationToken.None);

        var status = await store.GetNationalSyncOperationalStatusAsync("iaai-national-open", CancellationToken.None);

        Assert.True(status.LeaseActive);
        Assert.Equal("cursor-42", status.Checkpoint.Cursor);
        Assert.Equal(42, status.Checkpoint.PagesCompleted);
        Assert.False(status.Checkpoint.CycleCompleted);
        Assert.NotNull(status.LeaseExpiresAt);
    }

    [Fact]
    public async Task Records_execution_summary_and_lot_events_for_the_internal_audit()
    {
        var client = new CursorClient();
        var store = new InMemorySnapshotStore();
        var processor = CreateProcessor(client, store, Microsoft.Extensions.Options.Options.Create(new IaaINationalOptions
        {
            Enabled = true,
            PagesPerRun = 1,
            BackfillPagesPerRun = 1,
            BackfillMaxRequestsPerRun = 4,
            EnrichVehicleDetails = false,
            CaptureUsage = false,
        }));

        var result = await processor.RunAsync(CancellationToken.None);
        var history = await store.GetExecutionHistoryAsync(new InventoryExecutionHistoryRequest(1, 25, "iaai"), CancellationToken.None);
        var events = await store.GetExecutionEventsAsync(result.RunId, 1, 50, CancellationToken.None);

        var execution = Assert.Single(history.Items);
        Assert.Equal(2, execution.Loaded);
        Assert.Equal(2, execution.Created);
        Assert.Equal(0, execution.Updated);
        Assert.Equal(2, events.Total);
        Assert.All(events.Items, item => Assert.Equal("created", item.Action));
        Assert.All(events.Items, item => Assert.StartsWith("*************4352", item.VinMasked));
    }

    [Fact]
    public async Task Rebuilds_the_search_projection_idempotently_and_reports_its_row_count()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.UtcNow;
        await store.PersistAsync(new AuctionVehicle
        {
            Platform = "iaai",
            LotNumber = "projection-1",
            Year = 2024,
            Make = "Ford",
            Model = "F-150",
        }, observedAt, CancellationToken.None);

        var first = await store.RebuildSearchProjectionAsync(CancellationToken.None);
        var second = await store.RebuildSearchProjectionAsync(CancellationToken.None);

        Assert.True(first.Ready);
        Assert.Equal(1, first.Rows);
        Assert.True(second.Ready);
        Assert.Equal(first.Rows, second.Rows);
        Assert.NotNull(second.FacetsRefreshedAt);
    }

    private static IaaINationalSyncProcessor CreateProcessor(IApibaraClient client, InMemorySnapshotStore store, IOptions<IaaINationalOptions> options) => new(
        client,
        store,
        Microsoft.Extensions.Options.Options.Create(new ApibaraOptions { ApiKey = "test", PageSize = 20 }),
        options,
        NullLogger<IaaINationalSyncProcessor>.Instance);

    private sealed class CursorClient : IApibaraClient
    {
        public int ListRequests { get; private set; }
        public List<VehicleSearchRequest> Requests { get; } = [];

        public Task<VehicleListResponse> SearchVehiclesAsync(VehicleSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            ListRequests++;
            var secondPage = string.Equals(request.Cursor, "page-2", StringComparison.Ordinal);
            var prefix = secondPage ? "2" : "1";
            var vehicles = Enumerable.Range(1, 2).Select(number => new AuctionVehicle
            {
                Platform = "iaai",
                LotNumber = $"{prefix}0000{number}",
                Vin = "1HGCM82633A004352",
                Year = 2020,
                Make = "Honda",
                Model = "Accord",
                Auction = new AuctionInfo { AuctionAt = DateTimeOffset.UtcNow.AddDays(5), LotSubStatus = "Open" },
                Location = new VehicleLocation { Display = "Orlando-North (FL)" },
                Seller = new AuctionSeller { Name = "Insurance Company" },
                Condition = new VehicleCondition { PrimaryDamage = "Front End", HasKey = true, RunCondition = new RunConditionInfo { Value = "RUNS AND DRIVES" } },
                OdometerInfo = new OdometerInfo { Miles = 40_000, Status = "ACTUAL" },
                SaleDocument = new SaleDocument { Name = "CERTIFICATE OF TITLE", IsPending = false },
                Media = new MediaInfo { Photos = ["https://vis.iaai.com/resizer?imageKeys=test&width=640"] },
            }).ToArray();
            return Task.FromResult(new VehicleListResponse(vehicles, new CursorMeta(20, secondPage ? null : "page-2", null)));
        }

        public Task<LocationsResponse> GetLocationsAsync(string platform, string state, int perPage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VehicleDetailsResponse> GetVehicleAsync(string vinOrLot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UsageResponse> GetUsageAsync(CancellationToken cancellationToken) => Task.FromResult(new UsageResponse(JsonSerializer.SerializeToElement(new { })));
    }

    private sealed class AlwaysFailingListClient : IApibaraClient
    {
        public Task<VehicleListResponse> SearchVehiclesAsync(VehicleSearchRequest request, CancellationToken cancellationToken) =>
            Task.FromException<VehicleListResponse>(new HttpRequestException("Apibara returned 502 for vehicles", null, System.Net.HttpStatusCode.BadGateway));

        public Task<LocationsResponse> GetLocationsAsync(string platform, string state, int perPage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VehicleDetailsResponse> GetVehicleAsync(string vinOrLot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UsageResponse> GetUsageAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RecoveringCursorClient(bool failFreshCursor) : IApibaraClient
    {
        public int ListRequests { get; private set; }
        public List<string?> Cursors { get; } = [];

        public Task<VehicleListResponse> SearchVehiclesAsync(VehicleSearchRequest request, CancellationToken cancellationToken)
        {
            ListRequests++;
            Cursors.Add(request.Cursor);
            if (request.Cursor == "expired-cursor" || (failFreshCursor && request.Cursor == "fresh-cursor"))
                throw new ApibaraInvalidCursorException("Apibara rejected an opaque cursor for vehicles");

            if (request.Cursor == "fresh-cursor")
                return Task.FromResult(new VehicleListResponse([], new CursorMeta(20, null, null)));

            var vehicle = new AuctionVehicle
            {
                Platform = "iaai",
                LotNumber = "recovered-1",
                Vin = "1HGCM82633A004352",
                Year = 2020,
                Make = "Honda",
                Model = "Accord",
                Auction = new AuctionInfo { AuctionAt = DateTimeOffset.UtcNow.AddDays(5), LotSubStatus = "Open" },
                Location = new VehicleLocation { Display = "Orlando-North (FL)" },
                Seller = new AuctionSeller { Name = "Insurance Company" },
                Condition = new VehicleCondition { PrimaryDamage = "Front End", HasKey = true, RunCondition = new RunConditionInfo { Value = "RUNS AND DRIVES" } },
                OdometerInfo = new OdometerInfo { Miles = 40_000, Status = "ACTUAL" },
                SaleDocument = new SaleDocument { Name = "CERTIFICATE OF TITLE", IsPending = false }
            };
            return Task.FromResult(new VehicleListResponse([vehicle], new CursorMeta(20, "fresh-cursor", null)));
        }

        public Task<LocationsResponse> GetLocationsAsync(string platform, string state, int perPage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VehicleDetailsResponse> GetVehicleAsync(string vinOrLot, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<UsageResponse> GetUsageAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
