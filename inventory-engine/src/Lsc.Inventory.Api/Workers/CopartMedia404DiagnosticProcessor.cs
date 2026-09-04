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
    string? FailureCode);

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
    ICopartMediaResolver resolver) : ICopartMedia404DiagnosticProcessor
{
    public async Task<CopartMedia404DiagnosticResult> RunAsync(int maximum, CancellationToken cancellationToken)
    {
        var candidates = await snapshotStore.GetCopartMediaCandidatesAsync(Math.Clamp(maximum, 1, 500), cancellationToken);
        var rows = new List<CopartMedia404DiagnosticRow>(candidates.Count);
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resolution = await resolver.ResolveAsync(candidate.Vehicle, cancellationToken);
            rows.Add(new CopartMedia404DiagnosticRow(
                candidate.Identity,
                candidate.Vehicle.LotNumber,
                candidate.Vehicle.Vin,
                ReadCatalogUrl(candidate.Vehicle.RawSource),
                resolution.Resolved,
                resolution.GalleryImages,
                resolution.HdImages,
                resolution.FailureCode));
        }

        return new CopartMedia404DiagnosticResult(
            rows.Count,
            rows.Count(row => row.Resolved),
            rows.Count(row => !row.Resolved),
            rows);
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
