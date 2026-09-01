namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    private const string NationalSyncSchemaSql = """
        select pg_advisory_xact_lock(hashtext('lsc:national-sync-schema:v1'));

        create table if not exists inventory_sync_leases (
            lease_name text primary key,
            owner_run_id uuid not null,
            expires_at timestamptz not null,
            updated_at timestamptz not null default now()
        );

        create table if not exists iaai_national_sync_state (
            stream_name text primary key,
            cycle_id uuid,
            cursor text,
            pages_completed integer not null default 0,
            lots_observed integer not null default 0,
            cycle_completed boolean not null default true,
            initial_backfill_completed boolean not null default false,
            updated_at timestamptz not null default now()
        );

        create table if not exists iaai_national_cycle_observations (
            cycle_id uuid not null,
            lot_key text not null,
            observed_at timestamptz not null,
            primary key (cycle_id, lot_key)
        );

        create index if not exists ix_iaai_national_cycle_observations_cycle
            on iaai_national_cycle_observations (cycle_id);

        create index if not exists ix_inventory_sync_leases_expires
            on inventory_sync_leases (expires_at);
        """;

    private async Task EnsureNationalSyncSchemaAsync(CancellationToken cancellationToken)
    {
        if (_nationalSyncSchemaInitialized) return;

        await NationalSyncSchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_nationalSyncSchemaInitialized) return;

            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = NationalSyncSchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _nationalSyncSchemaInitialized = true;
        }
        finally
        {
            NationalSyncSchemaLock.Release();
        }
    }
}
