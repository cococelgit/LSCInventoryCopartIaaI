using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Scoring;
using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    private const int HighPriorityScoring = 100;
    private const int BackfillPriorityScoring = 10;
    private const int MaximumScoringAttempts = 3;

    private sealed record ScoringQueueItem(string LotKey, string Platform, DateTimeOffset SourceObservedAt, int Attempts, int Priority);

    private async Task EnqueueScoringCandidateAsync(string lotKey, string? platform, DateTimeOffset observedAt, CancellationToken cancellationToken)
    {
        await EnsureScoringSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            insert into inventory_vehicle_scoring_queue (
                lot_key, platform, source_observed_at, status, attempts, priority, requested_at, updated_at)
            values (@lot_key, @platform, @source_observed_at, 'queued', 0, @priority, now(), now())
            on conflict (lot_key) do update set
                platform = excluded.platform,
                source_observed_at = excluded.source_observed_at,
                status = 'queued',
                attempts = 0,
                priority = excluded.priority,
                last_error = null,
                requested_at = now(),
                claimed_at = null,
                completed_at = null,
                updated_at = now();
            """;
        AddParameter(command, "lot_key", lotKey);
        AddParameter(command, "platform", platform?.Trim().ToLowerInvariant() ?? "unknown");
        AddParameter(command, "source_observed_at", observedAt);
        AddParameter(command, "priority", HighPriorityScoring);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<LscVehicleScoringResult> PersistScoringResultAsync(
        AuctionVehicle vehicle,
        EligibilityEvaluation eligibility,
        DateTimeOffset sourceObservedAt,
        CancellationToken cancellationToken)
    {
        var outcome = LscVehicleScoringEngine.Evaluate(vehicle, eligibility, sourceObservedAt);
        await PersistScoringResultAsync(outcome, sourceObservedAt, cancellationToken);
        return outcome;
    }

    public async Task<InventoryScoringBackfillResult> EnqueueScoringBackfillAsync(int maximum, CancellationToken cancellationToken)
    {
        await EnsureScoringSchemaAsync(cancellationToken);
        var limit = Math.Clamp(maximum, 1, 10_000);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
        command.CommandText = """
            with eligible as (
                select current.lot_key, current.platform, current.observed_at,
                       row_number() over (
                           partition by current.platform
                           order by current.observed_at asc, current.lot_key asc) as platform_position
                from inventory_search_current current
                left join inventory_vehicle_score_current score on score.lot_key = current.lot_key
                left join inventory_vehicle_scoring_queue queue on queue.lot_key = current.lot_key
                where current.is_active
                  and (
                    score.lot_key is null
                    or score.policy_version <> @policy_version
                    or score.source_observed_at <> current.observed_at
                    or (queue.status = 'failed' and queue.attempts < @maximum_attempts)
                  )
            ), candidates as (
                select lot_key, platform, observed_at
                from eligible
                order by platform_position asc, platform asc, lot_key asc
                limit @limit
            ), upserted as (
                insert into inventory_vehicle_scoring_queue (
                    lot_key, platform, source_observed_at, status, attempts, priority, requested_at, updated_at)
                select lot_key, platform, observed_at, 'queued', 0, @priority, now(), now()
                from candidates
                on conflict (lot_key) do update set
                    platform = excluded.platform,
                    source_observed_at = excluded.source_observed_at,
                    status = 'queued',
                    attempts = inventory_vehicle_scoring_queue.attempts,
                    priority = greatest(inventory_vehicle_scoring_queue.priority, excluded.priority),
                    last_error = null,
                    requested_at = now(),
                    claimed_at = null,
                    completed_at = null,
                    updated_at = now()
                returning lot_key
            )
            select (select count(*)::int from candidates), (select count(*)::int from upserted);
            """;
        AddParameter(command, "policy_version", LscScoringPolicy.Version);
        AddParameter(command, "limit", limit);
        AddParameter(command, "priority", BackfillPriorityScoring);
        AddParameter(command, "maximum_attempts", MaximumScoringAttempts);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new InventoryScoringBackfillResult(0, 0, 0);
        var requested = reader.GetInt32(0);
        var enqueued = reader.GetInt32(1);
        return new InventoryScoringBackfillResult(requested, enqueued, Math.Max(0, requested - enqueued));
    }

    public async Task<InventoryScoringBatchResult> ProcessScoringBatchAsync(int maximum, CancellationToken cancellationToken)
    {
        var claimed = await ClaimScoringBatchAsync(maximum, cancellationToken);
        var completed = 0;
        var failed = 0;
        var skipped = 0;
        foreach (var item in claimed)
        {
            try
            {
                var snapshot = await GetScoringSnapshotAsync(item.LotKey, cancellationToken);
                if (snapshot is null)
                {
                    await CompleteScoringQueueItemAsync(item, "skipped", null, cancellationToken);
                    skipped++;
                    continue;
                }
                if (snapshot.ObservedAt != item.SourceObservedAt)
                {
                    await EnqueueScoringCandidateAsync(item.LotKey, snapshot.Vehicle.Platform, snapshot.ObservedAt, cancellationToken);
                    skipped++;
                    continue;
                }

                var eligibility = AuctionEligibilityEvaluator.Evaluate(snapshot.Vehicle);
                var outcome = LscVehicleScoringEngine.Evaluate(snapshot.Vehicle, eligibility);
                await PersistScoringResultAsync(outcome, item.SourceObservedAt, cancellationToken);
                await CompleteScoringQueueItemAsync(item, "completed", null, cancellationToken);
                completed++;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Could not calculate LSC scoring for lot {LotKey}.", item.LotKey);
                await CompleteScoringQueueItemAsync(item, "failed", TruncateError(exception.Message), cancellationToken);
                failed++;
            }
        }
        var status = await GetScoringOperationalStatusAsync(cancellationToken);
        return new InventoryScoringBatchResult(
            claimed.Count,
            completed,
            failed,
            skipped,
            (int)Math.Min(int.MaxValue, status.Queued),
            claimed.Count(item => item.Priority >= HighPriorityScoring),
            claimed.Count(item => item.Priority < HighPriorityScoring));
    }

    public async Task<InventoryScoringOperationalStatus> GetScoringOperationalStatusAsync(CancellationToken cancellationToken)
    {
        await EnsureScoringSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select
                count(*) filter (where status = 'queued')::bigint,
                count(*) filter (where status = 'processing')::bigint,
                count(*) filter (where status = 'completed')::bigint,
                count(*) filter (where status = 'failed')::bigint,
                (select max(scored_at) from inventory_vehicle_score_current),
                min(requested_at) filter (where status = 'queued')
            from inventory_vehicle_scoring_queue;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new InventoryScoringOperationalStatus(LscScoringPolicy.Version, 0, 0, 0, 0, null, [], null, []);
        var queued = reader.GetInt64(0);
        var processing = reader.GetInt64(1);
        var completed = reader.GetInt64(2);
        var failed = reader.GetInt64(3);
        DateTimeOffset? lastScoredAt = reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4);
        DateTimeOffset? oldestQueuedAt = reader.IsDBNull(5) ? null : reader.GetFieldValue<DateTimeOffset>(5);
        await reader.DisposeAsync();
        var platforms = await GetScoringPlatformStatusesAsync(cancellationToken);
        var recentRuns = await GetRecentScoringRunsAsync(10, cancellationToken);
        return new InventoryScoringOperationalStatus(
            LscScoringPolicy.Version,
            queued,
            processing,
            completed,
            failed,
            lastScoredAt,
            platforms,
            oldestQueuedAt,
            recentRuns);
    }

    public async Task<Guid> StartScoringRunAsync(string trigger, CancellationToken cancellationToken)
    {
        await EnsureScoringSchemaAsync(cancellationToken);
        var runId = Guid.NewGuid();
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            insert into inventory_vehicle_scoring_runs (run_id, trigger, status, started_at)
            values (@run_id, @trigger, 'running', now());
            """;
        AddParameter(command, "run_id", runId);
        AddParameter(command, "trigger", trigger.Trim().ToLowerInvariant());
        await command.ExecuteNonQueryAsync(cancellationToken);
        return runId;
    }

    public async Task CompleteScoringRunAsync(Guid runId, InventoryScoringRunCompletion completion, CancellationToken cancellationToken)
    {
        await EnsureScoringSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            update inventory_vehicle_scoring_runs
            set status = @status,
                finished_at = @finished_at,
                backfill_requested = @backfill_requested,
                backfill_enqueued = @backfill_enqueued,
                claimed = @claimed,
                completed = @completed,
                failed = @failed,
                skipped = @skipped,
                remaining = @remaining,
                high_priority_claimed = @high_priority_claimed,
                backfill_claimed = @backfill_claimed,
                error = @error,
                updated_at = now()
            where run_id = @run_id;
            """;
        AddParameter(command, "run_id", runId);
        AddParameter(command, "status", completion.Status);
        AddParameter(command, "finished_at", completion.FinishedAt);
        AddParameter(command, "backfill_requested", completion.BackfillRequested);
        AddParameter(command, "backfill_enqueued", completion.BackfillEnqueued);
        AddParameter(command, "claimed", completion.Claimed);
        AddParameter(command, "completed", completion.Completed);
        AddParameter(command, "failed", completion.Failed);
        AddParameter(command, "skipped", completion.Skipped);
        AddParameter(command, "remaining", completion.Remaining);
        AddParameter(command, "high_priority_claimed", completion.HighPriorityClaimed);
        AddParameter(command, "backfill_claimed", completion.BackfillClaimed);
        AddParameter(command, "error", completion.Error);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryScoringRunSummary>> GetRecentScoringRunsAsync(int maximum, CancellationToken cancellationToken)
    {
        await EnsureScoringSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select run_id, trigger, status, started_at, finished_at, backfill_requested, backfill_enqueued,
                   claimed, completed, failed, skipped, remaining, high_priority_claimed, backfill_claimed, error
            from inventory_vehicle_scoring_runs
            order by started_at desc
            limit @limit;
            """;
        AddParameter(command, "limit", Math.Clamp(maximum, 1, 100));
        var runs = new List<InventoryScoringRunSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            runs.Add(new InventoryScoringRunSummary(
                reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), reader.GetInt32(5), reader.GetInt32(6),
                reader.GetInt32(7), reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
                reader.GetInt32(12), reader.GetInt32(13), reader.IsDBNull(14) ? null : reader.GetString(14)));
        }
        return runs;
    }

    private async Task<IReadOnlyList<InventoryScoringPlatformStatus>> GetScoringPlatformStatusesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 60);
        command.CommandText = """
            with active_inventory as (
                select lot_key, lower(coalesce(platform, 'unknown')) as platform, observed_at
                from inventory_search_current
                where is_active
            )
            select active.platform,
                   count(*)::bigint as active_count,
                   count(*) filter (where score.lot_key is not null
                       and score.policy_version = @policy_version
                       and score.source_observed_at = active.observed_at)::bigint as current_count,
                   count(*) filter (where queue.status = 'queued')::bigint as queued_count,
                   count(*) filter (where queue.status = 'processing')::bigint as processing_count,
                   count(*) filter (where queue.status = 'failed')::bigint as failed_count,
                   count(*) filter (where queue.status = 'queued' and queue.priority >= @high_priority)::bigint as high_priority_queued,
                   min(queue.requested_at) filter (where queue.status = 'queued'),
                   max(score.scored_at) filter (where score.lot_key is not null
                       and score.policy_version = @policy_version
                       and score.source_observed_at = active.observed_at)
            from active_inventory active
            left join inventory_vehicle_score_current score on score.lot_key = active.lot_key
            left join inventory_vehicle_scoring_queue queue on queue.lot_key = active.lot_key
            group by active.platform
            order by active.platform;
            """;
        AddParameter(command, "policy_version", LscScoringPolicy.Version);
        AddParameter(command, "high_priority", HighPriorityScoring);
        var statuses = new List<InventoryScoringPlatformStatus>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var active = reader.GetInt64(1);
            var current = reader.GetInt64(2);
            statuses.Add(new InventoryScoringPlatformStatus(
                reader.GetString(0), active, current, reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5),
                Math.Max(0, active - current), reader.GetInt64(6),
                reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8)));
        }
        return statuses;
    }

    public async Task<LscVehicleScoringResult?> GetScoreByLotAsync(string lotNumber, CancellationToken cancellationToken)
    {
        await EnsureScoringSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select result.lot_key, result.platform, result.status, result.pre_grade, result.buy_score,
                   result.max_points_evaluable, result.coverage_percent, result.confidence_percent,
                   result.category, result.factor_scores, result.penalties, result.reason_codes,
                   result.missing_fields, result.policy_version, result.input_hash, result.scored_at
            from inventory_vehicle_score_current current
            join inventory_vehicle_score_results result
              on result.lot_key = current.lot_key
             and result.policy_version = current.policy_version
             and result.input_hash = current.input_hash
            join inventory_search_current inventory on inventory.lot_key = current.lot_key
            where inventory.lot_number = @lot_number and inventory.is_active
            order by current.scored_at desc
            limit 1;
            """;
        AddParameter(command, "lot_number", lotNumber.Trim());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        try
        {
            return ReadScoringResult(reader);
        }
        catch (Exception exception) when (exception is JsonException or InvalidCastException or FormatException or NotSupportedException)
        {
            logger.LogWarning(exception,
                "Ignoring an unreadable full LSC scoring result for active lot {LotNumber}; the vehicle detail will use its stored scoring summary.",
                lotNumber);
            return null;
        }
    }

    private async Task<IReadOnlyList<ScoringQueueItem>> ClaimScoringBatchAsync(int maximum, CancellationToken cancellationToken)
    {
        await EnsureScoringSchemaAsync(cancellationToken);
        var limit = Math.Clamp(maximum, 1, 500);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var recover = connection.CreateCommand())
        {
            recover.Transaction = transaction;
            recover.CommandTimeout = _persistence.CommandTimeoutSeconds;
            recover.CommandText = """
                update inventory_vehicle_scoring_queue
                set status = 'queued', claimed_at = null, updated_at = now()
                where status = 'processing' and claimed_at < now() - interval '15 minutes';
                """;
            await recover.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            with high_priority as (
                select lot_key, platform, priority, requested_at
                from inventory_vehicle_scoring_queue
                where status = 'queued' and priority >= @high_priority
                order by priority desc, requested_at asc, lot_key asc
                limit @limit
            ), remaining as (
                select greatest(@limit - count(*)::int, 0) as capacity
                from high_priority
            ), low_ranked as (
                select lot_key, platform, priority, requested_at,
                       row_number() over (
                           partition by platform
                           order by requested_at asc, lot_key asc) as platform_position
                from inventory_vehicle_scoring_queue
                where status = 'queued' and priority < @high_priority
            ), low_priority as (
                select low.lot_key, low.platform, low.priority, low.requested_at
                from low_ranked low
                cross join remaining
                order by low.platform_position asc, low.platform asc, low.requested_at asc, low.lot_key asc
                limit (select capacity from remaining)
            ), candidates as (
                select lot_key, priority, requested_at from high_priority
                union all
                select lot_key, priority, requested_at from low_priority
            ), locked as (
                select queue.lot_key
                from inventory_vehicle_scoring_queue queue
                join candidates on candidates.lot_key = queue.lot_key
                where queue.status = 'queued'
                order by candidates.priority desc, candidates.requested_at asc, candidates.lot_key asc
                for update of queue skip locked
            )
            update inventory_vehicle_scoring_queue queue
            set status = 'processing', attempts = queue.attempts + 1, claimed_at = now(), updated_at = now()
            from locked
            where queue.lot_key = locked.lot_key
            returning queue.lot_key, queue.platform, queue.source_observed_at, queue.attempts, queue.priority;
            """;
        AddParameter(command, "limit", limit);
        AddParameter(command, "high_priority", HighPriorityScoring);
        var items = new List<ScoringQueueItem>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                items.Add(new ScoringQueueItem(reader.GetString(0), reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetInt32(3), reader.GetInt32(4)));
        }
        await transaction.CommitAsync(cancellationToken);
        return items;
    }

    private async Task<StoredVehicleSnapshot?> GetScoringSnapshotAsync(string lotKey, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select lot_key, observed_at, payload::text
            from inventory_search_current
            where lot_key = @lot_key and is_active;
            """;
        AddParameter(command, "lot_key", lotKey);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var rawJson = reader.GetString(2);
        var vehicle = JsonSerializer.Deserialize<AuctionVehicle>(rawJson, new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
        return vehicle is null ? null : new StoredVehicleSnapshot(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), vehicle, rawJson);
    }

    private async Task PersistScoringResultAsync(LscVehicleScoringResult outcome, DateTimeOffset sourceObservedAt, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var result = connection.CreateCommand())
        {
            result.Transaction = transaction;
            result.CommandTimeout = _persistence.CommandTimeoutSeconds;
            result.CommandText = """
                insert into inventory_vehicle_score_results (
                    lot_key, platform, status, pre_grade, buy_score, max_points_evaluable,
                    coverage_percent, confidence_percent, category, factor_scores, penalties,
                    reason_codes, missing_fields, policy_version, input_hash, source_observed_at, scored_at)
                values (
                    @lot_key, @platform, @status, @pre_grade, @buy_score, @max_points_evaluable,
                    @coverage_percent, @confidence_percent, @category, cast(@factor_scores as jsonb), cast(@penalties as jsonb),
                    cast(@reason_codes as jsonb), cast(@missing_fields as jsonb), @policy_version, @input_hash, @source_observed_at, @scored_at)
                on conflict (lot_key, policy_version, input_hash) do update set
                    status = excluded.status, pre_grade = excluded.pre_grade, buy_score = excluded.buy_score,
                    max_points_evaluable = excluded.max_points_evaluable, coverage_percent = excluded.coverage_percent,
                    confidence_percent = excluded.confidence_percent, category = excluded.category,
                    factor_scores = excluded.factor_scores, penalties = excluded.penalties,
                    reason_codes = excluded.reason_codes, missing_fields = excluded.missing_fields,
                    source_observed_at = excluded.source_observed_at, scored_at = excluded.scored_at;
                """;
            AddScoringParameters(result, outcome, sourceObservedAt);
            await result.ExecuteNonQueryAsync(cancellationToken);
        }
        await using (var current = connection.CreateCommand())
        {
            current.Transaction = transaction;
            current.CommandTimeout = _persistence.CommandTimeoutSeconds;
            current.CommandText = """
                insert into inventory_vehicle_score_current (
                    lot_key, platform, status, pre_grade, buy_score, max_points_evaluable,
                    coverage_percent, confidence_percent, category, policy_version, input_hash,
                    source_observed_at, scored_at, updated_at)
                values (
                    @lot_key, @platform, @status, @pre_grade, @buy_score, @max_points_evaluable,
                    @coverage_percent, @confidence_percent, @category, @policy_version, @input_hash,
                    @source_observed_at, @scored_at, now())
                on conflict (lot_key) do update set
                    platform = excluded.platform, status = excluded.status, pre_grade = excluded.pre_grade,
                    buy_score = excluded.buy_score, max_points_evaluable = excluded.max_points_evaluable,
                    coverage_percent = excluded.coverage_percent, confidence_percent = excluded.confidence_percent,
                    category = excluded.category, policy_version = excluded.policy_version,
                    input_hash = excluded.input_hash, source_observed_at = excluded.source_observed_at,
                    scored_at = excluded.scored_at, updated_at = now()
                where inventory_vehicle_score_current.source_observed_at <= excluded.source_observed_at;
                """;
            AddScoringParameters(current, outcome, sourceObservedAt);
            await current.ExecuteNonQueryAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static void AddScoringParameters(NpgsqlCommand command, LscVehicleScoringResult outcome, DateTimeOffset sourceObservedAt)
    {
        AddParameter(command, "lot_key", outcome.LotKey);
        AddParameter(command, "platform", outcome.Platform);
        AddParameter(command, "status", outcome.Status);
        AddParameter(command, "pre_grade", outcome.PreGrade);
        AddParameter(command, "buy_score", outcome.BuyScore);
        AddParameter(command, "max_points_evaluable", outcome.MaxPointsEvaluable);
        AddParameter(command, "coverage_percent", outcome.CoveragePercent);
        AddParameter(command, "confidence_percent", outcome.ConfidencePercent);
        AddParameter(command, "category", outcome.Category);
        AddParameter(command, "factor_scores", JsonSerializer.Serialize(outcome.Factors));
        AddParameter(command, "penalties", JsonSerializer.Serialize(outcome.Penalties));
        AddParameter(command, "reason_codes", JsonSerializer.Serialize(outcome.ReasonCodes));
        AddParameter(command, "missing_fields", JsonSerializer.Serialize(outcome.MissingFields));
        AddParameter(command, "policy_version", outcome.PolicyVersion);
        AddParameter(command, "input_hash", outcome.InputHash);
        AddParameter(command, "source_observed_at", sourceObservedAt);
        AddParameter(command, "scored_at", outcome.ScoredAt);
    }

    private async Task CompleteScoringQueueItemAsync(ScoringQueueItem item, string status, string? error, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            update inventory_vehicle_scoring_queue
            set status = case
                    when @status = 'failed' and @attempts < @maximum_attempts then 'queued'
                    else @status
                end,
                last_error = @last_error,
                completed_at = case when @status in ('completed', 'skipped', 'failed') then now() else null end,
                updated_at = now()
            where lot_key = @lot_key and source_observed_at = @source_observed_at;
            """;
        AddParameter(command, "status", status);
        AddParameter(command, "attempts", item.Attempts);
        AddParameter(command, "maximum_attempts", MaximumScoringAttempts);
        AddParameter(command, "last_error", error);
        AddParameter(command, "lot_key", item.LotKey);
        AddParameter(command, "source_observed_at", item.SourceObservedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureScoringSchemaAsync(CancellationToken cancellationToken)
    {
        if (_scoringSchemaInitialized) return;
        await ScoringSchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_scoringSchemaInitialized) return;
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
            command.CommandText = """
                create table if not exists inventory_scoring_policies (
                    policy_version text primary key,
                    display_name text not null,
                    configuration jsonb not null,
                    is_active boolean not null default true,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );
                insert into inventory_scoring_policies (policy_version, display_name, configuration)
                values ('lsc_pre_grade_v1', 'Pre-grado LSC v1',
                    '{"photos_in_scope":false,"pre_grade_max_points":60,"minimum_coverage_pct":70,"buy_score_enabled":false}'::jsonb)
                on conflict (policy_version) do nothing;

                create table if not exists inventory_vehicle_score_results (
                    lot_key text not null,
                    platform text not null,
                    status text not null,
                    pre_grade numeric,
                    buy_score numeric,
                    max_points_evaluable numeric not null,
                    coverage_percent numeric not null,
                    confidence_percent numeric not null,
                    category text,
                    factor_scores jsonb not null default '[]'::jsonb,
                    penalties jsonb not null default '[]'::jsonb,
                    reason_codes jsonb not null default '[]'::jsonb,
                    missing_fields jsonb not null default '[]'::jsonb,
                    policy_version text not null,
                    input_hash text not null,
                    source_observed_at timestamptz not null,
                    scored_at timestamptz not null,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now(),
                    primary key (lot_key, policy_version, input_hash)
                );
                create index if not exists ix_inventory_vehicle_score_results_lot_scored
                    on inventory_vehicle_score_results (lot_key, scored_at desc);

                create table if not exists inventory_vehicle_score_current (
                    lot_key text primary key,
                    platform text not null,
                    status text not null,
                    pre_grade numeric,
                    buy_score numeric,
                    max_points_evaluable numeric not null,
                    coverage_percent numeric not null,
                    confidence_percent numeric not null,
                    category text,
                    policy_version text not null,
                    input_hash text not null,
                    source_observed_at timestamptz not null,
                    scored_at timestamptz not null,
                    updated_at timestamptz not null default now()
                );
                create index if not exists ix_inventory_vehicle_score_current_pre_grade
                    on inventory_vehicle_score_current (pre_grade desc nulls last, lot_key);
                create index if not exists ix_inventory_vehicle_score_current_status
                    on inventory_vehicle_score_current (status, coverage_percent desc, lot_key);

                create table if not exists inventory_vehicle_scoring_queue (
                    lot_key text primary key,
                    platform text not null,
                    source_observed_at timestamptz not null,
                    status text not null,
                    attempts integer not null default 0,
                    priority smallint not null default 10,
                    last_error text,
                    requested_at timestamptz not null default now(),
                    claimed_at timestamptz,
                    completed_at timestamptz,
                    updated_at timestamptz not null default now()
                );
                alter table inventory_vehicle_scoring_queue
                    add column if not exists priority smallint not null default 10;

                create index if not exists ix_inventory_vehicle_scoring_queue_status_requested
                    on inventory_vehicle_scoring_queue (status, priority desc, requested_at, lot_key);

                create index if not exists ix_inventory_vehicle_scoring_queue_status_priority_requested
                    on inventory_vehicle_scoring_queue (status, priority desc, requested_at, lot_key);

                create table if not exists inventory_vehicle_scoring_runs (
                    run_id uuid primary key,
                    trigger text not null,
                    status text not null,
                    started_at timestamptz not null,
                    finished_at timestamptz,
                    backfill_requested integer not null default 0,
                    backfill_enqueued integer not null default 0,
                    claimed integer not null default 0,
                    completed integer not null default 0,
                    failed integer not null default 0,
                    skipped integer not null default 0,
                    remaining integer not null default 0,
                    high_priority_claimed integer not null default 0,
                    backfill_claimed integer not null default 0,
                    error text,
                    created_at timestamptz not null default now(),
                    updated_at timestamptz not null default now()
                );
                create index if not exists ix_inventory_vehicle_scoring_runs_started
                    on inventory_vehicle_scoring_runs (started_at desc);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _scoringSchemaInitialized = true;
        }
        finally
        {
            ScoringSchemaLock.Release();
        }
    }

    private static LscVehicleScoringResult ReadScoringResult(NpgsqlDataReader reader)
    {
        static T[] ReadJson<T>(NpgsqlDataReader source, int ordinal) =>
            JsonSerializer.Deserialize<T[]>(source.GetString(ordinal), new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true }) ?? [];
        return new LscVehicleScoringResult(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetDecimal(3),
            reader.IsDBNull(4) ? null : reader.GetDecimal(4),
            reader.GetDecimal(5),
            reader.GetDecimal(6),
            reader.GetDecimal(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            ReadJson<LscScoringFactor>(reader, 9),
            ReadJson<LscScoringPenalty>(reader, 10),
            ReadJson<string>(reader, 11),
            ReadJson<string>(reader, 12),
            reader.GetString(13),
            reader.GetString(14),
            reader.GetFieldValue<DateTimeOffset>(15));
    }

    private static string TruncateError(string value) => value.Length <= 500 ? value : value[..500];
}
