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
            foreach (var vehicle in MapRows(activeWindow.Rows, normalizedPlatform))
            {
                if (string.IsNullOrWhiteSpace(vehicle.LotNumber))
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
            foreach (var vehicle in MapRows(archivedWindow.Rows, normalizedPlatform))
            {
                if (string.IsNullOrWhiteSpace(vehicle.LotNumber)) continue;
                archived++;
                archivedKeys.Add($"{normalizedPlatform}:{vehicle.LotNumber.Trim()}");
            }
            if (persist && archivedKeys.Count > 0)
                deactivated = await snapshotStore.DeactivateArchivedLotsAsync(normalizedPlatform, archivedKeys, DateTimeOffset.UtcNow, cancellationToken, runId);

            var finishedAt = DateTimeOffset.UtcNow;
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(finishedAt, changed + archived, requests, failures, loaded, marked, discarded, quarantined, failures.Count, pages, false), CancellationToken.None);
            return new(runId, normalizedPlatform, persist, changed, archived, loaded, marked, discarded, quarantined, deactivated, pages, requests, failures);
        }
        catch (OperationCanceledException exception)
        {
            failures.Add("cancelled");
            logger.LogWarning("AuctionsAPI incremental sync {RunId} cancelled for {Platform} after {Changed} changed lots.", runId, normalizedPlatform, changed + archived);
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(DateTimeOffset.UtcNow, changed + archived, requests, failures, loaded, marked, discarded, quarantined, failures.Count, pages, false, null, true), CancellationToken.None);
            throw new OperationCanceledException($"AuctionsAPI incremental sync {runId} cancelled for {normalizedPlatform}.", exception, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add(exception.Message);
            logger.LogError(exception, "AuctionsAPI incremental sync {RunId} failed for {Platform}.", runId, normalizedPlatform);
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(DateTimeOffset.UtcNow, changed + archived, requests, failures, loaded, marked, discarded, quarantined, failures.Count, pages, false), CancellationToken.None);
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
            rows.AddRange(ExtractRows(response.Data));
            if (response.NextPage is not null && response.NextPage <= page) break;
            if (response.NextPage is null && !HasNextPage(response.Meta, page)) break;
            page = response.NextPage ?? page + 1;
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

    internal static IEnumerable<JsonElement> ExtractRows(JsonElement data)
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

    internal static IEnumerable<AuctionVehicle> MapRows(IEnumerable<JsonElement> rows, string platform, bool trustRequestedDomain = false)
    {
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (row.TryGetProperty("lots", out var lots) && lots.ValueKind == JsonValueKind.Array && lots.GetArrayLength() > 0)
            {
                foreach (var lot in lots.EnumerateArray())
                {
                    var vehicle = MapVehicle(row, lot, platform, trustRequestedDomain);
                    if (vehicle is not null) yield return vehicle;
                }
            }
            else
            {
                var vehicle = MapVehicle(row, row, platform, trustRequestedDomain);
                if (vehicle is not null) yield return vehicle;
            }
        }
    }

    private static AuctionVehicle? MapVehicle(JsonElement vehicleRow, JsonElement lotRow, string platform, bool trustRequestedDomain)
    {
        var domain = Scalar(lotRow, "domain.id", "domain_id") ?? Scalar(vehicleRow, "domain.id", "domain_id");
        if (domain is null && trustRequestedDomain) domain = DomainId(platform).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!string.Equals(domain, DomainId(platform).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)) return null;
        var raw = JsonSerializer.SerializeToElement(new { vehicle = vehicleRow, lot = lotRow });
        var payload = new Dictionary<string, object?>
        {
            ["platform"] = platform,
            ["source_provider"] = "auctionsapi",
            ["lot_number"] = Scalar(lotRow, "lot", "lot_number", "lot.number", "external_id", "id"),
            ["vin"] = Scalar(lotRow, "vin") ?? Scalar(vehicleRow, "vin"),
            ["year"] = Number(vehicleRow, "year"),
            ["make"] = Scalar(vehicleRow, "manufacturer.name", "make"),
            ["model"] = Scalar(vehicleRow, "model.name", "model"),
            ["vehicle_type"] = Scalar(vehicleRow, "vehicle_type.name", "vehicle_type"),
            ["color"] = Scalar(vehicleRow, "color.name", "color"),
            ["fuel_type"] = Scalar(vehicleRow, "fuel.name", "fuel"),
            ["transmission"] = Scalar(vehicleRow, "transmission.name", "transmission"),
            ["drive_type"] = Scalar(vehicleRow, "drive_wheel.name", "drive_wheel"),
            ["title"] = Scalar(lotRow, "title", "detailed_title") ?? Scalar(vehicleRow, "title"),
            ["vehicle_specs"] = new Dictionary<string, object?>
            {
                ["body_style"] = Scalar(vehicleRow, "body_style", "vehicle_type.name", "vehicle_type"),
                ["airbags"] = Scalar(lotRow, "vehicle_specs.airbags", "airbags", "airbag"),
                ["restraint_system"] = Scalar(lotRow, "vehicle_specs.restraint_system", "restraint_system", "restraint"),
            },
            ["condition"] = new Dictionary<string, object?>
            {
                ["primary_damage"] = Scalar(lotRow, "damage.primary", "damage.primary_damage", "damage"),
                ["secondary_damage"] = Scalar(lotRow, "damage.secondary", "damage.secondary_damage"),
                ["has_key"] = Bool(lotRow, "keys_available", "key_available", "keys"),
                ["run_condition"] = new Dictionary<string, object?>
                {
                    ["value"] = Scalar(lotRow, "condition.run_condition.value", "run_condition.value", "run_and_drive", "run_drive", "start_code"),
                    ["label"] = Scalar(lotRow, "condition.run_condition.label", "run_condition.label", "run_and_drive_label"),
                    ["class_hint"] = Scalar(lotRow, "condition.run_condition.class_hint", "run_condition.class_hint"),
                },
            },
            ["seller"] = new Dictionary<string, object?>
            {
                ["name"] = Scalar(lotRow, "seller.name", "seller"),
                ["raw_type"] = Scalar(lotRow, "seller.raw_type", "seller_type"),
                ["type"] = Scalar(lotRow, "seller.type", "seller_type"),
                ["class"] = Scalar(lotRow, "seller.class", "seller_class"),
                ["text_class"] = Scalar(lotRow, "seller.text_class", "seller_text_class"),
            },
            ["odometer"] = new Dictionary<string, object?>
            {
                ["mi"] = Number(lotRow, "odometer.miles", "odometer.mi", "odometer"),
                ["status"] = Scalar(lotRow, "odometer.status"),
            },
            ["sale_document"] = new Dictionary<string, object?>
            {
                ["name"] = Scalar(lotRow, "title", "detailed_title"),
                ["is_pending"] = false,
            },
            ["auction"] = new Dictionary<string, object?>
            {
                ["auction_at"] = Scalar(lotRow, "sale_date"),
                ["lot_status"] = Scalar(lotRow, "status"),
                ["is_timed"] = Bool(lotRow, "is_timed_auction"),
            },
            ["pricing"] = new Dictionary<string, object?>
            {
                ["current_bid_usd"] = Number(lotRow, "bid", "final_bid"),
                ["buy_now_usd"] = Number(lotRow, "buy_now"),
            },
            ["location"] = new Dictionary<string, object?>
            {
                ["display"] = Scalar(lotRow, "location.name", "selling_branch"),
                ["state"] = Scalar(lotRow, "location.state"),
                ["facility_id"] = Scalar(lotRow, "location.id", "location.facility_id"),
            },
            ["media"] = new Dictionary<string, object?> { ["thumbs"] = ImageUrls(lotRow, vehicleRow) },
        };
        try
        {
            var mapped = JsonSerializer.SerializeToElement(payload).Deserialize<AuctionVehicle>(JsonOptions);
            return mapped is null ? null : mapped with { RawSource = raw };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? At(JsonElement value, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(part, out value)) return null;
        }
        return value;
    }

    private static string? Scalar(JsonElement value, params string[] paths)
    {
        foreach (var path in paths)
        {
            var found = At(value, path);
            if (found is null) continue;
            var item = found.Value;
            if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString())) return item.GetString();
            if (item.ValueKind is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False) return item.ToString();
            if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String) return name.GetString();
        }
        return null;
    }

    private static int? Number(JsonElement value, params string[] paths) => int.TryParse(Scalar(value, paths), out var number) ? number : null;
    private static bool? Bool(JsonElement value, params string[] paths) => bool.TryParse(Scalar(value, paths), out var result) ? result : null;

    private static string[] ImageUrls(params JsonElement[] rows)
    {
        var urls = new List<string>();
        foreach (var row in rows)
        {
            var images = At(row, "images");
            if (images is null) continue;
            CollectImageUrls(images.Value, urls);
        }
        return urls
            .Where(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static void CollectImageUrls(JsonElement value, ICollection<string> urls)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var candidate = value.GetString();
                if (!string.IsNullOrWhiteSpace(candidate)) urls.Add(candidate);
                return;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) CollectImageUrls(item, urls);
                return;
            case JsonValueKind.Object:
                foreach (var property in new[] { "big", "normal", "small", "exterior", "interior", "url", "src", "large" })
                {
                    if (value.TryGetProperty(property, out var nested)) CollectImageUrls(nested, urls);
                }
                return;
        }
    }

    private static string? MaskVin(string? vin) => string.IsNullOrWhiteSpace(vin) || vin.Length < 6 ? null : $"{vin[..3]}…{vin[^3..]}";

    private sealed record WindowReadResult(IReadOnlyList<JsonElement> Rows, int Pages, int Requests);
}
