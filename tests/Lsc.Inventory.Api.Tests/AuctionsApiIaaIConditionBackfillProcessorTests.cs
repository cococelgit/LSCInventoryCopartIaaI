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

public sealed class AuctionsApiIaaIConditionBackfillProcessorTests
{
    [Fact]
    public async Task Dry_run_allows_writes_disabled_and_does_not_persist_or_consume_Apibara()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistAsync(new AuctionVehicle
        {
            Platform = "iaai",
            LotNumber = "12345678",
            Vin = "1HGCM82633A004352",
            Year = 2020,
            Make = "Toyota",
            Model = "Camry",
            Auction = new AuctionInfo { AuctionAt = DateTimeOffset.UtcNow.AddDays(2), LotStatus = "Open" },
            Condition = new VehicleCondition { PrimaryDamage = "Front End" },
            SaleDocument = new SaleDocument { Name = "CERTIFICATE OF TITLE", IsPending = false },
            Media = new MediaInfo { Photos = ["https://vis.iaai.com/resizer?imageKeys=test&width=640"] }
        }, DateTimeOffset.UtcNow, CancellationToken.None);

        var auctions = new FakeAuctionsApiClient();
        var apibara = new FakeApibaraClient();
        var processor = new AuctionsApiIaaIConditionBackfillProcessor(
            auctions,
            apibara,
            store,
            new CanonicalInventoryIngestionPipeline(store),
            Microsoft.Extensions.Options.Options.Create(new AuctionsApiOptions { Enabled = true, AllowWrites = false, ApiKey = "test", PageSize = 1000 }),
            NullLogger<AuctionsApiIaaIConditionBackfillProcessor>.Instance);

        var result = await processor.RunAsync(10, DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None, dryRun: true);

        Assert.True(result.DryRun);
        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.AuctionsApiMatched);
        Assert.Equal(0, result.Updated);
        Assert.Equal(0, result.ApibaraFallbacks);
        Assert.Equal(0, apibara.DetailRequests);
        Assert.Empty((await store.GetExecutionHistoryAsync(new InventoryExecutionHistoryRequest(1, 10, "iaai"), CancellationToken.None)).Items);
    }

    [Fact]
    public async Task Uses_AuctionsApi_first_and_does_not_consume_Apibara_when_primary_matches()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistAsync(new AuctionVehicle
        {
            Platform = "iaai",
            LotNumber = "12345678",
            Vin = "1HGCM82633A004352",
            Year = 2020,
            Make = "Toyota",
            Model = "Camry",
            Auction = new AuctionInfo { AuctionAt = DateTimeOffset.UtcNow.AddDays(2), LotStatus = "Open" },
            Pricing = new PricingInfo { BuyNowUsd = 18_000 },
            Condition = new VehicleCondition { PrimaryDamage = "Front End" },
            SaleDocument = new SaleDocument { Name = "CERTIFICATE OF TITLE", IsPending = false },
            Media = new MediaInfo { Photos = ["https://vis.iaai.com/resizer?imageKeys=test&width=640"] }
        }, DateTimeOffset.UtcNow, CancellationToken.None);

        var auctions = new FakeAuctionsApiClient();
        var apibara = new FakeApibaraClient();
        var processor = new AuctionsApiIaaIConditionBackfillProcessor(
            auctions,
            apibara,
            store,
            new CanonicalInventoryIngestionPipeline(store),
            Microsoft.Extensions.Options.Options.Create(new AuctionsApiOptions { Enabled = true, AllowWrites = true, ApiKey = "test", PageSize = 1000 }),
            NullLogger<AuctionsApiIaaIConditionBackfillProcessor>.Instance);

        var result = await processor.RunAsync(10, DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);

        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.AuctionsApiMatched);
        Assert.Equal(0, result.ApibaraFallbacks);
        Assert.Equal(1, result.Updated);
        Assert.Equal(1, auctions.ChangedRequests);
        Assert.Equal(0, apibara.DetailRequests);

        var updated = await store.GetByPlatformAndLotAsync("iaai", "12345678", CancellationToken.None);
        Assert.Equal("RUNS AND DRIVES", updated?.Vehicle.Condition?.RunCondition?.Value);
        Assert.Equal("Intact", updated?.Vehicle.VehicleSpecs?.Airbags);
        Assert.True(updated?.Vehicle.Condition?.HasKey);
    }

    private sealed class FakeAuctionsApiClient : IAuctionsApiClient
    {
        public int ChangedRequests { get; private set; }

        public Task<AuctionsApiPage> GetChangedLotsAsync(AuctionsApiWindowRequest request, CancellationToken cancellationToken)
        {
            ChangedRequests++;
            using var document = JsonDocument.Parse("""
                [{
                  "vin":"1HGCM82633A004352",
                  "year":2020,
                  "manufacturer":{"name":"Toyota"},
                  "model":{"name":"Camry"},
                  "lots":[{
                    "lot":"12345678",
                    "domain":{"id":1},
                    "keys_available":true,
                    "run_condition":{"value":"RUNS AND DRIVES","label":"Run and Drive"},
                    "airbags":"Intact",
                    "restraint_system":"Dual Air Bag",
                    "damage":{"primary":"Front End"},
                    "seller":{"name":"State Farm","type":"Insurance"},
                    "sale_date":"2030-01-01T12:00:00Z",
                    "title":"CERTIFICATE OF TITLE"
                  }]
                }]
                """);
            return Task.FromResult(new AuctionsApiPage(document.RootElement.Clone(), JsonDocument.Parse("{}").RootElement.Clone()));
        }

        public Task<AuctionsApiPage> GetArchivedLotsAsync(AuctionsApiWindowRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new AuctionsApiPage(JsonDocument.Parse("[]").RootElement.Clone(), JsonDocument.Parse("{}").RootElement.Clone()));
    }

    private sealed class FakeApibaraClient : IApibaraClient
    {
        public int DetailRequests { get; private set; }
        public Task<VehicleListResponse> SearchVehiclesAsync(VehicleSearchRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LocationsResponse> GetLocationsAsync(string platform, string state, int perPage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VehicleDetailsResponse> GetVehicleAsync(string vinOrLot, CancellationToken cancellationToken)
        {
            DetailRequests++;
            throw new InvalidOperationException("Apibara fallback should not be used when AuctionsAPI matches.");
        }
        public Task<UsageResponse> GetUsageAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
