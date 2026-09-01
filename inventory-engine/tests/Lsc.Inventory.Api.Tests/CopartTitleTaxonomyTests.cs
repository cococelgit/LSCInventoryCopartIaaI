using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Sources;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartTitleTaxonomyTests
{
    [Theory]
    [InlineData("AQ", TitleFacetCategory.Clean, CopartTitleMapper.ClassifiedReviewStatus)]
    [InlineData("BS", TitleFacetCategory.Salvage, CopartTitleMapper.ClassifiedReviewStatus)]
    [InlineData("AR", TitleFacetCategory.Rebuilt, CopartTitleMapper.ClassifiedReviewStatus)]
    [InlineData("AD", TitleFacetCategory.Special, CopartTitleMapper.ClassifiedReviewStatus)]
    [InlineData("B1", TitleFacetCategory.Other, CopartTitleMapper.ReviewRequiredStatus)]
    public void Eligible_copart_taxonomy_uses_only_the_canonical_title_facet_category(string code, string expectedCategory, string expectedReviewStatus)
    {
        var mapped = CopartTitleMapper.ApplyTaxonomy(CopartTitleMapper.Apply(CopartVehicle(code)));

        Assert.Equal(expectedCategory, Text(mapped, "title_category"));
        Assert.Equal(expectedReviewStatus, Text(mapped, "title_review_status"));
        Assert.Equal(CopartTitleMapper.TaxonomyVersion, Text(mapped, "title_taxonomy_version"));
        Assert.Equal(code, Text(mapped, "source_title_type_code"));
        Assert.Equal(code, Text(mapped, "source_title_raw"));
        Assert.Equal("mapped", Text(mapped, "source_title_mapping"));
    }

    [Fact]
    public void Unknown_and_missing_codes_are_auditable_without_inventing_a_title_category()
    {
        var unknown = CopartTitleMapper.ApplyTaxonomy(CopartTitleMapper.Apply(CopartVehicle("ZZ")));
        var missing = CopartTitleMapper.ApplyTaxonomy(CopartTitleMapper.Apply(new AuctionVehicle { Platform = InventorySourcePolicy.CopartExcelSource }));

        Assert.Equal(TitleFacetCategory.Other, Text(unknown, "title_category"));
        Assert.Equal(CopartTitleMapper.ReviewRequiredStatus, Text(unknown, "title_review_status"));
        Assert.Equal("ZZ", Text(unknown, "source_title_raw"));
        Assert.Equal(TitleFacetCategory.Unverified, Text(missing, "title_category"));
        Assert.Equal(CopartTitleMapper.UnverifiedReviewStatus, Text(missing, "title_review_status"));
        Assert.Null(CopartTitleMapper.ReadCode(missing));
    }

    [Fact]
    public void Source_mapping_does_not_apply_taxonomy_until_the_eligible_snapshot_stage()
    {
        var sourceMapped = CopartTitleMapper.Apply(CopartVehicle("BS"));

        Assert.False(sourceMapped.AdditionalData!.ContainsKey("title_category"));
        Assert.Equal("BS", Text(sourceMapped, "source_title_type_code"));
        Assert.Equal("Salvage Certificate - Fire Damage", sourceMapped.Title);

        var classified = CopartTitleMapper.ApplyTaxonomy(sourceMapped);
        Assert.Equal(TitleFacetCategory.Salvage, Text(classified, "title_category"));
        Assert.Equal("BS", Text(classified, "source_title_raw"));
    }

    [Fact]
    public void Canonical_taxonomy_is_idempotent_and_keeps_the_original_document_values()
    {
        var once = CopartTitleMapper.ApplyTaxonomy(CopartTitleMapper.Apply(CopartVehicle("CT")));
        var twice = CopartTitleMapper.ApplyTaxonomy(once);

        Assert.Equal(JsonSerializer.Serialize(once), JsonSerializer.Serialize(twice));
        Assert.Equal("Clean Title - Theft Recovery", twice.Title);
        Assert.Equal("CT", Text(twice, "source_title_type_code"));
        Assert.Equal(TitleFacetCategory.Clean, Text(twice, "title_category"));
        Assert.Contains("Theft Recovery", twice.AdditionalData!["title_flags"].Deserialize<string[]>()!);
    }

    [Fact]
    public void Mapper_and_taxonomy_do_not_change_iaai()
    {
        var iaai = new AuctionVehicle
        {
            Platform = "iaai",
            Title = "AQ",
            AdditionalData = new Dictionary<string, JsonElement>
            {
                ["source_title_type_code"] = JsonSerializer.SerializeToElement("AQ")
            }
        };

        var result = CopartTitleMapper.ApplyTaxonomy(CopartTitleMapper.Apply(iaai));

        Assert.Same(iaai, result);
        Assert.False(result.AdditionalData!.ContainsKey("title_category"));
    }

    private static AuctionVehicle CopartVehicle(string code) => new()
    {
        Platform = InventorySourcePolicy.CopartExcelSource,
        Title = code,
        SaleDocument = new SaleDocument { Name = code },
        RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["Sale Title Type"] = code })
    };

    private static string? Text(AuctionVehicle vehicle, string key) =>
        vehicle.AdditionalData is not null && vehicle.AdditionalData.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
