using Lsc.Inventory.Api.Services;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class ProviderRequestLimiterTests
{
    [Fact]
    public async Task Spaces_requests_for_the_same_provider()
    {
        using var limiter = new ProviderRequestLimiter();
        var starts = new List<DateTimeOffset>();

        await limiter.WaitAsync("auctions-api", TimeSpan.FromMilliseconds(500), CancellationToken.None);
        starts.Add(DateTimeOffset.UtcNow);
        await limiter.WaitAsync("auctions-api", TimeSpan.FromMilliseconds(500), CancellationToken.None);
        starts.Add(DateTimeOffset.UtcNow);

        Assert.True((starts[1] - starts[0]).TotalMilliseconds >= 450);
    }

    [Fact]
    public async Task Keeps_provider_budgets_independent()
    {
        using var limiter = new ProviderRequestLimiter();
        var first = DateTimeOffset.UtcNow;
        await limiter.WaitAsync("auctions-api", TimeSpan.FromSeconds(1), CancellationToken.None);
        await limiter.WaitAsync("apibara", TimeSpan.FromSeconds(1), CancellationToken.None);
        Assert.True((DateTimeOffset.UtcNow - first).TotalMilliseconds < 500);
    }

    [Fact]
    public async Task Cancellation_does_not_leave_the_gate_held()
    {
        using var limiter = new ProviderRequestLimiter();
        await limiter.WaitAsync("auctions-api", TimeSpan.FromSeconds(1), CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            limiter.WaitAsync("auctions-api", TimeSpan.FromSeconds(1), cancelled.Token));
    }
}
