using System.Globalization;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    private async Task EnsureInventoryV2SchemaAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Min(_persistence.CommandTimeoutSeconds, 5);
        command.CommandText = "select to_regclass('public.inventory_current_v2') is not null and to_regclass('public.inventory_media_current_v2') is not null;";
        if (await command.ExecuteScalarAsync(cancellationToken) is not true)
            throw new InvalidOperationException("Inventory V2 schema is not available.");
    }

    private async Task<bool> IsInventoryV2ReaderEnabledAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Min(_persistence.CommandTimeoutSeconds, 5);
        command.CommandText = "select reader_enabled and writer_enabled from inventory_v2_schema_state where schema_name = 'inventory-current-v2' and schema_version >= @schema_version;";
        AddParameter(command, "schema_version", InventoryV2SchemaVersion);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    private async Task<InventorySearchSummary> GetInventorySearchSummaryV2Async(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        var facets = await GetInventoryFacetsV2Async(new InventoryFacetsV2Request(request, InventoryFacetsV2Groups.Core), cancellationToken);
        return new InventorySearchSummary(facets.Total, facets.AsOf, facets.Facets);
    }

    private async Task<InventorySearchPage> SearchInventoryV2Async(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);
        var offset = checked((page - 1) * pageSize);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var where = new List<string> { "latest.is_active" };
        await using var countCommand = connection.CreateCommand();
        countCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
        AddInventoryV2ReaderFilters(countCommand, request, where);
        countCommand.CommandText = $"select count(*)::int from inventory_current_v2 latest left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key where {string.Join(" and ", where)};";
        var total = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

        await using var itemsCommand = connection.CreateCommand();
        itemsCommand.CommandTimeout = _persistence.CommandTimeoutSeconds;
        var itemWhere = new List<string> { "latest.is_active" };
        AddInventoryV2ReaderFilters(itemsCommand, request, itemWhere);
        itemsCommand.CommandText = $"""
            select latest.*, latest.last_seen_at as observed_at, score.status as score_status, score.pre_grade as score_pre_grade,
                   score.buy_score as score_buy_score, score.max_points_evaluable as score_max_points_evaluable,
                   score.coverage_percent as score_coverage_percent, score.confidence_percent as score_confidence_percent,
                   score.category as score_category, score.policy_version as score_policy_version, score.scored_at as score_scored_at
            from inventory_current_v2 latest
            left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
            where {string.Join(" and ", itemWhere)}
            order by {GetInventoryV2ReaderOrdering(request.Sort)}, latest.lot_key asc
            limit @limit offset @offset;
            """;
        AddParameter(itemsCommand, "limit", pageSize);
        AddParameter(itemsCommand, "offset", offset);
        var rows = await ReadInventoryV2RowsAsync(itemsCommand, cancellationToken);
        await AttachInventoryV2MediaAsync(connection, rows, cancellationToken);
        var generatedAt = rows.Count == 0 ? DateTimeOffset.UtcNow : rows.Max(row => row.ObservedAt);
        return new InventorySearchPage(page, pageSize, total, generatedAt, rows.Select(ToStoredInventoryV2Snapshot).ToArray());
    }

    private async Task<IReadOnlyCollection<StoredVehicleSnapshot>> GetRecentInventoryV2Async(int maximum, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maximum, 1, 5000);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select latest.*, latest.last_seen_at as observed_at, score.status as score_status, score.pre_grade as score_pre_grade,
                   score.buy_score as score_buy_score, score.max_points_evaluable as score_max_points_evaluable,
                   score.coverage_percent as score_coverage_percent, score.confidence_percent as score_confidence_percent,
                   score.category as score_category, score.policy_version as score_policy_version, score.scored_at as score_scored_at
            from inventory_current_v2 latest
            left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
            where latest.is_active
            order by latest.last_seen_at desc nulls last, latest.lot_key asc
            limit @limit;
            """;
        AddParameter(command, "limit", limit);
        var rows = await ReadInventoryV2RowsAsync(command, cancellationToken);
        await AttachInventoryV2MediaAsync(connection, rows, cancellationToken);
        return rows.Select(ToStoredInventoryV2Snapshot).ToArray();
    }

    private async Task<StoredVehicleSnapshot?> GetByLotKeyInventoryV2Async(string lotKey, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select latest.*, latest.last_seen_at as observed_at, score.status as score_status, score.pre_grade as score_pre_grade,
                   score.buy_score as score_buy_score, score.max_points_evaluable as score_max_points_evaluable,
                   score.coverage_percent as score_coverage_percent, score.confidence_percent as score_confidence_percent,
                   score.category as score_category, score.policy_version as score_policy_version, score.scored_at as score_scored_at
            from inventory_current_v2 latest
            left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
            where latest.lot_key = @lot_key and latest.is_active
            limit 1;
            """;
        AddParameter(command, "lot_key", lotKey);
        var rows = await ReadInventoryV2RowsAsync(command, cancellationToken);
        if (rows.Count == 0) return null;
        await AttachInventoryV2MediaAsync(connection, rows, cancellationToken);
        return ToStoredInventoryV2Snapshot(rows[0]);
    }

    private async Task<StoredVehicleSnapshot?> GetByLotInventoryV2Async(string lotNumber, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select latest.*, latest.last_seen_at as observed_at, score.status as score_status, score.pre_grade as score_pre_grade,
                   score.buy_score as score_buy_score, score.max_points_evaluable as score_max_points_evaluable,
                   score.coverage_percent as score_coverage_percent, score.confidence_percent as score_confidence_percent,
                   score.category as score_category, score.policy_version as score_policy_version, score.scored_at as score_scored_at
            from inventory_current_v2 latest
            left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
            where latest.lot_number = @lot_number and latest.is_active
            limit 1;
            """;
        AddParameter(command, "lot_number", lotNumber.Trim());
        var rows = await ReadInventoryV2RowsAsync(command, cancellationToken);
        if (rows.Count == 0) return null;
        await AttachInventoryV2MediaAsync(connection, rows, cancellationToken);
        return ToStoredInventoryV2Snapshot(rows[0]);
    }

    private async Task<StoredVehicleSnapshot?> GetByPlatformAndLotInventoryV2Async(string platform, string lotNumber, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select latest.*, latest.last_seen_at as observed_at, score.status as score_status, score.pre_grade as score_pre_grade,
                   score.buy_score as score_buy_score, score.max_points_evaluable as score_max_points_evaluable,
                   score.coverage_percent as score_coverage_percent, score.confidence_percent as score_confidence_percent,
                   score.category as score_category, score.policy_version as score_policy_version, score.scored_at as score_scored_at
            from inventory_current_v2 latest
            left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key
            where latest.platform = @platform and latest.lot_number = @lot_number and latest.is_active
            limit 1;
            """;
        AddParameter(command, "platform", platform.Trim().ToLowerInvariant());
        AddParameter(command, "lot_number", lotNumber.Trim());
        var rows = await ReadInventoryV2RowsAsync(command, cancellationToken);
        if (rows.Count == 0) return null;
        await AttachInventoryV2MediaAsync(connection, rows, cancellationToken);
        return ToStoredInventoryV2Snapshot(rows[0]);
    }

    private static void AddInventoryV2ReaderFilters(NpgsqlCommand command, InventorySearchRequest request, List<string> where)
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
            where.Add("(to_tsvector('simple', concat_ws(' ', latest.lot_number, latest.vin, latest.make, latest.model, latest.title_type, latest.primary_damage, latest.seller_name)) @@ websearch_to_tsquery('simple', @v2_search_query) or latest.lot_number ilike @v2_query_like or latest.vin ilike @v2_query_like)");
            AddParameter(command, "v2_search_query", request.Query.Trim());
            AddParameter(command, "v2_query_like", $"%{request.Query.Trim()}%");
        }
        if (!string.IsNullOrWhiteSpace(request.Platform)) { where.Add("latest.platform = @v2_platform"); AddParameter(command, "v2_platform", request.Platform.Trim().ToLowerInvariant()); }
        AddAny("v2_makes", request.Makes, "latest.make");
        AddAny("v2_models", request.Models, "latest.model");
        AddAny("v2_vehicle_types", request.VehicleTypes, "latest.vehicle_type");
        AddAny("v2_titles", request.Titles, "latest.title_type");
        AddAny("v2_title_categories", request.TitleCategories, "latest.title_type");
        AddAny("v2_states", request.States, "latest.location_state");
        AddAny("v2_facilities", request.Facilities, "latest.location_display");
        AddAny("v2_primary_damages", request.PrimaryDamages, "latest.primary_damage");
        AddAny("v2_secondary_damages", request.SecondaryDamages, "latest.secondary_damage");
        AddAny("v2_seller_types", request.SellerTypes, "latest.seller_type");
        AddAny("v2_engine_layouts", request.EngineLayouts, "latest.engine");
        AddAny("v2_cylinders", request.Cylinders, "latest.cylinders::text");
        AddAny("v2_transmissions", request.Transmissions, "latest.transmission");
        AddAny("v2_fuels", request.Fuels, "latest.fuel_type");
        AddAny("v2_drives", request.Drives, "latest.drive_type");
        AddAny("v2_body_styles", request.BodyStyles, "latest.body_style");
        AddAny("v2_colors", request.Colors, "latest.exterior_color");
        AddAny("v2_loss_types", request.LossTypes, "latest.loss_type");
        AddAny("v2_start_codes", request.StartCodes, "latest.run_condition_value");
        AddAny("v2_run_conditions", request.RunConditions, PublicRunConditionV2Sql("latest"));
        AddAny("v2_scoring_statuses", request.ScoringStatuses, "score.status");
        if (request.ExcludeSpecialTitles) where.Add("coalesce(latest.title_type, '') <> 'SPECIAL'");
        if (request.YearFrom.HasValue) { where.Add("latest.year >= @v2_year_from"); AddParameter(command, "v2_year_from", request.YearFrom.Value); }
        if (request.YearTo.HasValue) { where.Add("latest.year <= @v2_year_to"); AddParameter(command, "v2_year_to", request.YearTo.Value); }
        if (request.OdometerFrom.HasValue) { where.Add("latest.odometer_miles >= @v2_odometer_from"); AddParameter(command, "v2_odometer_from", request.OdometerFrom.Value); }
        if (request.OdometerTo.HasValue) { where.Add("latest.odometer_miles <= @v2_odometer_to"); AddParameter(command, "v2_odometer_to", request.OdometerTo.Value); }
        if (request.PriceFrom.HasValue) { where.Add("latest.current_bid_usd >= @v2_price_from"); AddParameter(command, "v2_price_from", request.PriceFrom.Value); }
        if (request.PriceTo.HasValue) { where.Add("latest.current_bid_usd <= @v2_price_to"); AddParameter(command, "v2_price_to", request.PriceTo.Value); }
        if (request.BuyNowOnly == true || request.BuyNowFrom.HasValue || request.BuyNowTo.HasValue) where.Add("latest.buy_now_usd > 0");
        if (request.BuyNowFrom.HasValue) { where.Add("latest.buy_now_usd >= @v2_buy_now_from"); AddParameter(command, "v2_buy_now_from", request.BuyNowFrom.Value); }
        if (request.BuyNowTo.HasValue) { where.Add("latest.buy_now_usd <= @v2_buy_now_to"); AddParameter(command, "v2_buy_now_to", request.BuyNowTo.Value); }
        if (request.MaxCurrentBid.HasValue) { where.Add("(latest.current_bid_usd is null or latest.current_bid_usd <= @v2_max_current_bid)"); AddParameter(command, "v2_max_current_bid", request.MaxCurrentBid.Value); }
        if (request.AuctionFrom.HasValue) { where.Add("latest.auction_at >= @v2_auction_from"); AddParameter(command, "v2_auction_from", request.AuctionFrom.Value); }
        if (request.AuctionTo.HasValue) { where.Add("latest.auction_at <= @v2_auction_to"); AddParameter(command, "v2_auction_to", request.AuctionTo.Value); }
        if (request.WithPhotosOnly == true) where.Add("latest.media_has_photos");
        if (request.WithBidOnly == true) where.Add("latest.current_bid_usd is not null");
        if (string.Equals(request.KeyMode, "with", StringComparison.OrdinalIgnoreCase)) where.Add("latest.has_key is true");
        if (string.Equals(request.KeyMode, "without", StringComparison.OrdinalIgnoreCase)) where.Add("latest.has_key is false");
        if (request.ProviderEstimateFrom.HasValue) { where.Add("latest.provider_estimate_to_usd >= @v2_provider_estimate_from"); AddParameter(command, "v2_provider_estimate_from", request.ProviderEstimateFrom.Value); }
        if (request.ProviderEstimateTo.HasValue) { where.Add("latest.provider_estimate_from_usd <= @v2_provider_estimate_to"); AddParameter(command, "v2_provider_estimate_to", request.ProviderEstimateTo.Value); }
        if (request.EngineSizeFrom.HasValue) { where.Add("latest.engine_size_liters >= @v2_engine_size_from"); AddParameter(command, "v2_engine_size_from", request.EngineSizeFrom.Value); }
        if (request.EngineSizeTo.HasValue) { where.Add("latest.engine_size_liters <= @v2_engine_size_to"); AddParameter(command, "v2_engine_size_to", request.EngineSizeTo.Value); }
        if (request.HorsepowerFrom.HasValue) { where.Add("latest.horsepower >= @v2_horsepower_from"); AddParameter(command, "v2_horsepower_from", request.HorsepowerFrom.Value); }
        if (request.HorsepowerTo.HasValue) { where.Add("latest.horsepower <= @v2_horsepower_to"); AddParameter(command, "v2_horsepower_to", request.HorsepowerTo.Value); }
        if (request.PreGradeFrom.HasValue) { where.Add("score.pre_grade >= @v2_pre_grade_from"); AddParameter(command, "v2_pre_grade_from", request.PreGradeFrom.Value); }
        if (string.Equals(request.AuctionStatus, "open", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like any(array['%open%', '%active%'])");
        if (string.Equals(request.AuctionStatus, "live", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like '%live%'");
        if (string.Equals(request.AuctionStatus, "finished", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like any(array['%finished%', '%ended%', '%sold%'])");
    }

    private static string GetInventoryV2ReaderOrdering(string? sort) => sort?.Trim().ToLowerInvariant() switch
    {
        "auction" => "score.pre_grade desc nulls last, latest.auction_at asc nulls last",
        "auction-desc" => "score.pre_grade desc nulls last, latest.auction_at desc nulls last",
        "year-asc" => "score.pre_grade desc nulls last, latest.year asc nulls last",
        "year-desc" => "score.pre_grade desc nulls last, latest.year desc nulls last",
        "estimate-asc" => "score.pre_grade desc nulls last, latest.provider_estimate_from_usd asc nulls last",
        "estimate-desc" => "score.pre_grade desc nulls last, latest.provider_estimate_to_usd desc nulls last",
        "buy-asc" => "score.pre_grade desc nulls last, latest.buy_now_usd asc nulls last",
        "buy-desc" => "score.pre_grade desc nulls last, latest.buy_now_usd desc nulls last",
        "bid-asc" => "score.pre_grade desc nulls last, latest.current_bid_usd asc nulls last",
        "bid-desc" => "score.pre_grade desc nulls last, latest.current_bid_usd desc nulls last",
        "odometer-asc" => "score.pre_grade desc nulls last, latest.odometer_miles asc nulls last",
        "odometer-desc" => "score.pre_grade desc nulls last, latest.odometer_miles desc nulls last",
        _ => "score.pre_grade desc nulls last, latest.last_seen_at desc nulls last"
    };

    private static string PublicRunConditionV2Sql(string alias) => $"case when upper(replace(replace(replace(coalesce({alias}.run_condition_value, {alias}.run_condition_label, ''), '&', ' AND '), '/', ' AND '), '-', ' ')) like '%RUNS AND DRIVES%' or upper(replace(replace(replace(coalesce({alias}.run_condition_value, {alias}.run_condition_label, ''), '&', ' AND '), '/', ' AND '), '-', ' ')) like '%RUN AND DRIVE%' then 'RUNS_AND_DRIVES' when upper(coalesce({alias}.run_condition_value, {alias}.run_condition_label, '')) like '%START%' then 'STARTS' when upper(coalesce({alias}.run_condition_value, {alias}.run_condition_label, '')) like '%STATIONARY%' then 'STATIONARY' else 'UNVERIFIED' end";

    private sealed class InventoryV2ReadRow
    {
        public required Dictionary<string, object?> Values { get; init; }
        public required DateTimeOffset ObservedAt { get; init; }
        public List<InventoryV2MediaRow> Media { get; } = [];
    }

    private sealed record InventoryV2MediaRow(string MediaType, int Position, string SourceUrl, string? ThumbnailUrl, string? LargeUrl);

    private static async Task<List<InventoryV2ReadRow>> ReadInventoryV2RowsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var result = new List<InventoryV2ReadRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < reader.FieldCount; index++)
                values[reader.GetName(index)] = reader.IsDBNull(index) ? null : reader.GetValue(index);
            result.Add(new InventoryV2ReadRow { Values = values, ObservedAt = ReadV2Date(values, "observed_at") ?? throw new InvalidOperationException("Inventory V2 row is missing last_seen_at.") });
        }
        return result;
    }

    private static async Task AttachInventoryV2MediaAsync(NpgsqlConnection connection, IReadOnlyList<InventoryV2ReadRow> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0) return;
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = """
            select platform, lot_number, media_type, position, source_url, thumbnail_url, large_url
            from inventory_media_current_v2
            where platform = any(@media_platforms) and lot_number = any(@media_lots)
            order by platform, lot_number, position;
            """;
        AddParameter(command, "media_platforms", rows.Select(row => ReadV2String(row.Values, "platform") ?? string.Empty).Distinct().ToArray());
        AddParameter(command, "media_lots", rows.Select(row => ReadV2String(row.Values, "lot_number") ?? string.Empty).Distinct().ToArray());
        var index = rows.ToDictionary(row => $"{ReadV2String(row.Values, "platform")}\u001f{ReadV2String(row.Values, "lot_number")}", StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var key = $"{reader.GetString(0)}\u001f{reader.GetString(1)}";
            if (index.TryGetValue(key, out var row))
                row.Media.Add(new InventoryV2MediaRow(reader.GetString(2), reader.GetInt32(3), reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetString(6)));
        }
    }

    private static StoredVehicleSnapshot ToStoredInventoryV2Snapshot(InventoryV2ReadRow row)
    {
        var vehicle = BuildInventoryV2Vehicle(row);
        var scoring = ReadInventoryV2Scoring(row.Values);
        return new StoredVehicleSnapshot(ReadV2String(row.Values, "lot_key") ?? $"{vehicle.Platform}:{vehicle.LotNumber}", row.ObservedAt, vehicle, JsonSerializer.Serialize(vehicle, CreateStoredVehicleJsonOptions()), scoring);
    }

    private static AuctionVehicle BuildInventoryV2Vehicle(InventoryV2ReadRow row)
    {
        var r = row.Values;
        var vehicle = new AuctionVehicle
        {
            Platform = ReadV2String(r, "platform"), SourceProvider = ReadV2String(r, "source_provider"), LotNumber = ReadV2String(r, "lot_number"),
            Vin = ReadV2String(r, "vin"), Year = ReadV2Int(r, "year"), Make = ReadV2String(r, "make"), Model = ReadV2String(r, "model"),
            VehicleType = ReadV2String(r, "vehicle_type"), Color = ReadV2String(r, "exterior_color"), FuelType = ReadV2String(r, "fuel_type"),
            Transmission = ReadV2String(r, "transmission"), DriveType = ReadV2String(r, "drive_type"), Damage = ReadV2String(r, "primary_damage"),
            VehicleSpecs = new VehicleSpecs
            {
                ExteriorColor = ReadV2String(r, "exterior_color"), FuelType = ReadV2String(r, "fuel_type"), Transmission = ReadV2String(r, "transmission"),
                DriveType = ReadV2String(r, "drive_type"), BodyStyle = ReadV2String(r, "body_style"), Airbags = ReadV2String(r, "airbags"),
                RestraintSystem = ReadV2String(r, "restraint_system"), Engine = new VehicleEngine { SizeLiters = ReadV2String(r, "engine_size_liters"), Horsepower = ReadV2Decimal(r, "horsepower"), Layout = ReadV2String(r, "engine"), Raw = ReadV2String(r, "engine") }
            },
            Condition = new VehicleCondition
            {
                PrimaryDamage = ReadV2String(r, "primary_damage"), SecondaryDamage = ReadV2String(r, "secondary_damage"), Loss = ReadV2String(r, "loss_type"), HasKey = ReadV2Bool(r, "has_key"),
                RunCondition = new RunConditionInfo { Value = ReadV2String(r, "run_condition_value"), Label = ReadV2String(r, "run_condition_label"), ClassHint = ReadV2String(r, "run_condition_class_hint") }
            },
            OdometerInfo = new OdometerInfo { Miles = ReadV2Decimal(r, "odometer_miles"), Kilometers = ReadV2Decimal(r, "odometer_km"), Status = ReadV2String(r, "odometer_status") },
            Facility = new AuctionFacility { Id = ReadV2String(r, "facility_id"), OfficeName = ReadV2String(r, "facility_office_name"), State = ReadV2String(r, "location_state"), Zip = ReadV2String(r, "facility_zip") },
            Location = new VehicleLocation { Display = ReadV2String(r, "location_display"), State = ReadV2String(r, "location_state"), FacilityId = ReadV2String(r, "facility_id"), SendFrom = ReadV2String(r, "send_from") },
            Seller = new AuctionSeller { Name = ReadV2String(r, "seller_name"), RawType = ReadV2String(r, "seller_type"), Type = ReadV2String(r, "seller_type"), Class = ReadV2String(r, "seller_class"), TextClass = ReadV2String(r, "seller_text_class"), ClassificationConfidence = ReadV2Decimal(r, "seller_classification_confidence"), NeedsReview = ReadV2Bool(r, "seller_needs_review"), ClassificationEvidence = ReadV2String(r, "seller_classification_evidence"), TaxonomyVersion = ReadV2String(r, "seller_taxonomy_version") },
            SaleDocument = new SaleDocument { Name = ReadV2String(r, "detailed_title"), IsPending = ReadV2Bool(r, "title_pending"), Group = ReadV2String(r, "title_group"), Export = ReadV2Bool(r, "title_export"), Registration = ReadV2Bool(r, "title_registration"), PageId = ReadV2String(r, "title_page_id") },
            Details = new VehicleDetails
            {
                VehicleDescription = new VehicleDescriptionDetails { BodyStyle = ReadV2String(r, "body_style"), Series = ReadV2String(r, "series"), Cylinders = ReadV2String(r, "cylinders"), ManufacturedIn = ReadV2String(r, "manufactured_in"), VehicleClass = ReadV2String(r, "vehicle_class") },
                VehicleInformation = new VehicleInformationDetails { TitleBrand = ReadV2String(r, "title_brand"), TitleNotes = ReadV2String(r, "title_notes") },
                SaleInformation = new VehicleSaleInformation { Seller = ReadV2String(r, "seller_name"), SellerType = ReadV2String(r, "seller_type"), ActualCashValue = ReadV2String(r, "actual_cash_value_usd"), EstimatedRepairCost = ReadV2String(r, "estimated_repair_cost_usd"), Lane = ReadV2String(r, "lane"), Aisle = ReadV2String(r, "aisle") }
            },
            Auction = new AuctionInfo { State = ReadV2String(r, "auction_state"), AuctionAt = ReadV2Date(r, "auction_at"), LotStatus = ReadV2String(r, "lot_status"), LotSubStatus = ReadV2String(r, "lot_sub_status"), IsBuyNow = ReadV2Bool(r, "is_buy_now"), IsTimed = ReadV2Bool(r, "is_timed") },
            Pricing = new PricingInfo { CurrentBidUsd = ReadV2Decimal(r, "current_bid_usd"), BuyNowUsd = ReadV2Decimal(r, "buy_now_usd"), SalePriceUsd = ReadV2Decimal(r, "sale_price_usd"), PreBidUsd = ReadV2Decimal(r, "pre_bid_usd"), EstimatedCost = new EstimatedCostInfo { FromUsd = ReadV2Decimal(r, "provider_estimate_from_usd"), ToUsd = ReadV2Decimal(r, "provider_estimate_to_usd"), Text = ReadV2String(r, "provider_estimate_text") } },
            Media = new MediaInfo { ThumbnailsCount = ReadV2Int(r, "media_photos_count"), Has360 = ReadV2Bool(r, "media_has_360"), HasVideo = ReadV2Bool(r, "media_has_video"), Photos = row.Media.Where(media => media.MediaType.Equals("photo", StringComparison.OrdinalIgnoreCase)).OrderBy(media => media.Position).Select(media => media.LargeUrl ?? media.SourceUrl).ToArray(), Items = row.Media.OrderBy(media => media.Position).Select(media => new AuctionMediaItem { Large = media.LargeUrl ?? media.SourceUrl, Thumb = media.ThumbnailUrl, Type = media.MediaType }).ToArray() }
        };
        return vehicle;
    }

    private static LscScoringSummary? ReadInventoryV2Scoring(IReadOnlyDictionary<string, object?> reader)
    {
        var status = ReadV2String(reader, "score_status");
        return string.IsNullOrWhiteSpace(status) ? null : new LscScoringSummary(status, ReadV2Decimal(reader, "score_pre_grade"), ReadV2Decimal(reader, "score_buy_score"), ReadV2Decimal(reader, "score_max_points_evaluable") ?? 0m, ReadV2Decimal(reader, "score_coverage_percent") ?? 0m, ReadV2Decimal(reader, "score_confidence_percent") ?? 0m, ReadV2String(reader, "score_category"), ReadV2String(reader, "score_policy_version") ?? "unknown", ReadV2Date(reader, "score_scored_at") ?? DateTimeOffset.MinValue);
    }

    private static string? ReadV2String(IReadOnlyDictionary<string, object?> reader, string name)
    {
        if (!reader.TryGetValue(name, out var value) || value is null) return null;
        return value switch { string text => string.IsNullOrWhiteSpace(text) ? null : text, _ => Convert.ToString(value, CultureInfo.InvariantCulture) };
    }

    private static decimal? ReadV2Decimal(IReadOnlyDictionary<string, object?> reader, string name) => reader.TryGetValue(name, out var value) && value is not null ? Convert.ToDecimal(value, CultureInfo.InvariantCulture) : null;
    private static int? ReadV2Int(IReadOnlyDictionary<string, object?> reader, string name) => reader.TryGetValue(name, out var value) && value is not null ? Convert.ToInt32(value, CultureInfo.InvariantCulture) : null;
    private static bool? ReadV2Bool(IReadOnlyDictionary<string, object?> reader, string name) => reader.TryGetValue(name, out var value) && value is not null ? Convert.ToBoolean(value, CultureInfo.InvariantCulture) : null;
    private static DateTimeOffset? ReadV2Date(IReadOnlyDictionary<string, object?> reader, string name) => reader.TryGetValue(name, out var value) && value is not null ? value is DateTimeOffset date ? date : DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture) : null;
}
