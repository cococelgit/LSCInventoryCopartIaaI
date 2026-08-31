using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class SellerTaxonomyAuditContractTests
{
    [Fact]
    public void Audit_is_aggregated_active_only_and_keeps_raw_source_evidence_separate_from_projection_values()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.cs"));
        var contract = File.ReadAllText(FindRepositoryFile("SnapshotStore.cs"));

        Assert.Contains("GetSellerTaxonomyAuditAsync", source, StringComparison.Ordinal);
        Assert.Contains("current.seller_type", source, StringComparison.Ordinal);
        Assert.Contains("'{Seller,Type}'", source, StringComparison.Ordinal);
        Assert.Contains("'{Seller,Class}'", source, StringComparison.Ordinal);
        Assert.Contains("'{Seller,TextClass}'", source, StringComparison.Ordinal);
        Assert.Contains("'{Seller,Name}'", source, StringComparison.Ordinal);
        Assert.Contains("ActiveLifecyclePredicate", source, StringComparison.Ordinal);
        Assert.Contains("TopSellerNamesMissingSourceType", contract, StringComparison.Ordinal);
    }

    [Fact]
    public void Audit_endpoint_requires_the_existing_read_token_and_exposes_no_lot_or_vin_parameter()
    {
        var source = File.ReadAllText(FindRepositoryFile("Program.cs"));

        Assert.Contains("/api/v1/inventory/seller-taxonomy/audit", source, StringComparison.Ordinal);
        Assert.Contains("HasValidReadToken(context, inventoryReadToken)", source, StringComparison.Ordinal);
        Assert.Contains("store.GetSellerTaxonomyAuditAsync(cancellationToken)", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var apiRoot = Path.Combine(directory.FullName, "src", "Lsc.Inventory.Api");
            var candidate = new FileInfo(Path.Combine(apiRoot, fileName));
            if (candidate.Exists) return candidate.FullName;
            candidate = new FileInfo(Path.Combine(apiRoot, "Storage", fileName));
            if (candidate.Exists) return candidate.FullName;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(fileName);
    }
}
