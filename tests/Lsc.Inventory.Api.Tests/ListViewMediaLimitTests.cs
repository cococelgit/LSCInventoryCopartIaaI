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
        var method = entryPoint?
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name.Contains("ToPublicVehicle", StringComparison.Ordinal));
        Assert.NotNull(method);

        var vehicle = method!.Invoke(null, [snapshot, null, null, null, 1]) as PublicInventoryVehicle;

        Assert.NotNull(vehicle);
        Assert.Equal("101", vehicle.Lot);
        Assert.Single(vehicle.Photos);
        Assert.Single(vehicle.Media);
    }

    [Fact]
    public void Keeps_every_available_photo_when_browse_does_not_request_list_view()
    {
        var items = Enumerable.Range(1, 35)
            .Select(index => new AuctionMediaItem
            {
                Large = $"https://vis.iaai.com/resizer?imageKeys={index}&width=640",
                Type = "image"
            })
            .ToArray();
        var snapshot = new StoredVehicleSnapshot(
            "iaai:202",
            DateTimeOffset.UtcNow,
            new AuctionVehicle
            {
                Platform = "iaai",
                LotNumber = "202",
                Media = new MediaInfo { Items = items }
            },
            "{}");
        var entryPoint = typeof(PostgresSnapshotStore).Assembly.GetType("Program");
        var method = entryPoint?
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name.Contains("ToPublicVehicle", StringComparison.Ordinal));
        Assert.NotNull(method);

        var vehicle = method!.Invoke(null, [snapshot, null, null, null, null]) as PublicInventoryVehicle;

        Assert.NotNull(vehicle);
        Assert.Equal(35, vehicle.Photos.Count);
        Assert.Equal(35, vehicle.Media.Count);
    }

    [Fact]
    public void Does_not_expose_a_zero_price_Copart_listing_as_Buy_Now()
    {
        var snapshot = new StoredVehicleSnapshot(
            "copart:zero",
            DateTimeOffset.UtcNow,
            new AuctionVehicle
            {
                Platform = "copart",
                LotNumber = "zero",
                Auction = new AuctionInfo { IsBuyNow = true },
                Pricing = new PricingInfo { BuyNowUsd = 0m }
            },
            "{}");
        var entryPoint = typeof(PostgresSnapshotStore).Assembly.GetType("Program");
        var method = entryPoint?
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .SingleOrDefault(candidate => candidate.Name.Contains("ToPublicVehicle", StringComparison.Ordinal));
        Assert.NotNull(method);

        var vehicle = method!.Invoke(null, [snapshot, null, null, null, null]) as PublicInventoryVehicle;

        Assert.NotNull(vehicle);
        Assert.Equal(false, vehicle.IsBuyNow);
        Assert.Null(vehicle.BuyNowUsd);
    }
}
