using System.Globalization;
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
        if (!TryGetProperty(root, "lotImages", out var lotImages) || lotImages.ValueKind != JsonValueKind.Array)
            return (Array.Empty<string>(), 0);

        var candidates = new List<ImageCandidate>();
        var fallbackSequence = 0;
        foreach (var image in lotImages.EnumerateArray())
        {
            var sequence = GetInt(image, "sequence") ?? fallbackSequence;
            fallbackSequence++;
            if (!TryGetProperty(image, "link", out var links) || links.ValueKind != JsonValueKind.Array) continue;

            foreach (var link in links.EnumerateArray())
            {
                if (!TryReadImageLink(link, out var candidate))
                {
                    invalidLinks++;
                    continue;
                }
                candidates.Add(candidate with { Sequence = sequence });
            }
        }

        var photos = new List<string>();
        var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hdImages = 0;
        foreach (var group in candidates
                     .GroupBy(item => item.Sequence)
                     .OrderBy(item => item.Key))
        {
            var selected = group
                .OrderByDescending(item => item.IsHd)
                .ThenByDescending(item => item.Width ?? 0)
                .ThenBy(item => item.IsThumbnail)
                .ThenBy(item => item.Url, StringComparer.Ordinal)
                .FirstOrDefault();
            if (selected is null || !unique.Add(selected.Url)) continue;
            photos.Add(selected.Url);
            if (selected.IsHd) hdImages++;
        }
        return (photos, hdImages);
    }

    private static bool TryReadImageLink(JsonElement link, out ImageCandidate candidate)
    {
        candidate = default!;
        if (link.ValueKind != JsonValueKind.Object) return false;

        var value = GetString(link, "url", "imageUrl", "imageURL", "href");
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            (uri.Host is not "copart.com" && !uri.Host.EndsWith(".copart.com", StringComparison.OrdinalIgnoreCase)))
            return false;

        var isThumbnail = GetBool(link, "isThumbNail", "isThumbnail", "thumbnail", "isThumb") ?? false;
        var explicitHd = GetBool(link, "isHdImage", "isHDImage", "isHd", "hd", "highDefinition");
        var width = GetInt(link, "width", "imageWidth");
        var isHd = explicitHd == true || IsHighDefinitionUri(uri, width);
        candidate = new ImageCandidate(0, uri.ToString(), isHd, isThumbnail, width);
        return true;
    }

    private static bool IsHighDefinitionUri(Uri uri, int? width) =>
        width >= 1600 ||
        uri.AbsolutePath.Contains("/hd/", StringComparison.OrdinalIgnoreCase) ||
        uri.AbsolutePath.Contains("highres", StringComparison.OrdinalIgnoreCase) ||
        uri.Query.Contains("hd", StringComparison.OrdinalIgnoreCase) ||
        uri.Query.Contains("width=2048", StringComparison.OrdinalIgnoreCase) ||
        uri.Query.Contains("width=1920", StringComparison.OrdinalIgnoreCase);

    private static string? ReadCatalogUrl(AuctionVehicle vehicle, out bool invalid)
    {
        invalid = false;
        if (vehicle.RawSource is not { } rawSource || rawSource.ValueKind != JsonValueKind.Object)
            return null;
        var value = GetString(rawSource, "Image URL", "ImageURL", "image_url");
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            (uri.Host is not ("inventoryv2.copart.io" or "inventoryv2.copart.com") &&
             !uri.Host.EndsWith(".copart.com", StringComparison.OrdinalIgnoreCase)))
        {
            invalid = true;
            return null;
        }
        return uri.ToString();
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out value)) return true;
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }

    private static string? GetString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        return null;
    }

    private static int? GetInt(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
            if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        return null;
    }

    private static bool? GetBool(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(element, name, out var value)) continue;
            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) return value.GetBoolean();
            if (value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var parsed)) return parsed;
        }
        return null;
    }

    private sealed record ImageCandidate(int Sequence, string Url, bool IsHd, bool IsThumbnail, int? Width);
}
