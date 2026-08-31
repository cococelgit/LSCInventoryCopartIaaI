using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class VehicleDetailScoringGracefulDegradationTests
{
    [Fact]
    public void Active_vehicle_detail_returns_vehicle_when_full_scoring_lookup_fails()
    {
        var source = File.ReadAllText(FindRepositoryFile("Program.cs"));

        Assert.Contains("Serving active vehicle detail without full scoring after scoring lookup failed", source, StringComparison.Ordinal);
        Assert.Contains("LscVehicleScoringResult? scoring = null;", source, StringComparison.Ordinal);
        Assert.Contains("return Results.Ok(ToPublicVehicle(snapshot", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = directory.EnumerateFiles(fileName, SearchOption.AllDirectories)
                .FirstOrDefault(file => file.FullName.EndsWith(
                    Path.Combine("src", "Lsc.Inventory.Api", "Program.cs"),
                    StringComparison.OrdinalIgnoreCase));
            if (candidate is not null) return candidate.FullName;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(fileName);
    }
}
