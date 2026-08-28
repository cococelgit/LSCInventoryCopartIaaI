using System.Security.Cryptography;
using System.Text;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Sources;
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
        Assert.Equal("Clear Title", mappedVehicle.Title);
        Assert.Equal("Clear Title", mappedVehicle.SaleDocument!.Name);
        Assert.Equal("AQ", mappedVehicle.AdditionalData!["source_title_type_code"].GetString());
        Assert.Equal("mapped", mappedVehicle.AdditionalData["source_title_mapping"].GetString());
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
        Assert.Equal("M02", unmappedVehicle.Title);
        Assert.Equal("unmapped", unmappedVehicle.AdditionalData!["source_title_mapping"].GetString());
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

    private static CopartSnapshotEnvelope CreateSnapshot(string csv)
    {
        var bytes = Encoding.UTF8.GetBytes(csv);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        return new CopartSnapshotEnvelope("salesdata.csv", hash, DateTimeOffset.UtcNow, new MemoryStream(bytes));
    }

    private static string BuildCsv(int rows)
    {
        const string header = "Lot number,VIN,Year,Make,Model Group,Model Detail,Vehicle Type,Sale Date M/D/CY,Sale time (HHMM),Time Zone,Damage Description,Secondary Damage,Sale Title Type,Special Note,Announcements,Location state,Location city,Location ZIP,Yard number,Yard name,Seller Name,Has Keys-Yes or No,Runs/Drives,Odometer,Odometer Brand,Sale Status,\"High Bid =non-vix,Sealed=Vix\",Buy-It-Now Price,Image Thumbnail\n";
        var builder = new StringBuilder(header);
        for (var index = 0; index < rows; index++)
        {
            builder.Append($"{12345678 + index},1HGCM82633A004352,2025,Honda,Accord,Accord LX,Automobile,12/31/2099,1300,EST,Normal Wear,Minor Dent,Salvage,none,none,FL,Miami,33101,100,Miami Yard,Good Seller,Yes,Runs and Drives,10000,Actual,Open,5000,0,https://cs.copart.com/v1/AUTH_svc.pdoc00001/lpp/123.jpg\n");
        }
        return builder.ToString();
    }
}
