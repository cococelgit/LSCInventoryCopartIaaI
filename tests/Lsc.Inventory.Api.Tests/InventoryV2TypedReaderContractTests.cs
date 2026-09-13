using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class InventoryV2TypedReaderContractTests
{
    [Fact]
    public void Typed_reader_reconstructs_public_vehicle_shape_without_database_payload_reads()
    {
        var reader = File.ReadAllText(FindRepositoryRootFile("src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.InventoryV2Readers.cs"));

        Assert.Contains("inventory_current_v2 latest", reader, StringComparison.Ordinal);
        Assert.Contains("inventory_media_current_v2", reader, StringComparison.Ordinal);
        Assert.Contains("BuildInventoryV2Vehicle", reader, StringComparison.Ordinal);
        Assert.Contains("JsonSerializer.Serialize(vehicle", reader, StringComparison.Ordinal);
        Assert.DoesNotContain("payload", reader, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Azure.Storage.Blobs", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_reader_preserves_database_timestamp_precision()
    {
        var reader = File.ReadAllText(FindRepositoryRootFile("src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.InventoryV2Readers.cs"));

        Assert.Contains("DateTime.SpecifyKind(date, DateTimeKind.Utc)", reader, StringComparison.Ordinal);
        Assert.Contains("DateTimeOffset date => date", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_and_detail_have_the_same_reversible_double_guard()
    {
        var store = File.ReadAllText(FindRepositoryRootFile("src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.cs"));
        var facets = File.ReadAllText(FindRepositoryRootFile("src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.FacetsV2.cs"));

        Assert.DoesNotContain("if (_inventoryV2.ReaderEnabled)", store, StringComparison.Ordinal);
        Assert.Contains("EnsureInventoryV2SchemaAsync", store, StringComparison.Ordinal);
        Assert.Contains("IsInventoryV2ReaderEnabledAsync", store, StringComparison.Ordinal);
        Assert.Contains("return await SearchInventoryV2Async", store, StringComparison.Ordinal);
        Assert.Contains("return await GetByPlatformAndLotInventoryV2Async", store, StringComparison.Ordinal);
        Assert.Contains("Inventory V2 facets require both writer and reader state", facets, StringComparison.Ordinal);
    }

    [Fact]
    public void Typed_reader_preserves_critical_filter_semantics()
    {
        var reader = File.ReadAllText(FindRepositoryRootFile("src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.InventoryV2Readers.cs"));

        Assert.Contains("latest.buy_now_usd > 0", reader, StringComparison.Ordinal);
        Assert.Contains("latest.media_has_photos", reader, StringComparison.Ordinal);
        Assert.Contains("latest.current_bid_usd is not null", reader, StringComparison.Ordinal);
        Assert.Contains("latest.is_active", reader, StringComparison.Ordinal);
        Assert.Contains("latest.primary_damage", reader, StringComparison.Ordinal);
        Assert.Contains("latest.auction_at", reader, StringComparison.Ordinal);
    }

    [Fact]
    public void Search_facets_and_detail_share_the_current_score_publication_gate()
    {
        var reader = File.ReadAllText(FindRepositoryRootFile("src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.InventoryV2Readers.cs"));
        var facets = File.ReadAllText(FindRepositoryRootFile("src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.FacetsV2.cs"));

        Assert.Contains("RequireCurrentForPublication", reader, StringComparison.Ordinal);
        Assert.Contains("{scoreAlias}.policy_version = @v2_current_score_policy", reader, StringComparison.Ordinal);
        Assert.Contains("{scoreAlias}.input_hash = {inventoryAlias}.score_input_hash", reader, StringComparison.Ordinal);
        Assert.Contains("if (requirePublishedScore) AddCurrentScorePublicationGate", reader, StringComparison.Ordinal);
        Assert.Contains("requirePublishedScore: false", File.ReadAllText(FindRepositoryRootFile("src/Lsc.Inventory.Api/Storage/PostgresSnapshotStore.Scoring.cs")), StringComparison.Ordinal);
        Assert.Contains("_scoring.RequireCurrentForPublication", facets, StringComparison.Ordinal);
        Assert.Contains("AddCurrentScorePublicationGate(command, fixedWhere)", facets, StringComparison.Ordinal);
    }

    private static string FindRepositoryRootFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relativePath);
    }
}
