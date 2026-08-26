using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Contracts;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class InventorySourcePolicyTests
{
    [Theory]
    [InlineData("iaai")]
    [InlineData(" IAAI ")]
    public void Allows_only_iaai_through_apibara(string platform) =>
        Assert.Equal("iaai", InventorySourcePolicy.RequireApibaraPlatform(platform));

    [Theory]
    [InlineData("copart")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_copart_and_unknown_sources_from_apibara(string? platform) =>
        Assert.Throws<InvalidOperationException>(() => InventorySourcePolicy.RequireApibaraPlatform(platform));

    [Fact]
    public async Task Copart_excel_adapter_contract_yields_copart_vehicles_without_apibara()
    {
        ICopartExcelSnapshotAdapter adapter = new ContractProbeAdapter();
        var snapshot = new CopartSnapshotEnvelope("copart.xlsx", "abc123", DateTimeOffset.Parse("2026-08-26T00:00:00Z"), new MemoryStream());
        var vehicles = new List<AuctionVehicle>();
        await foreach (var vehicle in adapter.ReadAcceptedSnapshotAsync(snapshot, CancellationToken.None)) vehicles.Add(vehicle);
        Assert.Single(vehicles);
        Assert.Equal("copart", vehicles[0].Platform);
    }

    private sealed class ContractProbeAdapter : ICopartExcelSnapshotAdapter
    {
        public async IAsyncEnumerable<AuctionVehicle> ReadAcceptedSnapshotAsync(CopartSnapshotEnvelope snapshot, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new AuctionVehicle { Platform = InventorySourcePolicy.CopartExcelSource, LotNumber = "12345678" };
        }
    }
}
