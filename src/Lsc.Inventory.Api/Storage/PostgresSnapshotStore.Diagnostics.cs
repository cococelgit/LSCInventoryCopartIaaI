using System.Text.Json.Nodes;
using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    public async Task<JsonObject> GetPostgresLockDiagnosticAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Min(Math.Max(_persistence.CommandTimeoutSeconds, 30), 60);
        command.CommandText = """
            with sessions as (
                select pid, usename, application_name, client_addr::text as client_addr,
                       state, wait_event_type, wait_event,
                       xact_start, query_start, state_change,
                       now() - query_start as query_age,
                       left(query, 1000) as query
                from pg_stat_activity
                where datname = current_database()
                  and pid <> pg_backend_pid()
            )
            select
                coalesce((select jsonb_agg(to_jsonb(s) order by s.query_start nulls last) from sessions s), '[]'::jsonb) as sessions,
                coalesce((
                    select jsonb_agg(jsonb_build_object(
                        'blocked_pid', blocked.pid,
                        'blocked_user', blocked.usename,
                        'blocked_state', blocked.state,
                        'blocked_wait_event_type', blocked.wait_event_type,
                        'blocked_wait_event', blocked.wait_event,
                        'blocked_query', left(blocked.query, 1000),
                        'blocker_pid', blocker.pid,
                        'blocker_user', blocker.usename,
                        'blocker_state', blocker.state,
                        'blocker_query', left(blocker.query, 1000),
                        'blocking_query_age', now() - blocker.query_start
                    ) order by blocked.query_start nulls last)
                    from pg_stat_activity blocked
                    cross join lateral unnest(pg_blocking_pids(blocked.pid)) as blocker_pid(pid)
                    join pg_stat_activity blocker on blocker.pid = blocker_pid.pid
                    where blocked.datname = current_database()
                ), '[]'::jsonb) as blockers,
                coalesce((
                    select jsonb_agg(jsonb_build_object(
                        'pid', l.pid,
                        'locktype', l.locktype,
                        'mode', l.mode,
                        'granted', l.granted,
                        'relation', case when l.relation is null then null else l.relation::regclass::text end,
                        'state', a.state,
                        'wait_event_type', a.wait_event_type,
                        'wait_event', a.wait_event,
                        'query', left(a.query, 1000)
                    ) order by l.granted, l.pid)
                    from pg_locks l
                    left join pg_stat_activity a on a.pid = l.pid
                    where a.datname = current_database()
                      and (l.relation is not null or not l.granted)
                ), '[]'::jsonb) as locks;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            return new JsonObject { ["sessions"] = new JsonArray(), ["blockers"] = new JsonArray(), ["locks"] = new JsonArray() };

        return new JsonObject
        {
            ["database"] = _persistence.Database,
            ["captured_at_utc"] = DateTimeOffset.UtcNow,
            ["sessions"] = JsonNode.Parse(reader.GetString(0)) ?? new JsonArray(),
            ["blockers"] = JsonNode.Parse(reader.GetString(1)) ?? new JsonArray(),
            ["locks"] = JsonNode.Parse(reader.GetString(2)) ?? new JsonArray()
        };
    }
}

