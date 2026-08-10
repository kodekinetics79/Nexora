using System.Net.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Pins the framework behaviour that <c>HttpsRedirectionMiddleware</c> is built on top of, using
/// the EXACT <see cref="ForwardedHeadersOptions"/> Program.cs configures.
///
/// <para>This suite exists because a reasonable-sounding reading of that configuration is wrong,
/// and the wrong reading produces a control that silently never fires. <c>KnownProxies</c> and
/// <c>KnownNetworks</c> are both cleared and no environment repopulates them — which does NOT
/// mean "trust no hop". <c>ForwardedHeadersMiddleware</c> performs its known-address check only
/// when at least one entry exists (<c>KnownProxies.Count + KnownNetworks.Count &gt; 0</c>), so an
/// empty pair means the opposite: trust EVERY caller. That is what the SEC-H6 comment in
/// Program.cs warns about, and it is asserted here rather than believed.</para>
///
/// <para>The consequence that matters downstream: the forwarded headers are APPLIED and then
/// CONSUMED, so any middleware after <c>UseForwardedHeaders</c> that tries to read
/// <c>X-Forwarded-Proto</c> for itself reads nothing at all.</para>
/// </summary>
public sealed class ForwardedHeadersBehaviourTests
{
    [Fact]
    public async Task The_forwarded_scheme_is_applied_even_with_no_known_proxy_configured()
    {
        var observed = await ObserveAsync(("X-Forwarded-Proto", "https"));

        Assert.Equal("https", observed.Scheme);
        Assert.True(observed.IsHttps);
    }

    [Fact]
    public async Task The_forwarded_proto_header_is_consumed_and_does_not_reach_later_middleware()
    {
        // THE defect this suite was written to find. A downstream reader of the raw header sees
        // nothing, so a redirect decision keyed on it can never fire in a real deployment.
        var observed = await ObserveAsync(("X-Forwarded-Proto", "http"));

        Assert.False(observed.HasForwardedProto);
        Assert.Equal("http", observed.Scheme);
    }

    [Fact]
    public async Task Consuming_it_leaves_X_Original_Proto_behind_as_the_evidence_it_happened()
    {
        // This is the signal that survives, and it is exact rather than heuristic: it is present
        // if and only if ForwardedHeadersMiddleware actually rewrote the scheme from a forwarded
        // header — i.e. if and only if an edge is in front AND speaks the header.
        var observed = await ObserveAsync(("X-Forwarded-Proto", "http"));

        Assert.True(observed.HasOriginalProto);
    }

    [Fact]
    public async Task A_request_with_no_forwarded_header_leaves_no_evidence_either()
    {
        // The unknowable case: no edge, or an edge that does not label the scheme. Nothing is
        // rewritten and nothing is left behind, which is exactly what must NOT be redirected.
        var observed = await ObserveAsync();

        Assert.False(observed.HasOriginalProto);
        Assert.False(observed.HasForwardedProto);
        Assert.Equal("http", observed.Scheme);
    }

    private sealed record Observed(string Scheme, bool IsHttps, bool HasForwardedProto, bool HasOriginalProto);

    private static async Task<Observed> ObserveAsync(params (string Name, string Value)[] headers)
    {
        using var host = await new HostBuilder()
            .ConfigureWebHost(web => web
                .UseTestServer()
                .ConfigureServices(services => services.Configure<ForwardedHeadersOptions>(options =>
                {
                    // Character-for-character the Program.cs configuration, including the two
                    // Clear() calls that no environment undoes.
                    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
                    options.ForwardLimit = 1;
                    options.KnownNetworks.Clear();
                    options.KnownProxies.Clear();
                }))
                .Configure(app =>
                {
                    app.UseForwardedHeaders();
                    app.Run(async context =>
                    {
                        context.Response.Headers["X-Test-Scheme"] = context.Request.Scheme;
                        context.Response.Headers["X-Test-Is-Https"] = context.Request.IsHttps.ToString();
                        context.Response.Headers["X-Test-Has-Forwarded-Proto"] =
                            context.Request.Headers.ContainsKey("X-Forwarded-Proto").ToString();
                        context.Response.Headers["X-Test-Has-Original-Proto"] =
                            context.Request.Headers.ContainsKey("X-Original-Proto").ToString();
                        await context.Response.WriteAsync("ok");
                    });
                }))
            .StartAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        foreach (var (name, value) in headers)
            request.Headers.Add(name, value);
        using var response = await host.GetTestClient().SendAsync(request);

        return new Observed(
            response.Headers.GetValues("X-Test-Scheme").Single(),
            bool.Parse(response.Headers.GetValues("X-Test-Is-Https").Single()),
            bool.Parse(response.Headers.GetValues("X-Test-Has-Forwarded-Proto").Single()),
            bool.Parse(response.Headers.GetValues("X-Test-Has-Original-Proto").Single()));
    }
}
