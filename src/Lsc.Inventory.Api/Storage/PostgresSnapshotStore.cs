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
    ILogger<PostgresSnapshotStore> logger,
    IFacetsV2SharedCache? facetsV2SharedCache = null,
    IOptions<InventoryV2Options>? inventoryV2Options = null) : IInventorySnapshotStore, IAuctionsApiImportJobStore, IInventoryV2BatchWriter
{
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static readonly SemaphoreSlim AuditSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim SearchProjectionSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim EligibilitySchemaLock = new(1, 1);
    private static readonly SemaphoreSlim LifecycleSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim ScoringSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim NationalSyncSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim InventoryV2SchemaLock = new(1, 1);
    private static bool _auditSchemaInitialized;
    private static bool _eligibilitySchemaInitialized;
    private static bool _lifecycleSchemaInitialized;
    private static bool _scoringSchemaInitialized;
    private static bool _nationalSyncSchemaInitialized;
    private readonly PersistenceOptions _persistence = persistenceOptions.Value;
    private readonly InventoryV2Options _inventoryV2 = inventoryV2Options?.Value ?? new InventoryV2Options();
    private readonly IFacetsV2SharedCache _facetsV2SharedCache = facetsV2SharedCache ?? DisabledFacetsV2SharedCache.Instance;
    private readonly SemaphoreSlim _databaseTokenLock = new(1, 1);
    private AccessToken _cachedDatabaseAccessToken;
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
        await EnsureSellerClassificationSchemaAsync(databaseConnection, cancellationToken);
        logger.LogInformation("Bootstrapped the database and least-privilege runtime principal.");
    }

    private async Task EnsureSellerClassificationSchemaAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            create table if not exists public.seller_classifications (
                id bigserial primary key,
                platform text not null default 'unknown',
                seller_name_normalized text not null,
                seller_name_raw_last_seen text,
                category text not null default 'unknown',
                confidence numeric(5,4) not null default 0,
                needs_review boolean not null default true,
                reason text,
                evidence_type text not null default 'insufficient_evidence',
                model text,
                prompt_version text not null default 'seller_classifier_v1',
                first_seen_at timestamptz not null default now(),
                last_seen_at timestamptz not null default now(),
                classified_at timestamptz,
                created_at timestamptz not null default now(),
                updated_at timestamptz not null default now(),
                constraint seller_classifications_category_ck check (category in ('insurance','dealer','finance','rental_fleet','government','repossession_bank','other','unknown','unclassified')),
                constraint seller_classifications_confidence_ck check (confidence >= 0 and confidence <= 1),
                constraint seller_classifications_evidence_ck check (evidence_type in ('deterministic_rule','provider_field','name_only','insufficient_evidence')),
                constraint seller_classifications_platform_name_uq unique (platform, seller_name_normalized)
            );
            create index if not exists seller_classifications_category_idx on public.seller_classifications (category);
            create index if not exists seller_classifications_review_idx on public.seller_classifications (needs_review, updated_at);
            create index if not exists seller_classifications_name_idx on public.seller_classifications (seller_name_normalized);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public Task<InventoryLotPersistenceResult> PersistAsync(
        AuctionVehicle vehicle,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken,
        Guid? runId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("Legacy single-row persistence has been removed; use the Inventory V2 batch writer.");
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
    }

    public async Task UpdateSyncRunProgressAsync(Guid runId, InventorySyncRunProgress progress, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var metrics = connection.CreateCommand();
        metrics.CommandTimeout = _persistence.CommandTimeoutSeconds;
        metrics.CommandText = """
            insert into inventory_execution_run_metrics (
                run_id, loaded_count, marked_count, discarded_count, quarantined_count, error_count, pages_processed,
                cycle_completed, failures, updated_at)
            values (@run_id, @loaded_count, @marked_count, @discarded_count, @quarantined_count, @error_count, @pages_processed,
                false, '[]'::jsonb, now())
            on conflict (run_id) do update set
                loaded_count = excluded.loaded_count, marked_count = excluded.marked_count,
                discarded_count = excluded.discarded_count, quarantined_count = excluded.quarantined_count,
                error_count = excluded.error_count, pages_processed = excluded.pages_processed, updated_at = now();
            """;
        AddParameter(metrics, "run_id", runId);
        AddParameter(metrics, "loaded_count", progress.Loaded);
        AddParameter(metrics, "marked_count", progress.Marked);
        AddParameter(metrics, "discarded_count", progress.Discarded);
        AddParameter(metrics, "quarantined_count", progress.Quarantined);
        AddParameter(metrics, "error_count", progress.Errors);
        AddParameter(metrics, "pages_processed", progress.PagesProcessed);
        await metrics.ExecuteNonQueryAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            update inventory_sync_runs
            set vehicles_observed = @vehicles_observed, requests_issued = @requests_issued
            where run_id = @run_id and status = 'running';
            """;
        AddParameter(command, "run_id", runId);
        AddParameter(command, "vehicles_observed", progress.VehiclesObserved);
        AddParameter(command, "requests_issued", progress.RequestsIssued);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        AddParameter(command, "audit_blob_name", "postgresql-only");
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
                (select count(*) from inventory_sale_attempts_v2) as versions,
                count(*) filter (where nullif(btrim(vin), '') is not null) as vin_present,
                count(*) filter (where nullif(btrim(title), '') is not null) as title_present,
                count(*) filter (where nullif(btrim(primary_damage), '') is not null) as damage_present,
                count(*) filter (where odometer_miles is not null) as odometer_present,
                count(*) filter (where current_bid_usd is not null) as current_bid_present,
                count(*) filter (where auction_at is not null) as auction_date_present,
                count(*) filter (where media_has_photos) as lots_with_photos
            from inventory_current_v2 where is_active;
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
            select lot_key, vin, title, location_state, current_bid_usd, auction_at, primary_damage, odometer, case when media_has_photos then 1 else 0 end
            from inventory_current_v2
            where is_active
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
            where c.relname in ('inventory_current_v2', 'inventory_media_current_v2', 'inventory_sync_runs', 'provider_usage_snapshots')
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
        await EnsureInventoryV2SchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select lot_key, source_url
            from inventory_media_current_v2
            where source_url is not null and btrim(source_url) <> ''
            order by lot_key, position;
            """;

        var lots = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var lotKey = reader.GetString(0);
            var url = reader.GetString(1);
            if (!lots.TryGetValue(lotKey, out var photos))
                lots[lotKey] = photos = [];
            photos.Add(url);
        }

        var result = lots.Select(pair =>
        {
            var allPhotos = pair.Value.ToArray();
            var publicPhotos = allPhotos
                .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp) &&
                    string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.UserInfo))
                .Take(6)
                .ToArray();
            return new
            {
                LotKey = pair.Key,
                PhotosReported = allPhotos.Length,
                PublicPhotos = publicPhotos,
                PhotosWithQueryString = allPhotos.Count(url => Uri.TryCreate(url, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Query))
            };
        }).ToArray();

        return JsonSerializer.Serialize(result);
    }

    public Task<IReadOnlyList<StoredVehicleSnapshot>> GetIaaIConditionBackfillCandidatesAsync(int maximum, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<StoredVehicleSnapshot>>([]);
    }

    public Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartCatchUpCandidatesAsync(int maximum, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<StoredVehicleSnapshot>>([]);
    }

    public async Task<IReadOnlyCollection<StoredVehicleSnapshot>> GetRecentAsync(int maximum, CancellationToken cancellationToken)
    {
        await EnsureInventoryV2SchemaAsync(cancellationToken);
        if (!await IsInventoryV2ReaderEnabledAsync(cancellationToken))
            throw new InvalidOperationException("Inventory V2 reader is disabled; legacy V1 recent fallback has been removed.");
        return await GetRecentInventoryV2Async(maximum, cancellationToken);
    }

    public async Task<InventorySearchPage> SearchAsync(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        await EnsureInventoryV2SchemaAsync(cancellationToken);
        if (!await IsInventoryV2ReaderEnabledAsync(cancellationToken))
            throw new InvalidOperationException("Inventory V2 reader is disabled; legacy V1 search fallback has been removed.");
        return await SearchInventoryV2Async(request, cancellationToken);
    }

    public async Task<InventorySearchSummary> GetInventorySearchSummaryAsync(InventorySearchRequest request, CancellationToken cancellationToken)
    {
        await EnsureInventoryV2SchemaAsync(cancellationToken);
        if (!await IsInventoryV2ReaderEnabledAsync(cancellationToken))
            throw new InvalidOperationException("Inventory V2 reader is disabled; legacy V1 summary fallback has been removed.");
        return await GetInventorySearchSummaryV2Async(request, cancellationToken);
    }

    public Task<SellerTaxonomyAudit> GetSellerTaxonomyAuditAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new SellerTaxonomyAudit(0, 0, 0, 0, 0, [], DateTimeOffset.UtcNow));
    }

    public async Task<InventorySearchProjectionStatus> GetSearchProjectionStatusAsync(CancellationToken cancellationToken)
    {
        await EnsureInventoryV2SchemaAsync(cancellationToken);
        if (!await IsInventoryV2ReaderEnabledAsync(cancellationToken))
            throw new InvalidOperationException("Inventory V2 reader is disabled.");
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = "select count(*)::bigint, max(last_seen_at) from inventory_current_v2 where is_active;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new InventorySearchProjectionStatus(true, 0, null, null, TimeSpan.Zero);
        var rows = reader.GetInt64(0);
        DateTimeOffset? generatedAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1);
        return new InventorySearchProjectionStatus(true, rows, generatedAt, null, TimeSpan.Zero);
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
                count(*) filter (where lower(platform) = 'copart' and nullif(btrim(title_type), '') is not null)::bigint
            from inventory_current_v2
            where is_active;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new CopartTitleTaxonomyCoverage(version, 0, 0, 0m, false, DateTimeOffset.UtcNow);
        var total = reader.GetInt64(0);
        var classified = reader.GetInt64(1);
        var coverage = total == 0 ? 0m : decimal.Round(classified * 100m / total, 2);
        return new CopartTitleTaxonomyCoverage(version, total, classified, coverage, total > 0 && coverage >= 95m, DateTimeOffset.UtcNow);
    }


    public async Task<StoredVehicleSnapshot?> GetByLotAsync(string lotNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(lotNumber)) throw new ArgumentException("Lot number is required.", nameof(lotNumber));
        await EnsureInventoryV2SchemaAsync(cancellationToken);
        if (!await IsInventoryV2ReaderEnabledAsync(cancellationToken))
            throw new InvalidOperationException("Inventory V2 reader is disabled; legacy V1 detail fallback has been removed.");
        return await GetByLotInventoryV2Async(lotNumber, cancellationToken);
    }

    public async Task<StoredVehicleSnapshot?> GetByPlatformAndLotAsync(string platform, string lotNumber, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(platform)) throw new ArgumentException("Platform is required.", nameof(platform));
        if (string.IsNullOrWhiteSpace(lotNumber)) throw new ArgumentException("Lot number is required.", nameof(lotNumber));
        await EnsureInventoryV2SchemaAsync(cancellationToken);
        if (!await IsInventoryV2ReaderEnabledAsync(cancellationToken))
            throw new InvalidOperationException("Inventory V2 reader is disabled; legacy V1 detail fallback has been removed.");
        return await GetByPlatformAndLotInventoryV2Async(platform, lotNumber, cancellationToken);
    }

    private static string SqlTitleCategoryExpression(string alias)
    {
        var normalizedSource = "regexp_replace(upper(coalesce(nullif(btrim(" + alias + ".title_type), ''), 'NO REPORTADO')), '[-/_,.]+', ' ', 'g')";
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
            where.Add("concat_ws(' ', latest.lot_key, latest.lot_number, latest.vin, latest.make, latest.model, latest.title, latest.title) ilike @query");
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
        AddAny("title_categories", request.TitleCategories, "latest.title_group");
        AddAny("states", request.States, "latest.location_state");
        AddAny("cities", request.Cities, "latest.location_city");
        AddAny("facilities", request.Facilities, "latest.location_display");
        AddAny("primary_damages", request.PrimaryDamages, "latest.primary_damage");
        AddAny("secondary_damages", request.SecondaryDamages, "latest.secondary_damage");
        AddAny("seller_types", request.SellerTypes, SqlSellerTypeTaxonomy("nullif(btrim(latest.seller_type), '')"));
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
        if (request.WithPhotosOnly == true) where.Add("(latest.media_has_photos)");
        if (request.WithBidOnly == true) where.Add("latest.current_bid_usd is not null");
        if (string.Equals(request.KeyMode, "with", StringComparison.OrdinalIgnoreCase)) where.Add("latest.has_key is true");
        if (string.Equals(request.KeyMode, "without", StringComparison.OrdinalIgnoreCase)) where.Add("latest.has_key is false");
        if (request.ProviderEstimateFrom.HasValue) { where.Add("latest.provider_estimate_to >= @provider_estimate_from"); AddParameter(command, "provider_estimate_from", request.ProviderEstimateFrom.Value); }
        if (request.ProviderEstimateTo.HasValue) { where.Add("latest.provider_estimate_from <= @provider_estimate_to"); AddParameter(command, "provider_estimate_to", request.ProviderEstimateTo.Value); }
        if (request.EngineSizeFrom.HasValue) { where.Add("latest.engine_size_liters >= @engine_size_from"); AddParameter(command, "engine_size_from", request.EngineSizeFrom.Value); }
        if (request.EngineSizeTo.HasValue) { where.Add("latest.engine_size_liters <= @engine_size_to"); AddParameter(command, "engine_size_to", request.EngineSizeTo.Value); }
        if (request.HorsepowerFrom.HasValue) { where.Add("latest.horsepower >= @horsepower_from"); AddParameter(command, "horsepower_from", request.HorsepowerFrom.Value); }
        if (request.HorsepowerTo.HasValue) { where.Add("latest.horsepower <= @horsepower_to"); AddParameter(command, "horsepower_to", request.HorsepowerTo.Value); }
        if (string.Equals(request.AuctionStatus, "open", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like any(array['%open%', '%active%'])");
        if (string.Equals(request.AuctionStatus, "live", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like '%live%'");
        if (string.Equals(request.AuctionStatus, "finished", StringComparison.OrdinalIgnoreCase)) where.Add("lower(concat_ws(' ', latest.auction_state, latest.lot_status, latest.lot_sub_status)) like any(array['%finished%', '%ended%', '%sold%'])");
    }

    private static string GetSearchOrdering(string? sort)
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
        if (runId is not null)
            foreach (var lotKey in deactivated)
                await RecordSyncRunEventAsync(new InventorySyncRunEvent(runId.Value, normalizedPlatform, lotKey, lotKey.Split(':').LastOrDefault(), null, "deactivated", ["provider-archived"], [], archivedAt), cancellationToken);
        return deactivated.Count;
    }

    public Task<InventoryReconciliationResult> ReconcileSourceAsync(string platform, IReadOnlyCollection<string> observedLotKeys, bool isCompleteSnapshot, DateTimeOffset observedAt, CancellationToken cancellationToken, Guid? runId = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new InventoryReconciliationResult(platform.Trim().ToLowerInvariant(), false, observedLotKeys.Count, 0, 0, 0));
    }

    private Task EnsureSchemaAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
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

    private Task EnsureSearchProjectionSchemaAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
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
            SslMode = _persistence.RequireTls ? SslMode.VerifyFull : SslMode.Disable,
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
}
