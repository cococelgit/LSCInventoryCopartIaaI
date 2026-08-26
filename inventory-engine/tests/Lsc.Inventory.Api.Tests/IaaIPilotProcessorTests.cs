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

public sealed class IaaIPilotProcessorTests
{
    [Fact]
    public async Task Loads_exactly_one_thousand_iaai_list_vehicles_in_fifty_requests()
    {
        var client = new FakeApibaraClient();
        var store = new InMemorySnapshotStore();
        var processor = new IaaIPilotProcessor(
            client,
            store,
            Microsoft.Extensions.Options.Options.Create(new ApibaraOptions { ApiKey = "test", PageSize = 20 }),
            Microsoft.Extensions.Options.Options.Create(new IaaIPilotOptions { Enabled = true, MaxVehicles = 1000, MaxListRequests = 50 }),
            NullLogger<IaaIPilotProcessor>.Instance);

        var result = await processor.RunAsync(CancellationToken.None);

        Assert.Equal(1000, result.Observed);
        Assert.Equal(1000, result.Loaded);
        Assert.Equal(0, result.Discarded);
        Assert.Equal(0, result.Quarantined);
        Assert.Equal(50, result.RequestsIssued);
        Assert.Equal(50, client.ListRequests);
        Assert.All(client.Requests, request => Assert.Equal("iaai", request.Platform));
        Assert.Equal(1000, (await store.GetRecentAsync(5000, CancellationToken.None)).Count);
    }

    private sealed class FakeApibaraClient : IApibaraClient
    {
        public int ListRequests { get; private set; }
        public List<VehicleSearchRequest> Requests { get; } = [];

        public Task<VehicleListResponse> SearchVehiclesAsync(VehicleSearchRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var page = ListRequests++;
            var vehicles = Enumerable.Range(1, 20).Select(index => new AuctionVehicle
            {
                Platform = "iaai",
                LotNumber = $"{page + 1:D3}{index:D3}",
                Vin = "1HGCM82633A004352",
                Year = 2012,
                Make = "Honda",
                Model = "Accord",
                Auction = new AuctionInfo { AuctionAt = DateTimeOffset.UtcNow.AddDays(10), LotSubStatus = "Open" },
                Location = new VehicleLocation { Display = "Orlando-North (FL)" },
                Seller = new AuctionSeller { Name = "Insurance Company" },
                Condition = new VehicleCondition { PrimaryDamage = "Front End", HasKey = true, RunCondition = new RunConditionInfo { Value = "RUNS AND DRIVES" } },
                OdometerInfo = new OdometerInfo { Miles = 40_000, Status = "ACTUAL" },
                SaleDocument = new SaleDocument { Name = "CERTIFICATE OF TITLE", IsPending = false },
                Media = new MediaInfo { Photos = ["https://vis.iaai.com/resizer?imageKeys=test&width=640"] }
            }).ToArray();
            return Task.FromResult(new VehicleListResponse(vehicles, new CursorMeta(20, page < 49 ? $"cursor-{page + 1}" : null, null)));
        }

        public Task<LocationsResponse> GetLocationsAsync(string platform, string state, int perPage, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<VehicleDetailsResponse> GetVehicleAsync(string vinOrLot, CancellationToken cancellationToken) => throw new NotSupportedException("Pilot must not call per-vehicle details.");
        public Task<UsageResponse> GetUsageAsync(CancellationToken cancellationToken) => Task.FromResult(new UsageResponse(JsonSerializer.SerializeToElement(new { })));
    }
}
