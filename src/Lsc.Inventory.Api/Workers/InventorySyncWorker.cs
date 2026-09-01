using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public interface IInventorySyncProcessor
{
    Task<InventorySyncResult> RunOnceAsync(CancellationToken cancellationToken);
}

public sealed record InventorySyncResult(
    Guid RunId,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int ScopesProcessed,
    int VehiclesObserved,
    int RequestsIssued,
    IReadOnlyList<string> Failures);

public sealed class InventorySyncProcessor(
    IApibaraClient apibaraClient,
    IInventorySnapshotStore snapshotStore,
    ICanonicalInventoryIngestionPipeline canonicalPipeline,
    IOptions<ApibaraOptions> apibaraOptions,
    IOptions<SyncOptions> syncOptions,
    ILogger<InventorySyncProcessor> logger) : IInventorySyncProcessor
{
    private readonly ApibaraOptions _apibara = apibaraOptions.Value;
    private readonly SyncOptions _sync = syncOptions.Value;

    public async Task<InventorySyncResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var scopesProcessed = 0;
        var vehiclesObserved = 0;
        var requestsIssued = 0;
        var failures = new List<string>();
        var platformScope = string.Join(',', _sync.Platforms.Where(static platform => !string.IsNullOrWhiteSpace(platform)));
        var stateScope = string.Join(',', _sync.States.Where(static state => !string.IsNullOrWhiteSpace(state)));
        var runId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart("apibara", platformScope, stateScope, _sync.PagesPerScope, _apibara.PageSize, startedAt),
            cancellationToken);
        var previouslyStored = (await snapshotStore.GetRecentAsync(5000, cancellationToken))
            .Where(snapshot => !string.IsNullOrWhiteSpace(snapshot.Vehicle.Platform) &&
                !string.IsNullOrWhiteSpace(snapshot.Vehicle.LotNumber))
            .ToDictionary(
                snapshot => BuildLookupKey(snapshot.Vehicle.Platform!, snapshot.Vehicle.LotNumber!),
                snapshot => snapshot.Vehicle,
                StringComparer.OrdinalIgnoreCase);

        try
        {
            if (_sync.CaptureUsage)
            {
                await CaptureUsageAsync("before", cancellationToken);
                requestsIssued++;
            }

            var detailsRemaining = _sync.EnrichVehicleDetails
                ? _sync.DetailEnrichmentLimitPerRun
                : 0;

            foreach (var platform in _sync.Platforms.Where(static platform => !string.IsNullOrWhiteSpace(platform)))
            {
                foreach (var state in _sync.States.Where(static state => !string.IsNullOrWhiteSpace(state)))
                {
                    IReadOnlyList<AuctionLocation> facilityScopes;
                    try
                    {
                        requestsIssued++;
                        var locations = await apibaraClient.GetLocationsAsync(platform, state, _apibara.PageSize, cancellationToken);
                        facilityScopes = ResolveFacilityScopes(locations.Data);
                        if (facilityScopes.Count == 0)
                        {
                            throw new InvalidOperationException($"Apibara did not return an eligible {platform}:{state} facility.");
                        }
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        var failure = $"{platform.ToLowerInvariant()}:{state.ToUpperInvariant()} location: {exception.Message}";
                        failures.Add(failure);
                        logger.LogError(exception, "Unable to resolve a facility for platform {Platform} and state {State}", platform, state);
                        continue;
                    }

                    foreach (var resolvedLocation in facilityScopes)
                    {
                        scopesProcessed++;
                        var facilityId = resolvedLocation.FacilityId!;
                        string? cursor = null;

                        for (var page = 0; page < _sync.PagesPerScope; page++)
                        {
                            try
                            {
                                requestsIssued++;
                                var response = await apibaraClient.SearchVehiclesAsync(
                                    new VehicleSearchRequest(platform.Trim().ToLowerInvariant(), PerPage: _apibara.PageSize, Cursor: cursor, FacilityId: facilityId),
                                    cancellationToken);

                                var observedAt = DateTimeOffset.UtcNow;

                                foreach (var vehicle in response.Data)
                                {
                                    var providerVehicle = CanonicalVehicleCleaner.Clean(AuctionVehicleNormalizer.Normalize(vehicle, resolvedLocation, state));
                                    var vehicleToPersist = providerVehicle;
                                    var lookup = providerVehicle.LotNumber ?? providerVehicle.Vin;
                                    AuctionVehicle? storedVehicle = null;
                                    if (!string.IsNullOrWhiteSpace(providerVehicle.Platform) && !string.IsNullOrWhiteSpace(providerVehicle.LotNumber) &&
                                        previouslyStored.TryGetValue(BuildLookupKey(providerVehicle.Platform, providerVehicle.LotNumber), out storedVehicle))
                                    {
                                        vehicleToPersist = MergeVehicle(providerVehicle, storedVehicle);
                                    }

                                    if (detailsRemaining > 0 && NeedsDetailEnrichment(vehicleToPersist))
                                    {
                                        if (!string.IsNullOrWhiteSpace(lookup))
                                        {
                                            try
                                            {
                                                requestsIssued++;
                                                detailsRemaining--;
                                                var details = await apibaraClient.GetVehicleAsync(lookup, cancellationToken);
                                                providerVehicle = MergeVehicle(
                                                    CanonicalVehicleCleaner.Clean(AuctionVehicleNormalizer.Normalize(details.Data, resolvedLocation, state)),
                                                    providerVehicle);
                                                vehicleToPersist = storedVehicle is null
                                                    ? providerVehicle
                                                    : MergeVehicle(providerVehicle, storedVehicle);
                                            }
                                            catch (Exception exception) when (exception is not OperationCanceledException)
                                            {
                                                var failure = $"{platform.ToLowerInvariant()}:{state.ToUpperInvariant()} detail {lookup}: {exception.Message}";
                                                failures.Add(failure);
                                                logger.LogWarning(exception, "Unable to enrich lot {Lot} from Apibara detail endpoint; persisting list payload.", lookup);
                                            }
                                        }
                                    }

                                    var ingestion = await canonicalPipeline.ProcessAsync(providerVehicle, observedAt, cancellationToken, runId, vehicleToPersist);
                                    vehiclesObserved++;
                                    if (!ingestion.Loaded)
                                    {
                                        logger.LogInformation(
                                            "Discarded lot {Lot} under eligibility rules {RuleCodes}.",
                                            ingestion.Eligibility.LotNumber,
                                            string.Join(',', ingestion.Eligibility.DiscardReasons.Select(reason => reason.Code)));
                                        continue;
                                    }

                                    if (!string.IsNullOrWhiteSpace(vehicleToPersist.Platform) && !string.IsNullOrWhiteSpace(vehicleToPersist.LotNumber))
                                    {
                                        previouslyStored[BuildLookupKey(vehicleToPersist.Platform, vehicleToPersist.LotNumber)] = vehicleToPersist;
                                    }
                                }

                                cursor = response.Meta.NextCursor;
                                if (string.IsNullOrWhiteSpace(cursor))
                                {
                                    break;
                                }
                            }
                            catch (Exception exception) when (exception is not OperationCanceledException)
                            {
                                var failure = $"{platform.ToLowerInvariant()}:{state.ToUpperInvariant()} facility {facilityId} page {page + 1}: {exception.Message}";
                                failures.Add(failure);
                                logger.LogError(exception, "Inventory sync scope failed for platform {Platform}, state {State}, facility {FacilityId}", platform, state, facilityId);
                                break;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add($"preflight: {exception.Message}");
            logger.LogError(exception, "Inventory sync preflight failed for run {RunId}", runId);
        }
        finally
        {
            try
            {
                if (_sync.CaptureUsage)
                {
                    await CaptureUsageAsync("after", cancellationToken);
                    requestsIssued++;
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                failures.Add($"usage-after: {exception.Message}");
                logger.LogWarning(exception, "Unable to capture post-run Apibara usage for run {RunId}", runId);
            }
        }

        var finishedAt = DateTimeOffset.UtcNow;
        await snapshotStore.CompleteSyncRunAsync(
            runId,
            new InventorySyncRunCompletion(finishedAt, vehiclesObserved, requestsIssued, failures),
            cancellationToken);

        return new InventorySyncResult(runId, startedAt, finishedAt, scopesProcessed, vehiclesObserved, requestsIssued, failures);
    }

    private async Task CaptureUsageAsync(string timing, CancellationToken cancellationToken)
    {
        var usage = await apibaraClient.GetUsageAsync(cancellationToken);
        await snapshotStore.PersistProviderUsageAsync("apibara", usage.Data, DateTimeOffset.UtcNow, cancellationToken);
        logger.LogInformation("Captured {Timing} provider usage snapshot.", timing);
    }

    private IReadOnlyList<AuctionLocation> ResolveFacilityScopes(IReadOnlyList<AuctionLocation> locations)
    {
        var validLocations = locations
            .Where(static location => !string.IsNullOrWhiteSpace(location.FacilityId))
            .ToList();

        if (_sync.UseAllFacilitiesForState)
        {
            return validLocations;
        }

        var configuredFacilities = _sync.FacilityIds
            .Append(_sync.FacilityId)
            .Where(static facilityId => !string.IsNullOrWhiteSpace(facilityId))
            .Select(static facilityId => facilityId!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (configuredFacilities.Count > 0)
        {
            return validLocations
                .Where(location => configuredFacilities.Contains(location.FacilityId!))
                .ToList();
        }

        return validLocations.Take(1).ToList();
    }

    private static bool NeedsDetailEnrichment(AuctionVehicle vehicle) =>
        string.IsNullOrWhiteSpace(vehicle.Title) ||
        vehicle.Odometer is null ||
        string.IsNullOrWhiteSpace(vehicle.Damage) ||
        string.IsNullOrWhiteSpace(vehicle.FuelType) ||
        string.IsNullOrWhiteSpace(vehicle.Transmission) ||
        string.IsNullOrWhiteSpace(vehicle.DriveType);

    private static AuctionVehicle MergeVehicle(AuctionVehicle preferredVehicle, AuctionVehicle fallbackVehicle) => preferredVehicle with
    {
        Platform = Prefer(preferredVehicle.Platform, fallbackVehicle.Platform),
        LotNumber = Prefer(preferredVehicle.LotNumber, fallbackVehicle.LotNumber),
        Vin = Prefer(preferredVehicle.Vin, fallbackVehicle.Vin),
        Title = Prefer(preferredVehicle.Title, fallbackVehicle.Title),
        Year = preferredVehicle.Year ?? fallbackVehicle.Year,
        Make = Prefer(preferredVehicle.Make, fallbackVehicle.Make),
        Model = Prefer(preferredVehicle.Model, fallbackVehicle.Model),
        VehicleType = Prefer(preferredVehicle.VehicleType, fallbackVehicle.VehicleType),
        Color = Prefer(preferredVehicle.Color, fallbackVehicle.Color),
        FuelType = Prefer(preferredVehicle.FuelType, fallbackVehicle.FuelType),
        Transmission = Prefer(preferredVehicle.Transmission, fallbackVehicle.Transmission),
        DriveType = Prefer(preferredVehicle.DriveType, fallbackVehicle.DriveType),
        Damage = Prefer(preferredVehicle.Damage, fallbackVehicle.Damage),
        VehicleSpecs = MergeVehicleSpecs(fallbackVehicle.VehicleSpecs, preferredVehicle.VehicleSpecs),
        Condition = MergeCondition(fallbackVehicle.Condition, preferredVehicle.Condition),
        Facility = MergeFacility(fallbackVehicle.Facility, preferredVehicle.Facility),
        Seller = MergeSeller(fallbackVehicle.Seller, preferredVehicle.Seller),
        OdometerInfo = MergeOdometer(fallbackVehicle.OdometerInfo, preferredVehicle.OdometerInfo),
        SaleDocument = MergeSaleDocument(fallbackVehicle.SaleDocument, preferredVehicle.SaleDocument),
        TitleNotes = preferredVehicle.TitleNotes ?? fallbackVehicle.TitleNotes,
        SpecialNote = preferredVehicle.SpecialNote ?? fallbackVehicle.SpecialNote,
        Announcements = preferredVehicle.Announcements ?? fallbackVehicle.Announcements,
        Auction = MergeAuction(fallbackVehicle.Auction, preferredVehicle.Auction),
        Pricing = MergePricing(fallbackVehicle.Pricing, preferredVehicle.Pricing),
        Location = MergeLocation(fallbackVehicle.Location, preferredVehicle.Location),
        Media = MergeMedia(fallbackVehicle.Media, preferredVehicle.Media),
        AdditionalData = preferredVehicle.AdditionalData ?? fallbackVehicle.AdditionalData
        ,RawSource = preferredVehicle.RawSource ?? fallbackVehicle.RawSource
    };

    private static string? Prefer(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static string BuildLookupKey(string platform, string lotNumber) =>
        $"{platform.Trim().ToLowerInvariant()}:{lotNumber.Trim()}";

    private static VehicleSpecs? MergeVehicleSpecs(VehicleSpecs? fallbackSpecs, VehicleSpecs? preferredSpecs)
    {
        if (preferredSpecs is null) return fallbackSpecs;
        if (fallbackSpecs is null) return preferredSpecs;
        return preferredSpecs with
        {
            ExteriorColor = Prefer(preferredSpecs.ExteriorColor, fallbackSpecs.ExteriorColor),
            FuelType = Prefer(preferredSpecs.FuelType, fallbackSpecs.FuelType),
            Transmission = Prefer(preferredSpecs.Transmission, fallbackSpecs.Transmission),
            DriveType = Prefer(preferredSpecs.DriveType, fallbackSpecs.DriveType)
        };
    }

    private static VehicleCondition? MergeCondition(VehicleCondition? fallbackCondition, VehicleCondition? preferredCondition)
    {
        if (preferredCondition is null) return fallbackCondition;
        if (fallbackCondition is null) return preferredCondition;
        return preferredCondition with
        {
            PrimaryDamage = Prefer(preferredCondition.PrimaryDamage, fallbackCondition.PrimaryDamage),
            SecondaryDamage = Prefer(preferredCondition.SecondaryDamage, fallbackCondition.SecondaryDamage)
        };
    }

    private static AuctionFacility? MergeFacility(AuctionFacility? fallbackFacility, AuctionFacility? preferredFacility)
    {
        if (preferredFacility is null) return fallbackFacility;
        if (fallbackFacility is null) return preferredFacility;
        return preferredFacility with
        {
            Id = Prefer(preferredFacility.Id, fallbackFacility.Id),
            OfficeName = Prefer(preferredFacility.OfficeName, fallbackFacility.OfficeName),
            State = Prefer(preferredFacility.State, fallbackFacility.State),
            Zip = Prefer(preferredFacility.Zip, fallbackFacility.Zip)
        };
    }

    private static AuctionSeller? MergeSeller(AuctionSeller? fallbackSeller, AuctionSeller? preferredSeller)
    {
        if (preferredSeller is null) return fallbackSeller;
        if (fallbackSeller is null) return preferredSeller;
        return preferredSeller with
        {
            Name = Prefer(preferredSeller.Name, fallbackSeller.Name),
            Type = Prefer(preferredSeller.Type, fallbackSeller.Type)
        };
    }

    private static OdometerInfo? MergeOdometer(OdometerInfo? fallbackOdometer, OdometerInfo? preferredOdometer)
    {
        if (preferredOdometer is null) return fallbackOdometer;
        if (fallbackOdometer is null) return preferredOdometer;
        return preferredOdometer with { Miles = preferredOdometer.Miles ?? fallbackOdometer.Miles };
    }

    private static SaleDocument? MergeSaleDocument(SaleDocument? fallbackDocument, SaleDocument? preferredDocument)
    {
        if (preferredDocument is null) return fallbackDocument;
        if (fallbackDocument is null) return preferredDocument;
        return preferredDocument with
        {
            Name = Prefer(preferredDocument.Name, fallbackDocument.Name),
            IsPending = preferredDocument.IsPending ?? fallbackDocument.IsPending
        };
    }

    private static AuctionInfo? MergeAuction(AuctionInfo? listAuction, AuctionInfo? detailAuction)
    {
        if (detailAuction is null) return listAuction;
        if (listAuction is null) return detailAuction;
        return detailAuction with
        {
            State = Prefer(detailAuction.State, listAuction.State),
            AuctionAt = detailAuction.AuctionAt ?? listAuction.AuctionAt,
            LotStatus = Prefer(detailAuction.LotStatus, listAuction.LotStatus),
            LotSubStatus = Prefer(detailAuction.LotSubStatus, listAuction.LotSubStatus)
        };
    }

    private static PricingInfo? MergePricing(PricingInfo? listPricing, PricingInfo? detailPricing)
    {
        if (detailPricing is null) return listPricing;
        if (listPricing is null) return detailPricing;
        return detailPricing with
        {
            CurrentBidUsd = detailPricing.CurrentBidUsd ?? listPricing.CurrentBidUsd,
            BuyNowUsd = detailPricing.BuyNowUsd ?? listPricing.BuyNowUsd,
            SalePriceUsd = detailPricing.SalePriceUsd ?? listPricing.SalePriceUsd
        };
    }

    private static VehicleLocation? MergeLocation(VehicleLocation? listLocation, VehicleLocation? detailLocation)
    {
        if (detailLocation is null) return listLocation;
        if (listLocation is null) return detailLocation;
        return detailLocation with
        {
            Display = Prefer(detailLocation.Display, listLocation.Display),
            State = Prefer(detailLocation.State, listLocation.State),
            FacilityId = Prefer(detailLocation.FacilityId, listLocation.FacilityId)
        };
    }

    private static MediaInfo? MergeMedia(MediaInfo? listMedia, MediaInfo? detailMedia)
    {
        if (detailMedia is null) return listMedia;
        if (listMedia is null) return detailMedia;
        return detailMedia with
        {
            ThumbnailsCount = detailMedia.ThumbnailsCount ?? listMedia.ThumbnailsCount,
            Has360 = detailMedia.Has360 ?? listMedia.Has360,
            Photos = detailMedia.Photos is { Count: > 0 } ? detailMedia.Photos : listMedia.Photos
        };
    }
}

public sealed class InventorySyncWorker(
    IServiceScopeFactory serviceScopeFactory,
    IOptions<SyncOptions> options,
    ILogger<InventorySyncWorker> logger) : BackgroundService
{
    private readonly SyncOptions _options = options.Value;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Inventory sync worker is disabled by configuration.");
            return;
        }

        await ExecuteOneRunAsync(stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(_options.IntervalMinutes));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await ExecuteOneRunAsync(stoppingToken);
        }
    }

    private async Task ExecuteOneRunAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            logger.LogWarning("Skipped inventory sync because an earlier run is still active.");
            return;
        }

        try
        {
            using var scope = serviceScopeFactory.CreateScope();
            var processor = scope.ServiceProvider.GetRequiredService<IInventorySyncProcessor>();
            var result = await processor.RunOnceAsync(cancellationToken);

            logger.LogInformation(
                "Inventory sync {RunId} finished: {ScopesProcessed} scopes, {VehiclesObserved} vehicles, {RequestsIssued} API requests, {FailureCount} failures.",
                result.RunId,
                result.ScopesProcessed,
                result.VehiclesObserved,
                result.RequestsIssued,
                result.Failures.Count);
        }
        finally
        {
            _runLock.Release();
        }
    }

    public override void Dispose()
    {
        _runLock.Dispose();
        base.Dispose();
    }
}
