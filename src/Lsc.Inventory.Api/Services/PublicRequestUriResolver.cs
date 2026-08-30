using Microsoft.AspNetCore.Http;

namespace Lsc.Inventory.Api.Services;

public static class PublicRequestUriResolver
{
    public static Uri Resolve(HttpRequest request)
    {
        var host = request.Host.Host;
        var scheme = host.EndsWith(".azurecontainerapps.io", StringComparison.OrdinalIgnoreCase)
            ? Uri.UriSchemeHttps
            : request.Scheme;

        if (!Uri.TryCreate($"{scheme}://{request.Host}", UriKind.Absolute, out var uri))
            throw new InvalidOperationException("Unable to construct a public request URI.");

        return uri;
    }
}
