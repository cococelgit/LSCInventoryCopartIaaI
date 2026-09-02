using System.Collections.Concurrent;

namespace Lsc.Inventory.Api.Services;

public interface IProviderRequestLimiter
{
    Task WaitAsync(string provider, TimeSpan minimumInterval, CancellationToken cancellationToken);
}

/// <summary>
/// Serializes request start times per provider within this process. The caller must invoke
/// it before every real HTTP attempt, including retries. It intentionally does not hold the
/// gate while the network request is in flight, so slow responses do not reduce throughput
/// below the configured start-time budget.
/// </summary>
public sealed class ProviderRequestLimiter : IProviderRequestLimiter, IDisposable
{
    private readonly ConcurrentDictionary<string, Gate> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async Task WaitAsync(string provider, TimeSpan minimumInterval, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        if (minimumInterval < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumInterval));

        var gate = _gates.GetOrAdd(provider.Trim(), static _ => new Gate());
        await gate.Mutex.WaitAsync(cancellationToken);
        try
        {
            var now = DateTimeOffset.UtcNow;
            var nextAllowed = gate.LastStartedAt + minimumInterval;
            if (nextAllowed > now)
            {
                await Task.Delay(nextAllowed - now, cancellationToken);
                now = DateTimeOffset.UtcNow;
            }

            gate.LastStartedAt = now;
        }
        finally
        {
            gate.Mutex.Release();
        }
    }

    public void Dispose()
    {
        foreach (var gate in _gates.Values) gate.Mutex.Dispose();
        _gates.Clear();
    }

    private sealed class Gate
    {
        public readonly SemaphoreSlim Mutex = new(1, 1);
        public DateTimeOffset LastStartedAt { get; set; } = DateTimeOffset.MinValue;
    }
}
