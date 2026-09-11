using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    /// <summary>
    /// Operational integrity audit for Inventory V2. This deliberately does not
    /// query any V1 projection or JSONB table, so it remains usable after V1 is
    /// retired. Historical V1 parity evidence lives in the migration documents.
    /// </summary>
    public async Task<InventoryV2IntegrityReport> GetInventoryV2IntegrityReportAsync(string? platform, CancellationToken cancellationToken)
    {
        var normalizedPlatform = string.IsNullOrWhiteSpace(platform) || platform.Equals("all", StringComparison.OrdinalIgnoreCase)
            ? null
            : platform.Trim().ToLowerInvariant();
        if (normalizedPlatform is not null && normalizedPlatform is not ("copart" or "iaai"))
            throw new ArgumentOutOfRangeException(nameof(platform));

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
        command.CommandText = """
            with scoped as (
                select *
                from inventory_current_v2
                where (@platform::text is null or platform = @platform::text)
            ), media_counts as (
                select m.platform, m.lot_number, count(*) filter (where m.media_type = 'photo')::integer as photo_count
                from inventory_media_current_v2 m
                join scoped s on s.platform = m.platform and s.lot_number = m.lot_number
                group by m.platform, m.lot_number
            )
            select
                (select count(*)::bigint from scoped) as v2_rows,
                (select count(*) filter (where is_active)::bigint from scoped) as active_rows,
                (select count(*) filter (where not is_active)::bigint from scoped) as inactive_rows,
                (select count(*) filter (where nullif(btrim(platform), '') is null or nullif(btrim(lot_number), '') is null or nullif(btrim(lot_key), '') is null)::bigint from scoped) as missing_identity,
                (select count(*) filter (where nullif(btrim(identity_hash), '') is null or nullif(btrim(spec_hash), '') is null or nullif(btrim(condition_hash), '') is null or nullif(btrim(auction_hash), '') is null or nullif(btrim(seller_location_hash), '') is null or nullif(btrim(media_hash), '') is null or nullif(btrim(score_input_hash), '') is null or nullif(btrim(search_hash), '') is null)::bigint from scoped) as missing_hashes,
                (select count(*) filter (where s.media_photos_count <> coalesce(mc.photo_count, 0))::bigint from scoped s left join media_counts mc on mc.platform = s.platform and mc.lot_number = s.lot_number) as media_count_mismatches,
                (select count(*) filter (where seller_needs_review)::bigint from scoped) as seller_needs_review,
                (select count(distinct platform)::bigint from scoped) as platform_count,
                (select max(updated_at) from scoped) as as_of;
            """;
        AddParameter(command, "platform", normalizedPlatform);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Inventory V2 integrity query returned no result.");

        var failures = new List<string>();
        var v2Rows = reader.GetInt64(0);
        var activeRows = reader.GetInt64(1);
        var inactiveRows = reader.GetInt64(2);
        var missingIdentity = reader.GetInt64(3);
        var missingHashes = reader.GetInt64(4);
        var mediaCountMismatches = reader.GetInt64(5);
        var sellerNeedsReview = reader.GetInt64(6);
        var platformCount = reader.GetInt64(7);
        var asOf = ReadV2Date(reader, 8);

        if (v2Rows == 0) failures.Add("inventory_current_v2 has no rows for the requested scope");
        if (missingIdentity > 0) failures.Add($"{missingIdentity} rows have incomplete identity columns");
        if (missingHashes > 0) failures.Add($"{missingHashes} rows have incomplete change-detection hashes");
        if (mediaCountMismatches > 0) failures.Add($"{mediaCountMismatches} rows have media count mismatches");
        if (platformCount == 0) failures.Add("no platform is represented in the requested scope");

        return new InventoryV2IntegrityReport(
            normalizedPlatform ?? "all",
            v2Rows,
            activeRows,
            inactiveRows,
            missingIdentity,
            missingHashes,
            mediaCountMismatches,
            sellerNeedsReview,
            platformCount,
            asOf,
            failures.Count == 0,
            failures);
    }

    private static DateTimeOffset? ReadV2Date(NpgsqlDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal)) return null;
        var value = reader.GetValue(ordinal);
        return value switch
        {
            DateTimeOffset date => date,
            DateTime date => new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc)),
            _ => DateTimeOffset.Parse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!, System.Globalization.CultureInfo.InvariantCulture)
        };
    }
}

public sealed record InventoryV2IntegrityReport(
    string Platform,
    long V2Rows,
    long ActiveRows,
    long InactiveRows,
    long RowsMissingIdentity,
    long RowsMissingHashes,
    long MediaCountMismatches,
    long SellerNeedsReview,
    long PlatformCount,
    DateTimeOffset? AsOf,
    bool IsHealthy,
    IReadOnlyList<string> Failures);
