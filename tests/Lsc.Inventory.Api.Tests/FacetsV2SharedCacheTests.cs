using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class FacetsV2SharedCacheTests
{
    [Fact]
    public async Task Disabled_cache_is_a_safe_noop_without_a_redis_connection()
    {
        var cache = new AzureManagedRedisFacetsV2SharedCache(
            Microsoft.Extensions.Options.Options.Create(new FacetsRedisOptions { Enabled = false }),
            NullLogger<AzureManagedRedisFacetsV2SharedCache>.Instance);

        Assert.False(cache.IsConfigured);
        var diagnostics = cache.GetDiagnostics();
        Assert.False(diagnostics.Configured);
        Assert.Equal("disabled", diagnostics.ConnectionState);
        Assert.Null(diagnostics.LastFailureStage);
        Assert.Null(await cache.GetAsync("source:fingerprint", CancellationToken.None));
        Assert.Null(await cache.TryAcquireLockAsync("source:fingerprint", CancellationToken.None));
        await cache.SetAsync("source:fingerprint", CreateResponse(), CancellationToken.None);
        await cache.DisposeAsync();
    }

    [Fact]
    public void Redis_requires_enabled_endpoint_and_managed_identity()
    {
        Assert.False(new FacetsRedisOptions { Enabled = true }.IsConfigured);
        Assert.False(new FacetsRedisOptions { Enabled = true, Endpoint = "cache.example:10000" }.IsConfigured);
        Assert.True(new FacetsRedisOptions
        {
            Enabled = true,
            Endpoint = "cache.example:10000",
            ManagedIdentityClientId = "11111111-1111-1111-1111-111111111111"
        }.IsConfigured);
    }

    [Fact]
    public void Shared_cache_contract_uses_tls_entra_scoped_keys_and_safe_lock_release()
    {
        var source = File.ReadAllText(FindRepositoryFile("FacetsV2SharedCache.cs"));

        Assert.Contains("ConfigureForAzureWithUserAssignedManagedIdentityAsync", source, StringComparison.Ordinal);
        Assert.Contains("configuration.Ssl = true", source, StringComparison.Ordinal);
        Assert.Contains("RedisProtocol.Resp3", source, StringComparison.Ordinal);
        Assert.Contains("AbortOnConnectFail = false", source, StringComparison.Ordinal);
        Assert.Equal("lsc:facets:v2:", new FacetsRedisOptions().KeyPrefix);
        Assert.Contains("redis.call('get', KEYS[1])", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AccessKey", source, StringComparison.Ordinal);
        Assert.Contains("FacetsV2SharedCacheDiagnostics", source, StringComparison.Ordinal);
        Assert.Contains("RecordFailure", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Distributed_cache_uses_source_version_and_preserves_local_fallback()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.FacetsV2.cs"));

        Assert.Contains("$\"{version.SourceVersion}:{fingerprint}\"", source, StringComparison.Ordinal);
        Assert.Contains("TryGetFacetsV2Cache(cacheKey", source, StringComparison.Ordinal);
        Assert.Contains("_facetsV2SharedCache.GetAsync(cacheKey", source, StringComparison.Ordinal);
        Assert.Contains("shared-hit", source, StringComparison.Ordinal);
        Assert.Contains("shared-wait", source, StringComparison.Ordinal);
        Assert.Contains("ExecuteFacetsV2Async", source, StringComparison.Ordinal);
    }

    private static InventoryFacetsV2Response CreateResponse() => new(
        1,
        DateTimeOffset.Parse("2026-08-31T12:00:00Z"),
        "inventory-current-v1:1:1",
        1,
        "miss",
        new Dictionary<string, IReadOnlyList<InventoryFacetValue>>(),
        new InventoryFacetsV2Ranges(),
        []);

    private static string FindRepositoryFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Lsc.Inventory.Api", "Storage", name);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(name);
    }
}
