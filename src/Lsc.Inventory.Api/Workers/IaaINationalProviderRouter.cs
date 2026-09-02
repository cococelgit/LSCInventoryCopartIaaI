using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

/// <summary>
/// Selects the configured IAAI feed without changing the canonical ingestion pipeline.
/// The initial AuctionsAPI load must be complete before scheduled incremental work starts.
/// </summary>
public sealed class IaaINationalProviderRouter(
    IaaINationalSyncProcessor apibaraProcessor,
    IAuctionsApiIncrementalSyncProcessor auctionsApiProcessor,
    IAuctionsApiImportJobStore importJobStore,
    IOptions<IaaINationalOptions> options,
    ILogger<IaaINationalProviderRouter> logger) : IIaaINationalSyncProcessor
{
    private readonly IaaINationalOptions _options = options.Value;

    public async Task<IaaINationalSyncResult> RunAsync(CancellationToken cancellationToken)
    {
        var provider = (_options.PrimaryProvider ?? "apibara").Trim().ToLowerInvariant();
        if (provider is not ("auctionsapi" or "apibara"))
            throw new InvalidOperationException($"Unsupported IAAI primary provider '{provider}'.");

        if (provider == "auctionsapi")
        {
            if (_options.RequireInitialImportCompleted
                && !await importJobStore.HasCompletedInitialImportAsync("iaai", cancellationToken))
            {
                var now = DateTimeOffset.UtcNow;
                logger.LogInformation("IAAI scheduled sync skipped because the AuctionsAPI initial import is not complete.");
                return new(Guid.NewGuid(), now, now, true, "initial-import-not-complete", Guid.Empty, 0, 0, 0, 0, 0, 0, 0, false, null, new Dictionary<string, int>(), [], false);
            }

            try
            {
                var result = await auctionsApiProcessor.RunAsync("iaai", persist: true, cancellationToken);
                return new(
                    result.RunId,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    false,
                    null,
                    Guid.Empty,
                    result.ChangedObserved + result.ArchivedObserved,
                    result.Loaded,
                    result.Marked,
                    result.Discarded,
                    result.Quarantined,
                    result.PagesProcessed,
                    result.RequestsIssued,
                    false,
                    null,
                    new Dictionary<string, int>(),
                    result.Failures,
                    result.Failures.Count > 0);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AuctionsAPI IAAI sync failed; falling back to Apibara for this run.");
                return await apibaraProcessor.RunAsync(cancellationToken);
            }
        }

        return await apibaraProcessor.RunAsync(cancellationToken);
    }
}
