namespace Lsc.Inventory.Api.SaleAttempts;

public static class SaleAttemptSignalLevels
{
    public const string High = "high";
    public const string Medium = "medium";
    public const string FollowUp = "follow_up";
    public const string None = "none";
}

public static class SaleAttemptHistoryAvailability
{
    public const string Available = "available";
    public const string Empty = "empty";
    public const string NotRequested = "not_requested";
}

public sealed record AuctionSaleAttempt(
    string Platform,
    string LotKey,
    string LotNumber,
    string? ProviderVehicleId,
    string? ProviderLotId,
    string? ProviderAttemptId,
    string AttemptKey,
    DateTimeOffset SaleDate,
    string Status,
    int? StatusId,
    decimal? BidUsd,
    decimal? BuyNowUsd,
    DateTimeOffset? FinalBidUpdatedAt,
    DateTimeOffset SourceObservedAt,
    string InputHash);

public sealed record CurrentAuctionPriceState(
    decimal? CurrentBidUsd,
    decimal? CurrentBuyNowUsd,
    decimal? FinalBidUsd,
    decimal? SellerReserveUsd,
    string? AuctionType,
    string? Status,
    DateTimeOffset? SaleDate,
    DateTimeOffset? FinalBidUpdatedAt,
    bool? Archived);

public sealed record AuctionSaleHistorySnapshot(
    string Platform,
    string LotKey,
    string LotNumber,
    string? ProviderVehicleId,
    string? ProviderLotId,
    string HistoryAvailability,
    CurrentAuctionPriceState Current,
    IReadOnlyList<AuctionSaleAttempt> Attempts,
    DateTimeOffset SourceObservedAt);

public sealed record SaleAttemptParseDiagnostics(
    int LotsExamined,
    int LotsAccepted,
    int AttemptsExamined,
    int AttemptsAccepted,
    int MissingLotSkipped,
    int CrossPlatformSkipped,
    int MissingSaleDateSkipped,
    int DuplicateAttemptSkipped);

public sealed record SaleAttemptParseResult(
    IReadOnlyList<AuctionSaleHistorySnapshot> Snapshots,
    SaleAttemptParseDiagnostics Diagnostics);

public sealed record SellerMotivationSignal(
    string Platform,
    string LotKey,
    string LotNumber,
    string HistoryAvailability,
    int AttemptCount,
    int NotSoldCount,
    decimal? HistoricalMaxBidUsd,
    decimal? CurrentBuyNowUsd,
    decimal? CurrentSellerReserveUsd,
    decimal? CurrentAskUsd,
    decimal? AskGapUsd,
    decimal? AskGapPercent,
    decimal? BuyNowDropPercent,
    DateTimeOffset? FirstAttemptAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastNotSoldAt,
    int DaysInCycle,
    string SignalLevel,
    decimal ConfidencePercent,
    IReadOnlyList<string> ReasonCodes,
    string PolicyVersion,
    string InputHash,
    DateTimeOffset SourceObservedAt,
    DateTimeOffset CalculatedAt);

public sealed record SaleAttemptPersistenceResult(
    int InsertedAttempts,
    int UpdatedAttempts,
    int UnchangedAttempts,
    bool SignalChanged);

public sealed record StoredSaleAttemptState(
    IReadOnlyList<AuctionSaleAttempt> Attempts,
    SellerMotivationSignal? Signal);
