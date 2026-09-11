using Lsc.Inventory.Api.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class InventoryV2ReaderContractTests
{
    [Fact]
    public void Reader_is_disabled_by_default_and_schema_contains_required_public_fields()
    {
        Assert.False(new InventoryV2Options().ReaderEnabled);

        var schema = File.ReadAllText(FindRepositoryRootFile("infra/sql/20260911_inventory_v2_schema.sql"));
        var requiredColumns = new[]
        {
            "platform", "lot_number", "vin", "year", "make", "model", "generation",
            "vehicle_type", "body_style", "fuel_type", "engine", "engine_size_liters",
            "horsepower", "cylinders", "transmission", "drive_type", "exterior_color",
            "primary_damage", "secondary_damage", "loss_type", "run_condition_value",
            "run_condition_label", "has_key", "airbags", "odometer_miles", "odometer_km",
            "odometer_status", "title_type", "detailed_title", "title_group", "title_pending",
            "title_export", "title_registration", "title_brand", "title_notes", "seller_name",
            "seller_type", "auction_state", "lot_status", "lot_sub_status", "auction_at",
            "is_buy_now", "is_timed", "current_bid_usd", "pre_bid_usd", "buy_now_usd",
            "provider_estimate_from_usd", "provider_estimate_to_usd", "actual_cash_value_usd",
            "estimated_repair_cost_usd", "location_display", "location_state", "facility_id",
            "send_from", "lane", "aisle", "media_has_360", "media_has_video", "is_active",
            "first_seen_at", "last_seen_at", "source_updated_at",
        };

        foreach (var column in requiredColumns)
            Assert.Contains(column, schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("jsonb", schema, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Automatic_dual_write_workflow_does_not_enable_reader_or_change_schedules()
    {
        var workflow = File.ReadAllText(FindRepositoryRootFile(".github/workflows/configure-inventory-v2-dual-write.yml"));

        Assert.DoesNotContain("{name:\"InventoryV2__ReaderEnabled\",value:\"true\"}", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("{name:\"InventoryV2__ReaderEnabled\",value:$enabled}", workflow, StringComparison.Ordinal);
        Assert.Contains("--inventory-v2-reader-state", workflow, StringComparison.Ordinal);
        Assert.Contains("--enabled\",\"false", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("triggerType=\"Schedule\"", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("scheduleTriggerConfig", workflow, StringComparison.Ordinal);
    }

    private static string FindRepositoryRootFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relativePath);
    }
}
