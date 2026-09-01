using Lsc.Inventory.Api.Workers;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class IaaIStartupModeResolverTests
{
    [Fact]
    public void Selects_national_mode_when_only_national_startup_is_enabled()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["IaaIPilot:RunOnStartup"] = "false",
            ["IaaINational:RunOnStartup"] = "true"
        });

        Assert.Equal(IaaIStartupMode.National, IaaIStartupModeResolver.Resolve([], configuration));
    }

    [Fact]
    public void Rejects_ambiguous_pilot_and_national_startup_configuration()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["IaaIPilot:RunOnStartup"] = "true",
            ["IaaINational:RunOnStartup"] = "true"
        });

        var exception = Assert.Throws<InvalidOperationException>(() => IaaIStartupModeResolver.Resolve([], configuration));

        Assert.Contains("cannot be enabled together", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Keeps_all_iaai_workers_disabled_without_an_explicit_mode()
    {
        Assert.Equal(IaaIStartupMode.None, IaaIStartupModeResolver.Resolve([], Configuration([])));
    }

    private static IConfiguration Configuration(IEnumerable<KeyValuePair<string, string?>> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
