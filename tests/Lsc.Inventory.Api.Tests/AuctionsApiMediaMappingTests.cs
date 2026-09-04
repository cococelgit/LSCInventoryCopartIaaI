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
