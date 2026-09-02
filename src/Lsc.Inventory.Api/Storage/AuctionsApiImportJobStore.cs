using System.Collections.Concurrent;
using Lsc.Inventory.Api.Workers;
using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed record AuctionsApiImportJob(
    AuctionsApiImportRequest Request,
    string Status,
    int Attempts,
    DateTimeOffset? LeaseUntil,
    DateTimeOffset? LastHeartbeat,
    DateTimeOffset EnqueuedAt);

public interface IAuctionsApiImportJobStore
{
    Task EnqueueAsync(AuctionsApiImportRequest request, DateTimeOffset enqueuedAt, CancellationToken cancellationToken);
    Task<AuctionsApiImportJob?> TryClaimAsync(DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> HeartbeatAsync(Guid runId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task CompleteAsync(Guid runId, string status, DateTimeOffset completedAt, CancellationToken cancellationToken);
}

public sealed partial class PostgresSnapshotStore
{
    private static readonly SemaphoreSlim ImportJobSchemaLock = new(1, 1);
    private static bool _importJobSchemaInitialized;

    public async Task EnqueueAsync(AuctionsApiImportRequest request, DateTimeOffset enqueuedAt, CancellationToken cancellationToken)
    {
        await EnsureImportJobSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            insert into auctions_api_import_jobs
                (run_id, platform, maximum_lots, persist, start_page, require_sale_date,
                 skip_sale_date_matches, require_future_sale_date, status, attempts, enqueued_at)
            values
                (@run_id, @platform, @maximum_lots, @persist, @start_page, @require_sale_date,
                 @skip_sale_date_matches, @require_future_sale_date, 'queued', 0, @enqueued_at)
            on conflict (run_id) do nothing;
            """;
        AddParameter(command, "run_id", request.RunId);
        AddParameter(command, "platform", request.Platform);
        AddParameter(command, "maximum_lots", request.MaximumLots);
        AddParameter(command, "persist", request.Persist);
        AddParameter(command, "start_page", request.StartPage);
        AddParameter(command, "require_sale_date", request.RequireSaleDate);
        AddParameter(command, "skip_sale_date_matches", request.SkipSaleDateMatches);
        AddParameter(command, "require_future_sale_date", request.RequireFutureSaleDate);
        AddParameter(command, "enqueued_at", enqueuedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<AuctionsApiImportJob?> TryClaimAsync(DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await EnsureImportJobSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select run_id, platform, maximum_lots, persist, start_page, require_sale_date,
                   skip_sale_date_matches, require_future_sale_date, attempts, enqueued_at
            from auctions_api_import_jobs
            where status = 'queued' or (status = 'running' and (lease_until is null or lease_until < @now))
            order by enqueued_at, run_id
            for update skip locked
            limit 1;
            """;
        AddParameter(command, "now", now);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return null;
        }
        var request = new AuctionsApiImportRequest(
            reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3),
            reader.GetInt32(4), reader.GetBoolean(5), reader.GetInt32(6), reader.GetBoolean(7));
        var attempts = reader.GetInt32(8) + 1;
        var enqueuedAt = reader.GetFieldValue<DateTimeOffset>(9);
        await reader.CloseAsync();
        var leaseUntil = now.Add(leaseDuration);
        await using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandTimeout = _persistence.CommandTimeoutSeconds;
        update.CommandText = """
            update auctions_api_import_jobs
            set status = 'running', attempts = @attempts, lease_until = @lease_until, last_heartbeat = @now
            where run_id = @run_id;
            """;
        AddParameter(update, "attempts", attempts);
        AddParameter(update, "lease_until", leaseUntil);
        AddParameter(update, "now", now);
        AddParameter(update, "run_id", request.RunId);
        await update.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AuctionsApiImportJob(request, "running", attempts, leaseUntil, now, enqueuedAt);
    }

    public async Task<bool> HeartbeatAsync(Guid runId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await EnsureImportJobSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            update auctions_api_import_jobs
            set lease_until = @lease_until, last_heartbeat = @now
            where run_id = @run_id and status = 'running';
            """;
        AddParameter(command, "lease_until", now.Add(leaseDuration));
        AddParameter(command, "now", now);
        AddParameter(command, "run_id", runId);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task CompleteAsync(Guid runId, string status, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        await EnsureImportJobSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            update auctions_api_import_jobs
            set status = @status, completed_at = @completed_at, lease_until = null, last_heartbeat = @completed_at
            where run_id = @run_id and status <> 'succeeded' and status <> 'failed' and status <> 'cancelled';
            """;
        AddParameter(command, "status", status);
        AddParameter(command, "completed_at", completedAt);
        AddParameter(command, "run_id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task EnsureImportJobSchemaAsync(CancellationToken cancellationToken)
    {
        if (_importJobSchemaInitialized) return;
        await ImportJobSchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_importJobSchemaInitialized) return;
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
                create table if not exists auctions_api_import_jobs (
                    run_id uuid primary key,
                    platform text not null,
                    maximum_lots integer not null,
                    persist boolean not null default false,
                    start_page integer not null default 1,
                    require_sale_date boolean not null default false,
                    skip_sale_date_matches integer not null default 0,
                    require_future_sale_date boolean not null default false,
                    status text not null default 'queued',
                    attempts integer not null default 0,
                    lease_until timestamptz,
                    last_heartbeat timestamptz,
                    enqueued_at timestamptz not null,
                    completed_at timestamptz
                );
                create index if not exists ix_auctions_api_import_jobs_claim
                    on auctions_api_import_jobs (status, lease_until, enqueued_at);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _importJobSchemaInitialized = true;
        }
        finally
        {
            ImportJobSchemaLock.Release();
        }
    }
}

public sealed class InMemoryAuctionsApiImportJobStore : IAuctionsApiImportJobStore
{
    private readonly ConcurrentDictionary<Guid, AuctionsApiImportJob> _jobs = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task EnqueueAsync(AuctionsApiImportRequest request, DateTimeOffset enqueuedAt, CancellationToken cancellationToken)
    {
        _jobs.TryAdd(request.RunId, new AuctionsApiImportJob(request, "queued", 0, null, null, enqueuedAt));
        return Task.CompletedTask;
    }

    public async Task<AuctionsApiImportJob?> TryClaimAsync(DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var job = _jobs.Values.Where(item => item.Status == "queued" || (item.Status == "running" && item.LeaseUntil < now))
                .OrderBy(item => item.EnqueuedAt).FirstOrDefault();
            if (job is null) return null;
            var claimed = job with { Status = "running", Attempts = job.Attempts + 1, LeaseUntil = now.Add(leaseDuration), LastHeartbeat = now };
            _jobs[job.Request.RunId] = claimed;
            return claimed;
        }
        finally { _gate.Release(); }
    }

    public async Task<bool> HeartbeatAsync(Guid runId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!_jobs.TryGetValue(runId, out var job) || job.Status != "running") return false;
            _jobs[runId] = job with { LeaseUntil = now.Add(leaseDuration), LastHeartbeat = now };
            return true;
        }
        finally { _gate.Release(); }
    }

    public Task CompleteAsync(Guid runId, string status, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        if (_jobs.TryGetValue(runId, out var job)) _jobs[runId] = job with { Status = status, LeaseUntil = null, LastHeartbeat = completedAt };
        return Task.CompletedTask;
    }
}
