using System.ComponentModel.DataAnnotations;

namespace Lsc.Inventory.Api.Options;

public sealed class ScoringOptions
{
    public const string SectionName = "Scoring";

    public bool RunOnStartup { get; init; }

    [Range(1, 10_000)]
    public int BackfillMaximumLots { get; init; } = 500;

    [Range(1, 500)]
    public int BatchSize { get; init; } = 100;

    [Range(1, 10_000)]
    public int ImmediateMaximumLots { get; init; } = 5000;

    public bool ProcessIngestionRunImmediately { get; init; }

    public bool RequireCurrentForPublication { get; init; }
}
