using System.Net;
using System.Text;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionsApiClientTests
{
    [Fact]
    public async Task Refuses_to_issue_a_request_when_the_evaluation_is_disabled()
    {
        var handler = new CapturingHandler();
        var client = CreateClient(handler, enabled: false);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.GetChangedLotsAsync(new AuctionsApiWindowRequest(3, 120), CancellationToken.None));

        Assert.Contains("disabled", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Builds_a_bounded_incremental_copart_request_with_server_side_key()
    {
        var handler = new CapturingHandler("{\"data\":[],\"meta\":{\"current_page\":1}}");
        var client = CreateClient(handler, enabled: true);

        var page = await client.GetChangedLotsAsync(new AuctionsApiWindowRequest(3, 120, 2, 1000), CancellationToken.None);

        Assert.Equal(1, handler.Requests);
        Assert.Equal("/api/cars?domain_id=3&minutes=120&page=2&per_page=1000", handler.LastRequest!.RequestUri!.PathAndQuery);
        Assert.Equal("test-key", handler.LastRequest.Headers.GetValues("x-api-key").Single());
        Assert.Equal(0, page.Data.GetArrayLength());
    }

    [Fact]
    public async Task Restricts_domains_and_windows_before_network_io()
    {
        var handler = new CapturingHandler();
        var client = CreateClient(handler, enabled: true);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetArchivedLotsAsync(new AuctionsApiWindowRequest(12, 120), CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => client.GetArchivedLotsAsync(new AuctionsApiWindowRequest(3, 4321), CancellationToken.None));
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Retries_rate_limit_and_returns_the_next_successful_page()
    {
        var handler = new RateLimitThenSuccessHandler();
        var client = CreateClient(handler, enabled: true);

        var page = await client.GetChangedLotsAsync(new AuctionsApiWindowRequest(1, null, 1, 1000), CancellationToken.None);

        Assert.Equal(2, handler.Requests);
        Assert.Equal(0, page.Data.GetArrayLength());
    }

    [Fact]
    public async Task Retries_a_transient_transport_failure_before_returning_the_page()
    {
        var handler = new TransportFailureThenSuccessHandler();
        var client = CreateClient(handler, enabled: true);

        var page = await client.GetChangedLotsAsync(new AuctionsApiWindowRequest(1, null, 1, 1000), CancellationToken.None);

        Assert.Equal(2, handler.Requests);
        Assert.Equal(0, page.Data.GetArrayLength());
    }

    [Fact]
    public async Task Keeps_retrying_past_the_previous_short_rate_limit_window()
    {
        var handler = new RateLimitThenSuccessHandler(rateLimitResponses: 6);
        var client = CreateClient(handler, enabled: true);

        var page = await client.GetChangedLotsAsync(new AuctionsApiWindowRequest(1, null, 1, 1000), CancellationToken.None);

        Assert.Equal(7, handler.Requests);
        Assert.Equal(0, page.Data.GetArrayLength());
    }

    private static AuctionsApiClient CreateClient(HttpMessageHandler handler, bool enabled) => new(
        new HttpClient(handler) { BaseAddress = new Uri("https://auctions.test/api/") },
        Microsoft.Extensions.Options.Options.Create(new AuctionsApiOptions { Enabled = enabled, ApiKey = "test-key", BaseUrl = "https://auctions.test/api/" }),
        new ProviderRequestLimiter(),
        NullLogger<AuctionsApiClient>.Instance);

    private sealed class CapturingHandler(string json = "{\"data\":[],\"meta\":{}}") : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class TransportFailureThenSuccessHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Requests == 1) throw new HttpRequestException("transient connection reset");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[],\"meta\":{}}", Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class RateLimitThenSuccessHandler(int rateLimitResponses = 1) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            if (Requests <= rateLimitResponses)
            {
                var throttled = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                throttled.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
                return Task.FromResult(throttled);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"data\":[],\"meta\":{}}", Encoding.UTF8, "application/json")
            });
        }
    }
}
