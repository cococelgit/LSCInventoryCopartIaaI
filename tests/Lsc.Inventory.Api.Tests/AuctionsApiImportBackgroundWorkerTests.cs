using Lsc.Inventory.Api.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionsApiImportBackgroundWorkerTests
{
    [Fact]
    public void Queue_preserves_run_id_and_rejects_when_bounded_capacity_is_full()
    {
        var queue = new AuctionsApiImportQueue();
        static AuctionsApiImportRequest Request() => new(
            Guid.NewGuid(), "iaai", 1000, false, 1, false, 0, true);

        Assert.True(queue.TryEnqueue(Request()));
        Assert.True(queue.TryEnqueue(Request()));
        Assert.True(queue.TryEnqueue(Request()));
        Assert.True(queue.TryEnqueue(Request()));
        Assert.False(queue.TryEnqueue(Request()));
    }

    [Fact]
    public async Task Queue_returns_the_same_request_run_id()
    {
        var queue = new AuctionsApiImportQueue();
        var expected = Request();
        Assert.True(queue.TryEnqueue(expected));

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        await using var enumerator = queue.ReadAllAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(expected, enumerator.Current);
    }

    private static AuctionsApiImportRequest Request() => new(
        Guid.NewGuid(), "iaai", 1000, false, 1, false, 0, true);
}
