using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    public async Task<SellerAuditReport> GetSellerAuditReportAsync(CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var saleDateFrom = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 120);
        command.CommandText = """
            with current_versions as (
                select distinct on (lots.lot_key)
                       lots.platform,
                       lots.auction_at,
                       versions.payload
                from auction_lots lots
                join auction_lot_versions versions on versions.lot_key = lots.lot_key
                where lower(lots.platform) in ('copart', 'iaai')
                  and (lots.auction_at at time zone 'America/New_York')::date >= (now() at time zone 'America/New_York')::date
                order by lots.lot_key, versions.observed_at desc, versions.id desc
            )
            select lower(platform),
                   coalesce(nullif(trim(payload #>> '{seller,name}'), ''), '<NULL>'),
                   coalesce(nullif(trim(payload #>> '{seller,type}'), ''), '<NULL>'),
                   coalesce(nullif(trim(payload #>> '{seller,class}'), ''), '<NULL>'),
                   coalesce(nullif(trim(payload #>> '{seller,text_class}'), ''), '<NULL>'),
                   count(*)::bigint
            from current_versions
            group by lower(platform),
                     coalesce(nullif(trim(payload #>> '{seller,name}'), ''), '<NULL>'),
                     coalesce(nullif(trim(payload #>> '{seller,type}'), ''), '<NULL>'),
                     coalesce(nullif(trim(payload #>> '{seller,class}'), ''), '<NULL>'),
                     coalesce(nullif(trim(payload #>> '{seller,text_class}'), ''), '<NULL>')
            order by lower(platform), count(*) desc,
                     coalesce(nullif(trim(payload #>> '{seller,name}'), ''), '<NULL>');
            """;

        var rows = new List<SellerAuditRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add(new SellerAuditRow(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt64(5)));
        }

        return new SellerAuditReport(
            DateTimeOffset.UtcNow,
            saleDateFrom,
            rows.Sum(row => row.VehicleCount),
            rows);
    }
}
