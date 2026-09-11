using System.Data.Common;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    public async Task<InventoryV2ParityReport> GetInventoryV2ParityReportAsync(string? platform, CancellationToken cancellationToken)
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
            select
                count(*)::bigint as v2_rows,
                count(*) filter (where v1.lot_key is null)::bigint as missing_v1,
                count(*) filter (where v1.lot_key is not null and v2.vin is distinct from v1.vin)::bigint as vin,
                count(*) filter (where v1.lot_key is not null and v2.year is distinct from v1.year)::bigint as year,
                count(*) filter (where v1.lot_key is not null and v2.make is distinct from v1.make)::bigint as make,
                count(*) filter (where v1.lot_key is not null and v2.model is distinct from v1.model)::bigint as model,
                count(*) filter (where v1.lot_key is not null and v2.vehicle_type is distinct from v1.vehicle_type)::bigint as vehicle_type,
                count(*) filter (where v1.lot_key is not null and v2.exterior_color is distinct from v1.color)::bigint as color,
                count(*) filter (where v1.lot_key is not null and v2.fuel_type is distinct from v1.fuel_type)::bigint as fuel_type,
                count(*) filter (where v1.lot_key is not null and v2.transmission is distinct from v1.transmission)::bigint as transmission,
                count(*) filter (where v1.lot_key is not null and v2.drive_type is distinct from v1.drive_type)::bigint as drive_type,
                count(*) filter (where v1.lot_key is not null and v2.body_style is distinct from v1.body_style)::bigint as body_style,
                count(*) filter (where v1.lot_key is not null and v2.title_type is distinct from v1.title_type)::bigint as title_type,
                count(*) filter (where v1.lot_key is not null and v2.primary_damage is distinct from v1.primary_damage)::bigint as primary_damage,
                count(*) filter (where v1.lot_key is not null and v2.secondary_damage is distinct from v1.secondary_damage)::bigint as secondary_damage,
                count(*) filter (where v1.lot_key is not null and v1.seller_name is not null and btrim(v2.seller_name) is distinct from btrim(v1.seller_name))::bigint as seller_name,
                count(*) filter (where v1.lot_key is not null and v2.seller_type is distinct from v1.seller_type)::bigint as seller_type,
                count(*) filter (where v1.lot_key is not null and v2.auction_state is distinct from v1.auction_state)::bigint as auction_state,
                count(*) filter (where v1.lot_key is not null and v2.auction_at is distinct from v1.auction_at)::bigint as auction_at,
                count(*) filter (where v1.lot_key is not null and v2.lot_status is distinct from v1.lot_status)::bigint as lot_status,
                count(*) filter (where v1.lot_key is not null and v2.lot_sub_status is distinct from v1.lot_sub_status)::bigint as lot_sub_status,
                count(*) filter (where v1.lot_key is not null and v2.location_display is distinct from v1.location_display)::bigint as location_display,
                count(*) filter (where v1.lot_key is not null and v2.location_state is distinct from v1.location_state)::bigint as location_state,
                count(*) filter (where v1.lot_key is not null and v2.facility_id is distinct from v1.facility_id)::bigint as facility_id,
                count(*) filter (where v1.lot_key is not null and v2.odometer_miles is distinct from v1.odometer)::bigint as odometer,
                count(*) filter (where v1.lot_key is not null and v2.current_bid_usd is distinct from v1.current_bid_usd)::bigint as current_bid,
                count(*) filter (where v1.lot_key is not null and v2.buy_now_usd is distinct from v1.buy_now_usd)::bigint as buy_now,
                count(*) filter (where v1.lot_key is not null and v2.provider_estimate_from_usd is distinct from v1.provider_estimate_from)::bigint as estimate_from,
                count(*) filter (where v1.lot_key is not null and v2.provider_estimate_to_usd is distinct from v1.provider_estimate_to)::bigint as estimate_to,
                count(*) filter (where v1.lot_key is not null and v2.has_key is distinct from v1.has_key)::bigint as has_key,
                count(*) filter (where v1.lot_key is not null and v2.media_photos_count is distinct from coalesce((select count(*)::integer from inventory_media_current_v2 m where m.platform = v2.platform and m.lot_number = v2.lot_number and m.media_type = 'photo'), 0))::bigint as v2_media_internal,
                count(*) filter (where v1.lot_key is not null and v2.media_has_photos is distinct from v1.has_photos)::bigint as has_photos,
                count(*) filter (where v1.lot_key is not null and v2.media_has_360 is distinct from v1.media_has_360)::bigint as has_360,
                count(*) filter (where v1.lot_key is not null and v2.is_buy_now is distinct from v1.is_buy_now)::bigint as is_buy_now,
                count(*) filter (where v1.lot_key is not null and v2.is_active is distinct from v1.is_active)::bigint as is_active,
                count(*) filter (where v1.lot_key is not null and v1.seller_name is null and v2.seller_name is not null)::bigint as seller_v2_only,
                count(*) filter (where v1.lot_key is not null and v1.seller_name is not null and v2.seller_name is null)::bigint as seller_v1_only,
                count(*) filter (where v1.lot_key is not null and v1.seller_name is not null and v2.seller_name is not null and btrim(v1.seller_name) is distinct from btrim(v2.seller_name))::bigint as seller_conflict
            from inventory_current_v2 v2
            left join inventory_search_current v1 on v1.lot_key = v2.lot_key
            where (@platform::text is null or v2.platform = @platform::text);
            """;
        AddParameter(command, "platform", normalizedPlatform);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new InvalidOperationException("Inventory V2 parity query returned no result.");
        var mismatches = new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["vin"] = reader.GetInt64(2), ["year"] = reader.GetInt64(3), ["make"] = reader.GetInt64(4),
            ["model"] = reader.GetInt64(5), ["vehicle_type"] = reader.GetInt64(6), ["color"] = reader.GetInt64(7),
            ["fuel_type"] = reader.GetInt64(8), ["transmission"] = reader.GetInt64(9), ["drive_type"] = reader.GetInt64(10),
            ["body_style"] = reader.GetInt64(11), ["title_type"] = reader.GetInt64(12), ["primary_damage"] = reader.GetInt64(13),
            ["secondary_damage"] = reader.GetInt64(14), ["seller_name"] = reader.GetInt64(15), ["seller_type"] = reader.GetInt64(16),
            ["auction_state"] = reader.GetInt64(17), ["auction_at"] = reader.GetInt64(18), ["lot_status"] = reader.GetInt64(19),
            ["lot_sub_status"] = reader.GetInt64(20), ["location_display"] = reader.GetInt64(21), ["location_state"] = reader.GetInt64(22),
            ["facility_id"] = reader.GetInt64(23), ["odometer"] = reader.GetInt64(24), ["current_bid"] = reader.GetInt64(25),
            ["buy_now"] = reader.GetInt64(26), ["estimate_from"] = reader.GetInt64(27), ["estimate_to"] = reader.GetInt64(28),
            ["has_key"] = reader.GetInt64(29), ["v2_media_internal"] = reader.GetInt64(30), ["has_photos"] = reader.GetInt64(31),
            ["has_360"] = reader.GetInt64(32), ["is_buy_now"] = reader.GetInt64(33), ["is_active"] = reader.GetInt64(34),
        };
        var v2Rows = reader.GetInt64(0);
        var missingV1Rows = reader.GetInt64(1);
        var sellerParity = new InventoryV2SellerParity(
            reader.GetInt64(35),
            reader.GetInt64(36),
            reader.GetInt64(37));
        await reader.DisposeAsync();

        var sellerSamples = new List<InventoryV2SellerMismatchSample>();
        await using (var sampleCommand = connection.CreateCommand())
        {
            sampleCommand.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
            sampleCommand.CommandText = """
                select v2.platform, v2.lot_number, v1.seller_name, v2.seller_name
                from inventory_current_v2 v2
                join inventory_search_current v1 on v1.lot_key = v2.lot_key
                where (@platform::text is null or v2.platform = @platform::text)
                  and v1.seller_name is not null
                  and btrim(v2.seller_name) is distinct from btrim(v1.seller_name)
                order by v2.platform, v2.lot_number
                limit 20;
                """;
            AddParameter(sampleCommand, "platform", normalizedPlatform);
            await using var sampleReader = await sampleCommand.ExecuteReaderAsync(cancellationToken);
            while (await sampleReader.ReadAsync(cancellationToken))
                sellerSamples.Add(new(
                    sampleReader.GetString(0),
                    sampleReader.GetString(1),
                    sampleReader.IsDBNull(2) ? null : sampleReader.GetString(2),
                    sampleReader.IsDBNull(3) ? null : sampleReader.GetString(3)));
        }

        return new InventoryV2ParityReport(
            normalizedPlatform ?? "all",
            v2Rows,
            missingV1Rows,
            mismatches,
            mismatches.Values.Sum(),
            sellerParity,
            sellerSamples);
    }
}

public sealed record InventoryV2ParityReport(
    string Platform,
    long V2Rows,
    long MissingV1Rows,
    IReadOnlyDictionary<string, long> FieldMismatches,
    long TotalFieldMismatches,
    InventoryV2SellerParity SellerParity,
    IReadOnlyList<InventoryV2SellerMismatchSample> SellerMismatchSamples);

public sealed record InventoryV2SellerParity(
    long V2Only,
    long V1Only,
    long ConflictingNonNull);

public sealed record InventoryV2SellerMismatchSample(
    string Platform,
    string LotNumber,
    string? V1SellerName,
    string? V2SellerName);
