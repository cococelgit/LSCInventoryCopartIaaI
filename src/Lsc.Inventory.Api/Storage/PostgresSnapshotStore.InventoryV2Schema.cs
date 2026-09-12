using System.Reflection;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    internal const int InventoryV2SchemaVersion = 1;
    internal const string InventoryV2SchemaResourceName = "InventoryV2Schema.sql";

    internal static readonly IReadOnlyList<string> InventoryV2OwnedTables =
    [
        "inventory_current_v2",
        "inventory_media_current_v2",
        "inventory_sale_attempts_v2",
        "inventory_tombstones_v2",
        "inventory_sync_checkpoints_v2",
        "inventory_v2_schema_state",
    ];

    public async Task<InventoryV2SchemaPreparationResult> PrepareInventoryV2SchemaAsync(CancellationToken cancellationToken)
    {
        await InventoryV2SchemaLock.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
            command.CommandText = ReadInventoryV2SchemaSql();
            await command.ExecuteNonQueryAsync(cancellationToken);

            await using var verify = connection.CreateCommand();
            verify.Transaction = transaction;
            verify.CommandTimeout = _persistence.CommandTimeoutSeconds;
            verify.CommandText = """
                select
                    to_regclass('public.inventory_current_v2') is not null,
                    to_regclass('public.inventory_media_current_v2') is not null,
                    to_regclass('public.inventory_sale_attempts_v2') is not null,
                    to_regclass('public.inventory_tombstones_v2') is not null,
                    to_regclass('public.inventory_sync_checkpoints_v2') is not null,
                    to_regclass('public.inventory_v2_schema_state') is not null;
                """;
            await using var reader = await verify.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Inventory V2 schema verification returned no result.");

            var createdTables = InventoryV2OwnedTables
                .Where((_, index) => reader.GetBoolean(index))
                .ToArray();
            if (createdTables.Length != InventoryV2OwnedTables.Count)
            {
                var missing = InventoryV2OwnedTables.Except(createdTables, StringComparer.Ordinal).ToArray();
                throw new InvalidOperationException($"Inventory V2 schema is incomplete. Missing: {string.Join(", ", missing)}.");
            }

            await reader.CloseAsync();

            await using var state = connection.CreateCommand();
            state.Transaction = transaction;
            state.CommandTimeout = _persistence.CommandTimeoutSeconds;
            state.CommandText = """
                select schema_version, writer_enabled, reader_enabled, prepared_at
                from inventory_v2_schema_state
                where schema_name = 'inventory-current-v2';
                """;
            await using var stateReader = await state.ExecuteReaderAsync(cancellationToken);
            if (!await stateReader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Inventory V2 schema state is missing.");

            var schemaVersion = stateReader.GetInt32(0);
            var writerEnabled = stateReader.GetBoolean(1);
            var readerEnabled = stateReader.GetBoolean(2);
            var preparedAt = stateReader.GetFieldValue<DateTimeOffset>(3);

            if (schemaVersion < InventoryV2SchemaVersion)
                throw new InvalidOperationException($"Inventory V2 schema state is outdated: {schemaVersion}.");
            if (writerEnabled || readerEnabled)
                throw new InvalidOperationException("Inventory V2 schema preparation must not enable V2 writers or readers.");

            await stateReader.CloseAsync();

            await transaction.CommitAsync(cancellationToken);
            return new InventoryV2SchemaPreparationResult(
                schemaVersion,
                createdTables,
                writerEnabled,
                readerEnabled,
                preparedAt);
        }
        finally
        {
            InventoryV2SchemaLock.Release();
        }
    }

    public async Task<InventoryV2WriterStateResult> SetInventoryV2WriterStateAsync(bool enabled, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            update inventory_v2_schema_state
            set writer_enabled = @enabled,
                updated_at = now()
            where schema_name = 'inventory-current-v2'
              and schema_version >= @schemaVersion
              and reader_enabled = false
            returning schema_version, writer_enabled, reader_enabled, updated_at;
            """;
        AddParameter(command, "enabled", enabled);
        AddParameter(command, "schemaVersion", InventoryV2SchemaVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Inventory V2 writer state could not be changed; schema is missing, outdated, or reader is enabled.");
        return new InventoryV2WriterStateResult(
            reader.GetInt32(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.GetFieldValue<DateTimeOffset>(3));
    }

    public async Task<InventoryV2ReaderStateResult> SetInventoryV2ReaderStateAsync(bool enabled, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            update inventory_v2_schema_state
            set reader_enabled = @enabled,
                updated_at = now()
            where schema_name = 'inventory-current-v2'
              and schema_version >= @schemaVersion
            returning schema_version, writer_enabled, reader_enabled, updated_at;
            """;
        AddParameter(command, "enabled", enabled);
        AddParameter(command, "schemaVersion", InventoryV2SchemaVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Inventory V2 reader cannot be changed until the schema exists and is current.");
        return new InventoryV2ReaderStateResult(
            reader.GetInt32(0),
            reader.GetBoolean(1),
            reader.GetBoolean(2),
            reader.GetFieldValue<DateTimeOffset>(3));
    }

    public async Task<InventoryV2ShadowResetResult> ResetInventoryV2ShadowAsync(string platform, CancellationToken cancellationToken)
    {
        var normalizedPlatform = platform.Trim().ToLowerInvariant();
        if (normalizedPlatform is not ("copart" or "iaai"))
            throw new ArgumentOutOfRangeException(nameof(platform));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var guard = connection.CreateCommand();
        guard.Transaction = transaction;
        guard.CommandTimeout = _persistence.CommandTimeoutSeconds;
        guard.CommandText = """
            select writer_enabled, reader_enabled
            from inventory_v2_schema_state
            where schema_name = 'inventory-current-v2'
            for update;
            """;
        await using (var reader = await guard.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Inventory V2 schema state is missing.");
            if (reader.GetBoolean(0) || reader.GetBoolean(1))
                throw new InvalidOperationException("Inventory V2 shadow reset requires writer_enabled=false and reader_enabled=false.");
        }

        async Task<int> DeleteAsync(string table)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = $"delete from {table} where platform = @platform;";
            AddParameter(command, "platform", normalizedPlatform);
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var media = await DeleteAsync("inventory_media_current_v2");
        var attempts = await DeleteAsync("inventory_sale_attempts_v2");
        var tombstones = await DeleteAsync("inventory_tombstones_v2");
        var checkpoints = await DeleteAsync("inventory_sync_checkpoints_v2");
        var current = await DeleteAsync("inventory_current_v2");
        await transaction.CommitAsync(cancellationToken);
        return new InventoryV2ShadowResetResult(normalizedPlatform, current, media, attempts, tombstones, checkpoints);
    }

    public async Task<InventoryDataResetResult> ResetInventoryDataAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var guard = connection.CreateCommand();
        guard.Transaction = transaction;
        guard.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 1800);
        guard.CommandText = """
            select writer_enabled, reader_enabled
            from inventory_v2_schema_state
            where schema_name = 'inventory-current-v2'
            for update;
            """;
        await using (var reader = await guard.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Inventory V2 schema state is missing.");
        }

        await using (var disable = connection.CreateCommand())
        {
            disable.Transaction = transaction;
            disable.CommandTimeout = _persistence.CommandTimeoutSeconds;
            disable.CommandText = """
                update inventory_v2_schema_state
                set writer_enabled = false,
                    reader_enabled = false,
                    updated_at = now()
                where schema_name = 'inventory-current-v2';
                """;
            await disable.ExecuteNonQueryAsync(cancellationToken);
        }

        var tables = new[]
        {
            "inventory_media_current_v2", "inventory_sale_attempts_v2", "inventory_tombstones_v2",
            "inventory_sync_checkpoints_v2", "inventory_current_v2",
            "inventory_search_facet_counts", "inventory_search_projection_state",
            "inventory_vehicle_scoring_queue", "inventory_vehicle_score_results", "inventory_vehicle_score_current", "inventory_vehicle_scoring_runs",
            "inventory_lot_lifecycle", "eligibility_decisions",
            "inventory_sync_run_events", "inventory_execution_run_metrics", "inventory_sync_runs", "inventory_sync_leases",
            "provider_usage_snapshots", "copart_snapshot_manifests", "auctions_api_import_jobs", "iaai_national_cycle_observations"
        };
        var existingTables = new List<string>();
        foreach (var table in tables)
        {
            await using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandTimeout = _persistence.CommandTimeoutSeconds;
            exists.CommandText = "select to_regclass(@qualified) is not null;";
            AddParameter(exists, "qualified", $"public.{table}");
            if (Convert.ToBoolean(await exists.ExecuteScalarAsync(cancellationToken)))
                existingTables.Add(table);
        }

        var beforeRows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in existingTables)
        {
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 1800);
            count.CommandText = $"select count(*) from public.{table};";
            beforeRows[table] = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        if (existingTables.Count > 0)
        {
            await using var truncate = connection.CreateCommand();
            truncate.Transaction = transaction;
            truncate.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 1800);
            truncate.CommandText = $"truncate table {string.Join(", ", existingTables.Select(table => $"public.{table}"))};";
            await truncate.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        var afterRows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in existingTables)
        {
            await using var count = connection.CreateCommand();
            count.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 1800);
            count.CommandText = $"select count(*) from public.{table};";
            afterRows[table] = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        var deletedRows = existingTables.ToDictionary(
            table => table,
            table => beforeRows[table] - afterRows[table],
            StringComparer.Ordinal);
        return new InventoryDataResetResult(beforeRows, afterRows, deletedRows);
    }

    public async Task<LegacyInventoryDropResult> DropLegacyInventoryAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var guard = connection.CreateCommand();
        guard.Transaction = transaction;
        guard.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
        guard.CommandText = """
            select writer_enabled, reader_enabled
            from inventory_v2_schema_state
            where schema_name = 'inventory-current-v2'
            for update;
            """;
        await using (var reader = await guard.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Inventory V2 schema state is missing.");
            if (reader.GetBoolean(0) || reader.GetBoolean(1))
                throw new InvalidOperationException("Legacy inventory DROP requires writer_enabled=false and reader_enabled=false.");
        }

        var legacyTables = new[] { "inventory_search_current", "auction_lot_versions", "auction_lots" };
        var existing = new List<string>();
        foreach (var table in legacyTables)
        {
            await using var exists = connection.CreateCommand();
            exists.Transaction = transaction;
            exists.CommandTimeout = _persistence.CommandTimeoutSeconds;
            exists.CommandText = "select to_regclass(@qualified) is not null;";
            AddParameter(exists, "qualified", $"public.{table}");
            if (Convert.ToBoolean(await exists.ExecuteScalarAsync(cancellationToken))) existing.Add(table);
        }

        var beforeRows = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in existing)
        {
            await using var count = connection.CreateCommand();
            count.Transaction = transaction;
            count.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
            count.CommandText = $"select count(*) from public.{table};";
            beforeRows[table] = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken));
        }

        foreach (var table in existing)
        {
            await using var drop = connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
            drop.CommandText = $"drop table public.{table};";
            await drop.ExecuteNonQueryAsync(cancellationToken);
        }

        var remaining = new List<string>();
        foreach (var table in legacyTables)
        {
            await using var verify = connection.CreateCommand();
            verify.Transaction = transaction;
            verify.CommandTimeout = _persistence.CommandTimeoutSeconds;
            verify.CommandText = "select to_regclass(@qualified)::text;";
            AddParameter(verify, "qualified", $"public.{table}");
            if (await verify.ExecuteScalarAsync(cancellationToken) is not null and not DBNull) remaining.Add(table);
        }
        if (remaining.Count > 0)
            throw new InvalidOperationException($"Legacy tables remain after DROP: {string.Join(", ", remaining)}.");

        await transaction.CommitAsync(cancellationToken);
        return new LegacyInventoryDropResult(beforeRows, existing, remaining);
    }

    private static string ReadInventoryV2SchemaSql()
    {
        using var stream = typeof(PostgresSnapshotStore).Assembly.GetManifestResourceStream(InventoryV2SchemaResourceName)
            ?? throw new InvalidOperationException($"Embedded migration {InventoryV2SchemaResourceName} was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

public sealed record InventoryV2SchemaPreparationResult(
    int SchemaVersion,
    IReadOnlyList<string> Tables,
    bool WriterEnabled,
    bool ReaderEnabled,
    DateTimeOffset PreparedAt);

public sealed record InventoryV2WriterStateResult(
    int SchemaVersion,
    bool WriterEnabled,
    bool ReaderEnabled,
    DateTimeOffset UpdatedAt);

public sealed record InventoryV2ReaderStateResult(
    int SchemaVersion,
    bool WriterEnabled,
    bool ReaderEnabled,
    DateTimeOffset UpdatedAt);

public sealed record InventoryV2ShadowResetResult(
    string Platform,
    int CurrentRows,
    int MediaRows,
    int SaleAttemptRows,
    int TombstoneRows,
    int CheckpointRows);

public sealed record InventoryDataResetResult(
    IReadOnlyDictionary<string, long> BeforeRows,
    IReadOnlyDictionary<string, long> AfterRows,
    IReadOnlyDictionary<string, long> DeletedRows);
public sealed record LegacyInventoryDropResult(
    IReadOnlyDictionary<string, long> BeforeRows,
    IReadOnlyList<string> DroppedTables,
    IReadOnlyList<string> RemainingTables);
