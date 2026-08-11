using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Sec-D2. A platform password, on its own, must not open the control plane.
///
/// <para><b>The defect these pin shut.</b> <c>PlatformPolicies.PlatformScope</c> required only
/// <c>scope=platform</c>. Only the ROLE sub-policies (Owner, TenantAdmin, Billing, Impersonate)
/// added <c>amr=mfa</c>, and nothing anywhere forced enrollment — including for the bootstrap
/// owner, so "never enrolled" was a steady state, not an edge case. A password-only session
/// therefore reached every action gated on PlatformScope alone: the tenant register, the entire
/// cross-tenant privileged audit trail, and per-tenant queue and job rows — all executing under
/// BYPASSRLS. Mutations still demanded MFA, which bounded it to disclosure; the disclosure was of
/// every tenant on the deployment.</para>
///
/// <para>These tests evaluate the REAL policies registered by
/// <see cref="PlatformAuthExtensions.AddPlatformPolicies"/> against principals built from tokens
/// the REAL <see cref="PlatformAuthService"/> issued, so they cannot pass by restating the rule.</para>
/// </summary>
public sealed class PlatformScopeRequiresMfaTests
{
    private static IConfiguration Configuration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["Jwt:Key"] = "tenant-signing-key-that-is-at-least-32-bytes",
            ["Jwt:Issuer"] = "nexora-tests",
            ["Jwt:Audience"] = "RFQ"
        }).Build();

    private static async Task<bool> AuthorizesAsync(ClaimsPrincipal principal, string policy)
    {
        using var provider = new ServiceCollection().AddLogging()
            .AddAuthorization(options => options.AddPlatformPolicies()).BuildServiceProvider();
        return (await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, null, policy)).Succeeded;
    }

    private static ClaimsPrincipal Principal(string token) => new(new ClaimsIdentity(
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, PlatformAuthConstants.Scheme));

    private static async Task<long> SeedOwnerAsync(TestDb db, string email)
    {
        await using var context = db.ContextFor(null);
        var user = new PlatformUser
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("valid-password-123"),
            PlatformRole = PlatformRole.Owner,
            IsActive = true,
            CreatedOn = DateTime.UtcNow,
            CreatedBy = "test"
        };
        context.Set<PlatformUser>().Add(user);
        await context.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>A real password-only login — the bootstrap owner's first sign-in.</summary>
    private static async Task<PlatformLoginResponse> PasswordOnlyLoginAsync(TestDb db, string email)
    {
        await using var context = db.ContextFor(null);
        var service = new PlatformAuthService(
            context, Configuration(), NullLogger<PlatformAuthService>.Instance);
        return await service.LoginAsync(new PlatformLoginRequest { Email = email, Password = "valid-password-123" });
    }

    // ---- the refusal ------------------------------------------------------

    [Fact]
    public async Task A_password_only_session_is_refused_platform_scope()
    {
        using var db = new TestDb();
        await SeedOwnerAsync(db, "sec-d2-owner@example.test");
        var login = await PasswordOnlyLoginAsync(db, "sec-d2-owner@example.test");

        Assert.False(login.MfaRequired);           // no challenge: this operator never enrolled
        Assert.NotNull(login.Token);               // a token IS issued — see the enrollment test
        var principal = Principal(login.Token!);

        // THE assertion. Before Sec-D2 this was true, and with it went every tenant record, the
        // cross-tenant audit trail and per-tenant queue rows.
        Assert.False(await AuthorizesAsync(principal, PlatformPolicies.PlatformScope));

        // The role sub-policies were already closed; they must stay closed.
        Assert.False(await AuthorizesAsync(principal, PlatformPolicies.Owner));
        Assert.False(await AuthorizesAsync(principal, PlatformPolicies.TenantAdmin));
        Assert.False(await AuthorizesAsync(principal, PlatformPolicies.Billing));
        Assert.False(await AuthorizesAsync(principal, PlatformPolicies.Impersonate));
        Assert.False(await AuthorizesAsync(principal, PlatformPolicies.Mfa));
    }

    [Fact]
    public async Task The_same_session_can_still_enrol_so_the_bootstrap_owner_is_not_locked_out()
    {
        // The other half of the change, and the reason PlatformScope could be tightened at all.
        // Without this, a fresh deployment's first and only operator could sign in and do
        // nothing — including nothing about it.
        using var db = new TestDb();
        await SeedOwnerAsync(db, "sec-d2-firstrun@example.test");
        var login = await PasswordOnlyLoginAsync(db, "sec-d2-firstrun@example.test");

        Assert.True(login.MfaEnrollmentRequired);
        Assert.True(await AuthorizesAsync(Principal(login.Token!), PlatformPolicies.Enrollment));
    }

    [Fact]
    public async Task An_MFA_authenticated_session_reaches_platform_scope()
    {
        // The control. Without it, "everything is refused" would pass the test above.
        using var db = new TestDb();
        var userId = await SeedOwnerAsync(db, "sec-d2-enrolled@example.test");

        string token;
        await using (var context = db.ContextFor(null))
        {
            var service = new PlatformAuthService(
                context, Configuration(), NullLogger<PlatformAuthService>.Instance);
            var enrollment = await service.BeginMfaEnrollmentAsync(userId);
            await service.ConfirmMfaEnrollmentAsync(userId, new PlatformMfaEnrollmentConfirmRequest
            {
                TotpCode = PlatformTotp.CodeAt(enrollment.Secret, CurrentStep())
            });

            var challenged = await service.LoginAsync(new PlatformLoginRequest
            {
                Email = "sec-d2-enrolled@example.test",
                Password = "valid-password-123"
            });
            Assert.True(challenged.MfaRequired);

            var completed = await service.CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
            {
                ChallengeId = challenged.MfaChallengeId!.Value,
                TotpCode = PlatformTotp.CodeAt(enrollment.Secret, CurrentStep() + 1)
            });
            token = completed.Token!;
            Assert.False(completed.MfaEnrollmentRequired);
        }

        var principal = Principal(token);
        Assert.True(await AuthorizesAsync(principal, PlatformPolicies.PlatformScope));
        Assert.True(await AuthorizesAsync(principal, PlatformPolicies.Owner));
    }

    private static long CurrentStep() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / PlatformTotp.StepSeconds;

    // ---- the carve-out is exactly these endpoints -------------------------

    [Fact]
    public void Only_the_operators_own_second_factor_and_session_sit_on_the_enrollment_policy()
    {
        // An allow-list of one policy is only as good as what is on it. If a later change moves a
        // tenant-data endpoint onto PlatformPolicies.Enrollment "because it was 403ing", the
        // password-only hole reopens with no other symptom. This names every member.
        //
        // It grew by three when server-authoritative MFA enforcement landed, and each one is on the
        // list for the same reason the original four are — it is unreachable from anything else a
        // password-only session can do, and refusing it would make a control unusable in exactly
        // the state it exists for:
        //
        //   Reauthenticate      — the step-up itself. A session that could not reach this could
        //                         never satisfy PlatformHighRiskOperationAttribute, so purge,
        //                         export, legal-hold release and invoice finalisation would be
        //                         permanently unreachable rather than protected. It grants nothing
        //                         on its own: the stamp it writes is worthless without the
        //                         Owner/TenantAdmin gate the high-risk endpoint already carries.
        //   ListBrowserTrusts   — the operator's OWN remembered browsers, scoped to their user id.
        //   RevokeBrowserTrust  — likewise, and it only ever REMOVES authority.
        var onEnrollment = typeof(PlatformAuthController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<AuthorizeAttribute>()
                .Any(attribute => attribute.Policy == PlatformPolicies.Enrollment))
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            new[]
            {
                "BeginMfaEnrollment", "ConfirmMfaEnrollment", "GetMfaStatus", "ListBrowserTrusts",
                "Logout", "Reauthenticate", "RevokeBrowserTrust"
            },
            onEnrollment);

        // Outside PlatformAuthController, exactly one type may use it — and it is a READ of the
        // enforcement policy itself. With enforcement relaxed the console has to be able to ask
        // "is it relaxed?" before it can render the banner that says so, and after a bypass expires
        // an operator needs to be told that is why they are suddenly being challenged. The write
        // verbs on that controller carry PlatformPolicies.Owner on top (asserted in
        // PlatformMfaPolicyTests), so the carve-out buys a read of a mode name and nothing else.
        // The exclusion below is a METHOD, not the type it lives on. That distinction is the whole
        // control. PlatformMfaPolicyController carries Enrollment at CLASS level, so excluding the
        // type would mean every method added to it in future silently inherits the password-only
        // carve-out and this test — the one thing standing between that carve-out and creep —
        // reports green. Found by the independent SDET reviewing exactly this allow-list.
        //
        // Effective is named because it is the read the console cannot do without: with enforcement
        // relaxed it has to ask "is it relaxed?" before it can render the banner saying so. Every
        // OTHER member of that controller must re-assert Owner on the method, and the assertion
        // below proves it rather than trusting it.
        var mfaPolicyControllerLeaks = typeof(ERP_RFQ_Automation.Platform.Auth.PlatformMfaPolicyController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(method => method.GetCustomAttributes<HttpMethodAttribute>().Any())
            .Where(method => method.Name != "Effective")
            .Where(method => !method.GetCustomAttributes<AuthorizeAttribute>()
                .Any(attribute => attribute.Policy == PlatformPolicies.Owner))
            .Select(method => method.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(mfaPolicyControllerLeaks.Count == 0,
            "Every endpoint on PlatformMfaPolicyController except the 'Effective' read must carry " +
            "PlatformPolicies.Owner on the method, because the class-level Enrollment policy admits a " +
            "password-only session. Unguarded: " + string.Join(", ", mfaPolicyControllerLeaks));

        var elsewhere = typeof(PlatformAuthController).Assembly.GetTypes()
            .Where(type => type != typeof(PlatformAuthController))
            .Where(type => type != typeof(ERP_RFQ_Automation.Platform.Auth.PlatformMfaPolicyController))
            .SelectMany(type => type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                .SelectMany(method => method.GetCustomAttributes<AuthorizeAttribute>()
                    .Select(attribute => (Owner: $"{type.Name}.{method.Name}", attribute.Policy)))
                .Concat(type.GetCustomAttributes<AuthorizeAttribute>()
                    .Select(attribute => (Owner: type.Name, attribute.Policy))))
            .Where(pair => pair.Policy == PlatformPolicies.Enrollment)
            .Select(pair => pair.Owner)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.True(elsewhere.Count == 0,
            "PlatformPolicies.Enrollment is satisfied by a session that has NOT completed a second "
            + "factor. It exists so an unenrolled operator can enrol, and nothing else may sit on "
            + "it:\n  " + string.Join("\n  ", elsewhere));
    }
}
