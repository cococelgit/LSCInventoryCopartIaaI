using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class AuctionsApiIncrementalShadowLimitTests
{
    [Fact]
    public void Requested_observation_cap_is_checked_before_shadow_comparison()
    {
        var source = File.ReadAllText(FindRepositoryFile("../Workers/AuctionsApiIncrementalSyncProcessor.cs"));
        var loopStart = source.IndexOf("foreach (var vehicle in activeVehicles)", StringComparison.Ordinal);
        var loopEnd = source.IndexOf("var archivedKeys", loopStart, StringComparison.Ordinal);
        Assert.True(loopStart >= 0 && loopEnd > loopStart);

        var loop = source[loopStart..loopEnd];
        var capCheck = loop.IndexOf("requestedMaximum is not null && changed >= requestedMaximum.Value", StringComparison.Ordinal);
        var shadowCall = loop.IndexOf("LogCanonicalShadowComparison(vehicle, normalizedPlatform)", StringComparison.Ordinal);

        Assert.True(capCheck >= 0);
        Assert.True(shadowCall >= 0);
        Assert.True(capCheck < shadowCall);
    }

    [Fact]
    public void Incremental_windows_are_streamed_page_by_page()
    {
        var source = File.ReadAllText(FindRepositoryFile("../Workers/AuctionsApiIncrementalSyncProcessor.cs"));
        Assert.Contains("ReadWindowPagesAsync", source, StringComparison.Ordinal);
        Assert.Contains("IAsyncEnumerable<WindowPage>", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadWindowAsync", source, StringComparison.Ordinal);
        Assert.Contains("var rows = ExtractRows(response.Data).ToArray();", source, StringComparison.Ordinal);
        Assert.Contains("yield return new WindowPage(rows);", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Lsc.Inventory.Api", "Storage", fileName);
            if (File.Exists(candidate)) return candidate;
            candidate = Path.Combine(directory.FullName, "src", "Lsc.Inventory.Api", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(fileName);
    }
}
