using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record AuctionsApiIncrementalSyncResult(
    Guid RunId,
    string Platform,
    bool Persisted,
    int ChangedObserved,
    int ArchivedObserved,
    int Loaded,
    int Marked,
    int Discarded,
    int Quarantined,
    int Deactivated,
    int PagesProcessed,
    int RequestsIssued,
    IReadOnlyList<string> Failures);

public interface IAuctionsApiIncrementalSyncProcessor
{
    Task<AuctionsApiIncrementalSyncResult> RunAsync(string platform, bool persist, CancellationToken cancellationToken);
}

/// <summary>
/// Provider adapter only: it reads the incremental AuctionsAPI windows and
/// maps rows to AuctionVehicle. Business rules remain in the canonical pipeline.
/// </summary>
public sealed class AuctionsApiIncrementalSyncProcessor(
    IAuctionsApiClient client,
    IInventorySnapshotStore snapshotStore,
    ICanonicalInventoryIngestionPipeline canonicalPipeline,
    IOptions<AuctionsApiOptions> options,
    ILogger<AuctionsApiIncrementalSyncProcessor> logger) : IAuctionsApiIncrementalSyncProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AuctionsApiOptions _options = options.Value;

    public async Task<AuctionsApiIncrementalSyncResult> RunAsync(string platform, bool persist, CancellationToken cancellationToken)
    {
        var normalizedPlatform = platform.Trim().ToLowerInvariant();
        if (normalizedPlatform is not ("copart" or "iaai")) throw new ArgumentOutOfRangeException(nameof(platform));
        if (!_options.IsConfigured) throw new InvalidOperationException("AuctionsAPI incremental sync is disabled until the production configuration is explicitly enabled.");
        if (persist && !_options.AllowWrites) throw new InvalidOperationException("AuctionsAPI canonical writes are disabled until the Owner explicitly approves activation.");
        if (!persist) logger.LogInformation("AuctionsAPI incremental run for {Platform} is shadow-only; no inventory writes will occur.", normalizedPlatform);

        var startedAt = DateTimeOffset.UtcNow;
        var runId = await snapshotStore.StartSyncRunAsync(new InventorySyncRunStart("auctions_api", normalizedPlatform, persist ? "incremental-active" : "incremental-shadow", 2, _options.PageSize, startedAt), cancellationToken);
        var leaseName = $"auctions-api-{normalizedPlatform}-incremental";
        var lease = await snapshotStore.TryAcquireLeaseAsync(leaseName, runId, startedAt, TimeSpan.FromMinutes(10), cancellationToken);
        if (!lease.Acquired)
        {
            var skipped = new[] { lease.SkipReason ?? "lease-active" };
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(DateTimeOffset.UtcNow, 0, 0, skipped), cancellationToken);
            return new(runId, normalizedPlatform, persist, 0, 0, 0, 0, 0, 0, 0, 0, 0, skipped);
        }

        var failures = new List<string>();
        var changed = 0;
        var archived = 0;
        var loaded = 0;
        var marked = 0;
        var discarded = 0;
        var quarantined = 0;
        var deactivated = 0;
        var pages = 0;
        var requests = 0;

        try
        {
            var minutes = Math.Clamp(_options.DefaultOverlapMinutes, 1, 4320);
            var activeWindow = await ReadWindowAsync(normalizedPlatform, minutes, archived: false, cancellationToken);
            pages += activeWindow.Pages;
            requests += activeWindow.Requests;
            foreach (var element in activeWindow.Rows)
            {
                var vehicle = DeserializeVehicle(element, normalizedPlatform);
                if (vehicle is null || string.IsNullOrWhiteSpace(vehicle.LotNumber))
                {
                    failures.Add("changed:missing-lot");
                    continue;
                }
                changed++;
                var lotKey = $"{normalizedPlatform}:{vehicle.LotNumber.Trim()}";
                var observedAt = DateTimeOffset.UtcNow;
                var ingested = await canonicalPipeline.ProcessAsync(vehicle, observedAt, cancellationToken, runId, persist: persist);
                if (!ingested.Loaded)
                {
                    if (ingested.Quarantined) quarantined++;
                    else if (ingested.Discarded) discarded++;
                    continue;
                }
                loaded++;
                if (ingested.Marked) marked++;
                if (persist)
                {
                    var saved = ingested.Persistence!;
                    await snapshotStore.RecordSyncRunEventAsync(new InventorySyncRunEvent(runId, normalizedPlatform, saved.LotKey, ingested.Vehicle.LotNumber, MaskVin(ingested.Vehicle.Vin), saved.Action, saved.ChangedFields, [], observedAt), cancellationToken);
                }
                else
                {
                    await snapshotStore.RecordSyncRunEventAsync(new InventorySyncRunEvent(runId, normalizedPlatform, lotKey, vehicle.LotNumber, MaskVin(vehicle.Vin), "shadow-evaluated", [], [], observedAt), cancellationToken);
                }
            }

            var archivedWindow = await ReadWindowAsync(normalizedPlatform, minutes, archived: true, cancellationToken);
            pages += archivedWindow.Pages;
            requests += archivedWindow.Requests;
            var archivedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var element in archivedWindow.Rows)
            {
                var vehicle = DeserializeVehicle(element, normalizedPlatform);
                if (vehicle is null || string.IsNullOrWhiteSpace(vehicle.LotNumber)) continue;
                archived++;
                archivedKeys.Add($"{normalizedPlatform}:{vehicle.LotNumber.Trim()}");
            }
            if (persist && archivedKeys.Count > 0)
                deactivated = await snapshotStore.DeactivateArchivedLotsAsync(normalizedPlatform, archivedKeys, DateTimeOffset.UtcNow, cancellationToken, runId);

            var finishedAt = DateTimeOffset.UtcNow;
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(finishedAt, changed + archived, requests, failures, loaded, marked, discarded, quarantined, failures.Count, pages, false), cancellationToken);
            return new(runId, normalizedPlatform, persist, changed, archived, loaded, marked, discarded, quarantined, deactivated, pages, requests, failures);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add(exception.Message);
            logger.LogError(exception, "AuctionsAPI incremental sync {RunId} failed for {Platform}.", runId, normalizedPlatform);
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(DateTimeOffset.UtcNow, changed + archived, requests, failures, loaded, marked, discarded, quarantined, failures.Count, pages, false), cancellationToken);
            throw;
        }
        finally
        {
            await snapshotStore.ReleaseLeaseAsync(leaseName, runId, DateTimeOffset.UtcNow, CancellationToken.None);
        }
    }

    private async Task<WindowReadResult> ReadWindowAsync(string platform, int minutes, bool archived, CancellationToken cancellationToken)
    {
        var rows = new List<JsonElement>();
        var page = 1;
        var pages = 0;
        var requests = 0;
        while (page <= 1000)
        {
            var response = archived
                ? await client.GetArchivedLotsAsync(new AuctionsApiWindowRequest(DomainId(platform), minutes, page, _options.PageSize), cancellationToken)
                : await client.GetChangedLotsAsync(new AuctionsApiWindowRequest(DomainId(platform), minutes, page, _options.PageSize), cancellationToken);
            requests++;
            pages++;
            rows.AddRange(Rows(response.Data));
            if (!HasNextPage(response.Meta, page)) break;
            page++;
        }
        return new(rows, pages, requests);
    }

    private static bool HasNextPage(JsonElement meta, int currentPage)
    {
        if (meta.ValueKind != JsonValueKind.Object) return false;
        if (TryBool(meta, "has_more", out var more) || TryBool(meta, "has_next", out more)) return more;
        if (TryInt(meta, "next_page", out var next)) return next > currentPage;
        if (TryInt(meta, "total_pages", out var total)) return total > currentPage;
        if (TryInt(meta, "last_page", out var last)) return last > currentPage;
        foreach (var key in new[] { "pagination", "paging" })
            if (meta.TryGetProperty(key, out var nested) && HasNextPage(nested, currentPage)) return true;
        return false;
    }

    private static bool TryBool(JsonElement value, string name, out bool result)
    {
        if (value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            result = property.GetBoolean();
            return true;
        }
        result = false;
        return false;
    }

    private static bool TryInt(JsonElement value, string name, out int result)
    {
        if (value.TryGetProperty(name, out var property) && property.TryGetInt32(out var parsed))
        {
            result = parsed;
            return true;
        }
        result = 0;
        return false;
    }

    private static int DomainId(string platform) => platform == "iaai" ? 1 : 3;

    private static IEnumerable<JsonElement> Rows(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Array)
        {
            foreach (var row in data.EnumerateArray()) yield return row;
            yield break;
        }
        if (data.ValueKind != JsonValueKind.Object) yield break;
        foreach (var key in new[] { "lots", "cars", "data", "results" })
        {
            if (!data.TryGetProperty(key, out var nested) || nested.ValueKind != JsonValueKind.Array) continue;
            foreach (var row in nested.EnumerateArray()) yield return row;
            yield break;
        }
    }

    private static AuctionVehicle? DeserializeVehicle(JsonElement element, string platform)
    {
        try
        {
            var vehicle = element.Deserialize<AuctionVehicle>(JsonOptions);
            return vehicle is null ? null : vehicle with { Platform = platform, RawSource = element.Clone() };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? MaskVin(string? vin) => string.IsNullOrWhiteSpace(vin) || vin.Length < 6 ? null : $"{vin[..3]}…{vin[^3..]}";

    private sealed record WindowReadResult(IReadOnlyList<JsonElement> Rows, int Pages, int Requests);
}
