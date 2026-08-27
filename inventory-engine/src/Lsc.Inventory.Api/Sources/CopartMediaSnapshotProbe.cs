using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;

namespace Lsc.Inventory.Api.Sources;

public sealed record CopartMediaProbeResult(
    bool SnapshotValid,
    int SnapshotRows,
    int SampleMediaUrls,
    int SuccessfulResponses,
    IReadOnlyDictionary<int, int> StatusCounts,
    double? MedianRequestMilliseconds,
    double? P95RequestMilliseconds,
    double? AverageGalleryImages,
    double? AverageResolvedPhotos,
    double? AverageHdPhotos,
    int FailedResponses,
    string? Failure);

/// <summary>
/// Performs a small, read-only sample against Image URLs already supplied by an approved Copart snapshot.
/// It never logs lots, VINs, URLs, query values, response values, or credentials.
/// </summary>
public static class CopartMediaSnapshotProbe
{
    private const int SampleLimit = 12;
    private const int MaximumConcurrency = 4;

    public static async Task<CopartMediaProbeResult> ProbeAsync(
        ICopartExcelSnapshotSource snapshotSource,
        ICopartExcelSnapshotAdapter adapter,
        CancellationToken cancellationToken)
    {
        await using var lease = await snapshotSource.OpenLatestAsync(cancellationToken);
        var validation = await adapter.ValidateAsync(lease.Snapshot, cancellationToken);
        if (!validation.IsComplete)
        {
            return new CopartMediaProbeResult(false, validation.RowCount, 0, 0, new Dictionary<int, int>(), null, null, null, null, null, 0,
                string.Join(" | ", validation.Failures));
        }

        var mediaUrls = new List<string>(SampleLimit);
        await foreach (var vehicle in adapter.ReadAcceptedSnapshotAsync(lease.Snapshot, cancellationToken))
        {
            var mediaUrl = ReadRawMediaUrl(vehicle);
            if (mediaUrl is not null) mediaUrls.Add(mediaUrl);
            if (mediaUrls.Count == SampleLimit) break;
        }

        if (mediaUrls.Count == 0)
            return new CopartMediaProbeResult(true, validation.RowCount, 0, 0, new Dictionary<int, int>(), null, null, null, null, null, 0,
                "No valid Copart Image URL was present in the snapshot.");

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        var samples = new ConcurrentBag<MediaResponseSample>();
        await Parallel.ForEachAsync(mediaUrls, new ParallelOptions
        {
            MaxDegreeOfParallelism = MaximumConcurrency,
            CancellationToken = cancellationToken
        }, async (mediaUrl, token) => samples.Add(await FetchAsync(client, mediaUrl, token)));

        var completed = samples.Where(sample => sample.StatusCode is >= 200 and < 300).ToArray();
        var elapsed = completed.Select(sample => sample.ElapsedMilliseconds).OrderBy(value => value).ToArray();
        var galleries = completed.Where(sample => sample.DeclaredImageCount is not null).Select(sample => sample.DeclaredImageCount!.Value).ToArray();
        var photos = completed.Select(sample => sample.ResolvedPhotoCount).ToArray();
        var hdPhotos = completed.Select(sample => sample.HdPhotoCount).ToArray();

        return new CopartMediaProbeResult(
            true,
            validation.RowCount,
            mediaUrls.Count,
            completed.Length,
            samples.Where(sample => sample.StatusCode is not null).GroupBy(sample => sample.StatusCode!.Value).ToDictionary(group => group.Key, group => group.Count()),
            Percentile(elapsed, 0.50),
            Percentile(elapsed, 0.95),
            Average(galleries),
            Average(photos),
            Average(hdPhotos),
            samples.Count - completed.Length,
            null);
    }

    private static async Task<MediaResponseSample> FetchAsync(HttpClient client, string mediaUrl, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            timer.Stop();
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
                return new MediaResponseSample((int)response.StatusCode, timer.Elapsed.TotalMilliseconds, null, 0, 0);

            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            var root = document.RootElement;
            int? declaredImages = root.TryGetProperty("imgCount", out var imageCount) && imageCount.TryGetInt32(out var count) ? count : null;
            var directPhotos = new HashSet<string>(StringComparer.Ordinal);
            var hdPhotos = new HashSet<string>(StringComparer.Ordinal);

            if (root.TryGetProperty("lotImages", out var lotImages) && lotImages.ValueKind == JsonValueKind.Array)
            {
                foreach (var image in lotImages.EnumerateArray())
                {
                    if (!image.TryGetProperty("link", out var links) || links.ValueKind != JsonValueKind.Array) continue;
                    foreach (var link in links.EnumerateArray())
                    {
                        if (!TryReadDirectImage(link, out var url, out var isThumbnail, out var isHd)) continue;
                        if (isThumbnail) continue;
                        directPhotos.Add(url);
                        if (isHd) hdPhotos.Add(url);
                    }
                }
            }

            return new MediaResponseSample((int)response.StatusCode, timer.Elapsed.TotalMilliseconds, declaredImages, directPhotos.Count, hdPhotos.Count);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            timer.Stop();
            return new MediaResponseSample(null, timer.Elapsed.TotalMilliseconds, null, 0, 0);
        }
    }

    private static bool TryReadDirectImage(JsonElement link, out string url, out bool isThumbnail, out bool isHd)
    {
        url = string.Empty;
        isThumbnail = link.TryGetProperty("isThumbNail", out var thumbnail) && thumbnail.ValueKind == JsonValueKind.True;
        isHd = link.TryGetProperty("isHdImage", out var hd) && hd.ValueKind == JsonValueKind.True;
        if (!link.TryGetProperty("url", out var candidate) || candidate.ValueKind != JsonValueKind.String) return false;
        var value = candidate.GetString()?.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith("copart.com", StringComparison.OrdinalIgnoreCase)) return false;
        url = uri.ToString();
        return true;
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

    private static double? Average(IReadOnlyCollection<int> values) => values.Count == 0 ? null : values.Average();

    private static double? Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0) return null;
        var index = (int)Math.Ceiling(values.Count * percentile) - 1;
        return Math.Round(values[Math.Clamp(index, 0, values.Count - 1)], 2);
    }

    private sealed record MediaResponseSample(int? StatusCode, double ElapsedMilliseconds, int? DeclaredImageCount, int ResolvedPhotoCount, int HdPhotoCount);
}
