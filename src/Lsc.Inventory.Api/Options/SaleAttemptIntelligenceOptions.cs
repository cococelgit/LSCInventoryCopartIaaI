using System.ComponentModel.DataAnnotations;

namespace Lsc.Inventory.Api.Options;

/// <summary>
/// Independent safety gates for sale-attempt history. These switches are
/// intentionally separate from AuctionsApi:AllowWrites so the feature cannot
/// inherit production write permission by accident.
/// </summary>
public sealed class SaleAttemptIntelligenceOptions
{
    public const string SectionName = "SaleAttemptIntelligence";

    /// <summary>Allows parsing and calculation only. Defaults to disabled.</summary>
    public bool Enabled { get; init; }

    /// <summary>Second gate for writes to the additive attempt tables.</summary>
    public bool AllowWrites { get; init; }

    /// <summary>No worker or scheduler consumes this value during Sprint 1.</summary>
    public bool IncludePricesHistory { get; init; }

    [Range(1, 100)]
    public int DryRunSamplePerPlatform { get; init; } = 20;

    [Required]
    public string PolicyVersion { get; init; } = "motivated_seller_v1";
}
