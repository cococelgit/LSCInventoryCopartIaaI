using System.Text.Json;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.SaleAttempts;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class SellerMotivationSignalTests
{
    private static readonly DateTimeOffset ObservedAt = DateTimeOffset.Parse("2026-09-08T14:00:00Z");
    private readonly SellerMotivationSignalCalculator _calculator = new();

    [Theory]
    [InlineData("54450906", "copart", "high", 26, "-4.80")]
    [InlineData("53371186", "copart", "high", 19, "-28.57")]
    [InlineData("45785276", "iaai", "high", 5, "0.00")]
    [InlineData("37887931", "iaai", "follow_up", 4, null)]
    public void Classifies_confirmed_cases_with_explainable_components(string lot, string platform, string level, int notSold, string? gapPercent)
    {
        var snapshot = ReadFixture(lot, platform);

        var signal = _calculator.Calculate(snapshot, "motivated_seller_v1", ObservedAt);

        Assert.Equal(level, signal.SignalLevel);
        Assert.Equal(notSold, signal.NotSoldCount);
        Assert.Equal(gapPercent is null ? null : decimal.Parse(gapPercent, System.Globalization.CultureInfo.InvariantCulture), signal.AskGapPercent);
        Assert.Equal("motivated_seller_v1", signal.PolicyVersion);
        Assert.NotEmpty(signal.InputHash);
        Assert.NotEmpty(signal.ReasonCodes);
    }

    [Fact]
    public void Does_not_equate_missing_history_with_zero_attempts()
    {
        var snapshot = Snapshot("iaai", "100", SaleAttemptHistoryAvailability.NotRequested, [], currentBuyNow: 1200);

        var signal = _calculator.Calculate(snapshot, "motivated_seller_v1", ObservedAt);

        Assert.Equal(SaleAttemptSignalLevels.None, signal.SignalLevel);
        Assert.Equal(0, signal.ConfidencePercent);
        Assert.Contains("HISTORY_NOT_REQUESTED", signal.ReasonCodes);
    }

    [Fact]
    public void Suppresses_the_signal_when_the_current_lot_is_confirmed_sold()
    {
        var attempts = Enumerable.Range(1, 5)
            .Select(index => Attempt("iaai", "101", index, 1000 + index * 100))
            .ToArray();
        var snapshot = Snapshot("iaai", "101", SaleAttemptHistoryAvailability.Available, attempts, currentBuyNow: 1500, status: "sold");

        var signal = _calculator.Calculate(snapshot, "motivated_seller_v1", ObservedAt);

        Assert.Equal(SaleAttemptSignalLevels.None, signal.SignalLevel);
        Assert.Contains("CURRENTLY_SOLD", signal.ReasonCodes);
    }

    [Fact]
    public async Task In_memory_store_is_idempotent_and_updates_only_changed_attempts()
    {
        var store = new InMemorySaleAttemptHistoryStore();
        var snapshot = ReadFixture("45785276", "iaai");
        var signal = _calculator.Calculate(snapshot, "motivated_seller_v1", ObservedAt);

        var first = await store.PersistAsync(snapshot, signal, CancellationToken.None);
        var second = await store.PersistAsync(snapshot, signal, CancellationToken.None);
        var changedAttempt = snapshot.Attempts[0] with { BidUsd = 9999, InputHash = "changed-hash", SourceObservedAt = ObservedAt.AddMinutes(1) };
        var changedSnapshot = snapshot with { Attempts = [changedAttempt, .. snapshot.Attempts.Skip(1)], SourceObservedAt = ObservedAt.AddMinutes(1) };
        var changedSignal = _calculator.Calculate(changedSnapshot, "motivated_seller_v1", ObservedAt.AddMinutes(1));
        var third = await store.PersistAsync(changedSnapshot, changedSignal, CancellationToken.None);

        Assert.Equal(5, first.InsertedAttempts);
        Assert.True(first.SignalChanged);
        Assert.Equal(5, second.UnchangedAttempts);
        Assert.False(second.SignalChanged);
        Assert.Equal(1, third.UpdatedAttempts);
        Assert.Equal(4, third.UnchangedAttempts);
        Assert.True(third.SignalChanged);
        Assert.Equal(9999, (await store.GetAsync(snapshot.LotKey, CancellationToken.None))!.Attempts[0].BidUsd);
    }

    [Fact]
    public async Task Processor_defaults_to_disabled_and_requires_a_second_gate_for_writes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("45785276.json")));
        var disabled = new SaleAttemptIntelligenceProcessor(
            Microsoft.Extensions.Options.Options.Create(new SaleAttemptIntelligenceOptions()),
            _calculator,
            new InMemorySaleAttemptHistoryStore());
        var readOnly = new SaleAttemptIntelligenceProcessor(
            Microsoft.Extensions.Options.Options.Create(new SaleAttemptIntelligenceOptions { Enabled = true, AllowWrites = false }),
            _calculator,
            new InMemorySaleAttemptHistoryStore());

        var disabledResult = await disabled.ProcessAsync(document.RootElement, "iaai", ObservedAt, persist: false, CancellationToken.None);
        var dryRun = await readOnly.ProcessAsync(document.RootElement, "iaai", ObservedAt, persist: false, CancellationToken.None);

        Assert.False(disabledResult.Enabled);
        Assert.Empty(disabledResult.Signals);
        Assert.True(dryRun.Enabled);
        Assert.False(dryRun.Persisted);
        Assert.Single(dryRun.Signals);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            readOnly.ProcessAsync(document.RootElement, "iaai", ObservedAt, persist: true, CancellationToken.None));
    }

    [Fact]
    public void PostgreSql_schema_is_additive_idempotent_and_query_ready()
    {
        var sql = PostgresSnapshotStore.SaleAttemptSchemaSql;
        var migration = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Migrations", "015_sale_attempt_intelligence_v1.sql"));

        Assert.Contains("create table if not exists inventory_sale_attempts", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("primary key (attempt_key)", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("where provider_attempt_id is not null", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("create table if not exists inventory_sale_attempt_signals_current", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("policy_version text not null", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("015_sale_attempt_intelligence_v1", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("drop table", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("truncate", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(NormalizeSql(migration), NormalizeSql(sql));
    }

    [Fact]
    public void Recalculates_the_versioned_signal_when_days_in_cycle_changes()
    {
        var snapshot = ReadFixture("45785276", "iaai");

        var first = _calculator.Calculate(snapshot, "motivated_seller_v1", ObservedAt);
        var nextDay = _calculator.Calculate(snapshot, "motivated_seller_v1", ObservedAt.AddDays(1));

        Assert.NotEqual(first.InputHash, nextDay.InputHash);
        Assert.Equal(first.DaysInCycle + 1, nextDay.DaysInCycle);
    }

    private AuctionSaleHistorySnapshot ReadFixture(string lot, string platform)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath($"{lot}.json")));
        return Assert.Single(AuctionsApiSaleAttemptParser.Parse(document.RootElement, platform, ObservedAt).Snapshots);
    }

    private static AuctionSaleHistorySnapshot Snapshot(string platform, string lot, string availability, IReadOnlyList<AuctionSaleAttempt> attempts, decimal? currentBuyNow, string? status = "sale") => new(
        platform,
        $"{platform}:{lot}",
        lot,
        null,
        null,
        availability,
        new(null, currentBuyNow, null, null, null, status, ObservedAt.AddDays(1), null, false),
        attempts,
        ObservedAt);

    private static AuctionSaleAttempt Attempt(string platform, string lot, int index, decimal bid) => new(
        platform,
        $"{platform}:{lot}",
        lot,
        null,
        null,
        index.ToString(System.Globalization.CultureInfo.InvariantCulture),
        $"{platform}:price:{index}",
        ObservedAt.AddDays(-index),
        "not_sold",
        8,
        bid,
        null,
        ObservedAt.AddDays(-index),
        ObservedAt,
        $"hash-{index}");

    private static string FixturePath(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", "SaleAttempts", name);

    private static string NormalizeSql(string sql) => string.Join('\n', sql
        .Replace("\r", string.Empty, StringComparison.Ordinal)
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(line => !string.Equals(line, "begin;", StringComparison.OrdinalIgnoreCase) &&
                       !string.Equals(line, "commit;", StringComparison.OrdinalIgnoreCase)));
}
