using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartExcelSnapshotAdapterTests
{
    [Fact]
    public async Task Valid_snapshot_streams_canonical_copart_vehicle()
    {
        var csv = BuildCsv(3);
        var snapshot = CreateSnapshot(csv);
        var adapter = CreateAdapter();

        var validation = await adapter.ValidateAsync(snapshot, CancellationToken.None);
        var vehicles = new List<Lsc.Inventory.Api.Contracts.AuctionVehicle>();
        await foreach (var vehicle in adapter.ReadAcceptedSnapshotAsync(snapshot, CancellationToken.None)) vehicles.Add(vehicle);

        Assert.True(validation.IsComplete);
        Assert.Equal(3, validation.RowCount);
        Assert.Equal(3, vehicles.Count);
        Assert.All(vehicles, vehicle => Assert.Equal("copart", vehicle.Platform));
        Assert.Equal("Accord LX", vehicles[0].Model);
        Assert.Equal("FL", vehicles[0].Location!.State);
        Assert.Equal("Good Seller", vehicles[0].Seller!.Name);
        Assert.NotNull(vehicles[0].RawSource);
    }

    [Fact]
    public async Task Maps_official_title_code_to_descriptions_without_changing_eligibility()
    {
        var csv = BuildCsv(1).Replace(",Salvage,none", ",AQ,none", StringComparison.Ordinal);
        var snapshot = CreateSnapshot(csv);
        var adapter = CreateAdapter();
        var vehicles = new List<Lsc.Inventory.Api.Contracts.AuctionVehicle>();

        await foreach (var vehicle in adapter.ReadAcceptedSnapshotAsync(snapshot, CancellationToken.None)) vehicles.Add(vehicle);

        var mappedVehicle = Assert.Single(vehicles);
        Assert.Equal("2025 Honda Accord LX", mappedVehicle.Title);
        Assert.Equal("Clear Title", mappedVehicle.SaleDocument!.Name);
        Assert.Equal("AQ", mappedVehicle.AdditionalData!["source_title_type_code"].GetString());
        Assert.Equal("mapped", mappedVehicle.AdditionalData["source_title_mapping"].GetString());
        Assert.False(mappedVehicle.AdditionalData.ContainsKey("title_category"));
        Assert.True(AuctionEligibilityEvaluator.Evaluate(mappedVehicle, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)).LoadToSystem);
    }

    [Fact]
    public async Task Preserves_unknown_title_code_as_visible_unmapped_mark_without_discarding()
    {
        var csv = BuildCsv(1).Replace(",Salvage,none", ",M02,none", StringComparison.Ordinal);
        var snapshot = CreateSnapshot(csv);
        var adapter = CreateAdapter();
        var vehicles = new List<Lsc.Inventory.Api.Contracts.AuctionVehicle>();

        await foreach (var vehicle in adapter.ReadAcceptedSnapshotAsync(snapshot, CancellationToken.None)) vehicles.Add(vehicle);

        var unmappedVehicle = Assert.Single(vehicles);
        var evaluation = AuctionEligibilityEvaluator.Evaluate(unmappedVehicle, new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("2025 Honda Accord LX", unmappedVehicle.Title);
        Assert.Equal("unmapped", unmappedVehicle.AdditionalData!["source_title_mapping"].GetString());
        Assert.False(unmappedVehicle.AdditionalData.ContainsKey("title_category"));
        Assert.True(evaluation.LoadToSystem);
        Assert.Contains(evaluation.Flags, flag => flag.Code == "M02");
    }

    [Fact]
    public async Task Missing_required_column_is_rejected_without_streaming_rows()
    {
        const string csv = "Lot number,VIN\n12345678,1HGCM82633A004352\n";
        var snapshot = CreateSnapshot(csv);
        var adapter = CreateAdapter();

        var validation = await adapter.ValidateAsync(snapshot, CancellationToken.None);

        Assert.False(validation.IsComplete);
        Assert.Contains(validation.Failures, failure => failure.StartsWith("F03:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Snapshot_below_completeness_floor_is_rejected()
    {
        var bytes = Encoding.UTF8.GetBytes(BuildCsv(2));
        var snapshot = new CopartSnapshotEnvelope("salesdata.csv", Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), DateTimeOffset.UtcNow, new MemoryStream(bytes));
        var adapter = new CopartExcelSnapshotAdapter(new OptionsWrapper<CopartExcelOptions>(new CopartExcelOptions
        {
            MinimumFileSizeKilobytes = 0,
            MaximumFileSizeMegabytes = 8,
            MinimumRowsForCompleteSnapshot = 3
        }));

        var validation = await adapter.ValidateAsync(snapshot, CancellationToken.None);

        Assert.False(validation.IsComplete);
        Assert.Contains(validation.Failures, failure => failure.StartsWith("F05:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Large_snapshot_streams_all_rows_without_buffering_the_file()
    {
        var snapshot = CreateSnapshot(BuildCsv(10_000));
        var adapter = CreateAdapter();
        var count = 0;

        await foreach (var _ in adapter.ReadAcceptedSnapshotAsync(snapshot, CancellationToken.None)) count++;

        Assert.Equal(10_000, count);
    }

    [Fact]
    public async Task Missing_runs_drives_column_is_accepted_as_unverified_and_raw_is_absent()
    {
        var csv = BuildCsv(1)
            .Replace("Runs/Drives,", string.Empty, StringComparison.Ordinal)
            .Replace(",Runs and Drives,", ",", StringComparison.Ordinal);
        var adapter = CreateAdapter();
        var snapshot = CreateSnapshot(csv);

        var validation = await adapter.ValidateAsync(snapshot, CancellationToken.None);
        var vehicles = new List<Lsc.Inventory.Api.Contracts.AuctionVehicle>();
        await foreach (var vehicle in adapter.ReadAcceptedSnapshotAsync(snapshot, CancellationToken.None)) vehicles.Add(vehicle);

        var condition = Assert.Single(vehicles).Condition!.RunCondition!;
        Assert.True(validation.IsComplete);
        Assert.Equal("UNVERIFIED", condition.Normalized);
        Assert.Null(condition.Raw);
    }

    [Fact]
    public async Task Empty_runs_drives_value_is_unverified_and_raw_is_absent()
    {
        var adapter = CreateAdapter();
        var snapshot = CreateSnapshot(BuildCsv(1, string.Empty));
        var vehicles = new List<Lsc.Inventory.Api.Contracts.AuctionVehicle>();

        await foreach (var vehicle in adapter.ReadAcceptedSnapshotAsync(snapshot, CancellationToken.None)) vehicles.Add(vehicle);

        var condition = Assert.Single(vehicles).Condition!.RunCondition!;
        Assert.Equal("UNVERIFIED", condition.Normalized);
        Assert.Null(condition.Raw);
    }

    [Theory]
    [InlineData("RUN & DRIVE", "RUNS_AND_DRIVES")]
    [InlineData("Runs and Drives", "RUNS_AND_DRIVES")]
    [InlineData("rUn & dRiVe", "RUNS_AND_DRIVES")]
    [InlineData("STARTS", "STARTS")]
    [InlineData("engine start program", "STARTS")]
    [InlineData("STATIONARY", "STATIONARY")]
    [InlineData("No Information", "UNVERIFIED")]
    [InlineData("unknown", "UNVERIFIED")]
    [InlineData("Inspected only", "UNVERIFIED")]
    public async Task Runs_drives_maps_only_explicit_values_and_preserves_raw_text(string rawValue, string expectedNormalized)
    {
        var adapter = CreateAdapter();
        var snapshot = CreateSnapshot(BuildCsv(1, rawValue));
        var vehicles = new List<Lsc.Inventory.Api.Contracts.AuctionVehicle>();

        await foreach (var vehicle in adapter.ReadAcceptedSnapshotAsync(snapshot, CancellationToken.None)) vehicles.Add(vehicle);

        var condition = Assert.Single(vehicles).Condition!.RunCondition!;
        Assert.Equal(expectedNormalized, condition.Normalized);
        Assert.Equal(rawValue, condition.Raw);
        Assert.Equal("FRONT WHEEL DRIVE", Assert.Single(vehicles).DriveType);
    }

    [Fact]
    public async Task Run_condition_payload_uses_explicit_raw_and_normalized_field_names()
    {
        var adapter = CreateAdapter();
        var snapshot = CreateSnapshot(BuildCsv(1, "Run & Drive"));
        var vehicles = new List<Lsc.Inventory.Api.Contracts.AuctionVehicle>();

        await foreach (var vehicle in adapter.ReadAcceptedSnapshotAsync(snapshot, CancellationToken.None)) vehicles.Add(vehicle);

        var payload = JsonSerializer.Serialize(Assert.Single(vehicles).Condition!.RunCondition);
        using var document = JsonDocument.Parse(payload);
        Assert.Equal("RUNS_AND_DRIVES", document.RootElement.GetProperty("run_condition").GetString());
        Assert.Equal("Run & Drive", document.RootElement.GetProperty("run_condition_raw").GetString());
        Assert.False(document.RootElement.TryGetProperty("value", out _));
        Assert.False(document.RootElement.TryGetProperty("label", out _));
    }

    [Fact]
    public void Legacy_run_condition_payload_remains_readable_without_reemitting_legacy_fields()
    {
        var condition = JsonSerializer.Deserialize<Lsc.Inventory.Api.Contracts.RunConditionInfo>("{\"value\":\"RUNS AND DRIVES\",\"label\":\"Runs and Drives\"}");

        Assert.NotNull(condition);
        Assert.Equal("RUNS AND DRIVES", condition.Normalized);
        Assert.Equal("Runs and Drives", condition.Raw);
        var reserialized = JsonSerializer.Serialize(condition);
        Assert.False(reserialized.Contains("\"value\"", StringComparison.Ordinal));
        Assert.False(reserialized.Contains("\"label\"", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("2500.50", true)]
    [InlineData("0", false)]
    [InlineData("-25", false)]
    [InlineData("", false)]
    [InlineData("not-a-price", false)]
    public async Task Buy_now_is_present_only_for_strictly_positive_copart_values(string buyNow, bool expectedAvailable)
    {
        var adapter = CreateAdapter();
        var vehicles = new List<Lsc.Inventory.Api.Contracts.AuctionVehicle>();

        await foreach (var row in adapter.ReadAcceptedSnapshotAsync(CreateSnapshot(BuildCsv(1, buyNow: buyNow)), CancellationToken.None))
            vehicles.Add(row);

        var vehicle = Assert.Single(vehicles);
        Assert.Equal(expectedAvailable, vehicle.Pricing!.BuyNowUsd is > 0m);
        if (expectedAvailable) Assert.Equal(2500.50m, vehicle.Pricing.BuyNowUsd);
        else Assert.Null(vehicle.Pricing.BuyNowUsd);
    }

    [Fact]
    public async Task Zero_current_bid_remains_distinct_from_absent_buy_now()
    {
        var adapter = CreateAdapter();
        var vehicles = new List<Lsc.Inventory.Api.Contracts.AuctionVehicle>();

        await foreach (var row in adapter.ReadAcceptedSnapshotAsync(CreateSnapshot(BuildCsv(1, currentBid: "0", buyNow: "0")), CancellationToken.None))
            vehicles.Add(row);

        var vehicle = Assert.Single(vehicles);
        Assert.Equal(0m, vehicle.Pricing!.CurrentBidUsd);
        Assert.Null(vehicle.Pricing.BuyNowUsd);
    }

    [Theory]
    [InlineData("2026-08-25T03:46:59Z", "2026-08-25T03:46:59.0000000+00:00")]
    [InlineData("N", null)]
    [InlineData("", null)]
    public async Task Last_updated_time_maps_to_utc_or_safe_null(string rawValue, string? expected)
    {
        var adapter = CreateAdapter();
        var vehicles = new List<Lsc.Inventory.Api.Contracts.AuctionVehicle>();

        await foreach (var row in adapter.ReadAcceptedSnapshotAsync(CreateSnapshot(BuildCsv(1, lastUpdatedTime: rawValue)), CancellationToken.None))
            vehicles.Add(row);

        var vehicle = Assert.Single(vehicles);
        Assert.Equal(expected, CopartLotWatermarkPolicy.GetSourceUpdatedAt(vehicle)?.ToString("O"));
        Assert.Equal(rawValue, vehicle.RawSource!.Value.GetProperty("Last Updated Time").GetString());
    }

    [Fact]
    public async Task Missing_last_updated_time_column_remains_backward_compatible()
    {
        var adapter = CreateAdapter();
        var vehicle = Assert.Single(await ReadAllAsync(adapter, CreateSnapshot(BuildCsv(1))));

        Assert.Null(CopartLotWatermarkPolicy.GetSourceUpdatedAt(vehicle));
        Assert.False(vehicle.RawSource!.Value.TryGetProperty("Last Updated Time", out _));
    }

    [Fact]
    public async Task Hash_mismatch_is_rejected()
    {
        var snapshot = new CopartSnapshotEnvelope("salesdata.csv", new string('0', 64), DateTimeOffset.UtcNow, new MemoryStream(Encoding.UTF8.GetBytes(BuildCsv(1))));
        var adapter = CreateAdapter();

        var validation = await adapter.ValidateAsync(snapshot, CancellationToken.None);

        Assert.False(validation.IsComplete);
        Assert.Contains(validation.Failures, failure => failure.StartsWith("F01: Snapshot SHA-256", StringComparison.Ordinal));
    }

    private static CopartExcelSnapshotAdapter CreateAdapter() =>
        new(new OptionsWrapper<CopartExcelOptions>(new CopartExcelOptions
        {
            MinimumFileSizeKilobytes = 0,
            MaximumFileSizeMegabytes = 8,
            MinimumRowsForCompleteSnapshot = 1,
            ProcessingBatchSize = 2
        }));

    private static async Task<List<Lsc.Inventory.Api.Contracts.AuctionVehicle>> ReadAllAsync(CopartExcelSnapshotAdapter adapter, CopartSnapshotEnvelope snapshot)
    {
        var vehicles = new List<Lsc.Inventory.Api.Contracts.AuctionVehicle>();
        await foreach (var vehicle in adapter.ReadAcceptedSnapshotAsync(snapshot, CancellationToken.None)) vehicles.Add(vehicle);
        return vehicles;
    }

    private static CopartSnapshotEnvelope CreateSnapshot(string csv)
    {
        var bytes = Encoding.UTF8.GetBytes(csv);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new CopartSnapshotEnvelope("salesdata.csv", hash, DateTimeOffset.UtcNow, new MemoryStream(bytes));
    }

    private static string BuildCsv(int rows, string runDrives = "Runs and Drives", string currentBid = "5000", string buyNow = "0", string? lastUpdatedTime = null)
    {
        var includeLastUpdated = lastUpdatedTime is not null;
        var header = "Lot number,VIN,Year,Make,Model Group,Model Detail,Vehicle Type,Sale Date M/D/CY,Sale time (HHMM),Time Zone,Damage Description,Secondary Damage,Sale Title Type,Special Note,Announcements,Location state,Location city,Location ZIP,Yard number,Yard name,Seller Name,Has Keys-Yes or No,Drive,Runs/Drives,Odometer,Odometer Brand,Sale Status,\"High Bid =non-vix,Sealed=Vix\",Buy-It-Now Price,Image Thumbnail" + (includeLastUpdated ? ",Last Updated Time" : string.Empty) + "\n";
        var builder = new StringBuilder(header);
        for (var index = 0; index < rows; index++)
        {
            builder.Append($"{12345678 + index},1HGCM82633A004352,2025,Honda,Accord,Accord LX,Automobile,12/31/2099,1300,EST,Normal Wear,Minor Dent,Salvage,none,none,FL,Miami,33101,100,Miami Yard,Good Seller,Yes,FRONT WHEEL DRIVE,{runDrives},10000,Actual,Open,{currentBid},{buyNow},https://cs.copart.com/v1/AUTH_svc.pdoc00001/lpp/123.jpg{(includeLastUpdated ? $",{lastUpdatedTime}" : string.Empty)}\n");
        }
        return builder.ToString();
    }
}
