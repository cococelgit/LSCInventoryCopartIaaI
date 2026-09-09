using System.Text.Json;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.SaleAttempts;

public interface ISaleAttemptIntelligenceProcessor
{
    Task<SaleAttemptProcessingResult> ProcessAsync(JsonElement envelope, string platform, DateTimeOffset observedAt, bool persist, CancellationToken cancellationToken);
}

public sealed record SaleAttemptProcessingResult(
    bool Enabled,
    bool Persisted,
    SaleAttemptParseDiagnostics Diagnostics,
    IReadOnlyList<SellerMotivationSignal> Signals,
    IReadOnlyList<SaleAttemptPersistenceResult> Persistence);

public sealed class SaleAttemptIntelligenceProcessor(
    IOptions<SaleAttemptIntelligenceOptions> options,
    SellerMotivationSignalCalculator calculator,
    ISaleAttemptHistoryStore store) : ISaleAttemptIntelligenceProcessor
{
    private readonly SaleAttemptIntelligenceOptions _options = options.Value;

    public async Task<SaleAttemptProcessingResult> ProcessAsync(JsonElement envelope, string platform, DateTimeOffset observedAt, bool persist, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
            return new(false, false, new(0, 0, 0, 0, 0, 0, 0, 0), [], []);
        if (persist && !_options.AllowWrites)
            throw new InvalidOperationException("Sale-attempt writes are disabled. Enable both SaleAttemptIntelligence:Enabled and AllowWrites explicitly.");

        var parsed = AuctionsApiSaleAttemptParser.Parse(envelope, platform, observedAt);
        var signals = parsed.Snapshots
            .Select(snapshot => calculator.Calculate(snapshot, _options.PolicyVersion, observedAt))
            .ToArray();
        if (!persist) return new(true, false, parsed.Diagnostics, signals, []);

        var persistence = new List<SaleAttemptPersistenceResult>(parsed.Snapshots.Count);
        for (var index = 0; index < parsed.Snapshots.Count; index++)
            persistence.Add(await store.PersistAsync(parsed.Snapshots[index], signals[index], cancellationToken));
        return new(true, true, parsed.Diagnostics, signals, persistence);
    }
}
