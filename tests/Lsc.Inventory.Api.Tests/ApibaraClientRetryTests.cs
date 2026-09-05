using System.Net;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class ApibaraClientRetryTests
{
    [Fact]
    public async Task Sends_updated_within_minutes_in_vehicle_search_query()
    {
        var handler = new SequenceHandler(
            Response(HttpStatusCode.OK, "{\"data\":[],\"meta\":{\"per_page\":20,\"next_cursor\":null,\"prev_cursor\":null}}"));
        var client = CreateClient(handler, attempts: 1);

        await client.SearchVehiclesAsync(new VehicleSearchRequest("iaai", "Open", UpdatedWithinMinutes: 200), CancellationToken.None);

        Assert.NotNull(handler.LastRequest);
        Assert.Contains("updated_within_minutes=200", handler.LastRequest!.RequestUri!.Query, StringComparison.Ordinal);
        Assert.Contains("platform=iaai", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
        Assert.Contains("lot_sub_status=Open", handler.LastRequest.RequestUri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Missing_api_key_fails_before_sending_a_request()
    {
        var handler = new SequenceHandler(Response(HttpStatusCode.OK, "{}"));
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://apibara.test/") };
        var options = Microsoft.Extensions.Options.Options.Create(new ApibaraOptions
        {
            ApiKey = string.Empty,
            BaseUrl = "https://apibara.test/",
            RetryMaxAttempts = 1
        });
        var client = new ApibaraClient(httpClient, options, new ProviderRequestLimiter(), NullLogger<ApibaraClient>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.SearchVehiclesAsync(new VehicleSearchRequest("iaai", "Open"), CancellationToken.None));

        Assert.Contains("API key is required", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task Retries_transient_502_and_returns_the_successful_payload()
    {
        var handler = new SequenceHandler(
            Response(HttpStatusCode.BadGateway, "error code: 502"),
            Response(HttpStatusCode.BadGateway, "error code: 502"),
            Response(HttpStatusCode.OK, "{\"data\":[],\"meta\":{\"per_page\":20,\"next_cursor\":null,\"prev_cursor\":null}}"));
        var client = CreateClient(handler, attempts: 3);

        var result = await client.SearchVehiclesAsync(new VehicleSearchRequest("iaai", "Open"), CancellationToken.None);

        Assert.Empty(result.Data);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task Throws_after_transient_retry_budget_is_exhausted()
    {
        var handler = new SequenceHandler(
            Response(HttpStatusCode.BadGateway, "error code: 502"),
            Response(HttpStatusCode.BadGateway, "error code: 502"),
            Response(HttpStatusCode.BadGateway, "error code: 502"));
        var client = CreateClient(handler, attempts: 3);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SearchVehiclesAsync(new VehicleSearchRequest("iaai", "Open"), CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal(3, handler.Requests);
    }

    [Fact]
    public async Task Does_not_retry_non_transient_authorization_errors()
    {
        var handler = new SequenceHandler(Response(HttpStatusCode.Unauthorized, "unauthorized"));
        var client = CreateClient(handler, attempts: 3);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            client.SearchVehiclesAsync(new VehicleSearchRequest("iaai", "Open"), CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Does_not_retry_a_deterministically_invalid_cursor()
    {
        var handler = new SequenceHandler(Response(
            HttpStatusCode.InternalServerError,
            "{\"message\":\"Unable to find parameter [vehicles.id] in pagination item.\"}"));
        var client = CreateClient(handler, attempts: 3);

        var exception = await Assert.ThrowsAsync<ApibaraInvalidCursorException>(() =>
            client.SearchVehiclesAsync(new VehicleSearchRequest("iaai", "Open", 20, "expired-cursor"), CancellationToken.None));

        Assert.Contains("opaque cursor", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, handler.Requests);
    }

    private static ApibaraClient CreateClient(HttpMessageHandler handler, int attempts)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://apibara.test/") };
        var options = Microsoft.Extensions.Options.Options.Create(new ApibaraOptions
        {
            ApiKey = "test-key",
            BaseUrl = "https://apibara.test/",
            RetryMaxAttempts = attempts,
            RetryBaseDelayMilliseconds = 50,
            RetryMaxDelayMilliseconds = 100
        });
        return new ApibaraClient(httpClient, options, new ProviderRequestLimiter(), NullLogger<ApibaraClient>.Instance);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body)
    };

    private sealed class SequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);
        public int Requests { get; private set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastRequest = request;
            return Task.FromResult(_responses.Dequeue());
        }
    }
}
