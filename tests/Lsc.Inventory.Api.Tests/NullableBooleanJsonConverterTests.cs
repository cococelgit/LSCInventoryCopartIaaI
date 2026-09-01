using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class NullableBooleanJsonConverterTests
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"Y\"", true)]
    [InlineData("\"N\"", false)]
    [InlineData("\"1\"", true)]
    [InlineData("\"0\"", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void Reads_common_auction_boolean_encodings(string json, bool expected)
    {
        var payload = $"{{\"auction\":{{\"is_buy_now\":{json}}}}}";
        var vehicle = JsonSerializer.Deserialize<AuctionVehicle>(payload, Options);

        Assert.Equal(expected, vehicle?.Auction?.IsBuyNow);
    }

    [Theory]
    [InlineData("\"NO INFORMATION\"")]
    [InlineData("\"unknown\"")]
    [InlineData("[]")]
    [InlineData("{}")]
    public void Keeps_unknown_boolean_encodings_null(string json)
    {
        var payload = $"{{\"auction\":{{\"is_buy_now\":{json}}}}}";
        var vehicle = JsonSerializer.Deserialize<AuctionVehicle>(payload, Options);

        Assert.Null(vehicle?.Auction?.IsBuyNow);
    }
}
