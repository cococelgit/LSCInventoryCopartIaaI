using Npgsql;

namespace Lsc.Inventory.Api.Storage;

public sealed partial class PostgresSnapshotStore
{
    private const string VerifiedSellerInsuranceTaxonomyVersion = "seller_taxonomy_ai_verified_v1_20260912";

    public async Task<SellerReclassificationResult> ApplyVerifiedSellerInsuranceReclassificationAsync(CancellationToken cancellationToken)
    {
        var verifiedNames = new Dictionary<string, decimal>(StringComparer.Ordinal)
        {
            ["state farm insurance"] = 0.95000m,
            ["usaa"] = 0.95000m,
            ["geico"] = 0.95000m,
            ["progressive"] = 0.95000m,
            ["bristol west insurance"] = 0.95000m,
            ["farmers insurance"] = 0.95000m,
            ["farmers insurance company of flemington"] = 0.95000m,
            ["csaa"] = 0.90000m,
            ["aig insurance"] = 0.95000m
        };

        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var seedCommand = connection.CreateCommand())
            {
                seedCommand.Transaction = transaction;
                seedCommand.CommandText = "create temporary table seller_reclassification_targets (seller_name_key text primary key, confidence numeric(6,5) not null) on commit drop;";
                foreach (var (name, confidence) in verifiedNames)
                {
                    var nameParameter = seedCommand.Parameters.Add($"name_{seedCommand.Parameters.Count}", NpgsqlTypes.NpgsqlDbType.Text);
                    nameParameter.Value = name;
                    var confidenceParameter = seedCommand.Parameters.Add($"confidence_{seedCommand.Parameters.Count}", NpgsqlTypes.NpgsqlDbType.Numeric);
                    confidenceParameter.Value = confidence;
                    seedCommand.CommandText += $"insert into seller_reclassification_targets (seller_name_key, confidence) values ({nameParameter.ParameterName}, {confidenceParameter.ParameterName});\n";
                }
                await seedCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            int candidateCount;
            await using (var countCommand = connection.CreateCommand())
            {
                countCommand.Transaction = transaction;
                countCommand.CommandText = """
                    select count(*)
                    from public.inventory_current_v2 v
                    join seller_reclassification_targets t on lower(btrim(v.seller_name)) = t.seller_name_key
                    where v.is_active and lower(btrim(v.platform)) = 'copart'
                      and lower(coalesce(v.seller_type, '')) = 'unknown';
                    """;
                candidateCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
            }
            if (candidateCount != 648)
                throw new InvalidOperationException($"Expected 648 verified Copart seller rows, found {candidateCount}.");

            int updated;
            await using (var updateCommand = connection.CreateCommand())
            {
                updateCommand.Transaction = transaction;
                updateCommand.CommandText = """
                    update public.inventory_current_v2 v
                    set seller_type = 'insurance',
                        seller_class = 'insurance',
                        seller_is_insurance = true,
                        seller_is_rental = false,
                        seller_is_credit_company = false,
                        seller_classification_confidence = t.confidence,
                        seller_needs_review = false,
                        seller_classification_evidence = 'ai_verified_external_sources',
                        seller_taxonomy_version = 'seller_taxonomy_ai_verified_v1_20260912',
                        score_input_hash = md5(coalesce(v.score_input_hash, '') || chr(31) || 'seller_taxonomy_ai_verified_v1_20260912'),
                        search_hash = md5(coalesce(v.search_hash, '') || chr(31) || 'seller_taxonomy_ai_verified_v1_20260912'),
                        record_version = v.record_version + 1,
                        updated_at = now()
                    from seller_reclassification_targets t
                    where v.is_active and lower(btrim(v.platform)) = 'copart'
                      and lower(btrim(v.seller_name)) = t.seller_name_key
                      and lower(coalesce(v.seller_type, '')) = 'unknown';
                    """;
                updated = await updateCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            if (updated != candidateCount)
                throw new InvalidOperationException($"Expected to update {candidateCount} rows, updated {updated}.");

            await transaction.CommitAsync(cancellationToken);
            return new SellerReclassificationResult(
                candidateCount,
                updated,
                verifiedNames.Keys.ToArray(),
                VerifiedSellerInsuranceTaxonomyVersion,
                DateTimeOffset.UtcNow);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }
}
