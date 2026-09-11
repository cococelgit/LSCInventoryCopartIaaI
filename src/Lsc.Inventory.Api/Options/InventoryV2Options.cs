using System.ComponentModel.DataAnnotations;

namespace Lsc.Inventory.Api.Options;

public sealed class InventoryV2Options
{
    public const string SectionName = "InventoryV2";

    /// <summary>
    /// Enables collection and shadow writes to Inventory V2. The database-level
    /// writer flag must also be enabled before any V2 row can be written.
    /// </summary>
    public bool ShadowWriteEnabled { get; init; }

    [Range(100, 2000)]
    public int BatchSize { get; init; } = 1000;
}
