using System.Net;
using ERP_RFQ_Automation.Platform.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class PlatformNetworkAccessMiddlewareTests
{
    [Theory]
    [InlineData("10.24.7.9", true)]
    [InlineData("10.25.0.1", false)]
    [InlineData("2001:db8:42::19", true)]
    [InlineData("2001:db8:43::1", false)]
    public async Task Allow_list_enforces_ipv4_and_ipv6_cidrs(string address, bool allowed)
    {
        var nextCalled = false;
        var middleware = Create(new Dictionary<string, string?>
        {
            ["PlatformAccess:NetworkMode"] = "AllowList",
            ["PlatformAccess:AllowedCidrs:0"] = "10.24.0.0/16",
            ["PlatformAccess:AllowedCidrs:1"] = "2001:db8:42::/48"
        }, _ => { nextCalled = true; return Task.CompletedTask; });
        var context = Context("/api/platform/tenants", address);

        await middleware.InvokeAsync(context);

        Assert.Equal(allowed, nextCalled);
        Assert.Equal(allowed ? 200 : 403, context.Response.StatusCode);
    }

    [Fact]
    public async Task Raw_forwarded_header_is_never_a_trust_source()
    {
        var nextCalled = false;
        var middleware = Create(new Dictionary<string, string?>
        {
            ["PlatformAccess:NetworkMode"] = "AllowList",
            ["PlatformAccess:AllowedCidrs:0"] = "10.24.0.0/16"
        }, _ => { nextCalled = true; return Task.CompletedTask; });
        var context = Context("/api/platform/auth/login", "203.0.113.9");
        context.Request.Headers["X-Forwarded-For"] = "10.24.7.9";

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(403, context.Response.StatusCode);
    }

    [Fact]
    public async Task Tenant_routes_are_not_affected_by_platform_network_policy()
    {
        var nextCalled = false;
        var middleware = Create(new Dictionary<string, string?>
        {
            ["PlatformAccess:NetworkMode"] = "AllowList",
            ["PlatformAccess:AllowedCidrs:0"] = "10.24.0.0/16"
        }, _ => { nextCalled = true; return Task.CompletedTask; });

        await middleware.InvokeAsync(Context("/api/leads", "203.0.113.9"));

        Assert.True(nextCalled);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("AllowList", null)]
    [InlineData("AllowList", "10.0.0.0/99")]
    [InlineData("unexpected", "10.0.0.0/8")]
    public void Production_refuses_implicit_empty_or_malformed_policy(string? mode, string? cidr)
    {
        var values = new Dictionary<string, string?>();
        if (mode is not null) values["PlatformAccess:NetworkMode"] = mode;
        if (cidr is not null) values["PlatformAccess:AllowedCidrs:0"] = cidr;

        Assert.Throws<InvalidOperationException>(() => Create(values, _ => Task.CompletedTask, "Production"));
    }

    private static PlatformNetworkAccessMiddleware Create(
        Dictionary<string, string?> values, RequestDelegate next, string environment = "Testing") =>
        new(next, new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new TestEnvironment(environment), NullLogger<PlatformNetworkAccessMiddleware>.Instance);

    private static DefaultHttpContext Context(string path, string address)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Connection.RemoteIpAddress = IPAddress.Parse(address);
        context.Response.Body = new MemoryStream();
        return context;
    }

    private sealed class TestEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = environmentName;
        public string ContentRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
