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

    /// <summary>
    /// Allows public read paths to opt into Inventory V2 only after the database
    /// reader flag is enabled. It remains false throughout shadow validation.
    /// </summary>
    public bool ReaderEnabled { get; init; }

    /// <summary>
    /// Keeps the legacy V1 read path available only as an explicit emergency rollback.
    /// Set false after the V2 reader has passed its production observation window.
    /// </summary>
    public bool LegacyReadFallbackEnabled { get; init; } = true;

    [Range(100, 2000)]
    public int BatchSize { get; init; } = 1000;
}
