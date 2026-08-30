using System.Reflection;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class StoredSnapshotFallbackTests
{
    [Fact]
    public void Preserves_the_vehicle_when_one_historical_field_has_an_incompatible_type()
    {
        var store = new PostgresSnapshotStore(
            Microsoft.Extensions.Options.Options.Create(new PersistenceOptions()),
            Microsoft.Extensions.Options.Options.Create(new BlobAuditOptions()),
            NullLogger<PostgresSnapshotStore>.Instance);
        var method = typeof(PostgresSnapshotStore).GetMethod(
            "DeserializeStoredVehicle",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        const string payload = """
            {"platform":"copart","lot_number":"48826366","year":{"unexpected":2024},"auction":{"is_buy_now":true}}
            """;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true
        };

        var vehicle = method!.Invoke(store, [payload, "copart:48826366", options]) as AuctionVehicle;

        Assert.NotNull(vehicle);
        Assert.Equal("copart", vehicle.Platform);
        Assert.Equal("48826366", vehicle.LotNumber);
        Assert.Null(vehicle.Year);
        Assert.True(vehicle.Auction?.IsBuyNow);
    }
}
