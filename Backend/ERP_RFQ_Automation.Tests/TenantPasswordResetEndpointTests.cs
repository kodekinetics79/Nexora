using System.Net;
using System.Reflection;
using System.Text.Json;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Platform.Hardening;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Security.PasswordReset;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The three anonymous password-reset endpoints, at the boundary a locked-out customer touches.
///
/// <para>Four properties are load-bearing here and none is visible from the service alone.</para>
///
/// <para>First, the endpoints must be reachable with NO credential of any kind — Program.cs sets an
/// authorization <c>FallbackPolicy</c> AND calls <c>MapControllers().RequireAuthorization()</c>, so
/// a reset controller that forgot <c>[AllowAnonymous]</c> would 401 every user and the flow would
/// be dead on arrival.</para>
///
/// <para>Second, and unique to this flow: <b>the request endpoint must answer identically whatever
/// it is given.</b> The service cannot leak (its request method returns <c>Task</c>), so the only
/// remaining place a leak can appear is here, in a status code, a body, or a validation shape.</para>
///
/// <para>Third, an endpoint that accepts a bearer secret and answers "was that one right?" is a
/// guessing oracle unless something bounds the guesses — and an endpoint that causes an email to be
/// sent is a spam cannon unless something bounds the sends. Those are two different budgets and
/// they must not share a counter.</para>
///
/// <para>Fourth, the wire contract is shared with a page
/// (<c>Frontend/src/pages/PasswordReset/passwordResetApi.ts</c>), which switches on the body's
/// <c>status</c> and falls back to the HTTP code — so both have to say the same thing, and the
/// same thing activation says for the same situation.</para>
/// </summary>
public sealed class TenantPasswordResetEndpointTests
{
    private const string GoodPassword = "Yanbu-Refinery-2#h";

    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult<EmailDeliveryReceipt?>(
                new EmailDeliveryReceipt("test", "accepted", DateTimeOffset.UtcNow));
        }
    }

    private static PasswordResetService Service(
        ErpRfqAutomationContext context, IEmailSender sender, ILoginAttemptThrottle throttle) =>
        new(context,
            sender,
            throttle,
            Options.Create(new NotificationsOptions { AppBaseUrl = "https://app.nexora.test" }),
            Options.Create(new TenantOnboardingOptions()),
            NullLogger<PasswordResetService>.Instance);

    private static (PasswordResetController Controller, CapturingEmailSender Sender) Controller(
        ErpRfqAutomationContext context, string clientIp = "203.0.113.7")
    {
        var sender = new CapturingEmailSender();
        var throttle = new LoginAttemptThrottle(
            context, new LoginThrottleOptions(), NullLogger<LoginAttemptThrottle>.Instance);

        var controller = new PasswordResetController(
            Service(context, sender, throttle), throttle,
            NullLogger<PasswordResetController>.Instance);

        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(clientIp);
        controller.ControllerContext = new ControllerContext { HttpContext = http };
        return (controller, sender);
    }

    private static async Task<User> SeedAsync(TestDb db, string slug, string email)
    {
        await using var context = db.ContextFor(null);

        var businessUnit = new BusinessUnit
        {
            BusinessUnitCode = slug.ToUpperInvariant(),
            BusinessUnitName = $"Tenant {slug}",
            Description = "Seeded for password-reset endpoint tests",
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

        var user = new User
        {
            FirstName = "Faisal",
            LastName = "Nasser",
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("the-password-they-forgot"),
            ImageUrl = string.Empty,
            RoleId = role.SetupId,
            Buid = businessUnit.Id,
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return user;
    }

    private static string TokenFrom(CapturingEmailSender sender)
    {
        var body = sender.Sent[^1].TextBody!;
        const string marker = "https://app.nexora.test/reset-password/";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0);
        var rest = body[(start + marker.Length)..];
        var end = rest.IndexOfAny(['\n', '\r', ' ']);
        return end < 0 ? rest : rest[..end];
    }

    // ==== reachability and shape ==============================================================

    [Fact]
    public void The_reset_endpoints_are_anonymous_and_the_request_endpoint_is_rate_limited()
    {
        // Program.cs makes authentication the default twice over: an authorization FallbackPolicy
        // plus MapControllers().RequireAuthorization(). [AllowAnonymous] on the controller is what
        // overrides both, exactly as AuthController and TenantActivationController do it. Losing
        // this attribute would 401 every locked-out customer — a failure that is invisible in unit
        // tests of the service and total in production.
        Assert.NotNull(typeof(PasswordResetController)
            .GetCustomAttribute<AllowAnonymousAttribute>(inherit: true));

        // Nothing else in the product limits how many emails an anonymous caller can cause to be
        // sent. The smtp policy (10 per 60s, partitioned per IP for anonymous traffic) is the
        // in-process bound on the burst that arrives before the durable counter catches up.
        var limiter = typeof(PasswordResetController)
            .GetMethod(nameof(PasswordResetController.RequestReset))!
            .GetCustomAttribute<EnableRateLimitingAttribute>(inherit: true);
        Assert.NotNull(limiter);
        Assert.Equal(RateLimitingExtensions.SmtpPolicy, limiter!.PolicyName);

        // The two guessing budgets are separate namespaces, and separate from every login plane.
        // A flood of mistyped addresses must not exhaust the budget that stops token guessing, and
        // neither may ever lock a real sign-in out.
        var planes = new[]
        {
            PasswordResetController.ResetTokenPlane,
            PasswordResetController.ResetRequestPlane,
            LoginPlane.Tenant,
            LoginPlane.Platform,
            LoginPlane.PlatformIp,
            TenantActivationController.ActivationPlane
        };
        Assert.Equal(planes.Length, planes.Distinct(StringComparer.Ordinal).Count());

        // The tenant login endpoint is the one this flow returns people to; it stays where it is.
        Assert.NotNull(typeof(AuthController).GetCustomAttribute<AllowAnonymousAttribute>(inherit: true));
    }

    /// <summary>
    /// The defect class no SQLite test can see, pinned the same way
    /// <c>DatabaseExecutionRoleRoutingTests</c> pins it for the two login paths.
    ///
    /// <para>On PostgreSQL the reset flow MUST execute as <c>nexora_identity_app</c>. Under
    /// <c>nexora_tenant_app</c> — the role every other anonymous request falls through to — the
    /// request carries no <c>nexora.business_unit_id</c>, so RLS hides the very Users row the
    /// lookup by email has to find and the completion has to write. The symptom would be this
    /// flow's worst possible failure: every address silently answered as "no such account",
    /// forever, invisibly, because the endpoint is built never to say whether it found one.</para>
    ///
    /// <para>The tenant-token case is not hypothetical. A stale session token in localStorage is a
    /// common way to end up needing a password reset, and axiosInstance is not what calls these
    /// endpoints — but a browser extension, a proxy or a future client change could still put one
    /// on the request, and the tenant check would then downgrade the role out of the privileges
    /// the flow requires.</para>
    /// </summary>
    [Theory]
    [InlineData(null, "/api/password-reset/requests")]
    [InlineData(null, "/api/password-reset/abc")]
    [InlineData(null, "/api/password-reset")]
    [InlineData(42L, "/api/password-reset/requests")]
    [InlineData(42L, "/api/password-reset/abc")]
    public void The_reset_endpoints_execute_as_the_identity_role_even_carrying_a_tenant_token(
        long? businessUnitId, string path)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;

        Assert.Equal(
            MultiTenancy.TenantRlsCommandInterceptor.IdentityRole,
            MultiTenancy.TenantRlsCommandInterceptor.ResolveDatabaseRole(
                businessUnitId, new HttpContextAccessor { HttpContext = context }));
    }

    [Fact]
    public void The_reset_prefix_is_segment_aware_and_not_merely_string_prefixed()
    {
        // StartsWithSegments, so a sibling route that merely shares the first characters is NOT
        // silently handed the identity role — the same property the login branches hold.
        var context = new DefaultHttpContext();
        context.Request.Path = "/api/password-reset-audit";

        Assert.Equal(
            MultiTenancy.TenantRlsCommandInterceptor.TenantRole,
            MultiTenancy.TenantRlsCommandInterceptor.ResolveDatabaseRole(
                null, new HttpContextAccessor { HttpContext = context }));
    }

    // ==== THE enumeration rule, at the wire ===================================================

    /// <summary>
    /// The response to a known address and to every kind of unknown one must be byte-identical:
    /// same status code, same body. Anything else turns a public form into a directory of a
    /// customer's staff.
    /// </summary>
    [Fact]
    public async Task Every_request_gets_a_byte_identical_answer_whoever_the_address_belongs_to()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db, "reset-endpoint-enum", "faisal@customer.example");

        await using var context = db.ContextFor(null);
        var (controller, sender) = Controller(context, "198.51.100.30");

        var answers = new List<(int Code, string Body)>();
        foreach (var address in new[]
                 {
                     user.Email,                       // real, active
                     "nobody@customer.example",        // no such account
                     "faisal@elsewhere.example",       // right local part, wrong domain
                     "not-an-email"                    // not an address at all
                 })
        {
            var response = Assert.IsType<ObjectResult>(
                await controller.RequestReset(new ForgotPasswordRequest { Email = address }, default));
            answers.Add((response.StatusCode!.Value, JsonSerializer.Serialize(response.Value)));
        }

        Assert.Single(answers.Select(a => a.Code).Distinct());
        Assert.Single(answers.Select(a => a.Body).Distinct(StringComparer.Ordinal));
        Assert.Equal(StatusCodes.Status202Accepted, answers[0].Code);

        // The body says nothing about any account, and the conditional phrasing is what makes it
        // true in all four cases rather than a reassuring fiction in three of them.
        Assert.DoesNotContain("faisal", answers[0].Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("If that address belongs", answers[0].Body, StringComparison.Ordinal);

        // Identical on the wire, and yet the work really happened for the one real address.
        Assert.Single(sender.Sent);
        Assert.Equal(user.Email, Assert.Single(sender.Sent[0].To).Address);
    }

    /// <summary>
    /// A malformed body is the shape most likely to grow a second response by accident: the
    /// obvious <c>[EmailAddress]</c> attribute, or a <c>BadRequest(ModelState)</c>, would make
    /// this endpoint answer differently for "sara@" than for "sara@nowhere.invalid" — and once
    /// there are two shapes, a prober has something to measure.
    /// </summary>
    [Fact]
    public async Task Even_an_invalid_body_gets_the_one_standard_answer()
    {
        using var db = new TestDb();
        await SeedAsync(db, "reset-endpoint-modelstate", "modelstate@customer.example");

        await using var context = db.ContextFor(null);
        var (controller, sender) = Controller(context, "198.51.100.31");
        controller.ModelState.AddModelError("Email", "The Email field is required.");

        var response = Assert.IsType<ObjectResult>(
            await controller.RequestReset(new ForgotPasswordRequest { Email = "" }, default));

        Assert.Equal(StatusCodes.Status202Accepted, response.StatusCode);
        Assert.Contains("If that address belongs",
            JsonSerializer.Serialize(response.Value), StringComparison.Ordinal);
        Assert.Empty(sender.Sent);
    }

    // ==== the wire contract for tokens ========================================================

    [Fact]
    public async Task Every_token_verdict_answers_with_the_status_and_code_the_page_reads()
    {
        using var db = new TestDb();

        // Three separate users, one per verdict. They cannot share an account: completing a reset
        // deliberately kills that ACCOUNT's other outstanding links, so a single shared user would
        // leave the "revoked" and "expired" cases already superseded and the test would prove
        // nothing about how those two states are reported.
        var spentUser = await SeedAsync(db, "reset-code-used", "used@customer.example");
        var revokedUser = await SeedAsync(db, "reset-code-revoked", "revoked@customer.example");
        var expiredUser = await SeedAsync(db, "reset-code-expired", "expired@customer.example");

        await using var context = db.ContextFor(null);
        var (controller, sender) = Controller(context);

        await controller.RequestReset(new ForgotPasswordRequest { Email = spentUser.Email }, default);
        var spent = TokenFrom(sender);
        await controller.RequestReset(new ForgotPasswordRequest { Email = revokedUser.Email }, default);
        var revoked = TokenFrom(sender);
        await controller.RequestReset(new ForgotPasswordRequest { Email = expiredUser.Email }, default);
        var expired = TokenFrom(sender);

        Assert.IsType<OkObjectResult>(await controller.Complete(
            spent, new SetNewPasswordRequest { Password = GoodPassword }, default));

        // "Revoked" is reached the way a real user reaches it: by asking for a second link, which
        // supersedes the first. There is no operator revocation on this flow — nobody at the
        // platform can cause or cancel a reset, by design.
        await controller.RequestReset(new ForgotPasswordRequest { Email = revokedUser.Email }, default);

        await context.Set<PasswordResetToken>()
            .Where(t => t.UserId == expiredUser.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAtUtc, DateTime.UtcNow.AddHours(-1)));

        // The page prefers the body's `status` and falls back to the code when the body is missing
        // (a proxy error page, a truncated response). Both are asserted, because a customer must
        // get the same explanation either way — and the same one activation gives.
        var expectations = new (string Token, string Status, int Code)[]
        {
            (spent, "used", StatusCodes.Status409Conflict),
            (revoked, "revoked", StatusCodes.Status403Forbidden),
            (expired, "expired", StatusCodes.Status410Gone),
            (new string('Z', 43), "invalid", StatusCodes.Status404NotFound)
        };

        foreach (var (token, status, code) in expectations)
        {
            var response = Assert.IsType<ObjectResult>(await controller.Inspect(token, default));
            var body = Assert.IsType<PasswordResetChallengeResponse>(response.Value);

            Assert.Equal(code, response.StatusCode);
            Assert.Equal(status, body.Status);

            // A link the holder cannot use must not describe the account it belonged to.
            Assert.Null(body.Email);
            Assert.Null(body.ExpiresAtUtc);
            Assert.Null(body.FirstName);
        }
    }

    [Fact]
    public async Task The_valid_challenge_returns_only_what_the_page_needs()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db, "reset-endpoint-preview", "faisal@customer.example");

        await using var context = db.ContextFor(null);
        var (controller, sender) = Controller(context);
        await controller.RequestReset(new ForgotPasswordRequest { Email = user.Email }, default);

        var payload = Assert.IsType<PasswordResetChallengeResponse>(
            Assert.IsType<OkObjectResult>(
                await controller.Inspect(TokenFrom(sender), default)).Value);

        Assert.Equal("valid", payload.Status);
        Assert.Equal("Faisal", payload.FirstName);
        Assert.Equal(12, payload.MinimumPasswordLength);

        // Serialised in full and swept: the real address, the token and the password hash must all
        // be absent from what an unauthenticated caller receives.
        var json = JsonSerializer.Serialize(payload);
        Assert.DoesNotContain("faisal@customer.example", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TokenFrom(sender), json, StringComparison.Ordinal);
        Assert.DoesNotContain(user.PasswordHash, json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Completing_through_the_endpoint_changes_the_password_and_says_so_plainly()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db, "reset-endpoint-complete", "complete@customer.example");

        await using var context = db.ContextFor(null);
        var (controller, sender) = Controller(context);
        await controller.RequestReset(new ForgotPasswordRequest { Email = user.Email }, default);

        var ok = Assert.IsType<OkObjectResult>(await controller.Complete(
            TokenFrom(sender), new SetNewPasswordRequest { Password = GoodPassword }, default));
        var body = JsonSerializer.Serialize(ok.Value);
        Assert.Contains("\"status\":\"reset\"", body, StringComparison.Ordinal);

        await using var verify = db.ContextFor(null);
        var updated = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == user.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(GoodPassword, updated.PasswordHash));
    }

    // ==== the two guessing bounds =============================================================

    [Fact]
    public async Task Repeated_unrecognised_tokens_from_one_address_are_locked_out()
    {
        using var db = new TestDb();
        await SeedAsync(db, "reset-endpoint-throttle", "throttle@customer.example");

        await using var context = db.ContextFor(null);
        var (controller, _) = Controller(context, "198.51.100.44");

        // The default policy tolerates five failures before the first lockout (SEC-H6), and the
        // counter is the SAME durable, cross-instance one the login endpoints use — under its own
        // key namespace, so a locked-out guesser here can never lock a real sign-in out.
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var refused = Assert.IsType<ObjectResult>(
                await controller.Complete(
                    $"guess-{attempt}".PadRight(43, 'x'),
                    new SetNewPasswordRequest { Password = GoodPassword }, default));
            Assert.Equal(StatusCodes.Status404NotFound, refused.StatusCode);
        }

        var throttled = Assert.IsType<ObjectResult>(
            await controller.Complete(
                new string('q', 43), new SetNewPasswordRequest { Password = GoodPassword }, default));
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled.StatusCode);
        // Tells an honest client when to come back instead of leaving it to retry blindly.
        Assert.False(string.IsNullOrEmpty(controller.Response.Headers.RetryAfter.ToString()));

        // No `status` on a 429: the page maps it to "we could not check your link just now", which
        // is true. Claiming the link is invalid would send a legitimate customer to support over a
        // rate limit.
        Assert.DoesNotContain("\"status\"", JsonSerializer.Serialize(throttled.Value));

        // The read endpoint shares the counter: guessing is guessing whichever door it uses.
        Assert.Equal(StatusCodes.Status429TooManyRequests,
            Assert.IsType<ObjectResult>(await controller.Inspect(new string('r', 43), default)).StatusCode);
    }

    /// <summary>
    /// The mail cannon, bounded — and bounded on a counter of its own.
    ///
    /// <para>Every request counts, whether or not the address matched anything. A counter that
    /// advanced only on unknown addresses would be a side channel with exactly the shape the
    /// response body refuses to have: a prober could read account existence off how quickly they
    /// got locked out.</para>
    /// </summary>
    [Fact]
    public async Task Flooding_the_request_form_is_locked_out_on_its_own_counter()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db, "reset-endpoint-flood", "flood@customer.example");

        await using var context = db.ContextFor(null);
        var (controller, sender) = Controller(context, "198.51.100.55");

        for (var attempt = 0; attempt < 5; attempt++)
        {
            var accepted = Assert.IsType<ObjectResult>(await controller.RequestReset(
                new ForgotPasswordRequest { Email = user.Email }, default));
            Assert.Equal(StatusCodes.Status202Accepted, accepted.StatusCode);
        }

        var throttled = Assert.IsType<ObjectResult>(await controller.RequestReset(
            new ForgotPasswordRequest { Email = user.Email }, default));
        Assert.Equal(StatusCodes.Status429TooManyRequests, throttled.StatusCode);

        // Five emails, then nothing. Without the bound, one caller can fill a stranger's inbox and
        // burn the deployment's sending reputation for as long as they care to keep going.
        Assert.Equal(5, sender.Sent.Count);

        // A locked-out flooder has NOT locked the token endpoints: the budgets are separate, so a
        // customer who is holding a real link can still use it while somebody abuses the form from
        // the same office NAT.
        Assert.Equal(StatusCodes.Status404NotFound,
            Assert.IsType<ObjectResult>(await controller.Inspect(new string('s', 43), default)).StatusCode);
    }

    [Fact]
    public async Task An_expired_link_does_not_count_toward_the_token_lockout()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db, "reset-endpoint-old", "old@customer.example");

        await using var context = db.ContextFor(null);
        var (controller, sender) = Controller(context, "198.51.100.66");

        var stale = new List<string>();
        for (var i = 0; i < 6; i++)
        {
            await controller.RequestReset(new ForgotPasswordRequest { Email = user.Email }, default);
            stale.Add(TokenFrom(sender));
            await context.Set<PasswordResetToken>()
                .Where(t => t.RedeemedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ExpiresAtUtc, DateTime.UtcNow.AddHours(-1)));
        }

        // Clicking six old links is what a real person does with a cluttered inbox — and here it
        // is the COMMON case, because "request another" is what the page tells them to do. Every
        // one was genuinely issued, so none is evidence of guessing; counting them would lock the
        // customer out of the link they are about to be sent.
        //
        // Superseded rows report "revoked" and expired ones "expired"; neither may feed the
        // counter, which is what the fresh link at the end proves.
        foreach (var token in stale)
        {
            var code = Assert.IsType<ObjectResult>(await controller.Inspect(token, default)).StatusCode;
            Assert.True(code is StatusCodes.Status410Gone or StatusCodes.Status403Forbidden,
                $"A genuinely issued link answered {code}.");
        }

        // The request plane is exhausted by six requests, so the seventh is minted directly
        // through the service — the token counter is what is under test here, not the mail one.
        var throttle = new LoginAttemptThrottle(
            context, new LoginThrottleOptions(), NullLogger<LoginAttemptThrottle>.Instance);
        var fresh = new CapturingEmailSender();
        await Service(context, fresh, throttle).RequestResetAsync(user.Email, null);

        Assert.IsType<OkObjectResult>(await controller.Inspect(TokenFrom(fresh), default));
    }

    [Fact]
    public async Task A_rejected_password_neither_spends_the_link_nor_counts_toward_the_lockout()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db, "reset-endpoint-policy", "policy@customer.example");

        await using var context = db.ContextFor(null);
        var (controller, sender) = Controller(context, "198.51.100.77");
        await controller.RequestReset(new ForgotPasswordRequest { Email = user.Email }, default);
        var token = TokenFrom(sender);

        // Someone choosing a new password fumbles it repeatedly. They hold a REAL link — this is
        // not guessing, and locking them out would send them back to the form for another email,
        // which is the loop this whole feature exists to end.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var weak = Assert.IsType<BadRequestObjectResult>(await controller.Complete(
                token, new SetNewPasswordRequest { Password = "password" }, default));

            var body = JsonSerializer.Serialize(weak.Value);
            // `error` is what the frontend's error-presentation boundary renders; the absence of
            // `status` is what stops the page treating a password problem as a dead link.
            Assert.Contains("\"error\"", body);
            Assert.DoesNotContain("\"status\"", body);
        }

        Assert.IsType<OkObjectResult>(await controller.Complete(
            token, new SetNewPasswordRequest { Password = GoodPassword }, default));

        await using var verify = db.ContextFor(null);
        var updated = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == user.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(GoodPassword, updated.PasswordHash));
    }

    // ==== the emailed link ====================================================================

    [Fact]
    public async Task The_emailed_link_matches_the_route_the_page_declares()
    {
        using var db = new TestDb();
        var user = await SeedAsync(db, "reset-endpoint-url", "url@customer.example");

        await using var context = db.ContextFor(null);
        var (controller, sender) = Controller(context);
        await controller.RequestReset(new ForgotPasswordRequest { Email = user.Email }, default);

        // Frontend/src/App.tsx routes "/reset-password/:token" — a path segment, not a query
        // parameter. A link built the other way 404s in the SPA and the customer sees a blank page
        // with no way to tell us what went wrong.
        var token = TokenFrom(sender);
        Assert.Contains($"https://app.nexora.test/reset-password/{token}", sender.Sent[^1].TextBody!);
    }
}
