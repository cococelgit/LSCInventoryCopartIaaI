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
    DateTimeOffset EnqueuedAt,
    bool CancellationRequested = false);

public interface IAuctionsApiImportJobStore
{
    Task EnqueueAsync(AuctionsApiImportRequest request, DateTimeOffset enqueuedAt, CancellationToken cancellationToken);
    Task<AuctionsApiImportJob?> TryClaimAsync(DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<bool> HeartbeatAsync(Guid runId, DateTimeOffset now, TimeSpan leaseDuration, CancellationToken cancellationToken);
    Task<AuctionsApiImportJob?> GetAsync(Guid runId, CancellationToken cancellationToken);
    Task<bool> RequestCancellationAsync(Guid runId, DateTimeOffset requestedAt, CancellationToken cancellationToken);
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
                   skip_sale_date_matches, require_future_sale_date, attempts, enqueued_at, cancellation_requested, lease_until, last_heartbeat
            from auctions_api_import_jobs
            where cancellation_requested = false and (status = 'queued' or (status = 'running' and (lease_until is null or lease_until < @now)))
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

    public async Task<AuctionsApiImportJob?> GetAsync(Guid runId, CancellationToken cancellationToken)
    {
        await EnsureImportJobSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            select run_id, platform, maximum_lots, persist, start_page, require_sale_date,
                   skip_sale_date_matches, require_future_sale_date, status, attempts,
                   lease_until, last_heartbeat, enqueued_at, cancellation_requested
            from auctions_api_import_jobs where run_id = @run_id;
            """;
        AddParameter(command, "run_id", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var request = new AuctionsApiImportRequest(reader.GetGuid(0), reader.GetString(1), reader.GetInt32(2), reader.GetBoolean(3), reader.GetInt32(4), reader.GetBoolean(5), reader.GetInt32(6), reader.GetBoolean(7));
        return new AuctionsApiImportJob(request, reader.GetString(8), reader.GetInt32(9), reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10), reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11), reader.GetFieldValue<DateTimeOffset>(12), reader.GetBoolean(13));
    }

    public async Task<bool> RequestCancellationAsync(Guid runId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        await EnsureImportJobSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            update auctions_api_import_jobs
            set cancellation_requested = true, status = case when status = 'queued' then 'cancelled' else status end,
                completed_at = case when status = 'queued' then @requested_at else completed_at end,
                lease_until = case when status = 'queued' then null else lease_until end
            where run_id = @run_id and status not in ('succeeded', 'partial', 'failed', 'cancelled');
            """;
        AddParameter(command, "requested_at", requestedAt);
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
                    cancellation_requested boolean not null default false,
                    status text not null default 'queued',
                    attempts integer not null default 0,
                    lease_until timestamptz,
                    last_heartbeat timestamptz,
                    enqueued_at timestamptz not null,
                    completed_at timestamptz
                );
                alter table auctions_api_import_jobs
                    add column if not exists cancellation_requested boolean not null default false;
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
            if (!_jobs.TryGetValue(runId, out var job) || job.Status != "running" || job.CancellationRequested) return false;
            _jobs[runId] = job with { LeaseUntil = now.Add(leaseDuration), LastHeartbeat = now };
            return true;
        }
        finally { _gate.Release(); }
    }

    public Task<AuctionsApiImportJob?> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        Task.FromResult(_jobs.TryGetValue(runId, out var job) ? job : null);

    public Task<bool> RequestCancellationAsync(Guid runId, DateTimeOffset requestedAt, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(runId, out var job) || job.Status is "succeeded" or "partial" or "failed" or "cancelled") return Task.FromResult(false);
        _jobs[runId] = job with { Status = job.Status == "queued" ? "cancelled" : job.Status, CancellationRequested = true, LeaseUntil = job.Status == "queued" ? null : job.LeaseUntil, LastHeartbeat = requestedAt };
        return Task.FromResult(true);
    }

    public Task CompleteAsync(Guid runId, string status, DateTimeOffset completedAt, CancellationToken cancellationToken)
    {
        if (_jobs.TryGetValue(runId, out var job)) _jobs[runId] = job with { Status = status, LeaseUntil = null, LastHeartbeat = completedAt };
        return Task.CompletedTask;
    }
}
