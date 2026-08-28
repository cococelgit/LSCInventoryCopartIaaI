using System.Text.Json;
using Lsc.Inventory.Api.Contracts;

namespace Lsc.Inventory.Api.Sources;

public sealed record CopartMediaResolution(
    AuctionVehicle Vehicle,
    bool Resolved,
    int GalleryImages,
    int HdImages,
    string? Failure);

public interface ICopartMediaResolver
{
    Task<CopartMediaResolution> ResolveAsync(AuctionVehicle vehicle, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the image catalog exposed by the approved Copart snapshot's Image URL.
/// Only direct HTTPS image links from Copart are persisted; endpoint URLs and query values are never published.
/// </summary>
public sealed class CopartMediaResolver(HttpClient client) : ICopartMediaResolver
{
    public async Task<CopartMediaResolution> ResolveAsync(AuctionVehicle vehicle, CancellationToken cancellationToken)
    {
        var catalogUrl = ReadCatalogUrl(vehicle);
        if (catalogUrl is null)
            return new CopartMediaResolution(vehicle, false, 0, 0, "No approved Copart image catalog URL was supplied by the snapshot.");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, catalogUrl);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new CopartMediaResolution(vehicle, false, 0, 0, $"Copart media catalog returned HTTP {(int)response.StatusCode}.");
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
                return new CopartMediaResolution(vehicle, false, 0, 0, "Copart media catalog did not return JSON.");

            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            var resolved = ResolveGallery(document.RootElement, out var rejectedHosts);
            if (resolved.Photos.Count == 0)
            {
                var hostSuffix = rejectedHosts.Count == 0
                    ? string.Empty
                    : $" Rejected HTTPS hosts: {string.Join(",", rejectedHosts)}.";
                return new CopartMediaResolution(vehicle, false, 0, 0, $"Copart media catalog contained no approved direct image links.{hostSuffix}");
            }

            var media = new MediaInfo
            {
                Photos = resolved.Photos,
                ThumbnailsCount = resolved.Photos.Count,
                Has360 = vehicle.Media?.Has360
            };
            return new CopartMediaResolution(vehicle with { Media = media }, true, resolved.Photos.Count, resolved.HdImages, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new CopartMediaResolution(vehicle, false, 0, 0, $"Copart media request failed: {exception.GetType().Name}.");
        }
    }

    private static (IReadOnlyList<string> Photos, int HdImages) ResolveGallery(JsonElement root, out IReadOnlyList<string> rejectedHosts)
    {
        var rejected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        rejectedHosts = Array.Empty<string>();
        if (!root.TryGetProperty("lotImages", out var lotImages) || lotImages.ValueKind != JsonValueKind.Array)
            return (Array.Empty<string>(), 0);

        var sequences = new List<(int Sequence, string? Hd, string? Standard, string? Thumbnail)>();
        var fallbackSequence = 0;
        foreach (var image in lotImages.EnumerateArray())
        {
            var sequence = image.TryGetProperty("sequence", out var sequenceValue) && sequenceValue.TryGetInt32(out var parsedSequence)
                ? parsedSequence
                : fallbackSequence;
            fallbackSequence++;
            if (!image.TryGetProperty("link", out var links) || links.ValueKind != JsonValueKind.Array) continue;

            string? hd = null;
            string? standard = null;
            string? thumbnail = null;
            foreach (var link in links.EnumerateArray())
            {
                if (!TryReadImageLink(link, rejected, out var url, out var isThumbnail, out var isHd)) continue;
                if (isHd && hd is null) hd = url;
                else if (!isThumbnail && standard is null) standard = url;
                else if (thumbnail is null) thumbnail = url;
            }
            sequences.Add((sequence, hd, standard, thumbnail));
        }

        var photos = new List<string>(sequences.Count);
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var hdImages = 0;
        foreach (var sequence in sequences.OrderBy(item => item.Sequence))
        {
            var selected = sequence.Hd ?? sequence.Standard ?? sequence.Thumbnail;
            if (selected is null || !unique.Add(selected)) continue;
            photos.Add(selected);
            if (sequence.Hd == selected) hdImages++;
        }
        rejectedHosts = rejected.OrderBy(host => host, StringComparer.OrdinalIgnoreCase).Take(10).ToArray();
        return (photos, hdImages);
    }

    private static bool TryReadImageLink(JsonElement link, ISet<string> rejectedHosts, out string url, out bool isThumbnail, out bool isHd)
    {
        url = string.Empty;
        isThumbnail = link.TryGetProperty("isThumbNail", out var thumbnail) && thumbnail.ValueKind == JsonValueKind.True;
        isHd = link.TryGetProperty("isHdImage", out var hd) && hd.ValueKind == JsonValueKind.True;
        if (!link.TryGetProperty("url", out var candidate) || candidate.ValueKind != JsonValueKind.String) return false;
        var value = candidate.GetString()?.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (uri.Host is not "copart.com" && !uri.Host.EndsWith(".copart.com", StringComparison.OrdinalIgnoreCase))
        {
            rejectedHosts.Add(uri.Host);
            return false;
        }

        // Keep the original approved HTTPS URL only inside the private vehicle payload. The public API replaces it
        // with a signed first-party proxy URL, so query values never reach the browser.
        url = uri.ToString();
        return true;
    }

    private static string? ReadCatalogUrl(AuctionVehicle vehicle)
    {
        if (vehicle.RawSource is not { } rawSource || rawSource.ValueKind != JsonValueKind.Object ||
            !rawSource.TryGetProperty("Image URL", out var candidate) || candidate.ValueKind != JsonValueKind.String) return null;
        var value = candidate.GetString()?.Trim();
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               (uri.Host is "inventoryv2.copart.io" or "inventoryv2.copart.com")
            ? uri.ToString()
            : null;
    }
}
