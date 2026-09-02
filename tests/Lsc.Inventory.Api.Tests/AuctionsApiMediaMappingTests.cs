using System.Text.Json;
using Lsc.Inventory.Api.Workers;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionsApiMediaMappingTests
{
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
