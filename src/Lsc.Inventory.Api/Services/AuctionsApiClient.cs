using System.Net.Http.Headers;
using System.Text.Json;
using Lsc.Inventory.Api.Options;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Services;

/// <summary>
/// Read-only vendor client for a future shadow evaluation. It cannot select a source,
/// persist a vehicle, execute a Job or invoke reconciliation.
/// </summary>
public interface IAuctionsApiClient
{
    Task<AuctionsApiPage> GetChangedLotsAsync(AuctionsApiWindowRequest request, CancellationToken cancellationToken);
    Task<AuctionsApiPage> GetArchivedLotsAsync(AuctionsApiWindowRequest request, CancellationToken cancellationToken);
}

public sealed record AuctionsApiWindowRequest(
    int DomainId,
    int? Minutes,
    int Page = 1,
    int? PerPage = null);

public sealed record AuctionsApiPage(JsonElement Data, JsonElement Meta, int? NextPage = null);

public sealed class AuctionsApiClient(
    HttpClient httpClient,
    IOptions<AuctionsApiOptions> options,
    ILogger<AuctionsApiClient> logger) : IAuctionsApiClient
{
    private const int MaxRateLimitAttempts = 5;
    private readonly AuctionsApiOptions _options = options.Value;

    public Task<AuctionsApiPage> GetChangedLotsAsync(AuctionsApiWindowRequest request, CancellationToken cancellationToken) =>
        GetPageAsync("cars", request, cancellationToken);

    public Task<AuctionsApiPage> GetArchivedLotsAsync(AuctionsApiWindowRequest request, CancellationToken cancellationToken) =>
        GetPageAsync("archived-lots", request, cancellationToken);

    private async Task<AuctionsApiPage> GetPageAsync(string path, AuctionsApiWindowRequest request, CancellationToken cancellationToken)
    {
        if (!_options.IsConfigured)
            throw new InvalidOperationException("AuctionsAPI is disabled. Enable it only for an approved shadow evaluation.");
        if (request.DomainId is not (1 or 3))
            throw new ArgumentOutOfRangeException(nameof(request.DomainId), "Only IAAI (1) and Copart (3) are allowed in this adapter.");
        if (request.Minutes is not null and (< 1 or > 4320))
            throw new ArgumentOutOfRangeException(nameof(request.Minutes), "The overlap window must be between 1 and 4320 minutes.");
        if (request.Page < 1)
            throw new ArgumentOutOfRangeException(nameof(request.Page));

        var perPage = Math.Clamp(request.PerPage ?? _options.PageSize, 1, 1000);
        var query = new Dictionary<string, string?>
        {
            ["domain_id"] = request.DomainId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["minutes"] = request.Minutes?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["page"] = request.Page.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["per_page"] = perPage.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        var uri = QueryHelpers.AddQueryString(path, query);

        for (var attempt = 1; attempt <= MaxRateLimitAttempts; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.Add("x-api-key", _options.ApiKey);
            message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if ((int)response.StatusCode == 429 && attempt < MaxRateLimitAttempts)
            {
                var retryAfter = response.Headers.RetryAfter?.Delta
                    ?? TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt) + Random.Shared.Next(0, 2)));
                logger.LogWarning("AuctionsAPI rate-limited {Path}; retrying attempt {Attempt}/{MaxAttempts} after {RetryAfter}.", path, attempt, MaxRateLimitAttempts, retryAfter);
                await Task.Delay(retryAfter, cancellationToken);
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var summary = string.IsNullOrWhiteSpace(body) ? "empty response body" : body.Replace('\r', ' ').Replace('\n', ' ').Trim();
                logger.LogWarning("AuctionsAPI returned status {StatusCode} for {Path}: {ResponseSummary}", (int)response.StatusCode, path, summary[..Math.Min(summary.Length, 400)]);
                throw new HttpRequestException($"AuctionsAPI returned {(int)response.StatusCode} for {path}.", null, response.StatusCode);
            }

            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data))
                throw new InvalidOperationException($"AuctionsAPI returned no data envelope for {path}.");
            var meta = document.RootElement.TryGetProperty("meta", out var metaValue)
                ? metaValue.Clone()
                : JsonDocument.Parse("{}").RootElement.Clone();
            int? nextPage = null;
            if (document.RootElement.TryGetProperty("links", out var links) && links.ValueKind == JsonValueKind.Object && links.TryGetProperty("next", out var next) && next.ValueKind == JsonValueKind.String)
            {
                var nextQuery = QueryHelpers.ParseQuery(next.GetString()!);
                if (nextQuery.TryGetValue("page", out var pageValue) && int.TryParse(pageValue.FirstOrDefault(), out var parsedPage)) nextPage = parsedPage;
            }
            return new AuctionsApiPage(data.Clone(), meta, nextPage);
        }

        throw new InvalidOperationException("AuctionsAPI retry loop exited unexpectedly.");
    }
}
