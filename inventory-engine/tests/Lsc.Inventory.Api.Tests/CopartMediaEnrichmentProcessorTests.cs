using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
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
    public async Task Enrichment_persists_hd_gallery_preserves_original_references_and_reports_metrics()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.UtcNow;
        await store.PersistAsync(Source("1001"), observedAt, CancellationToken.None);
        var processor = CreateProcessor(store, new FakeMediaResolver(_ => HdResolution(_)));

        var result = await processor.RunAsync(CancellationToken.None);
        var enriched = (await store.GetRecentAsync(10, CancellationToken.None)).Single().Vehicle;

        Assert.True(result.Processed);
        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.Resolved);
        Assert.Equal(2, enriched.Media!.Photos!.Count);
        Assert.Equal(2, enriched.Media.ThumbnailsCount);
        Assert.True(enriched.AdditionalData!.ContainsKey("copart_media_resolution"));
        Assert.Equal(new[] { "https://cs.copart.com/v1/thumb.jpg" }, enriched.AdditionalData["copart_media_original_photos"].Deserialize<string[]>());
        Assert.NotNull(result.Metrics);
        Assert.Equal(1, result.Metrics!.GalleryCount);
        Assert.Equal(2, result.Metrics.HdImages);
        Assert.Equal(0, result.Metrics.ThumbnailOnly);
        Assert.Empty(await store.GetCopartMediaCandidatesAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task Complete_gallery_is_not_a_media_candidate_and_is_not_resolved_again()
    {
        var store = new InMemorySnapshotStore();
        var source = Source("1002") with { Media = new MediaInfo { Photos = ["https://cs.copart.com/v1/one.jpg", "https://cs.copart.com/v1/two.jpg"], ThumbnailsCount = 2 } };
        await store.PersistAsync(source, DateTimeOffset.UtcNow, CancellationToken.None);
        var resolver = new FakeMediaResolver(HdResolution);
        var processor = CreateProcessor(store, resolver);

        var result = await processor.RunAsync(CancellationToken.None);

        Assert.True(result.Processed);
        Assert.Equal(0, result.Candidates);
        Assert.Equal(0, resolver.Calls);
        Assert.Equal(0, result.Metrics!.GalleryCount);
    }

    [Fact]
    public async Task Not_found_catalog_is_controlled_and_does_not_change_eligibility_or_score()
    {
        var store = new InMemorySnapshotStore();
        var source = Source("1003");
        var observedAt = DateTimeOffset.UtcNow;
        await store.PersistAsync(source, observedAt, CancellationToken.None);
        var eligibility = AuctionEligibilityEvaluator.Evaluate(source, observedAt);
        var beforeScore = await store.PersistScoringResultAsync(source, eligibility, observedAt, CancellationToken.None);
        var processor = CreateProcessor(store, new FakeMediaResolver(vehicle => new CopartMediaResolution(vehicle, false, 0, 0, "NOT_FOUND_404")));

        var result = await processor.RunAsync(CancellationToken.None);
        var after = Assert.Single(await store.GetRecentAsync(10, CancellationToken.None));
        var afterScore = await store.GetScoreByLotAsync("1003", CancellationToken.None);

        Assert.True(result.Processed);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Metrics!.NotFound404);
        Assert.Equal(new[] { "https://cs.copart.com/v1/thumb.jpg" }, after.Vehicle.Media!.Photos);
        Assert.Equal(beforeScore.InputHash, afterScore!.InputHash);
        var eligibilityAfter = AuctionEligibilityEvaluator.Evaluate(after.Vehicle, observedAt);
        Assert.Equal(eligibility.LoadToSystem, eligibilityAfter.LoadToSystem);
        Assert.Equal(eligibility.Decision, eligibilityAfter.Decision);
        Assert.Equal(eligibility.Flags.Select(flag => flag.Code).Order(), eligibilityAfter.Flags.Select(flag => flag.Code).Order());
        Assert.Single(await store.GetCopartMediaCandidatesAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task Invalid_url_and_transient_failure_are_counted_without_touching_iaai()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistAsync(Source("1004"), DateTimeOffset.UtcNow, CancellationToken.None);
        await store.PersistAsync(new AuctionVehicle
        {
            Platform = "iaai",
            LotNumber = "2004",
            Media = new MediaInfo { Photos = ["https://example.com/thumb.jpg"], ThumbnailsCount = 1 }
        }, DateTimeOffset.UtcNow, CancellationToken.None);
        var resolver = new FakeMediaResolver(vehicle => vehicle.LotNumber == "1004"
            ? new CopartMediaResolution(vehicle, false, 0, 0, "INVALID_URL")
            : throw new HttpRequestException("transient"));
        var processor = CreateProcessor(store, resolver);

        var result = await processor.RunAsync(CancellationToken.None);

        Assert.True(result.Processed);
        Assert.Equal(1, result.Candidates);
        Assert.Equal(1, result.Failed);
        Assert.Equal(1, result.Metrics!.InvalidUrl);
        Assert.Equal(1, resolver.Calls);
        Assert.Empty((await store.GetRecentAsync(10, CancellationToken.None)).Where(item => item.Vehicle.Platform == "iaai" && item.Vehicle.AdditionalData?.ContainsKey("copart_media_resolution") == true));
    }

    [Fact]
    public async Task Thumbnail_only_gallery_is_reported_without_affecting_the_critical_snapshot_pipeline()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistAsync(Source("1005"), DateTimeOffset.UtcNow, CancellationToken.None);
        var processor = CreateProcessor(store, new FakeMediaResolver(vehicle => new CopartMediaResolution(
            vehicle with { Media = new MediaInfo { Photos = ["https://cs.copart.com/v1/standard.jpg"], ThumbnailsCount = 1 } }, true, 1, 0, null)));

        var result = await processor.RunAsync(CancellationToken.None);

        Assert.True(result.Processed);
        Assert.Equal(1, result.Resolved);
        Assert.Equal(1, result.Metrics!.GalleryCount);
        Assert.Equal(0, result.Metrics.HdImages);
        Assert.Equal(1, result.Metrics.ThumbnailOnly);
    }

    private static CopartMediaEnrichmentProcessor CreateProcessor(InMemorySnapshotStore store, ICopartMediaResolver resolver) => new(
        store,
        resolver,
        new OptionsWrapper<CopartExcelOptions>(new CopartExcelOptions { MediaEnrichmentBatchSize = 10, MediaResolutionConcurrency = 2 }),
        NullLogger<CopartMediaEnrichmentProcessor>.Instance);

    private static AuctionVehicle Source(string lotNumber) => new()
    {
        Platform = "copart",
        LotNumber = lotNumber,
        Vin = "1HGCM82633A004352",
        Auction = new AuctionInfo { AuctionAt = DateTimeOffset.UtcNow.AddDays(1) },
        Media = new MediaInfo { Photos = ["https://cs.copart.com/v1/thumb.jpg"], ThumbnailsCount = 1 },
        RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["Image URL"] = $"https://inventoryv2.copart.io/v1/lotImages/{lotNumber}?brand=REDACTED" })
    };

    private static CopartMediaResolution HdResolution(AuctionVehicle vehicle) => new(
        vehicle with { Media = new MediaInfo { Photos = ["https://cs.copart.com/v1/hd-1.jpg", "https://cs.copart.com/v1/hd-2.jpg"], ThumbnailsCount = 2 } },
        true,
        2,
        2,
        null);

    private sealed class FakeMediaResolver(Func<AuctionVehicle, CopartMediaResolution> resolve) : ICopartMediaResolver
    {
        public int Calls { get; private set; }
        public Task<CopartMediaResolution> ResolveAsync(AuctionVehicle vehicle, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(resolve(vehicle));
        }
    }
}
