using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Lsc.Inventory.Api.Normalization;
using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    private const int FacetsV2ValueLimit = 250;
    private const int FacetsV2CacheMaximumEntries = 128;
    private static readonly TimeSpan FacetsV2CacheTimeToLive = TimeSpan.FromSeconds(15);

    private readonly object _facetsV2CacheLock = new();
    private readonly Dictionary<string, LinkedListNode<FacetsV2CacheEntry>> _facetsV2Cache = new(StringComparer.Ordinal);
    private readonly LinkedList<FacetsV2CacheEntry> _facetsV2CacheLru = new();
    private readonly ConcurrentDictionary<string, Lazy<Task<InventoryFacetsV2Response>>> _facetsV2SingleFlights = new(StringComparer.Ordinal);

    private sealed record FacetsV2ProjectionVersion(bool Ready, DateTimeOffset AsOf, string SourceVersion);
    private sealed record FacetsV2CacheEntry(string Key, DateTimeOffset ExpiresAt, InventoryFacetsV2Response Response);
    private sealed record FacetsV2ValueSpec(string Group, string ValueAlias, string Expression, string Parameter);
    private sealed record FacetsV2RangeSpec(string Group, string MinimumAlias, string MaximumAlias, string? FromParameter, string? ToParameter);

    private static readonly IReadOnlyList<FacetsV2ValueSpec> FacetsV2ValueSpecs =
    [
        new(InventoryFacetsV2Groups.Platforms, "platform_value", "nullif(btrim(latest.platform), '')", "facet_platforms"),
        new(InventoryFacetsV2Groups.SellerTypes, "seller_type_value", SqlSellerTypeTaxonomy("nullif(btrim(latest.seller_type), '')"), "facet_seller_types"),
        new(InventoryFacetsV2Groups.Makes, "make_value", "nullif(btrim(latest.make), '')", "facet_makes"),
        new(InventoryFacetsV2Groups.Models, "model_value", "nullif(btrim(latest.model), '')", "facet_models"),
        new(InventoryFacetsV2Groups.VehicleTypes, "vehicle_type_value", "nullif(btrim(latest.vehicle_type), '')", "facet_vehicle_types"),
        new(InventoryFacetsV2Groups.Titles, "title_value", "nullif(btrim(latest.title_type), '')", "facet_titles"),
        new(InventoryFacetsV2Groups.States, "state_value", "nullif(btrim(latest.location_state), '')", "facet_states"),
        new(InventoryFacetsV2Groups.Facilities, "facility_value", "nullif(btrim(latest.location_display), '')", "facet_facilities"),
        new(InventoryFacetsV2Groups.PrimaryDamages, "primary_damage_value", "nullif(btrim(latest.primary_damage), '')", "facet_primary_damages"),
        new(InventoryFacetsV2Groups.SecondaryDamages, "secondary_damage_value", "nullif(btrim(latest.secondary_damage), '')", "facet_secondary_damages"),
        new(InventoryFacetsV2Groups.EngineLayouts, "engine_layout_value", "nullif(btrim(latest.engine_layout), '')", "facet_engine_layouts"),
        new(InventoryFacetsV2Groups.Cylinders, "cylinders_value", "nullif(btrim(latest.cylinders), '')", "facet_cylinders"),
        new(InventoryFacetsV2Groups.Transmissions, "transmission_value", "nullif(btrim(latest.transmission), '')", "facet_transmissions"),
        new(InventoryFacetsV2Groups.Fuels, "fuel_value", "nullif(btrim(latest.fuel_type), '')", "facet_fuels"),
        new(InventoryFacetsV2Groups.Drives, "drive_value", "nullif(btrim(latest.drive_type), '')", "facet_drives"),
        new(InventoryFacetsV2Groups.BodyStyles, "body_style_value", "nullif(btrim(latest.body_style), '')", "facet_body_styles"),
        new(InventoryFacetsV2Groups.Colors, "color_value", "nullif(btrim(latest.color), '')", "facet_colors"),
        new(InventoryFacetsV2Groups.LossTypes, "loss_type_value", "nullif(btrim(latest.loss_type), '')", "facet_loss_types"),
        new(InventoryFacetsV2Groups.StartCodes, "start_code_value", "nullif(btrim(latest.start_code), '')", "facet_start_codes"),
        new(InventoryFacetsV2Groups.RunConditions, "run_condition_value", PublicRunConditionSql("latest"), "facet_run_conditions"),
        new(InventoryFacetsV2Groups.ScoringStatuses, "scoring_status_value", "nullif(btrim(score.status), '')", "facet_scoring_statuses")
    ];

    private static readonly IReadOnlyList<FacetsV2RangeSpec> FacetsV2RangeSpecs =
    [
        new(InventoryFacetsV2Groups.Year, "year_value", "year_value", "facet_year_from", "facet_year_to"),
        new(InventoryFacetsV2Groups.Odometer, "odometer_value", "odometer_value", "facet_odometer_from", "facet_odometer_to"),
        new(InventoryFacetsV2Groups.CurrentBid, "current_bid_value", "current_bid_value", "facet_price_from", "facet_price_to"),
        new(InventoryFacetsV2Groups.ProviderEstimate, "provider_estimate_from_value", "provider_estimate_to_value", "facet_provider_estimate_from", "facet_provider_estimate_to"),
        new(InventoryFacetsV2Groups.AuctionDate, "auction_at_value", "auction_at_value", "facet_auction_from", "facet_auction_to"),
        new(InventoryFacetsV2Groups.EngineSize, "engine_size_value", "engine_size_value", "facet_engine_size_from", "facet_engine_size_to"),
        new(InventoryFacetsV2Groups.Horsepower, "horsepower_value", "horsepower_value", "facet_horsepower_from", "facet_horsepower_to"),
        new(InventoryFacetsV2Groups.PreGrade, "pre_grade_value", "pre_grade_value", "facet_pre_grade_from", null)
    ];

    public async Task<InventoryFacetsV2Response> GetInventoryFacetsV2Async(InventoryFacetsV2Request request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await EnsureSearchProjectionSchemaAsync(cancellationToken);
        if (!await IsSearchProjectionReadyAsync(cancellationToken))
            throw new InvalidOperationException("Facets V2 requires the inventory_search_current projection to be ready.");

        var requested = InventoryFacetsV2Groups.NormalizeRequested(request.RequestedFacets);
        var filters = request.Filters with
        {
            Page = 1,
            PageSize = 1,
            Sort = null,
            Titles = InventoryFacetsV2Fingerprint.Merge(request.Filters.Titles, request.Filters.TitleCategories),
            TitleCategories = null
        };

        await using var connection = await OpenConnectionAsync(cancellationToken);
        var version = await ReadFacetsV2ProjectionVersionAsync(connection, cancellationToken);
        if (!version.Ready)
            throw new InvalidOperationException("Facets V2 requires a ready projection version.");
        var fingerprint = InventoryFacetsV2Fingerprint.Create(new InventoryFacetsV2Request(filters, requested));
        var cacheKey = $"{version.SourceVersion}:{fingerprint}";
        if (TryGetFacetsV2Cache(cacheKey, out var cached))
            return cached with { Cache = "hit", DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds };

        var candidate = new Lazy<Task<InventoryFacetsV2Response>>(
            () => ExecuteFacetsV2Async(filters, requested, version, CancellationToken.None),
            LazyThreadSafetyMode.ExecutionAndPublication);
        var lazy = _facetsV2SingleFlights.GetOrAdd(cacheKey, candidate);
        var isLeader = ReferenceEquals(candidate, lazy);
        try
        {
            var response = await lazy.Value.WaitAsync(cancellationToken);
            SetFacetsV2Cache(cacheKey, response);
            return response with
            {
                Cache = isLeader ? response.Cache : "hit",
                DurationMs = isLeader ? response.DurationMs : (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds
            };
        }
        finally
        {
            if (_facetsV2SingleFlights.TryGetValue(cacheKey, out var current) && ReferenceEquals(current, lazy))
                _facetsV2SingleFlights.TryRemove(cacheKey, out _);
        }
    }

    private async Task<InventoryFacetsV2Response> ExecuteFacetsV2Async(
        InventorySearchRequest filters,
        IReadOnlyList<string> requested,
        FacetsV2ProjectionVersion version,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = BuildFacetsV2Command(connection, filters, requested);
        var total = 0;
        var facets = requested
            .Where(InventoryFacetsV2Groups.Categorical.Contains)
            .ToDictionary(group => group, _ => new List<InventoryFacetValue>(), StringComparer.OrdinalIgnoreCase);
        var numericRanges = new Dictionary<string, InventoryNumericFacetRange>(StringComparer.OrdinalIgnoreCase);
        var dateRanges = new Dictionary<string, InventoryDateFacetRange>(StringComparer.OrdinalIgnoreCase);

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var kind = reader.GetString(0);
                if (string.Equals(kind, "meta", StringComparison.Ordinal))
                {
                    total = reader.GetInt32(3);
                    continue;
                }

                var group = reader.GetString(1);
                if (string.Equals(kind, "facet", StringComparison.Ordinal))
                {
                    if (!reader.IsDBNull(2) && facets.TryGetValue(group, out var values))
                        values.Add(new InventoryFacetValue(reader.GetString(2), reader.GetInt32(3)));
                    continue;
                }

                if (string.Equals(group, InventoryFacetsV2Groups.AuctionDate, StringComparison.OrdinalIgnoreCase))
                {
                    dateRanges[group] = new InventoryDateFacetRange(
                        reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
                        reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7));
                }
                else
                {
                    numericRanges[group] = new InventoryNumericFacetRange(
                        reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                        reader.IsDBNull(5) ? null : reader.GetDecimal(5));
                }
            }
        }

        foreach (var (group, values) in facets)
        {
            foreach (var selected in InventoryFacetsV2Selections.Get(filters, group))
            {
                if (!values.Any(value => string.Equals(value.Value, selected, StringComparison.OrdinalIgnoreCase)))
                    values.Add(new InventoryFacetValue(selected, 0));
            }
        }

        var warnings = new List<string>();
        if (requested.Contains(InventoryFacetsV2Groups.Models, StringComparer.OrdinalIgnoreCase) && filters.Makes is not { Count: > 0 })
            warnings.Add("models was requested without a make filter; request this high-cardinality facet on demand.");
        if (requested.Contains(InventoryFacetsV2Groups.Facilities, StringComparer.OrdinalIgnoreCase) && filters.States is not { Count: > 0 })
            warnings.Add("facilities was requested without a state filter; request this high-cardinality facet on demand.");

        return new InventoryFacetsV2Response(
            total,
            version.AsOf,
            version.SourceVersion,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            "miss",
            facets.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<InventoryFacetValue>)pair.Value, StringComparer.OrdinalIgnoreCase),
            new InventoryFacetsV2Ranges(
                numericRanges.GetValueOrDefault(InventoryFacetsV2Groups.Year),
                numericRanges.GetValueOrDefault(InventoryFacetsV2Groups.Odometer),
                numericRanges.GetValueOrDefault(InventoryFacetsV2Groups.CurrentBid),
                numericRanges.GetValueOrDefault(InventoryFacetsV2Groups.ProviderEstimate),
                dateRanges.GetValueOrDefault(InventoryFacetsV2Groups.AuctionDate),
                numericRanges.GetValueOrDefault(InventoryFacetsV2Groups.EngineSize),
                numericRanges.GetValueOrDefault(InventoryFacetsV2Groups.Horsepower),
                numericRanges.GetValueOrDefault(InventoryFacetsV2Groups.PreGrade)),
            warnings);
    }

    private bool TryGetFacetsV2Cache(string key, out InventoryFacetsV2Response response)
    {
        lock (_facetsV2CacheLock)
        {
            if (!_facetsV2Cache.TryGetValue(key, out var node))
            {
                response = default!;
                return false;
            }
            if (node.Value.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                _facetsV2Cache.Remove(key);
                _facetsV2CacheLru.Remove(node);
                response = default!;
                return false;
            }
            _facetsV2CacheLru.Remove(node);
            _facetsV2CacheLru.AddFirst(node);
            response = node.Value.Response;
            return true;
        }
    }

    private void SetFacetsV2Cache(string key, InventoryFacetsV2Response response)
    {
        lock (_facetsV2CacheLock)
        {
            if (_facetsV2Cache.Remove(key, out var existing))
                _facetsV2CacheLru.Remove(existing);
            var entry = new FacetsV2CacheEntry(key, DateTimeOffset.UtcNow.Add(FacetsV2CacheTimeToLive), response);
            var node = _facetsV2CacheLru.AddFirst(entry);
            _facetsV2Cache[key] = node;

            while (_facetsV2Cache.Count > FacetsV2CacheMaximumEntries)
            {
                var last = _facetsV2CacheLru.Last;
                if (last is null) break;
                _facetsV2Cache.Remove(last.Value.Key);
                _facetsV2CacheLru.RemoveLast();
            }
        }
    }

    private async Task<FacetsV2ProjectionVersion> ReadFacetsV2ProjectionVersionAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Min(_persistence.CommandTimeoutSeconds, 5);
        command.CommandText = """
            select is_ready, coalesce(generated_at, updated_at),
                   concat(projection_name, ':', row_count::text, ':', extract(epoch from coalesce(generated_at, updated_at))::numeric(20, 6)::text)
            from inventory_search_projection_state
            where projection_name = 'inventory-current-v1';
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new FacetsV2ProjectionVersion(false, DateTimeOffset.UtcNow, "inventory-current-v1:missing");
        return new FacetsV2ProjectionVersion(reader.GetBoolean(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetString(2));
    }

    private NpgsqlCommand BuildFacetsV2Command(NpgsqlConnection connection, InventorySearchRequest request, IReadOnlyList<string> requested)
    {
        var command = connection.CreateCommand();
        command.CommandTimeout = Math.Min(_persistence.CommandTimeoutSeconds, 15);
        AddParameter(command, "facet_limit", FacetsV2ValueLimit);
        var fixedWhere = new List<string> { "latest.is_active" };
        AddFacetsV2FixedFilters(command, request, fixedWhere);

        var needsScore = request.PreGradeFrom.HasValue || request.ScoringStatuses is { Count: > 0 } ||
            requested.Contains(InventoryFacetsV2Groups.PreGrade, StringComparer.OrdinalIgnoreCase) ||
            requested.Contains(InventoryFacetsV2Groups.ScoringStatuses, StringComparer.OrdinalIgnoreCase);
        var scoreJoin = needsScore ? "left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key" : string.Empty;
        var scoreStatusExpression = needsScore ? "nullif(btrim(score.status), '')" : "null::text";
        var preGradeExpression = needsScore ? "score.pre_grade" : "null::numeric";

        var valueExpressions = FacetsV2ValueSpecs
            .Select(spec => $"{(spec.Group == InventoryFacetsV2Groups.ScoringStatuses ? scoreStatusExpression : spec.Expression)} as {spec.ValueAlias}")
            .ToList();
        valueExpressions.AddRange(
        [
            "latest.year::numeric as year_value",
            "latest.odometer as odometer_value",
            "latest.current_bid_usd as current_bid_value",
            "latest.provider_estimate_from as provider_estimate_from_value",
            "latest.provider_estimate_to as provider_estimate_to_value",
            "latest.auction_at as auction_at_value",
            "latest.engine_size_liters as engine_size_value",
            "latest.horsepower as horsepower_value",
            $"{preGradeExpression} as pre_grade_value"
        ]);

        var matchExpressions = new List<string>();
        foreach (var spec in FacetsV2ValueSpecs)
        {
            var selected = InventoryFacetsV2Selections.Get(request, spec.Group).ToArray();
            var expression = spec.Group == InventoryFacetsV2Groups.ScoringStatuses ? scoreStatusExpression : spec.Expression;
            if (selected.Length == 0)
            {
                matchExpressions.Add($"true as matches_{spec.Group}");
                continue;
            }
            AddParameter(command, spec.Parameter, selected.Select(value => value.ToLowerInvariant()).ToArray());
            matchExpressions.Add($"lower(coalesce({expression}, '')) = any(@{spec.Parameter}) as matches_{spec.Group}");
        }

        matchExpressions.AddRange(BuildFacetsV2RangeMatches(command, request));
        var allGroups = FacetsV2ValueSpecs.Select(spec => spec.Group).Concat(FacetsV2RangeSpecs.Select(spec => spec.Group)).ToArray();
        var allPredicate = string.Join(" and ", allGroups.Select(group => $"matches_{group}"));
        var branches = new List<string>
        {
            $"select 'meta'::text as result_kind, null::text as group_key, null::text as value, count(*)::int as vehicle_count, null::numeric as minimum_numeric, null::numeric as maximum_numeric, null::timestamptz as minimum_date, null::timestamptz as maximum_date from base where {allPredicate}"
        };

        foreach (var group in requested)
        {
            var exceptPredicate = string.Join(" and ", allGroups.Where(candidate => !string.Equals(candidate, group, StringComparison.OrdinalIgnoreCase)).Select(candidate => $"matches_{candidate}"));
            if (InventoryFacetsV2Groups.Categorical.Contains(group))
            {
                var spec = FacetsV2ValueSpecs.Single(candidate => string.Equals(candidate.Group, group, StringComparison.OrdinalIgnoreCase));
                branches.Add($"""
                    select 'facet'::text, '{spec.Group}'::text, grouped.value, grouped.vehicle_count,
                           null::numeric, null::numeric, null::timestamptz, null::timestamptz
                    from (
                        select min({spec.ValueAlias})::text as value, count(*)::int as vehicle_count
                        from base
                        where {exceptPredicate} and {spec.ValueAlias} is not null
                        group by lower({spec.ValueAlias})
                        order by vehicle_count desc, value asc
                        limit @facet_limit
                    ) grouped
                    """);
                continue;
            }

            var range = FacetsV2RangeSpecs.Single(candidate => string.Equals(candidate.Group, group, StringComparison.OrdinalIgnoreCase));
            if (InventoryFacetsV2Groups.DateRanges.Contains(group))
            {
                branches.Add($"select 'range'::text, '{range.Group}'::text, null::text, 0::int, null::numeric, null::numeric, min({range.MinimumAlias}), max({range.MaximumAlias}) from base where {exceptPredicate}");
            }
            else
            {
                branches.Add($"select 'range'::text, '{range.Group}'::text, null::text, 0::int, min({range.MinimumAlias}), max({range.MaximumAlias}), null::timestamptz, null::timestamptz from base where {exceptPredicate}");
            }
        }

        command.CommandText = $"""
            with base as materialized (
                select
                    {string.Join(",\n                    ", valueExpressions)},
                    {string.Join(",\n                    ", matchExpressions)}
                from inventory_search_current latest
                {scoreJoin}
                where {string.Join(" and ", fixedWhere)}
            )
            {string.Join("\nunion all\n", branches)};
            """;
        return command;
    }

    private static IReadOnlyList<string> BuildFacetsV2RangeMatches(NpgsqlCommand command, InventorySearchRequest request)
    {
        var matches = new List<string>();
        matches.Add(BuildRangeMatch(command, InventoryFacetsV2Groups.Year, "latest.year", request.YearFrom, "facet_year_from", request.YearTo, "facet_year_to"));
        matches.Add(BuildRangeMatch(command, InventoryFacetsV2Groups.Odometer, "latest.odometer", request.OdometerFrom, "facet_odometer_from", request.OdometerTo, "facet_odometer_to"));

        var bid = new List<string>();
        if (request.PriceFrom.HasValue) { bid.Add("latest.current_bid_usd >= @facet_price_from"); AddParameter(command, "facet_price_from", request.PriceFrom.Value); }
        if (request.PriceTo.HasValue) { bid.Add("latest.current_bid_usd <= @facet_price_to"); AddParameter(command, "facet_price_to", request.PriceTo.Value); }
        if (request.MaxCurrentBid.HasValue) { bid.Add("(latest.current_bid_usd is null or latest.current_bid_usd <= @facet_max_current_bid)"); AddParameter(command, "facet_max_current_bid", request.MaxCurrentBid.Value); }
        matches.Add($"{(bid.Count == 0 ? "true" : string.Join(" and ", bid))} as matches_{InventoryFacetsV2Groups.CurrentBid}");

        var estimate = new List<string>();
        if (request.ProviderEstimateFrom.HasValue) { estimate.Add("latest.provider_estimate_to >= @facet_provider_estimate_from"); AddParameter(command, "facet_provider_estimate_from", request.ProviderEstimateFrom.Value); }
        if (request.ProviderEstimateTo.HasValue) { estimate.Add("latest.provider_estimate_from <= @facet_provider_estimate_to"); AddParameter(command, "facet_provider_estimate_to", request.ProviderEstimateTo.Value); }
        matches.Add($"{(estimate.Count == 0 ? "true" : string.Join(" and ", estimate))} as matches_{InventoryFacetsV2Groups.ProviderEstimate}");

        matches.Add(BuildRangeMatch(command, InventoryFacetsV2Groups.AuctionDate, "latest.auction_at", request.AuctionFrom, "facet_auction_from", request.AuctionTo, "facet_auction_to"));
        matches.Add(BuildRangeMatch(command, InventoryFacetsV2Groups.EngineSize, "latest.engine_size_liters", request.EngineSizeFrom, "facet_engine_size_from", request.EngineSizeTo, "facet_engine_size_to"));
        matches.Add(BuildRangeMatch(command, InventoryFacetsV2Groups.Horsepower, "latest.horsepower", request.HorsepowerFrom, "facet_horsepower_from", request.HorsepowerTo, "facet_horsepower_to"));
        if (request.PreGradeFrom.HasValue)
        {
            AddParameter(command, "facet_pre_grade_from", request.PreGradeFrom.Value);
            matches.Add($"score.pre_grade >= @facet_pre_grade_from as matches_{InventoryFacetsV2Groups.PreGrade}");
        }
        else
        {
            matches.Add($"true as matches_{InventoryFacetsV2Groups.PreGrade}");
        }
        return matches;
    }

    private static string BuildRangeMatch<T>(NpgsqlCommand command, string group, string expression, T? from, string fromParameter, T? to, string toParameter) where T : struct
    {
        var predicates = new List<string>();
        if (from.HasValue) { predicates.Add($"{expression} >= @{fromParameter}"); AddParameter(command, fromParameter, from.Value); }
        if (to.HasValue) { predicates.Add($"{expression} <= @{toParameter}"); AddParameter(command, toParameter, to.Value); }
        return $"{(predicates.Count == 0 ? "true" : string.Join(" and ", predicates))} as matches_{group}";
    }

    private static void AddFacetsV2FixedFilters(NpgsqlCommand command, InventorySearchRequest request, ICollection<string> where)
    {
        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            where.Add("(to_tsvector('simple', latest.search_text) @@ websearch_to_tsquery('simple', @facet_search_query) or latest.lot_number ilike @facet_query_like or latest.vin ilike @facet_query_like)");
            AddParameter(command, "facet_search_query", request.Query.Trim());
            AddParameter(command, "facet_query_like", $"%{request.Query.Trim()}%");
        }
        if (request.ExcludeSpecialTitles) where.Add("not latest.is_special_title");
        if (request.BuyNowOnly == true) where.Add("latest.is_buy_now");
        if (request.WithPhotosOnly == true) where.Add("latest.has_photos");
        if (request.WithBidOnly == true) where.Add("latest.current_bid_usd is not null");
        if (string.Equals(request.KeyMode, "with", StringComparison.OrdinalIgnoreCase)) where.Add("latest.has_key is true");
        if (string.Equals(request.KeyMode, "without", StringComparison.OrdinalIgnoreCase)) where.Add("latest.has_key is false");
        if (string.Equals(request.AuctionStatus, "open", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like any(array['%open%', '%active%'])");
        if (string.Equals(request.AuctionStatus, "live", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like '%live%'");
        if (string.Equals(request.AuctionStatus, "finished", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like any(array['%finished%', '%ended%', '%sold%'])");
    }
}
