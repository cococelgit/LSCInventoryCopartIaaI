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
