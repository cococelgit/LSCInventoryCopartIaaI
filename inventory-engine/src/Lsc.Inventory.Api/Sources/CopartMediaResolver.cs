using System.Text.Json;
using Lsc.Inventory.Api.Contracts;

namespace Lsc.Inventory.Api.Sources;

public sealed record CopartMediaResolution(
    AuctionVehicle Vehicle,
    bool Resolved,
    int GalleryImages,
    int HdImages,
    string? FailureCode);

public interface ICopartMediaResolver
{
    Task<CopartMediaResolution> ResolveAsync(AuctionVehicle vehicle, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the image catalog exposed by the approved Copart snapshot's Image URL. It is only used
/// by the separately invoked enrichment worker; it is never called by the critical snapshot load.
/// </summary>
public sealed class CopartMediaResolver(HttpClient client) : ICopartMediaResolver
{
    public async Task<CopartMediaResolution> ResolveAsync(AuctionVehicle vehicle, CancellationToken cancellationToken)
    {
        var catalogUrl = ReadCatalogUrl(vehicle, out var catalogUrlInvalid);
        if (catalogUrl is null)
            return new CopartMediaResolution(vehicle, false, 0, 0, catalogUrlInvalid ? "INVALID_URL" : "MISSING_CATALOG_URL");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, catalogUrl);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return new CopartMediaResolution(vehicle, false, 0, 0,
                    response.StatusCode == System.Net.HttpStatusCode.NotFound ? "NOT_FOUND_404" : $"HTTP_{(int)response.StatusCode}");
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) != true)
                return new CopartMediaResolution(vehicle, false, 0, 0, "INVALID_CATALOG_RESPONSE");

            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            var resolved = ResolveGallery(document.RootElement, out var invalidLinks);
            if (resolved.Photos.Count == 0)
                return new CopartMediaResolution(vehicle, false, 0, 0, invalidLinks > 0 ? "INVALID_URL" : "INCOMPLETE_GALLERY");

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
            return new CopartMediaResolution(vehicle, false, 0, 0, $"REQUEST_{exception.GetType().Name.ToUpperInvariant()}");
        }
    }

    private static (IReadOnlyList<string> Photos, int HdImages) ResolveGallery(JsonElement root, out int invalidLinks)
    {
        invalidLinks = 0;
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
                if (!TryReadImageLink(link, out var url, out var isThumbnail, out var isHd))
                {
                    invalidLinks++;
                    continue;
                }
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
        return (photos, hdImages);
    }

    private static bool TryReadImageLink(JsonElement link, out string url, out bool isThumbnail, out bool isHd)
    {
        url = string.Empty;
        isThumbnail = link.TryGetProperty("isThumbNail", out var thumbnail) && thumbnail.ValueKind == JsonValueKind.True;
        isHd = link.TryGetProperty("isHdImage", out var hd) && hd.ValueKind == JsonValueKind.True;
        if (!link.TryGetProperty("url", out var candidate) || candidate.ValueKind != JsonValueKind.String) return false;
        var value = candidate.GetString()?.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return false;
        if (uri.Host is not "copart.com" && !uri.Host.EndsWith(".copart.com", StringComparison.OrdinalIgnoreCase)) return false;

        // The original approved URL remains only in the private payload. Public API media remains proxy-backed.
        url = uri.ToString();
        return true;
    }

    private static string? ReadCatalogUrl(AuctionVehicle vehicle, out bool invalid)
    {
        invalid = false;
        if (vehicle.RawSource is not { } rawSource || rawSource.ValueKind != JsonValueKind.Object ||
            !rawSource.TryGetProperty("Image URL", out var candidate) || candidate.ValueKind != JsonValueKind.String) return null;
        var value = candidate.GetString()?.Trim();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.Host is not ("inventoryv2.copart.io" or "inventoryv2.copart.com"))
        {
            invalid = true;
            return null;
        }
        return uri.ToString();
    }
}
