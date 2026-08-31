using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Normalization;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class SellerTaxonomyNormalizationTests
{
    [Theory]
    [InlineData("Progressive Casualty", SellerTaxonomy.Insurance)]
    [InlineData("Franchise Dealer", SellerTaxonomy.Dealer)]
    [InlineData("Ford Motor Credit", SellerTaxonomy.Finance)]
    [InlineData("Bank Repossession", SellerTaxonomy.RepossessionBank)]
    [InlineData("Rental Fleet", SellerTaxonomy.RentalFleet)]
    [InlineData("County Government", SellerTaxonomy.Government)]
    [InlineData("UNKNOWN", SellerTaxonomy.Unknown)]
    [InlineData("Private Seller", SellerTaxonomy.Other)]
    [InlineData(null, SellerTaxonomy.Unclassified)]
    public void Normalize_uses_declared_evidence_and_keeps_absent_data_unclassified(string? source, string expected)
    {
        Assert.Equal(expected, SellerTaxonomy.Normalize(source));
    }

    [Fact]
    public void Clean_preserves_raw_seller_type_while_storing_the_canonical_category()
    {
        var cleaned = CanonicalVehicleCleaner.Clean(new AuctionVehicle
        {
            Seller = new AuctionSeller { Name = " Progressive Casualty ", Type = " Insurance Company " }
        });

        Assert.Equal("Progressive Casualty", cleaned.Seller?.Name);
        Assert.Equal("Insurance Company", cleaned.Seller?.RawType);
        Assert.Equal(SellerTaxonomy.Insurance, cleaned.Seller?.Type);
    }
}
