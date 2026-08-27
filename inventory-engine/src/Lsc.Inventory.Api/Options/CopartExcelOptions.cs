using System.ComponentModel.DataAnnotations;

namespace Lsc.Inventory.Api.Options;

public sealed class CopartExcelOptions
{
    public const string SectionName = "CopartExcel";

    public string AccountUrl { get; init; } = string.Empty;

    [Required]
    [RegularExpression("^[a-z0-9-]+$")]
    public string ContainerName { get; init; } = "copart-raw";

    [RegularExpression("^[A-Za-z0-9_./-]*$")]
    public string? SnapshotBlobName { get; init; }

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

    public bool AllowInterruptedSnapshotRetry { get; init; }

    [Range(1, 64)]
    public int PersistenceConcurrency { get; init; } = 8;

    [Range(1, 10000)]
    public int MediaEnrichmentBatchSize { get; init; } = 5000;

    [Range(1, 32)]
    public int MediaResolutionConcurrency { get; init; } = 8;
}
