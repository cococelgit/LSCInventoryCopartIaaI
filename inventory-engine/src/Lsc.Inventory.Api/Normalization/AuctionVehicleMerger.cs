using Lsc.Inventory.Api.Contracts;

namespace Lsc.Inventory.Api.Normalization;

public static class AuctionVehicleMerger
{
    public static AuctionVehicle Merge(AuctionVehicle preferred, AuctionVehicle fallback) => preferred with
    {
        Platform = Text(preferred.Platform, fallback.Platform),
        LotNumber = Text(preferred.LotNumber, fallback.LotNumber),
        Vin = Text(preferred.Vin, fallback.Vin),
        Title = Text(preferred.Title, fallback.Title),
        Year = preferred.Year ?? fallback.Year,
        Make = Text(preferred.Make, fallback.Make),
        Model = Text(preferred.Model, fallback.Model),
        VehicleType = Text(preferred.VehicleType, fallback.VehicleType),
        Color = Text(preferred.Color, fallback.Color),
        FuelType = Text(preferred.FuelType, fallback.FuelType),
        Transmission = Text(preferred.Transmission, fallback.Transmission),
        DriveType = Text(preferred.DriveType, fallback.DriveType),
        Damage = Text(preferred.Damage, fallback.Damage),
        VehicleSpecs = MergeSpecs(preferred.VehicleSpecs, fallback.VehicleSpecs),
        Condition = MergeCondition(preferred.Condition, fallback.Condition),
        Facility = MergeFacility(preferred.Facility, fallback.Facility),
        Seller = MergeSeller(preferred.Seller, fallback.Seller),
        OdometerInfo = MergeOdometer(preferred.OdometerInfo, fallback.OdometerInfo),
        SaleDocument = MergeDocument(preferred.SaleDocument, fallback.SaleDocument),
        Auction = MergeAuction(preferred.Auction, fallback.Auction),
        Pricing = MergePricing(preferred.Pricing, fallback.Pricing),
        Location = MergeLocation(preferred.Location, fallback.Location),
        Media = MergeMedia(preferred.Media, fallback.Media),
        Details = preferred.Details ?? fallback.Details,
        TitleNotes = preferred.TitleNotes ?? fallback.TitleNotes,
        SpecialNote = preferred.SpecialNote ?? fallback.SpecialNote,
        Announcements = preferred.Announcements ?? fallback.Announcements,
        AdditionalData = preferred.AdditionalData ?? fallback.AdditionalData,
        RawSource = preferred.RawSource ?? fallback.RawSource
    };

    private static string? Text(string? preferred, string? fallback) => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static VehicleSpecs? MergeSpecs(VehicleSpecs? preferred, VehicleSpecs? fallback) => preferred is null ? fallback : fallback is null ? preferred : preferred with
    {
        ExteriorColor = Text(preferred.ExteriorColor, fallback.ExteriorColor), FuelType = Text(preferred.FuelType, fallback.FuelType), Transmission = Text(preferred.Transmission, fallback.Transmission), DriveType = Text(preferred.DriveType, fallback.DriveType), BodyStyle = Text(preferred.BodyStyle, fallback.BodyStyle), Engine = preferred.Engine ?? fallback.Engine, Airbags = Text(preferred.Airbags, fallback.Airbags), RestraintSystem = Text(preferred.RestraintSystem, fallback.RestraintSystem)
    };
    private static VehicleCondition? MergeCondition(VehicleCondition? preferred, VehicleCondition? fallback) => preferred is null ? fallback : fallback is null ? preferred : preferred with
    {
        PrimaryDamage = Text(preferred.PrimaryDamage, fallback.PrimaryDamage), SecondaryDamage = Text(preferred.SecondaryDamage, fallback.SecondaryDamage), Loss = Text(preferred.Loss, fallback.Loss), RunCondition = preferred.RunCondition ?? fallback.RunCondition, HasKey = preferred.HasKey ?? fallback.HasKey
    };
    private static AuctionFacility? MergeFacility(AuctionFacility? preferred, AuctionFacility? fallback) => preferred is null ? fallback : fallback is null ? preferred : preferred with
    {
        Id = Text(preferred.Id, fallback.Id), OfficeName = Text(preferred.OfficeName, fallback.OfficeName), State = Text(preferred.State, fallback.State), Zip = Text(preferred.Zip, fallback.Zip)
    };
    private static AuctionSeller? MergeSeller(AuctionSeller? preferred, AuctionSeller? fallback) => preferred is null ? fallback : fallback is null ? preferred : preferred with
    {
        Name = Text(preferred.Name, fallback.Name), Type = Text(preferred.Type, fallback.Type), Class = Text(preferred.Class, fallback.Class), TextClass = Text(preferred.TextClass, fallback.TextClass)
    };
    private static OdometerInfo? MergeOdometer(OdometerInfo? preferred, OdometerInfo? fallback) => preferred is null ? fallback : fallback is null ? preferred : preferred with
    {
        Miles = preferred.Miles ?? fallback.Miles, Kilometers = preferred.Kilometers ?? fallback.Kilometers, Status = Text(preferred.Status, fallback.Status)
    };
    private static SaleDocument? MergeDocument(SaleDocument? preferred, SaleDocument? fallback) => preferred is null ? fallback : fallback is null ? preferred : preferred with
    {
        Name = Text(preferred.Name, fallback.Name), Type = Text(preferred.Type, fallback.Type), Group = Text(preferred.Group, fallback.Group), IsPending = preferred.IsPending ?? fallback.IsPending, Export = preferred.Export ?? fallback.Export, Registration = preferred.Registration ?? fallback.Registration, PageId = Text(preferred.PageId, fallback.PageId)
    };
    private static AuctionInfo? MergeAuction(AuctionInfo? preferred, AuctionInfo? fallback) => preferred is null ? fallback : fallback is null ? preferred : preferred with
    {
        State = Text(preferred.State, fallback.State), AuctionAt = preferred.AuctionAt ?? fallback.AuctionAt, LotStatus = Text(preferred.LotStatus, fallback.LotStatus), LotSubStatus = Text(preferred.LotSubStatus, fallback.LotSubStatus), IsBuyNow = preferred.IsBuyNow ?? fallback.IsBuyNow, IsTimed = preferred.IsTimed ?? fallback.IsTimed
    };
    private static PricingInfo? MergePricing(PricingInfo? preferred, PricingInfo? fallback) => preferred is null ? fallback : fallback is null ? preferred : preferred with
    {
        CurrentBidUsd = preferred.CurrentBidUsd ?? fallback.CurrentBidUsd, PreBidUsd = preferred.PreBidUsd ?? fallback.PreBidUsd, BuyNowUsd = preferred.BuyNowUsd ?? fallback.BuyNowUsd, SalePriceUsd = preferred.SalePriceUsd ?? fallback.SalePriceUsd, EstimatedCost = preferred.EstimatedCost ?? fallback.EstimatedCost
    };
    private static VehicleLocation? MergeLocation(VehicleLocation? preferred, VehicleLocation? fallback) => preferred is null ? fallback : fallback is null ? preferred : preferred with
    {
        Display = Text(preferred.Display, fallback.Display), State = Text(preferred.State, fallback.State), FacilityId = Text(preferred.FacilityId, fallback.FacilityId), SendFrom = Text(preferred.SendFrom, fallback.SendFrom)
    };
    private static MediaInfo? MergeMedia(MediaInfo? preferred, MediaInfo? fallback) => preferred is null ? fallback : fallback is null ? preferred : preferred with
    {
        ThumbnailsCount = preferred.ThumbnailsCount ?? fallback.ThumbnailsCount, Has360 = preferred.Has360 ?? fallback.Has360, HasVideo = preferred.HasVideo ?? fallback.HasVideo, Photos = preferred.Photos is { Count: > 0 } ? preferred.Photos : fallback.Photos, Items = preferred.Items is { Count: > 0 } ? preferred.Items : fallback.Items
    };
}
