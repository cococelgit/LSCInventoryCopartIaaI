using Xunit;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Normalization;

namespace Lsc.Inventory.Api.Tests;

public sealed class SellerTaxonomyTests
{
    [Theory]
    [InlineData("insurance", null, null, "Any Seller", SellerTaxonomy.Insurance)]
    [InlineData("unknown", null, null, "Global Insurance Services", SellerTaxonomy.Insurance)]
    [InlineData(null, "insurance", null, "Any Seller", SellerTaxonomy.Insurance)]
    [InlineData(null, null, null, "Global Insurance Services", SellerTaxonomy.Insurance)]
    [InlineData(null, null, null, "VOYAGER GLOBAL MOBILITY", SellerTaxonomy.Other)]
    [InlineData(null, null, null, "ROUTES CAR RENTAL", SellerTaxonomy.RentalFleet)]
    [InlineData(null, null, null, "M & T BANK", SellerTaxonomy.RepossessionBank)]
    [InlineData(null, null, null, "VINTARI AUTO GROUP LLC", SellerTaxonomy.Dealer)]
    [InlineData(null, null, null, "Unknown Seller", SellerTaxonomy.Other)]
    public void Classify_uses_common_evidence_and_name_rules(string? rawType, string? rawClass, string? rawTextClass, string name, string expected)
    {
        Assert.Equal(expected, SellerTaxonomy.Classify(rawType, rawClass, rawTextClass, name));
    }

    [Fact]
    public void Clean_preserves_visible_name_and_raw_type_while_deriving_common_category()
    {
        var cleaned = CanonicalVehicleCleaner.Clean(new AuctionVehicle
        {
            Platform = "iaai",
            Seller = new AuctionSeller { Name = "  Progressive Casualty  ", Type = "Insurance" }
        });

        Assert.Equal("Progressive Casualty", cleaned.Seller?.Name);
        Assert.Equal("Insurance", cleaned.Seller?.RawType);
        Assert.Equal(SellerTaxonomy.Insurance, cleaned.Seller?.Type);
        Assert.Equal(SellerTaxonomy.Version, cleaned.Seller?.TaxonomyVersion);
        Assert.Equal(1.0m, cleaned.Seller?.ClassificationConfidence);
        Assert.False(cleaned.Seller?.NeedsReview);

        var probable = CanonicalVehicleCleaner.Clean(new AuctionVehicle
        {
            Platform = "copart",
            Seller = new AuctionSeller { Name = "Global Insurance Services" }
        });
        Assert.Equal(SellerTaxonomy.Insurance, probable.Seller?.Type);
        Assert.Equal(0.75m, probable.Seller?.ClassificationConfidence);
        Assert.True(probable.Seller?.NeedsReview);
    }
}
