using System.Text.Json;
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
