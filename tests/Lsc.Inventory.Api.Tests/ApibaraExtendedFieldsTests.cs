using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class ApibaraExtendedFieldsTests
{
    [Fact]
    public void Deserializes_extended_iaai_vehicle_fields_without_losing_nested_data()
    {
        const string json = """
        {
          "platform": "iaai",
          "lot_number": "sample-lot",
          "auction": { "lot_status": "open", "lot_sub_status": "timed", "is_buy_now": true, "is_timed": true },
          "pricing": { "current_bid_usd": 900, "current_bid2_usd": 850, "estimated_cost": { "from": 1400, "to": 2200, "text": "$1,400 - $2,200" } },
          "location": { "display": "Sample Branch (FL)", "state": "FL", "send_from": "Sample Branch" },
          "seller": { "name": "Sample Seller", "type": "insurance", "class": "insurance", "text_class": "Insurance Company" },
          "media": { "has_360": true, "has_video": true, "thumbs": ["https://vis.iaai.com/sample.jpg"], "items": [{ "large": "https://vis.iaai.com/large.jpg", "thumb": "https://vis.iaai.com/thumb.jpg", "type": "image" }] },
          "vehicle_specs": { "body_style": "SUV", "airbags": "Intact", "restraint_system": "Dual Air Bag", "engine": { "size_l": "2.0", "hp": 240, "layout": "I", "raw": "2.0L I4" } },
          "condition": { "primary_damage": "Front End", "secondary_damage": "Left Side", "loss": "Collision", "has_key": true, "run_condition": { "value": "RUNS AND DRIVES", "label": "Run and Drive", "class_hint": "green" } },
          "odometer": { "mi": 12000, "km": 19312, "status": "Actual" },
          "sale_document": { "name": "CERTIFICATE OF TITLE", "type": "Clean", "sale_document_group": "Title", "is_pending": false, "export": true, "registration": true, "page_id": 8 },
          "details": {
            "sale_information": { "ActualCashValue": "$18,500", "EstimatedRepairCost": "$7,200", "Lane": "A", "Aisle": "12", "SellingBranch": "Sample Branch", "Seller": "Sample Seller", "SellerType": "insurance" },
            "vehicle_description": { "BodyStyle": "SUV", "Series": "Premium", "Cylinders": "4", "ManufacturedIn": "USA", "Options": "Navigation", "VehicleClass": "Class 1", "VehicleScore": "80", "VINStatus": "OK" },
            "vehicle_information": { "VINStatus": "OK", "TitleSaleDocBrand": "Clean", "TitleSaleDocNotes": "None" }
          }
        }
        """;

        var vehicle = JsonSerializer.Deserialize<AuctionVehicle>(json);

        Assert.NotNull(vehicle);
        Assert.True(vehicle.Auction?.IsBuyNow);
        Assert.Equal(850m, vehicle.Pricing?.PreBidUsd);
        Assert.Equal(2200m, vehicle.Pricing?.EstimatedCost?.ToUsd);
        Assert.Equal("Collision", vehicle.Condition?.Loss);
        Assert.Equal(19312m, vehicle.OdometerInfo?.Kilometers);
        Assert.Equal("2.0", vehicle.VehicleSpecs?.Engine?.SizeLiters);
        Assert.Equal(240m, vehicle.VehicleSpecs?.Engine?.Horsepower);
        Assert.Equal("Title", vehicle.SaleDocument?.Group);
        Assert.True(vehicle.Media?.HasVideo);
        Assert.Equal("https://vis.iaai.com/large.jpg", vehicle.Media?.Items?[0].Large);
        Assert.Equal("$18,500", vehicle.Details?.SaleInformation?.ActualCashValue);
        Assert.Equal("Premium", vehicle.Details?.VehicleDescription?.Series);
        Assert.Equal("Clean", vehicle.Details?.VehicleInformation?.TitleBrand);
    }
}
