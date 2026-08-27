using System.Text.Json;
using Lsc.Inventory.Api.Contracts;

namespace Lsc.Inventory.Api.Sources;

public sealed record CopartMediaProbeResult(
    bool SnapshotValid,
    int SnapshotRows,
    bool SampleMediaUrlFound,
    int? HttpStatus,
    string? ContentType,
    long? ContentLength,
    string? JsonShape,
    string? Failure);

/// <summary>
/// Performs one read-only request against an Image URL already supplied by an approved Copart snapshot.
/// It never logs lots, VINs, URLs, query values, response values, or credentials.
/// </summary>
public static class CopartMediaSnapshotProbe
{
    public static async Task<CopartMediaProbeResult> ProbeAsync(
        ICopartExcelSnapshotSource snapshotSource,
        ICopartExcelSnapshotAdapter adapter,
        CancellationToken cancellationToken)
    {
        await using var lease = await snapshotSource.OpenLatestAsync(cancellationToken);
        var validation = await adapter.ValidateAsync(lease.Snapshot, cancellationToken);
        if (!validation.IsComplete)
        {
            return new CopartMediaProbeResult(false, validation.RowCount, false, null, null, null, null,
                string.Join(" | ", validation.Failures));
        }

        string? mediaUrl = null;
        await foreach (var vehicle in adapter.ReadAcceptedSnapshotAsync(lease.Snapshot, cancellationToken))
        {
            mediaUrl = ReadRawMediaUrl(vehicle);
            if (mediaUrl is not null)
                break;
        }

        if (mediaUrl is null)
            return new CopartMediaProbeResult(true, validation.RowCount, false, null, null, null, null, "No valid Copart Image URL was present in the snapshot.");

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
            request.Headers.Accept.ParseAdd("application/json, image/*;q=0.9");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var contentType = response.Content.Headers.ContentType?.MediaType;
            var contentLength = response.Content.Headers.ContentLength;
            string? jsonShape = null;

            if (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && response.IsSuccessStatusCode)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                using var document = JsonDocument.Parse(bytes);
                jsonShape = Describe(document.RootElement, 0);
            }

            return new CopartMediaProbeResult(true, validation.RowCount, true, (int)response.StatusCode, contentType, contentLength, jsonShape, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CopartMediaProbeResult(true, validation.RowCount, true, null, null, null, null, $"Media request failed: {exception.GetType().Name}");
        }
    }

    private static string? ReadRawMediaUrl(AuctionVehicle vehicle)
    {
        if (vehicle.RawSource is not { } rawSource ||
            rawSource.ValueKind != JsonValueKind.Object ||
            !rawSource.TryGetProperty("Image URL", out var candidate) ||
            candidate.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var value = candidate.GetString()?.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               uri.Host.EndsWith("copart.io", StringComparison.OrdinalIgnoreCase)
            ? uri.ToString()
            : null;
    }

    private static string Describe(JsonElement value, int depth)
    {
        if (depth > 4) return value.ValueKind.ToString();
        return value.ValueKind switch
        {
            JsonValueKind.Object => "object(" + string.Join(",", value.EnumerateObject().Take(16).Select(property => property.Name + ":" + Describe(property.Value, depth + 1))) + ")",
            JsonValueKind.Array => "array[" + (value.GetArrayLength() == 0 ? 0 : Describe(value[0], depth + 1)) + "]",
            JsonValueKind.String => "string",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => value.ValueKind.ToString()
        };
    }
}
