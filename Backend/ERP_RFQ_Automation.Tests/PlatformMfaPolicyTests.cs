using System.IdentityModel.Tokens.Jwt;
using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Server-authoritative platform MFA enforcement.
///
/// <para>The invariant every test here defends is the same one: the mode is decided by the BACKEND
/// from a persisted row, the clock and the process's own environment classification — and by
/// nothing a request, a header or a console can say.</para>
/// </summary>
public sealed class PlatformMfaPolicyTests
{
    private const string OwnerPassword = "valid-password-123";
    private const string ReasonThatIsLongEnough =
        "Rehearsing the ZATCA cutover on the isolated staging rig; TOTP devices are with the auditors.";

    // ==== who may relax enforcement, and where ======================================================

    [Fact]
    public async Task Local_owner_can_select_DISABLED_TEST_ONLY_and_the_effective_policy_follows()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        await using var context = db.ContextFor(null);
        var service = ServiceFor(context, Local());

        var result = await service.ChangeAsync(
            Request(PlatformMfaMode.DISABLED_TEST_ONLY, hours: 4), Principal(owner), null);

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(nameof(PlatformMfaMode.DISABLED_TEST_ONLY), result.Policy!.Mode);
        Assert.NotNull(result.Policy.ExpiresAtUtc);

        var effective = await service.GetEffectiveAsync();
        Assert.Equal(PlatformMfaMode.DISABLED_TEST_ONLY, effective.Mode);
        Assert.True(effective.EnforcementDisabled);
        Assert.True(effective.PasswordOnlySessionsPermitted);
    }

    [Fact]
    public async Task A_local_non_owner_cannot_reach_the_policy_endpoint_at_all()
    {
        // Two halves, because either one alone would be a false pass. First: the endpoint really
        // does carry the Owner policy — a test that only exercised the service would keep passing
        // if somebody removed the attribute.
        var change = typeof(PlatformMfaPolicyController)
            .GetMethod(nameof(PlatformMfaPolicyController.Change))!;
        var policies = change.GetCustomAttributes<AuthorizeAttribute>()
            .Select(attribute => attribute.Policy).ToArray();
        Assert.Contains(PlatformPolicies.Owner, policies);

        // Second: that policy refuses a fully MFA-authenticated SupportAdmin. Relaxing MFA is not a
        // support action — it is the one change that makes every stolen support credential worth
        // more.
        Assert.False(await AuthorizesAsync(
            OperatorPrincipal(PlatformRole.SupportAdmin, mfa: true), PlatformPolicies.Owner));
        Assert.True(await AuthorizesAsync(
            OperatorPrincipal(PlatformRole.Owner, mfa: true), PlatformPolicies.Owner));
    }

    [Fact]
    public async Task Production_rejects_OPTIONAL()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        await using var context = db.ContextFor(null);
        var service = ServiceFor(context, Production());

        var result = await service.ChangeAsync(
            Request(PlatformMfaMode.OPTIONAL, hours: 2), Principal(owner), null);

        Assert.False(result.Succeeded);
        Assert.True(result.Forbidden);
        Assert.Contains("REQUIRED only", result.Error);
        Assert.Equal(PlatformMfaMode.REQUIRED, (await service.GetEffectiveAsync()).Mode);
    }

    [Fact]
    public async Task Production_rejects_DISABLED_TEST_ONLY_and_says_it_is_unreachable_by_any_route()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        await using var context = db.ContextFor(null);
        var service = ServiceFor(context, Production());

        var result = await service.ChangeAsync(
            Request(PlatformMfaMode.DISABLED_TEST_ONLY, hours: 2), Principal(owner), null);

        Assert.False(result.Succeeded);
        Assert.True(result.Forbidden);
        Assert.Contains("no configuration that makes this reachable", result.Error);
        Assert.Empty(await context.Set<PlatformMfaPolicy>().ToListAsync());
    }

    [Fact]
    public void Production_refuses_to_start_when_it_has_been_classified_as_isolated_test_infrastructure()
    {
        // The single most dangerous configuration in the feature: a staging appsettings copied onto
        // a production host. Merely ignoring the key would boot silently, and the only difference
        // would be that DISABLED_TEST_ONLY had quietly become reachable in production.
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [PlatformMfaPolicyOptions.IsolatedTestInfrastructureKey] = "true"
        }).Build();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlatformMfaPolicyOptions.CreateFromConfiguration(configuration, Environment("Production")));
        Assert.Contains(PlatformMfaPolicyOptions.IsolatedTestInfrastructureKey, exception.Message);
    }

    [Fact]
    public void An_unrecognised_environment_name_classifies_as_Production()
    {
        // Fail-closed by default. The alternative — listing the production names and defaulting to
        // permissive — fails open on exactly the name nobody remembered to add.
        Assert.Equal(PlatformMfaEnvironmentClass.Production,
            PlatformMfaPolicyOptions.ClassifyEnvironment("prod-eu-west"));
        Assert.Equal(PlatformMfaEnvironmentClass.LocalOrTest,
            PlatformMfaPolicyOptions.ClassifyEnvironment("Development"));
        Assert.Equal(PlatformMfaEnvironmentClass.StagingOrUat,
            PlatformMfaPolicyOptions.ClassifyEnvironment("UAT"));
    }

    [Fact]
    public async Task Staging_may_relax_to_OPTIONAL_but_may_not_disable_without_the_isolation_key()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        await using var context = db.ContextFor(null);
        var service = ServiceFor(context, Staging(isolated: false));

        Assert.True((await service.ChangeAsync(
            Request(PlatformMfaMode.OPTIONAL, hours: 3), Principal(owner), null)).Succeeded);

        var disabled = await service.ChangeAsync(
            Request(PlatformMfaMode.DISABLED_TEST_ONLY, hours: 3), Principal(owner), null);
        Assert.False(disabled.Succeeded);
        Assert.Contains(PlatformMfaPolicyOptions.IsolatedTestInfrastructureKey, disabled.Error);
    }

    // ==== the console is not the authority ==========================================================

    [Fact]
    public async Task A_stored_bypass_is_ignored_by_a_production_process_so_the_UI_cannot_override_the_backend()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);

        // Written by a process that was allowed to: a local rig, or a staging database that was
        // later restored onto production. Nothing about the row itself is invalid.
        await using (var local = db.ContextFor(null))
            Assert.True((await ServiceFor(local, Local()).ChangeAsync(
                Request(PlatformMfaMode.DISABLED_TEST_ONLY, hours: 8), Principal(owner), null)).Succeeded);

        await using var production = db.ContextFor(null);
        var effective = await ServiceFor(production, Production()).GetEffectiveAsync();

        // The row still SAYS disabled; production simply does not honour it. The read-path ceiling
        // is what survives a database that arrived from somewhere else.
        Assert.Equal(nameof(PlatformMfaMode.DISABLED_TEST_ONLY),
            (await production.Set<PlatformMfaPolicy>().AsNoTracking().SingleAsync()).Mode);
        Assert.Equal(PlatformMfaMode.REQUIRED, effective.Mode);
        Assert.False(effective.PasswordOnlySessionsPermitted);
    }

    [Fact]
    public async Task The_authorization_gate_still_demands_a_second_factor_when_the_policy_is_REQUIRED()
    {
        // The assertion in AddPlatformPolicies falls back to the amr claim alone when it cannot
        // prove a relaxation. This is the fail-closed half: no HttpContext, no provider, no bypass.
        Assert.False(await AuthorizesAsync(OperatorPrincipal(PlatformRole.Owner, mfa: false),
            PlatformPolicies.PlatformScope));
        Assert.True(await AuthorizesAsync(OperatorPrincipal(PlatformRole.Owner, mfa: true),
            PlatformPolicies.PlatformScope));
    }

    // ==== expiry ====================================================================================

    [Fact]
    public async Task An_expired_bypass_returns_to_REQUIRED_on_a_read_and_records_it_once()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        var clock = new MovableClock(new DateTime(2026, 8, 10, 9, 0, 0, DateTimeKind.Utc));

        await using var context = db.ContextFor(null);
        var service = ServiceFor(context, Local(), clock);
        Assert.True((await service.ChangeAsync(
            Request(PlatformMfaMode.DISABLED_TEST_ONLY, hours: 4), Principal(owner), null)).Succeeded);
        Assert.True((await service.GetEffectiveAsync()).EnforcementDisabled);

        // No job runs, nothing is swept, nobody acts. Only the clock moves.
        clock.Advance(TimeSpan.FromHours(4) + TimeSpan.FromMinutes(1));

        var afterExpiry = await service.GetEffectiveAsync();
        Assert.Equal(PlatformMfaMode.REQUIRED, afterExpiry.Mode);
        Assert.Equal(PlatformMfaMode.DISABLED_TEST_ONLY, afterExpiry.DeclaredMode);
        Assert.True(afterExpiry.BypassExpired);
        Assert.False(afterExpiry.PasswordOnlySessionsPermitted);

        // Read again, twice more: the evidence is emitted once per window, not once per request.
        await service.GetEffectiveAsync();
        await service.GetEffectiveAsync();

        await using var audit = db.ContextFor(null);
        var expiryEvents = await audit.Set<PlatformAuditLog>()
            .Where(entry => entry.Action == PlatformMfaPolicyService.BypassExpiredAction)
            .ToListAsync();
        var expiryEvent = Assert.Single(expiryEvents);
        Assert.Equal(owner.Id, expiryEvent.ActorPlatformUserId);
        Assert.Contains("automatic-expiry", expiryEvent.Metadata);
    }

    // ==== enrolment survives ========================================================================

    [Fact]
    public async Task An_enrolled_TOTP_secret_survives_disable_and_re_enable_untouched()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        string secret;
        await using (var enrollment = db.ContextFor(null))
        {
            var auth = new PlatformAuthService(enrollment, JwtConfiguration(), NullLogger<PlatformAuthService>.Instance);
            secret = (await auth.BeginMfaEnrollmentAsync(owner.Id)).Secret;
            await auth.ConfirmMfaEnrollmentAsync(owner.Id,
                new PlatformMfaEnrollmentConfirmRequest { TotpCode = PlatformTotp.CodeAt(secret, CurrentStep()) });
        }

        await using var context = db.ContextFor(null);
        var service = ServiceFor(context, Local());
        Assert.True((await service.ChangeAsync(
            Request(PlatformMfaMode.DISABLED_TEST_ONLY, hours: 1), Principal(owner), null)).Succeeded);

        var whileDisabled = await context.Set<PlatformMfaCredential>().AsNoTracking()
            .SingleAsync(credential => credential.PlatformUserId == owner.Id);
        Assert.NotNull(whileDisabled.EnabledAtUtc);
        Assert.Equal(secret, whileDisabled.TotpSecret);
        Assert.Equal(PlatformAuthService.RecoveryCodeCount,
            await context.Set<PlatformMfaRecoveryCode>().CountAsync(code => code.PlatformUserId == owner.Id));

        Assert.True((await service.ChangeAsync(
            Request(PlatformMfaMode.REQUIRED, hours: null), Principal(owner), null)).Succeeded);

        // The whole point of requirement 6: re-enabling costs nobody a QR code, an authenticator
        // re-scan or a new set of recovery codes — which is the cost that makes people leave it off.
        await using var after = db.ContextFor(null);
        var restored = await after.Set<PlatformMfaCredential>().AsNoTracking()
            .SingleAsync(credential => credential.PlatformUserId == owner.Id);
        Assert.Equal(secret, restored.TotpSecret);
        Assert.Equal(whileDisabled.EnabledAtUtc, restored.EnabledAtUtc);

        // And the secret still works: the operator is challenged and gets in with the SAME device.
        await using var login = db.ContextFor(null);
        var service2 = new PlatformAuthService(login, JwtConfiguration(), NullLogger<PlatformAuthService>.Instance);
        var challenge = await service2.LoginAsync(new PlatformLoginRequest
        {
            Email = owner.Email, Password = OwnerPassword
        });
        Assert.True(challenge.MfaRequired);
        var completed = await service2.CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
        {
            ChallengeId = challenge.MfaChallengeId!.Value,
            TotpCode = PlatformTotp.CodeAt(secret, CurrentStep() + 1)
        });
        Assert.NotNull(completed.Token);
    }

    // ==== remembered browsers =======================================================================

    [Fact]
    public async Task A_remembered_browser_skips_the_challenge_inside_its_window_and_never_stores_the_raw_token()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        string secret;
        await using (var enrollment = db.ContextFor(null))
        {
            var auth = new PlatformAuthService(enrollment, JwtConfiguration(), NullLogger<PlatformAuthService>.Instance);
            secret = (await auth.BeginMfaEnrollmentAsync(owner.Id)).Secret;
            await auth.ConfirmMfaEnrollmentAsync(owner.Id,
                new PlatformMfaEnrollmentConfirmRequest { TotpCode = PlatformTotp.CodeAt(secret, CurrentStep()) });
        }

        string trustToken;
        await using (var challengeContext = db.ContextFor(null))
        {
            var auth = AuthServiceWithTrust(challengeContext);
            var first = await auth.LoginAsync(new PlatformLoginRequest { Email = owner.Email, Password = OwnerPassword });
            Assert.True(first.MfaRequired);
            var completed = await auth.CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
            {
                ChallengeId = first.MfaChallengeId!.Value,
                TotpCode = PlatformTotp.CodeAt(secret, CurrentStep() + 1),
                RememberBrowser = true
            }, "Mozilla/5.0 (Macintosh; Intel Mac OS X) Chrome/140.0 Safari/537.36");
            trustToken = completed.BrowserTrustToken!;
            Assert.False(string.IsNullOrWhiteSpace(trustToken));
        }

        // Presenting the token: no challenge, and a session that is genuinely MFA-bound.
        await using (var second = db.ContextFor(null))
        {
            var response = await AuthServiceWithTrust(second).LoginAsync(new PlatformLoginRequest
            {
                Email = owner.Email, Password = OwnerPassword, BrowserTrustToken = trustToken
            });
            Assert.False(response.MfaRequired);
            Assert.True(response.BrowserTrustUsed);
            Assert.NotNull(response.Token);
        }

        // Without it, the challenge is back — the trust is bound to the token, not to the account.
        await using (var stranger = db.ContextFor(null))
            Assert.True((await AuthServiceWithTrust(stranger).LoginAsync(new PlatformLoginRequest
            {
                Email = owner.Email, Password = OwnerPassword
            })).MfaRequired);

        // A row read by a backup, a support query or anything under BYPASSRLS yields a digest, not
        // a working second factor for every operator at once.
        await using var inspection = db.ContextFor(null);
        var stored = await inspection.Set<PlatformBrowserTrust>().AsNoTracking().SingleAsync();
        Assert.NotEqual(trustToken, stored.TokenHash);
        Assert.Equal(64, stored.TokenHash.Length);
        Assert.Equal("Chrome on macOS", stored.Label);
    }

    [Fact]
    public async Task Revoking_a_browser_trust_is_refused_immediately_and_kills_the_session_it_minted()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        string secret;
        await using (var enrollment = db.ContextFor(null))
        {
            var auth = new PlatformAuthService(enrollment, JwtConfiguration(), NullLogger<PlatformAuthService>.Instance);
            secret = (await auth.BeginMfaEnrollmentAsync(owner.Id)).Secret;
            await auth.ConfirmMfaEnrollmentAsync(owner.Id,
                new PlatformMfaEnrollmentConfirmRequest { TotpCode = PlatformTotp.CodeAt(secret, CurrentStep()) });
        }

        string trustToken;
        await using (var challengeContext = db.ContextFor(null))
        {
            var auth = AuthServiceWithTrust(challengeContext);
            var first = await auth.LoginAsync(new PlatformLoginRequest { Email = owner.Email, Password = OwnerPassword });
            trustToken = (await auth.CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
            {
                ChallengeId = first.MfaChallengeId!.Value,
                TotpCode = PlatformTotp.CodeAt(secret, CurrentStep() + 1),
                RememberBrowser = true
            })).BrowserTrustToken!;
        }

        string sessionToken;
        long trustId;
        await using (var second = db.ContextFor(null))
        {
            sessionToken = (await AuthServiceWithTrust(second).LoginAsync(new PlatformLoginRequest
            {
                Email = owner.Email, Password = OwnerPassword, BrowserTrustToken = trustToken
            })).Token!;
            trustId = (await second.Set<PlatformBrowserTrust>().AsNoTracking().SingleAsync()).Id;
        }

        await using (var revocation = db.ContextFor(null))
            Assert.True(await BrowserTrustFor(revocation)
                .RevokeAsync(owner.Id, trustId, Principal(owner), null));

        // Immediate on BOTH surfaces. Refusing only the next sign-in would leave the session the
        // trust minted running out its remaining lifetime — the exact window a revocation exists
        // to close.
        await using (var afterRevocation = db.ContextFor(null))
        {
            Assert.True((await AuthServiceWithTrust(afterRevocation).LoginAsync(new PlatformLoginRequest
            {
                Email = owner.Email, Password = OwnerPassword, BrowserTrustToken = trustToken
            })).MfaRequired);
        }

        await using var validation = db.ContextFor(null);
        Assert.False(await new PlatformSessionValidator(validation).IsCurrentAsync(TokenPrincipal(sessionToken)));

        var revokedEvent = await validation.Set<PlatformAuditLog>()
            .SingleAsync(entry => entry.Action == PlatformBrowserTrustService.RevokedAction);
        Assert.Equal(owner.Id, revokedEvent.ActorPlatformUserId);
    }

    // ==== evidence ==================================================================================

    [Fact]
    public async Task Every_policy_change_writes_immutable_audit_evidence_with_actor_reason_expiry_and_environment()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        await using var context = db.ContextFor(null);
        var service = ServiceFor(context, Local());

        Assert.True((await service.ChangeAsync(
            Request(PlatformMfaMode.DISABLED_TEST_ONLY, hours: 6), Principal(owner), null)).Succeeded);
        Assert.True((await service.ChangeAsync(
            Request(PlatformMfaMode.REQUIRED, hours: null), Principal(owner), null)).Succeeded);

        await using var audit = db.ContextFor(null);
        var entries = await audit.Set<PlatformAuditLog>().AsNoTracking()
            .Where(entry => entry.Action.StartsWith("platform.mfa."))
            .ToListAsync();

        var changes = entries.Where(entry => entry.Action == PlatformMfaPolicyService.PolicyChangedAction).ToList();
        Assert.Equal(2, changes.Count);
        Assert.Single(entries, entry => entry.Action == PlatformMfaPolicyService.BypassStartedAction);
        // Manually ended, as distinct from expired. Both restore REQUIRED; only one has a person's
        // name on it, and a reviewer has to be able to tell which.
        Assert.Single(entries, entry => entry.Action == PlatformMfaPolicyService.BypassEndedAction);

        var started = entries.Single(entry => entry.Action == PlatformMfaPolicyService.BypassStartedAction);
        Assert.Equal(owner.Id, started.ActorPlatformUserId);
        Assert.Equal(PlatformAuditResults.Success, started.Result);
        Assert.Contains(ReasonThatIsLongEnough, started.Metadata);
        Assert.Contains("\"expiresAtUtc\"", started.Metadata);
        Assert.Contains("Development", started.Metadata);
    }

    [Fact]
    public async Task A_refused_change_is_audited_as_a_failure_before_anything_is_written()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        await using var context = db.ContextFor(null);

        await ServiceFor(context, Production()).ChangeAsync(
            Request(PlatformMfaMode.DISABLED_TEST_ONLY, hours: 1), Principal(owner), null);

        await using var audit = db.ContextFor(null);
        var refusal = await audit.Set<PlatformAuditLog>()
            .SingleAsync(entry => entry.Action == PlatformMfaPolicyService.PolicyChangeRefusedAction);
        Assert.Equal(PlatformAuditResults.Failure, refusal.Result);
        Assert.Contains("DISABLED_TEST_ONLY", refusal.Metadata);
    }

    // ==== the controls on the change itself =========================================================

    [Theory]
    [InlineData("wrong-password", ReasonThatIsLongEnough, "DISABLE PLATFORM MFA IN TEST", 4, "password is not correct")]
    [InlineData(OwnerPassword, "too short", "DISABLE PLATFORM MFA IN TEST", 4, "at least 20 characters")]
    [InlineData(OwnerPassword, ReasonThatIsLongEnough, "disable platform mfa in test", 4, "Type DISABLE PLATFORM MFA IN TEST")]
    [InlineData(OwnerPassword, ReasonThatIsLongEnough, "DISABLE PLATFORM MFA IN TEST", 0, "An expiry is required")]
    [InlineData(OwnerPassword, ReasonThatIsLongEnough, "DISABLE PLATFORM MFA IN TEST", 48, "maximum bypass duration")]
    public async Task Relaxing_enforcement_requires_every_control_and_names_the_missing_one(
        string password, string reason, string confirmation, int hours, string expectedMessage)
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        await using var context = db.ContextFor(null);
        var service = ServiceFor(context, Local());

        var result = await service.ChangeAsync(new ChangePlatformMfaPolicyRequest
        {
            Mode = nameof(PlatformMfaMode.DISABLED_TEST_ONLY),
            CurrentPassword = password,
            Reason = reason,
            Confirmation = confirmation,
            DurationHours = hours == 0 ? null : hours
        }, Principal(owner), null);

        Assert.False(result.Succeeded);
        Assert.Contains(expectedMessage, result.Error);
        Assert.Equal(PlatformMfaMode.REQUIRED, (await service.GetEffectiveAsync()).Mode);
    }

    // ==== startup guard =============================================================================

    [Fact]
    public async Task A_production_host_refuses_to_start_on_a_stored_bypass()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        await using (var local = db.ContextFor(null))
            await ServiceFor(local, Local()).ChangeAsync(
                Request(PlatformMfaMode.DISABLED_TEST_ONLY, hours: 8), Principal(owner), null);

        // BackgroundServiceExceptionBehavior.Ignore is set in Program.cs, which is why this is an
        // IHostedService: a BackgroundService throwing here would be swallowed and the API would
        // serve traffic with enforcement off and a stack trace in the log.
        var options = Production();
        await using var provider = new ServiceCollection()
            .AddScoped(_ => db.ContextFor(null))
            .BuildServiceProvider();
        var guard = new PlatformMfaPolicyStartupGuard(
            provider, options, NullLogger<PlatformMfaPolicyStartupGuard>.Instance);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => guard.StartAsync(CancellationToken.None));
        Assert.Contains("will not start with MFA enforcement relaxed", exception.Message);
    }

    [Fact]
    public async Task A_local_host_starts_normally_on_the_same_row()
    {
        using var db = new TestDb();
        var owner = await SeedOwnerAsync(db);
        await using (var local = db.ContextFor(null))
            await ServiceFor(local, Local()).ChangeAsync(
                Request(PlatformMfaMode.DISABLED_TEST_ONLY, hours: 8), Principal(owner), null);

        await using var provider = new ServiceCollection()
            .AddScoped(_ => db.ContextFor(null))
            .BuildServiceProvider();
        var guard = new PlatformMfaPolicyStartupGuard(
            provider, Local(), NullLogger<PlatformMfaPolicyStartupGuard>.Instance);

        await guard.StartAsync(CancellationToken.None);
    }

    // ==== harness ===================================================================================

    private static ChangePlatformMfaPolicyRequest Request(PlatformMfaMode mode, int? hours) => new()
    {
        Mode = mode.ToString(),
        CurrentPassword = OwnerPassword,
        Reason = ReasonThatIsLongEnough,
        Confirmation = PlatformMfaPolicyOptions.ConfirmationPhraseFor(mode),
        DurationHours = hours
    };

    private static PlatformMfaPolicyService ServiceFor(
        ErpRfqAutomationContext context, PlatformMfaPolicyOptions options, TimeProvider? clock = null) =>
        new(context, options, new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<PlatformMfaPolicyService>.Instance, clock);

    private static PlatformBrowserTrustService BrowserTrustFor(ErpRfqAutomationContext context) =>
        new(context, Local(), new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<PlatformBrowserTrustService>.Instance);

    private static PlatformAuthService AuthServiceWithTrust(ErpRfqAutomationContext context) =>
        new(context, JwtConfiguration(), NullLogger<PlatformAuthService>.Instance,
            ServiceFor(context, Local()), BrowserTrustFor(context));

    private static PlatformMfaPolicyOptions Local() => Options("Development", isolated: false);

    private static PlatformMfaPolicyOptions Staging(bool isolated) => Options("Staging", isolated);

    private static PlatformMfaPolicyOptions Production() => Options("Production", isolated: false);

    private static PlatformMfaPolicyOptions Options(string environmentName, bool isolated) =>
        PlatformMfaPolicyOptions.CreateFromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [PlatformMfaPolicyOptions.IsolatedTestInfrastructureKey] = isolated ? "true" : "false"
            }).Build(),
            Environment(environmentName));

    private static IHostEnvironment Environment(string name) => new StubEnvironment(name);

    private static IConfiguration JwtConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["Jwt:Key"] = "tenant-signing-key-that-is-at-least-32-bytes",
            ["Jwt:Issuer"] = "nexora-tests",
            ["Jwt:Audience"] = "RFQ"
        }).Build();

    private static long CurrentStep() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / PlatformTotp.StepSeconds;

    private static async Task<PlatformUser> SeedOwnerAsync(TestDb db, string email = "owner@nexora.test")
    {
        await using var context = db.ContextFor(null);
        var user = new PlatformUser
        {
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(OwnerPassword),
            PlatformRole = PlatformRole.Owner,
            IsActive = true,
            CreatedOn = DateTime.UtcNow,
            CreatedBy = "test"
        };
        context.Set<PlatformUser>().Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    private static ClaimsPrincipal Principal(PlatformUser user) => new(new ClaimsIdentity(
    [
        new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
        new Claim("email", user.Email),
        new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
    ], PlatformAuthConstants.Scheme));

    private static ClaimsPrincipal TokenPrincipal(string token) => new(new ClaimsIdentity(
        new JwtSecurityTokenHandler().ReadJwtToken(token).Claims, PlatformAuthConstants.Scheme));

    private static ClaimsPrincipal OperatorPrincipal(PlatformRole role, bool mfa)
    {
        var claims = new List<Claim>
        {
            new(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue),
            new(PlatformAuthConstants.PlatformRoleClaim, role.ToString())
        };
        if (mfa)
            claims.Add(new Claim(PlatformAuthConstants.AuthenticationMethodClaim,
                PlatformAuthConstants.MfaAuthenticationMethod));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, PlatformAuthConstants.Scheme));
    }

    /// <summary>Authorises a bare principal — deliberately with no HttpContext resource, which is
    /// the fail-closed path: without one the assertion cannot consult the policy and falls back to
    /// the claim alone.</summary>
    private static async Task<bool> AuthorizesAsync(ClaimsPrincipal principal, string policy)
    {
        using var provider = new ServiceCollection().AddLogging()
            .AddAuthorization(options => options.AddPlatformPolicies()).BuildServiceProvider();
        return (await provider.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, null, policy)).Succeeded;
    }

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "ERP_RFQ_Automation";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class MovableClock(DateTime start) : TimeProvider
    {
        private DateTimeOffset _now = new(start, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
