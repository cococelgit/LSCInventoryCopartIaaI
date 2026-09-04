namespace Lsc.Inventory.Api.Storage;

public sealed record SellerAuditRow(
    string Platform,
    string SellerName,
    string SellerType,
    string SellerClass,
    string SellerTextClass,
    string SellerCategory,
    decimal ClassificationConfidence,
    bool NeedsReview,
    long VehicleCount);

public sealed record SellerAuditReport(
    DateTimeOffset GeneratedAt,
    DateTimeOffset SaleDateFrom,
    long TotalVehicles,
    IReadOnlyList<SellerAuditRow> Rows);
