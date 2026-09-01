using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;

namespace Lsc.Inventory.Api.Workers;

public sealed record AuctionsApiImportRequest(
    Guid RunId,
    string Platform,
    int MaximumLots,
    bool Persist,
    int StartPage,
    bool RequireSaleDate,
    int SkipSaleDateMatches,
    bool RequireFutureSaleDate);

public interface IAuctionsApiImportQueue
{
    bool TryEnqueue(AuctionsApiImportRequest request);
    IAsyncEnumerable<AuctionsApiImportRequest> ReadAllAsync(CancellationToken cancellationToken);
}

public sealed class AuctionsApiImportQueue : IAuctionsApiImportQueue
{
    private readonly Channel<AuctionsApiImportRequest> _channel = Channel.CreateBounded<AuctionsApiImportRequest>(new BoundedChannelOptions(4)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public bool TryEnqueue(AuctionsApiImportRequest request) => _channel.Writer.TryWrite(request);

    public IAsyncEnumerable<AuctionsApiImportRequest> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

public sealed class AuctionsApiImportBackgroundWorker(
    IAuctionsApiImportQueue queue,
    IServiceScopeFactory scopeFactory,
    ILogger<AuctionsApiImportBackgroundWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IAuctionsApiInitialImportProcessor>();
                await processor.RunAsync(
                    request.Platform,
                    request.MaximumLots,
                    request.Persist,
                    stoppingToken,
                    request.StartPage,
                    request.RequireSaleDate,
                    request.SkipSaleDateMatches,
                    request.RequireFutureSaleDate,
                    request.RunId);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("AuctionsAPI import worker stopping while processing run {RunId}.", request.RunId);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AuctionsAPI background import run {RunId} failed after leaving the request lifecycle.", request.RunId);
            }
        }
    }
}
