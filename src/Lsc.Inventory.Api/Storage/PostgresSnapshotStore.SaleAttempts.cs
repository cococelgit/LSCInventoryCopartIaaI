using System.Text.Json;
using Lsc.Inventory.Api.SaleAttempts;
using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore : ISaleAttemptHistoryStore
{
    private static readonly SemaphoreSlim SaleAttemptSchemaLock = new(1, 1);
    private static bool _saleAttemptSchemaInitialized;

    internal const string SaleAttemptSchemaSql = """
        create table if not exists schema_migrations (
            migration_id text primary key,
            applied_at timestamptz not null default now()
        );

        create table if not exists inventory_sale_attempts (
            platform text not null check (platform in ('iaai', 'copart')),
            lot_key text not null,
            lot_number text not null,
            provider_vehicle_id text,
            provider_lot_id text,
            provider_attempt_id text,
            attempt_key text not null,
            sale_date timestamptz not null,
            status text not null,
            status_id integer,
            bid_usd numeric,
            buy_now_usd numeric,
            final_bid_updated_at timestamptz,
            input_hash text not null,
            source_observed_at timestamptz not null,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now(),
            primary key (attempt_key)
        );
        create unique index if not exists ux_inventory_sale_attempts_provider_id
            on inventory_sale_attempts (platform, provider_attempt_id)
            where provider_attempt_id is not null;
        create index if not exists ix_inventory_sale_attempts_lot_date
            on inventory_sale_attempts (lot_key, sale_date desc, attempt_key);
        create index if not exists ix_inventory_sale_attempts_status_date
            on inventory_sale_attempts (status, sale_date desc, lot_key);

        create table if not exists inventory_sale_attempt_signals_current (
            lot_key text primary key,
            platform text not null check (platform in ('iaai', 'copart')),
            lot_number text not null,
            history_availability text not null,
            attempt_count integer not null,
            not_sold_count integer not null,
            historical_max_bid_usd numeric,
            current_buy_now_usd numeric,
            current_seller_reserve_usd numeric,
            current_ask_usd numeric,
            ask_gap_usd numeric,
            ask_gap_percent numeric,
            buy_now_drop_percent numeric,
            first_attempt_at timestamptz,
            last_attempt_at timestamptz,
            last_not_sold_at timestamptz,
            days_in_cycle integer not null,
            signal_level text not null,
            confidence_percent numeric not null,
            reason_codes jsonb not null default '[]'::jsonb,
            policy_version text not null,
            input_hash text not null,
            source_observed_at timestamptz not null,
            calculated_at timestamptz not null,
            created_at timestamptz not null default now(),
            updated_at timestamptz not null default now()
        );
        create index if not exists ix_inventory_sale_attempt_signals_rank
            on inventory_sale_attempt_signals_current (signal_level, ask_gap_percent nulls last, not_sold_count desc, lot_key);
        create index if not exists ix_inventory_sale_attempt_signals_attempts
            on inventory_sale_attempt_signals_current (not_sold_count desc, lot_key);

        insert into schema_migrations (migration_id)
        values ('015_sale_attempt_intelligence_v1')
        on conflict (migration_id) do nothing;
        """;

    public async Task<SaleAttemptPersistenceResult> PersistAsync(AuctionSaleHistorySnapshot snapshot, SellerMotivationSignal signal, CancellationToken cancellationToken)
    {
        if (!_saleAttempts.Enabled || !_saleAttempts.AllowWrites)
            throw new InvalidOperationException("Sale-attempt writes require both SaleAttemptIntelligence:Enabled and AllowWrites.");

        await EnsureSaleAttemptSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var inserted = 0;
        var updated = 0;
        var unchanged = 0;

        foreach (var attempt in snapshot.Attempts)
        {
            string? previousHash;
            await using (var existing = connection.CreateCommand())
            {
                existing.Transaction = transaction;
                existing.CommandTimeout = _persistence.CommandTimeoutSeconds;
                existing.CommandText = "select input_hash from inventory_sale_attempts where attempt_key = @attempt_key;";
                AddParameter(existing, "attempt_key", attempt.AttemptKey);
                previousHash = (string?)await existing.ExecuteScalarAsync(cancellationToken);
            }

            if (string.Equals(previousHash, attempt.InputHash, StringComparison.Ordinal))
            {
                unchanged++;
                continue;
            }

            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
                insert into inventory_sale_attempts (
                    platform, lot_key, lot_number, provider_vehicle_id, provider_lot_id,
                    provider_attempt_id, attempt_key, sale_date, status, status_id, bid_usd,
                    buy_now_usd, final_bid_updated_at, input_hash, source_observed_at, updated_at)
                values (
                    @platform, @lot_key, @lot_number, @provider_vehicle_id, @provider_lot_id,
                    @provider_attempt_id, @attempt_key, @sale_date, @status, @status_id, @bid_usd,
                    @buy_now_usd, @final_bid_updated_at, @input_hash, @source_observed_at, now())
                on conflict (attempt_key) do update set
                    provider_vehicle_id = excluded.provider_vehicle_id,
                    provider_lot_id = excluded.provider_lot_id,
                    provider_attempt_id = excluded.provider_attempt_id,
                    sale_date = excluded.sale_date,
                    status = excluded.status,
                    status_id = excluded.status_id,
                    bid_usd = excluded.bid_usd,
                    buy_now_usd = excluded.buy_now_usd,
                    final_bid_updated_at = excluded.final_bid_updated_at,
                    input_hash = excluded.input_hash,
                    source_observed_at = excluded.source_observed_at,
                    updated_at = now()
                where inventory_sale_attempts.source_observed_at <= excluded.source_observed_at;
                """;
            AddAttemptParameters(command, attempt);
            var changed = await command.ExecuteNonQueryAsync(cancellationToken);
            if (previousHash is null && changed == 1) inserted++;
            else if (changed == 1) updated++;
            else unchanged++;
        }

        var signalChanged = await UpsertSaleAttemptSignalAsync(connection, transaction, signal, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(inserted, updated, unchanged, signalChanged);
    }

    public async Task<StoredSaleAttemptState?> GetAsync(string lotKey, CancellationToken cancellationToken)
    {
        await EnsureSaleAttemptSchemaAsync(cancellationToken);
        await using var connection = await OpenConnectionAsync(cancellationToken);
        var attempts = new List<AuctionSaleAttempt>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
                select platform, lot_key, lot_number, provider_vehicle_id, provider_lot_id,
                       provider_attempt_id, attempt_key, sale_date, status, status_id, bid_usd,
                       buy_now_usd, final_bid_updated_at, source_observed_at, input_hash
                from inventory_sale_attempts
                where lot_key = @lot_key
                order by sale_date, attempt_key;
                """;
            AddParameter(command, "lot_key", lotKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) attempts.Add(ReadAttempt(reader));
        }

        SellerMotivationSignal? signal = null;
        await using (var command = connection.CreateCommand())
        {
            command.CommandTimeout = _persistence.CommandTimeoutSeconds;
            command.CommandText = """
                select platform, lot_key, lot_number, history_availability, attempt_count,
                       not_sold_count, historical_max_bid_usd, current_buy_now_usd,
                       current_seller_reserve_usd, current_ask_usd, ask_gap_usd, ask_gap_percent,
                       buy_now_drop_percent, first_attempt_at, last_attempt_at, last_not_sold_at,
                       days_in_cycle, signal_level, confidence_percent, reason_codes::text,
                       policy_version, input_hash, source_observed_at, calculated_at
                from inventory_sale_attempt_signals_current where lot_key = @lot_key;
                """;
            AddParameter(command, "lot_key", lotKey);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken)) signal = ReadSignal(reader);
        }

        return attempts.Count == 0 && signal is null ? null : new(attempts, signal);
    }

    private async Task EnsureSaleAttemptSchemaAsync(CancellationToken cancellationToken)
    {
        if (_saleAttemptSchemaInitialized) return;
        if (!_persistence.RunMigrations)
            throw new InvalidOperationException("Sale-attempt schema is unavailable because Persistence:RunMigrations is disabled.");
        await SaleAttemptSchemaLock.WaitAsync(cancellationToken);
        try
        {
            if (_saleAttemptSchemaInitialized) return;
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
            command.CommandText = SaleAttemptSchemaSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
            _saleAttemptSchemaInitialized = true;
        }
        finally
        {
            SaleAttemptSchemaLock.Release();
        }
    }

    private async Task<bool> UpsertSaleAttemptSignalAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, SellerMotivationSignal signal, CancellationToken cancellationToken)
    {
        string? previousHash;
        await using (var existing = connection.CreateCommand())
        {
            existing.Transaction = transaction;
            existing.CommandTimeout = _persistence.CommandTimeoutSeconds;
            existing.CommandText = "select input_hash from inventory_sale_attempt_signals_current where lot_key = @lot_key;";
            AddParameter(existing, "lot_key", signal.LotKey);
            previousHash = (string?)await existing.ExecuteScalarAsync(cancellationToken);
        }
        if (string.Equals(previousHash, signal.InputHash, StringComparison.Ordinal)) return false;

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandTimeout = _persistence.CommandTimeoutSeconds;
        command.CommandText = """
            insert into inventory_sale_attempt_signals_current (
                lot_key, platform, lot_number, history_availability, attempt_count, not_sold_count,
                historical_max_bid_usd, current_buy_now_usd, current_seller_reserve_usd,
                current_ask_usd, ask_gap_usd, ask_gap_percent, buy_now_drop_percent,
                first_attempt_at, last_attempt_at, last_not_sold_at, days_in_cycle,
                signal_level, confidence_percent, reason_codes, policy_version, input_hash,
                source_observed_at, calculated_at, updated_at)
            values (
                @lot_key, @platform, @lot_number, @history_availability, @attempt_count, @not_sold_count,
                @historical_max_bid_usd, @current_buy_now_usd, @current_seller_reserve_usd,
                @current_ask_usd, @ask_gap_usd, @ask_gap_percent, @buy_now_drop_percent,
                @first_attempt_at, @last_attempt_at, @last_not_sold_at, @days_in_cycle,
                @signal_level, @confidence_percent, cast(@reason_codes as jsonb), @policy_version,
                @input_hash, @source_observed_at, @calculated_at, now())
            on conflict (lot_key) do update set
                platform = excluded.platform,
                lot_number = excluded.lot_number,
                history_availability = excluded.history_availability,
                attempt_count = excluded.attempt_count,
                not_sold_count = excluded.not_sold_count,
                historical_max_bid_usd = excluded.historical_max_bid_usd,
                current_buy_now_usd = excluded.current_buy_now_usd,
                current_seller_reserve_usd = excluded.current_seller_reserve_usd,
                current_ask_usd = excluded.current_ask_usd,
                ask_gap_usd = excluded.ask_gap_usd,
                ask_gap_percent = excluded.ask_gap_percent,
                buy_now_drop_percent = excluded.buy_now_drop_percent,
                first_attempt_at = excluded.first_attempt_at,
                last_attempt_at = excluded.last_attempt_at,
                last_not_sold_at = excluded.last_not_sold_at,
                days_in_cycle = excluded.days_in_cycle,
                signal_level = excluded.signal_level,
                confidence_percent = excluded.confidence_percent,
                reason_codes = excluded.reason_codes,
                policy_version = excluded.policy_version,
                input_hash = excluded.input_hash,
                source_observed_at = excluded.source_observed_at,
                calculated_at = excluded.calculated_at,
                updated_at = now()
            where inventory_sale_attempt_signals_current.source_observed_at <= excluded.source_observed_at;
            """;
        AddSignalParameters(command, signal);
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static void AddAttemptParameters(NpgsqlCommand command, AuctionSaleAttempt attempt)
    {
        AddParameter(command, "platform", attempt.Platform);
        AddParameter(command, "lot_key", attempt.LotKey);
        AddParameter(command, "lot_number", attempt.LotNumber);
        AddParameter(command, "provider_vehicle_id", attempt.ProviderVehicleId);
        AddParameter(command, "provider_lot_id", attempt.ProviderLotId);
        AddParameter(command, "provider_attempt_id", attempt.ProviderAttemptId);
        AddParameter(command, "attempt_key", attempt.AttemptKey);
        AddParameter(command, "sale_date", attempt.SaleDate);
        AddParameter(command, "status", attempt.Status);
        AddParameter(command, "status_id", attempt.StatusId);
        AddParameter(command, "bid_usd", attempt.BidUsd);
        AddParameter(command, "buy_now_usd", attempt.BuyNowUsd);
        AddParameter(command, "final_bid_updated_at", attempt.FinalBidUpdatedAt);
        AddParameter(command, "input_hash", attempt.InputHash);
        AddParameter(command, "source_observed_at", attempt.SourceObservedAt);
    }

    private static void AddSignalParameters(NpgsqlCommand command, SellerMotivationSignal signal)
    {
        AddParameter(command, "lot_key", signal.LotKey);
        AddParameter(command, "platform", signal.Platform);
        AddParameter(command, "lot_number", signal.LotNumber);
        AddParameter(command, "history_availability", signal.HistoryAvailability);
        AddParameter(command, "attempt_count", signal.AttemptCount);
        AddParameter(command, "not_sold_count", signal.NotSoldCount);
        AddParameter(command, "historical_max_bid_usd", signal.HistoricalMaxBidUsd);
        AddParameter(command, "current_buy_now_usd", signal.CurrentBuyNowUsd);
        AddParameter(command, "current_seller_reserve_usd", signal.CurrentSellerReserveUsd);
        AddParameter(command, "current_ask_usd", signal.CurrentAskUsd);
        AddParameter(command, "ask_gap_usd", signal.AskGapUsd);
        AddParameter(command, "ask_gap_percent", signal.AskGapPercent);
        AddParameter(command, "buy_now_drop_percent", signal.BuyNowDropPercent);
        AddParameter(command, "first_attempt_at", signal.FirstAttemptAt);
        AddParameter(command, "last_attempt_at", signal.LastAttemptAt);
        AddParameter(command, "last_not_sold_at", signal.LastNotSoldAt);
        AddParameter(command, "days_in_cycle", signal.DaysInCycle);
        AddParameter(command, "signal_level", signal.SignalLevel);
        AddParameter(command, "confidence_percent", signal.ConfidencePercent);
        AddParameter(command, "reason_codes", JsonSerializer.Serialize(signal.ReasonCodes));
        AddParameter(command, "policy_version", signal.PolicyVersion);
        AddParameter(command, "input_hash", signal.InputHash);
        AddParameter(command, "source_observed_at", signal.SourceObservedAt);
        AddParameter(command, "calculated_at", signal.CalculatedAt);
    }

    private static AuctionSaleAttempt ReadAttempt(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6), reader.GetFieldValue<DateTimeOffset>(7),
        reader.GetString(8), reader.IsDBNull(9) ? null : reader.GetInt32(9),
        reader.IsDBNull(10) ? null : reader.GetDecimal(10), reader.IsDBNull(11) ? null : reader.GetDecimal(11),
        reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12), reader.GetFieldValue<DateTimeOffset>(13), reader.GetString(14));

    private static SellerMotivationSignal ReadSignal(NpgsqlDataReader reader) => new(
        reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5),
        reader.IsDBNull(6) ? null : reader.GetDecimal(6), reader.IsDBNull(7) ? null : reader.GetDecimal(7),
        reader.IsDBNull(8) ? null : reader.GetDecimal(8), reader.IsDBNull(9) ? null : reader.GetDecimal(9),
        reader.IsDBNull(10) ? null : reader.GetDecimal(10), reader.IsDBNull(11) ? null : reader.GetDecimal(11),
        reader.IsDBNull(12) ? null : reader.GetDecimal(12), reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
        reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14), reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
        reader.GetInt32(16), reader.GetString(17), reader.GetDecimal(18),
        JsonSerializer.Deserialize<string[]>(reader.GetString(19)) ?? [], reader.GetString(20), reader.GetString(21),
        reader.GetFieldValue<DateTimeOffset>(22), reader.GetFieldValue<DateTimeOffset>(23));
}
