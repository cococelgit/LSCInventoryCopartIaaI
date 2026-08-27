using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;
using Lsc.Inventory.Api.Workers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartMediaEnrichmentProcessorTests
{
    [Fact]
    public async Task Enrichment_persists_one_hd_photo_per_sequence_and_marks_the_listing_resolved()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.UtcNow;
        var source = new AuctionVehicle
        {
            Platform = "copart",
            LotNumber = "1001",
            Vin = "1HGCM82633A004352",
            Media = new MediaInfo { Photos = ["https://cs.copart.com/v1/thumb.jpg"], ThumbnailsCount = 1 },
            RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["Image URL"] = "https://inventoryv2.copart.io/v1/lotImages/1001?brand=XXX" })
        };
        await store.PersistAsync(source, observedAt, CancellationToken.None);
        var resolver = new FakeMediaResolver();
        var options = new OptionsWrapper<CopartExcelOptions>(new CopartExcelOptions { MediaEnrichmentBatchSize = 10, MediaResolutionConcurrency = 2 });
        var processor = new CopartMediaEnrichmentProcessor(store, resolver, options, NullLogger<CopartMediaEnrichmentProcessor>.Instance);

        var result = await processor.RunAsync(CancellationToken.None);
        var enriched = (await store.GetRecentAsync(10, CancellationToken.None)).Single().Vehicle;

        Assert.True(result.Processed);
        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.Resolved);
        Assert.Equal(2, enriched.Media!.Photos!.Count);
        Assert.Equal(2, enriched.Media.ThumbnailsCount);
        Assert.True(enriched.AdditionalData!.ContainsKey("copart_media_resolution"));
        Assert.Empty(await store.GetCopartMediaCandidatesAsync(10, CancellationToken.None));
    }

    private sealed class FakeMediaResolver : ICopartMediaResolver
    {
        public Task<CopartMediaResolution> ResolveAsync(AuctionVehicle vehicle, CancellationToken cancellationToken) =>
            Task.FromResult(new CopartMediaResolution(
                vehicle with { Media = new MediaInfo { Photos = ["https://cs.copart.com/v1/hd-1.jpg", "https://cs.copart.com/v1/hd-2.jpg"], ThumbnailsCount = 2 } },
                true,
                2,
                2,
                null));
    }
}
