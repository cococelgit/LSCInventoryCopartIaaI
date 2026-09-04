using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;

namespace Lsc.Inventory.Api.Workers;

public sealed record CopartMedia404DiagnosticRow(
    string Identity,
    string? LotNumber,
    string? Vin,
    string? CatalogUrl,
    bool Resolved,
    int GalleryImages,
    int HdImages,
    string? FailureCode,
    string? VariantProbe);

public sealed record CopartMedia404DiagnosticResult(
    int Candidates,
    int Resolved,
    int Failed,
    IReadOnlyList<CopartMedia404DiagnosticRow> Rows);

public interface ICopartMedia404DiagnosticProcessor
{
    Task<CopartMedia404DiagnosticResult> RunAsync(int maximum, CancellationToken cancellationToken);
}

/// <summary>
/// Read-only Copart media diagnostic. It resolves candidate catalogs but never updates PostgreSQL.
/// </summary>
public sealed class CopartMedia404DiagnosticProcessor(
    IInventorySnapshotStore snapshotStore,
    ICopartMediaResolver resolver,
    IHttpClientFactory httpClientFactory) : ICopartMedia404DiagnosticProcessor
{
    public async Task<CopartMedia404DiagnosticResult> RunAsync(int maximum, CancellationToken cancellationToken)
    {
        var candidates = await snapshotStore.GetCopartMediaCandidatesAsync(Math.Clamp(maximum, 1, 500), cancellationToken);
        var rows = new List<CopartMedia404DiagnosticRow>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = await resolver.ResolveAsync(candidate.Vehicle, cancellationToken);
            var catalogUrl = ReadCatalogUrl(candidate.Vehicle.RawSource);
            var variantProbe = resolution.Resolved || catalogUrl is null
                ? null
                : await ProbeVariantsAsync(catalogUrl, candidate.Vehicle.LotNumber, cancellationToken);
            rows.Add(new CopartMedia404DiagnosticRow(
                candidate.Identity,
                candidate.Vehicle.LotNumber,
                candidate.Vehicle.Vin,
                catalogUrl,
                resolution.Resolved,
                resolution.GalleryImages,
                resolution.HdImages,
                resolution.FailureCode,
                variantProbe));
        }

        return new CopartMedia404DiagnosticResult(
            rows.Count,
            rows.Count(row => row.Resolved),
            rows.Count(row => !row.Resolved),
            rows);
    }

    private async Task<string> ProbeVariantsAsync(string catalogUrl, string? lotNumber, CancellationToken cancellationToken)
    {
        var variants = new List<string> { catalogUrl };
        if (Uri.TryCreate(catalogUrl, UriKind.Absolute, out var uri))
        {
            variants.Add(new UriBuilder(uri) { Scheme = Uri.UriSchemeHttps, Port = -1 }.Uri.ToString());
            variants.Add(new UriBuilder(uri) { Host = "inventoryv2.copart.com", Port = -1 }.Uri.ToString());
            variants.Add($"https://inventoryv2.copart.io/v1/lotImages/{Uri.EscapeDataString(lotNumber ?? string.Empty)}");
        }

        var client = httpClientFactory.CreateClient("copart-media-proxy");
        var observations = new List<string>();
        foreach (var variant in variants.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, variant);
                request.Headers.Accept.ParseAdd("application/json");
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                var shape = "";
                if (response.IsSuccessStatusCode && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
                    shape = DescribeJsonShape(document.RootElement);
                }
                observations.Add($"{response.StatusCode}:{response.Content.Headers.ContentType?.MediaType ?? ""}:{shape}:{variant}");
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                observations.Add($"{exception.GetType().Name}:{variant}");
            }
        }
        return string.Join(" || ", observations);
    }

    private static string DescribeJsonShape(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return root.ValueKind.ToString();
        var parts = new List<string>();
        foreach (var property in root.EnumerateObject())
        {
            var value = property.Value;
            parts.Add(value.ValueKind == JsonValueKind.Array
                ? $"{property.Name}[]:{value.GetArrayLength()}"
                : $"{property.Name}:{value.ValueKind}");
        }
        return string.Join(",", parts.OrderBy(item => item, StringComparer.Ordinal));
    }

    private static string? ReadCatalogUrl(JsonElement? rawSource)
    {
        if (rawSource is not { ValueKind: JsonValueKind.Object } raw) return null;
        foreach (var name in new[] { "Image URL", "ImageURL", "image_url" })
        {
            if (raw.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
                return value.GetString();
        }
        return null;
    }
}
