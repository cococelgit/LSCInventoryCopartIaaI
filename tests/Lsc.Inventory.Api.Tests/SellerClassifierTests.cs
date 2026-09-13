using Lsc.Inventory.Api.Classification;
using Xunit;
using Lsc.Inventory.Api.Normalization;

namespace Lsc.Inventory.Api.Tests;

public sealed class SellerClassifierTests
{
    [Theory]
    [InlineData("Insurance", SellerTaxonomy.Insurance)]
    [InlineData("Enterprise Rental Fleet", SellerTaxonomy.RentalFleet)]
    [InlineData("County of Miami", SellerTaxonomy.Government)]
    [InlineData("Unknown Seller", SellerTaxonomy.Unknown)]
    public void Deterministic_taxonomy_preserves_safe_categories(string rawName, string expected)
    {
        var result = SellerTaxonomy.ClassifyDetailed(null, null, null, rawName);
        Assert.Equal(expected, result.Category);
        Assert.True(SellerTaxonomy.IsAllowedCategory(result.Category));
    }

    [Fact]
    public void Missing_name_falls_back_to_unknown_without_guessing()
    {
        var result = SellerTaxonomy.ClassifyDetailed(null, null, null, null);
        Assert.Equal(SellerTaxonomy.Unknown, result.Category);
        Assert.Equal(0m, result.Confidence);
        Assert.Equal("missing_name", result.Evidence);
        Assert.Equal("insufficient_evidence", result.EvidenceType);
    }

    [Fact]
    public void Prompt_contract_contains_only_allowed_taxonomy_categories()
    {
        var format = System.Text.Json.JsonSerializer.Serialize(SellerClassifierPrompt.ResponseFormat);
        Assert.Contains("insurance", format, StringComparison.Ordinal);
        Assert.Contains("repossession_bank", format, StringComparison.Ordinal);
        Assert.Contains("unknown", format, StringComparison.Ordinal);
        Assert.DoesNotContain("copart", format, StringComparison.OrdinalIgnoreCase);
    }
}

