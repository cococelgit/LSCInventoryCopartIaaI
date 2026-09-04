using Npgsql;
using Lsc.Inventory.Api.Normalization;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    public async Task<SellerAuditReport> GetSellerAuditReportAsync(CancellationToken cancellationToken)
    {
        await EnsureSchemaAsync(cancellationToken);
        var saleDateFrom = DateTimeOffset.UtcNow;
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = Math.Max(_persistence.CommandTimeoutSeconds, 600);
        command.CommandText = """
            with eligible_lots as materialized (
                select lot_key, platform
                from auction_lots
                where lower(platform) in ('copart', 'iaai')
                  and (auction_at at time zone 'America/New_York')::date >= (now() at time zone 'America/New_York')::date
            ), current_versions as (
                select eligible.platform, current_version.payload
                from eligible_lots eligible
                join lateral (
                    select payload
                    from auction_lot_versions
                    where lot_key = eligible.lot_key
                    order by observed_at desc, id desc
                    limit 1
                ) current_version on true
            )
            select lower(platform),
                   coalesce(nullif(trim(payload #>> '{seller,name}'), ''), '<NULL>'),
                   coalesce(nullif(trim(payload #>> '{seller,type}'), ''), '<NULL>'),
                   coalesce(nullif(trim(payload #>> '{seller,class}'), ''), '<NULL>'),
                   coalesce(nullif(trim(payload #>> '{seller,text_class}'), ''), nullif(trim(payload #>> '{seller,textClass}'), ''), '<NULL>'),
                   count(*)::bigint
            from current_versions
            group by lower(platform),
                     coalesce(nullif(trim(payload #>> '{seller,name}'), ''), '<NULL>'),
                     coalesce(nullif(trim(payload #>> '{seller,type}'), ''), '<NULL>'),
                     coalesce(nullif(trim(payload #>> '{seller,class}'), ''), '<NULL>'),
                     coalesce(nullif(trim(payload #>> '{seller,text_class}'), ''), nullif(trim(payload #>> '{seller,textClass}'), ''), '<NULL>')
            order by lower(platform), count(*) desc,
                     coalesce(nullif(trim(payload #>> '{seller,name}'), ''), '<NULL>');
            """;

        var rows = new List<SellerAuditRow>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var platform = reader.GetString(0);
            var sellerName = NullMarker(reader.GetString(1));
            var sellerType = NullMarker(reader.GetString(2));
            var sellerClass = NullMarker(reader.GetString(3));
            var sellerTextClass = NullMarker(reader.GetString(4));
            var classification = SellerTaxonomy.ClassifyDetailed(sellerType, sellerClass, sellerTextClass, sellerName);
            rows.Add(new SellerAuditRow(
                platform,
                sellerName ?? "<NULL>",
                sellerType ?? "<NULL>",
                sellerClass ?? "<NULL>",
                sellerTextClass ?? "<NULL>",
                classification.Category,
                classification.Confidence,
                classification.NeedsReview,
                reader.GetInt64(5)));
        }

        return new SellerAuditReport(
            DateTimeOffset.UtcNow,
            saleDateFrom,
            rows.Sum(row => row.VehicleCount),
            rows);
    }

    private static string? NullMarker(string value) => value == "<NULL>" ? null : value;
}
