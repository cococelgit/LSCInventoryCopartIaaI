using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Workers;

public sealed record CopartFutureBodyStyleBackfillResult(
    bool Processed,
    int Candidates,
    int Updated,
    int Skipped,
    int Failed,
    TimeSpan Duration,
    IReadOnlyList<string> Failures);

public interface ICopartFutureBodyStyleBackfillProcessor
{
    Task<CopartFutureBodyStyleBackfillResult> RunAsync(CancellationToken cancellationToken);
}

/// <summary>Updates only future Copart lots whose persisted Body Style is available. It never reconciles lifecycle or reads IAAI.</summary>
public sealed class CopartFutureBodyStyleBackfillProcessor(
    IInventorySnapshotStore snapshotStore,
    IOptions<CopartExcelOptions> options,
    ILogger<CopartFutureBodyStyleBackfillProcessor> logger) : ICopartFutureBodyStyleBackfillProcessor
{
    private readonly CopartExcelOptions _options = options.Value;

    public async Task<CopartFutureBodyStyleBackfillResult> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var batchSize = Math.Clamp(_options.BodyStyleBackfillBatchSize, 1, 10_000);
        var concurrency = Math.Clamp(_options.BodyStyleBackfillConcurrency, 1, 32);
        var runId = await snapshotStore.StartSyncRunAsync(
            new InventorySyncRunStart("copart-body-style-backfill", InventorySourcePolicy.CopartExcelSource, "future-sale-body-style", 1, batchSize, startedAt), cancellationToken);
        var candidates = 0;
        var updated = 0;
        var skipped = 0;
        var failed = 0;
        var failures = new List<string>();

        try
        {
            while (true)
            {
                var batch = await snapshotStore.GetCopartFutureBodyStyleCandidatesAsync(batchSize, cancellationToken);
                if (batch.Count == 0) break;
                candidates += batch.Count;
                var updatedThisBatch = 0;
                for (var offset = 0; offset < batch.Count; offset += concurrency)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var outcomes = await Task.WhenAll(batch.Skip(offset).Take(concurrency).Select(candidate => UpdateCandidateAsync(candidate, cancellationToken)));
                    foreach (var outcome in outcomes)
                    {
                        if (outcome.Updated) { updated++; updatedThisBatch++; }
                        else if (outcome.Skipped) skipped++;
                        else { failed++; if (outcome.Failure is not null) failures.Add(outcome.Failure); }
                    }
                }
                logger.LogInformation("Copart Body Style backfill progress: candidates={Candidates}, updated={Updated}, skipped={Skipped}, failed={Failed}.", candidates, updated, skipped, failed);
                if (updatedThisBatch == 0 || batch.Count < batchSize) break;
            }

            var finishedAt = DateTimeOffset.UtcNow;
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(finishedAt, candidates, updated, failures.Take(20).ToArray()), cancellationToken);
            return new CopartFutureBodyStyleBackfillResult(failed == 0, candidates, updated, skipped, failed, finishedAt - startedAt, failures.Take(20).ToArray());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var finishedAt = DateTimeOffset.UtcNow;
            failures.Add($"body-style-backfill:{exception.GetType().Name}:{exception.Message}");
            await snapshotStore.CompleteSyncRunAsync(runId, new InventorySyncRunCompletion(finishedAt, candidates, updated, failures.Take(20).ToArray()), cancellationToken);
            logger.LogError(exception, "Copart Body Style backfill stopped after {Candidates} candidates.", candidates);
            return new CopartFutureBodyStyleBackfillResult(false, candidates, updated, skipped, failed + 1, finishedAt - startedAt, failures.Take(20).ToArray());
        }
    }

    private async Task<BackfillOutcome> UpdateCandidateAsync(StoredVehicleSnapshot candidate, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(candidate.Vehicle.Platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase))
                return new(false, true, null);
            var bodyStyle = ReadBodyStyle(candidate.Vehicle);
            if (string.IsNullOrWhiteSpace(bodyStyle)) return new(false, true, null);
            var updatedVehicle = candidate.Vehicle with
            {
                VehicleType = bodyStyle.Trim(),
                VehicleSpecs = candidate.Vehicle.VehicleSpecs is null
                    ? new VehicleSpecs { BodyStyle = bodyStyle.Trim() }
                    : candidate.Vehicle.VehicleSpecs with { BodyStyle = bodyStyle.Trim() }
            };
            return new(await snapshotStore.UpdateCopartVehicleTypeAsync(candidate.Identity, candidate.ObservedAt, updatedVehicle, cancellationToken), false, null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new(false, false, $"{candidate.Identity}:{exception.GetType().Name}");
        }
    }

    private static string? ReadBodyStyle(AuctionVehicle vehicle)
    {
        var direct = vehicle.VehicleSpecs?.BodyStyle;
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        if (vehicle.RawSource is { ValueKind: System.Text.Json.JsonValueKind.Object } raw)
        {
            foreach (var key in new[] { "Body Style", "Body Type" })
                if (raw.TryGetProperty(key, out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                    return value.GetString();
        }
        if (vehicle.AdditionalData is not null && vehicle.AdditionalData.TryGetValue("source_body_style", out var source) && source.ValueKind == System.Text.Json.JsonValueKind.String)
            return source.GetString();
        return null;
    }

    private sealed record BackfillOutcome(bool Updated, bool Skipped, string? Failure);
}
