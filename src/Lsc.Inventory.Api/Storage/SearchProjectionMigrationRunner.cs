using Azure.Core;
using Azure.Identity;
using Lsc.Inventory.Api.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed record SearchProjectionMigrationResult(
    string MigrationId,
    int ScoreRowsUpdated,
    bool MigrationRecorded,
    IReadOnlyList<string> IndexesEnsured);

/// <summary>
/// Applies the additive search projection migration using the same managed identity
/// and PostgreSQL connection contract as the running API. This is intentionally a
/// one-shot command path; it is not executed during normal API startup.
/// </summary>
public sealed class SearchProjectionMigrationRunner(
    IOptions<PersistenceOptions> persistenceOptions,
    ILogger<SearchProjectionMigrationRunner> logger)
{
    private const string MigrationId = "016_search_projection_denormalized_score_v1";
    private readonly PersistenceOptions _persistence = persistenceOptions.Value;
    private readonly TokenCredential _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
    {
        ManagedIdentityClientId = persistenceOptions.Value.ManagedIdentityClientId
    });

    private static readonly string[] ScoreColumns =
    [
        "score_status text",
        "score_pre_grade numeric",
        "score_buy_score numeric",
        "score_max_points_evaluable numeric",
        "score_coverage_percent numeric",
        "score_confidence_percent numeric",
        "score_category text",
        "score_policy_version text",
        "score_scored_at timestamptz",
        "score_source_observed_at timestamptz"
    ];

    private static readonly (string Name, string Sql)[] Indexes =
    [
        ("ix_inventory_search_visible_score_observed_v7", "create index concurrently if not exists ix_inventory_search_visible_score_observed_v7 on inventory_search_current (score_pre_grade desc nulls last, observed_at desc nulls last, lot_key) where is_active and not is_special_title;"),
        ("ix_inventory_search_active_score_observed_v7", "create index concurrently if not exists ix_inventory_search_active_score_observed_v7 on inventory_search_current (score_pre_grade desc nulls last, observed_at desc nulls last, lot_key) where is_active;"),
        ("ix_inventory_search_visible_platform_score_observed_v7", "create index concurrently if not exists ix_inventory_search_visible_platform_score_observed_v7 on inventory_search_current (platform, score_pre_grade desc nulls last, observed_at desc nulls last, lot_key) where is_active and not is_special_title;"),
        ("ix_inventory_search_visible_auction_v7", "create index concurrently if not exists ix_inventory_search_visible_auction_v7 on inventory_search_current (auction_at, lot_key) where is_active and not is_special_title;")
    ];

    public async Task<SearchProjectionMigrationResult> RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);

        foreach (var column in ScoreColumns)
        {
            await ExecuteAsync(connection, $"alter table inventory_search_current add column if not exists {column};", cancellationToken);
        }

        var updatedRows = await ExecuteAsync(connection, """
            update inventory_search_current latest
            set score_status = score.status,
                score_pre_grade = score.pre_grade,
                score_buy_score = score.buy_score,
                score_max_points_evaluable = score.max_points_evaluable,
                score_coverage_percent = score.coverage_percent,
                score_confidence_percent = score.confidence_percent,
                score_category = score.category,
                score_policy_version = score.policy_version,
                score_scored_at = score.scored_at,
                score_source_observed_at = score.source_observed_at
            from inventory_vehicle_score_current score
            where score.lot_key = latest.lot_key;
            """, cancellationToken);

        foreach (var index in Indexes)
        {
            await ExecuteAsync(connection, index.Sql, cancellationToken);
        }

        await ExecuteAsync(connection, "analyze inventory_search_current;", cancellationToken);
        await ExecuteAsync(connection, """
            insert into schema_migrations (migration_id)
            values ('016_search_projection_denormalized_score_v1')
            on conflict (migration_id) do nothing;
            """, cancellationToken);

        logger.LogInformation("Search projection migration {MigrationId} completed; updated score rows={UpdatedRows}", MigrationId, updatedRows);
        return new SearchProjectionMigrationResult(MigrationId, updatedRows, true, Indexes.Select(index => index.Name).ToArray());
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var accessToken = await _credential.GetTokenAsync(
            new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]),
            cancellationToken);

        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = _persistence.PostgreSqlHost,
            Database = _persistence.Database,
            Username = _persistence.DatabaseUser,
            Password = accessToken.Token,
            SslMode = SslMode.VerifyFull,
            Timeout = _persistence.CommandTimeoutSeconds,
            CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120)
        }.ConnectionString;

        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<int> ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
        command.CommandText = sql;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
