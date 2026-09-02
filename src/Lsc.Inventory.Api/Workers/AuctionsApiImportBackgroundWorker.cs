using Lsc.Inventory.Api.Storage;
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

public sealed class AuctionsApiImportBackgroundWorker(
    IAuctionsApiImportJobStore jobStore,
    IServiceScopeFactory scopeFactory,
    ILogger<AuctionsApiImportBackgroundWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            AuctionsApiImportJob? job = null;
            try
            {
                job = await jobStore.TryClaimAsync(DateTimeOffset.UtcNow, LeaseDuration, stoppingToken);
                if (job is null)
                {
                    await Task.Delay(PollInterval, stoppingToken);
                    continue;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var processor = scope.ServiceProvider.GetRequiredService<IAuctionsApiInitialImportProcessor>();
                using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var heartbeat = MaintainLeaseAsync(job.Request.RunId, runCancellation, runCancellation.Token);
                try
                {
                    var result = await processor.RunAsync(
                        job.Request.Platform,
                        job.Request.MaximumLots,
                        job.Request.Persist,
                        runCancellation.Token,
                        job.Request.StartPage,
                        job.Request.RequireSaleDate,
                        job.Request.SkipSaleDateMatches,
                        job.Request.RequireFutureSaleDate,
                        job.Request.RunId,
                        (progress, checkpointToken) => jobStore.CheckpointAsync(job.Request.RunId, progress, checkpointToken));
                    await jobStore.CompleteAsync(job.Request.RunId, result.Failures.Count == 0 ? "succeeded" : "partial", DateTimeOffset.UtcNow, CancellationToken.None);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    await jobStore.CompleteAsync(job.Request.RunId, "cancelled", DateTimeOffset.UtcNow, CancellationToken.None);
                    logger.LogInformation("AuctionsAPI import worker stopped while processing run {RunId}.", job.Request.RunId);
                }
                catch (Exception exception)
                {
                    await jobStore.CompleteAsync(job.Request.RunId, "failed", DateTimeOffset.UtcNow, CancellationToken.None);
                    logger.LogError(exception, "AuctionsAPI durable import run {RunId} failed.", job.Request.RunId);
                }
                finally
                {
                    runCancellation.Cancel();
                    try { await heartbeat; } catch (OperationCanceledException) { }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "AuctionsAPI durable import worker polling failed.");
                await Task.Delay(PollInterval, stoppingToken);
            }
        }
    }

    private async Task MaintainLeaseAsync(Guid runId, CancellationTokenSource runCancellation, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var job = await jobStore.GetAsync(runId, cancellationToken);
            if (job?.CancellationRequested == true)
            {
                runCancellation.Cancel();
                return;
            }
            if (!await jobStore.HeartbeatAsync(runId, DateTimeOffset.UtcNow, LeaseDuration, cancellationToken))
            {
                runCancellation.Cancel();
                return;
            }
        }
    }
}
