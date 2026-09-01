using Lsc.Inventory.Api.Services;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class PublicRequestUriResolverTests
{
    [Fact]
    public void Uses_https_for_the_known_azure_container_apps_public_host()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("ca-lsc-inventory-api-prod.example.eastus2.azurecontainerapps.io");

        var uri = PublicRequestUriResolver.Resolve(context.Request);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal(context.Request.Host.Host, uri.Host);
    }

    [Fact]
    public void Preserves_the_local_request_scheme_for_non_azure_hosts()
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new HostString("localhost", 5080);

        var uri = PublicRequestUriResolver.Resolve(context.Request);

        Assert.Equal("http", uri.Scheme);
        Assert.Equal(5080, uri.Port);
    }
}
