using System.Net;
using System.Reflection;
using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The two anonymous activation endpoints, at the boundary the invitee actually touches.
///
/// <para>Three properties are load-bearing here and none is visible from the service alone.
/// First, the endpoints must be reachable with NO token of any kind — Program.cs sets an
/// authorization <c>FallbackPolicy</c> AND calls <c>MapControllers().RequireAuthorization()</c>,
/// so an activation controller that forgot <c>[AllowAnonymous]</c> would 401 every invitee and
/// the flow would be dead on arrival. Second, an endpoint that accepts a bearer secret and
/// answers "was that one right?" is a guessing oracle unless something bounds the guesses.
/// Third, the wire contract is shared with a page that already exists
/// (<c>Frontend/src/pages/Activation/activationApi.ts</c>), which switches on the body's
/// <c>status</c> and falls back to the HTTP code — so both have to say the same thing.</para>
/// </summary>
public sealed class TenantActivationEndpointTests
{
    private const string GoodPassword = "Jeddah-Terminal-3#w";

    private sealed class SilentEmailSender : IEmailSender
    {
        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default) =>
            Task.FromResult<EmailDeliveryReceipt?>(
                new EmailDeliveryReceipt("test", "accepted", DateTimeOffset.UtcNow));
    }

    private static TenantAdminInvitationService Service(
        ErpRfqAutomationContext context, TenantOnboardingOptions? options = null) =>
        new(context,
            new SilentEmailSender(),
            Options.Create(new NotificationsOptions { AppBaseUrl = "https://app.nexora.test" }),
            Options.Create(options ?? new TenantOnboardingOptions()),
            NullLogger<TenantAdminInvitationService>.Instance);

    private static TenantActivationController Controller(
        ErpRfqAutomationContext context, string clientIp = "203.0.113.7")
    {
        var throttle = new LoginAttemptThrottle(
            context, new LoginThrottleOptions(), NullLogger<LoginAttemptThrottle>.Instance);

        var controller = new TenantActivationController(
            Service(context), throttle, NullLogger<TenantActivationController>.Instance);

        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return controller;
    }

    private static async Task<(Tenant tenant, User admin)> SeedAsync(TestDb db, string slug, string email)
    {
        await using var context = db.ContextFor(null);

        var businessUnit = new BusinessUnit
        {
            BusinessUnitCode = slug.ToUpperInvariant(),
            BusinessUnitName = $"Tenant {slug}",
            Description = "Seeded for activation endpoint tests",
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        context.BusinessUnits.Add(businessUnit);
        await context.SaveChangesAsync();

        var role = new SetupMaster
        {
            SetupType = "Role",
            SetupCode = "SUPER_ADMIN",
            SetupValue = "Super Administrator",
            BusinessUnitId = businessUnit.Id,
            RoleRank = RoleRanks.Owner,
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        context.SetupMasters.Add(role);

        var tenant = new Tenant
        {
            Name = $"Tenant {slug}",
            Slug = slug,
            Status = TenantStatus.Active,
            PrimaryBusinessUnitId = businessUnit.Id,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        context.Set<Tenant>().Add(tenant);
        await context.SaveChangesAsync();

        var admin = new User
        {
            FirstName = "Omar",
            LastName = "Haddad",
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("operator-chosen-secret"),
            ImageUrl = string.Empty,
            RoleId = role.SetupId,
            Buid = businessUnit.Id,
            IsActive = false,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        context.Users.Add(admin);
        await context.SaveChangesAsync();

        return (tenant, admin);
    }

    private static async Task<IssuedTenantAdminInvitation> IssueAsync(
        TestDb db, Tenant tenant, User admin, TenantOnboardingOptions? options = null)
    {
        await using var context = db.ContextFor(null);
        return await Service(context, options).IssueAsync(context, new TenantAdminInvitationRequest
        {
            TenantId = tenant.Id,
            UserId = admin.Id,
            Email = admin.Email,
            RecipientName = admin.FirstName,
            TenantName = tenant.Name,
            IssuedBy = "owner@nexora.app"
        });
    }

    // ==== reachability ========================================================================

    [Fact]
    public void The_activation_endpoints_are_anonymous_and_the_operator_endpoints_are_not()
    {
        // Program.cs makes authentication the default twice over: an authorization
        // FallbackPolicy plus MapControllers().RequireAuthorization(). [AllowAnonymous] on the
        // controller is what overrides both, exactly as AuthController and PlatformAuthController
        // do it. Losing this attribute would 401 every invitee — a failure that is invisible in
        // unit tests of the service and total in production.
        Assert.NotNull(typeof(TenantActivationController)
            .GetCustomAttribute<AllowAnonymousAttribute>(inherit: true));

        // The operator half must NOT be anonymous, and must sit behind the same gates the
        // tenant mutations on TenantsController use.
        Assert.Null(typeof(TenantAdminInvitationsController)
            .GetCustomAttribute<AllowAnonymousAttribute>(inherit: true));
        Assert.Equal(PlatformPolicies.PlatformScope,
            typeof(TenantAdminInvitationsController)
                .GetCustomAttribute<AuthorizeAttribute>(inherit: true)!.Policy);

        foreach (var mutation in new[] { "Resend", "Revoke" })
            Assert.Equal(PlatformPolicies.TenantAdmin,
                typeof(TenantAdminInvitationsController).GetMethod(mutation)!
                    .GetCustomAttribute<AuthorizeAttribute>(inherit: true)!.Policy);
    }

    // ==== the wire contract ===================================================================

    [Fact]
    public async Task Every_token_verdict_answers_with_the_status_and_code_the_page_reads()
    {
        using var db = new TestDb();

        // Three separate tenants, one per verdict. They cannot share an administrator: spending
        // an invitation deliberately kills that ACCOUNT's other outstanding links, so a single
        // shared admin would leave the "revoked" and "expired" cases already superseded and the
        // test would prove nothing about how those two states are reported.
        var (spentTenant, spentAdmin) = await SeedAsync(db, "endpoint-used", "used@customer.example");
        var (revokedTenant, revokedAdmin) = await SeedAsync(db, "endpoint-revoked", "revoked@customer.example");
        var (expiredTenant, expiredAdmin) = await SeedAsync(db, "endpoint-expired", "expired@customer.example");

        var spent = await IssueAsync(db, spentTenant, spentAdmin);
        var revoked = await IssueAsync(db, revokedTenant, revokedAdmin);
        var expired = await IssueAsync(db, expiredTenant, expiredAdmin);

        await using var setup = db.ContextFor(null);
        Assert.IsType<OkObjectResult>(
            await Controller(setup).Activate(
                spent.Token, new SetActivationPasswordRequest { Password = GoodPassword },
                CancellationToken.None));
        Assert.True(await Service(setup).RevokeAsync(
            revokedTenant.Id, revoked.InvitationId, "owner@nexora.app", "Wrong address."));
        await setup.Set<TenantAdminInvitation>()
            .Where(i => i.Id == expired.InvitationId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ExpiresAtUtc, DateTime.UtcNow.AddHours(-1)));

        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        // The page prefers the body's `status` and falls back to the code when the body is
        // missing (a proxy error page, a truncated response). Both are asserted, because a
        // customer must get the same explanation either way.
        var expectations = new (string Token, string Status, int Code)[]
        {
            (spent.Token, "used", StatusCodes.Status409Conflict),
            (revoked.Token, "revoked", StatusCodes.Status403Forbidden),
            (expired.Token, "expired", StatusCodes.Status410Gone),
            (new string('Z', 43), "invalid", StatusCodes.Status404NotFound)
        };

        foreach (var (token, status, code) in expectations)
        {
            var response = Assert.IsType<ObjectResult>(
                await controller.Inspect(token, CancellationToken.None));
            var body = Assert.IsType<TenantActivationChallengeResponse>(response.Value);

            Assert.Equal(code, response.StatusCode);
            Assert.Equal(status, body.Status);

            // A link the holder cannot use must not describe the account it belonged to.
            Assert.Null(body.Email);
            Assert.Null(body.TenantName);
            Assert.Null(body.ExpiresAtUtc);
        }
    }

    [Fact]
    public async Task The_valid_challenge_returns_only_what_the_page_needs()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedAsync(db, "endpoint-preview", "omar@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        await using var context = db.ContextFor(null);
        var payload = Assert.IsType<TenantActivationChallengeResponse>(
            Assert.IsType<OkObjectResult>(
                await Controller(context).Inspect(issued.Token, CancellationToken.None)).Value);

        Assert.Equal("valid", payload.Status);
        Assert.Equal("Tenant endpoint-preview", payload.TenantName);
        Assert.Equal("Omar", payload.FirstName);
        Assert.Equal(12, payload.MinimumPasswordLength);

        // Serialised in full and swept: the real address, the token and the password hash must
        // all be absent from what an unauthenticated caller receives.
        var json = JsonSerializer.Serialize(payload);
        Assert.DoesNotContain("omar@customer.example", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(issued.Token, json, StringComparison.Ordinal);
        Assert.DoesNotContain(admin.PasswordHash, json, StringComparison.Ordinal);
    }

    // ==== the guessing bound ==================================================================

    [Fact]
    public async Task Repeated_unrecognised_tokens_from_one_address_are_locked_out()
    {
        using var db = new TestDb();
        await SeedAsync(db, "endpoint-throttle", "throttle@customer.example");

        await using var context = db.ContextFor(null);
        var controller = Controller(context, "198.51.100.44");

        // The default policy tolerates five failures before the first lockout (SEC-H6), and the
        // counter is the SAME durable, cross-instance one the login endpoints use — under a
        // separate key namespace, so a locked-out guesser here can never lock a real sign-in out.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var refused = Assert.IsType<ObjectResult>(
                await controller.Activate(
                    $"guess-{attempt}".PadRight(43, 'x'),
                    new SetActivationPasswordRequest { Password = GoodPassword },
                    CancellationToken.None));
            Assert.Equal(StatusCodes.Status404NotFound, refused.StatusCode);
        }

        var throttled = Assert.IsType<ObjectResult>(
            await controller.Activate(
                new string('q', 43), new SetActivationPasswordRequest { Password = GoodPassword },
                CancellationToken.None));
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled.StatusCode);
        // Tells an honest client when to come back instead of leaving it to retry blindly.
        Assert.False(string.IsNullOrEmpty(controller.Response.Headers.RetryAfter.ToString()));

        // No `status` on a 429: the page maps it to "we could not check your link just now",
        // which is true. Claiming the link is invalid would send a legitimate customer to
        // support over a rate limit.
        Assert.DoesNotContain("\"status\"", JsonSerializer.Serialize(throttled.Value));

        // The read endpoint shares the counter: guessing is guessing whichever door it uses.
        var inspect = Assert.IsType<ObjectResult>(
            await controller.Inspect(new string('r', 43), CancellationToken.None));
        Assert.Equal(StatusCodes.Status429TooManyRequests, inspect.StatusCode);
    }

    [Fact]
    public async Task An_expired_link_does_not_count_toward_the_lockout()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedAsync(db, "endpoint-expired-budget", "old@customer.example");

        await using var context = db.ContextFor(null);
        var stale = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            var issued = await IssueAsync(db, tenant, admin);
            await context.Set<TenantAdminInvitation>()
                .Where(x => x.Id == issued.InvitationId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.ExpiresAtUtc, DateTime.UtcNow.AddHours(-1)));
            stale.Add(issued.Token);
        }

        var controller = Controller(context, "198.51.100.66");

        // Clicking six old invitations is what a real person does with a cluttered inbox. Every
        // one of these was genuinely issued, so none is evidence of guessing — counting them
        // would lock the customer out of the resend they are about to be sent.
        foreach (var token in stale)
            Assert.Equal(StatusCodes.Status410Gone,
                Assert.IsType<ObjectResult>(await controller.Inspect(token, CancellationToken.None)).StatusCode);

        var fresh = await IssueAsync(db, tenant, admin);
        Assert.IsType<OkObjectResult>(await controller.Inspect(fresh.Token, CancellationToken.None));
    }

    [Fact]
    public async Task A_rejected_password_neither_spends_the_link_nor_counts_toward_the_lockout()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedAsync(db, "endpoint-policy", "policy@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        await using var context = db.ContextFor(null);
        var controller = Controller(context, "198.51.100.55");

        // Someone choosing a password fumbles it repeatedly. They hold a REAL invitation — this
        // is not guessing, and locking them out would send them straight back to the operator
        // asking for a password, which is the phone call this whole flow exists to end.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var weak = Assert.IsType<BadRequestObjectResult>(
                await controller.Activate(
                    issued.Token, new SetActivationPasswordRequest { Password = "password" },
                    CancellationToken.None));

            var body = JsonSerializer.Serialize(weak.Value);
            // `error` is what the frontend's error-presentation boundary renders; the absence of
            // `status` is what stops the page treating a password problem as a dead link.
            Assert.Contains("\"error\"", body);
            Assert.DoesNotContain("\"status\"", body);
        }

        Assert.IsType<OkObjectResult>(
            await controller.Activate(
                issued.Token, new SetActivationPasswordRequest { Password = GoodPassword },
                CancellationToken.None));

        await using var verify = db.ContextFor(null);
        var activated = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == admin.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(GoodPassword, activated.PasswordHash));
    }

    // ==== the emailed link ====================================================================

    [Fact]
    public async Task The_emailed_link_matches_the_route_the_page_declares()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedAsync(db, "endpoint-url", "url@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        // Frontend/src/App.tsx routes "/activate/:token" — a path segment, not a query
        // parameter. A link built the other way 404s in the SPA and the customer sees a blank
        // page with no way to tell us what went wrong.
        Assert.Equal($"https://app.nexora.test/activate/{issued.Token}", issued.ActivationUrl);
    }
}
