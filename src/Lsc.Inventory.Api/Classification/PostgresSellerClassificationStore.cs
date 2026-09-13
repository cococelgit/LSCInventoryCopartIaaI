using Azure.Core;
using Azure.Identity;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Options;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Lsc.Inventory.Api.Classification;

public interface ISellerClassificationStore
{
    Task<SellerClassification?> GetAsync(string platform, string normalizedName, CancellationToken cancellationToken);
    Task UpsertAsync(string platform, string normalizedName, string rawName, SellerClassification classification, CancellationToken cancellationToken);
}

public sealed class PostgresSellerClassificationStore : ISellerClassificationStore
{
    private readonly PersistenceOptions _options;
    private readonly TokenCredential _credential;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private (string Token, DateTimeOffset ExpiresOn) _cachedToken;

    public PostgresSellerClassificationStore(IOptions<PersistenceOptions> options)
    {
        _options = options.Value;
        _credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = _options.ManagedIdentityClientId
        });
    }

    public async Task<SellerClassification?> GetAsync(string platform, string normalizedName, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        command.CommandText = """
            select category, confidence, needs_review, evidence_type, coalesce(reason, ''), coalesce(model, ''), prompt_version
            from public.seller_classifications
            where platform = @platform and seller_name_normalized = @seller_name_normalized;
            """;
        command.Parameters.AddWithValue("platform", platform);
        command.Parameters.AddWithValue("seller_name_normalized", normalizedName);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new SellerClassification(
            reader.GetString(0),
            reader.GetDecimal(1),
            reader.GetBoolean(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(3),
            string.IsNullOrWhiteSpace(reader.GetString(5)) ? null : reader.GetString(5),
            reader.GetString(6));
    }

    public async Task UpsertAsync(string platform, string normalizedName, string rawName, SellerClassification classification, CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = _options.CommandTimeoutSeconds;
        command.CommandText = """
            insert into public.seller_classifications
                (platform, seller_name_normalized, seller_name_raw_last_seen, category, confidence, needs_review, reason, evidence_type, model, prompt_version, classified_at, last_seen_at, updated_at)
            values
                (@platform, @seller_name_normalized, @seller_name_raw_last_seen, @category, @confidence, @needs_review, @reason, @evidence_type, @model, @prompt_version, @classified_at, now(), now())
            on conflict (platform, seller_name_normalized) do update set
                seller_name_raw_last_seen = excluded.seller_name_raw_last_seen,
                category = excluded.category,
                confidence = excluded.confidence,
                needs_review = excluded.needs_review,
                reason = excluded.reason,
                evidence_type = excluded.evidence_type,
                model = excluded.model,
                prompt_version = excluded.prompt_version,
                classified_at = excluded.classified_at,
                last_seen_at = now(),
                updated_at = now();
            """;
        command.Parameters.AddWithValue("platform", platform);
        command.Parameters.AddWithValue("seller_name_normalized", normalizedName);
        command.Parameters.AddWithValue("seller_name_raw_last_seen", rawName);
        command.Parameters.AddWithValue("category", classification.Category);
        command.Parameters.AddWithValue("confidence", classification.Confidence);
        command.Parameters.AddWithValue("needs_review", classification.NeedsReview);
        command.Parameters.AddWithValue("reason", classification.Reason);
        command.Parameters.AddWithValue("evidence_type", classification.EvidenceType);
        command.Parameters.AddWithValue("model", (object?)classification.Model ?? DBNull.Value);
        command.Parameters.AddWithValue("prompt_version", classification.PromptVersion);
        command.Parameters.AddWithValue("classified_at", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var token = await GetDatabaseAccessTokenAsync(cancellationToken);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = _options.PostgreSqlHost,
            Database = _options.Database,
            Username = _options.DatabaseUser,
            Password = token,
            SslMode = _options.RequireTls ? SslMode.VerifyFull : SslMode.Disable,
            Timeout = _options.CommandTimeoutSeconds,
            CommandTimeout = _options.CommandTimeoutSeconds
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private async Task<string> GetDatabaseAccessTokenAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.AccessToken)) return _options.AccessToken;
        var cached = _cachedToken;
        if (!string.IsNullOrWhiteSpace(cached.Token) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5)) return cached.Token;
        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            cached = _cachedToken;
            if (!string.IsNullOrWhiteSpace(cached.Token) && cached.ExpiresOn > DateTimeOffset.UtcNow.AddMinutes(5)) return cached.Token;
            var accessToken = await _credential.GetTokenAsync(new TokenRequestContext(["https://ossrdbms-aad.database.windows.net/.default"]), cancellationToken);
            _cachedToken = (accessToken.Token, accessToken.ExpiresOn);
            return accessToken.Token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }
}
