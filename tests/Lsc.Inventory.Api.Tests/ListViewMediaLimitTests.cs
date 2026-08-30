using System.Reflection;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class ListViewMediaLimitTests
{
    [Fact]
    public void Limits_browse_vehicle_media_to_one_item_without_changing_vehicle_identity()
    {
        var snapshot = new StoredVehicleSnapshot(
            "iaai:101",
            DateTimeOffset.UtcNow,
            new AuctionVehicle
            {
                Platform = "iaai",
                LotNumber = "101",
                Media = new MediaInfo
                {
                    Items =
                    [
                        new AuctionMediaItem { Large = "https://vis.iaai.com/resizer?imageKeys=one&width=640", Type = "image" },
                        new AuctionMediaItem { Large = "https://vis.iaai.com/resizer?imageKeys=two&width=640", Type = "image" }
                    ]
                }
            },
            "{}");
        var entryPoint = typeof(PostgresSnapshotStore).Assembly.GetType("Program");
        var method = entryPoint?.GetMethod("ToPublicVehicle", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var vehicle = method!.Invoke(null, [snapshot, null, null, null, 1]) as PublicInventoryVehicle;

        Assert.NotNull(vehicle);
        Assert.Equal("101", vehicle.Lot);
        Assert.Single(vehicle.Photos);
        Assert.Single(vehicle.Media);
    }
}
