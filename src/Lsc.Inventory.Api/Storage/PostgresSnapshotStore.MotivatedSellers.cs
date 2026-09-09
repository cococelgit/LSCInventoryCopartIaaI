using Lsc.Inventory.Api.SaleAttempts;
using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    public Task EnsureMotivatedSellerSchemaForMigrationAsync(CancellationToken cancellationToken) => EnsureSaleAttemptSchemaAsync(cancellationToken);

    public async Task<MotivatedSellerDetail?> GetMotivatedSellerDetailAsync(string lotKey, CancellationToken cancellationToken)
    {
        var normalizedLotKey = lotKey.Trim().StartsWith("copart:", StringComparison.OrdinalIgnoreCase)
            ? lotKey.Trim().ToLowerInvariant()
            : $"copart:{lotKey.Trim()}";
        if (normalizedLotKey.Length <= "copart:".Length) return null;

        try
        {
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
                AddParameter(command, "lot_key", normalizedLotKey);
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
                    from inventory_sale_attempt_signals_current
                    where lot_key = @lot_key;
                    """;
                AddParameter(command, "lot_key", normalizedLotKey);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken)) signal = ReadSignal(reader);
            }

            return attempts.Count == 0 && signal is null ? null : new MotivatedSellerDetail(normalizedLotKey, signal, attempts);
        }
        catch (PostgresException exception) when (exception.SqlState is "42P01" or "42703")
        {
            return null;
        }
    }

    public async Task<MotivatedSellerReport> GetMotivatedSellerReportAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            long attempts;
            long signals;
            var levels = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            var availability = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            await using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = _persistence.CommandTimeoutSeconds;
                command.CommandText = "select count(*) from inventory_sale_attempts;";
                attempts = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = _persistence.CommandTimeoutSeconds;
                command.CommandText = "select count(*) from inventory_sale_attempt_signals_current;";
                signals = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = _persistence.CommandTimeoutSeconds;
                command.CommandText = "select signal_level, count(*) from inventory_sale_attempt_signals_current group by signal_level;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) levels[reader.GetString(0)] = reader.GetInt64(1);
            }
            await using (var command = connection.CreateCommand())
            {
                command.CommandTimeout = _persistence.CommandTimeoutSeconds;
                command.CommandText = "select history_availability, count(*) from inventory_sale_attempt_signals_current group by history_availability;";
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) availability[reader.GetString(0)] = reader.GetInt64(1);
            }
            return new MotivatedSellerReport(0, attempts, signals, levels, availability);
        }
        catch (PostgresException exception) when (exception.SqlState is "42P01" or "42703")
        {
            return new MotivatedSellerReport(0, 0, 0, new Dictionary<string, long>(), new Dictionary<string, long>());
        }
    }
}
