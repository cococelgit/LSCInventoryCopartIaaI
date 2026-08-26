using System.Security.Cryptography;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Lsc.Inventory.Api.Options;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Sources;

public interface ICopartExcelSnapshotSource
{
    Task<CopartSnapshotLease> OpenLatestAsync(CancellationToken cancellationToken);
}

public sealed class CopartSnapshotLease(CopartSnapshotEnvelope snapshot, string temporaryPath) : IAsyncDisposable
{
    public CopartSnapshotEnvelope Snapshot { get; } = snapshot;

    public async ValueTask DisposeAsync()
    {
        await Snapshot.Content.DisposeAsync();
        File.Delete(temporaryPath);
    }
}

public sealed class CopartBlobSnapshotSource(
    IOptions<CopartExcelOptions> copartOptions,
    IOptions<PersistenceOptions> persistenceOptions,
    ILogger<CopartBlobSnapshotSource> logger) : ICopartExcelSnapshotSource
{
    public async Task<CopartSnapshotLease> OpenLatestAsync(CancellationToken cancellationToken)
    {
        var copart = copartOptions.Value;
        var persistence = persistenceOptions.Value;
        ValidateOptions(copart);
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = persistence.ManagedIdentityClientId
        });
        var serviceClient = new BlobServiceClient(new Uri(copart.AccountUrl), credential);
        var container = serviceClient.GetBlobContainerClient(copart.ContainerName);
        var blob = await ResolveBlobAsync(container, copart, cancellationToken);
        var properties = await blob.GetPropertiesAsync(cancellationToken: cancellationToken);
        if (properties.Value.ContentLength <= 0)
            throw new IOException("Copart Blob snapshot is empty.");

        var temporaryPath = Path.Combine(Path.GetTempPath(), $"copart-{Guid.NewGuid():N}.csv");
        try
        {
            string sha256;
            await using (var destination = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var response = await blob.DownloadStreamingAsync(cancellationToken: cancellationToken);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = await response.Value.Content.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                await destination.FlushAsync(cancellationToken);
                destination.Position = 0;
                sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            var stream = new FileStream(temporaryPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var downloadedAt = properties.Value.LastModified == default ? DateTimeOffset.UtcNow : properties.Value.LastModified;
            logger.LogInformation("Prepared Copart snapshot {BlobName} with {Bytes} bytes and SHA-256 {HashPrefix}.", blob.Name, properties.Value.ContentLength, sha256[..12]);
            return new CopartSnapshotLease(new CopartSnapshotEnvelope(blob.Name, sha256, downloadedAt, stream), temporaryPath);
        }
        catch
        {
            File.Delete(temporaryPath);
            throw;
        }
    }

    private static async Task<BlobClient> ResolveBlobAsync(BlobContainerClient container, CopartExcelOptions copart, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(copart.SnapshotBlobName))
            return container.GetBlobClient(copart.SnapshotBlobName);

        BlobItem? newest = null;
        var inspected = 0;
        var sampleNames = new List<string>(capacity: 5);
        await foreach (var candidate in container.GetBlobsAsync(cancellationToken: cancellationToken))
        {
            inspected++;
            if (sampleNames.Count < 5) sampleNames.Add(candidate.Name);
            if (!candidate.Name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) continue;
            if (newest is null || candidate.Properties.LastModified > newest.Properties.LastModified) newest = candidate;
        }

        return newest is null
            ? throw new FileNotFoundException($"No .csv Copart snapshot was found in the configured Blob container after inspecting {inspected} blob(s). Sample names: {string.Join(", ", sampleNames)}")
            : container.GetBlobClient(newest.Name);
    }

    private static void ValidateOptions(CopartExcelOptions copart)
    {
        if (!Uri.TryCreate(copart.AccountUrl, UriKind.Absolute, out _))
            throw new InvalidOperationException("CopartExcel:AccountUrl must be an absolute Azure Blob account URL.");
        if (string.IsNullOrWhiteSpace(copart.ContainerName))
            throw new InvalidOperationException("CopartExcel:ContainerName is required.");
    }
}
