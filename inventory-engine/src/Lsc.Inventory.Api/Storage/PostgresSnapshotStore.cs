using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed class PostgresSnapshotStore(
    IOptions<PersistenceOptions> persistenceOptions,
    IOptions<BlobAuditOptions> blobOptions,
    ILogger<PostgresSnapshotStore> logger) : IInventorySnapshotStore
{
    private static readonly SemaphoreSlim SchemaLock = new(1, 1);
    private static readonly SemaphoreSlim CopartSchemaLock = new(1, 1);
    private static readonly SemaphoreSlim EligibilitySchemaLock = new(1, 1);
    private static readonly SemaphoreSlim LifecycleSchemaLock = new(1, 1);
    private static bool _schemaInitialized;
    private static bool _copartSchemaInitialized;
    private static bool _eligibilitySchemaInitialized;
    private static bool _lifecycleSchemaInitialized;
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
        var observedAtUtc = observedAt.ToUniversalTime();
        var auctionAtUtc = vehicle.Auction?.AuctionAt?.ToUniversalTime();
        var identity = BuildIdentity(vehicle);
        var rawJson = JsonSerializer.Serialize(vehicle);
        var payloadHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawJson))).ToLowerInvariant();
        var blobName = BuildBlobName(identity, observedAtUtc, payloadHash);

        await EnsureSchemaAsync(cancellationToken);
        await EnsureLifecycleSchemaAsync(cancellationToken);
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
    }

    public async Task<Guid> StartSyncRunAsync(InventorySyncRunStart start, CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
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

    public async Task<CopartSnapshotRegistration> TryRegisterCopartSnapshotAsync(CopartSnapshotReceipt receipt, decimal minimumRowCountRatio, int baselineSnapshotCount, CancellationToken cancellationToken)
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

        await blobClient.UploadAsync(BinaryData.FromString(rawJson), overwrite: false, cancellationToken: cancellationToken);
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
