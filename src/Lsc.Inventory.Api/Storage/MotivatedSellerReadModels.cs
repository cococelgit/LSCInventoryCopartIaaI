using Lsc.Inventory.Api.SaleAttempts;

namespace Lsc.Inventory.Api.Storage;

public sealed record MotivatedSellerDetail(
    string LotKey,
    SellerMotivationSignal? Signal,
    IReadOnlyList<AuctionSaleAttempt> Attempts);

public sealed record MotivatedSellerSample(
    IReadOnlyList<MotivatedSellerDetail> Items,
    int Total,
    IReadOnlyDictionary<string, int> SignalLevels);

public sealed record MotivatedSellerReport(
    long Observations,
    long Attempts,
    long Signals,
    IReadOnlyDictionary<string, long> SignalLevels,
    IReadOnlyDictionary<string, long> HistoryAvailability);
