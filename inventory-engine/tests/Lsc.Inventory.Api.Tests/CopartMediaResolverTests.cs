using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Sources;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartMediaResolverTests
{
    [Fact]
    public async Task Resolver_preserves_approved_copart_hd_urls_for_private_proxy_delivery()
    {
        const string catalog = """
            {
              "lotImages": [
                {"sequence": 1, "link": [{"url": "https://cs.copart.com/v1/A/hd-1.jpg?width=2048&token=opaque", "isThumbNail": false, "isHdImage": true}]},
                {"sequence": 2, "link": [{"url": "https://cs.copart.com/v1/A/hd-2.jpg?width=2048", "isThumbNail": false, "isHdImage": true}]}
              ]
            }
            """;
        var resolver = new CopartMediaResolver(new HttpClient(new StubHandler(_ => JsonResponse(catalog))));

        var result = await resolver.ResolveAsync(Source("https://inventoryv2.copart.io/v1/lotImages/48826366?brand=REDACTED"), CancellationToken.None);

        Assert.True(result.Resolved);
        Assert.Equal(2, result.GalleryImages);
        Assert.Equal(2, result.HdImages);
        Assert.Equal("https://cs.copart.com/v1/A/hd-1.jpg?width=2048&token=opaque", result.Vehicle.Media!.Photos![0]);
        Assert.Equal("https://cs.copart.com/v1/A/hd-2.jpg?width=2048", result.Vehicle.Media!.Photos![1]);
    }

    [Fact]
    public async Task Resolver_prefers_hd_then_standard_then_thumbnail_and_preserves_sequence_order()
    {
        const string catalog = """
        {
          "lotImages": [
            { "sequence": 20, "link": [
              { "url": "https://cs.copart.com/v1/twenty-thumb.jpg", "isThumbNail": true },
              { "url": "https://cs.copart.com/v1/twenty-hd.jpg", "isHdImage": true }
            ] },
            { "sequence": 10, "link": [
              { "url": "https://cs.copart.com/v1/ten-standard.jpg", "isThumbNail": false }
            ] }
          ]
        }
        """;
        var resolver = new CopartMediaResolver(new HttpClient(new StubHandler(_ => JsonResponse(catalog))));

        var resolution = await resolver.ResolveAsync(Source("https://inventoryv2.copart.io/v1/lotImages/1001"), CancellationToken.None);

        Assert.True(resolution.Resolved);
        Assert.Equal(2, resolution.GalleryImages);
        Assert.Equal(1, resolution.HdImages);
        Assert.Equal(new[] { "https://cs.copart.com/v1/ten-standard.jpg", "https://cs.copart.com/v1/twenty-hd.jpg" }, resolution.Vehicle.Media!.Photos);
        Assert.Null(resolution.FailureCode);
    }

    [Fact]
    public async Task Resolver_returns_controlled_not_found_without_changing_media()
    {
        var resolver = new CopartMediaResolver(new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        var source = Source("https://inventoryv2.copart.io/v1/lotImages/1002");

        var resolution = await resolver.ResolveAsync(source, CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Equal("NOT_FOUND_404", resolution.FailureCode);
        Assert.Same(source.Media, resolution.Vehicle.Media);
    }

    [Fact]
    public async Task Resolver_rejects_invalid_catalog_url_without_an_http_call()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("Should not be invoked"));
        var resolver = new CopartMediaResolver(new HttpClient(handler));

        var resolution = await resolver.ResolveAsync(Source("https://invalid.example/lot/1003"), CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Equal("INVALID_URL", resolution.FailureCode);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Resolver_returns_controlled_transient_failure()
    {
        var resolver = new CopartMediaResolver(new HttpClient(new StubHandler(_ => throw new HttpRequestException("temporary"))));

        var resolution = await resolver.ResolveAsync(Source("https://inventoryv2.copart.io/v1/lotImages/1004"), CancellationToken.None);

        Assert.False(resolution.Resolved);
        Assert.Equal("REQUEST_HTTPREQUESTEXCEPTION", resolution.FailureCode);
    }

    private static AuctionVehicle Source(string catalogUrl) => new()
    {
        Platform = "copart",
        LotNumber = "1001",
        Media = new MediaInfo { Photos = ["https://cs.copart.com/v1/thumb.jpg"], ThumbnailsCount = 1 },
        RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["Image URL"] = catalogUrl })
    };

    private static HttpResponseMessage JsonResponse(string content)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return response;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }
}
