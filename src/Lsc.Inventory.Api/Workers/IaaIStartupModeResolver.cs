using Microsoft.Extensions.Configuration;

namespace Lsc.Inventory.Api.Workers;

public enum IaaIStartupMode
{
    None,
    Pilot,
    National
}

public static class IaaIStartupModeResolver
{
    public static IaaIStartupMode Resolve(string[] args, IConfiguration configuration)
    {
        var pilot = args.Contains("--iaai-pilot", StringComparer.OrdinalIgnoreCase)
            || configuration.GetValue<bool>("IaaIPilot:RunOnStartup");
        var national = args.Contains("--iaai-national", StringComparer.OrdinalIgnoreCase)
            || configuration.GetValue<bool>("IaaINational:RunOnStartup");

        if (pilot && national)
            throw new InvalidOperationException("IAAI pilot and national startup modes cannot be enabled together.");

        if (national) return IaaIStartupMode.National;
        return pilot ? IaaIStartupMode.Pilot : IaaIStartupMode.None;
    }
}
