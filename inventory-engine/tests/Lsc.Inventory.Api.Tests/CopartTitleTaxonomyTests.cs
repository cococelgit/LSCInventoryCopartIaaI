using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Sources;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartTitleTaxonomyTests
{
    [Theory]
    [InlineData("AQ", "CLEAN", "STANDARD")]
    [InlineData("CT", "BRANDED_TITLE", "STANDARD")]
    [InlineData("AC", "SALVAGE", "STANDARD")]
    [InlineData("AR", "REBUILT_RECONSTRUCTED", "STANDARD")]
    [InlineData("AD", "NON_REPAIRABLE_PARTS_SCRAP", "DOCUMENT_REVIEW")]
    [InlineData("BE", "EXPORT_ONLY", "DOCUMENT_REVIEW")]
    [InlineData("BB", "DOCUMENT_ONLY", "DOCUMENT_REVIEW")]
    [InlineData("B1", "STATE_VARIANT_VERIFY", "DOCUMENT_REVIEW")]
    public void Known_codes_map_to_one_safe_normalized_category(string code, string expectedCategory, string expectedReviewStatus)
    {
        var mapped = CopartTitleMapper.Apply(CopartVehicle(code));

        Assert.Equal(expectedCategory, Text(mapped, "title_category"));
        Assert.Equal(expectedReviewStatus, Text(mapped, "title_review_status"));
        Assert.Equal(CopartTitleTaxonomy.Version, Text(mapped, "title_taxonomy_version"));
        Assert.Equal(code, Text(mapped, "source_title_type_code"));
        Assert.Equal("mapped", Text(mapped, "source_title_mapping"));
    }

    [Theory]
    [InlineData("BS", "FIRE")]
    [InlineData("DY", "WATER_FLOOD")]
    [InlineData("F1", "STRUCTURAL_FRAME_UNIBODY")]
    [InlineData("SC", "THEFT")]
    [InlineData("OT", "ODOMETER")]
    [InlineData("DA", "DEALER_RESTRICTION")]
    public void Taxonomy_keeps_disclosures_as_flags_instead_of_creating_more_primary_categories(string code, string expectedFlag)
    {
        var mapped = CopartTitleMapper.Apply(CopartVehicle(code));

        var flags = mapped.AdditionalData!["title_flags"].Deserialize<string[]>();
        Assert.Contains(expectedFlag, flags!);
        Assert.Equal("SALVAGE", Text(mapped, "title_category"));
    }

    [Fact]
    public void Unknown_or_missing_codes_remain_auditable_and_require_document_review()
    {
        var unknown = CopartTitleMapper.Apply(CopartVehicle("ZZ"));
        var missing = CopartTitleMapper.Apply(new AuctionVehicle { Platform = InventorySourcePolicy.CopartExcelSource });

        Assert.Equal("unmapped", Text(unknown, "source_title_mapping"));
        Assert.Equal("OTHER_UNVERIFIED", Text(unknown, "title_category"));
        Assert.Equal("DOCUMENT_REVIEW", Text(unknown, "title_review_status"));
        Assert.Contains("TITLE_CODE_UNMAPPED", unknown.AdditionalData!["title_flags"].Deserialize<string[]>()!);
        Assert.Equal("OTHER_UNVERIFIED", Text(missing, "title_category"));
        Assert.Null(CopartTitleMapper.ReadCode(missing));
    }

    [Fact]
    public void Mapper_does_not_apply_taxonomy_to_iaai()
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

        var result = CopartTitleMapper.Apply(iaai);

        Assert.Same(iaai, result);
        Assert.False(result.AdditionalData!.ContainsKey("title_category"));
    }

    [Fact]
    public void Taxonomy_serializes_with_copart_payload_without_changing_original_description()
    {
        var mapped = CopartTitleMapper.Apply(CopartVehicle("BS"));
        var payload = JsonSerializer.Serialize(mapped);
        using var document = JsonDocument.Parse(payload);

        Assert.Equal("Salvage Certificate - Fire Damage", mapped.Title);
        Assert.Equal("SALVAGE", document.RootElement.GetProperty("title_category").GetString());
        Assert.Equal(CopartTitleTaxonomy.Version, document.RootElement.GetProperty("title_taxonomy_version").GetString());
        Assert.Contains("FIRE", document.RootElement.GetProperty("title_flags").EnumerateArray().Select(item => item.GetString()));
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
