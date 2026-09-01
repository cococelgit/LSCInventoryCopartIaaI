using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Lsc.Inventory.Api.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionsApiInitialImportCancellationTests
{
    [Fact]
    public async Task Cancellation_finalizes_run_without_persisting_a_vehicle()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new InMemorySnapshotStore();
        var client = new CancellingAuctionsApiClient(cancellation);
        var pipeline = new CanonicalInventoryIngestionPipeline(store);
        var processor = new AuctionsApiInitialImportProcessor(
            client,
            store,
            pipeline,
            Microsoft.Extensions.Options.Options.Create(new AuctionsApiOptions { Enabled = true, ApiKey = "test", PageSize = 1000 }),
            NullLogger<AuctionsApiInitialImportProcessor>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            processor.RunAsync("iaai", 100, persist: false, cancellation.Token));

        var history = await store.GetExecutionHistoryAsync(
            new InventoryExecutionHistoryRequest(1, 10, "iaai"), CancellationToken.None);

        var run = Assert.Single(history.Items);
        Assert.Equal("cancelled", run.Status);
        Assert.Equal(0, run.Observed);
        Assert.Equal(0, run.Created);
        Assert.Equal(0, run.Updated);
        Assert.Equal(0, run.Loaded);
    }

    private sealed class CancellingAuctionsApiClient(CancellationTokenSource source) : IAuctionsApiClient
    {
        public Task<AuctionsApiPage> GetChangedLotsAsync(AuctionsApiWindowRequest request, CancellationToken cancellationToken)
        {
            source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new AuctionsApiPage(JsonDocument.Parse("{}").RootElement, JsonDocument.Parse("{}").RootElement));
        }

        public Task<AuctionsApiPage> GetArchivedLotsAsync(AuctionsApiWindowRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
