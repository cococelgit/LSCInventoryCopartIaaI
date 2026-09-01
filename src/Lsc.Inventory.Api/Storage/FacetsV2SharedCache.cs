using System.Text.Json;
using Azure.Identity;
using Lsc.Inventory.Api.Options;
using Microsoft.Azure.StackExchangeRedis;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Lsc.Inventory.Api.Storage;

public interface IFacetsV2SharedCache
{
    bool IsConfigured { get; }
    TimeSpan TimeToLive { get; }
    TimeSpan LockTimeToLive { get; }
    FacetsV2SharedCacheDiagnostics GetDiagnostics();
    Task<InventoryFacetsV2Response?> GetAsync(string key, CancellationToken cancellationToken);
    Task SetAsync(string key, InventoryFacetsV2Response response, CancellationToken cancellationToken);
    Task<string?> TryAcquireLockAsync(string key, CancellationToken cancellationToken);
    Task ReleaseLockAsync(string key, string ownerToken, CancellationToken cancellationToken);
}

public sealed record FacetsV2SharedCacheDiagnostics(
    bool Configured,
    string ConnectionState,
    long ReadHits,
    long ReadMisses,
    long ReadFailures,
    long WriteSuccesses,
    long WriteFailures,
    long LockAcquired,
    long LockContention,
    long LockFailures,
    long LockReleaseFailures,
    string? LastFailureStage,
    string? LastFailureType,
    DateTimeOffset? LastFailureAt);

public sealed class DisabledFacetsV2SharedCache : IFacetsV2SharedCache
{
    public static readonly DisabledFacetsV2SharedCache Instance = new();
    public bool IsConfigured => false;
    public TimeSpan TimeToLive => TimeSpan.Zero;
    public TimeSpan LockTimeToLive => TimeSpan.Zero;
    public FacetsV2SharedCacheDiagnostics GetDiagnostics() => new(false, "disabled", 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null);
    public Task<InventoryFacetsV2Response?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult<InventoryFacetsV2Response?>(null);
    public Task SetAsync(string key, InventoryFacetsV2Response response, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<string?> TryAcquireLockAsync(string key, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    public Task ReleaseLockAsync(string key, string ownerToken, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class AzureManagedRedisFacetsV2SharedCache : IFacetsV2SharedCache, IAsyncDisposable
{
    private const string ReleaseLockScript = "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly FacetsRedisOptions _options;
    private readonly ILogger<AzureManagedRedisFacetsV2SharedCache> _logger;
    private readonly object _connectionLock = new();
    private Task<ConnectionMultiplexer>? _connectionTask;
    private long _readHits;
    private long _readMisses;
    private long _readFailures;
    private long _writeSuccesses;
    private long _writeFailures;
    private long _lockAcquired;
    private long _lockContention;
    private long _lockFailures;
    private long _lockReleaseFailures;
    private int _connectionState;
    private string? _lastFailureStage;
    private string? _lastFailureType;
    private DateTimeOffset? _lastFailureAt;

    public AzureManagedRedisFacetsV2SharedCache(
        IOptions<FacetsRedisOptions> options,
        ILogger<AzureManagedRedisFacetsV2SharedCache> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => _options.IsConfigured;
    public TimeSpan TimeToLive => TimeSpan.FromSeconds(_options.TimeToLiveSeconds);
    public TimeSpan LockTimeToLive => TimeSpan.FromMilliseconds(_options.DistributedLockMilliseconds);
    public FacetsV2SharedCacheDiagnostics GetDiagnostics() => new(
        IsConfigured,
        !IsConfigured ? "disabled" : Volatile.Read(ref _connectionState) switch { 1 => "connected", 2 => "unavailable", _ => "not_attempted" },
        Interlocked.Read(ref _readHits),
        Interlocked.Read(ref _readMisses),
        Interlocked.Read(ref _readFailures),
        Interlocked.Read(ref _writeSuccesses),
        Interlocked.Read(ref _writeFailures),
        Interlocked.Read(ref _lockAcquired),
        Interlocked.Read(ref _lockContention),
        Interlocked.Read(ref _lockFailures),
        Interlocked.Read(ref _lockReleaseFailures),
        Volatile.Read(ref _lastFailureStage),
        Volatile.Read(ref _lastFailureType),
        _lastFailureAt);

    public async Task<InventoryFacetsV2Response?> GetAsync(string key, CancellationToken cancellationToken)
    {
        var database = await GetDatabaseAsync(cancellationToken);
        if (database is null) return null;

        try
        {
            var value = await database.StringGetAsync(CacheKey(key)).WaitAsync(cancellationToken);
            if (!value.HasValue)
            {
                Interlocked.Increment(ref _readMisses);
                return null;
            }
            Interlocked.Increment(ref _readHits);
            return JsonSerializer.Deserialize<InventoryFacetsV2Response>(value!, JsonOptions);
        }
        catch (Exception exception) when (IsCacheException(exception))
        {
            Interlocked.Increment(ref _readFailures);
            RecordFailure("read", exception);
            _logger.LogWarning(exception, "Facets V2 shared cache read failed; falling back to PostgreSQL.");
            return null;
        }
    }

    public async Task SetAsync(string key, InventoryFacetsV2Response response, CancellationToken cancellationToken)
    {
        var database = await GetDatabaseAsync(cancellationToken);
        if (database is null) return;

        try
        {
            var payload = JsonSerializer.Serialize(response, JsonOptions);
            await database.StringSetAsync(CacheKey(key), payload, TimeToLive).WaitAsync(cancellationToken);
            Interlocked.Increment(ref _writeSuccesses);
        }
        catch (Exception exception) when (IsCacheException(exception))
        {
            Interlocked.Increment(ref _writeFailures);
            RecordFailure("write", exception);
            _logger.LogWarning(exception, "Facets V2 shared cache write failed; response remains available from PostgreSQL.");
        }
    }

    public async Task<string?> TryAcquireLockAsync(string key, CancellationToken cancellationToken)
    {
        var database = await GetDatabaseAsync(cancellationToken);
        if (database is null) return null;

        var ownerToken = Guid.NewGuid().ToString("N");
        try
        {
            var acquired = await database.StringSetAsync(LockKey(key), ownerToken, LockTimeToLive, When.NotExists).WaitAsync(cancellationToken);
            if (acquired) Interlocked.Increment(ref _lockAcquired);
            else Interlocked.Increment(ref _lockContention);
            return acquired ? ownerToken : null;
        }
        catch (Exception exception) when (IsCacheException(exception))
        {
            Interlocked.Increment(ref _lockFailures);
            RecordFailure("lock", exception);
            _logger.LogWarning(exception, "Facets V2 distributed lock failed; using local single-flight only.");
            return null;
        }
    }

    public async Task ReleaseLockAsync(string key, string ownerToken, CancellationToken cancellationToken)
    {
        var database = await GetDatabaseAsync(cancellationToken);
        if (database is null) return;

        try
        {
            await database.ScriptEvaluateAsync(ReleaseLockScript, [LockKey(key)], [ownerToken]).WaitAsync(cancellationToken);
        }
        catch (Exception exception) when (IsCacheException(exception))
        {
            Interlocked.Increment(ref _lockReleaseFailures);
            RecordFailure("lock_release", exception);
            _logger.LogWarning(exception, "Facets V2 distributed lock release failed; lock will expire safely.");
        }
    }

    private async Task<IDatabase?> GetDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!IsConfigured) return null;
        try
        {
            Task<ConnectionMultiplexer> connectionTask;
            lock (_connectionLock)
            {
                _connectionTask ??= ConnectAsync();
                connectionTask = _connectionTask;
            }
            var connection = await connectionTask.WaitAsync(cancellationToken);
            Volatile.Write(ref _connectionState, 1);
            return connection.GetDatabase();
        }
        catch (Exception exception) when (IsCacheException(exception))
        {
            lock (_connectionLock)
            {
                if (_connectionTask is { IsFaulted: true }) _connectionTask = null;
            }
            Volatile.Write(ref _connectionState, 2);
            RecordFailure("connect", exception);
            _logger.LogWarning(exception, "Facets V2 shared cache is unavailable; falling back to PostgreSQL.");
            return null;
        }
    }

    private async Task<ConnectionMultiplexer> ConnectAsync()
    {
        var configuration = ConfigurationOptions.Parse(_options.Endpoint);
        configuration.Ssl = true;
        configuration.AbortOnConnectFail = false;
        configuration.ConnectRetry = 1;
        configuration.ConnectTimeout = 2000;
        configuration.SyncTimeout = 2000;
        configuration.AsyncTimeout = 2000;
        configuration.Protocol = RedisProtocol.Resp3;
        configuration.ClientName = "lsc-inventory-facets-v2";
        await configuration.ConfigureForAzureWithUserAssignedManagedIdentityAsync(_options.ManagedIdentityClientId);
        return await ConnectionMultiplexer.ConnectAsync(configuration);
    }

    private string CacheKey(string key) => $"{_options.KeyPrefix}{key}";
    private string LockKey(string key) => $"{_options.KeyPrefix}lock:{key}";

    private void RecordFailure(string stage, Exception exception)
    {
        Volatile.Write(ref _lastFailureStage, stage);
        Volatile.Write(ref _lastFailureType, exception.GetType().Name);
        _lastFailureAt = DateTimeOffset.UtcNow;
    }

    private static bool IsCacheException(Exception exception) => exception is RedisException or AuthenticationFailedException or TimeoutException or OperationCanceledException or JsonException;

    public async ValueTask DisposeAsync()
    {
        Task<ConnectionMultiplexer>? connectionTask;
        lock (_connectionLock)
        {
            connectionTask = _connectionTask;
            _connectionTask = null;
        }
        if (connectionTask is { IsCompletedSuccessfully: true })
        {
            var connection = await connectionTask;
            await connection.CloseAsync();
            connection.Dispose();
        }
    }
}
