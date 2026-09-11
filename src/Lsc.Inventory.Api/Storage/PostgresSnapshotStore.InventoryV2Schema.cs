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
            await reader.CloseAsync();

            if (createdTables.Length != InventoryV2OwnedTables.Count)
            {
                var missing = InventoryV2OwnedTables.Except(createdTables, StringComparer.Ordinal).ToArray();
                throw new InvalidOperationException($"Inventory V2 schema is incomplete. Missing: {string.Join(", ", missing)}.");
            }

            await transaction.CommitAsync(cancellationToken);
            return new InventoryV2SchemaPreparationResult(
                InventoryV2SchemaVersion,
                createdTables,
                WriterEnabled: false,
                ReaderEnabled: false,
                PreparedAt: DateTimeOffset.UtcNow);
        }
        finally
        {
            InventoryV2SchemaLock.Release();
        }
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
