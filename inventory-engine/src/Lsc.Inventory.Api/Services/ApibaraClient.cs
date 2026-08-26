using System.Net.Http.Headers;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Services;

public interface IApibaraClient
{
    Task<VehicleListResponse> SearchVehiclesAsync(VehicleSearchRequest request, CancellationToken cancellationToken);
    Task<LocationsResponse> GetLocationsAsync(string platform, string state, int perPage, CancellationToken cancellationToken);
    Task<VehicleDetailsResponse> GetVehicleAsync(string vinOrLot, CancellationToken cancellationToken);
    Task<UsageResponse> GetUsageAsync(CancellationToken cancellationToken);
}

public sealed class ApibaraClient(
    HttpClient httpClient,
    IOptions<ApibaraOptions> options,
    ILogger<ApibaraClient> logger) : IApibaraClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly ApibaraOptions _options = options.Value;

    public Task<VehicleListResponse> SearchVehiclesAsync(VehicleSearchRequest request, CancellationToken cancellationToken)
    {
        var platform = InventorySourcePolicy.RequireApibaraPlatform(request.Platform);
        var query = new Dictionary<string, string?>
        {
            ["platform"] = platform,
            ["lot_sub_status"] = request.LotSubStatus,
            ["per_page"] = request.PerPage.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["cursor"] = request.Cursor,
            ["facility_id"] = request.FacilityId,
            ["year_from"] = request.YearFrom?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["year_to"] = request.YearTo?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["price_min"] = request.PriceMin?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["price_max"] = request.PriceMax?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["make"] = request.Make,
            ["model"] = request.Model
        };

        return GetAsync<VehicleListResponse>("vehicles", query, cancellationToken);
    }

    public Task<LocationsResponse> GetLocationsAsync(string platform, string state, int perPage, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(perPage, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(perPage, 20);
        var query = new Dictionary<string, string?>
        {
            ["platform"] = InventorySourcePolicy.RequireApibaraPlatform(platform),
            ["state"] = state.Trim().ToUpperInvariant(),
            ["per_page"] = perPage.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        return GetAsync<LocationsResponse>("locations", query, cancellationToken);
    }

    public Task<VehicleDetailsResponse> GetVehicleAsync(string vinOrLot, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vinOrLot);
        return GetAsync<VehicleDetailsResponse>($"vehicles/{Uri.EscapeDataString(vinOrLot)}", null, cancellationToken);
    }

    public Task<UsageResponse> GetUsageAsync(CancellationToken cancellationToken) =>
        GetAsync<UsageResponse>("usage", null, cancellationToken);

    private async Task<T> GetAsync<T>(string relativePath, Dictionary<string, string?>? query, CancellationToken cancellationToken)
        where T : class
    {
        var path = query is null
            ? relativePath
            : QueryHelpers.AddQueryString(relativePath, query.Where(x => !string.IsNullOrWhiteSpace(x.Value)).ToDictionary());

        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-API-Key", _options.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseSummary = string.IsNullOrWhiteSpace(body)
                ? "empty response body"
                : body.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (responseSummary.Length > 500)
            {
                responseSummary = responseSummary[..500];
            }

            logger.LogWarning("Apibara returned status {StatusCode} for {Path}: {ResponseSummary}", (int)response.StatusCode, relativePath, responseSummary);
            throw new HttpRequestException($"Apibara returned {(int)response.StatusCode} for {relativePath}: {responseSummary}", null, response.StatusCode);
        }

        var payload = JsonSerializer.Deserialize<T>(body, JsonOptions);
        return payload ?? throw new InvalidOperationException($"Apibara returned an empty payload for {relativePath}.");
    }
}
