using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
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
    private sealed record FacetsV2RangeSpec(
        string Group,
        string MinimumAlias,
        string MaximumAlias,
        string MinimumExpression,
        string MaximumExpression);

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
        new(InventoryFacetsV2Groups.Year, "year_value", "year_value", "latest.year::numeric", "latest.year::numeric"),
        new(InventoryFacetsV2Groups.Odometer, "odometer_value", "odometer_value", "latest.odometer", "latest.odometer"),
        new(InventoryFacetsV2Groups.CurrentBid, "current_bid_value", "current_bid_value", "latest.current_bid_usd", "latest.current_bid_usd"),
        new(InventoryFacetsV2Groups.ProviderEstimate, "provider_estimate_from_value", "provider_estimate_to_value", "latest.provider_estimate_from", "latest.provider_estimate_to"),
        new(InventoryFacetsV2Groups.AuctionDate, "auction_at_value", "auction_at_value", "latest.auction_at", "latest.auction_at"),
        new(InventoryFacetsV2Groups.EngineSize, "engine_size_value", "engine_size_value", "latest.engine_size_liters", "latest.engine_size_liters"),
        new(InventoryFacetsV2Groups.Horsepower, "horsepower_value", "horsepower_value", "latest.horsepower", "latest.horsepower"),
        new(InventoryFacetsV2Groups.PreGrade, "pre_grade_value", "pre_grade_value", "score.pre_grade", "score.pre_grade")
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

        var sharedCached = await _facetsV2SharedCache.GetAsync(cacheKey, cancellationToken);
        if (sharedCached is not null && string.Equals(sharedCached.SourceVersion, version.SourceVersion, StringComparison.Ordinal))
        {
            SetFacetsV2Cache(cacheKey, sharedCached);
            return sharedCached with { Cache = "shared-hit", DurationMs = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds };
        }

        var candidate = new Lazy<Task<InventoryFacetsV2Response>>(
            () => GetOrComputeFacetsV2Async(cacheKey, filters, requested, version),
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

    private async Task<InventoryFacetsV2Response> GetOrComputeFacetsV2Async(
        string cacheKey,
        InventorySearchRequest filters,
        IReadOnlyList<string> requested,
        FacetsV2ProjectionVersion version)
    {
        var sharedCached = await _facetsV2SharedCache.GetAsync(cacheKey, CancellationToken.None);
        if (sharedCached is not null && string.Equals(sharedCached.SourceVersion, version.SourceVersion, StringComparison.Ordinal))
            return sharedCached with { Cache = "shared-hit" };

        string? lockToken = null;
        if (_facetsV2SharedCache.IsConfigured)
        {
            lockToken = await _facetsV2SharedCache.TryAcquireLockAsync(cacheKey, CancellationToken.None);
            if (lockToken is null)
            {
                var deadline = DateTimeOffset.UtcNow.AddMilliseconds(2200);
                while (DateTimeOffset.UtcNow < deadline)
                {
                    await Task.Delay(50, CancellationToken.None);
                    sharedCached = await _facetsV2SharedCache.GetAsync(cacheKey, CancellationToken.None);
                    if (sharedCached is not null && string.Equals(sharedCached.SourceVersion, version.SourceVersion, StringComparison.Ordinal))
                        return sharedCached with { Cache = "shared-wait" };
                }
            }
        }

        try
        {
            var response = await ExecuteFacetsV2Async(filters, requested, version, CancellationToken.None);
            await _facetsV2SharedCache.SetAsync(cacheKey, response, CancellationToken.None);
            return response;
        }
        finally
        {
            if (lockToken is not null)
                await _facetsV2SharedCache.ReleaseLockAsync(cacheKey, lockToken, CancellationToken.None);
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
        var sellerFacets = new List<InventorySellerFacetValue>();

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
                if (string.Equals(kind, "seller", StringComparison.Ordinal))
                {
                    if (!reader.IsDBNull(2))
                    {
                        using var sellerJson = JsonDocument.Parse(reader.GetString(2));
                        var root = sellerJson.RootElement;
                        sellerFacets.Add(new InventorySellerFacetValue(
                            root.GetProperty("category").GetString() ?? SellerTaxonomy.Unknown,
                            root.GetProperty("sellerName").GetString() ?? "<NULL>",
                            root.GetProperty("platform").GetString() ?? "unknown",
                            reader.GetInt32(3),
                            root.TryGetProperty("confidence", out var confidence) ? confidence.GetDecimal() : 0m,
                            root.TryGetProperty("needsReview", out var needsReview) && needsReview.GetBoolean()));
                    }
                    continue;
                }
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
            warnings,
            sellerFacets
                .OrderByDescending(value => value.Count)
                .ThenBy(value => value.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(value => value.SellerName, StringComparer.OrdinalIgnoreCase)
                .ToArray());
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

        var requestedValueSpecs = FacetsV2ValueSpecs
            .Where(spec => requested.Contains(spec.Group, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var activeValueSpecs = FacetsV2ValueSpecs
            .Where(spec => InventoryFacetsV2Selections.Get(request, spec.Group).Any())
            .ToArray();
        var requestedRangeSpecs = FacetsV2RangeSpecs
            .Where(spec => requested.Contains(spec.Group, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var activeRangeSpecs = FacetsV2RangeSpecs
            .Where(spec => IsFacetsV2RangeActive(request, spec.Group))
            .ToArray();

        var needsScore = requestedValueSpecs.Concat(activeValueSpecs).Any(spec => spec.Group == InventoryFacetsV2Groups.ScoringStatuses) ||
            requestedRangeSpecs.Concat(activeRangeSpecs).Any(spec => spec.Group == InventoryFacetsV2Groups.PreGrade);
        var scoreJoin = needsScore ? "left join inventory_vehicle_score_current score on score.lot_key = latest.lot_key" : string.Empty;
        var scoreStatusExpression = needsScore ? "nullif(btrim(score.status), '')" : "null::text";

        var valueExpressions = requestedValueSpecs
            .Select(spec => $"{(spec.Group == InventoryFacetsV2Groups.ScoringStatuses ? scoreStatusExpression : spec.Expression)} as {spec.ValueAlias}")
            .ToList();
        foreach (var spec in requestedRangeSpecs)
        {
            valueExpressions.Add($"{spec.MinimumExpression} as {spec.MinimumAlias}");
            if (!string.Equals(spec.MinimumAlias, spec.MaximumAlias, StringComparison.Ordinal))
                valueExpressions.Add($"{spec.MaximumExpression} as {spec.MaximumAlias}");
        }

        var includeSellerDetails = requested.Contains(InventoryFacetsV2Groups.SellerTypes, StringComparer.OrdinalIgnoreCase);
        if (includeSellerDetails)
        {
            valueExpressions.Add("latest.platform as seller_platform_value");
            valueExpressions.Add("latest.seller_name as seller_name_value");
            valueExpressions.Add("latest.seller_type as seller_category_value");
            valueExpressions.Add("coalesce(latest.seller_classification_confidence, 0.35) as seller_confidence_value");
            valueExpressions.Add("coalesce(latest.seller_needs_review, true) as seller_needs_review_value");
        }

        var matchExpressions = new List<string>();
        if (request.SellerNames is { Count: > 0 })
        {
            AddParameter(command, "facet_seller_names", request.SellerNames.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim().ToLowerInvariant()).Distinct().ToArray());
            matchExpressions.Add("lower(coalesce(seller_name_value, '')) = any(@facet_seller_names) as matches_seller_names");
        }

        foreach (var spec in activeValueSpecs)
        {
            var selected = InventoryFacetsV2Selections.Get(request, spec.Group).ToArray();
            var expression = spec.Group == InventoryFacetsV2Groups.ScoringStatuses ? scoreStatusExpression : spec.Expression;
            AddParameter(command, spec.Parameter, selected.Select(value => value.ToLowerInvariant()).ToArray());
            matchExpressions.Add($"lower(coalesce({expression}, '')) = any(@{spec.Parameter}) as matches_{spec.Group}");
        }

        matchExpressions.AddRange(BuildFacetsV2RangeMatches(command, request, activeRangeSpecs));
        var activeGroups = activeValueSpecs.Select(spec => spec.Group).Concat(activeRangeSpecs.Select(spec => spec.Group)).ToArray();
        var allPredicate = BuildFacetsV2Predicate(activeGroups);
        var branches = new List<string>
        {
            $"select 'meta'::text as result_kind, null::text as group_key, null::text as value, count(*)::int as vehicle_count, null::numeric as minimum_numeric, null::numeric as maximum_numeric, null::timestamptz as minimum_date, null::timestamptz as maximum_date from base where {allPredicate}"
        };

        if (requested.Contains(InventoryFacetsV2Groups.SellerTypes, StringComparer.OrdinalIgnoreCase))
        {
            var sellerExceptPredicate = BuildFacetsV2Predicate(activeGroups.Where(candidate => !string.Equals(candidate, InventoryFacetsV2Groups.SellerTypes, StringComparison.OrdinalIgnoreCase)));
            branches.Add($"""
                (select 'seller'::text, 'sellerDetails'::text,
                       jsonb_build_object(
                           'category', base.seller_category_value,
                           'sellerName', coalesce(base.seller_name_value, '<NULL>'),
                           'platform', coalesce(base.seller_platform_value, 'unknown'),
                           'confidence', coalesce(base.seller_confidence_value, 0.0),
                           'needsReview', coalesce(base.seller_needs_review_value, true)
                       )::text,
                       count(*)::int, null::numeric, null::numeric, null::timestamptz, null::timestamptz
                from base
                where {sellerExceptPredicate} and base.seller_name_value is not null
                group by base.seller_category_value, base.seller_name_value, base.seller_platform_value, base.seller_confidence_value, base.seller_needs_review_value
                order by count(*) desc, base.seller_category_value, base.seller_name_value
                limit @facet_seller_detail_limit)
                """);
            AddParameter(command, "facet_seller_detail_limit", FacetsV2ValueLimit * 20);
        }

        foreach (var group in requested)
        {
            var exceptPredicate = BuildFacetsV2Predicate(activeGroups.Where(candidate => !string.Equals(candidate, group, StringComparison.OrdinalIgnoreCase)));
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

        if (valueExpressions.Count == 0 && matchExpressions.Count == 0)
            valueExpressions.Add("1 as facet_row");

        command.CommandText = $"""
            with base as materialized (
                select
                    {string.Join(",\n                    ", valueExpressions.Concat(matchExpressions))}
                from inventory_search_current latest
                {scoreJoin}
                where {string.Join(" and ", fixedWhere)}
            )
            {string.Join("\nunion all\n", branches)};
            """;
        return command;
    }

    private static IReadOnlyList<string> BuildFacetsV2RangeMatches(
        NpgsqlCommand command,
        InventorySearchRequest request,
        IReadOnlyCollection<FacetsV2RangeSpec> activeSpecs)
    {
        var matches = new List<string>();
        var activeGroups = activeSpecs.Select(spec => spec.Group).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (activeGroups.Contains(InventoryFacetsV2Groups.Year))
            matches.Add(BuildRangeMatch(command, InventoryFacetsV2Groups.Year, "latest.year", request.YearFrom, "facet_year_from", request.YearTo, "facet_year_to"));
        if (activeGroups.Contains(InventoryFacetsV2Groups.Odometer))
            matches.Add(BuildRangeMatch(command, InventoryFacetsV2Groups.Odometer, "latest.odometer", request.OdometerFrom, "facet_odometer_from", request.OdometerTo, "facet_odometer_to"));

        if (activeGroups.Contains(InventoryFacetsV2Groups.CurrentBid))
        {
            var bid = new List<string>();
            if (request.PriceFrom.HasValue) { bid.Add("latest.current_bid_usd >= @facet_price_from"); AddParameter(command, "facet_price_from", request.PriceFrom.Value); }
            if (request.PriceTo.HasValue) { bid.Add("latest.current_bid_usd <= @facet_price_to"); AddParameter(command, "facet_price_to", request.PriceTo.Value); }
            if (request.MaxCurrentBid.HasValue) { bid.Add("(latest.current_bid_usd is null or latest.current_bid_usd <= @facet_max_current_bid)"); AddParameter(command, "facet_max_current_bid", request.MaxCurrentBid.Value); }
            matches.Add($"{string.Join(" and ", bid)} as matches_{InventoryFacetsV2Groups.CurrentBid}");
        }

        if (activeGroups.Contains(InventoryFacetsV2Groups.ProviderEstimate))
        {
            var estimate = new List<string>();
            if (request.ProviderEstimateFrom.HasValue) { estimate.Add("latest.provider_estimate_to >= @facet_provider_estimate_from"); AddParameter(command, "facet_provider_estimate_from", request.ProviderEstimateFrom.Value); }
            if (request.ProviderEstimateTo.HasValue) { estimate.Add("latest.provider_estimate_from <= @facet_provider_estimate_to"); AddParameter(command, "facet_provider_estimate_to", request.ProviderEstimateTo.Value); }
            matches.Add($"{string.Join(" and ", estimate)} as matches_{InventoryFacetsV2Groups.ProviderEstimate}");
        }

        if (activeGroups.Contains(InventoryFacetsV2Groups.AuctionDate))
            matches.Add(BuildRangeMatch(command, InventoryFacetsV2Groups.AuctionDate, "latest.auction_at", request.AuctionFrom, "facet_auction_from", request.AuctionTo, "facet_auction_to"));
        if (activeGroups.Contains(InventoryFacetsV2Groups.EngineSize))
            matches.Add(BuildRangeMatch(command, InventoryFacetsV2Groups.EngineSize, "latest.engine_size_liters", request.EngineSizeFrom, "facet_engine_size_from", request.EngineSizeTo, "facet_engine_size_to"));
        if (activeGroups.Contains(InventoryFacetsV2Groups.Horsepower))
            matches.Add(BuildRangeMatch(command, InventoryFacetsV2Groups.Horsepower, "latest.horsepower", request.HorsepowerFrom, "facet_horsepower_from", request.HorsepowerTo, "facet_horsepower_to"));
        if (activeGroups.Contains(InventoryFacetsV2Groups.PreGrade) && request.PreGradeFrom is { } preGradeFrom)
        {
            AddParameter(command, "facet_pre_grade_from", preGradeFrom);
            matches.Add($"score.pre_grade >= @facet_pre_grade_from as matches_{InventoryFacetsV2Groups.PreGrade}");
        }
        return matches;
    }

    private static bool IsFacetsV2RangeActive(InventorySearchRequest request, string group) => group switch
    {
        InventoryFacetsV2Groups.Year => request.YearFrom.HasValue || request.YearTo.HasValue,
        InventoryFacetsV2Groups.Odometer => request.OdometerFrom.HasValue || request.OdometerTo.HasValue,
        InventoryFacetsV2Groups.CurrentBid => request.PriceFrom.HasValue || request.PriceTo.HasValue || request.MaxCurrentBid.HasValue,
        InventoryFacetsV2Groups.ProviderEstimate => request.ProviderEstimateFrom.HasValue || request.ProviderEstimateTo.HasValue,
        InventoryFacetsV2Groups.AuctionDate => request.AuctionFrom.HasValue || request.AuctionTo.HasValue,
        InventoryFacetsV2Groups.EngineSize => request.EngineSizeFrom.HasValue || request.EngineSizeTo.HasValue,
        InventoryFacetsV2Groups.Horsepower => request.HorsepowerFrom.HasValue || request.HorsepowerTo.HasValue,
        InventoryFacetsV2Groups.PreGrade => request.PreGradeFrom.HasValue,
        _ => false
    };

    private static string BuildFacetsV2Predicate(IEnumerable<string> groups)
    {
        var predicates = groups.Select(group => $"matches_{group}").ToArray();
        return predicates.Length == 0 ? "true" : string.Join(" and ", predicates);
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
        if (request.BuyNowOnly == true || request.BuyNowFrom.HasValue || request.BuyNowTo.HasValue) where.Add("latest.buy_now_usd > 0");
        if (request.BuyNowFrom.HasValue) { where.Add("latest.buy_now_usd >= @facet_buy_now_from"); AddParameter(command, "facet_buy_now_from", request.BuyNowFrom.Value); }
        if (request.BuyNowTo.HasValue) { where.Add("latest.buy_now_usd <= @facet_buy_now_to"); AddParameter(command, "facet_buy_now_to", request.BuyNowTo.Value); }
        if (request.WithPhotosOnly == true) where.Add("latest.has_photos");
        if (request.WithBidOnly == true) where.Add("latest.current_bid_usd is not null");
        if (string.Equals(request.KeyMode, "with", StringComparison.OrdinalIgnoreCase)) where.Add("latest.has_key is true");
        if (string.Equals(request.KeyMode, "without", StringComparison.OrdinalIgnoreCase)) where.Add("latest.has_key is false");
        if (string.Equals(request.AuctionStatus, "open", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like any(array['%open%', '%active%'])");
        if (string.Equals(request.AuctionStatus, "live", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like '%live%'");
        if (string.Equals(request.AuctionStatus, "finished", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like any(array['%finished%', '%ended%', '%sold%'])");
    }
}
