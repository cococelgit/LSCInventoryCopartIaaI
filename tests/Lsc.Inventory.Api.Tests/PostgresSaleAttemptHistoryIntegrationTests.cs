using System.Text.Json;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.SaleAttempts;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class PostgresSaleAttemptHistoryIntegrationTests
{
    [Fact]
    public async Task Persists_reads_and_replays_a_history_idempotently_in_an_isolated_database()
    {
        var connectionString = Environment.GetEnvironmentVariable("SALE_ATTEMPT_TEST_CONNECTION_STRING");
        if (string.IsNullOrWhiteSpace(connectionString)) return;

        await using (var reset = new NpgsqlConnection(connectionString))
        {
            await reset.OpenAsync();
            await using var command = reset.CreateCommand();
            command.CommandText = "truncate inventory_sale_attempts, inventory_sale_attempt_signals_current;";
            await command.ExecuteNonQueryAsync();
        }

        var store = new PostgresSnapshotStore(
            Microsoft.Extensions.Options.Options.Create(new PersistenceOptions
            {
                Provider = "Postgres",
                PostgreSqlHost = "localhost",
                Database = "lsc_sale_attempt_sprint1",
                ManagedIdentityClientId = string.Empty,
                DatabaseUser = "ubuntu",
                RunMigrations = true
            }),
            Microsoft.Extensions.Options.Options.Create(new BlobAuditOptions { AccountUrl = "https://example.invalid" }),
            NullLogger<PostgresSnapshotStore>.Instance,
            saleAttemptOptions: Microsoft.Extensions.Options.Options.Create(new SaleAttemptIntelligenceOptions { Enabled = true, AllowWrites = true }));
        store.UseConnectionFactoryForTests(async cancellationToken =>
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);
            return connection;
        });
        var observedAt = DateTimeOffset.Parse("2026-09-08T14:00:00Z");
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("45785276.json")));
        var snapshot = Assert.Single(AuctionsApiSaleAttemptParser.Parse(document.RootElement, "iaai", observedAt).Snapshots);
        var calculator = new SellerMotivationSignalCalculator();
        var signal = calculator.Calculate(snapshot, "motivated_seller_v1", observedAt);

        var first = await store.PersistAsync(snapshot, signal, CancellationToken.None);
        var second = await store.PersistAsync(snapshot, signal, CancellationToken.None);
        var stored = await store.GetAsync(snapshot.LotKey, CancellationToken.None);

        Assert.Equal(5, first.InsertedAttempts);
        Assert.True(first.SignalChanged);
        Assert.Equal(5, second.UnchangedAttempts);
        Assert.False(second.SignalChanged);
        Assert.NotNull(stored);
        Assert.Equal(5, stored.Attempts.Count);
        Assert.Equal(SaleAttemptSignalLevels.High, stored.Signal!.SignalLevel);
        Assert.Equal(1500, stored.Signal.HistoricalMaxBidUsd);
        Assert.Equal(0, stored.Signal.AskGapPercent);
    }

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SaleAttempts", name);
}
