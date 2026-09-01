using System.Reflection;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class NationalSyncSchemaDefinitionTests
{
    [Fact]
    public void Schema_IsAdditiveIdempotentAndLimitedToOwnedNationalObjects()
    {
        var field = typeof(PostgresSnapshotStore).GetField(
            "NationalSyncSchemaSql",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);

        var sql = Assert.IsType<string>(field!.GetRawConstantValue());
        var normalized = sql.ToLowerInvariant();

        Assert.Contains("pg_advisory_xact_lock", normalized);
        Assert.Contains("create table if not exists inventory_sync_leases", normalized);
        Assert.Contains("create table if not exists iaai_national_sync_state", normalized);
        Assert.Contains("create table if not exists iaai_national_cycle_observations", normalized);
        Assert.Contains("create index if not exists ix_inventory_sync_leases_expires", normalized);
        Assert.Contains("create index if not exists ix_iaai_national_cycle_observations_cycle", normalized);

        Assert.DoesNotContain("alter table", normalized);
        Assert.DoesNotContain("drop table", normalized);
        Assert.DoesNotContain("truncate", normalized);
        Assert.DoesNotContain("schema_migrations", normalized);
        Assert.DoesNotContain("inventory_sync_runs", normalized);
        Assert.DoesNotContain("auction_lots", normalized);
    }
}
