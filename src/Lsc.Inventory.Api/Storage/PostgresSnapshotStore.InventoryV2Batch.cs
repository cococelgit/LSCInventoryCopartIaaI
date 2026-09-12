using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Normalization;
using Npgsql;
using NpgsqlTypes;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    private static readonly string[] InventoryV2StageColumns =
    [
        "platform", "lot_number", "lot_key", "source_provider", "source_contract_version",
        "domain_id", "provider_vehicle_id", "provider_lot_id", "vin", "year",
        "manufacturer_id", "make", "model_id", "model", "generation_id", "generation",
        "vehicle_type_id", "vehicle_type", "body_type_id", "body_style", "fuel_id", "fuel_type",
        "engine_id", "engine", "engine_size_liters", "horsepower", "cylinders", "transmission_id",
        "transmission", "drive_id", "drive_type", "exterior_color", "manufactured_in", "vehicle_class", "series",
        "condition_id", "condition_name", "primary_damage_id", "primary_damage", "secondary_damage_id",
        "secondary_damage", "loss_type", "run_condition_value", "run_condition_label", "run_condition_class_hint",
        "has_key", "airbags", "restraint_system", "odometer_miles", "odometer_km", "odometer_status",
        "title_id", "title_type", "detailed_title_id", "detailed_title", "title_group", "title_pending",
        "title_export", "title_registration", "title_page_id", "title_brand", "title_notes", "special_note", "announcements",
        "seller_name", "seller_type_id", "seller_type", "seller_class", "seller_text_class", "seller_is_insurance",
        "seller_is_rental", "seller_is_credit_company", "seller_classification_confidence", "seller_needs_review",
        "seller_classification_evidence", "seller_taxonomy_version", "auction_state", "lot_status_id", "lot_status",
        "lot_sub_status", "auction_at", "auction_at_updated_at", "archived_at", "is_buy_now", "is_timed",
        "current_bid_usd", "current_bid_updated_at", "pre_bid_usd", "buy_now_usd", "buy_now_updated_at",
        "final_bid_usd", "final_bid_updated_at", "sale_price_usd", "sale_price_updated_at",
        "provider_estimate_from_usd", "provider_estimate_to_usd", "provider_estimate_text",
        "actual_cash_value_usd", "estimated_repair_cost_usd", "location_display", "location_city", "location_state", "facility_id",
        "facility_office_name", "facility_zip", "send_from", "lane", "aisle", "media_photos_count",
        "media_has_photos", "media_has_360", "media_has_video", "source_created_at", "source_updated_at",
        "identity_hash", "spec_hash", "condition_hash", "auction_hash", "seller_location_hash", "media_hash",
        "score_input_hash", "search_hash", "observed_at", "media_complete"
    ];

    private static readonly string[] InventoryV2TargetColumns =
        InventoryV2StageColumns.Where(static column => column is not ("observed_at" or "media_complete")).ToArray();

    public int PreferredBatchSize => Math.Clamp(_inventoryV2.BatchSize, 100, 2000);

    public bool ShadowWriteConfigured => _inventoryV2.ShadowWriteEnabled;

    public async Task<InventoryV2BatchWriteResult> WriteShadowBatchAsync(
        IReadOnlyCollection<InventoryV2BatchItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0) return InventoryV2BatchWriteResult.Skipped(0, "empty-batch");
        if (!_inventoryV2.ShadowWriteEnabled)
            return InventoryV2BatchWriteResult.Skipped(items.Count, "configuration-disabled");

        var prepared = items
            .Select(PrepareInventoryV2Lot)
            .Where(static row => row is not null)
            .Cast<PreparedInventoryV2Lot>()
            .GroupBy(static row => (row.Platform, row.LotNumber))
            .Select(static group => group.OrderByDescending(row => row.ObservedAt).First())
            .ToArray();
        if (prepared.Length == 0)
            return InventoryV2BatchWriteResult.Skipped(items.Count, "no-valid-lots");

        var started = Stopwatch.GetTimestamp();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (!await IsInventoryV2WriterEnabledAsync(connection, cancellationToken))
            return InventoryV2BatchWriteResult.Skipped(items.Count, "schema-writer-disabled");

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await CreateInventoryV2StageAsync(connection, transaction, cancellationToken);
        await CopyInventoryV2RowsAsync(connection, prepared, cancellationToken);
        await CopyInventoryV2MediaAsync(connection, prepared, cancellationToken);
        await BuildInventoryV2ActionsAsync(connection, transaction, cancellationToken);

        var counts = await ReadInventoryV2ActionCountsAsync(connection, transaction, cancellationToken);
        var mediaRows = await MergeInventoryV2Async(connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var durationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        logger.LogInformation(
            "Inventory V2 shadow batch completed input={Input} distinct={Distinct} created={Created} updated={Updated} unchanged={Unchanged} stale={Stale} media={Media} durationMs={DurationMs}",
            items.Count,
            prepared.Length,
            counts.Created,
            counts.Updated,
            counts.Unchanged,
            counts.Stale,
            mediaRows,
            durationMs);
        return new InventoryV2BatchWriteResult(
            true,
            items.Count,
            prepared.Length,
            counts.Created,
            counts.Updated,
            counts.Unchanged,
            counts.Stale,
            mediaRows,
            durationMs);
    }

    private async Task<bool> IsInventoryV2WriterEnabledAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select writer_enabled
            from inventory_v2_schema_state
            where schema_name = 'inventory-current-v2'
              and schema_version >= 1;
            """;
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null)
            throw new InvalidOperationException("Inventory V2 schema state is missing.");
        return (bool)result;
    }

    private async Task CreateInventoryV2StageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var definitions = string.Join(", ", InventoryV2StageColumns.Select(static column => $"{column} text"));
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = $"""
            create temp table inventory_v2_stage ({definitions}) on commit drop;
            create temp table inventory_v2_media_stage (
                platform text not null,
                lot_number text not null,
                media_type text not null,
                position integer not null,
                source_url text not null,
                thumbnail_url text,
                large_url text,
                observed_at timestamptz not null
            ) on commit drop;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task CopyInventoryV2RowsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<PreparedInventoryV2Lot> rows,
        CancellationToken cancellationToken)
    {
        var copy = $"copy inventory_v2_stage ({string.Join(", ", InventoryV2StageColumns)}) from stdin (format binary)";
        await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken);
        foreach (var row in rows)
        {
            await importer.StartRowAsync(cancellationToken);
            foreach (var column in InventoryV2StageColumns)
                await WriteNullableTextAsync(importer, row.Values.GetValueOrDefault(column), cancellationToken);
        }
        await importer.CompleteAsync(cancellationToken);
    }

    private static async Task CopyInventoryV2MediaAsync(
        NpgsqlConnection connection,
        IReadOnlyList<PreparedInventoryV2Lot> rows,
        CancellationToken cancellationToken)
    {
        const string copy = "copy inventory_v2_media_stage (platform, lot_number, media_type, position, source_url, thumbnail_url, large_url, observed_at) from stdin (format binary)";
        await using var importer = await connection.BeginBinaryImportAsync(copy, cancellationToken);
        foreach (var media in rows.SelectMany(static row => row.Media))
        {
            await importer.StartRowAsync(cancellationToken);
            await importer.WriteAsync(media.Platform, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(media.LotNumber, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(media.MediaType, NpgsqlDbType.Text, cancellationToken);
            await importer.WriteAsync(media.Position, NpgsqlDbType.Integer, cancellationToken);
            await importer.WriteAsync(media.SourceUrl, NpgsqlDbType.Text, cancellationToken);
            await WriteNullableTextAsync(importer, media.ThumbnailUrl, cancellationToken);
            await WriteNullableTextAsync(importer, media.LargeUrl, cancellationToken);
            await importer.WriteAsync(media.ObservedAt, NpgsqlDbType.TimestampTz, cancellationToken);
        }
        await importer.CompleteAsync(cancellationToken);
    }

    private static async Task WriteNullableTextAsync(NpgsqlBinaryImporter importer, string? value, CancellationToken cancellationToken)
    {
        if (value is null) await importer.WriteNullAsync(cancellationToken);
        else await importer.WriteAsync(value, NpgsqlDbType.Text, cancellationToken);
    }

    private async Task BuildInventoryV2ActionsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
        command.CommandText = $"""
            create temp table inventory_v2_typed on commit drop as
            select
                nullif(platform, '')::text as platform,
                nullif(lot_number, '')::text as lot_number,
                nullif(lot_key, '')::text as lot_key,
                coalesce(nullif(source_provider, ''), 'auctionsapi')::text as source_provider,
                coalesce(nullif(source_contract_version, ''), 'auctionsapi-v2')::text as source_contract_version,
                nullif(domain_id, '')::text as domain_id,
                nullif(provider_vehicle_id, '')::text as provider_vehicle_id,
                nullif(provider_lot_id, '')::text as provider_lot_id,
                nullif(vin, '')::text as vin,
                nullif(year, '')::integer as year,
                nullif(manufacturer_id, '')::text as manufacturer_id,
                nullif(make, '')::text as make,
                nullif(model_id, '')::text as model_id,
                nullif(model, '')::text as model,
                nullif(generation_id, '')::text as generation_id,
                nullif(generation, '')::text as generation,
                nullif(vehicle_type_id, '')::text as vehicle_type_id,
                nullif(vehicle_type, '')::text as vehicle_type,
                nullif(body_type_id, '')::text as body_type_id,
                nullif(body_style, '')::text as body_style,
                nullif(fuel_id, '')::text as fuel_id,
                nullif(fuel_type, '')::text as fuel_type,
                nullif(engine_id, '')::text as engine_id,
                nullif(engine, '')::text as engine,
                nullif(engine_size_liters, '')::numeric(6,2) as engine_size_liters,
                nullif(horsepower, '')::numeric(8,2) as horsepower,
                nullif(cylinders, '')::integer as cylinders,
                nullif(transmission_id, '')::text as transmission_id,
                nullif(transmission, '')::text as transmission,
                nullif(drive_id, '')::text as drive_id,
                nullif(drive_type, '')::text as drive_type,
                nullif(exterior_color, '')::text as exterior_color,
                nullif(manufactured_in, '')::text as manufactured_in,
                nullif(vehicle_class, '')::text as vehicle_class,
                nullif(series, '')::text as series,
                nullif(condition_id, '')::text as condition_id,
                nullif(condition_name, '')::text as condition_name,
                nullif(primary_damage_id, '')::text as primary_damage_id,
                nullif(primary_damage, '')::text as primary_damage,
                nullif(secondary_damage_id, '')::text as secondary_damage_id,
                nullif(secondary_damage, '')::text as secondary_damage,
                nullif(loss_type, '')::text as loss_type,
                nullif(run_condition_value, '')::text as run_condition_value,
                nullif(run_condition_label, '')::text as run_condition_label,
                nullif(run_condition_class_hint, '')::text as run_condition_class_hint,
                nullif(has_key, '')::boolean as has_key,
                nullif(airbags, '')::text as airbags,
                nullif(restraint_system, '')::text as restraint_system,
                nullif(odometer_miles, '')::numeric(14,1) as odometer_miles,
                nullif(odometer_km, '')::numeric(14,1) as odometer_km,
                nullif(odometer_status, '')::text as odometer_status,
                nullif(title_id, '')::text as title_id,
                nullif(title_type, '')::text as title_type,
                nullif(detailed_title_id, '')::text as detailed_title_id,
                nullif(detailed_title, '')::text as detailed_title,
                nullif(title_group, '')::text as title_group,
                nullif(title_pending, '')::boolean as title_pending,
                nullif(title_export, '')::boolean as title_export,
                nullif(title_registration, '')::boolean as title_registration,
                nullif(title_page_id, '')::text as title_page_id,
                nullif(title_brand, '')::text as title_brand,
                nullif(title_notes, '')::text as title_notes,
                nullif(special_note, '')::text as special_note,
                nullif(announcements, '')::text as announcements,
                nullif(seller_name, '')::text as seller_name,
                nullif(seller_type_id, '')::text as seller_type_id,
                nullif(seller_type, '')::text as seller_type,
                nullif(seller_class, '')::text as seller_class,
                nullif(seller_text_class, '')::text as seller_text_class,
                nullif(seller_is_insurance, '')::boolean as seller_is_insurance,
                nullif(seller_is_rental, '')::boolean as seller_is_rental,
                nullif(seller_is_credit_company, '')::boolean as seller_is_credit_company,
                nullif(seller_classification_confidence, '')::numeric(6,5) as seller_classification_confidence,
                nullif(seller_needs_review, '')::boolean as seller_needs_review,
                nullif(seller_classification_evidence, '')::text as seller_classification_evidence,
                nullif(seller_taxonomy_version, '')::text as seller_taxonomy_version,
                nullif(auction_state, '')::text as auction_state,
                nullif(lot_status_id, '')::text as lot_status_id,
                nullif(lot_status, '')::text as lot_status,
                nullif(lot_sub_status, '')::text as lot_sub_status,
                nullif(auction_at, '')::timestamptz as auction_at,
                nullif(auction_at_updated_at, '')::timestamptz as auction_at_updated_at,
                nullif(archived_at, '')::timestamptz as archived_at,
                nullif(is_buy_now, '')::boolean as is_buy_now,
                nullif(is_timed, '')::boolean as is_timed,
                nullif(current_bid_usd, '')::numeric(14,2) as current_bid_usd,
                nullif(current_bid_updated_at, '')::timestamptz as current_bid_updated_at,
                nullif(pre_bid_usd, '')::numeric(14,2) as pre_bid_usd,
                nullif(buy_now_usd, '')::numeric(14,2) as buy_now_usd,
                nullif(buy_now_updated_at, '')::timestamptz as buy_now_updated_at,
                nullif(final_bid_usd, '')::numeric(14,2) as final_bid_usd,
                nullif(final_bid_updated_at, '')::timestamptz as final_bid_updated_at,
                nullif(sale_price_usd, '')::numeric(14,2) as sale_price_usd,
                nullif(sale_price_updated_at, '')::timestamptz as sale_price_updated_at,
                nullif(provider_estimate_from_usd, '')::numeric(14,2) as provider_estimate_from_usd,
                nullif(provider_estimate_to_usd, '')::numeric(14,2) as provider_estimate_to_usd,
                nullif(provider_estimate_text, '')::text as provider_estimate_text,
                nullif(actual_cash_value_usd, '')::numeric(14,2) as actual_cash_value_usd,
                nullif(estimated_repair_cost_usd, '')::numeric(14,2) as estimated_repair_cost_usd,
                nullif(location_display, '')::text as location_display,
                nullif(location_city, '')::text as location_city,
                nullif(location_state, '')::text as location_state,
                nullif(facility_id, '')::text as facility_id,
                nullif(facility_office_name, '')::text as facility_office_name,
                nullif(facility_zip, '')::text as facility_zip,
                nullif(send_from, '')::text as send_from,
                nullif(lane, '')::text as lane,
                nullif(aisle, '')::text as aisle,
                coalesce(nullif(media_photos_count, '')::integer, 0) as media_photos_count,
                coalesce(nullif(media_has_photos, '')::boolean, false) as media_has_photos,
                nullif(media_has_360, '')::boolean as media_has_360,
                nullif(media_has_video, '')::boolean as media_has_video,
                nullif(source_created_at, '')::timestamptz as source_created_at,
                nullif(source_updated_at, '')::timestamptz as source_updated_at,
                identity_hash, spec_hash, condition_hash, auction_hash, seller_location_hash, media_hash,
                score_input_hash, search_hash,
                nullif(observed_at, '')::timestamptz as observed_at,
                coalesce(nullif(media_complete, '')::boolean, false) as media_complete
            from inventory_v2_stage;

            create temp table inventory_v2_actions on commit drop as
            select
                s.*,
                case
                    when current.platform is null then 'created'
                    when coalesce(s.source_updated_at, s.observed_at) < coalesce(current.source_updated_at, current.last_seen_at) then 'stale'
                    when not current.is_active
                      or current.identity_hash is distinct from s.identity_hash
                      or current.spec_hash is distinct from s.spec_hash
                      or current.condition_hash is distinct from s.condition_hash
                      or current.auction_hash is distinct from s.auction_hash
                      or current.seller_location_hash is distinct from s.seller_location_hash
                      or (s.media_complete and current.media_hash is distinct from s.media_hash)
                      or current.score_input_hash is distinct from s.score_input_hash
                      or current.search_hash is distinct from s.search_hash then 'updated'
                    else 'unchanged'
                end as action,
                current.platform is not null as existed,
                s.media_complete and (current.platform is null or current.media_hash is distinct from s.media_hash) as media_changed
            from inventory_v2_typed s
            left join inventory_current_v2 current
              on current.platform = s.platform and current.lot_number = s.lot_number;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<InventoryV2ActionCounts> ReadInventoryV2ActionCountsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select
                count(*) filter (where action = 'created')::integer,
                count(*) filter (where action = 'updated')::integer,
                count(*) filter (where action = 'unchanged')::integer,
                count(*) filter (where action = 'stale')::integer
            from inventory_v2_actions;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Inventory V2 action classification returned no result.");
        return new InventoryV2ActionCounts(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2), reader.GetInt32(3));
    }

    private async Task<int> MergeInventoryV2Async(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var insertColumns = string.Join(", ", InventoryV2TargetColumns);
        var selectColumns = string.Join(", ", InventoryV2TargetColumns.Select(static column => $"source.{column}"));
        var updateColumns = InventoryV2TargetColumns
            .Where(static column => column is not ("platform" or "lot_number" or "lot_key" or "media_photos_count" or "media_has_photos" or "media_has_360" or "media_has_video" or "media_hash"))
            .Select(static column => $"{column} = coalesce(source.{column}, current.{column})");
        var updateSet = string.Join(",\n                ", updateColumns);

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
        command.CommandText = $"""
            insert into inventory_current_v2 (
                {insertColumns}, is_active, consecutive_misses, first_seen_at, last_seen_at, record_version)
            select
                {selectColumns}, true, 0, source.observed_at, source.observed_at, 1
            from inventory_v2_actions source
            where source.action = 'created'
            on conflict (platform, lot_number) do nothing;

            update inventory_current_v2 current
            set {updateSet},
                media_photos_count = case when source.media_complete then source.media_photos_count else current.media_photos_count end,
                media_has_photos = case when source.media_complete then source.media_has_photos else current.media_has_photos end,
                media_has_360 = case when source.media_complete then source.media_has_360 else current.media_has_360 end,
                media_has_video = case when source.media_complete then source.media_has_video else current.media_has_video end,
                media_hash = case when source.media_complete then source.media_hash else current.media_hash end,
                is_active = true,
                consecutive_misses = 0,
                last_seen_at = greatest(current.last_seen_at, source.observed_at),
                deactivated_at = null,
                record_version = current.record_version + 1,
                updated_at = now()
            from inventory_v2_actions source
            where source.action = 'updated'
              and current.platform = source.platform
              and current.lot_number = source.lot_number;

            update inventory_current_v2 current
            set last_seen_at = greatest(current.last_seen_at, source.observed_at)
            from inventory_v2_actions source
            where source.action = 'unchanged'
              and current.platform = source.platform
              and current.lot_number = source.lot_number;

            update inventory_current_v2 current
            set is_active = true,
                consecutive_misses = 0,
                last_seen_at = greatest(current.last_seen_at, source.observed_at),
                deactivated_at = null,
                updated_at = case
                    when not current.is_active or current.consecutive_misses <> 0 or current.deactivated_at is not null then now()
                    else current.updated_at
                end
            from inventory_v2_actions source
            where source.action = 'stale'
              and current.platform = source.platform
              and current.lot_number = source.lot_number;

            delete from inventory_media_current_v2 media
            using inventory_v2_actions source
            where source.media_changed
              and source.action <> 'stale'
              and media.platform = source.platform
              and media.lot_number = source.lot_number;

            insert into inventory_media_current_v2 (
                platform, lot_number, media_type, position, source_url,
                thumbnail_url, large_url, observed_at, updated_at)
            select media.platform, media.lot_number, media.media_type, media.position, media.source_url,
                   media.thumbnail_url, media.large_url, media.observed_at, now()
            from inventory_v2_media_stage media
            join inventory_v2_actions source
              on source.platform = media.platform and source.lot_number = media.lot_number
            where source.media_changed and source.action <> 'stale'
            on conflict (platform, lot_number, media_type, position) do update set
                source_url = excluded.source_url,
                thumbnail_url = excluded.thumbnail_url,
                large_url = excluded.large_url,
                observed_at = excluded.observed_at,
                updated_at = now();
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        await using var count = connection.CreateCommand();
        count.Transaction = transaction;
        count.CommandTimeout = _persistence.CommandTimeoutSeconds;
        count.CommandText = """
            select count(*)::integer
            from inventory_v2_media_stage media
            join inventory_v2_actions source
              on source.platform = media.platform and source.lot_number = media.lot_number
            where source.media_changed and source.action <> 'stale';
            """;
        return (int)(await count.ExecuteScalarAsync(cancellationToken) ?? 0);
    }

    internal static PreparedInventoryV2Lot? PrepareInventoryV2Lot(InventoryV2BatchItem item)
    {
        var vehicle = item.Vehicle;
        var platform = NormalizeText(vehicle.Platform)?.ToLowerInvariant();
        var lotNumber = NormalizeText(vehicle.LotNumber);
        if (platform is null || lotNumber is null) return null;

        var rawRoot = vehicle.RawSource;
        var rawLot = FindRawLot(rawRoot, lotNumber);
        var media = PrepareInventoryV2Media(platform, lotNumber, vehicle.Media, item.ObservedAt);
        var sourceProvider = NormalizeText(vehicle.SourceProvider) ?? "auctionsapi";
        var mediaComplete = HasRawProperty(rawLot, "images")
            || HasRawProperty(rawRoot, "images")
            || (!sourceProvider.Equals("auctionsapi", StringComparison.OrdinalIgnoreCase) && vehicle.Media is not null);
        var sourceUpdatedAt = RawDate(rawLot, "updated_at") ?? RawDate(rawRoot, "updated_at");

        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["platform"] = platform,
            ["lot_number"] = lotNumber,
            ["lot_key"] = $"{platform}:{lotNumber}",
            ["source_provider"] = sourceProvider,
            ["source_contract_version"] = "auctionsapi-v2",
            ["domain_id"] = RawText(rawLot, "domain.id", "domain_id") ?? RawText(rawRoot, "domain.id", "domain_id"),
            ["provider_vehicle_id"] = RawText(rawRoot, "id", "car_id"),
            ["provider_lot_id"] = RawText(rawLot, "external_id", "id", "lot_id"),
            ["vin"] = NormalizeText(vehicle.Vin),
            ["year"] = Invariant(vehicle.Year),
            ["manufacturer_id"] = RawText(rawRoot, "manufacturer.id"),
            ["make"] = NormalizeText(vehicle.Make),
            ["model_id"] = RawText(rawRoot, "model.id"),
            ["model"] = NormalizeText(vehicle.Model),
            ["generation_id"] = RawText(rawRoot, "generation.id"),
            ["generation"] = RawText(rawRoot, "generation.name", "generation.label"),
            ["vehicle_type_id"] = RawText(rawRoot, "vehicle_type.id"),
            ["vehicle_type"] = NormalizeText(vehicle.VehicleType),
            ["body_type_id"] = RawText(rawRoot, "body_type.id"),
            ["body_style"] = NormalizeText(vehicle.VehicleSpecs?.BodyStyle ?? vehicle.Details?.VehicleDescription?.BodyStyle),
            ["fuel_id"] = RawText(rawRoot, "fuel.id"),
            ["fuel_type"] = NormalizeText(vehicle.FuelType ?? vehicle.VehicleSpecs?.FuelType),
            ["engine_id"] = RawText(rawRoot, "engine.id"),
            ["engine"] = NormalizeText(vehicle.VehicleSpecs?.Engine?.Raw ?? vehicle.VehicleSpecs?.Engine?.Layout),
            ["engine_size_liters"] = InvariantDecimal(vehicle.VehicleSpecs?.Engine?.SizeLiters),
            ["horsepower"] = Invariant(vehicle.VehicleSpecs?.Engine?.Horsepower),
            ["cylinders"] = InvariantInteger(vehicle.Details?.VehicleDescription?.Cylinders),
            ["transmission_id"] = RawText(rawRoot, "transmission.id"),
            ["transmission"] = NormalizeText(vehicle.Transmission ?? vehicle.VehicleSpecs?.Transmission),
            ["drive_id"] = RawText(rawRoot, "drive_wheel.id", "drive.id"),
            ["drive_type"] = NormalizeText(vehicle.DriveType ?? vehicle.VehicleSpecs?.DriveType),
            ["exterior_color"] = NormalizeText(vehicle.Color ?? vehicle.VehicleSpecs?.ExteriorColor),
            ["manufactured_in"] = NormalizeText(vehicle.Details?.VehicleDescription?.ManufacturedIn),
            ["vehicle_class"] = NormalizeText(vehicle.Details?.VehicleDescription?.VehicleClass),
            ["series"] = NormalizeText(vehicle.Details?.VehicleDescription?.Series),
            ["condition_id"] = RawText(rawLot, "condition.id"),
            ["condition_name"] = RawText(rawLot, "condition.name", "condition.label"),
            ["primary_damage_id"] = RawText(rawLot, "damage.main.id"),
            ["primary_damage"] = NormalizeText(vehicle.Condition?.PrimaryDamage ?? vehicle.Damage),
            ["secondary_damage_id"] = RawText(rawLot, "damage.second.id"),
            ["secondary_damage"] = NormalizeText(vehicle.Condition?.SecondaryDamage),
            ["loss_type"] = NormalizeText(vehicle.Condition?.Loss),
            ["run_condition_value"] = NormalizeText(vehicle.Condition?.RunCondition?.Value),
            ["run_condition_label"] = NormalizeText(vehicle.Condition?.RunCondition?.Label),
            ["run_condition_class_hint"] = NormalizeText(vehicle.Condition?.RunCondition?.ClassHint),
            ["has_key"] = Invariant(vehicle.Condition?.HasKey),
            ["airbags"] = NormalizeText(vehicle.VehicleSpecs?.Airbags),
            ["restraint_system"] = NormalizeText(vehicle.VehicleSpecs?.RestraintSystem),
            ["odometer_miles"] = Invariant(vehicle.OdometerInfo?.Miles),
            ["odometer_km"] = Invariant(vehicle.OdometerInfo?.Kilometers),
            ["odometer_status"] = NormalizeText(vehicle.OdometerInfo?.Status),
            ["title_id"] = RawText(rawLot, "title.id"),
            ["title_type"] = TitleFacetCategory.Classify(vehicle),
            ["detailed_title_id"] = RawText(rawLot, "detailed_title.id"),
            ["detailed_title"] = NormalizeText(vehicle.SaleDocument?.Name ?? vehicle.Title),
            ["title_group"] = NormalizeText(vehicle.SaleDocument?.Group),
            ["title_pending"] = Invariant(vehicle.SaleDocument?.IsPending),
            ["title_export"] = Invariant(vehicle.SaleDocument?.Export),
            ["title_registration"] = Invariant(vehicle.SaleDocument?.Registration),
            ["title_page_id"] = NormalizeText(vehicle.SaleDocument?.PageId),
            ["title_brand"] = NormalizeText(vehicle.Details?.VehicleInformation?.TitleBrand),
            ["title_notes"] = Flatten(vehicle.TitleNotes) ?? NormalizeText(vehicle.Details?.VehicleInformation?.TitleNotes),
            ["special_note"] = Flatten(vehicle.SpecialNote),
            ["announcements"] = Flatten(vehicle.Announcements),
            ["seller_name"] = NormalizeText(
                vehicle.Seller?.Name
                ?? vehicle.Details?.SaleInformation?.Seller
                ?? RawText(rawLot, "seller_name", "seller.name")
                ?? RawText(rawRoot, "seller_name", "seller.name")),
            ["seller_type_id"] = RawText(rawLot, "seller_type.id"),
            ["seller_type"] = NormalizeText(vehicle.Seller?.Type ?? vehicle.Seller?.RawType ?? vehicle.Details?.SaleInformation?.SellerType),
            ["seller_class"] = NormalizeText(vehicle.Seller?.Class),
            ["seller_text_class"] = NormalizeText(vehicle.Seller?.TextClass),
            ["seller_is_insurance"] = Invariant(RawBool(rawLot, "seller.is_insurance", "is_insurance")),
            ["seller_is_rental"] = Invariant(RawBool(rawLot, "seller.is_rental", "is_rental")),
            ["seller_is_credit_company"] = Invariant(RawBool(rawLot, "seller.is_credit_company", "is_credit_company")),
            ["seller_classification_confidence"] = Invariant(vehicle.Seller?.ClassificationConfidence),
            ["seller_needs_review"] = Invariant(vehicle.Seller?.NeedsReview),
            ["seller_classification_evidence"] = NormalizeText(vehicle.Seller?.ClassificationEvidence),
            ["seller_taxonomy_version"] = NormalizeText(vehicle.Seller?.TaxonomyVersion),
            ["auction_state"] = NormalizeText(vehicle.Auction?.State),
            ["lot_status_id"] = RawText(rawLot, "status.id"),
            ["lot_status"] = NormalizeText(vehicle.Auction?.LotStatus),
            ["lot_sub_status"] = NormalizeText(vehicle.Auction?.LotSubStatus),
            ["auction_at"] = Invariant(vehicle.Auction?.AuctionAt),
            ["auction_at_updated_at"] = Invariant(RawDate(rawLot, "sale_date.updated_at")),
            ["archived_at"] = Invariant(RawDate(rawLot, "archived_at")),
            ["is_buy_now"] = Invariant(vehicle.Pricing?.BuyNowUsd is > 0m),
            ["is_timed"] = Invariant(vehicle.Auction?.IsTimed),
            ["current_bid_usd"] = Invariant(vehicle.Pricing?.CurrentBidUsd),
            ["current_bid_updated_at"] = Invariant(RawDate(rawLot, "bid.updated_at")),
            ["pre_bid_usd"] = Invariant(vehicle.Pricing?.PreBidUsd),
            ["buy_now_usd"] = Invariant(vehicle.Pricing?.BuyNowUsd),
            ["buy_now_updated_at"] = Invariant(RawDate(rawLot, "buy_now.updated_at")),
            ["final_bid_usd"] = Invariant(RawDecimal(rawLot, "final_bid.value", "final_bid")),
            ["final_bid_updated_at"] = Invariant(RawDate(rawLot, "final_bid.updated_at")),
            ["sale_price_usd"] = Invariant(vehicle.Pricing?.SalePriceUsd),
            ["sale_price_updated_at"] = Invariant(RawDate(rawLot, "sale_price.updated_at")),
            ["provider_estimate_from_usd"] = Invariant(vehicle.Pricing?.EstimatedCost?.FromUsd),
            ["provider_estimate_to_usd"] = Invariant(vehicle.Pricing?.EstimatedCost?.ToUsd),
            ["provider_estimate_text"] = NormalizeText(vehicle.Pricing?.EstimatedCost?.Text),
            ["actual_cash_value_usd"] = InvariantDecimal(vehicle.Details?.SaleInformation?.ActualCashValue),
            ["estimated_repair_cost_usd"] = InvariantDecimal(vehicle.Details?.SaleInformation?.EstimatedRepairCost),
            ["location_display"] = NormalizeText(vehicle.Location?.Display),
            ["location_city"] = NormalizeText(vehicle.Location?.City),
            ["location_state"] = NormalizeText(vehicle.Location?.State ?? vehicle.Facility?.State),
            ["facility_id"] = NormalizeText(vehicle.Location?.FacilityId ?? vehicle.Facility?.Id),
            ["facility_office_name"] = NormalizeText(vehicle.Facility?.OfficeName),
            ["facility_zip"] = NormalizeText(vehicle.Facility?.Zip),
            ["send_from"] = NormalizeText(vehicle.Location?.SendFrom),
            ["lane"] = NormalizeText(vehicle.Details?.SaleInformation?.Lane),
            ["aisle"] = NormalizeText(vehicle.Details?.SaleInformation?.Aisle),
            ["media_photos_count"] = Invariant(media.Count),
            ["media_has_photos"] = Invariant(media.Count > 0),
            ["media_has_360"] = Invariant(vehicle.Media?.Has360),
            ["media_has_video"] = Invariant(vehicle.Media?.HasVideo),
            ["source_created_at"] = Invariant(RawDate(rawLot, "created_at") ?? RawDate(rawRoot, "created_at")),
            ["source_updated_at"] = Invariant(sourceUpdatedAt),
            ["observed_at"] = Invariant(item.ObservedAt),
            ["media_complete"] = Invariant(mediaComplete),
        };

        values["identity_hash"] = StableHash(values, "platform", "lot_number", "domain_id", "provider_vehicle_id", "provider_lot_id", "vin", "year", "manufacturer_id", "make", "model_id", "model", "generation_id", "generation");
        values["spec_hash"] = StableHash(values, "vehicle_type_id", "vehicle_type", "body_type_id", "body_style", "fuel_id", "fuel_type", "engine_id", "engine", "engine_size_liters", "horsepower", "cylinders", "transmission_id", "transmission", "drive_id", "drive_type", "exterior_color", "manufactured_in", "vehicle_class", "series");
        values["condition_hash"] = StableHash(values, "condition_id", "condition_name", "primary_damage_id", "primary_damage", "secondary_damage_id", "secondary_damage", "loss_type", "run_condition_value", "run_condition_label", "run_condition_class_hint", "has_key", "airbags", "restraint_system", "odometer_miles", "odometer_km", "odometer_status", "title_id", "title_type", "detailed_title_id", "detailed_title", "title_group", "title_pending", "title_export", "title_registration", "title_page_id", "title_brand", "title_notes", "special_note", "announcements");
        values["auction_hash"] = StableHash(values, "auction_state", "lot_status_id", "lot_status", "lot_sub_status", "auction_at", "archived_at", "is_buy_now", "is_timed", "current_bid_usd", "pre_bid_usd", "buy_now_usd", "final_bid_usd", "sale_price_usd", "provider_estimate_from_usd", "provider_estimate_to_usd", "provider_estimate_text", "actual_cash_value_usd", "estimated_repair_cost_usd");
        values["seller_location_hash"] = StableHash(values, "seller_name", "seller_type_id", "seller_type", "seller_class", "seller_text_class", "seller_is_insurance", "seller_is_rental", "seller_is_credit_company", "seller_classification_confidence", "seller_needs_review", "seller_classification_evidence", "seller_taxonomy_version", "location_display", "location_city", "location_state", "facility_id", "facility_office_name", "facility_zip", "send_from", "lane", "aisle");
        values["media_hash"] = StableHash(media.Select(static item => $"{item.MediaType}\u001f{item.Position}\u001f{item.SourceUrl}\u001f{item.ThumbnailUrl}\u001f{item.LargeUrl}"));
        values["score_input_hash"] = StableHash(values, "year", "make", "model", "vehicle_type", "odometer_miles", "odometer_status", "title_type", "title_group", "primary_damage", "secondary_damage", "loss_type", "run_condition_value", "has_key", "seller_class", "seller_is_insurance", "actual_cash_value_usd", "estimated_repair_cost_usd", "location_state");
        values["search_hash"] = StableHash(values, "vin", "year", "make", "model", "vehicle_type", "body_style", "fuel_type", "transmission", "drive_type", "exterior_color", "odometer_miles", "title_type", "title_group", "primary_damage", "secondary_damage", "run_condition_value", "seller_name", "seller_type", "seller_class", "auction_state", "auction_at", "lot_status", "lot_sub_status", "current_bid_usd", "buy_now_usd", "location_display", "location_city", "location_state", "facility_id", "media_has_photos", "media_has_360");

        return new PreparedInventoryV2Lot(platform, lotNumber, item.ObservedAt, values, media);
    }

    private static IReadOnlyList<InventoryV2PreparedMedia> PrepareInventoryV2Media(
        string platform,
        string lotNumber,
        MediaInfo? media,
        DateTimeOffset observedAt)
    {
        var result = new List<InventoryV2PreparedMedia>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        foreach (var url in media?.Photos ?? [])
        {
            var normalized = NormalizeUrl(url);
            if (normalized is null || !seen.Add(normalized)) continue;
            result.Add(new(platform, lotNumber, "photo", position++, normalized, null, normalized, observedAt));
        }
        foreach (var item in media?.Items ?? [])
        {
            var source = NormalizeUrl(item.Large) ?? NormalizeUrl(item.Thumb);
            if (source is null || !seen.Add(source)) continue;
            result.Add(new(platform, lotNumber, NormalizeText(item.Type)?.ToLowerInvariant() ?? "photo", position++, source, NormalizeUrl(item.Thumb), NormalizeUrl(item.Large), observedAt));
        }
        return result;
    }

    private static JsonElement? FindRawLot(JsonElement? rawRoot, string lotNumber)
    {
        if (rawRoot is null || rawRoot.Value.ValueKind != JsonValueKind.Object) return rawRoot;
        if (rawRoot.Value.TryGetProperty("lot", out var legacyLot) && legacyLot.ValueKind == JsonValueKind.Object)
            return legacyLot;
        if (rawRoot.Value.TryGetProperty("lots", out var lots) && lots.ValueKind == JsonValueKind.Array)
        {
            foreach (var lot in lots.EnumerateArray())
            {
                if (string.Equals(RawText(lot, "lot", "lot_number", "external_id", "id"), lotNumber, StringComparison.OrdinalIgnoreCase))
                    return lot.Clone();
            }
        }
        return rawRoot.Value.Clone();
    }

    private static bool HasRawProperty(JsonElement? value, string property) =>
        value is { ValueKind: JsonValueKind.Object } && value.Value.TryGetProperty(property, out _);

    private static string? RawText(JsonElement? value, params string[] paths)
    {
        if (value is null) return null;
        foreach (var path in paths)
        {
            var current = RawAt(value.Value, path);
            if (current is null || current.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
            var text = current.Value.ValueKind switch
            {
                JsonValueKind.String => current.Value.GetString(),
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => current.Value.ToString(),
                JsonValueKind.Object => RawText(current, "name", "label", "value", "id", "code"),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text)) return text.Trim();
        }
        return null;
    }

    private static decimal? RawDecimal(JsonElement? value, params string[] paths)
    {
        var text = RawText(value, paths);
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static bool? RawBool(JsonElement? value, params string[] paths)
    {
        var text = RawText(value, paths);
        if (bool.TryParse(text, out var result)) return result;
        return text?.Trim().ToUpperInvariant() switch
        {
            "YES" or "Y" or "AVAILABLE" or "PRESENT" or "INTACT" => true,
            "NO" or "N" or "NONE" or "MISSING" or "NOT AVAILABLE" => false,
            _ => null,
        };
    }

    private static DateTimeOffset? RawDate(JsonElement? value, string path)
    {
        var text = RawText(value, path);
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var result) ? result : null;
    }

    private static JsonElement? RawAt(JsonElement value, string path)
    {
        foreach (var segment in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return null;
        }
        return value;
    }

    private static string? NormalizeText(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NormalizeUrl(string? value) =>
        Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.AbsoluteUri
            : null;

    private static string? Flatten(JsonElement? value)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        return NormalizeText(value.Value.ValueKind == JsonValueKind.String ? value.Value.GetString() : value.Value.ToString());
    }

    private static string? Invariant(bool? value) => value?.ToString().ToLowerInvariant();
    private static string? Invariant(int? value) => value?.ToString(CultureInfo.InvariantCulture);
    private static string? Invariant(decimal? value) => value?.ToString("0.################", CultureInfo.InvariantCulture);
    private static string? Invariant(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static string? InvariantInteger(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : null;

    private static string? InvariantDecimal(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = new string(value.Where(static character => char.IsDigit(character) || character is '.' or '-').ToArray());
        return decimal.TryParse(cleaned, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)
            ? Invariant(parsed)
            : null;
    }

    private static string StableHash(IReadOnlyDictionary<string, string?> values, params string[] columns) =>
        StableHash(columns.Select(column => values.GetValueOrDefault(column)));

    private static string StableHash(IEnumerable<string?> values)
    {
        var canonical = string.Join('\u001f', values.Select(static value => value?.Trim() ?? string.Empty));
        return Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    internal sealed record PreparedInventoryV2Lot(
        string Platform,
        string LotNumber,
        DateTimeOffset ObservedAt,
        IReadOnlyDictionary<string, string?> Values,
        IReadOnlyList<InventoryV2PreparedMedia> Media);

    internal sealed record InventoryV2PreparedMedia(
        string Platform,
        string LotNumber,
        string MediaType,
        int Position,
        string SourceUrl,
        string? ThumbnailUrl,
        string? LargeUrl,
        DateTimeOffset ObservedAt);

    private sealed record InventoryV2ActionCounts(int Created, int Updated, int Unchanged, int Stale);
}
