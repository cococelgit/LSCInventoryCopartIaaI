using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore(
    IOptions<PersistenceOptions> persistenceOptions,
    IOptions<BlobAuditOptions> blobOptions,
    ILogger<PostgresSnapshotStore> logger) : IInventorySnapshotStore
{
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static readonly SemaphoreSlim CopartSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim CopartAuctionHistorySchemaLock = new(1, 1);
    private static readonly SemaphoreSlim EligibilitySchemaLock = new(1, 1);
    private static readonly SemaphoreSlim LifecycleSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim ScoringSchemaLock = new(1, 1);
    private static bool _schemaInitialized;
    private static bool _copartSchemaInitialized;
    private static bool _copartAuctionHistorySchemaInitialized;
    private static bool _eligibilitySchemaInitialized;
    private static bool _lifecycleSchemaInitialized;
    private static bool _scoringSchemaInitialized;
    private readonly PersistenceOptions _persistence = persistenceOptions.Value;
    private readonly BlobAuditOptions _blob = blobOptions.Value;
    private readonly ConcurrentDictionary<string, StoredVehicleSnapshot> _recent = new(StringComparer.OrdinalIgnoreCase);
    private readonly TokenCredential _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = persistenceOptions.Value.ManagedIdentityClientId
    });

    public async Task BootstrapRuntimePrincipalAsync(CancellationToken cancellationToken)
    {
        await using (var administrativeConnection = await OpenConnectionAsync("postgres", cancellationToken))
        {
            await using var createDatabase = administrativeConnection.CreateCommand();
            createDatabase.CommandTimeout = _persistence.CommandTimeoutSeconds;
            createDatabase.CommandText = "select 1 from pg_database where datname = @database_name;";
            AddParameter(createDatabase, "database_name", _persistence.Database);
            var databaseExists = await createDatabase.ExecuteScalarAsync(cancellationToken) is not null;
            if (!databaseExists)
            {
                await using var create = administrativeConnection.CreateCommand();
                create.CommandTimeout = _persistence.CommandTimeoutSeconds;
                create.CommandText = $"create database {QuoteIdentifier(_persistence.Database)};";
                await create.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var checkRole = administrativeConnection.CreateCommand();
            checkRole.CommandTimeout = _persistence.CommandTimeoutSeconds;
            checkRole.CommandText = "select 1 from pg_roles where rolname = @role_name;";
            AddParameter(checkRole, "role_name", _persistence.RuntimePrincipalName);
            var roleExists = await checkRole.ExecuteScalarAsync(cancellationToken) is not null;
            if (!roleExists)
            {
                await using var createPrincipal = administrativeConnection.CreateCommand();
                createPrincipal.CommandTimeout = _persistence.CommandTimeoutSeconds;
                createPrincipal.CommandText = "select pg_catalog.pgaadauth_create_principal_with_oid(@role_name, @object_id, 'service', false, false);";
                AddParameter(createPrincipal, "role_name", _persistence.RuntimePrincipalName);
                AddParameter(createPrincipal, "object_id", _persistence.RuntimePrincipalObjectId);
                await createPrincipal.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using var databaseConnection = await OpenConnectionAsync(_persistence.Database, cancellationToken);
        var quotedRole = QuoteIdentifier(_persistence.RuntimePrincipalName);
        if (!string.IsNullOrWhiteSpace(_persistence.PreviousRuntimePrincipalName) &&
            !string.Equals(_persistence.PreviousRuntimePrincipalName, _persistence.RuntimePrincipalName, StringComparison.OrdinalIgnoreCase))
        {
            const string ownerRoleName = "lsc_inventory_owner";
            var quotedOwnerRole = QuoteIdentifier(ownerRoleName);
            var quotedPreviousRole = QuoteIdentifier(_persistence.PreviousRuntimePrincipalName);
            await using var handoff = databaseConnection.CreateCommand();
            handoff.CommandTimeout = _persistence.CommandTimeoutSeconds;
            handoff.CommandText = $"""
                do $$
                begin
                    if not exists (select 1 from pg_roles where rolname = '{ownerRoleName}') then
                        create role {quotedOwnerRole} nologin;
                    end if;
                end $$;
                alter database {QuoteIdentifier(_persistence.Database)} owner to {quotedOwnerRole};
                reassign owned by {quotedPreviousRole} to {quotedOwnerRole};
                drop owned by {quotedPreviousRole};
                """;
            await handoff.ExecuteNonQueryAsync(cancellationToken);
            logger.LogInformation("Transferred database ownership away from the temporary runtime principal.");
        }

        await using var grant = databaseConnection.CreateCommand();
        grant.CommandTimeout = _persistence.CommandTimeoutSeconds;
        grant.CommandText = $"""
            grant connect on database {QuoteIdentifier(_persistence.Database)} to {quotedRole};
            grant usage, create on schema public to {quotedRole};
            grant select, insert, update on all tables in schema public to {quotedRole};
            grant usage, select on all sequences in schema public to {quotedRole};
            """;
        await grant.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("Bootstrapped the database and least-privilege runtime principal.");
    }

    public async Task PersistAsync(AuctionVehicle vehicle, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        var observedAtUtc = observedAt.ToUniversalTime();
        var auctionAtUtc = vehicle.Auction?.AuctionAt?.ToUniversalTime();
        var identity = BuildIdentity(vehicle);
        vehicle = await ReuseResolvedCopartMediaAsync(identity, vehicle, cancellationToken);
        var rawJson = JsonSerializer.Serialize(vehicle);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawJson))).ToLowerInvariant();
        var blobName = BuildBlobName(identity, observedAtUtc, payloadHash);

        await UploadRawPayloadAsync(blobName, rawJson, cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            insert into auction_lots (
                lot_key, platform, lot_number, vin, title, year, make, model, vehicle_type,
                color, fuel_type, transmission, drive_type, odometer, damage, auction_state,
                auction_at, lot_status, lot_sub_status, location_display, location_state,
                facility_id, current_bid_usd, buy_now_usd, sale_price_usd, media_photos_count,
                media_has_360, observed_at, updated_at)
            values (
                @lot_key, @platform, @lot_number, @vin, @title, @year, @make, @model, @vehicle_type,
                @color, @fuel_type, @transmission, @drive_type, @odometer, @damage, @auction_state,
                @auction_at, @lot_status, @lot_sub_status, @location_display, @location_state,
                @facility_id, @current_bid_usd, @buy_now_usd, @sale_price_usd, @media_photos_count,
                @media_has_360, @observed_at, now())
            on conflict (lot_key) do update set
                title = excluded.title,
                year = excluded.year,
                make = excluded.make,
                model = excluded.model,
                vehicle_type = excluded.vehicle_type,
                color = excluded.color,
                fuel_type = excluded.fuel_type,
                transmission = excluded.transmission,
                drive_type = excluded.drive_type,
                odometer = excluded.odometer,
                damage = excluded.damage,
                auction_state = excluded.auction_state,
                auction_at = excluded.auction_at,
                lot_status = excluded.lot_status,
                lot_sub_status = excluded.lot_sub_status,
                location_display = excluded.location_display,
                location_state = excluded.location_state,
                facility_id = excluded.facility_id,
                current_bid_usd = excluded.current_bid_usd,
                buy_now_usd = excluded.buy_now_usd,
                sale_price_usd = excluded.sale_price_usd,
                media_photos_count = excluded.media_photos_count,
                media_has_360 = excluded.media_has_360,
                observed_at = excluded.observed_at,
                updated_at = now();

            insert into auction_lot_versions (
                lot_key, observed_at, payload_hash, raw_blob_name, current_bid_usd,
                sale_price_usd, lot_status, lot_sub_status, payload)
            values (
                @lot_key, @observed_at, @payload_hash, @raw_blob_name, @current_bid_usd,
                @sale_price_usd, @lot_status, @lot_sub_status, cast(@payload as jsonb))
            on conflict (lot_key, payload_hash) do nothing;

            insert into inventory_lot_lifecycle (
                lot_key, platform, is_active, consecutive_misses, first_seen_at, last_seen_at, deactivated_at, updated_at)
            values (
                @lot_key, @platform, true, 0, @observed_at, @observed_at, null, now())
            on conflict (lot_key) do update set
                platform = excluded.platform,
                is_active = true,
                consecutive_misses = 0,
                last_seen_at = excluded.last_seen_at,
                deactivated_at = null,
                updated_at = now();
            """;

        AddParameter(command, "lot_key", identity);
        AddParameter(command, "platform", vehicle.Platform);
        AddParameter(command, "lot_number", vehicle.LotNumber);
        AddParameter(command, "vin", vehicle.Vin);
        AddParameter(command, "title", vehicle.Title);
        AddParameter(command, "year", vehicle.Year);
        AddParameter(command, "make", vehicle.Make);
        AddParameter(command, "model", vehicle.Model);
        AddParameter(command, "vehicle_type", vehicle.VehicleType);
        AddParameter(command, "color", vehicle.Color);
        AddParameter(command, "fuel_type", vehicle.FuelType);
        AddParameter(command, "transmission", vehicle.Transmission);
        AddParameter(command, "drive_type", vehicle.DriveType);
        AddParameter(command, "odometer", vehicle.Odometer);
        AddParameter(command, "damage", vehicle.Damage);
        AddParameter(command, "auction_state", vehicle.Auction?.State);
        AddParameter(command, "auction_at", auctionAtUtc);
        AddParameter(command, "lot_status", vehicle.Auction?.LotStatus);
        AddParameter(command, "lot_sub_status", vehicle.Auction?.LotSubStatus);
        AddParameter(command, "location_display", vehicle.Location?.Display);
        AddParameter(command, "location_state", vehicle.Location?.State);
        AddParameter(command, "facility_id", vehicle.Location?.FacilityId);
        AddParameter(command, "current_bid_usd", vehicle.Pricing?.CurrentBidUsd);
        AddParameter(command, "buy_now_usd", vehicle.Pricing?.BuyNowUsd);
        AddParameter(command, "sale_price_usd", vehicle.Pricing?.SalePriceUsd);
        AddParameter(command, "media_photos_count", vehicle.Media?.ThumbnailsCount);
        AddParameter(command, "media_has_360", vehicle.Media?.Has360);
        AddParameter(command, "observed_at", observedAtUtc);
        AddParameter(command, "payload_hash", payloadHash);
        AddParameter(command, "raw_blob_name", blobName);
        AddParameter(command, "payload", rawJson);

        await command.ExecuteNonQueryAsync(cancellationToken);

        _recent[identity] = new StoredVehicleSnapshot(identity, observedAtUtc, vehicle, rawJson);
        logger.LogInformation("Persisted inventory lot {LotKey} at {ObservedAt}", identity, observedAtUtc);
        await EnqueueScoringCandidateAsync(identity, vehicle.Platform, observedAtUtc, cancellationToken);
    }

    private async Task<AuctionVehicle> ReuseResolvedCopartMediaAsync(string identity, AuctionVehicle vehicle, CancellationToken cancellationToken)
    {
        if (!string.Equals(vehicle.Platform, "copart", StringComparison.OrdinalIgnoreCase) || vehicle.Media?.Photos?.Count is > 1)
            return vehicle;
        var catalogUrl = ReadCopartCatalogUrl(vehicle);
        if (catalogUrl is null) return vehicle;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select payload::text
            from auction_lot_versions
            where lot_key = @lot_key
              and payload ? 'copart_media_resolution'
              and payload #>> '{_raw_source,Image URL}' = @catalog_url
              and coalesce(jsonb_array_length(payload #> '{media,thumbs}'), 0) > 1
            order by created_at desc
            limit 1;
            """;
        AddParameter(command, "lot_key", identity);
        AddParameter(command, "catalog_url", catalogUrl);
        var rawJson = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (string.IsNullOrWhiteSpace(rawJson)) return vehicle;
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        var resolved = JsonSerializer.Deserialize<AuctionVehicle>(rawJson, jsonOptions);
        return resolved?.Media?.Photos?.Count is > 1 ? vehicle with { Media = resolved.Media } : vehicle;
    }

    private static string? ReadCopartCatalogUrl(AuctionVehicle vehicle)
    {
        if (vehicle.RawSource is not { } rawSource || rawSource.ValueKind != JsonValueKind.Object ||
            !rawSource.TryGetProperty("Image URL", out var candidate) || candidate.ValueKind != JsonValueKind.String) return null;
        var value = candidate.GetString()?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public async Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartMediaCandidatesAsync(int maximum, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        var limit = Math.Clamp(maximum, 1, 10000);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select lots.lot_key, lots.observed_at, versions.payload::text
            from auction_lots lots
            left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = lots.lot_key
            join lateral (
                select payload
                from auction_lot_versions
                where lot_key = lots.lot_key
                order by observed_at desc, id desc
                limit 1
            ) versions on true
            where lots.platform = 'copart'
              and coalesce(lifecycle.is_active, true)
              and coalesce(lots.media_photos_count, 0) <= 1
              and coalesce(versions.payload #>> '{_raw_source,Image URL}', '') <> ''
              and not exists (
                  select 1
                  from auction_lot_versions resolved
                  where resolved.lot_key = lots.lot_key
                    and resolved.payload ? 'copart_media_resolution'
                    and resolved.payload #>> '{_raw_source,Image URL}' = versions.payload #>> '{_raw_source,Image URL}'
                    and coalesce(jsonb_array_length(resolved.payload #> '{media,thumbs}'), 0) > 1
              )
            order by lots.updated_at asc, lots.lot_key
            limit @limit;
            """;
        AddParameter(command, "limit", limit);
        var result = new List<StoredVehicleSnapshot>(limit);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var identity = reader.GetString(0);
            var observedAt = reader.GetFieldValue<DateTimeOffset>(1);
            var rawJson = reader.GetString(2);
            var vehicle = JsonSerializer.Deserialize<AuctionVehicle>(rawJson, jsonOptions);
            if (vehicle is not null) result.Add(new StoredVehicleSnapshot(identity, observedAt, vehicle, rawJson));
        }
        return result;
    }

    public async Task<bool> UpdateCopartMediaAsync(string identity, DateTimeOffset expectedObservedAt, AuctionVehicle vehicle, string resolutionStatus, CancellationToken cancellationToken)
    {
        if (!string.Equals(vehicle.Platform, "copart", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Copart media can only update Copart inventory.");

        var observedAtUtc = expectedObservedAt.ToUniversalTime();
        var additional = vehicle.AdditionalData is null
            ? new Dictionary<string, JsonElement>()
            : new Dictionary<string, JsonElement>(vehicle.AdditionalData);
        additional["copart_media_resolution"] = JsonSerializer.SerializeToElement(resolutionStatus);
        var enriched = vehicle with { AdditionalData = additional };
        var rawJson = JsonSerializer.Serialize(enriched);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawJson))).ToLowerInvariant();
        var blobName = BuildBlobName(identity, observedAtUtc, payloadHash);

        await EnsureSchemaAsync(cancellationToken);
        await UploadRawPayloadAsync(blobName, rawJson, cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var update = connection.CreateCommand())
        {
            update.CommandTimeout = _persistence.CommandTimeoutSeconds;
            update.CommandText = """
                update auction_lots
                set media_photos_count = @media_photos_count,
                    media_has_360 = @media_has_360,
                    updated_at = now()
                where lot_key = @lot_key
                  and platform = 'copart'
                  and observed_at = @observed_at;
                """;
            AddParameter(update, "lot_key", identity);
            AddParameter(update, "media_photos_count", enriched.Media?.ThumbnailsCount);
            AddParameter(update, "media_has_360", enriched.Media?.Has360);
            AddParameter(update, "observed_at", observedAtUtc);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandTimeout = _persistence.CommandTimeoutSeconds;
            insert.CommandText = """
                insert into auction_lot_versions (
                    lot_key, observed_at, payload_hash, raw_blob_name, current_bid_usd,
                    sale_price_usd, lot_status, lot_sub_status, payload)
                values (
                    @lot_key, @observed_at, @payload_hash, @raw_blob_name, @current_bid_usd,
                    @sale_price_usd, @lot_status, @lot_sub_status, cast(@payload as jsonb))
                on conflict (lot_key, payload_hash) do nothing;
                """;
            AddParameter(insert, "lot_key", identity);
            AddParameter(insert, "observed_at", observedAtUtc);
            AddParameter(insert, "payload_hash", payloadHash);
            AddParameter(insert, "raw_blob_name", blobName);
            AddParameter(insert, "current_bid_usd", enriched.Pricing?.CurrentBidUsd);
            AddParameter(insert, "sale_price_usd", enriched.Pricing?.SalePriceUsd);
            AddParameter(insert, "lot_status", enriched.Auction?.LotStatus);
            AddParameter(insert, "lot_sub_status", enriched.Auction?.LotSubStatus);
            AddParameter(insert, "payload", rawJson);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        _recent[identity] = new StoredVehicleSnapshot(identity, observedAtUtc, enriched, rawJson);
        logger.LogInformation("Resolved Copart media for inventory lot {LotKey} with {PhotoCount} photos.", identity, enriched.Media?.Photos?.Count ?? 0);
        return true;
    }

    public async Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartTitleMappingCandidatesAsync(int maximum, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var limit = Math.Clamp(maximum, 1, 10_000);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select lots.lot_key, lots.observed_at, versions.payload::text
            from auction_lots lots
            join lateral (
                select payload
                from auction_lot_versions
                where lot_key = lots.lot_key
                order by observed_at desc, id desc
                limit 1
            ) versions on true
            where lots.platform = 'copart'
              and (
                  coalesce(versions.payload ->> 'source_title_mapping_version', '') <> @mapping_version
                  or coalesce(versions.payload ->> 'title_taxonomy_version', '') <> @taxonomy_version
              )
            order by lots.lot_key
            limit @limit;
            """;
        AddParameter(command, "mapping_version", CopartTitleCatalog.Version);
        AddParameter(command, "taxonomy_version", CopartTitleTaxonomy.Version);
        AddParameter(command, "limit", limit);
        var result = new List<StoredVehicleSnapshot>(limit);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var identity = reader.GetString(0);
            var observedAt = reader.GetFieldValue<DateTimeOffset>(1);
            var rawJson = reader.GetString(2);
            var vehicle = JsonSerializer.Deserialize<AuctionVehicle>(rawJson, jsonOptions);
            if (vehicle is not null) result.Add(new StoredVehicleSnapshot(identity, observedAt, vehicle, rawJson));
        }
        return result;
    }

    public async Task<bool> UpdateCopartTitleMappingAsync(string identity, DateTimeOffset expectedObservedAt, AuctionVehicle vehicle, CancellationToken cancellationToken)
    {
        if (!string.Equals(vehicle.Platform, "copart", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Copart title mapping can only update Copart inventory.");

        var observedAtUtc = expectedObservedAt.ToUniversalTime();
        var rawJson = JsonSerializer.Serialize(vehicle);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawJson))).ToLowerInvariant();
        var blobName = BuildBlobName(identity, observedAtUtc, payloadHash);
        await EnsureSchemaAsync(cancellationToken);
        await UploadRawPayloadAsync(blobName, rawJson, cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using (var update = connection.CreateCommand())
        {
            update.CommandTimeout = _persistence.CommandTimeoutSeconds;
            update.CommandText = """
                update auction_lots
                set title = @title,
                    updated_at = now()
                where lot_key = @lot_key
                  and platform = 'copart'
                  and observed_at = @observed_at;
                """;
            AddParameter(update, "title", vehicle.Title);
            AddParameter(update, "lot_key", identity);
            AddParameter(update, "observed_at", observedAtUtc);
            if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return false;
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandTimeout = _persistence.CommandTimeoutSeconds;
            insert.CommandText = """
                insert into auction_lot_versions (
                    lot_key, observed_at, payload_hash, raw_blob_name, current_bid_usd,
                    sale_price_usd, lot_status, lot_sub_status, payload)
                values (
                    @lot_key, @observed_at, @payload_hash, @raw_blob_name, @current_bid_usd,
                    @sale_price_usd, @lot_status, @lot_sub_status, cast(@payload as jsonb))
                on conflict (lot_key, payload_hash) do nothing;
                """;
            AddParameter(insert, "lot_key", identity);
            AddParameter(insert, "observed_at", observedAtUtc);
            AddParameter(insert, "payload_hash", payloadHash);
            AddParameter(insert, "raw_blob_name", blobName);
            AddParameter(insert, "current_bid_usd", vehicle.Pricing?.CurrentBidUsd);
            AddParameter(insert, "sale_price_usd", vehicle.Pricing?.SalePriceUsd);
            AddParameter(insert, "lot_status", vehicle.Auction?.LotStatus);
            AddParameter(insert, "lot_sub_status", vehicle.Auction?.LotSubStatus);
            AddParameter(insert, "payload", rawJson);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        _recent[identity] = new StoredVehicleSnapshot(identity, observedAtUtc, vehicle, rawJson);
        return true;
    }

    public async Task<int> RecordCopartAuctionObservationsAsync(IReadOnlyList<CopartAuctionObservation> observations, CancellationToken cancellationToken)
    {
        if (observations.Count == 0) return 0;
        await EnsureCopartAuctionHistorySchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;

        var values = new List<string>(observations.Count);
        for (var index = 0; index < observations.Count; index++)
        {
            var observation = observations[index];
            values.Add($"(@snapshot_sha{index}, @downloaded_at{index}, @lot_key{index}, @lot_number{index}, @auction_at{index}, @current_bid{index}, @buy_now{index}, @sale_price{index}, @lot_status{index}, @lot_sub_status{index}, @payload_hash{index})");
            AddParameter(command, $"snapshot_sha{index}", observation.SnapshotSha256);
            AddParameter(command, $"downloaded_at{index}", observation.SnapshotDownloadedAt.ToUniversalTime());
            AddParameter(command, $"lot_key{index}", observation.LotKey);
            AddParameter(command, $"lot_number{index}", observation.LotNumber);
            AddParameter(command, $"auction_at{index}", observation.AuctionAt?.ToUniversalTime());
            AddParameter(command, $"current_bid{index}", observation.CurrentBidUsd);
            AddParameter(command, $"buy_now{index}", observation.BuyNowUsd);
            AddParameter(command, $"sale_price{index}", observation.SalePriceUsd);
            AddParameter(command, $"lot_status{index}", observation.LotStatus);
            AddParameter(command, $"lot_sub_status{index}", observation.LotSubStatus);
            AddParameter(command, $"payload_hash{index}", observation.PayloadHash);
        }

        command.CommandText = $"""
            insert into copart_lot_observations (
                snapshot_sha256, snapshot_downloaded_at, lot_key, lot_number, auction_at,
                current_bid_usd, buy_now_usd, sale_price_usd, lot_status, lot_sub_status, payload_hash)
            values {string.Join(",", values)}
            on conflict (snapshot_sha256, lot_key) do nothing;
            """;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task FinalizeCopartAuctionAttemptsAsync(string snapshotSha256, DateTimeOffset finalizedAt, CancellationToken cancellationToken)
    {
        await EnsureCopartAuctionHistorySchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var allSources = string.Equals(snapshotSha256, "*", StringComparison.Ordinal);

        await using (var derive = connection.CreateCommand())
        {
            derive.Transaction = transaction;
            derive.CommandTimeout = _persistence.CommandTimeoutSeconds;
            derive.CommandText = """
                with source as (
                    select lot_key,
                           auction_at,
                           min(snapshot_downloaded_at) as first_observed_at,
                           max(snapshot_downloaded_at) as last_observed_at,
                           (array_agg(current_bid_usd order by snapshot_downloaded_at asc))[1] as first_bid_usd,
                           (array_agg(current_bid_usd order by snapshot_downloaded_at desc))[1] as last_bid_usd,
                           max(current_bid_usd) as maximum_bid_usd,
                           (array_agg(buy_now_usd order by snapshot_downloaded_at desc))[1] as buy_now_usd,
                           max(sale_price_usd) as sale_price_usd,
                           (array_agg(lot_status order by snapshot_downloaded_at desc))[1] as lot_status,
                           (array_agg(lot_sub_status order by snapshot_downloaded_at desc))[1] as lot_sub_status,
                           count(*)::integer as observation_count
                    from copart_lot_observations
                    where auction_at is not null
                      and (@all_sources or snapshot_sha256 = @snapshot_sha256)
                    group by lot_key, auction_at
                )
                insert into copart_auction_attempts (
                    lot_key, attempt_number, auction_at, first_observed_at, last_observed_at,
                    first_bid_usd, last_bid_usd, maximum_bid_usd, buy_now_usd, sale_price_usd,
                    outcome, evidence_level, outcome_evidence, observation_count)
                select
                    lot_key, 0, auction_at, first_observed_at, last_observed_at,
                    first_bid_usd, last_bid_usd, maximum_bid_usd, buy_now_usd, sale_price_usd,
                    case when coalesce(sale_price_usd, 0) > 0 then 'sold_confirmed' else 'scheduled' end,
                    case when coalesce(sale_price_usd, 0) > 0 then 'source_confirmed' else 'source_observed' end,
                    case when coalesce(sale_price_usd, 0) > 0 then 'Copart reported a positive sale price.' else null end,
                    observation_count
                from source
                on conflict (lot_key, auction_at) do update set
                    first_observed_at = least(copart_auction_attempts.first_observed_at, excluded.first_observed_at),
                    last_observed_at = greatest(copart_auction_attempts.last_observed_at, excluded.last_observed_at),
                    last_bid_usd = excluded.last_bid_usd,
                    maximum_bid_usd = greatest(copart_auction_attempts.maximum_bid_usd, excluded.maximum_bid_usd),
                    buy_now_usd = coalesce(excluded.buy_now_usd, copart_auction_attempts.buy_now_usd),
                    sale_price_usd = coalesce(excluded.sale_price_usd, copart_auction_attempts.sale_price_usd),
                    outcome = case when coalesce(excluded.sale_price_usd, 0) > 0 then 'sold_confirmed' else copart_auction_attempts.outcome end,
                    evidence_level = case when coalesce(excluded.sale_price_usd, 0) > 0 then 'source_confirmed' else copart_auction_attempts.evidence_level end,
                    outcome_evidence = case when coalesce(excluded.sale_price_usd, 0) > 0 then 'Copart reported a positive sale price.' else copart_auction_attempts.outcome_evidence end,
                    observation_count = case when @all_sources then excluded.observation_count else copart_auction_attempts.observation_count + excluded.observation_count end,
                    updated_at = now();
                """;
            AddParameter(derive, "snapshot_sha256", snapshotSha256);
            AddParameter(derive, "all_sources", allSources);
            await derive.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var outcomes = connection.CreateCommand())
        {
            outcomes.Transaction = transaction;
            outcomes.CommandTimeout = _persistence.CommandTimeoutSeconds;
            outcomes.CommandText = """
                with relisted as (
                    select distinct on (prior.id) prior.id
                    from copart_auction_attempts prior
                    join copart_auction_attempts later
                      on later.lot_key = prior.lot_key
                     and later.auction_at > prior.auction_at
                     and later.first_observed_at >= prior.auction_at
                    where prior.outcome <> 'sold_confirmed'
                    order by prior.id, later.auction_at
                )
                update copart_auction_attempts attempts
                set outcome = 'relisted_inferred',
                    evidence_level = 'inferred_from_reappearance',
                    outcome_evidence = 'Same Copart lot reappeared after this auction date with a later auction date.',
                    updated_at = now()
                from relisted
                where attempts.id = relisted.id;

                update copart_auction_attempts
                set outcome = 'unknown',
                    evidence_level = 'insufficient_evidence',
                    outcome_evidence = 'Auction date passed without an explicit sale result or later reappearance yet.',
                    updated_at = now()
                where auction_at < @finalized_at
                  and outcome = 'scheduled';
                """;
            AddParameter(outcomes, "finalized_at", finalizedAt.ToUniversalTime());
            await outcomes.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var renumber = connection.CreateCommand())
        {
            renumber.Transaction = transaction;
            renumber.CommandTimeout = _persistence.CommandTimeoutSeconds;
            renumber.CommandText = """
                with numbered as (
                    select id, row_number() over (partition by lot_key order by auction_at)::integer as attempt_number
                    from copart_auction_attempts
                )
                update copart_auction_attempts attempts
                set attempt_number = numbered.attempt_number, updated_at = now()
                from numbered
                where attempts.id = numbered.id
                  and attempts.attempt_number <> numbered.attempt_number;
                """;
            await renumber.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var signals = connection.CreateCommand())
        {
            signals.Transaction = transaction;
            signals.CommandTimeout = _persistence.CommandTimeoutSeconds;
            signals.CommandText = """
                with selected_lots as (
                    select distinct lot_key
                    from copart_lot_observations
                    where @all_sources or snapshot_sha256 = @snapshot_sha256
                ), facts as (
                    select attempts.lot_key,
                           count(*)::integer as attempt_count,
                           count(*) filter (where attempts.outcome = 'relisted_inferred')::integer as relisted_count,
                           min(attempts.auction_at) as first_attempt_at,
                           max(attempts.auction_at) as last_attempt_at,
                           (array_agg(attempts.last_bid_usd order by attempts.auction_at desc))[1] as last_bid_usd,
                           min(attempts.maximum_bid_usd) as historical_minimum_bid_usd,
                           max(attempts.maximum_bid_usd) as historical_maximum_bid_usd,
                           bool_or(attempts.outcome = 'sold_confirmed') as sold_confirmed
                    from copart_auction_attempts attempts
                    join selected_lots on selected_lots.lot_key = attempts.lot_key
                    group by attempts.lot_key
                ), scored as (
                    select *,
                           case when sold_confirmed or relisted_count = 0 then 0 else
                               least(relisted_count, 3) * 25 +
                               case when attempt_count >= 3 then 20 else 0 end +
                               case when first_attempt_at <= @finalized_at - interval '14 days' then 15 else 0 end +
                               case when last_bid_usd is not null and historical_maximum_bid_usd is not null and last_bid_usd < historical_maximum_bid_usd then 15 else 0 end +
                               case when historical_minimum_bid_usd is not null and historical_maximum_bid_usd > 0
                                      and (historical_maximum_bid_usd - historical_minimum_bid_usd) / historical_maximum_bid_usd <= 0.02 then 10 else 0 end
                           end as score
                    from facts
                )
                insert into copart_lot_motivation_signals (
                    lot_key, attempt_count, relisted_inferred_count, score, level, first_attempt_at,
                    last_attempt_at, last_bid_usd, historical_maximum_bid_usd, score_components)
                select lot_key, attempt_count, relisted_count, score,
                       case when score >= 60 then 'high' when score >= 35 then 'medium' when score > 0 then 'watch' else 'none' end,
                       first_attempt_at, last_attempt_at, last_bid_usd, historical_maximum_bid_usd,
                       jsonb_build_object('relisted_inferred_count', relisted_count, 'attempt_count', attempt_count,
                          'relisting_evidence_present', relisted_count > 0, 'sale_confirmed', sold_confirmed,
                          'three_or_more_attempts', attempt_count >= 3,
                          'first_attempt_at_least_14_days', first_attempt_at <= @finalized_at - interval '14 days',
                          'last_bid_below_historical_maximum', coalesce(last_bid_usd < historical_maximum_bid_usd, false),
                          'bidding_within_two_percent', coalesce(historical_minimum_bid_usd is not null and historical_maximum_bid_usd > 0
                              and (historical_maximum_bid_usd - historical_minimum_bid_usd) / historical_maximum_bid_usd <= 0.02, false),
                          'model_version', 'copart-auction-history-v1')
                from scored
                on conflict (lot_key) do update set
                    attempt_count = excluded.attempt_count,
                    relisted_inferred_count = excluded.relisted_inferred_count,
                    score = excluded.score,
                    level = excluded.level,
                    first_attempt_at = excluded.first_attempt_at,
                    last_attempt_at = excluded.last_attempt_at,
                    last_bid_usd = excluded.last_bid_usd,
                    historical_maximum_bid_usd = excluded.historical_maximum_bid_usd,
                    score_components = excluded.score_components,
                    updated_at = now();
                """;
            AddParameter(signals, "snapshot_sha256", snapshotSha256);
            AddParameter(signals, "all_sources", allSources);
            AddParameter(signals, "finalized_at", finalizedAt.ToUniversalTime());
            await signals.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<CopartAuctionHistoryBackfillResult> BackfillCopartAuctionObservationsAsync(int maximum, CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var limit = Math.Clamp(maximum, 1, 250_000);
        await EnsureCopartAuctionHistorySchemaAsync(cancellationToken);
        await EnsureSchemaAsync(cancellationToken);

        var candidates = 0;
        var inserted = 0;
        var failed = 0;
        var failures = new List<string>();
        var pending = new List<CopartAuctionObservation>(1_000);
        const int batchSize = 1_000;

        void RecordFailure(string message)
        {
            failed++;
            if (failures.Count < 100) failures.Add(message);
        }

        async Task FlushPendingAsync()
        {
            if (pending.Count == 0) return;
            inserted += await RecordCopartAuctionObservationsAsync(pending, cancellationToken);
            pending.Clear();
        }

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select versions.id, versions.observed_at, versions.payload_hash, versions.lot_key, versions.payload::text
            from auction_lot_versions versions
            join auction_lots lots on lots.lot_key = versions.lot_key
            where lots.platform = 'copart'
              and not exists (
                  select 1 from copart_lot_observations observed
                  where observed.snapshot_sha256 = concat('legacy-version-', versions.id)
                    and observed.lot_key = versions.lot_key)
            order by versions.id
            limit @limit;
            """;
        AddParameter(command, "limit", limit);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates++;
                var versionId = reader.GetInt64(0);
                var observedAt = reader.GetFieldValue<DateTimeOffset>(1);
                var payloadHash = reader.GetString(2);
                var lotKey = reader.GetString(3);
                var rawJson = reader.GetString(4);
                var lotNumber = lotKey.StartsWith("copart:", StringComparison.OrdinalIgnoreCase) ? lotKey["copart:".Length..] : lotKey;
                CopartAuctionObservation? observation = null;

                try
                {
                    var vehicle = JsonSerializer.Deserialize<AuctionVehicle>(rawJson, jsonOptions);
                    observation = vehicle is null ? null : CopartAuctionObservationFactory.Create(vehicle, $"legacy-version-{versionId}", observedAt);
                    if (observation is null)
                        RecordFailure($"version {versionId}: payload could not produce a Copart auction observation; retained as an idempotent placeholder.");
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    RecordFailure($"version {versionId}: {exception.Message}");
                }

                // A placeholder prevents a malformed legacy payload from being selected and retried forever.
                // It contains no inferred auction date, bid, sale or seller data, so it cannot create an attempt or signal.
                pending.Add(observation is null
                    ? new CopartAuctionObservation($"legacy-version-{versionId}", observedAt, lotKey, lotNumber, null, null, null, null, null, null, payloadHash)
                    : observation with { LotKey = lotKey, LotNumber = lotNumber, PayloadHash = payloadHash });

                if (pending.Count >= batchSize) await FlushPendingAsync();
            }
        }

        await FlushPendingAsync();
        var finalizedAt = DateTimeOffset.UtcNow;
        await FinalizeCopartAuctionAttemptsAsync("*", finalizedAt, cancellationToken);
        var attemptsDerived = await CountCopartAuctionAttemptsAsync(cancellationToken);
        if (failed > failures.Count)
            failures.Add($"{failed - failures.Count} additional legacy payload conversion failure(s) omitted from this summary.");
        return new CopartAuctionHistoryBackfillResult(true, candidates, inserted, attemptsDerived, failed, DateTimeOffset.UtcNow - startedAt, failures);
    }

    public async Task<CopartAuctionHistoryReport> GetCopartAuctionHistoryReportAsync(CancellationToken cancellationToken)
    {
        await EnsureCopartAuctionHistorySchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select
                (select count(*) from copart_lot_observations) as observations,
                (select count(distinct snapshot_sha256) from copart_lot_observations) as distinct_snapshots,
                (select count(*) from copart_auction_attempts) as attempts,
                (select count(*) from copart_lot_motivation_signals) as signals,
                (select coalesce(jsonb_object_agg(outcome, total), '{}'::jsonb)
                 from (select outcome, count(*)::bigint as total from copart_auction_attempts group by outcome) outcomes) as attempts_by_outcome,
                (select coalesce(jsonb_object_agg(evidence_level, total), '{}'::jsonb)
                 from (select evidence_level, count(*)::bigint as total from copart_auction_attempts group by evidence_level) evidence) as attempts_by_evidence,
                (select coalesce(jsonb_object_agg(level, total), '{}'::jsonb)
                 from (select level, count(*)::bigint as total from copart_lot_motivation_signals group by level) signal_levels) as signals_by_level;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Copart auction-history report did not return aggregate counts.");

        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        static IReadOnlyDictionary<string, long> ReadCounts(string raw, JsonSerializerOptions options) =>
            JsonSerializer.Deserialize<Dictionary<string, long>>(raw, options) ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        return new CopartAuctionHistoryReport(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            ReadCounts(reader.GetString(4), jsonOptions),
            ReadCounts(reader.GetString(5), jsonOptions),
            ReadCounts(reader.GetString(6), jsonOptions));
    }

    private async Task<int> CountCopartAuctionAttemptsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = "select count(*) from copart_auction_attempts;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<Guid> StartSyncRunAsync(InventorySyncRunStart start, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var runId = start.RunId ?? Guid.NewGuid();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            insert into inventory_sync_runs (
                run_id, provider, platform_scope, state_scope, pages_requested, page_size, started_at, status)
            values (
                @run_id, @provider, @platform_scope, @state_scope, @pages_requested, @page_size, @started_at, 'running');
            """;
        AddParameter(command, "run_id", runId);
        AddParameter(command, "provider", start.Provider);
        AddParameter(command, "platform_scope", start.Platform);
        AddParameter(command, "state_scope", start.State);
        AddParameter(command, "pages_requested", start.PagesRequested);
        AddParameter(command, "page_size", start.PageSize);
        AddParameter(command, "started_at", start.StartedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return runId;
    }

    public async Task CompleteSyncRunAsync(Guid runId, InventorySyncRunCompletion completion, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var failuresJson = JsonSerializer.Serialize(completion.Failures);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            update inventory_sync_runs
            set finished_at = @finished_at,
                vehicles_observed = @vehicles_observed,
                requests_issued = @requests_issued,
                status = @status,
                failures = cast(@failures as jsonb)
            where run_id = @run_id;
            """;
        AddParameter(command, "run_id", runId);
        AddParameter(command, "finished_at", completion.FinishedAt);
        AddParameter(command, "vehicles_observed", completion.VehiclesObserved);
        AddParameter(command, "requests_issued", completion.RequestsIssued);
        AddParameter(command, "status", completion.Failures.Count == 0 ? "succeeded" : "completed_with_errors");
        AddParameter(command, "failures", failuresJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<CopartSnapshotRegistration> TryRegisterCopartSnapshotAsync(CopartSnapshotReceipt receipt, decimal minimumRowCountRatio, int baselineSnapshotCount, bool allowInterruptedSnapshotRetry, CancellationToken cancellationToken)
    {
        await EnsureCopartSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);

        var historicalRows = new List<int>();
        await using (var baseline = connection.CreateCommand())
        {
            baseline.CommandTimeout = _persistence.CommandTimeoutSeconds;
            baseline.CommandText = """
                select row_count
                from copart_snapshot_manifests
                where status = 'succeeded' and is_complete = true
                order by downloaded_at desc
                limit @limit;
                """;
            AddParameter(baseline, "limit", Math.Max(1, baselineSnapshotCount));
            await using var reader = await baseline.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) historicalRows.Add(reader.GetInt32(0));
        }

        var ordered = historicalRows.OrderBy(value => value).ToArray();
        var median = ordered.Length == 0 ? (int?)null : ordered[ordered.Length / 2];
        if (median is > 0 && receipt.RowCount < decimal.Ceiling(median.Value * minimumRowCountRatio))
            return new CopartSnapshotRegistration(false, false, null, median, "F05: Copart snapshot row count is below the accepted baseline.");

        var runId = Guid.NewGuid();
        await using var insert = connection.CreateCommand();
        insert.CommandTimeout = _persistence.CommandTimeoutSeconds;
        insert.CommandText = """
            insert into copart_snapshot_manifests (
                sha256, file_name, downloaded_at, file_size_bytes, row_count, processing_batch_size,
                is_complete, status, run_id)
            values (
                @sha256, @file_name, @downloaded_at, @file_size_bytes, @row_count, @processing_batch_size,
                true, 'running', @run_id)
            on conflict (sha256) do update set
                file_name = excluded.file_name,
                downloaded_at = excluded.downloaded_at,
                file_size_bytes = excluded.file_size_bytes,
                row_count = excluded.row_count,
                processing_batch_size = excluded.processing_batch_size,
                is_complete = true,
                status = 'running',
                run_id = excluded.run_id,
                finished_at = null,
                observed_count = 0,
                accepted_count = 0,
                discarded_count = 0,
                quarantined_count = 0,
                marked_count = 0,
                error_count = 0,
                failures = '[]'::jsonb,
                updated_at = now()
            where copart_snapshot_manifests.status = 'completed_with_errors'
               or (@allow_interrupted_snapshot_retry and copart_snapshot_manifests.status = 'running')
            returning run_id;
            """;
        AddParameter(insert, "sha256", receipt.Sha256);
        AddParameter(insert, "file_name", receipt.FileName);
        AddParameter(insert, "downloaded_at", receipt.DownloadedAt);
        AddParameter(insert, "file_size_bytes", receipt.FileSizeBytes);
        AddParameter(insert, "row_count", receipt.RowCount);
        AddParameter(insert, "processing_batch_size", receipt.ProcessingBatchSize);
        AddParameter(insert, "run_id", runId);
        AddParameter(insert, "allow_interrupted_snapshot_retry", allowInterruptedSnapshotRetry);
        var inserted = await insert.ExecuteScalarAsync(cancellationToken);
        return inserted is null
            ? new CopartSnapshotRegistration(false, true, null, median, "F02: Copart snapshot hash was already processed.")
            : new CopartSnapshotRegistration(true, false, runId, median, null);
    }

    public async Task CompleteCopartSnapshotAsync(Guid runId, CopartSnapshotCompletion completion, CancellationToken cancellationToken)
    {
        await EnsureCopartSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            update copart_snapshot_manifests
            set finished_at = @finished_at,
                observed_count = @observed_count,
                accepted_count = @accepted_count,
                discarded_count = @discarded_count,
                quarantined_count = @quarantined_count,
                marked_count = @marked_count,
                error_count = @error_count,
                is_complete = @is_complete,
                status = @status,
                failures = cast(@failures as jsonb),
                updated_at = now()
            where run_id = @run_id;
            """;
        AddParameter(command, "run_id", runId);
        AddParameter(command, "finished_at", completion.FinishedAt);
        AddParameter(command, "observed_count", completion.Observed);
        AddParameter(command, "accepted_count", completion.Accepted);
        AddParameter(command, "discarded_count", completion.Discarded);
        AddParameter(command, "quarantined_count", completion.Quarantined);
        AddParameter(command, "marked_count", completion.Marked);
        AddParameter(command, "error_count", completion.Errors);
        AddParameter(command, "is_complete", completion.IsComplete);
        AddParameter(command, "status", completion.Failures.Count == 0 && completion.IsComplete ? "succeeded" : "completed_with_errors");
        AddParameter(command, "failures", JsonSerializer.Serialize(completion.Failures));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PersistProviderUsageAsync(string provider, JsonElement usage, DateTimeOffset capturedAt, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            insert into provider_usage_snapshots (provider, captured_at, usage)
            values (@provider, @captured_at, cast(@usage as jsonb));
            """;
        AddParameter(command, "provider", provider);
        AddParameter(command, "captured_at", capturedAt);
        AddParameter(command, "usage", usage.GetRawText());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PersistEligibilityDecisionAsync(EligibilityEvaluation evaluation, DateTimeOffset evaluatedAt, CancellationToken cancellationToken)
    {
        await EnsureEligibilitySchemaAsync(cancellationToken);
        var identity = $"{evaluation.AuctionSource ?? "unknown"}:{evaluation.LotNumber ?? "unknown"}";
        var safeIdentity = string.Concat(identity.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
        var blobName = $"eligibility/{evaluatedAt:yyyy/MM/dd}/{safeIdentity}/{evaluatedAt:HHmmssfff}-{evaluation.Decision.ToLowerInvariant()}.json";
        var json = JsonSerializer.Serialize(evaluation);
        await UploadRawPayloadAsync(blobName, json, cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            insert into eligibility_decisions (
                lot_key, auction_source, lot_number, vin_masked, decision, load_to_system,
                rule_version, evaluated_at, discard_reasons, flags, data_quality_notes,
                evaluated_fields, audit_blob_name)
            values (
                @lot_key, @auction_source, @lot_number, @vin_masked, @decision, @load_to_system,
                @rule_version, @evaluated_at, cast(@discard_reasons as jsonb), cast(@flags as jsonb),
                cast(@data_quality_notes as jsonb), cast(@evaluated_fields as jsonb), @audit_blob_name)
            on conflict (lot_key) do update set
                auction_source = excluded.auction_source,
                lot_number = excluded.lot_number,
                vin_masked = excluded.vin_masked,
                decision = excluded.decision,
                load_to_system = excluded.load_to_system,
                rule_version = excluded.rule_version,
                evaluated_at = excluded.evaluated_at,
                discard_reasons = excluded.discard_reasons,
                flags = excluded.flags,
                data_quality_notes = excluded.data_quality_notes,
                evaluated_fields = excluded.evaluated_fields,
                audit_blob_name = excluded.audit_blob_name,
                updated_at = now();
            """;
        AddParameter(command, "lot_key", identity);
        AddParameter(command, "auction_source", evaluation.AuctionSource);
        AddParameter(command, "lot_number", evaluation.LotNumber);
        AddParameter(command, "vin_masked", evaluation.VinMasked);
        AddParameter(command, "decision", evaluation.Decision);
        AddParameter(command, "load_to_system", evaluation.LoadToSystem);
        AddParameter(command, "rule_version", evaluation.RuleVersion);
        AddParameter(command, "evaluated_at", evaluatedAt);
        AddParameter(command, "discard_reasons", JsonSerializer.Serialize(evaluation.DiscardReasons));
        AddParameter(command, "flags", JsonSerializer.Serialize(evaluation.Flags));
        AddParameter(command, "data_quality_notes", JsonSerializer.Serialize(evaluation.DataQualityNotes));
        AddParameter(command, "evaluated_fields", JsonSerializer.Serialize(evaluation.EvaluatedFields));
        AddParameter(command, "audit_blob_name", blobName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<EligibilityAuditPage> GetDiscardedEligibilityDecisionsAsync(int page, int pageSize, string? ruleCode, string? query, CancellationToken cancellationToken)
    {
        await EnsureEligibilitySchemaAsync(cancellationToken);
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        var normalizedRule = string.IsNullOrWhiteSpace(ruleCode) ? null : ruleCode.Trim().ToUpperInvariant();
        var normalizedQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
        countCommand.CommandText = """
            select count(*)
            from eligibility_decisions
            where decision = 'DESCARTAR'
              and (cast(@rule_filter as jsonb) = '[]'::jsonb or discard_reasons @> cast(@rule_filter as jsonb))
              and (@query = '' or lot_number ilike '%' || @query || '%' or vin_masked ilike '%' || @query || '%');
            """;
        AddParameter(countCommand, "rule_filter", normalizedRule is null ? "[]" : JsonSerializer.Serialize(new[] { new { code = normalizedRule } }));
        AddParameter(countCommand, "query", normalizedQuery ?? string.Empty);
        var total = Convert.ToInt64(await countCommand.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);

        await using var itemsCommand = connection.CreateCommand();
        itemsCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
        itemsCommand.CommandText = """
            select evaluated_at, auction_source, lot_number, vin_masked, decision, load_to_system,
                   rule_version, discard_reasons::text, flags::text, data_quality_notes::text, evaluated_fields::text
            from eligibility_decisions
            where decision = 'DESCARTAR'
              and (cast(@rule_filter as jsonb) = '[]'::jsonb or discard_reasons @> cast(@rule_filter as jsonb))
              and (@query = '' or lot_number ilike '%' || @query || '%' or vin_masked ilike '%' || @query || '%')
            order by evaluated_at desc, lot_key
            limit @limit offset @offset;
            """;
        AddParameter(itemsCommand, "rule_filter", normalizedRule is null ? "[]" : JsonSerializer.Serialize(new[] { new { code = normalizedRule } }));
        AddParameter(itemsCommand, "query", normalizedQuery ?? string.Empty);
        AddParameter(itemsCommand, "limit", safePageSize);
        AddParameter(itemsCommand, "offset", (safePage - 1) * safePageSize);
        var items = new List<EligibilityAuditItem>();
        await using (var reader = await itemsCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var evaluation = new EligibilityEvaluation(
                    reader.GetString(4),
                    reader.GetBoolean(5),
                    ReadNullableString(reader, 2),
                    ReadNullableString(reader, 1),
                    ReadNullableString(reader, 3),
                    JsonSerializer.Deserialize<EligibilityReason[]>(reader.GetString(7)) ?? [],
                    JsonSerializer.Deserialize<EligibilityReason[]>(reader.GetString(8)) ?? [],
                    JsonSerializer.Deserialize<string[]>(reader.GetString(9)) ?? [],
                    JsonSerializer.Deserialize<string[]>(reader.GetString(10)) ?? [],
                    reader.GetString(6));
                items.Add(new EligibilityAuditItem(reader.GetFieldValue<DateTimeOffset>(0), evaluation));
            }
        }

        await using var summaryCommand = connection.CreateCommand();
        summaryCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
        summaryCommand.CommandText = """
            select reason->>'code' as code, reason->>'name' as name, count(*)
            from eligibility_decisions
            cross join lateral jsonb_array_elements(discard_reasons) reason
            where decision = 'DESCARTAR'
            group by reason->>'code', reason->>'name'
            order by reason->>'code';
            """;
        var summary = new List<EligibilityRuleSummary>();
        await using (var reader = await summaryCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                summary.Add(new EligibilityRuleSummary(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            }
        }

        return new EligibilityAuditPage(
            safePage,
            safePageSize,
            total,
            Math.Max(1, (int)Math.Ceiling(total / (double)safePageSize)),
            items,
            summary);
    }

    public async Task<InventoryValidationReport> GetValidationReportAsync(CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var summary = connection.CreateCommand();
        summary.CommandTimeout = _persistence.CommandTimeoutSeconds;
        summary.CommandText = """
            select
                count(*) as lots,
                (select count(*) from auction_lot_versions) as versions,
                count(*) filter (where nullif(btrim(vin), '') is not null) as vin_present,
                count(*) filter (where nullif(btrim(title), '') is not null) as title_present,
                count(*) filter (where nullif(btrim(damage), '') is not null) as damage_present,
                count(*) filter (where odometer is not null) as odometer_present,
                count(*) filter (where current_bid_usd is not null) as current_bid_present,
                count(*) filter (where auction_at is not null) as auction_date_present,
                count(*) filter (where coalesce(media_photos_count, 0) > 0) as lots_with_photos
            from auction_lots;
            """;

        long lots;
        long versions;
        long vinPresent;
        long titlePresent;
        long damagePresent;
        long odometerPresent;
        long currentBidPresent;
        long auctionDatePresent;
        long lotsWithPhotos;
        await using (var reader = await summary.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            lots = reader.GetInt64(0);
            versions = reader.GetInt64(1);
            vinPresent = reader.GetInt64(2);
            titlePresent = reader.GetInt64(3);
            damagePresent = reader.GetInt64(4);
            odometerPresent = reader.GetInt64(5);
            currentBidPresent = reader.GetInt64(6);
            auctionDatePresent = reader.GetInt64(7);
            lotsWithPhotos = reader.GetInt64(8);
        }

        await using var samplesCommand = connection.CreateCommand();
        samplesCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
        samplesCommand.CommandText = """
            select lot_key, vin, title, location_state, current_bid_usd, auction_at, damage, odometer, media_photos_count
            from auction_lots
            order by observed_at desc, lot_key
            limit 5;
            """;
        var samples = new List<InventorySampleLot>();
        await using (var reader = await samplesCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                samples.Add(new InventorySampleLot(
                    reader.GetString(0),
                    ReadNullableString(reader, 1),
                    ReadNullableString(reader, 2),
                    ReadNullableString(reader, 3),
                    ReadNullableDecimal(reader, 4),
                    ReadNullableDateTimeOffset(reader, 5),
                    ReadNullableString(reader, 6),
                    ReadNullableDecimal(reader, 7),
                    ReadNullableInt32(reader, 8)));
            }
        }

        return new InventoryValidationReport(
            lots,
            versions,
            vinPresent,
            titlePresent,
            damagePresent,
            odometerPresent,
            currentBidPresent,
            auctionDatePresent,
            lotsWithPhotos,
            samples);
    }

    public async Task<string> GetCopartPublicationReportAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        object? manifest = null;
        await using (var manifestCommand = connection.CreateCommand())
        {
            manifestCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
            manifestCommand.CommandText = """
                select left(sha256, 12), file_name, downloaded_at, row_count, observed_count,
                       accepted_count, discarded_count, quarantined_count, marked_count, error_count,
                       status, is_complete
                from copart_snapshot_manifests
                order by downloaded_at desc
                limit 1;
                """;
            await using var reader = await manifestCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                manifest = new
                {
                    SnapshotShaPrefix = reader.GetString(0),
                    FileName = reader.GetString(1),
                    DownloadedAt = reader.GetFieldValue<DateTimeOffset>(2),
                    RowsDeclared = reader.GetInt32(3),
                    Observed = reader.GetInt32(4),
                    Accepted = reader.GetInt32(5),
                    Discarded = reader.GetInt32(6),
                    Quarantined = reader.GetInt32(7),
                    Marked = reader.GetInt32(8),
                    Errors = reader.GetInt32(9),
                    Status = reader.GetString(10),
                    IsComplete = reader.GetBoolean(11)
                };
            }
        }

        long totalLots;
        long activeLots;
        long inactiveLots;
        await using (var lotCommand = connection.CreateCommand())
        {
            lotCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
            lotCommand.CommandText = """
                select count(*)::bigint,
                       count(*) filter (where coalesce(lifecycle.is_active, true))::bigint,
                       count(*) filter (where not coalesce(lifecycle.is_active, true))::bigint
                from auction_lots lots
                left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = lots.lot_key
                where lots.platform = 'copart';
                """;
            await using var reader = await lotCommand.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            totalLots = reader.GetInt64(0);
            activeLots = reader.GetInt64(1);
            inactiveLots = reader.GetInt64(2);
        }

        var decisions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using (var auditCommand = connection.CreateCommand())
        {
            auditCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
            auditCommand.CommandText = """
                select decision, count(*)::bigint
                from eligibility_decisions
                where lower(coalesce(auction_source, '')) = 'copart'
                group by decision
                order by decision;
                """;
            await using var reader = await auditCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) decisions[reader.GetString(0)] = reader.GetInt64(1);
        }

        return JsonSerializer.Serialize(new
        {
            Manifest = manifest,
            Lots = new { Total = totalLots, Active = activeLots, Inactive = inactiveLots },
            EligibilityDecisions = decisions
        });
    }

    public async Task<string> GetStorageDiagnosticsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var session = connection.CreateCommand();
        session.CommandTimeout = _persistence.CommandTimeoutSeconds;
        session.CommandText = "select current_database(), current_user, current_setting('search_path');";

        string database;
        string databaseUser;
        string searchPath;
        await using (var reader = await session.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            database = reader.GetString(0);
            databaseUser = reader.GetString(1);
            searchPath = reader.GetString(2);
        }

        await using var relations = connection.CreateCommand();
        relations.CommandTimeout = _persistence.CommandTimeoutSeconds;
        relations.CommandText = """
            select n.nspname, c.relname, c.relkind::text, c.reltuples::bigint, pg_get_userbyid(c.relowner)
            from pg_catalog.pg_class c
            inner join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where n.nspname = 'public'
              and (c.relname like 'inventory_%' or c.relname like 'copart_%' or c.relname like 'execution_%')
            order by n.nspname, c.relname;
            """;

        var relationList = new List<object>();
        await using (var reader = await relations.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                relationList.Add(new
                {
                    Schema = reader.GetString(0),
                    Relation = reader.GetString(1),
                    Kind = reader.GetString(2),
                    EstimatedRows = reader.GetInt64(3),
                    Owner = reader.GetString(4)
                });
            }
        }

        var recentCopartRuns = new List<object>();
        await using (var runs = connection.CreateCommand())
        {
            runs.CommandTimeout = _persistence.CommandTimeoutSeconds;
            runs.CommandText = """
                select run_id, platform_scope, state_scope, started_at, finished_at,
                       vehicles_observed, requests_issued, status, jsonb_array_length(failures)
                from inventory_sync_runs
                where provider = 'copart-excel'
                order by started_at desc
                limit 5;
                """;
            await using var reader = await runs.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                recentCopartRuns.Add(new
                {
                    RunId = reader.GetGuid(0),
                    Platform = reader.GetString(1),
                    Scope = reader.GetString(2),
                    StartedAt = reader.GetFieldValue<DateTimeOffset>(3),
                    FinishedAt = ReadNullableDateTimeOffset(reader, 4),
                    Observed = reader.GetInt32(5),
                    Requests = reader.GetInt32(6),
                    Status = reader.GetString(7),
                    FailureCount = reader.GetInt32(8)
                });
            }
        }

        long titleMapped;
        long titleUnmapped;
        long titleNotBackfilled;
        await using (var titleCoverage = connection.CreateCommand())
        {
            titleCoverage.CommandTimeout = _persistence.CommandTimeoutSeconds;
            titleCoverage.CommandText = """
                with latest as (
                    select distinct on (lot_key) lot_key, payload
                    from auction_lot_versions
                    order by lot_key, observed_at desc, id desc
                )
                select
                    count(*) filter (where latest.payload ->> 'source_title_mapping_version' = @mapping_version
                                     and latest.payload ->> 'source_title_mapping' = 'mapped')::bigint as mapped,
                    count(*) filter (where latest.payload ->> 'source_title_mapping_version' = @mapping_version
                                     and latest.payload ->> 'source_title_mapping' = 'unmapped')::bigint as unmapped,
                    count(*) filter (where coalesce(latest.payload ->> 'source_title_mapping_version', '') <> @mapping_version)::bigint as not_backfilled
                from auction_lots lots
                join latest on latest.lot_key = lots.lot_key
                where lots.platform = 'copart';
                """;
            AddParameter(titleCoverage, "mapping_version", CopartTitleCatalog.Version);
            await using var reader = await titleCoverage.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            titleMapped = reader.GetInt64(0);
            titleUnmapped = reader.GetInt64(1);
            titleNotBackfilled = reader.GetInt64(2);
        }

        long zeroPhotos;
        long onePhoto;
        long multiplePhotos;
        await using (var photoCoverage = connection.CreateCommand())
        {
            photoCoverage.CommandTimeout = _persistence.CommandTimeoutSeconds;
            photoCoverage.CommandText = """
                select
                    count(*) filter (where coalesce(lots.media_photos_count, 0) = 0)::bigint as zero_photos,
                    count(*) filter (where coalesce(lots.media_photos_count, 0) = 1)::bigint as one_photo,
                    count(*) filter (where coalesce(lots.media_photos_count, 0) > 1)::bigint as multiple_photos
                from auction_lots lots
                left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = lots.lot_key
                where lots.platform = 'copart'
                  and coalesce(lifecycle.is_active, true);
                """;
            await using var reader = await photoCoverage.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            zeroPhotos = reader.GetInt64(0);
            onePhoto = reader.GetInt64(1);
            multiplePhotos = reader.GetInt64(2);
        }

        return JsonSerializer.Serialize(new
        {
            Database = database,
            DatabaseUser = databaseUser,
            SearchPath = searchPath,
            Relations = relationList,
            RecentCopartRuns = recentCopartRuns,
            CopartTitleMappingCoverage = new
            {
                CatalogVersion = CopartTitleCatalog.Version,
                Mapped = titleMapped,
                Unmapped = titleUnmapped,
                NotBackfilled = titleNotBackfilled
            },
            CopartActivePhotoCoverage = new { ZeroPhotos = zeroPhotos, OnePhoto = onePhoto, MultiplePhotos = multiplePhotos }
        });
    }

    public async Task<string> GetPublicMediaManifestAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select distinct on (lot_key) lot_key, coalesce(payload #> '{media,photos}', '[]'::jsonb)
            from auction_lot_versions
            order by lot_key, observed_at desc;
            """;

        var lots = new List<object>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var lotKey = reader.GetString(0);
            using var document = JsonDocument.Parse(reader.GetString(1));
            var allPhotos = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString())
                    .Where(url => !string.IsNullOrWhiteSpace(url))
                    .ToArray()
                : [];

            var publicPhotos = allPhotos
                .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
                    string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo))
                .Take(6)
                .ToArray();

            lots.Add(new
            {
                LotKey = lotKey,
                PhotosReported = allPhotos.Length,
                PublicPhotos = publicPhotos,
                PhotosWithQueryString = allPhotos.Count(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Query))
            });
        }

        return JsonSerializer.Serialize(lots);
    }

    public async Task<string> GetCopartLotMediaDiagnosticsAsync(string lotNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lotNumber)) throw new ArgumentException("Lot number is required.", nameof(lotNumber));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select lots.lot_key, lots.lot_number, lots.media_photos_count, lots.updated_at, latest.payload::text,
                   coalesce(lifecycle.is_active, true), coalesce(lifecycle.consecutive_misses, 0)
            from auction_lots lots
            join lateral (
                select payload
                from auction_lot_versions
                where lot_key = lots.lot_key
                order by observed_at desc, id desc
                limit 1
            ) latest on true
            left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = lots.lot_key
            where lots.platform = 'copart'
              and lots.lot_number = @lot_number
            limit 1;
            """;
        AddParameter(command, "lot_number", lotNumber.Trim());

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return JsonSerializer.Serialize(new { Found = false, LotNumber = lotNumber.Trim() });

        var lotKey = reader.GetString(0);
        var storedPhotoCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        var updatedAt = reader.GetFieldValue<DateTimeOffset>(3);
        using var payload = JsonDocument.Parse(reader.GetString(4));
        var root = payload.RootElement;
        var photos = root.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Object &&
                     media.TryGetProperty("thumbs", out var thumbs) && thumbs.ValueKind == JsonValueKind.Array
            ? thumbs.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Cast<string>()
                .ToArray()
            : [];
        var photoHosts = photos
            .Select(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : null)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var catalogUrl = root.TryGetProperty("_raw_source", out var raw) && raw.ValueKind == JsonValueKind.Object &&
                         raw.TryGetProperty("Image URL", out var catalog) && catalog.ValueKind == JsonValueKind.String
            ? catalog.GetString()
            : null;
        var catalogHost = Uri.TryCreate(catalogUrl, UriKind.Absolute, out var catalogUri) ? catalogUri.Host : null;
        var resolution = root.TryGetProperty("copart_media_resolution", out var status) && status.ValueKind == JsonValueKind.String
            ? status.GetString()
            : null;

        return JsonSerializer.Serialize(new
        {
            Found = true,
            LotKey = lotKey,
            LotNumber = reader.GetString(1),
            UpdatedAt = updatedAt,
            StoredPhotoCount = storedPhotoCount,
            PayloadPhotoCount = photos.Length,
            GalleryResolved = photos.Length > 1,
            PhotoHosts = photoHosts,
            PhotosWithQueryString = photos.Count(value => Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Query)),
            CatalogUrlPresent = !string.IsNullOrWhiteSpace(catalogUrl),
            CatalogHost = catalogHost,
            ResolutionStatus = resolution,
            IsActive = reader.GetBoolean(5),
            ConsecutiveMisses = reader.GetInt32(6),
            HasMaskedVin = root.TryGetProperty("vin", out var vin) && vin.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(vin.GetString()),
            HasSeller = root.TryGetProperty("seller", out var seller) && seller.ValueKind == JsonValueKind.Object && seller.TryGetProperty("name", out var sellerName) && sellerName.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(sellerName.GetString())
        });
    }

    public async Task<StoredVehicleSnapshot?> GetByPlatformAndLotAsync(string platform, string lotNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(platform)) throw new ArgumentException("Platform is required.", nameof(platform));
        if (string.IsNullOrWhiteSpace(lotNumber)) throw new ArgumentException("Lot number is required.", nameof(lotNumber));

        await EnsureSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select lots.lot_key, lots.observed_at, versions.payload::text
            from auction_lots lots
            join lateral (
                select payload
                from auction_lot_versions
                where lot_key = lots.lot_key
                order by observed_at desc, id desc
                limit 1
            ) versions on true
            left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = lots.lot_key
            where lots.platform = @platform
              and lots.lot_number = @lot_number
              and coalesce(lifecycle.is_active, true)
            limit 1;
            """;
        AddParameter(command, "platform", platform.Trim().ToLowerInvariant());
        AddParameter(command, "lot_number", lotNumber.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var rawJson = reader.GetString(2);
        var vehicle = JsonSerializer.Deserialize<AuctionVehicle>(rawJson, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
        return vehicle is null ? null : new StoredVehicleSnapshot(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), vehicle, rawJson);
    }

    public async Task<IReadOnlyCollection<StoredVehicleSnapshot>> GetRecentAsync(int maximum, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        var limit = Math.Clamp(maximum, 1, 5000);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select latest_lots.lot_key, latest_lots.observed_at, latest_lots.payload::text
            from (
                select distinct on (lot_key) lot_key, observed_at, payload
                from auction_lot_versions
                order by lot_key, observed_at desc
            ) as latest_lots
            left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = latest_lots.lot_key
            where coalesce(lifecycle.is_active, true)
            order by latest_lots.observed_at desc
            limit @limit;
            """;
        AddParameter(command, "limit", limit);

        var snapshots = new List<StoredVehicleSnapshot>(limit);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        while (await reader.ReadAsync(cancellationToken))
        {
            var identity = reader.GetString(0);
            var observedAt = reader.GetFieldValue<DateTimeOffset>(1);
            var rawJson = reader.GetString(2);
            var vehicle = JsonSerializer.Deserialize<AuctionVehicle>(rawJson, jsonOptions);
            if (vehicle is not null)
            {
                snapshots.Add(new StoredVehicleSnapshot(identity, observedAt, vehicle, rawJson));
            }
        }

        return snapshots.OrderByDescending(snapshot => snapshot.ObservedAt).ToArray();
    }

    public async Task<InventoryPage> GetPageAsync(InventoryBrowseQuery query, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 100);
        var sort = query.Sort is "bid-low" or "bid-high" ? query.Sort : "auction";
        var platform = string.IsNullOrWhiteSpace(query.Platform) || string.Equals(query.Platform, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : query.Platform.Trim().ToLowerInvariant();
        var search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim();
        var offset = (page - 1) * pageSize;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            with filtered as (
                select lots.lot_key, lots.observed_at, latest.payload::text as payload,
                       lots.current_bid_usd, lots.auction_at
                from auction_lots lots
                join lateral (
                    select payload
                    from auction_lot_versions versions
                    where versions.lot_key = lots.lot_key
                    order by versions.observed_at desc
                    limit 1
                ) latest on true
                left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = lots.lot_key
                where coalesce(lifecycle.is_active, true)
                  and (@platform is null or lots.platform = @platform)
                  and (@search is null or concat_ws(' ', lots.lot_number, lots.title, lots.make, lots.model, latest.payload #>> '{sale_document,name}') ilike '%' || @search || '%')
                  and (@year_from is null or lots.year >= @year_from)
                  and (@year_to is null or lots.year <= @year_to)
                  and (@maximum_bid is null or lots.current_bid_usd is null or lots.current_bid_usd <= @maximum_bid)
                  and (not @require_bid or lots.current_bid_usd is not null)
                  and (not @require_photos or coalesce(lots.media_photos_count, 0) > 0)
                  and (@odometer_from is null or lots.odometer >= @odometer_from)
                  and (@odometer_to is null or lots.odometer <= @odometer_to)
                  and (@auction_from is null or (lots.auction_at at time zone 'America/New_York')::date >= @auction_from)
                  and (@auction_to is null or (lots.auction_at at time zone 'America/New_York')::date <= @auction_to)
                  and (@estimated_total_from is null or (lots.current_bid_usd is not null and lots.current_bid_usd + 699 >= @estimated_total_from))
                  and (@estimated_total_to is null or (lots.current_bid_usd is not null and lots.current_bid_usd + 399 <= @estimated_total_to))
                  and (cardinality(@makes) = 0 or lots.make = any(@makes))
                  and (cardinality(@models) = 0 or lots.model = any(@models))
                  and (cardinality(@facilities) = 0 or lots.facility_id = any(@facilities) or lots.location_display = any(@facilities))
                  and (cardinality(@states) = 0 or lots.location_state = any(@states))
                  and (cardinality(@vehicle_types) = 0 or lots.vehicle_type = any(@vehicle_types))
                  and (cardinality(@damages) = 0 or lots.damage = any(@damages))
                  and (cardinality(@title_types) = 0 or latest.payload #>> '{sale_document,name}' = any(@title_types))
                  and (cardinality(@drives) = 0 or lots.drive_type = any(@drives))
                  and (cardinality(@transmissions) = 0 or lots.transmission = any(@transmissions))
                  and (cardinality(@fuels) = 0 or lots.fuel_type = any(@fuels))
                  and (@include_special_titles or upper(coalesce(latest.payload #>> '{sale_document,name}', '')) not like '%CERTIFICATE OF DESTRUCTION%')
                  and (@include_special_titles or upper(coalesce(latest.payload #>> '{sale_document,name}', '')) not like '%JUNK%')
                  and (@include_special_titles or upper(coalesce(latest.payload #>> '{sale_document,name}', '')) not like '%NON REPAIRABLE%')
                  and (@include_special_titles or upper(coalesce(latest.payload #>> '{sale_document,name}', '')) not like '%PARTS ONLY%')
            ), numbered as (
                select *, count(*) over() as total_count
                from filtered
            )
            select lot_key, observed_at, payload, total_count
            from numbered
            order by
                case when @sort = 'bid-low' then current_bid_usd end asc nulls last,
                case when @sort = 'bid-high' then current_bid_usd end desc nulls last,
                case when @sort = 'auction' then auction_at end asc nulls last,
                observed_at desc,
                lot_key
            limit @limit offset @offset;
            """;
        AddParameter(command, "platform", platform);
        AddParameter(command, "search", search);
        AddParameter(command, "year_from", query.YearFrom);
        AddParameter(command, "year_to", query.YearTo);
        AddParameter(command, "maximum_bid", query.MaximumBid);
        AddParameter(command, "require_bid", query.RequireBid);
        AddParameter(command, "require_photos", query.RequirePhotos);
        AddParameter(command, "odometer_from", query.OdometerFrom);
        AddParameter(command, "odometer_to", query.OdometerTo);
        AddParameter(command, "auction_from", query.AuctionFrom);
        AddParameter(command, "auction_to", query.AuctionTo);
        AddParameter(command, "estimated_total_from", query.EstimatedTotalFrom);
        AddParameter(command, "estimated_total_to", query.EstimatedTotalTo);
        AddParameter(command, "makes", query.Makes?.ToArray() ?? Array.Empty<string>());
        AddParameter(command, "models", query.Models?.ToArray() ?? Array.Empty<string>());
        AddParameter(command, "facilities", query.Facilities?.ToArray() ?? Array.Empty<string>());
        AddParameter(command, "states", query.States?.ToArray() ?? Array.Empty<string>());
        AddParameter(command, "vehicle_types", query.VehicleTypes?.ToArray() ?? Array.Empty<string>());
        AddParameter(command, "damages", query.Damages?.ToArray() ?? Array.Empty<string>());
        AddParameter(command, "title_types", query.TitleTypes?.ToArray() ?? Array.Empty<string>());
        AddParameter(command, "drives", query.Drives?.ToArray() ?? Array.Empty<string>());
        AddParameter(command, "transmissions", query.Transmissions?.ToArray() ?? Array.Empty<string>());
        AddParameter(command, "fuels", query.Fuels?.ToArray() ?? Array.Empty<string>());
        AddParameter(command, "include_special_titles", query.IncludeSpecialTitles);
        AddParameter(command, "sort", sort);
        AddParameter(command, "limit", pageSize);
        AddParameter(command, "offset", offset);

        var vehicles = new List<StoredVehicleSnapshot>(pageSize);
        long total = 0;
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            total = reader.GetInt64(3);
            var rawJson = reader.GetString(2);
            var vehicle = JsonSerializer.Deserialize<AuctionVehicle>(rawJson, jsonOptions);
            if (vehicle is not null)
                vehicles.Add(new StoredVehicleSnapshot(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), vehicle, rawJson));
        }

        return new InventoryPage(page, pageSize, total, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)), vehicles);
    }

    public async Task<InventoryReconciliationResult> ReconcileSourceAsync(string platform, IReadOnlyCollection<string> observedLotKeys, bool isCompleteSnapshot, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        var normalizedPlatform = platform.Trim().ToLowerInvariant();
        if (!isCompleteSnapshot)
            return new InventoryReconciliationResult(normalizedPlatform, false, observedLotKeys.Count, 0, 0, 0);

        await EnsureLifecycleSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var observed = observedLotKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        await using (var backfill = connection.CreateCommand())
        {
            backfill.Transaction = transaction;
            backfill.CommandTimeout = _persistence.CommandTimeoutSeconds;
            backfill.CommandText = """
                insert into inventory_lot_lifecycle (lot_key, platform, is_active, consecutive_misses, first_seen_at, last_seen_at)
                select lot_key, platform, true, 0, observed_at, observed_at
                from auction_lots
                where platform = @platform
                on conflict (lot_key) do nothing;
                """;
            AddParameter(backfill, "platform", normalizedPlatform);
            await backfill.ExecuteNonQueryAsync(cancellationToken);
        }

        var reactivated = 0;
        if (observed.Length > 0)
        {
            await using var reactivate = connection.CreateCommand();
            reactivate.Transaction = transaction;
            reactivate.CommandTimeout = _persistence.CommandTimeoutSeconds;
            reactivate.CommandText = """
                update inventory_lot_lifecycle
                set is_active = true,
                    consecutive_misses = 0,
                    last_seen_at = @observed_at,
                    deactivated_at = null,
                    updated_at = now()
                where platform = @platform
                  and lot_key = any(@observed)
                  and not is_active;
                """;
            AddParameter(reactivate, "platform", normalizedPlatform);
            AddParameter(reactivate, "observed_at", observedAt);
            AddParameter(reactivate, "observed", observed);
            reactivated = await reactivate.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var reconcile = connection.CreateCommand();
        reconcile.Transaction = transaction;
        reconcile.CommandTimeout = _persistence.CommandTimeoutSeconds;
        reconcile.CommandText = """
            with updated as (
                update inventory_lot_lifecycle
                set consecutive_misses = consecutive_misses + 1,
                    is_active = case when consecutive_misses + 1 >= 3 then false else is_active end,
                    deactivated_at = case when consecutive_misses + 1 >= 3 then coalesce(deactivated_at, @observed_at) else deactivated_at end,
                    updated_at = now()
                where platform = @platform
                  and is_active
                  and not (lot_key = any(@observed))
                returning is_active
            )
            select count(*)::int, count(*) filter (where not is_active)::int from updated;
            """;
        AddParameter(reconcile, "platform", normalizedPlatform);
        AddParameter(reconcile, "observed_at", observedAt);
        AddParameter(reconcile, "observed", observed);
        int incremented;
        int deactivated;
        await using (var reader = await reconcile.ExecuteReaderAsync(cancellationToken))
        {
            await reader.ReadAsync(cancellationToken);
            incremented = reader.GetInt32(0);
            deactivated = reader.GetInt32(1);
        }

        await transaction.CommitAsync(cancellationToken);
        return new InventoryReconciliationResult(normalizedPlatform, true, observed.Length, reactivated, incremented, deactivated);
    }

    private async Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        if (!_persistence.RunMigrations)
        {
            return;
        }

        if (_schemaInitialized)
        {
            return;
        }

        await SchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_schemaInitialized)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
                create table if not exists schema_migrations (
                    migration_id text primary key,
                    applied_at timestamptz not null default now()
                );

                insert into schema_migrations (migration_id)
                values ('001_inventory_baseline')
                on conflict (migration_id) do nothing;

                create table if not exists inventory_sync_runs (
                    run_id uuid primary key,
                    provider text not null,
                    platform_scope text not null,
                    state_scope text not null,
                    pages_requested integer not null,
                    page_size integer not null,
                    started_at timestamptz not null,
                    finished_at timestamptz,
                    vehicles_observed integer not null default 0,
                    requests_issued integer not null default 0,
                    status text not null,
                    failures jsonb not null default '[]'::jsonb,
                    created_at timestamptz not null default now()
                );

                create table if not exists provider_usage_snapshots (
                    id bigserial primary key,
                    provider text not null,
                    captured_at timestamptz not null,
                    usage jsonb not null,
                    created_at timestamptz not null default now()
                );

                create table if not exists copart_snapshot_manifests (
                    sha256 text primary key,
                    file_name text not null,
                    downloaded_at timestamptz not null,
                    file_size_bytes bigint not null,
                    row_count integer not null,
                    processing_batch_size integer not null,
                    is_complete boolean not null,
                    status text not null,
                    run_id uuid not null unique,
                    finished_at timestamptz,
                    observed_count integer not null default 0,
                    accepted_count integer not null default 0,
                    discarded_count integer not null default 0,
                    quarantined_count integer not null default 0,
                    marked_count integer not null default 0,
                    error_count integer not null default 0,
                    failures jsonb not null default '[]'::jsonb,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create index if not exists ix_copart_snapshot_manifests_status_downloaded on copart_snapshot_manifests (status, downloaded_at desc);

                create index if not exists ix_provider_usage_snapshots_provider_captured on provider_usage_snapshots (provider, captured_at desc);

                create table if not exists eligibility_decisions (
                    lot_key text primary key,
                    auction_source text,
                    lot_number text,
                    vin_masked text,
                    decision text not null,
                    load_to_system boolean not null,
                    rule_version text not null,
                    evaluated_at timestamptz not null,
                    discard_reasons jsonb not null default '[]'::jsonb,
                    flags jsonb not null default '[]'::jsonb,
                    data_quality_notes jsonb not null default '[]'::jsonb,
                    evaluated_fields jsonb not null default '[]'::jsonb,
                    audit_blob_name text not null,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create index if not exists ix_eligibility_decisions_decision_evaluated on eligibility_decisions (decision, evaluated_at desc);
                create index if not exists ix_eligibility_decisions_discard_reasons on eligibility_decisions using gin (discard_reasons);

                create table if not exists auction_lots (
                    lot_key text primary key,
                    platform text not null,
                    lot_number text,
                    vin text,
                    title text,
                    year integer,
                    make text,
                    model text,
                    vehicle_type text,
                    color text,
                    fuel_type text,
                    transmission text,
                    drive_type text,
                    odometer numeric,
                    damage text,
                    auction_state text,
                    auction_at timestamptz,
                    lot_status text,
                    lot_sub_status text,
                    location_display text,
                    location_state text,
                    facility_id text,
                    current_bid_usd numeric,
                    buy_now_usd numeric,
                    sale_price_usd numeric,
                    media_photos_count integer,
                    media_has_360 boolean,
                    observed_at timestamptz not null,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create index if not exists ix_auction_lots_observed_at on auction_lots (observed_at desc);
                create index if not exists ix_auction_lots_platform_state on auction_lots (platform, location_state);
                create index if not exists ix_auction_lots_vin on auction_lots (vin) where vin is not null;

                create table if not exists auction_lot_versions (
                    id bigserial primary key,
                    lot_key text not null references auction_lots(lot_key),
                    observed_at timestamptz not null,
                    payload_hash text not null,
                    raw_blob_name text not null,
                    current_bid_usd numeric,
                    sale_price_usd numeric,
                    lot_status text,
                    lot_sub_status text,
                    payload jsonb not null,
                    created_at timestamptz not null default now(),
                    unique (lot_key, payload_hash)
                );

                create index if not exists ix_auction_lot_versions_lot_observed on auction_lot_versions (lot_key, observed_at desc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _schemaInitialized = true;
        }
        finally
        {
            SchemaLock.Release();
        }
    }

    private async Task EnsureCopartAuctionHistorySchemaAsync(CancellationToken cancellationToken)
    {
        if (_copartAuctionHistorySchemaInitialized) return;
        await CopartAuctionHistorySchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_copartAuctionHistorySchemaInitialized) return;
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
            create table if not exists copart_lot_observations (
                snapshot_sha256 text not null,
                snapshot_downloaded_at timestamptz not null,
                lot_key text not null,
                lot_number text not null,
                auction_at timestamptz,
                current_bid_usd numeric,
                buy_now_usd numeric,
                sale_price_usd numeric,
                lot_status text,
                lot_sub_status text,
                payload_hash text not null,
                created_at timestamptz not null default now(),
                primary key (snapshot_sha256, lot_key)
            );
            create index if not exists ix_copart_lot_observations_lot_auction
                on copart_lot_observations (lot_key, auction_at, snapshot_downloaded_at);
            create index if not exists ix_copart_lot_observations_snapshot
                on copart_lot_observations (snapshot_sha256, snapshot_downloaded_at);

            create table if not exists copart_auction_attempts (
                id bigserial primary key,
                lot_key text not null,
                attempt_number integer not null default 0,
                auction_at timestamptz not null,
                first_observed_at timestamptz not null,
                last_observed_at timestamptz not null,
                first_bid_usd numeric,
                last_bid_usd numeric,
                maximum_bid_usd numeric,
                buy_now_usd numeric,
                sale_price_usd numeric,
                outcome text not null,
                evidence_level text not null,
                outcome_evidence text,
                observation_count integer not null default 1,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                unique (lot_key, auction_at)
            );
            create index if not exists ix_copart_auction_attempts_lot_date
                on copart_auction_attempts (lot_key, auction_at);
            create index if not exists ix_copart_auction_attempts_outcome_date
                on copart_auction_attempts (outcome, auction_at desc);

            create table if not exists copart_lot_motivation_signals (
                lot_key text primary key,
                attempt_count integer not null,
                relisted_inferred_count integer not null,
                score integer not null,
                level text not null,
                first_attempt_at timestamptz,
                last_attempt_at timestamptz,
                last_bid_usd numeric,
                historical_maximum_bid_usd numeric,
                score_components jsonb not null default '{}'::jsonb,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now()
            );
            create index if not exists ix_copart_lot_motivation_signals_score
                on copart_lot_motivation_signals (score desc, last_attempt_at desc);
            """;
                    await command.ExecuteNonQueryAsync(cancellationToken);
            _copartAuctionHistorySchemaInitialized = true;
        }
        finally
        {
            CopartAuctionHistorySchemaLock.Release();
        }
    }

    private async Task EnsureCopartSchemaAsync(CancellationToken cancellationToken)

    {
        if (_copartSchemaInitialized) return;
        await CopartSchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_copartSchemaInitialized) return;
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
                create table if not exists copart_snapshot_manifests (
                    sha256 text primary key,
                    file_name text not null,
                    downloaded_at timestamptz not null,
                    file_size_bytes bigint not null,
                    row_count integer not null,
                    processing_batch_size integer not null,
                    is_complete boolean not null,
                    status text not null,
                    run_id uuid not null unique,
                    finished_at timestamptz,
                    observed_count integer not null default 0,
                    accepted_count integer not null default 0,
                    discarded_count integer not null default 0,
                    quarantined_count integer not null default 0,
                    marked_count integer not null default 0,
                    error_count integer not null default 0,
                    failures jsonb not null default '[]'::jsonb,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );
                create index if not exists ix_copart_snapshot_manifests_status_downloaded
                    on copart_snapshot_manifests (status, downloaded_at desc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _copartSchemaInitialized = true;
        }
        finally
        {
            CopartSchemaLock.Release();
        }
    }

    private async Task EnsureEligibilitySchemaAsync(CancellationToken cancellationToken)
    {
        if (_eligibilitySchemaInitialized)
        {
            return;
        }

        await EligibilitySchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_eligibilitySchemaInitialized)
            {
                return;
            }

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
                create table if not exists eligibility_decisions (
                    lot_key text primary key,
                    auction_source text,
                    lot_number text,
                    vin_masked text,
                    decision text not null,
                    load_to_system boolean not null,
                    rule_version text not null,
                    evaluated_at timestamptz not null,
                    discard_reasons jsonb not null default '[]'::jsonb,
                    flags jsonb not null default '[]'::jsonb,
                    data_quality_notes jsonb not null default '[]'::jsonb,
                    evaluated_fields jsonb not null default '[]'::jsonb,
                    audit_blob_name text not null,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );

                create index if not exists ix_eligibility_decisions_decision_evaluated on eligibility_decisions (decision, evaluated_at desc);
                create index if not exists ix_eligibility_decisions_discard_reasons on eligibility_decisions using gin (discard_reasons);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _eligibilitySchemaInitialized = true;
        }
        finally
        {
            EligibilitySchemaLock.Release();
        }
    }

    private async Task EnsureLifecycleSchemaAsync(CancellationToken cancellationToken)
    {
        if (_lifecycleSchemaInitialized) return;
        await LifecycleSchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_lifecycleSchemaInitialized) return;
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
                create table if not exists inventory_lot_lifecycle (
                    lot_key text primary key,
                    platform text not null,
                    is_active boolean not null default true,
                    consecutive_misses integer not null default 0,
                    first_seen_at timestamptz not null,
                    last_seen_at timestamptz not null,
                    deactivated_at timestamptz,
                    updated_at timestamptz not null default now()
                );
                create index if not exists ix_inventory_lot_lifecycle_platform_active
                    on inventory_lot_lifecycle (platform, is_active, consecutive_misses);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _lifecycleSchemaInitialized = true;
        }
        finally
        {
            LifecycleSchemaLock.Release();
        }
    }

    private async Task UploadRawPayloadAsync(string blobName, string rawJson, CancellationToken cancellationToken)
    {
        var serviceClient = new BlobServiceClient(new Uri(_blob.AccountUrl), _credential);
        var containerClient = serviceClient.GetBlobContainerClient(_blob.ContainerName);
        var blobClient = containerClient.GetBlobClient(blobName);

        try
        {
            await blobClient.UploadAsync(BinaryData.FromString(rawJson), overwrite: false, cancellationToken: cancellationToken);
        }
        catch (RequestFailedException exception) when (exception.Status == StatusCodes.Status409Conflict ||
                                                    string.Equals(exception.ErrorCode, "BlobAlreadyExists", StringComparison.OrdinalIgnoreCase))
        {
            // Content-addressed payloads are immutable. A retry that produces the same hash already has the audit blob.
        }
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        await OpenConnectionAsync(_persistence.Database, cancellationToken);

    private async Task<NpgsqlConnection> OpenConnectionAsync(string database, CancellationToken cancellationToken)
    {
        var accessToken = _persistence.AccessToken;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]),
                cancellationToken);
            accessToken = token.Token;
        }

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = _persistence.PostgreSqlHost,
            Database = database,
            Username = _persistence.DatabaseUser,
            Password = accessToken,
            SslMode = SslMode.VerifyFull,
            Timeout = _persistence.CommandTimeoutSeconds,
            CommandTimeout = _persistence.CommandTimeoutSeconds
        }.ConnectionString;

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static void AddParameter(NpgsqlCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? ReadNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static decimal? ReadNullableDecimal(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static DateTimeOffset? ReadNullableDateTimeOffset(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static int? ReadNullableInt32(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string QuoteIdentifier(string identifier) =>
        $"\"{identifier.Replace("\"", "\"\"")}\"";

    private static string BuildIdentity(AuctionVehicle vehicle) => string.Join(':',
        vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown",
        vehicle.LotNumber?.Trim() ?? vehicle.Vin?.Trim() ?? throw new InvalidOperationException("Apibara vehicle has neither lot number nor VIN."));

    private static string BuildBlobName(string identity, DateTimeOffset observedAt, string payloadHash)
    {
        var safeIdentity = string.Concat(identity.Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-'));
        return $"snapshots/{observedAt:yyyy/MM/dd}/{safeIdentity}/{observedAt:HHmmssfff}-{payloadHash[..12]}.json";
    }
}
