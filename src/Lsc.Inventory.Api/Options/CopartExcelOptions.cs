using System.ComponentModel.DataAnnotations;

namespace Lsc.Inventory.Api.Options;

public sealed class CopartExcelOptions
{
    public const string SectionName = "CopartExcel";

    [Range(1, 1024)]
    public int MinimumFileSizeKilobytes { get; init; } = 1024;

    [Range(1, 2048)]
    public int MaximumFileSizeMegabytes { get; init; } = 512;

    [Range(1, 100000)]
    public int MinimumRowsForCompleteSnapshot { get; init; } = 1000;

    [Range(0.01, 1.0)]
    public decimal MinimumRowCountRatioToRecentMedian { get; init; } = 0.70m;

    [Range(1, 10000)]
    public int RecentSnapshotCountForBaseline { get; init; } = 6;

    [Range(1, 1000000)]
    public int ProcessingBatchSize { get; init; } = 1000;
}
