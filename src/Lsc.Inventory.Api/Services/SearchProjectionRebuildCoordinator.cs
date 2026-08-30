using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Logging;

namespace Lsc.Inventory.Api.Services;

public interface IInventorySearchProjectionRebuildRunner
{
    Task<InventorySearchProjectionStatus> RebuildAsync(CancellationToken cancellationToken);
}

public sealed class InventorySearchProjectionRebuildRunner(IInventorySnapshotStore store) : IInventorySearchProjectionRebuildRunner
{
    public Task<InventorySearchProjectionStatus> RebuildAsync(CancellationToken cancellationToken) =>
        store.RebuildSearchProjectionAsync(cancellationToken);
}

public sealed record SearchProjectionRebuildExecutionStatus(
    bool IsRunning,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    InventorySearchProjectionStatus? LastSuccessfulResult,
    string? LastError);

public sealed record SearchProjectionRebuildRequestResult(
    bool Accepted,
    SearchProjectionRebuildExecutionStatus Status);

public interface ISearchProjectionRebuildCoordinator
{
    SearchProjectionRebuildRequestResult RequestRebuild();
    SearchProjectionRebuildExecutionStatus GetStatus();
}

public sealed class SearchProjectionRebuildCoordinator : ISearchProjectionRebuildCoordinator
{
    private readonly object _sync = new();
    private readonly IInventorySearchProjectionRebuildRunner _runner;
    private readonly ILogger<SearchProjectionRebuildCoordinator> _logger;
    private readonly Func<CancellationToken> _applicationStopping;
    private SearchProjectionRebuildExecutionStatus _status = new(false, null, null, null, null);

    public SearchProjectionRebuildCoordinator(
        IInventorySearchProjectionRebuildRunner runner,
        ILogger<SearchProjectionRebuildCoordinator> logger,
        IHostApplicationLifetime applicationLifetime)
        : this(runner, logger, () => applicationLifetime.ApplicationStopping)
    {
    }

    public SearchProjectionRebuildCoordinator(
        IInventorySearchProjectionRebuildRunner runner,
        ILogger<SearchProjectionRebuildCoordinator> logger,
        Func<CancellationToken> applicationStopping)
    {
        _runner = runner;
        _logger = logger;
        _applicationStopping = applicationStopping;
    }

    public SearchProjectionRebuildRequestResult RequestRebuild()
    {
        lock (_sync)
        {
            if (_status.IsRunning)
                return new(false, _status);

            _status = new SearchProjectionRebuildExecutionStatus(true, DateTimeOffset.UtcNow, null, _status.LastSuccessfulResult, null);
            _ = ExecuteAsync();
            return new(true, _status);
        }
    }

    public SearchProjectionRebuildExecutionStatus GetStatus()
    {
        lock (_sync)
            return _status;
    }

    private async Task ExecuteAsync()
    {
        try
        {
            var result = await _runner.RebuildAsync(_applicationStopping());
            lock (_sync)
                _status = new SearchProjectionRebuildExecutionStatus(false, _status.StartedAt, DateTimeOffset.UtcNow, result, null);

            _logger.LogInformation("Search projection rebuild completed. Rows={Rows} DurationMs={DurationMs}", result.Rows, result.Duration.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (_applicationStopping().IsCancellationRequested)
        {
            lock (_sync)
                _status = new SearchProjectionRebuildExecutionStatus(false, _status.StartedAt, DateTimeOffset.UtcNow, _status.LastSuccessfulResult, "Rebuild cancelled because the application is stopping.");
            _logger.LogWarning("Search projection rebuild cancelled because the application is stopping.");
        }
        catch (Exception exception)
        {
            lock (_sync)
                _status = new SearchProjectionRebuildExecutionStatus(false, _status.StartedAt, DateTimeOffset.UtcNow, _status.LastSuccessfulResult, exception.Message);
            _logger.LogError(exception, "Search projection rebuild failed.");
        }
    }
}
