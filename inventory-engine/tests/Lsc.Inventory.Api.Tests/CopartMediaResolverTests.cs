using System.Net;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Sources;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartMediaResolverTests
{
    [Fact]
    public async Task Resolver_canonicalizes_copart_hd_urls_with_query_parameters()
    {
        const string catalog = """
            {
              "lotImages": [
                {"sequence": 1, "link": [{"url": "https://cs.copart.com/v1/A/hd-1.jpg?width=2048&token=opaque", "isThumbNail": false, "isHdImage": true}]},
                {"sequence": 2, "link": [{"url": "https://cs.copart.com/v1/A/hd-2.jpg?width=2048", "isThumbNail": false, "isHdImage": true}]}
              ]
            }
            """;
        var handler = new StaticJsonHandler(catalog);
        var resolver = new CopartMediaResolver(new HttpClient(handler));
        var vehicle = new AuctionVehicle
        {
            Platform = "copart",
            LotNumber = "48826366",
            Media = new MediaInfo { Photos = ["https://cs.copart.com/v1/A/thumb.jpg"], ThumbnailsCount = 1 },
            RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string>
            {
                ["Image URL"] = "https://inventoryv2.copart.io/v1/lotImages/48826366?brand=XYZ"
            })
        };

        var result = await resolver.ResolveAsync(vehicle, CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal(2, result.GalleryImages);
        Assert.Equal(2, result.HdImages);
        Assert.All(result.Vehicle.Media!.Photos!, image => Assert.DoesNotContain("?", image));
        Assert.Equal("https://cs.copart.com/v1/A/hd-1.jpg", result.Vehicle.Media!.Photos![0]);
    }

    private sealed class StaticJsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
