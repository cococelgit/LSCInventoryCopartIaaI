using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class InventoryV2BatchWriterTests
{
    [Fact]
    public void Inventory_v2_is_disabled_by_default()
    {
        var options = new InventoryV2Options();

        Assert.False(options.ShadowWriteEnabled);
        Assert.False(options.ReaderEnabled);
        Assert.Equal(1000, options.BatchSize);
    }

    [Fact]
    public void Mapping_is_deterministic_and_contains_no_payload_column()
    {
        var observedAt = DateTimeOffset.Parse("2026-09-11T12:00:00Z");
        var item = new InventoryV2BatchItem(Vehicle(), observedAt);

        var first = PostgresSnapshotStore.PrepareInventoryV2Lot(item);
        var second = PostgresSnapshotStore.PrepareInventoryV2Lot(item);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.Values["identity_hash"], second!.Values["identity_hash"]);
        Assert.Equal(first.Values["auction_hash"], second.Values["auction_hash"]);
        Assert.Equal("copart:64206406", first.Values["lot_key"]);
        Assert.Equal("25000", first.Values["buy_now_usd"]);
        Assert.Equal("2", first.Values["media_photos_count"]);
        Assert.Equal("OTHER", first.Values["title_type"]);
        Assert.Equal("true", first.Values["is_buy_now"]);
        Assert.DoesNotContain(first.Values.Keys, key => key.Contains("payload", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(first.Values.Keys, key => key.Contains("json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Bid_change_only_changes_auction_and_search_hashes()
    {
        var observedAt = DateTimeOffset.Parse("2026-09-11T12:00:00Z");
        var original = PostgresSnapshotStore.PrepareInventoryV2Lot(new InventoryV2BatchItem(Vehicle(), observedAt))!;
        var changed = PostgresSnapshotStore.PrepareInventoryV2Lot(new InventoryV2BatchItem(
            Vehicle() with { Pricing = Vehicle().Pricing! with { CurrentBidUsd = 12_500m } },
            observedAt.AddMinutes(1)))!;

        Assert.Equal(original.Values["identity_hash"], changed.Values["identity_hash"]);
        Assert.Equal(original.Values["spec_hash"], changed.Values["spec_hash"]);
        Assert.Equal(original.Values["condition_hash"], changed.Values["condition_hash"]);
        Assert.Equal(original.Values["seller_location_hash"], changed.Values["seller_location_hash"]);
        Assert.Equal(original.Values["media_hash"], changed.Values["media_hash"]);
        Assert.Equal(original.Values["score_input_hash"], changed.Values["score_input_hash"]);
        Assert.NotEqual(original.Values["auction_hash"], changed.Values["auction_hash"]);
        Assert.NotEqual(original.Values["search_hash"], changed.Values["search_hash"]);
    }

    [Fact]
    public void Buy_now_flag_requires_a_positive_price_and_seller_can_fall_back_to_raw_lot()
    {
        var raw = JsonDocument.Parse("""
            {"lots":[{"lot":"64206406","seller_name":"Raw Seller","buy_now":{"value":0}}]}
            """).RootElement.Clone();
        var baseVehicle = Vehicle();
        var vehicle = baseVehicle with
        {
            Seller = null,
            Details = null,
            Auction = baseVehicle.Auction! with { IsBuyNow = true },
            Pricing = baseVehicle.Pricing! with { BuyNowUsd = 0m },
            RawSource = raw,
        };

        var prepared = PostgresSnapshotStore.PrepareInventoryV2Lot(
            new InventoryV2BatchItem(vehicle, DateTimeOffset.Parse("2026-09-11T12:00:00Z")))!;

        Assert.Equal("Raw Seller", prepared.Values["seller_name"]);
        Assert.Equal("false", prepared.Values["is_buy_now"]);
    }

    [Fact]
    public void Source_uses_copy_merge_and_non_blocking_shadow_contract()
    {
        var store = File.ReadAllText(FindRepositoryFile("Storage/PostgresSnapshotStore.InventoryV2Batch.cs"));
        var processor = File.ReadAllText(FindRepositoryFile("Workers/AuctionsApiIncrementalSyncProcessor.cs"));

        Assert.Contains("BeginBinaryImportAsync", store, StringComparison.Ordinal);
        Assert.Contains("create temp table inventory_v2_stage", store, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("inventory_v2_actions", store, StringComparison.Ordinal);
        Assert.Contains("writer_enabled", store, StringComparison.Ordinal);
        Assert.DoesNotContain("jsonb", store, StringComparison.OrdinalIgnoreCase);

        var persist = processor.IndexOf("var saved = ingested.Persistence!", StringComparison.Ordinal);
        var collect = processor.IndexOf("inventoryV2Batch.Add", StringComparison.Ordinal);
        Assert.True(persist >= 0 && collect > persist);
        Assert.Contains("failure must never make the V1 inventory write fail", processor, StringComparison.Ordinal);

        var program = File.ReadAllText(FindRepositoryFile("Program.cs"));
        Assert.Contains("--inventory-v2-writer-state", program, StringComparison.Ordinal);
        Assert.Contains("--inventory-v2-reader-state", program, StringComparison.Ordinal);
        Assert.Contains("--auctionsapi-incremental-canary", program, StringComparison.Ordinal);
        Assert.Contains("--inventory-v2-parity", program, StringComparison.Ordinal);
        Assert.Contains("--inventory-v2-reset-shadow", program, StringComparison.Ordinal);
        Assert.Contains("--platform copart|iaai", program, StringComparison.Ordinal);

        var parity = File.ReadAllText(FindRepositoryFile("Storage/PostgresSnapshotStore.InventoryV2Parity.cs"));
        Assert.Contains("FieldMismatches", parity, StringComparison.Ordinal);
        Assert.Contains("seller_v2_only", parity, StringComparison.Ordinal);
        Assert.Contains("seller_v1_only", parity, StringComparison.Ordinal);
        Assert.Contains("seller_conflict", parity, StringComparison.Ordinal);
        Assert.Contains("nullif(lower(btrim(v1.seller_name)), 'unknown')", parity, StringComparison.Ordinal);
        Assert.Contains("like '%' || lower(btrim(v2.seller_name)) || '%'", parity, StringComparison.Ordinal);
        Assert.Contains("SellerMismatchSamples", parity, StringComparison.Ordinal);

        var workflow = File.ReadAllText(FindRepositoryRootFile(".github/workflows/run-inventory-v2-writer-canary.yml"));
        Assert.Contains("RUN_INVENTORY_V2_WRITER_CANARY", workflow, StringComparison.Ordinal);
        Assert.Contains("InventoryV2__ShadowWriteEnabled", workflow, StringComparison.Ordinal);
        Assert.Contains("--inventory-v2-writer-state", workflow, StringComparison.Ordinal);
        Assert.Contains("--auctionsapi-incremental-canary", workflow, StringComparison.Ordinal);
        Assert.Contains("trap cleanup EXIT", workflow, StringComparison.Ordinal);
        Assert.Contains("reset_platform", workflow, StringComparison.Ordinal);

        var rolloutWorkflow = File.ReadAllText(FindRepositoryRootFile(".github/workflows/configure-inventory-v2-dual-write.yml"));
        Assert.Contains("CONFIGURE_INVENTORY_V2_DUAL_WRITE", rolloutWorkflow, StringComparison.Ordinal);
        Assert.Contains("InventoryV2__ShadowWriteEnabled", rolloutWorkflow, StringComparison.Ordinal);
        Assert.Contains("--inventory-v2-writer-state", rolloutWorkflow, StringComparison.Ordinal);
        Assert.Contains("--inventory-v2-reader-state", rolloutWorkflow, StringComparison.Ordinal);
        Assert.Contains("rollback_needed=true", rolloutWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("scheduleTriggerConfig", rolloutWorkflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--tail 500", workflow, StringComparison.Ordinal);
        Assert.Contains("--tail 300", workflow, StringComparison.Ordinal);
        Assert.Contains("reader_enabled = false", File.ReadAllText(FindRepositoryFile("Storage/PostgresSnapshotStore.InventoryV2Schema.cs")), StringComparison.Ordinal);

        var parityWorkflow = File.ReadAllText(FindRepositoryRootFile(".github/workflows/audit-inventory-v2-parity.yml"));
        Assert.Contains("AUDIT_INVENTORY_V2_PARITY", parityWorkflow, StringComparison.Ordinal);
        Assert.Contains("--inventory-v2-parity", parityWorkflow, StringComparison.Ordinal);
        Assert.Contains("triggerType=\"Manual\"", parityWorkflow, StringComparison.Ordinal);
    }

    private static AuctionVehicle Vehicle() => new()
    {
        Platform = "copart",
        SourceProvider = "auctionsapi",
        LotNumber = "64206406",
        Vin = "1HGCM82633A004352",
        Year = 2022,
        Make = "Honda",
        Model = "Accord",
        VehicleType = "Automobile",
        VehicleSpecs = new VehicleSpecs
        {
            BodyStyle = "Sedan",
            FuelType = "Gasoline",
            Transmission = "Automatic",
            DriveType = "FWD",
            Engine = new VehicleEngine { SizeLiters = "2.0", Horsepower = 192, Raw = "2.0L 4" },
        },
        Condition = new VehicleCondition
        {
            PrimaryDamage = "Front End",
            HasKey = true,
            RunCondition = new RunConditionInfo { Value = "RUNS AND DRIVES", Label = "Runs and Drives" },
        },
        OdometerInfo = new OdometerInfo { Miles = 48_000, Status = "ACTUAL" },
        SaleDocument = new SaleDocument { Type = "Clean", Name = "Certificate of Title" },
        Seller = new AuctionSeller { Name = "Insurance Company", Type = "insurance", Class = "insurance" },
        Auction = new AuctionInfo { State = "FL", AuctionAt = DateTimeOffset.Parse("2026-09-12T14:00:00Z"), LotStatus = "Live", IsBuyNow = true },
        Pricing = new PricingInfo { CurrentBidUsd = 10_000m, BuyNowUsd = 25_000m },
        Location = new VehicleLocation { Display = "MIAMI SOUTH", State = "FL", FacilityId = "36" },
        Media = new MediaInfo { Photos = ["https://images.example.test/a.jpg", "https://images.example.test/b.jpg"] },
        RawSource = JsonDocument.Parse("""
            {
              "id": 991,
              "domain": { "id": 1 },
              "manufacturer": { "id": 7, "name": "Honda" },
              "model": { "id": 44, "name": "Accord" },
              "updated_at": "2026-09-11T11:59:00Z",
              "lots": [
                {
                  "lot": "64206406",
                  "external_id": "lot-64206406",
                  "status": { "id": 2, "name": "Live" },
                  "bid": { "value": 10000, "updated_at": "2026-09-11T11:58:00Z" },
                  "buy_now": { "value": 25000, "updated_at": "2026-09-11T11:57:00Z" },
                  "images": ["https://images.example.test/a.jpg", "https://images.example.test/b.jpg"]
                }
              ]
            }
            """).RootElement.Clone(),
    };

    private static string FindRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Lsc.Inventory.Api", relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(relativePath);
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
