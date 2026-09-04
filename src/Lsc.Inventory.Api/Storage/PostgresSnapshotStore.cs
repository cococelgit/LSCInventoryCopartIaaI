using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Globalization;
using System.Text.RegularExpressions;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Scoring;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore(
    IOptions<PersistenceOptions> persistenceOptions,
    IOptions<BlobAuditOptions> blobOptions,
    ILogger<PostgresSnapshotStore> logger,
    IFacetsV2SharedCache? facetsV2SharedCache = null) : IInventorySnapshotStore, IAuctionsApiImportJobStore
{
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static readonly SemaphoreSlim AuditSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim SearchProjectionSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim EligibilitySchemaLock = new(1, 1);
    private static readonly SemaphoreSlim LifecycleSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim ScoringSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim NationalSyncSchemaLock = new(1, 1);
    private static bool _schemaInitialized;
    private static bool _auditSchemaInitialized;
    private static bool _searchProjectionSchemaInitialized;
    private static bool _eligibilitySchemaInitialized;
    private static bool _lifecycleSchemaInitialized;
    private static bool _scoringSchemaInitialized;
    private static bool _nationalSyncSchemaInitialized;
    private readonly PersistenceOptions _persistence = persistenceOptions.Value;
    private readonly BlobAuditOptions _blob = blobOptions.Value;
    private readonly IFacetsV2SharedCache _facetsV2SharedCache = facetsV2SharedCache ?? DisabledFacetsV2SharedCache.Instance;
    private readonly SemaphoreSlim _databaseTokenLock = new(1, 1);
    private AccessToken _cachedDatabaseAccessToken;
    private bool _projectionReadyCache;
    private long _projectionReadyCheckedAtTicks;
    private const string ActiveLifecyclePredicate = "coalesce(lifecycle.is_active, current.is_active)";
    private readonly TokenCredential _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = persistenceOptions.Value.ManagedIdentityClientId
    });

    private static JsonSerializerOptions CreateStoredVehicleJsonOptions() => new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

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

    public async Task<InventoryLotPersistenceResult> PersistAsync(AuctionVehicle vehicle, DateTimeOffset observedAt, CancellationToken cancellationToken, Guid? runId = null)
    {
        var identity = BuildIdentity(vehicle);
        var rawJson = JsonSerializer.Serialize(vehicle);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawJson))).ToLowerInvariant();
        var blobName = BuildBlobName(identity, observedAt, payloadHash);

        await EnsureSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        await UploadRawPayloadAsync(blobName, rawJson, cancellationToken);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        string? previousHash = null;
        AuctionVehicle? previousVehicle = null;
        await using (var previous = connection.CreateCommand())
        {
            previous.CommandTimeout = _persistence.CommandTimeoutSeconds;
            previous.CommandText = "select payload_hash, payload::text from auction_lot_versions where lot_key = @lot_key order by observed_at desc limit 1;";
            AddParameter(previous, "lot_key", identity);
            await using var reader = await previous.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                previousHash = reader.GetString(0);
                previousVehicle = JsonSerializer.Deserialize<AuctionVehicle>(reader.GetString(1), new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
            }
        }
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
        AddParameter(command, "auction_at", vehicle.Auction?.AuctionAt);
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
        AddParameter(command, "observed_at", observedAt);
        AddParameter(command, "payload_hash", payloadHash);
        AddParameter(command, "raw_blob_name", blobName);
        AddParameter(command, "payload", rawJson);

        await command.ExecuteNonQueryAsync(cancellationToken);
        await UpsertSearchProjectionAsync(connection, identity, vehicle, observedAt, rawJson, cancellationToken);

        logger.LogInformation("Persisted inventory lot {LotKey} at {ObservedAt}", identity, observedAt);
        var action = previousHash is null ? "created" : string.Equals(previousHash, payloadHash, StringComparison.Ordinal) ? "unchanged" : "updated";
        var changedFields = action == "created" ? new[] { "initial" } : action == "updated" ? DescribeChangedFields(previousVehicle, vehicle) : Array.Empty<string>();
        var result = new InventoryLotPersistenceResult(identity, action, changedFields);
        if (!string.Equals(action, "unchanged", StringComparison.Ordinal))
        {
            try
            {
                await EnqueueScoringCandidateAsync(identity, vehicle.Platform, observedAt, cancellationToken);
            }
            catch (Exception exception)
            {
                // Scoring is deliberately non-blocking: inventory ingestion stays available if its queue is unavailable.
                logger.LogError(exception, "Could not enqueue lot {LotKey} for LSC scoring.", identity);
            }
        }
        if (runId is not null)
        {
            await RecordSyncRunEventAsync(new InventorySyncRunEvent(
                runId.Value,
                vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown",
                identity,
                vehicle.LotNumber,
                MaskVin(vehicle.Vin),
                action,
                changedFields,
                [],
                observedAt), cancellationToken);
        }
        return result;
    }

    private async Task UpsertSearchProjectionAsync(NpgsqlConnection connection, string lotKey, AuctionVehicle vehicle, DateTimeOffset observedAt, string payloadJson, CancellationToken cancellationToken)
    {
        var sourceTitle = TitleFacetCategory.SourceTitle(vehicle);
        var titleType = TitleFacetCategory.Classify(vehicle);
        var specialTitle = TitleFacetCategory.IsSpecial(titleType);
        var hasPhotos = (vehicle.Media?.Photos?.Count ?? 0) > 0 || (vehicle.Media?.Items?.Count ?? 0) > 0;
        decimal? engineSize = decimal.TryParse(vehicle.VehicleSpecs?.Engine?.SizeLiters, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsedEngineSize) ? parsedEngineSize : null;

        await using var projection = connection.CreateCommand();
        projection.CommandTimeout = _persistence.CommandTimeoutSeconds;
        projection.CommandText = """
            insert into inventory_search_current (
                lot_key, platform, lot_number, vin, title, year, make, model, vehicle_type, title_type,
                color, fuel_type, transmission, drive_type, body_style, primary_damage, secondary_damage,
                seller_type, engine_layout, cylinders, loss_type, start_code, auction_state, auction_at,
                lot_status, lot_sub_status, location_display, location_state, facility_id, odometer,
                current_bid_usd, buy_now_usd, provider_estimate_from, provider_estimate_to, engine_size_liters,
                horsepower, has_key, has_photos, media_has_360, is_buy_now, is_special_title, is_active,
                observed_at, payload, search_text, updated_at)
            values (
                @lot_key, @platform, @lot_number, @vin, @title, @year, @make, @model, @vehicle_type, @title_type,
                @color, @fuel_type, @transmission, @drive_type, @body_style, @primary_damage, @secondary_damage,
                @seller_type, @engine_layout, @cylinders, @loss_type, @start_code, @auction_state, @auction_at,
                @lot_status, @lot_sub_status, @location_display, @location_state, @facility_id, @odometer,
                @current_bid_usd, @buy_now_usd, @provider_estimate_from, @provider_estimate_to, @engine_size_liters,
                @horsepower, @has_key, @has_photos, @media_has_360, @is_buy_now, @is_special_title, true,
                @observed_at, cast(@payload as jsonb), @search_text, now())
            on conflict (lot_key) do update set
                platform = excluded.platform, lot_number = excluded.lot_number, vin = excluded.vin,
                title = excluded.title, year = excluded.year, make = excluded.make, model = excluded.model,
                vehicle_type = excluded.vehicle_type, title_type = excluded.title_type, color = excluded.color,
                fuel_type = excluded.fuel_type, transmission = excluded.transmission, drive_type = excluded.drive_type,
                body_style = excluded.body_style, primary_damage = excluded.primary_damage,
                secondary_damage = excluded.secondary_damage, seller_type = excluded.seller_type,
                engine_layout = excluded.engine_layout, cylinders = excluded.cylinders, loss_type = excluded.loss_type,
                start_code = excluded.start_code, auction_state = excluded.auction_state, auction_at = excluded.auction_at,
                lot_status = excluded.lot_status, lot_sub_status = excluded.lot_sub_status,
                location_display = excluded.location_display, location_state = excluded.location_state,
                facility_id = excluded.facility_id, odometer = excluded.odometer,
                current_bid_usd = excluded.current_bid_usd, buy_now_usd = excluded.buy_now_usd,
                provider_estimate_from = excluded.provider_estimate_from, provider_estimate_to = excluded.provider_estimate_to,
                engine_size_liters = excluded.engine_size_liters, horsepower = excluded.horsepower,
                has_key = excluded.has_key, has_photos = excluded.has_photos, media_has_360 = excluded.media_has_360,
                is_buy_now = excluded.is_buy_now, is_special_title = excluded.is_special_title, is_active = true,
                observed_at = excluded.observed_at, payload = excluded.payload,
                search_text = excluded.search_text, updated_at = now();
            """;
        AddParameter(projection, "lot_key", lotKey);
        AddParameter(projection, "platform", vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown");
        AddParameter(projection, "lot_number", vehicle.LotNumber);
        AddParameter(projection, "vin", vehicle.Vin);
        AddParameter(projection, "title", vehicle.Title);
        AddParameter(projection, "year", vehicle.Year);
        AddParameter(projection, "make", vehicle.Make);
        AddParameter(projection, "model", vehicle.Model);
        AddParameter(projection, "vehicle_type", vehicle.VehicleType);
        AddParameter(projection, "title_type", titleType);
        AddParameter(projection, "color", vehicle.Color ?? vehicle.VehicleSpecs?.ExteriorColor);
        AddParameter(projection, "fuel_type", vehicle.FuelType ?? vehicle.VehicleSpecs?.FuelType);
        AddParameter(projection, "transmission", vehicle.Transmission ?? vehicle.VehicleSpecs?.Transmission);
        AddParameter(projection, "drive_type", vehicle.DriveType ?? vehicle.VehicleSpecs?.DriveType);
        AddParameter(projection, "body_style", vehicle.VehicleSpecs?.BodyStyle ?? vehicle.Details?.VehicleDescription?.BodyStyle);
        AddParameter(projection, "primary_damage", vehicle.Damage ?? vehicle.Condition?.PrimaryDamage);
        AddParameter(projection, "secondary_damage", vehicle.Condition?.SecondaryDamage);
        AddParameter(projection, "seller_type", vehicle.Seller?.Type);
        AddParameter(projection, "engine_layout", vehicle.VehicleSpecs?.Engine?.Layout);
        AddParameter(projection, "cylinders", vehicle.Details?.VehicleDescription?.Cylinders);
        AddParameter(projection, "loss_type", vehicle.Condition?.Loss);
        AddParameter(projection, "start_code", vehicle.Condition?.RunCondition?.Value ?? vehicle.Condition?.RunCondition?.Label);
        AddParameter(projection, "auction_state", vehicle.Auction?.State);
        AddParameter(projection, "auction_at", vehicle.Auction?.AuctionAt);
        AddParameter(projection, "lot_status", vehicle.Auction?.LotStatus);
        AddParameter(projection, "lot_sub_status", vehicle.Auction?.LotSubStatus);
        AddParameter(projection, "location_display", vehicle.Location?.Display);
        AddParameter(projection, "location_state", vehicle.Location?.State);
        AddParameter(projection, "facility_id", vehicle.Location?.FacilityId);
        AddParameter(projection, "odometer", vehicle.Odometer);
        AddParameter(projection, "current_bid_usd", vehicle.Pricing?.CurrentBidUsd);
        AddParameter(projection, "buy_now_usd", vehicle.Pricing?.BuyNowUsd);
        AddParameter(projection, "provider_estimate_from", vehicle.Pricing?.EstimatedCost?.FromUsd);
        AddParameter(projection, "provider_estimate_to", vehicle.Pricing?.EstimatedCost?.ToUsd);
        AddParameter(projection, "engine_size_liters", engineSize);
        AddParameter(projection, "horsepower", vehicle.VehicleSpecs?.Engine?.Horsepower);
        AddParameter(projection, "has_key", vehicle.Condition?.HasKey);
        AddParameter(projection, "has_photos", hasPhotos);
        AddParameter(projection, "media_has_360", vehicle.Media?.Has360);
        AddParameter(projection, "is_buy_now", vehicle.Pricing?.BuyNowUsd is > 0m);
        AddParameter(projection, "is_special_title", specialTitle);
        AddParameter(projection, "observed_at", observedAt);
        AddParameter(projection, "payload", payloadJson);
        AddParameter(projection, "search_text", string.Join(' ', new[] { lotKey, vehicle.LotNumber, vehicle.Vin, vehicle.Title, vehicle.Make, vehicle.Model, sourceTitle, titleType }.Where(value => !string.IsNullOrWhiteSpace(value))));
        await projection.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<Guid> StartSyncRunAsync(InventorySyncRunStart start, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureAuditSchemaAsync(cancellationToken);
        var runId = Guid.NewGuid();

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
        await EnsureAuditSchemaAsync(cancellationToken);
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
        AddParameter(command, "status", completion.Cancelled ? "cancelled" : completion.Failures.Count == 0 ? "succeeded" : "completed_with_errors");
        AddParameter(command, "failures", failuresJson);
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var metrics = connection.CreateCommand();
        metrics.CommandTimeout = _persistence.CommandTimeoutSeconds;
        metrics.CommandText = """
            insert into inventory_execution_run_metrics (
                run_id, loaded_count, marked_count, discarded_count, quarantined_count, error_count, pages_processed,
                cycle_completed, reactivated_count, misses_incremented_count, deactivated_count, failures, updated_at)
            values (@run_id, @loaded_count, @marked_count, @discarded_count, @quarantined_count, @error_count, @pages_processed,
                @cycle_completed, @reactivated_count, @misses_incremented_count, @deactivated_count, cast(@failures as jsonb), now())
            on conflict (run_id) do update set
                loaded_count = excluded.loaded_count, marked_count = excluded.marked_count,
                discarded_count = excluded.discarded_count, quarantined_count = excluded.quarantined_count,
                error_count = excluded.error_count, pages_processed = excluded.pages_processed,
                cycle_completed = excluded.cycle_completed, reactivated_count = excluded.reactivated_count,
                misses_incremented_count = excluded.misses_incremented_count, deactivated_count = excluded.deactivated_count,
                failures = excluded.failures, updated_at = now();
            """;
        AddParameter(metrics, "run_id", runId);
        AddParameter(metrics, "loaded_count", completion.Loaded);
        AddParameter(metrics, "marked_count", completion.Marked);
        AddParameter(metrics, "discarded_count", completion.Discarded);
        AddParameter(metrics, "quarantined_count", completion.Quarantined);
        AddParameter(metrics, "error_count", completion.Errors);
        AddParameter(metrics, "pages_processed", completion.PagesProcessed);
        AddParameter(metrics, "cycle_completed", completion.CycleCompleted);
        AddParameter(metrics, "reactivated_count", completion.Reconciliation?.Reactivated);
        AddParameter(metrics, "misses_incremented_count", completion.Reconciliation?.MissesIncremented);
        AddParameter(metrics, "deactivated_count", completion.Reconciliation?.Deactivated);
        AddParameter(metrics, "failures", failuresJson);
        await metrics.ExecuteNonQueryAsync(cancellationToken);
        await RefreshSearchProjectionStatisticsIfReadyAsync(cancellationToken);
    }

    public async Task RecordSyncRunEventAsync(InventorySyncRunEvent syncEvent, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureAuditSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            insert into inventory_sync_run_events (
                run_id, platform, lot_key, lot_number, vin_masked, action, changed_fields, rule_codes, occurred_at)
            values (
                @run_id, @platform, @lot_key, @lot_number, @vin_masked, @action,
                cast(@changed_fields as jsonb), cast(@rule_codes as jsonb), @occurred_at);
            """;
        AddParameter(command, "run_id", syncEvent.RunId);
        AddParameter(command, "platform", syncEvent.Platform);
        AddParameter(command, "lot_key", syncEvent.LotKey);
        AddParameter(command, "lot_number", syncEvent.LotNumber);
        AddParameter(command, "vin_masked", syncEvent.VinMasked);
        AddParameter(command, "action", syncEvent.Action);
        AddParameter(command, "changed_fields", JsonSerializer.Serialize(syncEvent.ChangedFields));
        AddParameter(command, "rule_codes", JsonSerializer.Serialize(syncEvent.RuleCodes));
        AddParameter(command, "occurred_at", syncEvent.OccurredAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<InventoryExecutionHistoryPage> GetExecutionHistoryAsync(InventoryExecutionHistoryRequest request, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureAuditSchemaAsync(cancellationToken);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var platform = request.Platform?.Trim().ToLowerInvariant() ?? string.Empty;
        var status = request.Status?.Trim().ToLowerInvariant() ?? string.Empty;
        const string runs = """
            with raw_history as (
                select base.run_id, base.provider, base.platform_scope as platform, base.state_scope as scope, base.status,
                       base.started_at, base.finished_at, base.vehicles_observed as observed, base.requests_issued as requests,
                       metrics.loaded_count, metrics.marked_count, metrics.discarded_count, metrics.quarantined_count,
                       metrics.error_count, metrics.pages_processed, metrics.cycle_completed, metrics.reactivated_count,
                       metrics.misses_incremented_count, metrics.deactivated_count,
                       null::integer as created_count, null::integer as updated_count, null::integer as unchanged_count,
                       coalesce(metrics.failures, base.failures, '[]'::jsonb)::text as failures, 0 as source_rank
                from inventory_sync_runs base
                left join inventory_execution_run_metrics metrics on metrics.run_id = base.run_id
                union all
                select run_id, 'copart-excel' as provider, 'copart' as platform, 'excel-snapshot' as scope, status,
                       downloaded_at as started_at, finished_at, observed_count as observed, 0 as requests,
                       accepted_count as loaded_count, marked_count, discarded_count, quarantined_count, error_count,
                       null::integer as pages_processed, is_complete as cycle_completed, null::integer as reactivated_count,
                       null::integer as misses_incremented_count, null::integer as deactivated_count,
                       created_count, updated_count, unchanged_count,
                       failures::text as failures, 1 as source_rank
                from copart_snapshot_manifests
            )
            select run_id,
                   (array_agg(provider order by source_rank desc, finished_at desc nulls last))[1] as provider,
                   (array_agg(platform order by source_rank desc, finished_at desc nulls last))[1] as platform,
                   (array_agg(scope order by source_rank desc, finished_at desc nulls last))[1] as scope,
                   (array_agg(status order by finished_at desc nulls last, source_rank desc))[1] as status,
                   min(started_at) as started_at, max(finished_at) as finished_at, max(observed) as observed,
                   max(requests) as requests, max(loaded_count) as loaded_count, max(marked_count) as marked_count,
                   max(discarded_count) as discarded_count, max(quarantined_count) as quarantined_count,
                   max(error_count) as error_count, max(pages_processed) as pages_processed,
                   bool_or(cycle_completed) filter (where cycle_completed is not null) as cycle_completed,
                   max(reactivated_count) as reactivated_count, max(misses_incremented_count) as misses_incremented_count,
                   max(deactivated_count) as deactivated_count,
                   max(created_count) as created_count, max(updated_count) as updated_count,
                   max(unchanged_count) as unchanged_count,
                   (array_agg(failures order by length(failures) desc, source_rank desc))[1] as failures
            from raw_history
            group by run_id
            """;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        long total;
        await using (var count = connection.CreateCommand())
        {
            count.CommandTimeout = _persistence.CommandTimeoutSeconds;
            count.CommandText = $"select count(*) from ({runs}) history where (@platform = '' or platform = @platform) and (@status = '' or status = @status);";
            AddParameter(count, "platform", platform);
            AddParameter(count, "status", status);
            total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }
        var results = new List<InventoryExecutionSummary>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = $"""
                select history.*,
                       case
                           when history.provider = 'copart-excel'
                                and copart_manifest.status = 'succeeded'
                                and copart_manifest.is_complete = true
                           then copart_manifest.created_count
                           when history.provider <> 'copart-excel' and events.event_count > 0
                           then events.created_count
                           else null
                       end as created_count,
                       case
                           when history.provider = 'copart-excel'
                                and copart_manifest.status = 'succeeded'
                                and copart_manifest.is_complete = true
                           then copart_manifest.updated_count
                           when history.provider <> 'copart-excel' and events.event_count > 0
                           then events.updated_count
                           else null
                       end as updated_count,
                       case
                           when history.provider = 'copart-excel'
                                and copart_manifest.status = 'succeeded'
                                and copart_manifest.is_complete = true
                           then copart_manifest.unchanged_count
                           when history.provider <> 'copart-excel' and events.event_count > 0
                           then events.unchanged_count
                           else null
                       end as unchanged_count
                from ({runs}) history
                left join lateral (
                    select count(*)::int as event_count,
                           count(*) filter (where action = 'created')::int as created_count,
                           count(*) filter (where action = 'updated')::int as updated_count,
                           count(*) filter (where action = 'unchanged')::int as unchanged_count
                    from inventory_sync_run_events where run_id = history.run_id
                ) events on true
                left join lateral (
                    select manifest.created_count,
                           manifest.updated_count,
                           manifest.unchanged_count,
                           manifest.status,
                           manifest.is_complete
                    from copart_snapshot_manifests manifest
                    where manifest.run_id = history.run_id
                    order by manifest.finished_at desc nulls last,
                             manifest.downloaded_at desc
                    limit 1
                ) copart_manifest on history.provider = 'copart-excel'
                where (@platform = '' or history.platform = @platform) and (@status = '' or history.status = @status)
                order by history.started_at desc
                limit @limit offset @offset;
                """;
            AddParameter(command, "platform", platform);
            AddParameter(command, "status", status);
            AddParameter(command, "limit", pageSize);
            AddParameter(command, "offset", (page - 1) * pageSize);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                results.Add(new InventoryExecutionSummary(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4),
                    reader.GetFieldValue<DateTimeOffset>(5), ReadNullableDateTimeOffset(reader, 6), reader.GetInt32(7), reader.GetInt32(8),
                    ReadNullableInt32(reader, 9), ReadNullableInt32(reader, 23), ReadNullableInt32(reader, 24), ReadNullableInt32(reader, 25),
                    ReadNullableInt32(reader, 10), ReadNullableInt32(reader, 11), ReadNullableInt32(reader, 12), ReadNullableInt32(reader, 13),
                    ReadNullableInt32(reader, 16), ReadNullableInt32(reader, 17), ReadNullableInt32(reader, 18), ReadNullableInt32(reader, 14),
                    reader.IsDBNull(15) ? null : reader.GetBoolean(15), ReadStringArray(reader, 22)));
        }
        return new InventoryExecutionHistoryPage(page, pageSize, total, Math.Max(1, (int)Math.Ceiling(total / (double)pageSize)), results);
    }

    public async Task<InventoryExecutionEventPage> GetExecutionEventsAsync(Guid runId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureAuditSchemaAsync(cancellationToken);
        var safePage = Math.Max(1, page);
        var safePageSize = Math.Clamp(pageSize, 1, 100);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var count = connection.CreateCommand();
        count.CommandTimeout = _persistence.CommandTimeoutSeconds;
        count.CommandText = "select count(*) from inventory_sync_run_events where run_id = @run_id;";
        AddParameter(count, "run_id", runId);
        var total = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select occurred_at, platform, lot_number, vin_masked, action, changed_fields::text, rule_codes::text
            from inventory_sync_run_events where run_id = @run_id
            order by occurred_at desc, id desc limit @limit offset @offset;
            """;
        AddParameter(command, "run_id", runId);
        AddParameter(command, "limit", safePageSize);
        AddParameter(command, "offset", (safePage - 1) * safePageSize);
        var results = new List<InventoryExecutionEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(new InventoryExecutionEvent(reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), ReadNullableString(reader, 2), ReadNullableString(reader, 3), reader.GetString(4), ReadStringArray(reader, 5), ReadStringArray(reader, 6)));
        return new InventoryExecutionEventPage(safePage, safePageSize, total, Math.Max(1, (int)Math.Ceiling(total / (double)safePageSize)), results);
    }

    public async Task<InventorySyncLease> TryAcquireLeaseAsync(string leaseName, Guid ownerRunId, DateTimeOffset acquiredAt, TimeSpan duration, CancellationToken cancellationToken)
    {
        await EnsureNationalSyncSchemaAsync(cancellationToken);
        var expiresAt = acquiredAt.Add(duration);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            insert into inventory_sync_leases (lease_name, owner_run_id, expires_at, updated_at)
            values (@lease_name, @owner_run_id, @expires_at, now())
            on conflict (lease_name) do update set
                owner_run_id = excluded.owner_run_id,
                expires_at = excluded.expires_at,
                updated_at = now()
            where inventory_sync_leases.expires_at <= @acquired_at
               or inventory_sync_leases.owner_run_id = @owner_run_id
            returning owner_run_id, expires_at;
            """;
        AddParameter(command, "lease_name", leaseName);
        AddParameter(command, "owner_run_id", ownerRunId);
        AddParameter(command, "acquired_at", acquiredAt);
        AddParameter(command, "expires_at", expiresAt);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (await reader.ReadAsync(cancellationToken))
            return new InventorySyncLease(true, reader.GetFieldValue<DateTimeOffset>(1), reader.GetGuid(0), null);

        await reader.CloseAsync();
        await using var existing = connection.CreateCommand();
        existing.CommandTimeout = _persistence.CommandTimeoutSeconds;
        existing.CommandText = "select owner_run_id, expires_at from inventory_sync_leases where lease_name = @lease_name;";
        AddParameter(existing, "lease_name", leaseName);
        await using var existingReader = await existing.ExecuteReaderAsync(cancellationToken);
        if (await existingReader.ReadAsync(cancellationToken))
            return new InventorySyncLease(false, existingReader.GetFieldValue<DateTimeOffset>(1), existingReader.GetGuid(0), "lease-active");
        return new InventorySyncLease(false, null, null, "lease-unavailable");
    }

    public async Task ReleaseLeaseAsync(string leaseName, Guid ownerRunId, DateTimeOffset releasedAt, CancellationToken cancellationToken)
    {
        await EnsureNationalSyncSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = "delete from inventory_sync_leases where lease_name = @lease_name and owner_run_id = @owner_run_id;";
        AddParameter(command, "lease_name", leaseName);
        AddParameter(command, "owner_run_id", ownerRunId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<NationalSyncCheckpoint> GetNationalSyncCheckpointAsync(string streamName, CancellationToken cancellationToken)
    {
        await EnsureNationalSyncSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select cycle_id, cursor, pages_completed, lots_observed, cycle_completed, initial_backfill_completed, updated_at
            from iaai_national_sync_state
            where stream_name = @stream_name;
            """;
        AddParameter(command, "stream_name", streamName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new NationalSyncCheckpoint(streamName, null, null, 0, 0, true, false, null);
        return new NationalSyncCheckpoint(
            streamName,
            reader.IsDBNull(0) ? null : reader.GetGuid(0),
            ReadNullableString(reader, 1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            reader.GetBoolean(4),
            reader.GetBoolean(5),
            reader.GetFieldValue<DateTimeOffset>(6));
    }

    public async Task<NationalSyncOperationalStatus> GetNationalSyncOperationalStatusAsync(string streamName, CancellationToken cancellationToken)
    {
        var checkpoint = await GetNationalSyncCheckpointAsync(streamName, cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        Guid? runId = null;
        DateTimeOffset? startedAt = null;
        DateTimeOffset? finishedAt = null;
        string? status = null;
        int? observed = null;
        int? requests = null;
        IReadOnlyList<string> failures = [];

        await using (var run = connection.CreateCommand())
        {
            run.CommandTimeout = _persistence.CommandTimeoutSeconds;
            run.CommandText = """
                select run_id, started_at, finished_at, status, vehicles_observed, requests_issued, failures::text
                from inventory_sync_runs
                where provider = 'apibara' and platform_scope = 'iaai' and state_scope = 'national-rotating'
                order by started_at desc
                limit 1;
                """;
            await using var reader = await run.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                runId = reader.GetGuid(0);
                startedAt = reader.GetFieldValue<DateTimeOffset>(1);
                finishedAt = ReadNullableDateTimeOffset(reader, 2);
                status = reader.GetString(3);
                observed = reader.GetInt32(4);
                requests = reader.GetInt32(5);
                failures = ReadStringArray(reader, 6);
            }
        }

        DateTimeOffset? leaseExpiresAt = null;
        await using (var lease = connection.CreateCommand())
        {
            lease.CommandTimeout = _persistence.CommandTimeoutSeconds;
            lease.CommandText = "select expires_at from inventory_sync_leases where lease_name = 'iaai-national-sync' and expires_at > now();";
            var value = await lease.ExecuteScalarAsync(cancellationToken);
            if (value is DateTimeOffset expiresAt) leaseExpiresAt = expiresAt;
        }

        return new NationalSyncOperationalStatus(checkpoint, runId, startedAt, finishedAt, status, observed, requests, failures, leaseExpiresAt is not null, leaseExpiresAt);
    }

    public async Task PersistNationalSyncBatchAsync(NationalSyncBatch batch, CancellationToken cancellationToken)
    {
        await EnsureNationalSyncSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var observed = batch.EligibleLotKeys.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (observed.Length > 0)
        {
            await using var observations = connection.CreateCommand();
            observations.Transaction = transaction;
            observations.CommandTimeout = _persistence.CommandTimeoutSeconds;
            observations.CommandText = """
                insert into iaai_national_cycle_observations (cycle_id, lot_key, observed_at)
                select @cycle_id, unnest(@lot_keys), @observed_at
                on conflict (cycle_id, lot_key) do update set observed_at = excluded.observed_at;
                """;
            AddParameter(observations, "cycle_id", batch.CycleId);
            AddParameter(observations, "lot_keys", observed);
            AddParameter(observations, "observed_at", batch.ObservedAt);
            await observations.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var checkpoint = connection.CreateCommand();
        checkpoint.Transaction = transaction;
        checkpoint.CommandTimeout = _persistence.CommandTimeoutSeconds;
        checkpoint.CommandText = """
            insert into iaai_national_sync_state (
                stream_name, cycle_id, cursor, pages_completed, lots_observed, cycle_completed, initial_backfill_completed, updated_at)
            values (@stream_name, @cycle_id, @cursor, @pages_completed, @lots_observed, @cycle_completed, @initial_backfill_completed, @updated_at)
            on conflict (stream_name) do update set
                cycle_id = excluded.cycle_id,
                cursor = excluded.cursor,
                pages_completed = excluded.pages_completed,
                lots_observed = excluded.lots_observed,
                cycle_completed = excluded.cycle_completed,
                initial_backfill_completed = excluded.initial_backfill_completed,
                updated_at = excluded.updated_at;
            """;
        AddParameter(checkpoint, "stream_name", batch.StreamName);
        AddParameter(checkpoint, "cycle_id", batch.CycleId);
        AddParameter(checkpoint, "cursor", batch.NextCursor);
        AddParameter(checkpoint, "pages_completed", batch.PagesCompleted);
        AddParameter(checkpoint, "lots_observed", batch.LotsObserved);
        AddParameter(checkpoint, "cycle_completed", batch.CycleCompleted);
        AddParameter(checkpoint, "initial_backfill_completed", batch.InitialBackfillCompleted);
        AddParameter(checkpoint, "updated_at", batch.ObservedAt);
        await checkpoint.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<InventoryReconciliationResult> CompleteNationalSyncCycleAsync(string streamName, Guid cycleId, DateTimeOffset completedAt, CancellationToken cancellationToken, Guid? runId = null)
    {
        await EnsureNationalSyncSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var observations = connection.CreateCommand();
        observations.CommandTimeout = _persistence.CommandTimeoutSeconds;
        observations.CommandText = "select lot_key from iaai_national_cycle_observations where cycle_id = @cycle_id;";
        AddParameter(observations, "cycle_id", cycleId);
        var observed = new List<string>();
        await using (var reader = await observations.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) observed.Add(reader.GetString(0));
        }

        var reconciliation = await ReconcileSourceAsync("iaai", observed, true, completedAt, cancellationToken, runId);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var state = connection.CreateCommand())
        {
            state.Transaction = transaction;
            state.CommandTimeout = _persistence.CommandTimeoutSeconds;
            state.CommandText = """
                update iaai_national_sync_state
                set cursor = null, pages_completed = 0, lots_observed = 0, cycle_completed = true, updated_at = @completed_at
                where stream_name = @stream_name and cycle_id = @cycle_id;
                """;
            AddParameter(state, "stream_name", streamName);
            AddParameter(state, "cycle_id", cycleId);
            AddParameter(state, "completed_at", completedAt);
            await state.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var cleanup = connection.CreateCommand())
        {
            cleanup.Transaction = transaction;
            cleanup.CommandTimeout = _persistence.CommandTimeoutSeconds;
            cleanup.CommandText = "delete from iaai_national_cycle_observations where cycle_id = @cycle_id;";
            AddParameter(cleanup, "cycle_id", cycleId);
            await cleanup.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return reconciliation;
    }

    public async Task<CopartSnapshotRegistration> TryRegisterCopartSnapshotAsync(CopartSnapshotReceipt receipt, decimal minimumRowCountRatio, int baselineSnapshotCount, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
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
            on conflict (sha256) do nothing
            returning run_id;
            """;
        AddParameter(insert, "sha256", receipt.Sha256);
        AddParameter(insert, "file_name", receipt.FileName);
        AddParameter(insert, "downloaded_at", receipt.DownloadedAt);
        AddParameter(insert, "file_size_bytes", receipt.FileSizeBytes);
        AddParameter(insert, "row_count", receipt.RowCount);
        AddParameter(insert, "processing_batch_size", receipt.ProcessingBatchSize);
        AddParameter(insert, "run_id", runId);
        var inserted = await insert.ExecuteScalarAsync(cancellationToken);
        return inserted is null
            ? new CopartSnapshotRegistration(false, true, null, median, "F02: Copart snapshot hash was already processed.")
            : new CopartSnapshotRegistration(true, false, runId, median, null);
    }

    public async Task CompleteCopartSnapshotAsync(Guid runId, CopartSnapshotCompletion completion, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
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
        await RefreshSearchProjectionStatisticsIfReadyAsync(cancellationToken);
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
            select n.nspname, c.relname, c.relkind, c.reltuples::bigint, pg_get_userbyid(c.relowner)
            from pg_catalog.pg_class c
            inner join pg_catalog.pg_namespace n on n.oid = c.relnamespace
            where c.relname in ('auction_lots', 'auction_lot_versions', 'inventory_sync_runs', 'provider_usage_snapshots')
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

        return JsonSerializer.Serialize(new
        {
            Database = database,
            DatabaseUser = databaseUser,
            SearchPath = searchPath,
            Relations = relationList
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

    public async Task<InventorySearchPage> SearchAsync(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        await EnsureScoringSchemaAsync(cancellationToken);
        if (await IsSearchProjectionReadyAsync(cancellationToken))
            return await SearchProjectionAsync(request, cancellationToken);
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var offset = checked((page - 1) * pageSize);
        const string source = """
            from (
                select distinct on (versions.lot_key)
                    versions.lot_key, versions.observed_at, versions.payload,
                    lots.platform, lots.lot_number, lots.vin, lots.title, lots.year, lots.make, lots.model,
                    lots.vehicle_type, lots.damage, lots.auction_state, lots.auction_at, lots.location_display,
                    lots.location_state, lots.odometer, lots.current_bid_usd, lots.buy_now_usd
                from auction_lot_versions versions
                join auction_lots lots on lots.lot_key = versions.lot_key
                order by versions.lot_key, versions.observed_at desc
            ) latest
            left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = latest.lot_key
            left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
            """;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var where = new List<string> { "coalesce(lifecycle.is_active, true)" };
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
            AddSearchFilters(countCommand, request, where);
            countCommand.CommandText = $"select count(*)::int {source} where {string.Join(" and ", where)};";
            var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

            var itemWhere = new List<string> { "coalesce(lifecycle.is_active, true)" };
            await using var itemsCommand = connection.CreateCommand();
            itemsCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
            AddSearchFilters(itemsCommand, request, itemWhere);
            itemsCommand.CommandText = $"""
                select latest.lot_key, latest.observed_at, latest.payload::text
                {source}
                where {string.Join(" and ", itemWhere)}
                order by {GetSearchOrdering(request.Sort)}, latest.lot_key asc
                limit @limit offset @offset;
                """;
            AddParameter(itemsCommand, "limit", pageSize);
            AddParameter(itemsCommand, "offset", offset);
            var items = await ReadStoredSnapshotsAsync(itemsCommand, cancellationToken);
            var generatedAt = items.Count == 0 ? DateTimeOffset.UtcNow : items.Max(snapshot => snapshot.ObservedAt);
            return new InventorySearchPage(page, pageSize, total, generatedAt, items);
        }
    }

    public async Task<InventorySearchSummary> GetInventorySearchSummaryAsync(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        if (await IsSearchProjectionReadyAsync(cancellationToken))
        {
            // The initial portal load has no dependent make filter. Read the
            // snapshot statistics produced by the projection rebuild instead
            // of repeating one count plus ~18 GROUP BY scans on every request.
            var cachedSummary = await GetCachedProjectionSummaryAsync(request, cancellationToken);
            if (cachedSummary is not null)
                return cachedSummary;
            return await GetProjectionSummaryAsync(request, cancellationToken);
        }
        const string source = """
            from (
                select distinct on (versions.lot_key)
                    versions.lot_key, versions.observed_at, versions.payload,
                    lots.platform, lots.lot_number, lots.vin, lots.title, lots.year, lots.make, lots.model,
                    lots.vehicle_type, lots.damage, lots.auction_state, lots.auction_at, lots.location_display,
                    lots.location_state, lots.odometer, lots.current_bid_usd, lots.buy_now_usd
                from auction_lot_versions versions
                join auction_lots lots on lots.lot_key = versions.lot_key
                order by versions.lot_key, versions.observed_at desc
            ) latest
            left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = latest.lot_key
            left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
            """;
        const string active = "coalesce(lifecycle.is_active, true)";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var summaryCommand = connection.CreateCommand();
        summaryCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
        var summaryWhere = new List<string> { active };
        AddSearchFilters(summaryCommand, request, summaryWhere);
        summaryCommand.CommandText = $"select count(*)::int, max(latest.observed_at) {source} where {string.Join(" and ", summaryWhere)};";
        var total = 0;
        DateTimeOffset? generatedAt = null;
        await using (var summaryReader = await summaryCommand.ExecuteReaderAsync(cancellationToken))
        {
            if (await summaryReader.ReadAsync(cancellationToken))
            {
                total = summaryReader.GetInt32(0);
                generatedAt = summaryReader.IsDBNull(1) ? null : summaryReader.GetFieldValue<DateTimeOffset>(1);
            }
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["platforms"] = "latest.platform",
            ["makes"] = "latest.make",
            ["models"] = "latest.model",
            ["vehicleTypes"] = "latest.vehicle_type",
            ["titles"] = SqlTitleCategoryExpression("latest"),
            ["states"] = "latest.location_state",
            ["facilities"] = "latest.location_display",
            ["primaryDamages"] = "latest.damage",
            ["secondaryDamages"] = "latest.payload #>> '{Condition,SecondaryDamage}'",
            ["sellerTypes"] = SqlSellerTypeExpression("latest"),
            ["engineLayouts"] = "latest.payload #>> '{VehicleSpecs,Engine,Layout}'",
            ["cylinders"] = "latest.payload #>> '{Details,VehicleDescription,Cylinders}'",
            ["transmissions"] = "latest.payload #>> '{Transmission}'",
            ["fuels"] = "latest.payload #>> '{FuelType}'",
            ["drives"] = "latest.payload #>> '{DriveType}'",
            ["bodyStyles"] = "coalesce(latest.payload #>> '{VehicleSpecs,BodyStyle}', latest.payload #>> '{Details,VehicleDescription,BodyStyle}')",
            ["colors"] = "latest.payload #>> '{Color}'",
            ["lossTypes"] = "latest.payload #>> '{Condition,Loss}'",
            ["startCodes"] = "coalesce(latest.payload #>> '{Condition,RunCondition,Value}', latest.payload #>> '{Condition,RunCondition,Label}')",
            ["runConditions"] = PublicRunConditionSql("latest"),
        };
        var facets = new Dictionary<string, IReadOnlyList<InventoryFacetValue>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, expression) in fields)
        {
            await using var facetCommand = connection.CreateCommand();
            facetCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
            var facetWhere = new List<string> { active };
            AddSearchFilters(facetCommand, request, facetWhere);
            facetCommand.CommandText = $"""
                select value, count(*)::int
                from (
                    select nullif(btrim({expression}), '') as value
                    {source}
                    where {string.Join(" and ", facetWhere)}
                ) values_to_count
                where value is not null
                group by value
                order by count(*) desc, value asc
                limit 250;
                """;
            await using var reader = await facetCommand.ExecuteReaderAsync(cancellationToken);
            var values = new List<InventoryFacetValue>();
            while (await reader.ReadAsync(cancellationToken))
                values.Add(new InventoryFacetValue(reader.GetString(0), reader.GetInt32(1)));
            facets[key] = values;
        }

        return new InventorySearchSummary(total, generatedAt ?? DateTimeOffset.UtcNow, facets);
    }

    private async Task<bool> IsSearchProjectionReadyAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var checkedAtTicks = Interlocked.Read(ref _projectionReadyCheckedAtTicks);
        if (checkedAtTicks > 0 && now.UtcTicks - checkedAtTicks < TimeSpan.FromSeconds(5).Ticks)
            return _projectionReadyCache;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = "select is_ready from inventory_search_projection_state where projection_name = 'inventory-current-v1';";
        _projectionReadyCache = await command.ExecuteScalarAsync(cancellationToken) is true;
        Interlocked.Exchange(ref _projectionReadyCheckedAtTicks, now.UtcTicks);
        return _projectionReadyCache;
    }

    private async Task<InventorySearchPage> SearchProjectionAsync(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        if (IsPreGradeBaselineSearch(request))
            return await SearchProjectionPreGradeBaselineAsync(request, cancellationToken);

        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var offset = checked((page - 1) * pageSize);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var total = await GetProjectionTotalAsync(connection, request, cancellationToken);
        var where = new List<string> { "latest.is_active" };
        var itemWhere = new List<string> { "latest.is_active" };
        await using var itemsCommand = connection.CreateCommand();
        itemsCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
        AddProjectionFilters(itemsCommand, request, itemWhere);
        itemsCommand.CommandText = $"""
            select {ProjectionSnapshotColumns}
            from inventory_search_current latest
            left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
            where {string.Join(" and ", itemWhere)}
            order by {GetProjectionOrdering(request.Sort)}, latest.lot_key asc
            limit @limit offset @offset;
            """;
        AddParameter(itemsCommand, "limit", pageSize);
        AddParameter(itemsCommand, "offset", offset);
        var items = await ReadStoredSnapshotsAsync(itemsCommand, cancellationToken);
        var generatedAt = items.Count == 0 ? DateTimeOffset.UtcNow : items.Max(snapshot => snapshot.ObservedAt);
        return new InventorySearchPage(page, pageSize, total, generatedAt, items);
    }

    private const string ProjectionSnapshotColumns = """
        latest.lot_key, latest.observed_at, latest.payload::text,
        latest.platform, latest.lot_number, latest.vin,
        score.status as score_status, score.pre_grade as score_pre_grade, score.buy_score as score_buy_score,
        score.max_points_evaluable as score_max_points_evaluable,
        score.coverage_percent as score_coverage_percent, score.confidence_percent as score_confidence_percent,
        score.category as score_category, score.policy_version as score_policy_version, score.scored_at as score_scored_at
        """;

    private const string ProjectionSnapshotColumnsWithoutScore = """
        latest.lot_key, latest.observed_at, latest.payload::text,
        latest.platform, latest.lot_number, latest.vin,
        null::text as score_status, null::numeric as score_pre_grade, null::numeric as score_buy_score,
        null::numeric as score_max_points_evaluable, null::numeric as score_coverage_percent,
        null::numeric as score_confidence_percent, null::text as score_category,
        null::text as score_policy_version, null::timestamptz as score_scored_at
        """;

    private async Task<InventorySearchPage> SearchProjectionPreGradeBaselineAsync(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var offset = checked((page - 1) * pageSize);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var total = await GetProjectionTotalAsync(connection, request, cancellationToken);
        var platformClause = string.IsNullOrWhiteSpace(request.Platform) ? string.Empty : " and latest.platform = @platform";
        var visibilityClause = ProjectionVisibilityClause(request);

        await using var scoredCountCommand = connection.CreateCommand();
        scoredCountCommand.CommandTimeout = Math.Min(_persistence.CommandTimeoutSeconds, 8);
        scoredCountCommand.CommandText = $"""
            select count(*)::int
            from inventory_vehicle_score_current score
            join inventory_search_current latest on latest.lot_key = score.lot_key
            where latest.is_active and score.pre_grade is not null{platformClause}{visibilityClause};
            """;
        AddPlatformParameter(scoredCountCommand, request);
        var scoredTotal = Convert.ToInt32(await scoredCountCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (scoredTotal == 0)
            return await SearchProjectionWithoutScoresAsync(connection, request, page, pageSize, offset, total, cancellationToken);

        var items = new List<StoredVehicleSnapshot>(pageSize);

        if (offset < scoredTotal)
        {
            await using var scoredItemsCommand = connection.CreateCommand();
            scoredItemsCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
            scoredItemsCommand.CommandText = $"""
                select {ProjectionSnapshotColumns}
                from inventory_vehicle_score_current score
                join inventory_search_current latest on latest.lot_key = score.lot_key
                where latest.is_active and score.pre_grade is not null{platformClause}{visibilityClause}
                order by score.pre_grade desc, latest.lot_key asc
                limit @limit offset @offset;
                """;
            AddPlatformParameter(scoredItemsCommand, request);
            AddParameter(scoredItemsCommand, "limit", pageSize);
            AddParameter(scoredItemsCommand, "offset", offset);
            items.AddRange(await ReadStoredSnapshotsAsync(scoredItemsCommand, cancellationToken));
        }

        if (items.Count < pageSize && offset + items.Count < total)
        {
            var unscoredOffset = Math.Max(0, offset - scoredTotal);
            await using var unscoredItemsCommand = connection.CreateCommand();
            unscoredItemsCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
            unscoredItemsCommand.CommandText = $"""
                select {ProjectionSnapshotColumns}
                from inventory_search_current latest
                left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
                where latest.is_active and score.pre_grade is null{platformClause}{visibilityClause}
                order by latest.lot_key asc
                limit @limit offset @offset;
                """;
            AddPlatformParameter(unscoredItemsCommand, request);
            AddParameter(unscoredItemsCommand, "limit", pageSize - items.Count);
            AddParameter(unscoredItemsCommand, "offset", unscoredOffset);
            items.AddRange(await ReadStoredSnapshotsAsync(unscoredItemsCommand, cancellationToken));
        }

        var generatedAt = items.Count == 0 ? DateTimeOffset.UtcNow : items.Max(snapshot => snapshot.ObservedAt);
        return new InventorySearchPage(page, pageSize, total, generatedAt, items);
    }

    private async Task<InventorySearchPage> SearchProjectionWithoutScoresAsync(
        NpgsqlConnection connection,
        InventorySearchRequest request,
        int page,
        int pageSize,
        int offset,
        int total,
        CancellationToken cancellationToken)
    {
        var platformClause = string.IsNullOrWhiteSpace(request.Platform) ? string.Empty : " and latest.platform = @platform";
        var visibilityClause = ProjectionVisibilityClause(request);
        await using var itemsCommand = connection.CreateCommand();
        itemsCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
        itemsCommand.CommandText = $"""
            select {ProjectionSnapshotColumnsWithoutScore}
            from inventory_search_current latest
            where latest.is_active{platformClause}{visibilityClause}
            order by latest.auction_at asc nulls last, latest.lot_key asc
            limit @limit offset @offset;
            """;
        AddPlatformParameter(itemsCommand, request);
        AddParameter(itemsCommand, "limit", pageSize);
        AddParameter(itemsCommand, "offset", offset);
        var items = await ReadStoredSnapshotsAsync(itemsCommand, cancellationToken);
        var generatedAt = items.Count == 0 ? DateTimeOffset.UtcNow : items.Max(snapshot => snapshot.ObservedAt);
        return new InventorySearchPage(page, pageSize, total, generatedAt, items);
    }

    private async Task<int> GetProjectionTotalAsync(NpgsqlConnection connection, InventorySearchRequest request, CancellationToken cancellationToken)
    {
        int? total = null;
        if (IsDefaultVisibleSearch(request))
        {
            await using var cachedCountCommand = connection.CreateCommand();
            cachedCountCommand.CommandTimeout = Math.Min(_persistence.CommandTimeoutSeconds, 5);
            cachedCountCommand.CommandText = "select row_count, visible_row_count from inventory_search_projection_state where projection_name = 'inventory-current-v1' and is_ready and facets_refreshed_at is not null;";
            await using var reader = await cachedCountCommand.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var rowCount = reader.GetInt64(0);
                var visibleRowCount = reader.GetInt64(1);
                // Older ready projections can have a persisted default of zero for
                // visible_row_count. Do not let that stale cache report zero while
                // the projection itself still contains active vehicles; fall back
                // to the exact filtered count until statistics are refreshed.
                if (!request.ExcludeSpecialTitles || rowCount == 0 || visibleRowCount > 0)
                    total = (int)Math.Min(int.MaxValue, request.ExcludeSpecialTitles ? visibleRowCount : rowCount);
            }
        }
        else if (IsPlatformOnlyVisibleSearch(request) && !request.ExcludeSpecialTitles)
        {
            await using var cachedPlatformCountCommand = connection.CreateCommand();
            cachedPlatformCountCommand.CommandTimeout = Math.Min(_persistence.CommandTimeoutSeconds, 5);
            cachedPlatformCountCommand.CommandText = """
                select facets.vehicle_count
                from inventory_search_facet_counts facets
                join inventory_search_projection_state state on state.projection_name = 'inventory-current-v1'
                where state.is_ready and state.facets_refreshed_at is not null
                  and facets.facet_key = 'platforms'
                  and lower(facets.facet_value) = @platform;
                """;
            AddParameter(cachedPlatformCountCommand, "platform", request.Platform!.Trim().ToLowerInvariant());
            var cachedCount = await cachedPlatformCountCommand.ExecuteScalarAsync(cancellationToken);
            if (cachedCount is not null && cachedCount != DBNull.Value)
                total = Convert.ToInt32(cachedCount, CultureInfo.InvariantCulture);
        }

        if (!total.HasValue && !IsKnownEmptyProjection(request))
        {
            var where = new List<string> { "latest.is_active" };
            await using var countCommand = connection.CreateCommand();
            countCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
            AddProjectionFilters(countCommand, request, where);
            var scoreJoin = RequiresProjectionScoreJoin(request)
                ? " left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key"
                : string.Empty;
            countCommand.CommandText = $"select count(*)::int from inventory_search_current latest{scoreJoin} where {string.Join(" and ", where)};";
            total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        }

        return total ?? 0;
    }

    private static void AddPlatformParameter(NpgsqlCommand command, InventorySearchRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.Platform))
            AddParameter(command, "platform", request.Platform.Trim().ToLowerInvariant());
    }

    private async Task<InventorySearchSummary?> GetCachedProjectionSummaryAsync(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        if (request.Makes is { Count: > 0 })
            return null;

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Min(_persistence.CommandTimeoutSeconds, 5);
        command.CommandText = """
            select row_count, generated_at
            from inventory_search_projection_state
            where projection_name = 'inventory-current-v1'
              and is_ready
              and facets_refreshed_at is not null;
            select facet_key, facet_value, vehicle_count
            from inventory_search_facet_counts
            order by facet_key, vehicle_count desc, facet_value asc;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return null;

        var total = reader.GetInt64(0);
        var generatedAt = reader.IsDBNull(1) ? DateTimeOffset.UtcNow : reader.GetFieldValue<DateTimeOffset>(1);
        if (!await reader.NextResultAsync(cancellationToken))
            return null;

        var facets = new Dictionary<string, List<InventoryFacetValue>>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = reader.GetString(0);
            if (!facets.TryGetValue(key, out var values))
            {
                values = [];
                facets[key] = values;
            }
            values.Add(new InventoryFacetValue(reader.GetString(1), reader.GetInt32(2)));
        }

        if (facets.Count == 0)
            return null;

        return new InventorySearchSummary(
            (int)Math.Min(int.MaxValue, total),
            generatedAt,
            facets.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<InventoryFacetValue>)pair.Value, StringComparer.OrdinalIgnoreCase));
    }

    private async Task<InventorySearchSummary> GetProjectionSummaryAsync(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        var makes = request.Makes?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var facets = new Dictionary<string, IReadOnlyList<InventoryFacetValue>>(StringComparer.OrdinalIgnoreCase);
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["platforms"] = "current.platform", ["makes"] = "current.make", ["models"] = "current.model",
            ["vehicleTypes"] = "current.vehicle_type", ["titles"] = "current.title_type", ["states"] = "current.location_state",
            ["facilities"] = "current.location_display", ["primaryDamages"] = "current.primary_damage", ["secondaryDamages"] = "current.secondary_damage",
            ["sellerTypes"] = SqlSellerTypeExpression("current"), ["engineLayouts"] = "current.engine_layout", ["cylinders"] = "current.cylinders",
            ["transmissions"] = "current.transmission", ["fuels"] = "current.fuel_type", ["drives"] = "current.drive_type",
            ["bodyStyles"] = "current.body_style", ["colors"] = "current.color", ["lossTypes"] = "current.loss_type", ["startCodes"] = "current.start_code", ["runConditions"] = PublicRunConditionSql("current")
        };

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var makeClause = makes.Length == 0 ? string.Empty : " and lower(coalesce(current.make, '')) = any(@makes)";
        var source = $"from inventory_search_current current left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = current.lot_key where {ActiveLifecyclePredicate}{makeClause}";
        var total = 0L;
        var generatedAt = DateTimeOffset.UtcNow;
        await using (var status = connection.CreateCommand())
        {
            status.CommandTimeout = _persistence.CommandTimeoutSeconds;
            status.CommandText = $"select count(*)::bigint, max(current.observed_at) {source};";
            if (makes.Length > 0) AddParameter(status, "makes", makes.Select(value => value.ToLowerInvariant()).ToArray());
            await using var reader = await status.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                total = reader.GetInt64(0);
                if (!reader.IsDBNull(1)) generatedAt = reader.GetFieldValue<DateTimeOffset>(1);
            }
        }

        foreach (var (key, expression) in fields)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = $"select nullif(btrim({expression}), '') as value, count(*)::int {source} and nullif(btrim({expression}), '') is not null group by value order by count(*) desc, value asc limit 250;";
            if (makes.Length > 0) AddParameter(command, "makes", makes.Select(value => value.ToLowerInvariant()).ToArray());
            var values = new List<InventoryFacetValue>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) values.Add(new InventoryFacetValue(reader.GetString(0), reader.GetInt32(1)));
            facets[key] = values;
        }

        return new InventorySearchSummary((int)Math.Min(int.MaxValue, total), generatedAt, facets);
    }

    public async Task<SellerTaxonomyAudit> GetSellerTaxonomyAuditAsync(CancellationToken cancellationToken)
    {
        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        const string platformSql = "coalesce(nullif(btrim(current.platform), ''), 'unknown')";
        const string sourceType = "coalesce(nullif(btrim(current.payload #>> '{Seller,Type}'), ''), nullif(btrim(current.payload #>> '{Details,SaleInformation,SellerType}'), ''))";
        const string sourceClass = "nullif(btrim(current.payload #>> '{Seller,Class}'), '')";
        const string sourceTextClass = "nullif(btrim(current.payload #>> '{Seller,TextClass}'), '')";
        const string sellerName = "coalesce(nullif(btrim(current.payload #>> '{Seller,Name}'), ''), nullif(btrim(current.payload #>> '{Details,SaleInformation,Seller}'), ''))";
        const string source = $"from inventory_search_current current left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = current.lot_key where {ActiveLifecyclePredicate}";

        var summaries = new Dictionary<string, (long Active, long ProjectionType, long SourceType, long Name, long NameMissingType)>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = $"""
                select {platformSql} as platform,
                       count(*)::bigint as active_lots,
                       count(*) filter (where nullif(btrim(current.seller_type), '') is not null)::bigint as projection_seller_type_present,
                       count(*) filter (where {sourceType} is not null)::bigint as source_type_present,
                       count(*) filter (where {sellerName} is not null)::bigint as seller_name_present,
                       count(*) filter (where {sellerName} is not null and {sourceType} is null)::bigint as seller_name_present_source_type_missing
                {source}
                group by {platformSql}
                order by platform asc;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                summaries[reader.GetString(0)] = (reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
        }

        async Task<IReadOnlyDictionary<string, IReadOnlyList<InventoryFacetValue>>> ReadFacetAsync(string expression, bool onlyMissingSourceType = false)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            var missingSourceType = onlyMissingSourceType ? $" and {sourceType} is null" : string.Empty;
            command.CommandText = $"""
                select {platformSql} as platform,
                       {expression} as value,
                       count(*)::int as vehicle_count
                {source}
                  and {expression} is not null{missingSourceType}
                group by {platformSql}, {expression}
                order by platform asc, vehicle_count desc, value asc
                limit 200;
                """;
            var values = new Dictionary<string, List<InventoryFacetValue>>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var platform = reader.GetString(0);
                if (!values.TryGetValue(platform, out var items)) values[platform] = items = [];
                items.Add(new InventoryFacetValue(reader.GetString(1), reader.GetInt32(2)));
            }
            return values.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<InventoryFacetValue>)pair.Value, StringComparer.OrdinalIgnoreCase);
        }

        var sourceTypes = await ReadFacetAsync(sourceType);
        var sourceClasses = await ReadFacetAsync(sourceClass);
        var sourceTextClasses = await ReadFacetAsync(sourceTextClass);
        var missingTypeNames = await ReadFacetAsync(sellerName, onlyMissingSourceType: true);
        var platforms = summaries
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => new SellerTaxonomyPlatformAudit(
                pair.Key,
                pair.Value.Active,
                pair.Value.ProjectionType,
                pair.Value.SourceType,
                pair.Value.Name,
                pair.Value.NameMissingType,
                sourceTypes.GetValueOrDefault(pair.Key, []),
                sourceClasses.GetValueOrDefault(pair.Key, []),
                sourceTextClasses.GetValueOrDefault(pair.Key, []),
                missingTypeNames.GetValueOrDefault(pair.Key, [])))
            .ToArray();
        return new SellerTaxonomyAudit(
            platforms.Sum(platform => platform.ActiveLots),
            platforms.Sum(platform => platform.ProjectionSellerTypePresent),
            platforms.Sum(platform => platform.SourceTypePresent),
            platforms.Sum(platform => platform.SellerNamePresent),
            platforms.Sum(platform => platform.SellerNamePresentSourceTypeMissing),
            platforms,
            DateTimeOffset.UtcNow);
    }

    private static bool IsDefaultVisibleSearch(InventorySearchRequest request)
    {
        static bool Empty(IReadOnlyCollection<string>? values) => values is null || values.Count == 0;
        return string.IsNullOrWhiteSpace(request.Query)
            && string.IsNullOrWhiteSpace(request.Platform)
            && Empty(request.Makes) && Empty(request.Models) && Empty(request.VehicleTypes)
            && Empty(request.Titles) && Empty(request.States) && Empty(request.Facilities)
            && Empty(request.PrimaryDamages) && Empty(request.SecondaryDamages) && Empty(request.SellerTypes)
            && Empty(request.EngineLayouts) && Empty(request.Cylinders) && Empty(request.Transmissions)
            && Empty(request.Fuels) && Empty(request.Drives) && Empty(request.BodyStyles) && Empty(request.Colors)
            && Empty(request.LossTypes) && Empty(request.StartCodes) && Empty(request.RunConditions)
            && request.YearFrom is null && request.YearTo is null && request.OdometerFrom is null && request.OdometerTo is null
            && request.PriceFrom is null && request.PriceTo is null && request.BuyNowFrom is null && request.BuyNowTo is null && request.AuctionFrom is null && request.AuctionTo is null
            && request.BuyNowOnly is null && request.WithPhotosOnly is null && request.AuctionStatus is null
            && request.WithBidOnly is null && request.KeyMode is null && request.ProviderEstimateFrom is null && request.ProviderEstimateTo is null
            && request.EngineSizeFrom is null && request.EngineSizeTo is null && request.HorsepowerFrom is null && request.HorsepowerTo is null
            && request.MaxCurrentBid is null && request.PreGradeFrom is null && Empty(request.ScoringStatuses);
    }

    private static bool IsPlatformOnlyVisibleSearch(InventorySearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Platform)) return false;
        return IsDefaultVisibleSearch(request with { Platform = null });
    }

    private static bool IsPreGradeBaselineSearch(InventorySearchRequest request)
    {
        // The baseline path partitions scored and unscored rows, so it cannot
        // preserve a global grading-first plus secondary-sort order.
        return false;
    }

    private static string ProjectionVisibilityClause(InventorySearchRequest request) =>
        request.ExcludeSpecialTitles ? " and not latest.is_special_title" : string.Empty;

    private static bool RequiresProjectionScoreJoin(InventorySearchRequest request) =>
        request.PreGradeFrom.HasValue || request.ScoringStatuses is { Count: > 0 };

    private static bool IsKnownEmptyProjection(InventorySearchRequest request) => request.PageSize <= 0;

    private static string PublicRunConditionSql(string alias) => $"case when upper(replace(replace(replace(coalesce({alias}.start_code, ''), '&', ' AND '), '/', ' AND '), '-', ' ')) like '%RUNS AND DRIVES%' or upper(replace(replace(replace(coalesce({alias}.start_code, ''), '&', ' AND '), '/', ' AND '), '-', ' ')) like '%RUN AND DRIVE%' then 'RUNS_AND_DRIVES' when upper(coalesce({alias}.start_code, '')) like '%START%' then 'STARTS' when upper(coalesce({alias}.start_code, '')) like '%STATIONARY%' then 'STATIONARY' else 'UNVERIFIED' end";
    private static string PublicRunConditionPayloadSql(string alias) => $"case when upper(replace(replace(replace(coalesce({alias}.payload #>> '{{Condition,RunCondition,Value}}', {alias}.payload #>> '{{Condition,RunCondition,Label}}', ''), '&', ' AND '), '/', ' AND '), '-', ' ')) like '%RUNS AND DRIVES%' or upper(replace(replace(replace(coalesce({alias}.payload #>> '{{Condition,RunCondition,Value}}', {alias}.payload #>> '{{Condition,RunCondition,Label}}', ''), '&', ' AND '), '/', ' AND '), '-', ' ')) like '%RUN AND DRIVE%' then 'RUNS_AND_DRIVES' when upper(coalesce({alias}.payload #>> '{{Condition,RunCondition,Value}}', {alias}.payload #>> '{{Condition,RunCondition,Label}}', '')) like '%START%' then 'STARTS' when upper(coalesce({alias}.payload #>> '{{Condition,RunCondition,Value}}', {alias}.payload #>> '{{Condition,RunCondition,Label}}', '')) like '%STATIONARY%' then 'STATIONARY' else 'UNVERIFIED' end";
    private static string SqlSellerTypeExpression(string alias) => SqlSellerTypeTaxonomy($"coalesce(nullif(btrim({alias}.seller_type), ''), nullif(btrim({alias}.payload #>> '{{seller,type}}'), ''), nullif(btrim({alias}.payload #>> '{{Seller,Type}}'), ''), nullif(btrim({alias}.payload #>> '{{seller,text_class}}'), ''), nullif(btrim({alias}.payload #>> '{{seller,textClass}}'), ''), nullif(btrim({alias}.payload #>> '{{Seller,TextClass}}'), ''), nullif(btrim({alias}.payload #>> '{{seller,class}}'), ''), nullif(btrim({alias}.payload #>> '{{Seller,Class}}'), ''))");
    private static string SqlSellerTypePayloadExpression(string alias) => SqlSellerTypeTaxonomy($"coalesce(nullif(btrim({alias}.payload #>> '{{seller,type}}'), ''), nullif(btrim({alias}.payload #>> '{{Seller,Type}}'), ''), nullif(btrim({alias}.payload #>> '{{seller,text_class}}'), ''), nullif(btrim({alias}.payload #>> '{{seller,textClass}}'), ''), nullif(btrim({alias}.payload #>> '{{Seller,TextClass}}'), ''), nullif(btrim({alias}.payload #>> '{{seller,class}}'), ''), nullif(btrim({alias}.payload #>> '{{Seller,Class}}'), ''))");
    private static string SqlSellerTypeTaxonomy(string source) => $"case when {source} is null or lower({source}) = '{SellerTaxonomy.Unclassified}' then '{SellerTaxonomy.Unclassified}' when lower({source}) = '{SellerTaxonomy.Insurance}' or lower({source}) like '%insurance%' or lower({source}) like '%insurer%' or lower({source}) like '%casualty%' then '{SellerTaxonomy.Insurance}' when lower({source}) = '{SellerTaxonomy.Dealer}' or lower({source}) like '%dealer%' or lower({source}) like '%auto group%' or lower({source}) like '%motor group%' then '{SellerTaxonomy.Dealer}' when lower({source}) = '{SellerTaxonomy.RepossessionBank}' or lower({source}) like '%repo%' or lower({source}) like '%bank%' or lower({source}) like '%credit union%' or lower({source}) like '%lender%' then '{SellerTaxonomy.RepossessionBank}' when lower({source}) = '{SellerTaxonomy.Finance}' or lower({source}) like '%finance%' or lower({source}) like '%financial%' or lower({source}) like '%leasing%' then '{SellerTaxonomy.Finance}' when lower({source}) = '{SellerTaxonomy.RentalFleet}' or lower({source}) like '%rental%' or lower({source}) like '%fleet%' then '{SellerTaxonomy.RentalFleet}' when lower({source}) = '{SellerTaxonomy.Government}' or lower({source}) like '%government%' or lower({source}) like '%govt%' or lower({source}) like '%municipal%' or lower({source}) like '%county%' or lower({source}) like '%city%' then '{SellerTaxonomy.Government}' when lower({source}) = '{SellerTaxonomy.Other}' then '{SellerTaxonomy.Other}' when lower({source}) = '{SellerTaxonomy.Unknown}' or lower({source}) in ('unknown', 'unavailable', 'no information', 'no info', 'not reported', 'n/a', 'na') then '{SellerTaxonomy.Unknown}' else '{SellerTaxonomy.Other}' end";

    private static void AddProjectionFilters(NpgsqlCommand command, InventorySearchRequest request, List<string> where)
    {
        static string[] Values(IReadOnlyCollection<string>? values) => values?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        void AddAny(string parameter, IReadOnlyCollection<string>? values, string expression)
        {
            var selected = Values(values);
            if (selected.Length == 0) return;
            where.Add($"lower(coalesce({expression}, '')) = any(@{parameter})");
            AddParameter(command, parameter, selected.Select(value => value.ToLowerInvariant()).ToArray());
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            where.Add("(to_tsvector('simple', latest.search_text) @@ websearch_to_tsquery('simple', @search_query) or latest.lot_number ilike @query_like or latest.vin ilike @query_like)");
            AddParameter(command, "search_query", request.Query.Trim());
            AddParameter(command, "query_like", $"%{request.Query.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Platform)) { where.Add("latest.platform = @platform"); AddParameter(command, "platform", request.Platform.Trim().ToLowerInvariant()); }
        AddAny("makes", request.Makes, "latest.make");
        AddAny("models", request.Models, "latest.model");
        AddAny("vehicle_types", request.VehicleTypes, "latest.vehicle_type");
        if (request.ExcludeSpecialTitles) where.Add("not latest.is_special_title");
        AddAny("titles", request.Titles, "latest.title_type");
        AddAny("states", request.States, "latest.location_state");
        AddAny("facilities", request.Facilities, "latest.location_display");
        AddAny("primary_damages", request.PrimaryDamages, "latest.primary_damage");
        AddAny("secondary_damages", request.SecondaryDamages, "latest.secondary_damage");
        AddAny("seller_types", request.SellerTypes, SqlSellerTypeExpression("latest"));
        AddAny("engine_layouts", request.EngineLayouts, "latest.engine_layout");
        AddAny("cylinders", request.Cylinders, "latest.cylinders");
        AddAny("transmissions", request.Transmissions, "latest.transmission");
        AddAny("fuels", request.Fuels, "latest.fuel_type");
        AddAny("drives", request.Drives, "latest.drive_type");
        AddAny("body_styles", request.BodyStyles, "latest.body_style");
        AddAny("colors", request.Colors, "latest.color");
        AddAny("loss_types", request.LossTypes, "latest.loss_type");
        AddAny("start_codes", request.StartCodes, "latest.start_code");
        AddAny("run_conditions", request.RunConditions, PublicRunConditionSql("latest"));
        if (request.YearFrom.HasValue) { where.Add("latest.year >= @year_from"); AddParameter(command, "year_from", request.YearFrom.Value); }
        if (request.YearTo.HasValue) { where.Add("latest.year <= @year_to"); AddParameter(command, "year_to", request.YearTo.Value); }
        if (request.OdometerFrom.HasValue) { where.Add("latest.odometer >= @odometer_from"); AddParameter(command, "odometer_from", request.OdometerFrom.Value); }
        if (request.OdometerTo.HasValue) { where.Add("latest.odometer <= @odometer_to"); AddParameter(command, "odometer_to", request.OdometerTo.Value); }
        if (request.PriceFrom.HasValue) { where.Add("latest.current_bid_usd >= @price_from"); AddParameter(command, "price_from", request.PriceFrom.Value); }
        if (request.PriceTo.HasValue) { where.Add("latest.current_bid_usd <= @price_to"); AddParameter(command, "price_to", request.PriceTo.Value); }
        if (request.BuyNowOnly == true || request.BuyNowFrom.HasValue || request.BuyNowTo.HasValue) where.Add("latest.buy_now_usd > 0");
        if (request.BuyNowFrom.HasValue) { where.Add("latest.buy_now_usd >= @buy_now_from"); AddParameter(command, "buy_now_from", request.BuyNowFrom.Value); }
        if (request.BuyNowTo.HasValue) { where.Add("latest.buy_now_usd <= @buy_now_to"); AddParameter(command, "buy_now_to", request.BuyNowTo.Value); }
        if (request.MaxCurrentBid.HasValue) { where.Add("(latest.current_bid_usd is null or latest.current_bid_usd <= @max_current_bid)"); AddParameter(command, "max_current_bid", request.MaxCurrentBid.Value); }
        if (request.AuctionFrom.HasValue) { where.Add("latest.auction_at >= @auction_from"); AddParameter(command, "auction_from", request.AuctionFrom.Value); }
        if (request.AuctionTo.HasValue) { where.Add("latest.auction_at <= @auction_to"); AddParameter(command, "auction_to", request.AuctionTo.Value); }
        if (request.WithPhotosOnly == true) where.Add("latest.has_photos");
        if (request.WithBidOnly == true) where.Add("latest.current_bid_usd is not null");
        if (string.Equals(request.KeyMode, "with", StringComparison.OrdinalIgnoreCase)) where.Add("latest.has_key is true");
        if (string.Equals(request.KeyMode, "without", StringComparison.OrdinalIgnoreCase)) where.Add("latest.has_key is false");
        if (request.ProviderEstimateFrom.HasValue) { where.Add("latest.provider_estimate_to >= @provider_estimate_from"); AddParameter(command, "provider_estimate_from", request.ProviderEstimateFrom.Value); }
        if (request.ProviderEstimateTo.HasValue) { where.Add("latest.provider_estimate_from <= @provider_estimate_to"); AddParameter(command, "provider_estimate_to", request.ProviderEstimateTo.Value); }
        if (request.EngineSizeFrom.HasValue) { where.Add("latest.engine_size_liters >= @engine_size_from"); AddParameter(command, "engine_size_from", request.EngineSizeFrom.Value); }
        if (request.EngineSizeTo.HasValue) { where.Add("latest.engine_size_liters <= @engine_size_to"); AddParameter(command, "engine_size_to", request.EngineSizeTo.Value); }
        if (request.HorsepowerFrom.HasValue) { where.Add("latest.horsepower >= @horsepower_from"); AddParameter(command, "horsepower_from", request.HorsepowerFrom.Value); }
        if (request.HorsepowerTo.HasValue) { where.Add("latest.horsepower <= @horsepower_to"); AddParameter(command, "horsepower_to", request.HorsepowerTo.Value); }
        if (request.PreGradeFrom.HasValue) { where.Add("score.pre_grade >= @pre_grade_from"); AddParameter(command, "pre_grade_from", request.PreGradeFrom.Value); }
        AddAny("scoring_statuses", request.ScoringStatuses, "score.status");
        if (string.Equals(request.AuctionStatus, "open", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like any(array['%open%', '%active%'])");
        if (string.Equals(request.AuctionStatus, "live", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like '%live%'");
        if (string.Equals(request.AuctionStatus, "finished", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like any(array['%finished%', '%ended%', '%sold%'])");
    }

    private static string GetProjectionOrdering(string? sort)
    {
        var secondary = sort?.Trim().ToLowerInvariant() switch
        {
            "auction" => "latest.auction_at asc nulls last",
            "auction-desc" => "latest.auction_at desc nulls last",
            "year-asc" => "latest.year asc nulls last",
            "year-desc" => "latest.year desc nulls last",
            "estimate-asc" => "latest.provider_estimate_from asc nulls last",
            "estimate-desc" => "latest.provider_estimate_to desc nulls last",
            "buy-asc" => "latest.buy_now_usd asc nulls last",
            "buy-desc" => "latest.buy_now_usd desc nulls last",
            "bid-asc" => "latest.current_bid_usd asc nulls last",
            "bid-desc" => "latest.current_bid_usd desc nulls last",
            "odometer-asc" => "latest.odometer asc nulls last",
            "odometer-desc" => "latest.odometer desc nulls last",
            _ => "latest.observed_at desc nulls last",
        };
        return $"score.pre_grade desc nulls last, {secondary}";
    }

    public async Task<InventorySearchProjectionStatus> RebuildSearchProjectionAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var titleCategorySql = TitleFacetCategory.BuildSqlCaseExpression("title_normalized.normalized_document", "lower(lots.platform)");
        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var acquireLock = connection.CreateCommand())
        {
            acquireLock.Transaction = transaction;
            acquireLock.CommandTimeout = _persistence.CommandTimeoutSeconds;
            acquireLock.CommandText = "select pg_try_advisory_xact_lock(hashtext('lsc-inventory-search-projection-v1'));";
            if (await acquireLock.ExecuteScalarAsync(cancellationToken) is not true)
                throw new InvalidOperationException("A search projection rebuild is already running.");
        }
        await using (var markBuilding = connection.CreateCommand())
        {
            markBuilding.Transaction = transaction;
            markBuilding.CommandTimeout = _persistence.CommandTimeoutSeconds;
            markBuilding.CommandText = "update inventory_search_projection_state set is_ready = false, updated_at = now() where projection_name = 'inventory-current-v1';";
            await markBuilding.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var rebuild = connection.CreateCommand())
        {
            rebuild.Transaction = transaction;
            rebuild.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 600);
            rebuild.CommandText = """
                insert into inventory_search_current (
                    lot_key, platform, lot_number, vin, title, year, make, model, vehicle_type, title_type,
                    color, fuel_type, transmission, drive_type, body_style, primary_damage, secondary_damage,
                    seller_type, seller_name, seller_classification_confidence, seller_needs_review, seller_classification_evidence, seller_taxonomy_version,
                    engine_layout, cylinders, loss_type, start_code, auction_state, auction_at,
                    lot_status, lot_sub_status, location_display, location_state, facility_id, odometer,
                    current_bid_usd, buy_now_usd, provider_estimate_from, provider_estimate_to, engine_size_liters,
                    horsepower, has_key, has_photos, media_has_360, is_buy_now, is_special_title, is_active,
                    observed_at, payload, search_text, updated_at)
                select lots.lot_key, lower(lots.platform), lots.lot_number, lots.vin, lots.title, lots.year, lots.make, lots.model,
                    case when lower(lots.platform) = 'copart' then coalesce(
                        nullif(btrim(latest.payload #>> '{vehicle_specs,body_style}'), ''),
                        nullif(btrim(latest.payload #>> '{details,vehicle_description,BodyStyle}'), ''),
                        nullif(btrim(latest.payload #>> '{BodyStyle}'), ''),
                        lots.vehicle_type)
                        else lots.vehicle_type end,
                    title_facet.category,
                    coalesce(lots.color, latest.payload #>> '{vehicle_specs,exterior_color}'),
                    coalesce(lots.fuel_type, latest.payload #>> '{vehicle_specs,fuel_type}'),
                    coalesce(lots.transmission, latest.payload #>> '{vehicle_specs,transmission}'),
                    coalesce(lots.drive_type, latest.payload #>> '{vehicle_specs,drive_type}'),
                    coalesce(
                        nullif(btrim(latest.payload #>> '{vehicle_specs,body_style}'), ''),
                        nullif(btrim(latest.payload #>> '{details,vehicle_description,BodyStyle}'), ''),
                        nullif(btrim(latest.payload #>> '{BodyStyle}'), '')),

                    coalesce(lots.damage, latest.payload #>> '{condition,primary_damage}'),
                    latest.payload #>> '{condition,secondary_damage}', __SELLER_TYPE_SQL__,
                    coalesce(nullif(btrim(latest.payload #>> '{seller,name}'), ''), nullif(btrim(latest.payload #>> '{Seller,Name}'), '')),
                    case when coalesce(latest.payload #>> '{seller,classification_confidence}', latest.payload #>> '{seller,classificationConfidence}', latest.payload #>> '{Seller,ClassificationConfidence}') ~ '^[0-9]+([.][0-9]+)?$' then coalesce(latest.payload #>> '{seller,classification_confidence}', latest.payload #>> '{seller,classificationConfidence}', latest.payload #>> '{Seller,ClassificationConfidence}')::numeric end,
                    case lower(coalesce(latest.payload #>> '{seller,needs_review}', latest.payload #>> '{seller,needsReview}', latest.payload #>> '{Seller,NeedsReview}')) when 'false' then false when 'true' then true else true end,
                    coalesce(latest.payload #>> '{seller,classification_evidence}', latest.payload #>> '{seller,classificationEvidence}', latest.payload #>> '{Seller,ClassificationEvidence}'),
                    coalesce(latest.payload #>> '{seller,taxonomy_version}', latest.payload #>> '{seller,taxonomyVersion}', latest.payload #>> '{Seller,TaxonomyVersion}'),
                    latest.payload #>> '{vehicle_specs,engine,layout}', latest.payload #>> '{details,vehicle_description,Cylinders}',
                    latest.payload #>> '{condition,loss}', coalesce(latest.payload #>> '{condition,run_condition,value}', latest.payload #>> '{condition,run_condition,label}'),
                    lots.auction_state, lots.auction_at, lots.lot_status, lots.lot_sub_status, lots.location_display,
                    lots.location_state, lots.facility_id, lots.odometer, lots.current_bid_usd, lots.buy_now_usd,
                    case when latest.payload #>> '{pricing,estimated_cost,from}' ~ '^[0-9]+([.][0-9]+)?$' then (latest.payload #>> '{pricing,estimated_cost,from}')::numeric end,
                    case when latest.payload #>> '{pricing,estimated_cost,to}' ~ '^[0-9]+([.][0-9]+)?$' then (latest.payload #>> '{pricing,estimated_cost,to}')::numeric end,
                    case when latest.payload #>> '{vehicle_specs,engine,size_l}' ~ '^[0-9]+([.][0-9]+)?$' then (latest.payload #>> '{vehicle_specs,engine,size_l}')::numeric end,
                    case when latest.payload #>> '{vehicle_specs,engine,hp}' ~ '^[0-9]+([.][0-9]+)?$' then (latest.payload #>> '{vehicle_specs,engine,hp}')::numeric end,
                    case lower(latest.payload #>> '{condition,has_key}') when 'true' then true when 'false' then false end,
                    coalesce(lots.media_photos_count, 0) > 0,
                    lots.media_has_360,
                    coalesce(lots.buy_now_usd, 0) > 0,
                    title_facet.category = 'SPECIAL',
                    coalesce(lifecycle.is_active, true), latest.observed_at, latest.payload,
                    concat_ws(' ', lots.lot_key, lots.lot_number, lots.vin, lots.title, lots.make, lots.model,
                        title_source.document, title_facet.category),
                    now()
                from auction_lots lots
                join lateral (
                    select versions.observed_at, versions.payload
                    from auction_lot_versions versions
                    where versions.lot_key = lots.lot_key
                    order by versions.observed_at desc
                    limit 1
                ) latest on true
                left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = lots.lot_key
                cross join lateral (
                    select coalesce(
                        nullif(btrim(latest.payload #>> '{sale_document,name}'), ''),
                        case when lower(lots.platform) = 'copart' then nullif(btrim(lots.title), '') end,
                        'NO REPORTADO') as document
                ) title_source
                cross join lateral (
                    select regexp_replace(
                        regexp_replace(upper(title_source.document), '[-/_,.]+', ' ', 'g'),
                        '\s+', ' ', 'g') as normalized_document
                ) title_normalized
                cross join lateral (
                    select __TITLE_CATEGORY_SQL__ as category
                ) title_facet
                on conflict (lot_key) do update set
                    platform = excluded.platform, lot_number = excluded.lot_number, vin = excluded.vin, title = excluded.title,
                    year = excluded.year, make = excluded.make, model = excluded.model, vehicle_type = excluded.vehicle_type,
                    title_type = excluded.title_type, color = excluded.color, fuel_type = excluded.fuel_type,
                    transmission = excluded.transmission, drive_type = excluded.drive_type, body_style = excluded.body_style,
                    primary_damage = excluded.primary_damage, secondary_damage = excluded.secondary_damage,
                    seller_type = excluded.seller_type, seller_name = excluded.seller_name,
                    seller_classification_confidence = excluded.seller_classification_confidence,
                    seller_needs_review = excluded.seller_needs_review, seller_classification_evidence = excluded.seller_classification_evidence,
                    seller_taxonomy_version = excluded.seller_taxonomy_version, engine_layout = excluded.engine_layout, cylinders = excluded.cylinders,
                    loss_type = excluded.loss_type, start_code = excluded.start_code, auction_state = excluded.auction_state,
                    auction_at = excluded.auction_at, lot_status = excluded.lot_status, lot_sub_status = excluded.lot_sub_status,
                    location_display = excluded.location_display, location_state = excluded.location_state,
                    facility_id = excluded.facility_id, odometer = excluded.odometer, current_bid_usd = excluded.current_bid_usd,
                    buy_now_usd = excluded.buy_now_usd, provider_estimate_from = excluded.provider_estimate_from,
                    provider_estimate_to = excluded.provider_estimate_to, engine_size_liters = excluded.engine_size_liters,
                    horsepower = excluded.horsepower, has_key = excluded.has_key, has_photos = excluded.has_photos,
                    media_has_360 = excluded.media_has_360, is_buy_now = excluded.is_buy_now,
                    is_special_title = excluded.is_special_title, is_active = excluded.is_active,
                    observed_at = excluded.observed_at, payload = excluded.payload,
                    search_text = excluded.search_text, updated_at = now();

                delete from inventory_search_current projection
                where not exists (select 1 from auction_lots lots where lots.lot_key = projection.lot_key);
                """
                .Replace("__TITLE_CATEGORY_SQL__", titleCategorySql, StringComparison.Ordinal)
                .Replace("__SELLER_TYPE_SQL__", SqlSellerTypePayloadExpression("latest"), StringComparison.Ordinal);
            await rebuild.ExecuteNonQueryAsync(cancellationToken);
        }

        await RefreshSearchFacetsAsync(connection, transaction, cancellationToken);
        long rows;
        DateTimeOffset? generatedAt;
        await using (var finalize = connection.CreateCommand())
        {
            finalize.Transaction = transaction;
            finalize.CommandTimeout = _persistence.CommandTimeoutSeconds;
            finalize.CommandText = """
                with stats as (
                    select count(*)::bigint as rows,
                           count(*) filter (where not is_special_title)::bigint as visible_rows,
                           max(observed_at) as generated_at
                    from inventory_search_current where is_active
                )
                update inventory_search_projection_state state
                set is_ready = true, schema_version = 3, row_count = stats.rows, visible_row_count = stats.visible_rows, generated_at = stats.generated_at,
                    facets_refreshed_at = now(), updated_at = now()
                from stats where state.projection_name = 'inventory-current-v1'
                returning state.row_count, state.generated_at;
                """;
            await using var reader = await finalize.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) throw new InvalidOperationException("Search projection state was not initialized.");
            rows = reader.GetInt64(0);
            generatedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
        }
        await transaction.CommitAsync(cancellationToken);
        _projectionReadyCache = true;
        Interlocked.Exchange(ref _projectionReadyCheckedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        return new InventorySearchProjectionStatus(true, rows, generatedAt, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow - startedAt);
    }

    public async Task<InventorySearchProjectionStatus> GetSearchProjectionStatusAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select is_ready, schema_version, row_count, generated_at, facets_refreshed_at
            from inventory_search_projection_state
            where projection_name = 'inventory-current-v1';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new InventorySearchProjectionStatus(false, 0, null, null, DateTimeOffset.UtcNow - startedAt);
        var ready = reader.GetBoolean(0);
        _projectionReadyCache = ready;
        Interlocked.Exchange(ref _projectionReadyCheckedAtTicks, DateTimeOffset.UtcNow.UtcTicks);
        return new InventorySearchProjectionStatus(
            ready,
            reader.GetInt64(2),
            reader.IsDBNull(3) ? null : reader.GetFieldValue<DateTimeOffset>(3),
            reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4),
            DateTimeOffset.UtcNow - startedAt,
            reader.GetInt32(1));
    }

    public async Task<CopartTitleTaxonomyCoverage> GetCopartTitleTaxonomyCoverageAsync(CancellationToken cancellationToken)
    {
        const string version = "copart-title-taxonomy-v1";
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select
                count(*) filter (where lower(platform) = 'copart')::bigint,
                count(*) filter (where lower(platform) = 'copart' and payload ->> 'title_taxonomy_version' = @version)::bigint
            from inventory_search_current
            where is_active;
            """;
        AddParameter(command, "version", version);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new CopartTitleTaxonomyCoverage(version, 0, 0, 0m, false, DateTimeOffset.UtcNow);
        var total = reader.GetInt64(0);
        var classified = reader.GetInt64(1);
        var coverage = total == 0 ? 0m : decimal.Round(classified * 100m / total, 2);
        return new CopartTitleTaxonomyCoverage(version, total, classified, coverage, total > 0 && coverage >= 95m, DateTimeOffset.UtcNow);
    }

    private async Task RefreshSearchFacetsAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        await using var facets = connection.CreateCommand();
        facets.Transaction = transaction;
        facets.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 300);
        facets.CommandText = $"""
            delete from inventory_search_facet_counts;
            with expanded as (
                select facet_key, nullif(btrim(facet_value), '') as facet_value
                from inventory_search_current current
                cross join lateral (values
                    ('platforms', current.platform), ('makes', current.make), ('models', current.model),
                    ('vehicleTypes', current.vehicle_type), ('titles', current.title_type), ('states', current.location_state),
                    ('facilities', current.location_display), ('primaryDamages', current.primary_damage),
                    ('secondaryDamages', current.secondary_damage), ('sellerTypes', {SqlSellerTypeExpression("current")}),
                    ('engineLayouts', current.engine_layout), ('cylinders', current.cylinders),
                    ('transmissions', current.transmission), ('fuels', current.fuel_type), ('drives', current.drive_type),
                    ('bodyStyles', current.body_style), ('colors', current.color), ('lossTypes', current.loss_type),
                    ('startCodes', current.start_code),
                    ('runConditions', {PublicRunConditionSql("current")})
                ) facets(facet_key, facet_value)
                where current.is_active
            ), counts as (
                select facet_key, facet_value, count(*)::int as vehicle_count
                from expanded where facet_value is not null group by facet_key, facet_value
            ), ranked as (
                select counts.*, row_number() over (partition by facet_key order by vehicle_count desc, facet_value asc) as rank
                from counts
            )
            insert into inventory_search_facet_counts (facet_key, facet_value, vehicle_count, generated_at)
            select facet_key, facet_value, vehicle_count, now() from ranked where rank <= 250;
            """;
        await facets.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task RefreshSearchProjectionStatisticsIfReadyAsync(CancellationToken cancellationToken)
    {
        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        if (!await IsSearchProjectionReadyAsync(cancellationToken)) return;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await RefreshSearchFacetsAsync(connection, transaction, cancellationToken);
        await using var state = connection.CreateCommand();
        state.Transaction = transaction;
        state.CommandTimeout = _persistence.CommandTimeoutSeconds;
        state.CommandText = """
            with stats as (
                select count(*)::bigint as rows,
                       count(*) filter (where not is_special_title)::bigint as visible_rows,
                       max(observed_at) as generated_at
                from inventory_search_current where is_active
            )
            update inventory_search_projection_state projection
            set row_count = stats.rows, visible_row_count = stats.visible_rows, generated_at = stats.generated_at,
                facets_refreshed_at = now(), updated_at = now()
            from stats where projection.projection_name = 'inventory-current-v1';
            """;
        await state.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<StoredVehicleSnapshot?> GetByLotAsync(string lotNumber, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        if (await IsSearchProjectionReadyAsync(cancellationToken))
        {
            await using var projectionConnection = await OpenConnectionAsync(cancellationToken);
            await using var projectionCommand = projectionConnection.CreateCommand();
            projectionCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
            projectionCommand.CommandText = """
                select current.lot_key, current.observed_at, current.payload::text, current.platform, current.lot_number, current.vin
                from inventory_search_current current
                left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = current.lot_key
                where current.lot_number = @lot_number and coalesce(lifecycle.is_active, current.is_active)
                order by observed_at desc limit 1;
                """;
            AddParameter(projectionCommand, "lot_number", lotNumber.Trim());
            return (await ReadStoredSnapshotsAsync(projectionCommand, cancellationToken)).SingleOrDefault();
        }
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select versions.lot_key, versions.observed_at, versions.payload::text,
                   lots.platform, lots.lot_number, lots.vin
            from auction_lot_versions versions
            join auction_lots lots on lots.lot_key = versions.lot_key
            left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = versions.lot_key
            where lots.lot_number = @lot_number
              and coalesce(lifecycle.is_active, true)
            order by versions.observed_at desc
            limit 1;
            """;
        AddParameter(command, "lot_number", lotNumber.Trim());
        return (await ReadStoredSnapshotsAsync(command, cancellationToken)).SingleOrDefault();
    }

    public async Task<StoredVehicleSnapshot?> GetByPlatformAndLotAsync(string platform, string lotNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(platform)) throw new ArgumentException("Platform is required.", nameof(platform));
        if (string.IsNullOrWhiteSpace(lotNumber)) throw new ArgumentException("Lot number is required.", nameof(lotNumber));

        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        if (await IsSearchProjectionReadyAsync(cancellationToken))
        {
            await using var projectionConnection = await OpenConnectionAsync(cancellationToken);
            await using var projectionCommand = projectionConnection.CreateCommand();
            projectionCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
            projectionCommand.CommandText = """
                select current.lot_key, current.observed_at, current.payload::text, current.platform, current.lot_number, current.vin
                from inventory_search_current current
                left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = current.lot_key
                where current.platform = @platform
                  and current.lot_number = @lot_number
                  and coalesce(lifecycle.is_active, current.is_active)
                order by observed_at desc
                limit 1;
                """;
            AddParameter(projectionCommand, "platform", platform.Trim().ToLowerInvariant());
            AddParameter(projectionCommand, "lot_number", lotNumber.Trim());
            return (await ReadStoredSnapshotsAsync(projectionCommand, cancellationToken)).SingleOrDefault();
        }

        await EnsureSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select versions.lot_key, versions.observed_at, versions.payload::text,
                   lots.platform, lots.lot_number, lots.vin
            from auction_lot_versions versions
            join auction_lots lots on lots.lot_key = versions.lot_key
            left join inventory_lot_lifecycle lifecycle on lifecycle.lot_key = versions.lot_key
            where lots.platform = @platform
              and lots.lot_number = @lot_number
              and coalesce(lifecycle.is_active, true)
            order by versions.observed_at desc
            limit 1;
            """;
        AddParameter(command, "platform", platform.Trim().ToLowerInvariant());
        AddParameter(command, "lot_number", lotNumber.Trim());
        return (await ReadStoredSnapshotsAsync(command, cancellationToken)).SingleOrDefault();
    }

    private static string SqlTitleCategoryExpression(string alias)
    {
        var normalizedSource = "regexp_replace(upper(coalesce(nullif(btrim(" + alias + ".payload #>> '{SaleDocument,Name}'), ''), case when lower(" + alias + ".platform) = 'copart' then nullif(btrim(" + alias + ".title), '') end, 'NO REPORTADO')), '[-/_,.]+', ' ', 'g')";
        var normalizedPlatform = "lower(coalesce(" + alias + ".platform, ''))";
        return TitleFacetCategory.BuildSqlCaseExpression(normalizedSource, normalizedPlatform);
    }

    private static void AddSearchFilters(NpgsqlCommand command, InventorySearchRequest request, List<string> where)
    {
        static string[] Values(IReadOnlyCollection<string>? values) => values?.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        void AddAny(string parameter, IReadOnlyCollection<string>? values, string expression)
        {
            var selected = Values(values);
            if (selected.Length == 0) return;
            where.Add($"lower(coalesce({expression}, '')) = any(@{parameter})");
            AddParameter(command, parameter, selected.Select(value => value.ToLowerInvariant()).ToArray());
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            where.Add("concat_ws(' ', latest.lot_key, latest.lot_number, latest.vin, latest.make, latest.model, latest.title, latest.payload #>> '{SaleDocument,Name}') ilike @query");
            AddParameter(command, "query", $"%{request.Query.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Platform))
        {
            where.Add("lower(latest.platform) = @platform");
            AddParameter(command, "platform", request.Platform.Trim().ToLowerInvariant());
        }
        AddAny("makes", request.Makes, "latest.make");
        AddAny("models", request.Models, "latest.model");
        AddAny("vehicle_types", request.VehicleTypes, "latest.vehicle_type");
        var titleCategory = SqlTitleCategoryExpression("latest");
        if (request.ExcludeSpecialTitles) where.Add($"{titleCategory} <> 'SPECIAL'");
        AddAny("titles", request.Titles, titleCategory);
        AddAny("title_categories", request.TitleCategories, "latest.payload ->> 'title_category'");
        AddAny("states", request.States, "latest.location_state");
        AddAny("facilities", request.Facilities, "latest.location_display");
        AddAny("primary_damages", request.PrimaryDamages, "latest.damage");
        AddAny("secondary_damages", request.SecondaryDamages, "latest.payload #>> '{Condition,SecondaryDamage}'");
        AddAny("seller_types", request.SellerTypes, SqlSellerTypePayloadExpression("latest"));
        AddAny("engine_layouts", request.EngineLayouts, "latest.payload #>> '{VehicleSpecs,Engine,Layout}'");
        AddAny("cylinders", request.Cylinders, "latest.payload #>> '{Details,VehicleDescription,Cylinders}'");
        AddAny("transmissions", request.Transmissions, "latest.payload #>> '{Transmission}'");
        AddAny("fuels", request.Fuels, "latest.payload #>> '{FuelType}'");
        AddAny("drives", request.Drives, "latest.payload #>> '{DriveType}'");
        AddAny("body_styles", request.BodyStyles, "latest.payload #>> '{BodyStyle}'");
        AddAny("colors", request.Colors, "latest.payload #>> '{Color}'");
        AddAny("loss_types", request.LossTypes, "latest.payload #>> '{LossType}'");
        AddAny("start_codes", request.StartCodes, "latest.payload #>> '{StartCode}'");
        AddAny("run_conditions", request.RunConditions, PublicRunConditionPayloadSql("latest"));
        if (request.YearFrom.HasValue) { where.Add("latest.year >= @year_from"); AddParameter(command, "year_from", request.YearFrom.Value); }
        if (request.YearTo.HasValue) { where.Add("latest.year <= @year_to"); AddParameter(command, "year_to", request.YearTo.Value); }
        if (request.OdometerFrom.HasValue) { where.Add("latest.odometer >= @odometer_from"); AddParameter(command, "odometer_from", request.OdometerFrom.Value); }
        if (request.OdometerTo.HasValue) { where.Add("latest.odometer <= @odometer_to"); AddParameter(command, "odometer_to", request.OdometerTo.Value); }
        if (request.PriceFrom.HasValue) { where.Add("latest.current_bid_usd >= @price_from"); AddParameter(command, "price_from", request.PriceFrom.Value); }
        if (request.PriceTo.HasValue) { where.Add("latest.current_bid_usd <= @price_to"); AddParameter(command, "price_to", request.PriceTo.Value); }
        if (request.BuyNowOnly == true || request.BuyNowFrom.HasValue || request.BuyNowTo.HasValue) where.Add("latest.buy_now_usd > 0");
        if (request.BuyNowFrom.HasValue) { where.Add("latest.buy_now_usd >= @buy_now_from"); AddParameter(command, "buy_now_from", request.BuyNowFrom.Value); }
        if (request.BuyNowTo.HasValue) { where.Add("latest.buy_now_usd <= @buy_now_to"); AddParameter(command, "buy_now_to", request.BuyNowTo.Value); }
        if (request.MaxCurrentBid.HasValue) { where.Add("(latest.current_bid_usd is null or latest.current_bid_usd <= @max_current_bid)"); AddParameter(command, "max_current_bid", request.MaxCurrentBid.Value); }
        if (request.AuctionFrom.HasValue) { where.Add("latest.auction_at >= @auction_from"); AddParameter(command, "auction_from", request.AuctionFrom.Value); }
        if (request.AuctionTo.HasValue) { where.Add("latest.auction_at <= @auction_to"); AddParameter(command, "auction_to", request.AuctionTo.Value); }
        if (request.WithPhotosOnly == true) where.Add("(coalesce(jsonb_array_length(latest.payload #> '{Media,Photos}'), 0) > 0 or coalesce(jsonb_array_length(latest.payload #> '{Media,Items}'), 0) > 0)");
        if (request.WithBidOnly == true) where.Add("latest.current_bid_usd is not null");
        if (string.Equals(request.KeyMode, "with", StringComparison.OrdinalIgnoreCase)) where.Add("lower(coalesce(latest.payload #>> '{Condition,HasKey}', '')) = 'true'");
        if (string.Equals(request.KeyMode, "without", StringComparison.OrdinalIgnoreCase)) where.Add("lower(coalesce(latest.payload #>> '{Condition,HasKey}', '')) = 'false'");
        if (request.ProviderEstimateFrom.HasValue) { where.Add("nullif(latest.payload #>> '{Pricing,EstimatedCost,ToUsd}', '')::numeric >= @provider_estimate_from"); AddParameter(command, "provider_estimate_from", request.ProviderEstimateFrom.Value); }
        if (request.ProviderEstimateTo.HasValue) { where.Add("nullif(latest.payload #>> '{Pricing,EstimatedCost,FromUsd}', '')::numeric <= @provider_estimate_to"); AddParameter(command, "provider_estimate_to", request.ProviderEstimateTo.Value); }
        if (request.EngineSizeFrom.HasValue) { where.Add("nullif(latest.payload #>> '{VehicleSpecs,Engine,SizeLiters}', '')::numeric >= @engine_size_from"); AddParameter(command, "engine_size_from", request.EngineSizeFrom.Value); }
        if (request.EngineSizeTo.HasValue) { where.Add("nullif(latest.payload #>> '{VehicleSpecs,Engine,SizeLiters}', '')::numeric <= @engine_size_to"); AddParameter(command, "engine_size_to", request.EngineSizeTo.Value); }
        if (request.HorsepowerFrom.HasValue) { where.Add("nullif(latest.payload #>> '{VehicleSpecs,Engine,Horsepower}', '')::numeric >= @horsepower_from"); AddParameter(command, "horsepower_from", request.HorsepowerFrom.Value); }
        if (request.HorsepowerTo.HasValue) { where.Add("nullif(latest.payload #>> '{VehicleSpecs,Engine,Horsepower}', '')::numeric <= @horsepower_to"); AddParameter(command, "horsepower_to", request.HorsepowerTo.Value); }
        if (string.Equals(request.AuctionStatus, "open", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.payload #>> '{Auction,LotStatus}', latest.payload #>> '{Auction,LotSubStatus}')) like any(array['%open%', '%active%'])");
        if (string.Equals(request.AuctionStatus, "live", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.payload #>> '{Auction,LotStatus}', latest.payload #>> '{Auction,LotSubStatus}')) like '%live%'");
        if (string.Equals(request.AuctionStatus, "finished", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.payload #>> '{Auction,LotStatus}', latest.payload #>> '{Auction,LotSubStatus}')) like any(array['%finished%', '%ended%', '%sold%'])");
    }

    private static string GetSearchOrdering(string? sort)
    {
        var secondary = sort?.Trim().ToLowerInvariant() switch
        {
            "auction" => "latest.auction_at asc nulls last",
            "auction-desc" => "latest.auction_at desc nulls last",
            "year-asc" => "latest.year asc nulls last",
            "year-desc" => "latest.year desc nulls last",
            "estimate-asc" => "nullif(latest.payload #>> '{Pricing,EstimatedCost,FromUsd}', '')::numeric asc nulls last",
            "estimate-desc" => "nullif(latest.payload #>> '{Pricing,EstimatedCost,ToUsd}', '')::numeric desc nulls last",
            "buy-asc" => "latest.buy_now_usd asc nulls last",
            "buy-desc" => "latest.buy_now_usd desc nulls last",
            "bid-asc" => "latest.current_bid_usd asc nulls last",
            "bid-desc" => "latest.current_bid_usd desc nulls last",
            "odometer-asc" => "latest.odometer asc nulls last",
            "odometer-desc" => "latest.odometer desc nulls last",
            _ => "latest.observed_at desc nulls last",
        };
        return $"score.pre_grade desc nulls last, {secondary}";
    }

    private static string? ReadOptionalString(NpgsqlDataReader reader, string column)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (!string.Equals(reader.GetName(index), column, StringComparison.OrdinalIgnoreCase)) continue;
            return reader.IsDBNull(index) ? null : reader.GetString(index);
        }
        return null;
    }

    private static decimal? ReadOptionalDecimal(NpgsqlDataReader reader, string column)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (!string.Equals(reader.GetName(index), column, StringComparison.OrdinalIgnoreCase)) continue;
            return reader.IsDBNull(index) ? null : reader.GetDecimal(index);
        }
        return null;
    }

    private static DateTimeOffset? ReadOptionalDateTimeOffset(NpgsqlDataReader reader, string column)
    {
        for (var index = 0; index < reader.FieldCount; index++)
        {
            if (!string.Equals(reader.GetName(index), column, StringComparison.OrdinalIgnoreCase)) continue;
            return reader.IsDBNull(index) ? null : reader.GetFieldValue<DateTimeOffset>(index);
        }
        return null;
    }

    private static string? PreferPersisted(string? persisted, string? payload) =>
        string.IsNullOrWhiteSpace(persisted) ? payload : persisted;

    private async Task<List<StoredVehicleSnapshot>> ReadStoredSnapshotsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var snapshots = new List<StoredVehicleSnapshot>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var jsonOptions = CreateStoredVehicleJsonOptions();
        while (await reader.ReadAsync(cancellationToken))
        {
            var rawJson = reader.GetString(2);
            var lotKey = reader.GetString(0);
            var vehicle = DeserializeStoredVehicle(rawJson, lotKey, jsonOptions);
            if (vehicle is not null)
            {
                vehicle = vehicle with
                {
                    Platform = PreferPersisted(ReadOptionalString(reader, "platform"), vehicle.Platform),
                    LotNumber = PreferPersisted(ReadOptionalString(reader, "lot_number"), vehicle.LotNumber),
                    Vin = PreferPersisted(ReadOptionalString(reader, "vin"), vehicle.Vin)
                };
                LscScoringSummary? scoring = null;
                var scoreStatus = ReadOptionalString(reader, "score_status");
                if (!string.IsNullOrWhiteSpace(scoreStatus))
                {
                    scoring = new LscScoringSummary(
                        scoreStatus,
                        ReadOptionalDecimal(reader, "score_pre_grade"),
                        ReadOptionalDecimal(reader, "score_buy_score"),
                        ReadOptionalDecimal(reader, "score_max_points_evaluable") ?? 0m,
                        ReadOptionalDecimal(reader, "score_coverage_percent") ?? 0m,
                        ReadOptionalDecimal(reader, "score_confidence_percent") ?? 0m,
                        ReadOptionalString(reader, "score_category"),
                        ReadOptionalString(reader, "score_policy_version") ?? "unknown",
                        ReadOptionalDateTimeOffset(reader, "score_scored_at") ?? DateTimeOffset.MinValue);
                }
                snapshots.Add(new StoredVehicleSnapshot(lotKey, reader.GetFieldValue<DateTimeOffset>(1), vehicle, rawJson, scoring));
            }
        }
        return snapshots;
    }

    private AuctionVehicle? DeserializeStoredVehicle(string rawJson, string lotKey, JsonSerializerOptions jsonOptions)
    {
        try
        {
            return JsonSerializer.Deserialize<AuctionVehicle>(rawJson, jsonOptions);
        }
        catch (JsonException firstException)
        {
            JsonNode? sanitized;
            try
            {
                sanitized = JsonNode.Parse(rawJson);
            }
            catch (JsonException)
            {
                throw;
            }

            var removedPaths = new List<string>();
            var currentException = firstException;
            for (var attempt = 0; attempt < 8 && sanitized is not null; attempt++)
            {
                var path = currentException.Path;
                if (!TryRemoveIncompatibleJsonValue(sanitized, path)) break;
                removedPaths.Add(path ?? "$unknown");

                try
                {
                    var vehicle = JsonSerializer.Deserialize<AuctionVehicle>(sanitized.ToJsonString(), jsonOptions);
                    logger.LogWarning(
                        "Skipped incompatible snapshot fields while serving inventory lot {LotKey}. Paths: {Paths}",
                        lotKey,
                        string.Join(", ", removedPaths));
                    return vehicle;
                }
                catch (JsonException nextException)
                {
                    currentException = nextException;
                }
            }

            throw new JsonException(
                $"Stored inventory snapshot for lot {lotKey} cannot be read after removing incompatible fields: {string.Join(", ", removedPaths)}.",
                currentException);
        }
    }

    private static bool TryRemoveIncompatibleJsonValue(JsonNode root, string? jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath) || !jsonPath.StartsWith("$", StringComparison.Ordinal)) return false;
        var matches = JsonPathSegmentRegex().Matches(jsonPath);
        if (matches.Count == 0) return false;

        JsonNode? current = root;
        for (var index = 0; index < matches.Count; index++)
        {
            var isLast = index == matches.Count - 1;
            var property = matches[index].Groups[1].Success ? matches[index].Groups[1].Value : null;
            var arrayIndex = matches[index].Groups[2].Success
                ? int.Parse(matches[index].Groups[2].Value, CultureInfo.InvariantCulture)
                : (int?)null;

            if (property is not null)
            {
                if (current is not JsonObject objectNode) return false;
                if (isLast) return objectNode.Remove(property);
                current = objectNode[property];
                continue;
            }

            if (arrayIndex is null || current is not JsonArray arrayNode || arrayIndex < 0 || arrayIndex >= arrayNode.Count) return false;
            if (isLast)
            {
                arrayNode[arrayIndex.Value] = null;
                return true;
            }
            current = arrayNode[arrayIndex.Value];
        }

        return false;
    }

    [GeneratedRegex(@"(?:\.([A-Za-z_][A-Za-z0-9_]*))|(?:\[(\d+)\])", RegexOptions.CultureInvariant)]
    private static partial Regex JsonPathSegmentRegex();

    public async Task<int> DeactivateArchivedLotsAsync(string platform, IReadOnlyCollection<string> lotKeys, DateTimeOffset archivedAt, CancellationToken cancellationToken, Guid? runId = null)
    {
        var normalizedPlatform = platform.Trim().ToLowerInvariant();
        var keys = lotKeys.Where(key => !string.IsNullOrWhiteSpace(key)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (keys.Length == 0) return 0;
        await EnsureLifecycleSchemaAsync(cancellationToken);
        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var deactivated = new List<string>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
                update inventory_lot_lifecycle
                set is_active = false,
                    consecutive_misses = 0,
                    deactivated_at = coalesce(deactivated_at, @archived_at),
                    updated_at = now()
                where platform = @platform
                  and lot_key = any(@lot_keys)
                  and is_active
                returning lot_key;
                """;
            AddParameter(command, "platform", normalizedPlatform);
            AddParameter(command, "archived_at", archivedAt);
            AddParameter(command, "lot_keys", keys);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) deactivated.Add(reader.GetString(0));
        }
        await transaction.CommitAsync(cancellationToken);
        await using (var projection = await OpenConnectionAsync(cancellationToken))
        await using (var command = projection.CreateCommand())
        {
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
                update inventory_search_current projection
                set is_active = lifecycle.is_active, updated_at = now()
                from inventory_lot_lifecycle lifecycle
                where lifecycle.platform = @platform
                  and lifecycle.lot_key = any(@lot_keys)
                  and lifecycle.lot_key = projection.lot_key;
                """;
            AddParameter(command, "platform", normalizedPlatform);
            AddParameter(command, "lot_keys", keys);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        if (runId is not null)
            foreach (var lotKey in deactivated)
                await RecordSyncRunEventAsync(new InventorySyncRunEvent(runId.Value, normalizedPlatform, lotKey, lotKey.Split(':').LastOrDefault(), null, "deactivated", ["provider-archived"], [], archivedAt), cancellationToken);
        return deactivated.Count;
    }

    public async Task<InventoryReconciliationResult> ReconcileSourceAsync(string platform, IReadOnlyCollection<string> observedLotKeys, bool isCompleteSnapshot, DateTimeOffset observedAt, CancellationToken cancellationToken, Guid? runId = null)
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

        var reactivatedLotKeys = new List<string>();
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
                  and not is_active
                returning lot_key;
                """;
            AddParameter(reactivate, "platform", normalizedPlatform);
            AddParameter(reactivate, "observed_at", observedAt);
            AddParameter(reactivate, "observed", observed);
            await using var reader = await reactivate.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) reactivatedLotKeys.Add(reader.GetString(0));
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
                returning lot_key, is_active
            )
            select lot_key, is_active from updated;
            """;
        AddParameter(reconcile, "platform", normalizedPlatform);
        AddParameter(reconcile, "observed_at", observedAt);
        AddParameter(reconcile, "observed", observed);
        var incremented = 0;
        var deactivatedLotKeys = new List<string>();
        await using (var reader = await reconcile.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                incremented++;
                if (!reader.GetBoolean(1)) deactivatedLotKeys.Add(reader.GetString(0));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        await using (var projectionConnection = await OpenConnectionAsync(cancellationToken))
        {
            await using var projectionState = projectionConnection.CreateCommand();
            projectionState.CommandTimeout = _persistence.CommandTimeoutSeconds;
            projectionState.CommandText = """
                update inventory_search_current projection
                set is_active = lifecycle.is_active, updated_at = now()
                from inventory_lot_lifecycle lifecycle
                where lifecycle.platform = @platform and lifecycle.lot_key = projection.lot_key;
                """;
            AddParameter(projectionState, "platform", normalizedPlatform);
            await projectionState.ExecuteNonQueryAsync(cancellationToken);
        }
        if (runId is not null)
        {
            foreach (var lotKey in reactivatedLotKeys)
                await RecordSyncRunEventAsync(new InventorySyncRunEvent(runId.Value, normalizedPlatform, lotKey, lotKey.Split(':').LastOrDefault(), null, "reactivated", ["estado activo"], [], observedAt), cancellationToken);
            foreach (var lotKey in deactivatedLotKeys)
                await RecordSyncRunEventAsync(new InventorySyncRunEvent(runId.Value, normalizedPlatform, lotKey, lotKey.Split(':').LastOrDefault(), null, "deactivated", ["tres ausencias consecutivas"], [], observedAt), cancellationToken);
        }
        return new InventoryReconciliationResult(normalizedPlatform, true, observed.Length, reactivatedLotKeys.Count, incremented, deactivatedLotKeys.Count);
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

                create table if not exists inventory_execution_run_metrics (
                    run_id uuid primary key,
                    loaded_count integer,
                    marked_count integer,
                    discarded_count integer,
                    quarantined_count integer,
                    error_count integer,
                    pages_processed integer,
                    cycle_completed boolean,
                    reactivated_count integer,
                    misses_incremented_count integer,
                    deactivated_count integer,
                    failures jsonb not null default '[]'::jsonb,
                    updated_at timestamptz not null default now()
                );

                create table if not exists inventory_sync_run_events (
                    id bigserial primary key,
                    run_id uuid not null,
                    platform text not null,
                    lot_key text not null,
                    lot_number text,
                    vin_masked text,
                    action text not null,
                    changed_fields jsonb not null default '[]'::jsonb,
                    rule_codes jsonb not null default '[]'::jsonb,
                    occurred_at timestamptz not null,
                    created_at timestamptz not null default now()
                );
                create index if not exists ix_inventory_sync_run_events_run_action on inventory_sync_run_events (run_id, action, occurred_at desc);

                create table if not exists provider_usage_snapshots (
                    id bigserial primary key,
                    provider text not null,
                    captured_at timestamptz not null,
                    usage jsonb not null,
                    created_at timestamptz not null default now()
                );

                insert into schema_migrations (migration_id)
                values ('002_iaai_national_sync')
                on conflict (migration_id) do nothing;

                create table if not exists inventory_sync_leases (
                    lease_name text primary key,
                    owner_run_id uuid not null,
                    expires_at timestamptz not null,
                    updated_at timestamptz not null default now()
                );

                create table if not exists iaai_national_sync_state (
                    stream_name text primary key,
                    cycle_id uuid,
                    cursor text,
                    pages_completed integer not null default 0,
                    lots_observed integer not null default 0,
                    cycle_completed boolean not null default true,
                    initial_backfill_completed boolean not null default false,
                    updated_at timestamptz not null default now()
                );

                alter table iaai_national_sync_state
                    add column if not exists initial_backfill_completed boolean not null default false;

                create table if not exists iaai_national_cycle_observations (
                    cycle_id uuid not null,
                    lot_key text not null,
                    observed_at timestamptz not null,
                    primary key (cycle_id, lot_key)
                );

                create index if not exists ix_iaai_national_cycle_observations_cycle on iaai_national_cycle_observations (cycle_id);
                create index if not exists ix_inventory_sync_leases_expires on inventory_sync_leases (expires_at);

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

    private async Task EnsureAuditSchemaAsync(CancellationToken cancellationToken)
    {
        if (_auditSchemaInitialized) return;
        await AuditSchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_auditSchemaInitialized) return;
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
                create table if not exists inventory_execution_run_metrics (
                    run_id uuid primary key, loaded_count integer, marked_count integer, discarded_count integer,
                    quarantined_count integer, error_count integer, pages_processed integer, cycle_completed boolean,
                    reactivated_count integer, misses_incremented_count integer, deactivated_count integer,
                    failures jsonb not null default '[]'::jsonb, updated_at timestamptz not null default now()
                );
                create table if not exists inventory_sync_run_events (
                    id bigserial primary key, run_id uuid not null, platform text not null, lot_key text not null,
                    lot_number text, vin_masked text, action text not null,
                    changed_fields jsonb not null default '[]'::jsonb, rule_codes jsonb not null default '[]'::jsonb,
                    occurred_at timestamptz not null, created_at timestamptz not null default now()
                );
                create index if not exists ix_inventory_sync_run_events_run_action on inventory_sync_run_events (run_id, action, occurred_at desc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _auditSchemaInitialized = true;
        }
        finally { AuditSchemaLock.Release(); }
    }

    private async Task EnsureSearchProjectionSchemaAsync(CancellationToken cancellationToken)
    {
        if (_searchProjectionSchemaInitialized) return;
        await SearchProjectionSchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_searchProjectionSchemaInitialized) return;
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
            command.CommandText = """
                create table if not exists inventory_search_current (
                    lot_key text primary key,
                    platform text not null,
                    lot_number text,
                    vin text,
                    title text,
                    year integer,
                    make text,
                    model text,
                    vehicle_type text,
                    title_type text,
                    color text,
                    fuel_type text,
                    transmission text,
                    drive_type text,
                    body_style text,
                    primary_damage text,
                    secondary_damage text,
                    seller_type text,
                    seller_name text,
                    seller_classification_confidence numeric,
                    seller_needs_review boolean,
                    seller_classification_evidence text,
                    seller_taxonomy_version text,
                    engine_layout text,
                    cylinders text,
                    loss_type text,
                    start_code text,
                    auction_state text,
                    auction_at timestamptz,
                    lot_status text,
                    lot_sub_status text,
                    location_display text,
                    location_state text,
                    facility_id text,
                    odometer numeric,
                    current_bid_usd numeric,
                    buy_now_usd numeric,
                    provider_estimate_from numeric,
                    provider_estimate_to numeric,
                    engine_size_liters numeric,
                    horsepower numeric,
                    has_key boolean,
                    has_photos boolean not null default false,
                    media_has_360 boolean,
                    is_buy_now boolean not null default false,
                    is_special_title boolean not null default false,
                    is_active boolean not null default true,
                    observed_at timestamptz not null,
                    payload jsonb not null,
                    search_text text not null default '',
                    updated_at timestamptz not null default now()
                );
                create index if not exists ix_inventory_search_active_auction on inventory_search_current (auction_at, lot_key) where is_active;
                create index if not exists ix_inventory_search_active_observed on inventory_search_current (observed_at desc, lot_key) where is_active;
                create index if not exists ix_inventory_search_platform_auction on inventory_search_current (platform, auction_at, lot_key) where is_active;
                alter table inventory_search_current add column if not exists seller_name text;
                alter table inventory_search_current add column if not exists seller_classification_confidence numeric;
                alter table inventory_search_current add column if not exists seller_needs_review boolean;
                alter table inventory_search_current add column if not exists seller_classification_evidence text;
                alter table inventory_search_current add column if not exists seller_taxonomy_version text;

                create index if not exists ix_inventory_search_make_model on inventory_search_current (lower(make), lower(model), lot_key) where is_active;
                create index if not exists ix_inventory_search_seller_name on inventory_search_current (lower(seller_name), lot_key) where is_active;
                create index if not exists ix_inventory_search_year on inventory_search_current (year, lot_key) where is_active;
                create index if not exists ix_inventory_search_bid on inventory_search_current (current_bid_usd, lot_key) where is_active;
                create index if not exists ix_inventory_search_buy_now on inventory_search_current (buy_now_usd, lot_key) where is_active;
                create index if not exists ix_inventory_search_odometer on inventory_search_current (odometer, lot_key) where is_active;
                create index if not exists ix_inventory_search_state on inventory_search_current (lower(location_state), lot_key) where is_active;
                create index if not exists ix_inventory_search_facility on inventory_search_current (lower(location_display), lot_key) where is_active;
                create index if not exists ix_inventory_search_title_type on inventory_search_current (lower(title_type), lot_key) where is_active;
                create index if not exists ix_inventory_search_primary_damage on inventory_search_current (lower(primary_damage), lot_key) where is_active;
                create index if not exists ix_inventory_search_fulltext on inventory_search_current using gin (to_tsvector('simple', search_text));

                create table if not exists inventory_search_projection_state (
                    projection_name text primary key,
                    schema_version integer not null,
                    is_ready boolean not null default false,
                    row_count bigint not null default 0,
                    visible_row_count bigint not null default 0,
                    generated_at timestamptz,
                    facets_refreshed_at timestamptz,
                    updated_at timestamptz not null default now()
                );
                alter table inventory_search_projection_state add column if not exists visible_row_count bigint not null default 0;
                insert into inventory_search_projection_state (projection_name, schema_version, is_ready)
                values ('inventory-current-v1', 1, false)
                on conflict (projection_name) do nothing;

                create table if not exists inventory_search_facet_counts (
                    facet_key text not null,
                    facet_value text not null,
                    vehicle_count integer not null,
                    generated_at timestamptz not null,
                    primary key (facet_key, facet_value)
                );
                create index if not exists ix_inventory_search_facets_key_count on inventory_search_facet_counts (facet_key, vehicle_count desc, facet_value);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _searchProjectionSchemaInitialized = true;
        }
        finally { SearchProjectionSchemaLock.Release(); }
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

        await blobClient.UploadAsync(BinaryData.FromString(rawJson), overwrite: false, cancellationToken: cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken) =>
        await OpenConnectionAsync(_persistence.Database, cancellationToken);

    private async Task<NpgsqlConnection> OpenConnectionAsync(string database, CancellationToken cancellationToken)
    {
        var accessToken = await GetDatabaseAccessTokenAsync(cancellationToken);

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

    private async Task<string> GetDatabaseAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_persistence.AccessToken)) return _persistence.AccessToken;
        var cached = _cachedDatabaseAccessToken;
        if (!string.IsNullOrWhiteSpace(cached.Token) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
            return cached.Token;

        await _databaseTokenLock.WaitAsync(cancellationToken);
        try
        {
            cached = _cachedDatabaseAccessToken;
            if (!string.IsNullOrWhiteSpace(cached.Token) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5))
                return cached.Token;
            _cachedDatabaseAccessToken = await _credential.GetTokenAsync(
                new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]),
                cancellationToken);
            return _cachedDatabaseAccessToken.Token;
        }
        finally
        {
            _databaseTokenLock.Release();
        }
    }

    private static void AddParameter(NpgsqlCommand command, string name, object? value) =>
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);

    private static string? ReadNullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static IReadOnlyList<string> ReadStringArray(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? [] : JsonSerializer.Deserialize<string[]>(reader.GetString(ordinal)) ?? [];

    private static decimal? ReadNullableDecimal(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetDecimal(ordinal);

    private static DateTimeOffset? ReadNullableDateTimeOffset(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static int? ReadNullableInt32(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);

    private static string? MaskVin(string? vin)
    {
        if (string.IsNullOrWhiteSpace(vin)) return null;
        var normalized = vin.Trim();
        return normalized.Length <= 4 ? normalized : string.Concat(Enumerable.Repeat('*', normalized.Length - 4)) + normalized[^4..];
    }

    private static IReadOnlyList<string> DescribeChangedFields(AuctionVehicle? previous, AuctionVehicle current)
    {
        if (previous is null) return ["snapshot"];
        var changed = new List<string>();
        void Compare(string name, object? left, object? right) { if (!Equals(left, right)) changed.Add(name); }
        Compare("puja actual", previous.Pricing?.CurrentBidUsd, current.Pricing?.CurrentBidUsd);
        Compare("Buy Now", previous.Pricing?.BuyNowUsd, current.Pricing?.BuyNowUsd);
        Compare("precio vendido", previous.Pricing?.SalePriceUsd, current.Pricing?.SalePriceUsd);
        Compare("estado del lote", previous.Auction?.LotStatus, current.Auction?.LotStatus);
        Compare("subestado", previous.Auction?.LotSubStatus, current.Auction?.LotSubStatus);
        Compare("fecha de subasta", previous.Auction?.AuctionAt, current.Auction?.AuctionAt);
        Compare("odómetro", previous.Odometer, current.Odometer);
        Compare("daño", previous.Damage, current.Damage);
        Compare("título", previous.SaleDocument?.Name ?? previous.Title, current.SaleDocument?.Name ?? current.Title);
        Compare("fotos", previous.Media?.ThumbnailsCount, current.Media?.ThumbnailsCount);
        return changed.Count == 0 ? ["snapshot"] : changed;
    }

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
