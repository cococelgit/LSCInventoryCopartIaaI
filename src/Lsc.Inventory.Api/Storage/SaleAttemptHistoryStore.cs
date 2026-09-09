using Lsc.Inventory.Api.SaleAttempts;

namespace Lsc.Inventory.Api.Storage;

public interface ISaleAttemptHistoryStore
{
    Task<SaleAttemptPersistenceResult> PersistAsync(AuctionSaleHistorySnapshot snapshot, SellerMotivationSignal signal, CancellationToken cancellationToken);
    Task<StoredSaleAttemptState?> GetAsync(string lotKey, CancellationToken cancellationToken);
}

public sealed class InMemorySaleAttemptHistoryStore : ISaleAttemptHistoryStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AuctionSaleAttempt> _attempts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SellerMotivationSignal> _signals = new(StringComparer.OrdinalIgnoreCase);

    public Task<SaleAttemptPersistenceResult> PersistAsync(AuctionSaleHistorySnapshot snapshot, SellerMotivationSignal signal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var inserted = 0;
        var updated = 0;
        var unchanged = 0;
        bool signalChanged;
        lock (_gate)
        {
            foreach (var attempt in snapshot.Attempts)
            {
                if (!_attempts.TryGetValue(attempt.AttemptKey, out var previous))
                {
                    _attempts[attempt.AttemptKey] = attempt;
                    inserted++;
                }
                else if (!string.Equals(previous.InputHash, attempt.InputHash, StringComparison.Ordinal))
                {
                    _attempts[attempt.AttemptKey] = attempt;
                    updated++;
                }
                else
                {
                    unchanged++;
                }
            }

            signalChanged = !_signals.TryGetValue(snapshot.LotKey, out var previousSignal) ||
                !string.Equals(previousSignal.InputHash, signal.InputHash, StringComparison.Ordinal) ||
                !string.Equals(previousSignal.PolicyVersion, signal.PolicyVersion, StringComparison.Ordinal);
            if (signalChanged) _signals[snapshot.LotKey] = signal;
        }
        return Task.FromResult(new SaleAttemptPersistenceResult(inserted, updated, unchanged, signalChanged));
    }

    public Task<StoredSaleAttemptState?> GetAsync(string lotKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var attempts = _attempts.Values
                .Where(item => string.Equals(item.LotKey, lotKey, StringComparison.OrdinalIgnoreCase))
                .OrderBy(item => item.SaleDate)
                .ToArray();
            _signals.TryGetValue(lotKey, out var signal);
            return Task.FromResult<StoredSaleAttemptState?>(attempts.Length == 0 && signal is null ? null : new(attempts, signal));
        }
    }
}
