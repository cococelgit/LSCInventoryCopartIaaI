using Lsc.Inventory.Api.Storage;
using Lsc.Inventory.Api.Workers;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionsApiImportBackgroundWorkerTests
{
    [Fact]
    public async Task Durable_queue_preserves_run_id_and_claims_once()
    {
        var store = new InMemoryAuctionsApiImportJobStore();
        var request = Request();
        await store.EnqueueAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);

        var first = await store.TryClaimAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), CancellationToken.None);
        var second = await store.TryClaimAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.NotNull(first);
        Assert.Equal(request.RunId, first!.Request.RunId);
        Assert.Null(second);
    }

    [Fact]
    public async Task Durable_queue_recovers_a_job_after_lease_expiration()
    {
        var store = new InMemoryAuctionsApiImportJobStore();
        var request = Request();
        var enqueuedAt = DateTimeOffset.UtcNow;
        await store.EnqueueAsync(request, enqueuedAt, CancellationToken.None);

        var first = await store.TryClaimAsync(enqueuedAt, TimeSpan.FromMinutes(1), CancellationToken.None);
        var recovered = await store.TryClaimAsync(enqueuedAt.AddMinutes(2), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(recovered);
        Assert.Equal(request.RunId, recovered!.Request.RunId);
        Assert.Equal(2, recovered.Attempts);
    }

    [Fact]
    public async Task Durable_queue_resumes_from_last_confirmed_page()
    {
        var store = new InMemoryAuctionsApiImportJobStore();
        var request = Request();
        var startedAt = DateTimeOffset.UtcNow;
        await store.EnqueueAsync(request, startedAt, CancellationToken.None);
        await store.TryClaimAsync(startedAt, TimeSpan.FromMinutes(1), CancellationToken.None);

        await store.CheckpointAsync(request.RunId, new AuctionsApiInitialImportProgress(4, 150, 3, 3), CancellationToken.None);
        var checkpointed = await store.GetAsync(request.RunId, CancellationToken.None);
        var recovered = await store.TryClaimAsync(startedAt.AddMinutes(2), TimeSpan.FromMinutes(1), CancellationToken.None);

        Assert.NotNull(checkpointed);
        Assert.Equal(4, checkpointed!.NextPage);
        Assert.Equal(150, checkpointed.ProcessedLots);
        Assert.NotNull(recovered);
        Assert.Equal(4, recovered!.Request.StartPage);
    }

    [Fact]
    public async Task Cancellation_marks_queued_job_and_does_not_allow_claim()
    {
        var store = new InMemoryAuctionsApiImportJobStore();
        var request = Request();
        await store.EnqueueAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.True(await store.RequestCancellationAsync(request.RunId, DateTimeOffset.UtcNow, CancellationToken.None));
        var job = await store.GetAsync(request.RunId, CancellationToken.None);
        var claimed = await store.TryClaimAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), CancellationToken.None);

        Assert.NotNull(job);
        Assert.Equal("cancelled", job!.Status);
        Assert.True(job.CancellationRequested);
        Assert.Null(claimed);
    }

    [Fact]
    public async Task Cancellation_is_idempotent_for_terminal_job()
    {
        var store = new InMemoryAuctionsApiImportJobStore();
        var request = Request();
        await store.EnqueueAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);
        var claimed = await store.TryClaimAsync(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(10), CancellationToken.None);
        await store.CompleteAsync(request.RunId, "succeeded", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.NotNull(claimed);
        Assert.False(await store.RequestCancellationAsync(request.RunId, DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal("succeeded", (await store.GetAsync(request.RunId, CancellationToken.None))!.Status);
    }

    private static AuctionsApiImportRequest Request() => new(
        Guid.NewGuid(), "iaai", 1000, false, 1, false, 0, true);
}
