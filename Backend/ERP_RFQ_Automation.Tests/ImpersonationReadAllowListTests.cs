using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Sec-D3. What a support-impersonation session may read is now stated, not guessed at.
///
/// <para><b>The defect these pin shut.</b> The guard was a DENY-list naming file-download and
/// export routes. A deny-list has to anticipate what is reachable, and this one was written
/// against the wrong set: an impersonation token carries no <c>roleId</c>, so
/// <c>PermissionHandler</c> refuses every <c>[RequireModulePermission]</c> endpoint — meaning the
/// surface actually reachable is exactly the endpoints with NO module permission, and the deny-list
/// named none of them. The tenant's whole customer and supplier contact directory
/// (<c>/api/Contact</c>) and the AI copilot's transcripts and decision audit (<c>/api/agent</c>)
/// were readable by any operator who could mint a session.</para>
///
/// <para>The list is now an ALLOW-list, so a route added tomorrow with no permission attribute is
/// refused by default and admitting it is a decision someone has to write down.</para>
/// </summary>
public sealed class ImpersonationReadAllowListTests
{
    private const string LiveJti = "sec-d3-live-session";

    private static DefaultHttpContext Impersonated(string path, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(PlatformAuthConstants.ImpersonatedClaim, "true"),
            new Claim(PlatformAuthConstants.ReadOnlyClaim, "true"),
            new Claim("businessUnitId", "7"),
            new Claim("jti", LiveJti)
        ], "test"));
        return context;
    }

    /// <summary>
    /// Runs the real middleware with the revocation decision pre-cached as VALID, so what these
    /// tests measure is the allow-list and not the session ledger — which has its own tests. A
    /// refusal here is therefore always the allow-list refusing, never a missing session row.
    /// </summary>
    private static async Task<(bool Reached, int Status, string Body)> InvokeAsync(HttpContext context)
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        cache.Set(ReadOnlyImpersonationMiddleware.SessionCacheKey(LiveJti), true);

        var reached = false;
        var middleware = new ReadOnlyImpersonationMiddleware(
            _ => { reached = true; return Task.CompletedTask; }, cache);

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        var body = await new StreamReader(context.Response.Body).ReadToEndAsync();
        return (reached, context.Response.StatusCode, body);
    }

    // ---- the routes the finding named ------------------------------------

    [Theory]
    // The tenant's entire customer and supplier contact directory — names, emails, direct lines.
    [InlineData("/api/Contact")]
    [InlineData("/api/Contact/41")]
    // The AI copilot's transcripts, its decision audit and its policy: the tenant's own
    // commercial reasoning, in prose.
    [InlineData("/api/agent/sessions")]
    [InlineData("/api/agent/audit")]
    // Reference lists that mix harmless statuses with supplier and warehouse names.
    [InlineData("/api/GeneralDropdown/suppliers")]
    // Tenant role rows.
    [InlineData("/api/SetupMaster")]
    // A specific operator's saved column layouts.
    [InlineData("/api/list-views/quotes/columns")]
    // Nothing that simply happens not to exist yet is admitted either.
    [InlineData("/api/some-endpoint-added-next-week")]
    public async Task A_route_that_is_not_on_the_allow_list_is_refused(string path)
    {
        var (reached, status, body) = await InvokeAsync(Impersonated(path));

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains(NexoraProblems.ImpersonationReadDenied, body);
    }

    [Theory]
    [InlineData("/api/sla/policy")]
    [InlineData("/api/commercial-policy")]
    [InlineData("/api/QuoteConfiguration/7")]
    [InlineData("/api/Country")]
    public async Task A_route_on_the_allow_list_is_served(string path)
    {
        // The control. Without it "refuse everything" would satisfy the theory above, and the
        // allow-list would be an outage rather than a boundary.
        var (reached, _, _) = await InvokeAsync(Impersonated(path));
        Assert.True(reached);
    }

    [Fact]
    public async Task A_near_miss_on_an_allowed_prefix_is_still_refused()
    {
        // Segment matching, not string prefixing. "/api/slaX" and "/api/sla/policy-history" must
        // not ride in on "/api/sla/policy".
        Assert.False((await InvokeAsync(Impersonated("/api/slaX"))).Reached);
        Assert.False((await InvokeAsync(Impersonated("/api/Contacts"))).Reached);
    }

    [Fact]
    public async Task An_export_beneath_an_allowed_prefix_is_still_refused()
    {
        // The retained second check. It is redundant against today's list and exists so that
        // widening a prefix later cannot hand out a bulk export by accident.
        Assert.False((await InvokeAsync(Impersonated("/api/Country/export"))).Reached);
        Assert.False((await InvokeAsync(Impersonated("/api/Country/export.csv"))).Reached);
    }

    [Fact]
    public async Task Writes_are_still_refused_even_on_an_allowed_route()
    {
        // The read allow-list must not be mistaken for permission to write. A PUT to
        // /api/commercial-policy would rewrite the tenant's margin and approval rules.
        var (reached, status, body) = await InvokeAsync(Impersonated("/api/commercial-policy", "PUT"));

        Assert.False(reached);
        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Contains(NexoraProblems.ReadOnlyImpersonation, body);
    }

    [Fact]
    public async Task A_normal_tenant_session_is_untouched_by_the_allow_list()
    {
        // The allow-list applies to impersonation and to nothing else. If it ever applied to
        // ordinary sessions it would take the product down, which is a failure mode worth one
        // assertion.
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/Contact";
        context.Request.Method = "GET";
        context.Response.Body = new MemoryStream();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("businessUnitId", "7"), new Claim("roleId", "3")], "test"));

        Assert.True((await InvokeAsync(context)).Reached);
    }

    /// <summary>
    /// The forbidden roots this guard holds the allow-list against: every route family that
    /// carries the tenant's customers, suppliers, commercial documents, files or AI reasoning.
    /// </summary>
    private static readonly string[] ForbiddenRoots =
    [
        "/api/Contact", "/api/Customer", "/api/Supplier", "/api/Lead", "/api/Rfq",
        "/api/Quote", "/api/Order", "/api/agent", "/api/File", "/api/processing-evidence",
        "/api/Product", "/api/commercial-finance", "/api/GeneralDropdown", "/api/SetupMaster"
    ];

    /// <summary>
    /// Route containment, by SEGMENT — the same rule the middleware itself applies, and for the
    /// same reason it applies it.
    ///
    /// <para>This started life as a raw <c>string.StartsWith</c> and that was a defect in the guard,
    /// not in the list it guards. A character-prefix test cannot tell a route from a different route
    /// that merely shares its opening letters: it read <c>/api/QuoteConfiguration</c> — the tenant's
    /// own quote numbering, template and letterhead — as if it were <c>/api/Quote</c>, the tenant's
    /// commercial documents. The middleware refuses to make that mistake in production
    /// (<c>PathString.StartsWithSegments</c>, and <c>A_near_miss_on_an_allowed_prefix_is_still_refused</c>
    /// pins it), and a guard that is looser than the thing it guards reports failures that are not
    /// there while staying silent about the ones that are.</para>
    /// </summary>
    private static bool IsUnderRoute(string candidate, string root) =>
        new PathString(candidate).StartsWithSegments(root, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void The_allow_list_admits_no_route_carrying_customer_or_document_data()
    {
        // A standing check on the list itself. Every entry has to be tenant CONFIGURATION; the
        // moment someone adds a data route "just to see the customer's records", this fails and
        // says why.
        var offending = ReadOnlyImpersonationMiddleware.AllowedReadPaths
            .Where(allowed => ForbiddenRoots.Any(root => IsUnderRoute(allowed, root)))
            .ToList();

        Assert.True(offending.Count == 0,
            "These impersonation allow-list entries expose tenant records rather than tenant "
            + "configuration:\n  " + string.Join("\n  ", offending));
    }

    [Fact]
    public void The_standing_guard_still_bites_on_a_genuine_data_route()
    {
        // Proof that tightening the matcher above did not turn the guard off. If someone adds the
        // tenant's commercial documents or their contact directory to the allow-list, this is the
        // shape of comparison that has to catch it — so it is asserted directly rather than left to
        // be discovered the day it matters.
        Assert.True(ForbiddenRoots.Any(root => IsUnderRoute("/api/Quote", root)));
        Assert.True(ForbiddenRoots.Any(root => IsUnderRoute("/api/Quote/17/lines", root)));
        Assert.True(ForbiddenRoots.Any(root => IsUnderRoute("/api/Contact", root)));
        Assert.True(ForbiddenRoots.Any(root => IsUnderRoute("/api/agent/sessions", root)));

        // And the near-miss that is genuinely a different route is not caught, which is the whole
        // difference between this matcher and the character-prefix one it replaced.
        Assert.False(ForbiddenRoots.Any(root => IsUnderRoute("/api/QuoteConfiguration", root)));
    }
}
