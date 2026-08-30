using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class SearchProjectionRebuildCoordinatorTests
{
    [Fact]
    public async Task RequestRebuild_runs_once_and_reports_completion_without_using_request_cancellation()
    {
        var runner = new ControlledRunner();
        var coordinator = new SearchProjectionRebuildCoordinator(runner, NullLogger<SearchProjectionRebuildCoordinator>.Instance, () => CancellationToken.None);

        var requested = coordinator.RequestRebuild();
        var duplicate = coordinator.RequestRebuild();

        Assert.True(requested.Accepted);
        Assert.True(requested.Status.IsRunning);
        Assert.False(duplicate.Accepted);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, runner.Calls);

        runner.Completion.SetResult(new InventorySearchProjectionStatus(true, 123_918, DateTimeOffset.Parse("2026-08-29T13:54:41Z"), DateTimeOffset.Parse("2026-08-29T14:01:00Z"), TimeSpan.FromSeconds(19)));
        await WaitUntilAsync(() => !coordinator.GetStatus().IsRunning);

        var status = coordinator.GetStatus();
        Assert.Null(status.LastError);
        Assert.Equal(123_918, status.LastSuccessfulResult?.Rows);
    }

    [Fact]
    public async Task RequestRebuild_records_failure_and_allows_a_later_retry()
    {
        var runner = new ControlledRunner();
        var coordinator = new SearchProjectionRebuildCoordinator(runner, NullLogger<SearchProjectionRebuildCoordinator>.Instance, () => CancellationToken.None);

        Assert.True(coordinator.RequestRebuild().Accepted);
        await runner.Started.Task.WaitAsync(TimeSpan.FromSeconds(1));
        runner.Completion.SetException(new InvalidOperationException("projection query failed"));
        await WaitUntilAsync(() => !coordinator.GetStatus().IsRunning);

        Assert.Equal("projection query failed", coordinator.GetStatus().LastError);
        Assert.True(coordinator.RequestRebuild().Accepted);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }

        Assert.Fail("Condition was not satisfied within 500 ms.");
    }

    private sealed class ControlledRunner : IInventorySearchProjectionRebuildRunner
    {
        public int Calls { get; private set; }
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<InventorySearchProjectionStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<InventorySearchProjectionStatus> RebuildAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult(true);
            return Completion.Task;
        }
    }
}
