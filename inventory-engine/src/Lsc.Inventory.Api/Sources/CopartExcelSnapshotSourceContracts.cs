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
