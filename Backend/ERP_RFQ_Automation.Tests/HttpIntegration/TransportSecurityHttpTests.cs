using System.Net;
using ERP_RFQ_Automation.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

/// <summary>
/// Gate 9, code half — the half of the transport controls that only the composed pipeline can
/// prove. <c>TransportSecurityAndSecretRedactionTests</c> asserts the DECISIONS on the portable
/// lane; these assert that the decision reaches the wire, against the real Program.cs, in an
/// environment that is not Development (the fixture runs as "Testing", exactly as a deploy does).
/// </summary>
[Collection(Release01BHttpCollection.Name)]
[Trait("Category", "PostgreSQL")]
public sealed class TransportSecurityHttpTests(Release01BHttpApplication app)
{
    [Fact]
    public async Task Every_response_carries_the_locked_down_content_security_policy()
    {
        using var response = await app.CreateClient().GetAsync("/health");

        var policy = Assert.Single(response.Headers.GetValues("Content-Security-Policy"));
        Assert.Equal(TransportSecurityPolicy.ApiContentSecurityPolicy, policy);
        // The headers that were already present must not have been lost in the process.
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
    }

    [Theory]
    [InlineData("http://localhost:5173")]
    [InlineData("http://localhost:4173")]
    [InlineData("http://localhost:3000")]
    [InlineData("http://127.0.0.1:5173")]
    [InlineData("http://127.0.0.1:4173")]
    [InlineData("http://127.0.0.1:3000")]
    public async Task A_loopback_origin_is_refused_outside_development(string origin)
    {
        // The finding: these six were merged into the allow-list in EVERY environment, so a page
        // served from any machine on one of those ports could READ authenticated API responses.
        // Absence of the header is the refusal — the browser, not the server, enforces it.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", origin);
        using var response = await app.CreateClient().SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task A_loopback_preflight_is_refused_outside_development()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/Supplier");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "authorization");
        using var response = await app.CreateClient().SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Methods"));
    }

    [Fact]
    public async Task The_deployed_frontend_origin_is_still_admitted()
    {
        // Gating localhost must not cost the real frontend its access. Without this assertion the
        // fix above is indistinguishable from breaking CORS altogether.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("Origin", TransportSecurityPolicy.DeployedFrontendOrigin);
        using var response = await app.CreateClient().SendAsync(request);

        Assert.Equal(
            TransportSecurityPolicy.DeployedFrontendOrigin,
            Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
    }

    [Fact]
    public async Task Nothing_under_the_web_root_is_served()
    {
        // ProductRepository, CustomerController and UserController all write user-supplied bytes
        // under WebRootPath with the uploaded extension preserved, and .html is on
        // DocumentIntakeAllowList. No static-file middleware is registered, so those URLs are
        // dead — and this test is what makes adding `app.UseStaticFiles()` a red build rather than
        // an unauthenticated stored-XSS hole on the API origin, next to a frontend that keeps its
        // JWT in localStorage. The file is written HERE rather than relying on a checked-in
        // fixture, because the demo uploads are no longer tracked and a fresh clone would make
        // this pass for the wrong reason.
        var environment = app.Services.GetRequiredService<IWebHostEnvironment>();
        var webRoot = string.IsNullOrWhiteSpace(environment.WebRootPath)
            ? Path.Combine(environment.ContentRootPath, "wwwroot")
            : environment.WebRootPath;
        var folder = Path.Combine(webRoot, "ProductAttachments");
        Directory.CreateDirectory(folder);
        var storedName = $"nexora-static-file-guard-{Guid.NewGuid():N}.html";
        var storedPath = Path.Combine(folder, storedName);
        await File.WriteAllTextAsync(storedPath, "<script>document.title='served'</script>");

        try
        {
            using var response = await app.CreateClient()
                .GetAsync($"/ProductAttachments/{storedName}");

            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.DoesNotContain("served", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            File.Delete(storedPath);
        }
    }

    [Fact]
    public async Task An_unlabelled_request_behind_an_untrusted_hop_is_answered_rather_than_looped()
    {
        // The fixture configures no trusted proxy, which is also true of every deployment today.
        // In that state the process cannot observe the client's scheme, and assuming it is plain
        // is exactly what turns a redirect into an infinite loop behind a TLS-terminating edge.
        // The request must therefore be SERVED, not redirected — this is the whole reason
        // redirection could be turned on by default at all.
        //
        // A REAL host is set deliberately: TestServer defaults to "localhost", which the loopback
        // exclusion short-circuits, so without this the test would pass for the wrong reason and
        // prove nothing about the scheme pass-through.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = "nexora-fyjw.onrender.com";
        using var response = await NonRedirectingClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    [Fact]
    public async Task A_request_the_edge_labels_as_plain_http_is_redirected_exactly_once()
    {
        // The header proves an edge is in front and speaks it, so redirecting is safe: the
        // redirected request arrives labelled https and terminates. Asserted end-to-end because
        // the loop, if it existed, would exist in the composed pipeline and nowhere else.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-Proto", "http");
        request.Headers.Host = "nexora-fyjw.onrender.com";
        using var response = await NonRedirectingClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        Assert.Equal(
            "https://nexora-fyjw.onrender.com/health",
            Assert.Single(response.Headers.GetValues("Location")));
    }

    [Fact]
    public async Task A_request_the_edge_labels_as_https_is_served_and_carries_hsts()
    {
        // The second hop of the redirect above. If this 307'd, the two together would be the
        // loop; that it does not is the termination proof.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Host = "nexora-fyjw.onrender.com";
        using var response = await NonRedirectingClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(
            ERP_RFQ_Automation.Infrastructure.TransportSecurityPolicy.HstsHeaderValue,
            Assert.Single(response.Headers.GetValues("Strict-Transport-Security")));
    }

    [Fact]
    public async Task A_loopback_host_is_neither_redirected_nor_pinned()
    {
        // An HSTS entry against "localhost" would poison every other project on the machine, and
        // no certificate can be presented for it.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Add("X-Forwarded-Proto", "http");
        request.Headers.Host = "localhost";
        using var response = await NonRedirectingClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }

    private HttpClient NonRedirectingClient() =>
        app.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
}
