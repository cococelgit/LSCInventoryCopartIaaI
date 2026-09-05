using System.Text.Json;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Workers;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionsApiMediaMappingTests
{
    [Fact]
    public void MapRows_preserves_iaai_condition_and_seller_evidence()
    {
        using var document = JsonDocument.Parse("""
            [{
              "vin":"1TESTVIN123456789",
              "year":2020,
              "manufacturer":{"name":"Toyota"},
              "model":{"name":"Camry"},
              "vehicle_type":{"name":"Sedan"},
              "lots":[{
                "lot":"12345678",
                "domain":{"id":1},
                "keys_available":true,
                "run_condition":{"value":"RUNS AND DRIVES","label":"Run and Drive"},
                "airbags":"Intact",
                "restraint_system":"Dual Air Bag",
                "damage":{"primary":"Front End","secondary":"Left Side"},
                "seller":{"name":"State Farm","type":"Insurance","class":"insurance"}
              }]
            }]
            """);

        var mapped = AuctionsApiIncrementalSyncProcessor.MapRows(document.RootElement.EnumerateArray(), "iaai").Single();

        Assert.Equal("RUNS AND DRIVES", mapped.Condition?.RunCondition?.Value);
        Assert.Equal("Intact", mapped.VehicleSpecs?.Airbags);
        Assert.Equal("Dual Air Bag", mapped.VehicleSpecs?.RestraintSystem);
        Assert.True(mapped.Condition?.HasKey);
        Assert.Equal("State Farm", mapped.Seller?.Name);
        Assert.Equal("Insurance", mapped.Seller?.Type);
        Assert.Equal("Front End", mapped.Condition?.PrimaryDamage);
    }

    [Fact]
    public void MapRows_preserves_copart_condition_seller_and_auction_evidence()
    {
        using var document = JsonDocument.Parse("""
            [{
              "vin":"2TESTVIN123456789",
              "year":2022,
              "manufacturer":{"name":"Ford"},
              "model":{"name":"F-150"},
              "vehicle_type":{"name":"Pickup"},
              "lots":[{
                "lot":"48826366",
                "domain":{"id":3},
                "sale_date":"2026-09-10T14:00:00Z",
                "status":{"name":"UPCOMING","id":10},
                "keys_available":"YES",
                "run_condition":{"value":"RUNS AND DRIVES","label":"Run & Drive"},
                "airbags":"Intact",
                "damage":{"primary":"Front End","secondary":"Minor Dent"},
                "seller":{"name":"Example Insurance","type":"Insurance","class":"insurance"},
                "images":{"big":["https://cdn.example.com/copart.jpg"]}
              }]
            }]
            """);

        var mapped = AuctionsApiIncrementalSyncProcessor.MapRows(document.RootElement.EnumerateArray(), "copart").Single();

        Assert.Equal("copart", mapped.Platform);
        Assert.Equal("48826366", mapped.LotNumber);
        Assert.Equal("RUNS AND DRIVES", mapped.Condition?.RunCondition?.Value);
        Assert.Equal("Intact", mapped.VehicleSpecs?.Airbags);
        Assert.True(mapped.Condition?.HasKey);
        Assert.Equal("Example Insurance", mapped.Seller?.Name);
        Assert.Equal("Insurance", mapped.Seller?.Type);
        Assert.Equal("Front End", mapped.Condition?.PrimaryDamage);
        Assert.Equal("2026-09-10T14:00:00.0000000+00:00", mapped.Auction?.AuctionAt?.ToString("O"));
        Assert.Single(mapped.Media?.Photos ?? []);
    }

    [Fact]
    public void MapRows_falls_back_to_parent_copart_fields_for_seller_title_condition_and_status()
    {
        using var document = JsonDocument.Parse("""
            [{
              "vin":"6TESTVIN123456789",
              "year":2023,
              "manufacturer":{"name":"Honda"},
              "model":{"name":"CR-V"},
              "title_type_label":"Clean Title",
              "sale_date":"2026-09-12T14:00:00Z",
              "status":{"name":"UPCOMING"},
              "seller":{"name":"Example Fleet","type":"Fleet","class":"fleet"},
              "condition":{"id":2,"name":"run_and_drives"},
              "lots":[{"lot":"10000004","domain":{"id":3},"images":["https://cdn.example.com/copart-parent.jpg"]}]
            }]
            """);

        var mapped = AuctionsApiIncrementalSyncProcessor.MapRows(document.RootElement.EnumerateArray(), "copart").Single();

        Assert.Equal("Example Fleet", mapped.Seller?.Name);
        Assert.Equal("Fleet", mapped.Seller?.Type);
        Assert.Equal("Clean Title", mapped.SaleDocument?.Name);
        Assert.Equal("RUNS AND DRIVES", mapped.Condition?.RunCondition?.Value);
        Assert.Equal("UPCOMING", mapped.Auction?.LotStatus);
        Assert.NotNull(mapped.Auction?.AuctionAt);

        var eligibility = AuctionEligibilityEvaluator.Evaluate(mapped, DateTimeOffset.Parse("2026-09-05T12:00:00Z"));
        Assert.DoesNotContain(eligibility.Flags, flag => flag.Code is "M01" or "M02" or "M04" or "M07");
    }

    [Fact]
    public void MapRows_accepts_copart_engine_as_string_object_or_null()
    {
        using var document = JsonDocument.Parse("""
            [
              {"vin":"3TESTVIN123456789","year":2021,"manufacturer":{"name":"Ford"},"model":{"name":"Escape"},"lots":[{"lot":"10000001","domain":{"id":3},"vehicle_specs":{"engine":"2.0L Turbo"}}]},
              {"vin":"4TESTVIN123456789","year":2022,"manufacturer":{"name":"Toyota"},"model":{"name":"Camry"},"lots":[{"lot":"10000002","domain":{"id":3},"vehicle_specs":{"engine":{"size_l":"3.5","hp":"290","layout":"V6"}}}]},
              {"vin":"5TESTVIN123456789","year":2023,"manufacturer":{"name":"Honda"},"model":{"name":"Civic"},"lots":[{"lot":"10000003","domain":{"id":3},"vehicle_specs":{"engine":null}}]}
            ]
            """);

        var mapped = AuctionsApiIncrementalSyncProcessor.MapRows(document.RootElement.EnumerateArray(), "copart").ToArray();

        Assert.Equal(3, mapped.Length);
        Assert.Equal("2.0L Turbo", mapped[0].VehicleSpecs?.Engine?.Raw);
        Assert.Equal("3.5", mapped[1].VehicleSpecs?.Engine?.SizeLiters);
        Assert.Equal(290m, mapped[1].VehicleSpecs?.Engine?.Horsepower);
        Assert.Equal("V6", mapped[1].VehicleSpecs?.Engine?.Layout);
        Assert.Null(mapped[2].VehicleSpecs?.Engine);
    }

    [Fact]
    public void MapRows_extracts_and_deduplicates_lot_image_object_urls()
    {
        using var document = JsonDocument.Parse("""
            [{
              "vin":"1TESTVIN123456789",
              "year":2020,
              "manufacturer":{"name":"Toyota"},
              "model":{"name":"Camry"},
              "lots":[{
                "lot":"12345678",
                "domain":{"id":1},
                "images":{
                  "big":["https://cdn.example.com/a.jpg","https://cdn.example.com/b.jpg"],
                  "normal":"https://cdn.example.com/a.jpg",
                  "small":"not-a-url"
                }
              }]
            }]
            """);

        var mapped = AuctionsApiIncrementalSyncProcessor.MapRows(document.RootElement.EnumerateArray(), "iaai").Single();

        Assert.NotNull(mapped.Media);
        Assert.Equal(["https://cdn.example.com/a.jpg", "https://cdn.example.com/b.jpg"], mapped.Media!.Photos);
    }
}
