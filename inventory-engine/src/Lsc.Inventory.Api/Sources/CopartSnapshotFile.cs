using System.Security.Cryptography;

namespace Lsc.Inventory.Api.Sources;

public static class CopartSnapshotFile
{
    public static async Task<CopartSnapshotEnvelope> OpenAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        var fullPath = Path.GetFullPath(filePath);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                hash.AppendData(buffer, 0, read);
            stream.Position = 0;
            var downloadedAt = File.GetLastWriteTimeUtc(fullPath);
            return new CopartSnapshotEnvelope(Path.GetFileName(fullPath), Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), downloadedAt, stream);
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }
}
