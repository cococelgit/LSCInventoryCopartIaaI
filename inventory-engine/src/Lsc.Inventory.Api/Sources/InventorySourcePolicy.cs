using Lsc.Inventory.Api.Contracts;

namespace Lsc.Inventory.Api.Sources;

public static class InventorySourcePolicy
{
    public const string IaaIApibaraSource = "iaai";
    public const string CopartExcelSource = "copart";

    public static string RequireApibaraPlatform(string? platform)
    {
        var normalized = platform?.Trim().ToLowerInvariant();
        return normalized == IaaIApibaraSource
            ? normalized
            : throw new InvalidOperationException("Apibara is authorized only for IAAI. Copart must enter through the server-side Excel adapter.");
    }
}

public sealed record CopartSnapshotEnvelope(
    string FileName,
    string Sha256,
    DateTimeOffset DownloadedAt,
    Stream Content);

public interface ICopartExcelSnapshotAdapter
{
    IAsyncEnumerable<AuctionVehicle> ReadAcceptedSnapshotAsync(
        CopartSnapshotEnvelope snapshot,
        CancellationToken cancellationToken);
}
