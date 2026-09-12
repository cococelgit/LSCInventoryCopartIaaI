using System.Reflection;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class InventoryV2SchemaContractTests
{
    [Fact]
    public void Migration_IsAdditiveIdempotentTypedAndLimitedToV2Objects()
    {
        var sql = ReadRepositoryFile("infra/sql/20260911_inventory_v2_schema.sql");
        var normalized = Normalize(sql);

        Assert.Contains("pg_advisory_xact_lock", normalized);
        Assert.Contains("create table if not exists inventory_current_v2", normalized);
        Assert.Contains("create table if not exists inventory_media_current_v2", normalized);
        Assert.Contains("create table if not exists inventory_sale_attempts_v2", normalized);
        Assert.Contains("create table if not exists inventory_tombstones_v2", normalized);
        Assert.Contains("create table if not exists inventory_sync_checkpoints_v2", normalized);
        Assert.Contains("create table if not exists inventory_v2_schema_state", normalized);
        Assert.Contains("on conflict (schema_name) do update", normalized);

        Assert.DoesNotContain(" json ", normalized);
        Assert.DoesNotContain("jsonb", normalized);
        Assert.Contains("alter table inventory_current_v2", normalized);
        Assert.DoesNotContain("drop table", normalized);
        Assert.DoesNotContain("truncate", normalized);
        Assert.DoesNotContain("delete from", normalized);
        Assert.DoesNotContain("auction_lot_versions", normalized);
        Assert.DoesNotContain("inventory_search_current", normalized);
        Assert.DoesNotContain("inventory_lot_lifecycle", normalized);
    }

    [Fact]
    public void CurrentState_HasTypedChangeGroupsLifecycleAndMinimalIndexes()
    {
        var normalized = Normalize(ReadRepositoryFile("infra/sql/20260911_inventory_v2_schema.sql"));

        foreach (var required in new[]
        {
            "primary key (platform, lot_number)",
            "source_updated_at timestamptz",
            "identity_hash text not null",
            "spec_hash text not null",
            "condition_hash text not null",
            "auction_hash text not null",
            "seller_location_hash text not null",
            "media_hash text not null",
            "score_input_hash text not null",
            "search_hash text not null",
            "is_active boolean not null default true",
            "deactivated_at timestamptz",
            "where is_active",
            "where not is_active and deactivated_at is not null",
        })
        {
            Assert.Contains(required, normalized);
        }
    }

    [Fact]
    public void EmbeddedMigration_MatchesRepositoryMigration()
    {
        var assembly = typeof(PostgresSnapshotStore).Assembly;
        using var stream = assembly.GetManifestResourceStream("InventoryV2Schema.sql");
        Assert.NotNull(stream);
        using var reader = new StreamReader(stream!);

        var embedded = Normalize(reader.ReadToEnd());
        var repository = Normalize(ReadRepositoryFile("infra/sql/20260911_inventory_v2_schema.sql"));
        Assert.Equal(repository, embedded);
    }

    [Fact]
    public void PreparationCommand_IsExplicitAndDoesNotEnableReadersOrWriters()
    {
        var program = Normalize(ReadRepositoryFile("src/Lsc.Inventory.Api/Program.cs"));
        var migration = Normalize(ReadRepositoryFile("infra/sql/20260911_inventory_v2_schema.sql"));
        var preparation = Normalize(ReadRepositoryFile("src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.InventoryV2Schema.cs"));

        Assert.Contains("--prepare-inventory-v2-schema", program);
        Assert.Contains("prepareinventoryv2schemaasync", program);
        Assert.Contains("writer_enabled boolean not null default false", migration);
        Assert.Contains("reader_enabled boolean not null default false", migration);
        Assert.Contains("values ('inventory-current-v2', 2, false, false)", migration);
        Assert.Contains("select schema_version, writer_enabled, reader_enabled, prepared_at", preparation);
        Assert.Contains("if (writerenabled || readerenabled)", preparation);
    }

    [Fact]
    public void State_disable_path_allows_schema_v1_to_be_quiesced_before_an_additive_migration()
    {
        var schema = ReadRepositoryFile("src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.InventoryV2Schema.cs");
        var normalized = Normalize(schema);

        Assert.Contains("schema_version >= @schemaversion or @enabled = false", normalized);
        Assert.Contains("reader_enabled = false", normalized);
    }

    [Fact]
    public void PreparationWorkflow_IsManualCommitPinnedIsolatedAndNonDestructive()
    {
        var workflow = Normalize(ReadRepositoryFile(".github/workflows/prepare-inventory-v2-schema.yml"));

        Assert.Contains("workflow_dispatch", workflow);
        Assert.Contains("prepare_inventory_v2_schema", workflow);
        Assert.Contains("test \"$(git rev-parse head)\" = \"$expected_sha\"", workflow);
        Assert.Contains("test \"$trigger\" = manual", workflow);
        Assert.Contains("test \"$active\" = 0", workflow);
        Assert.Contains("temp_job: job-lsc-v2-schema-prod", workflow);
        Assert.Contains("--prepare-inventory-v2-schema", workflow);
        Assert.Contains("run_once 1", workflow);
        Assert.Contains("run_once 2", workflow);
        Assert.Contains("az containerapp job delete --name \"$temp_job\"", workflow);

        Assert.DoesNotContain("az containerapp job update --name \"$source_job\"", workflow);
        Assert.DoesNotContain("scheduletriggerconfig={", workflow);
        Assert.DoesNotContain("drop table", workflow);
        Assert.DoesNotContain("truncate", workflow);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Repository file not found: {relativePath}");
    }

    private static string Normalize(string value) => string.Join(
        ' ',
        value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        .ToLowerInvariant();
}
