using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// "Remember this browser", as a customer-set policy rather than a deployment setting.
///
/// <para>Two product decisions are under test here and they pull in opposite directions. The window
/// may now reach a month, which is a real relaxation of the second factor; and browser trust can be
/// switched off entirely, which is the control that makes the relaxation supportable. Every test
/// below defends one of the three things that were argued to make the longer window acceptable — the
/// Owner-only audited change path, the bound that survives a hand-written UPDATE, and a switch that
/// takes effect on trusts that already exist.</para>
/// </summary>
public sealed class PlatformBrowserTrustPolicyTests
{
    private const string OwnerPassword = "valid-password-123";

    private const string ReasonThatIsLongEnough =
        "Field engineers sign in from two fixed laptops all month; the security review approved a 30-day window.";

    // ==== the switch, and where it has to be enforced ================================================

    [Fact]
    public async Task Disabling_browser_trust_refuses_a_trust_that_was_ALREADY_granted()
    {
        using var db = new TestDb();
        var owner = await SeedOperatorAsync(db);

        string token;
        await using (var issuing = db.ContextFor(null))
        {
            var grant = await BrowserTrustFor(issuing).IssueAsync(owner.Id, "Chrome/140.0", null);
            token = Assert.IsType<PlatformBrowserTrustGrant>(grant).Token;

            // Live before the policy changes, so the assertion after it means something.
            Assert.NotNull(await BrowserTrustFor(issuing).RedeemAsync(owner.Id, token));
        }

        await using (var change = db.ContextFor(null))
            Assert.True((await PolicyFor(change).ChangeAsync(
                Request(browserTrustEnabled: false), Principal(owner), null)).Succeeded, "the policy change");

        await using var after = db.ContextFor(null);

        // The whole point. Gating only issuance would have left this token working for the rest of
        // its window — an Owner who switched the control off at 10am would have switched it off for
        // the operators who had not used it and left it running for everyone who had.
        Assert.Null(await BrowserTrustFor(after).RedeemAsync(owner.Id, token));

        // And nothing new is minted either.
        Assert.Null(await BrowserTrustFor(after).IssueAsync(owner.Id, "Chrome/140.0", null));

        // The row is still there, unrevoked: disabling is not deletion. Switching the control back on
        // restores whatever window these trusts had left, which is what stops a half-hour experiment
        // costing every operator a fresh challenge cycle.
        var stored = await after.Set<PlatformBrowserTrust>().AsNoTracking().SingleAsync();
        Assert.Null(stored.RevokedAtUtc);
    }

    [Fact]
    public async Task A_sign_in_from_a_trusted_browser_is_challenged_again_once_the_switch_is_off()
    {
        using var db = new TestDb();
        var owner = await SeedOperatorAsync(db);
        var secret = await EnrollAsync(db, owner.Id);

        string trustToken;
        await using (var challenge = db.ContextFor(null))
        {
            var auth = AuthServiceWithTrust(challenge);
            var first = await auth.LoginAsync(new PlatformLoginRequest { Email = owner.Email, Password = OwnerPassword });
            Assert.True(first.MfaRequired);
            // The offer, and its duration, ride on the challenge response: at this point the operator
            // holds no token at all, so the effective-policy endpoint is unreachable to them.
            Assert.True(first.BrowserTrustOffered);
            Assert.Equal(PlatformMfaPolicyOptions.DefaultBrowserTrustHours, first.BrowserTrustHours);

            trustToken = (await auth.CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
            {
                ChallengeId = first.MfaChallengeId!.Value,
                TotpCode = PlatformTotp.CodeAt(secret, CurrentStep() + 1),
                RememberBrowser = true
            })).BrowserTrustToken!;
            Assert.False(string.IsNullOrWhiteSpace(trustToken));
        }

        await using (var change = db.ContextFor(null))
            Assert.True((await PolicyFor(change).ChangeAsync(
                Request(browserTrustEnabled: false), Principal(owner), null)).Succeeded);

        await using var after = db.ContextFor(null);
        var response = await AuthServiceWithTrust(after).LoginAsync(new PlatformLoginRequest
        {
            Email = owner.Email, Password = OwnerPassword, BrowserTrustToken = trustToken
        });

        Assert.True(response.MfaRequired);
        // …and the login screen is told not to offer the checkbox again, rather than offering a
        // control the platform will silently decline to honour.
        Assert.False(response.BrowserTrustOffered);
        Assert.Equal(0, response.BrowserTrustHours);
    }

    [Fact]
    public async Task Completing_a_challenge_with_remember_me_while_the_switch_is_off_still_signs_in_and_grants_nothing()
    {
        using var db = new TestDb();
        var owner = await SeedOperatorAsync(db);
        var secret = await EnrollAsync(db, owner.Id);

        await using (var change = db.ContextFor(null))
            Assert.True((await PolicyFor(change).ChangeAsync(
                Request(browserTrustEnabled: false), Principal(owner), null)).Succeeded);

        await using var context = db.ContextFor(null);
        var auth = AuthServiceWithTrust(context);
        var challenge = await auth.LoginAsync(new PlatformLoginRequest { Email = owner.Email, Password = OwnerPassword });
        var completed = await auth.CompleteMfaChallengeAsync(new PlatformMfaChallengeRequest
        {
            ChallengeId = challenge.MfaChallengeId!.Value,
            TotpCode = PlatformTotp.CodeAt(secret, CurrentStep() + 1),
            RememberBrowser = true
        });

        // A ticked box the platform no longer honours must not fail the sign-in — the operator got
        // their session; they simply have no trust, and the browser stores nothing.
        Assert.NotNull(completed.Token);
        Assert.Null(completed.BrowserTrustToken);
        Assert.Empty(await context.Set<PlatformBrowserTrust>().ToListAsync());
    }

    // ==== the bound ==================================================================================

    [Theory]
    [InlineData(0, false)]
    [InlineData(7, false)]
    [InlineData(721, false)]
    [InlineData(8, true)]
    [InlineData(12, true)]
    [InlineData(720, true)]
    public async Task The_window_is_accepted_only_between_eight_hours_and_thirty_days(int hours, bool permitted)
    {
        using var db = new TestDb();
        var owner = await SeedOperatorAsync(db);
        await using var context = db.ContextFor(null);
        var service = PolicyFor(context);

        var result = await service.ChangeAsync(Request(browserTrustHours: hours), Principal(owner), null);

        Assert.Equal(permitted, result.Succeeded);
        if (permitted)
        {
            Assert.Equal(hours, result.Policy!.BrowserTrustHours);
            return;
        }

        // The refusal names the value AND both bounds. "Invalid duration" leaves an Owner guessing
        // which end they hit, and 8 versus 720 is not a guess anyone should have to make.
        Assert.Contains(hours.ToString(), result.Error);
        Assert.Contains("8 hours", result.Error);
        Assert.Contains("720 hours", result.Error);
        Assert.False(result.Forbidden);
        Assert.False(result.Conflict);
        Assert.Empty(await context.Set<PlatformMfaPolicy>().ToListAsync());
    }

    [Fact]
    public void Configuration_outside_the_range_still_stops_the_deployment_rather_than_being_clamped()
    {
        // The ceiling moved; the fail-fast contract did not. An operator who wrote 721 believes they
        // have 30 days and one hour, and a silent clamp means the belief and the system disagree with
        // nobody told.
        var refused = Assert.Throws<InvalidOperationException>(() => OptionsWith("Development", browserTrustHours: 721));
        Assert.Contains(PlatformMfaPolicyOptions.BrowserTrustHoursKey, refused.Message);
        Assert.Contains("8–720", refused.Message);

        Assert.Throws<InvalidOperationException>(() => OptionsWith("Development", browserTrustHours: 7));

        // And the new ceiling really is reachable from configuration, not only from the screen.
        Assert.Equal(720, OptionsWith("Development", browserTrustHours: 720).BrowserTrustHours);
    }

    [Fact]
    public async Task The_database_refuses_an_out_of_range_window_written_around_the_service()
    {
        // The service is not the only guard. This is the UPDATE that never came through it — a
        // hand-edited row, a restore, a future admin script — and "remember this browser forever" is
        // exactly the value somebody would set by hand.
        using var db = new TestDb();
        var owner = await SeedOperatorAsync(db);
        await using var context = db.ContextFor(null);
        Assert.True((await PolicyFor(context).ChangeAsync(
            Request(browserTrustHours: 720), Principal(owner), null)).Succeeded);

        // The table name comes from the model rather than a literal, so this stays correct on both the
        // PostgreSQL runtime and the SQLite test lane — the same technique
        // PlatformEmailSettingsService.ClearUndecryptableSecretsAsync uses.
        var table = context.Model.FindEntityType(typeof(PlatformMfaPolicy))!.GetTableName();
        var refused = await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(
            () => context.Database.ExecuteSqlRawAsync(
                $"UPDATE \"{table}\" SET \"BrowserTrustHours\" = 8760 WHERE \"Id\" = 1;"));
        Assert.Contains("CK_PlatformMfaPolicies_BrowserTrustHours", refused.Message);
    }

    // ==== precedence =================================================================================

    [Fact]
    public async Task The_policy_row_wins_over_appsettings_and_the_seed_answers_only_until_there_is_one()
    {
        using var db = new TestDb();
        var owner = await SeedOperatorAsync(db);
        var clock = new FixedClock(new DateTime(2026, 8, 11, 9, 0, 0, DateTimeKind.Utc));

        // A deployment configured for the shortest permitted window.
        var options = OptionsWith("Development", browserTrustHours: 8);

        await using (var beforeAnyRow = db.ContextFor(null))
        {
            var seeded = await BrowserTrustFor(beforeAnyRow, options, clock).GetSettingsAsync();
            Assert.True(seeded.Enabled);
            Assert.Equal(8, seeded.Hours);
            Assert.False(seeded.FromPolicyRow);
        }

        await using (var change = db.ContextFor(null))
            Assert.True((await PolicyFor(change, options, clock).ChangeAsync(
                Request(browserTrustHours: 720), Principal(owner), null)).Succeeded);

        await using var after = db.ContextFor(null);
        var resolved = await BrowserTrustFor(after, options, clock).GetSettingsAsync();
        Assert.Equal(720, resolved.Hours);
        Assert.True(resolved.FromPolicyRow);

        // And it is the resolved window that a real trust is stamped with — not the configured seed,
        // which is the bug this precedence exists to prevent.
        var grant = await BrowserTrustFor(after, options, clock).IssueAsync(owner.Id, null, null);
        Assert.Equal(clock.GetUtcNow().UtcDateTime.AddHours(720), grant!.ExpiresAtUtc);

        // The screen reads the same number back, rather than echoing the process's own configuration.
        var dto = await PolicyFor(after, options, clock).GetAsync();
        Assert.Equal(720, dto.BrowserTrustHours);
        Assert.True(dto.BrowserTrustFromPolicyRow);
        Assert.Equal(PlatformMfaPolicyOptions.MinBrowserTrustHours, dto.MinBrowserTrustHours);
        Assert.Equal(PlatformMfaPolicyOptions.MaxBrowserTrustHours, dto.MaxBrowserTrustHours);
    }

    [Fact]
    public async Task A_change_that_says_nothing_about_browser_trust_leaves_it_exactly_as_it_was()
    {
        using var db = new TestDb();
        var owner = await SeedOperatorAsync(db);
        // Configured for 8 hours, so a default-shaped write to the row would be visible as 12.
        var options = OptionsWith("Development", browserTrustHours: 8);
        await using var context = db.ContextFor(null);
        var service = PolicyFor(context, options);

        // First change ever: the row is created and must INHERIT the configured seed, not the
        // entity's default. Otherwise switching MFA to OPTIONAL for an afternoon would silently
        // reset a deliberately configured window and nothing would mention it.
        Assert.True((await service.ChangeAsync(
            Request(mode: PlatformMfaMode.OPTIONAL, hours: 2), Principal(owner), null)).Succeeded);
        Assert.Equal(8, (await service.GetAsync()).BrowserTrustHours);

        Assert.True((await service.ChangeAsync(
            Request(browserTrustEnabled: false), Principal(owner), null)).Succeeded);
        // Disabling says nothing about the window, so the window does not move.
        var afterDisable = await service.GetAsync();
        Assert.False(afterDisable.BrowserTrustEnabled);
        Assert.Equal(8, afterDisable.BrowserTrustHours);

        Assert.True((await service.ChangeAsync(
            Request(browserTrustHours: 168), Principal(owner), null)).Succeeded);
        // …and setting the window says nothing about the switch, so the switch does not move either.
        var afterWindow = await service.GetAsync();
        Assert.False(afterWindow.BrowserTrustEnabled);
        Assert.Equal(168, afterWindow.BrowserTrustHours);
    }

    // ==== the change path ============================================================================

    [Fact]
    public async Task A_stale_expected_version_is_a_conflict_and_changes_nothing()
    {
        using var db = new TestDb();
        var owner = await SeedOperatorAsync(db);
        await using var context = db.ContextFor(null);
        var service = PolicyFor(context);

        var first = await service.ChangeAsync(Request(browserTrustHours: 24), Principal(owner), null);
        Assert.True(first.Succeeded);
        var staleVersion = first.Policy!.Version - 1;

        var conflicted = await service.ChangeAsync(
            Request(browserTrustHours: 720, expectedVersion: staleVersion), Principal(owner), null);

        Assert.False(conflicted.Succeeded);
        Assert.True(conflicted.Conflict);
        // Two Owners widening the window from two tabs is exactly the case where last-write-wins
        // would leave the LONGER window in force with the shorter one's audit row beside it.
        Assert.Equal(24, (await service.GetAsync()).BrowserTrustHours);
    }

    [Fact]
    public async Task Widening_the_window_is_audited_with_the_before_and_the_after()
    {
        using var db = new TestDb();
        var owner = await SeedOperatorAsync(db);
        await using var context = db.ContextFor(null);

        Assert.True((await PolicyFor(context).ChangeAsync(
            Request(browserTrustHours: 720, browserTrustEnabled: true), Principal(owner), null)).Succeeded);

        await using var audit = db.ContextFor(null);
        var entry = await audit.Set<PlatformAuditLog>().AsNoTracking()
            .SingleAsync(row => row.Action == PlatformMfaPolicyService.PolicyChangedAction);

        Assert.Equal(owner.Id, entry.ActorPlatformUserId);
        Assert.Equal(PlatformAuditResults.Success, entry.Result);
        // A row that recorded only the new window would let a reviewer see 720 hours without being
        // able to tell whether THIS change is what made it so.
        Assert.Contains("\"browserTrustHours\":720", entry.Metadata);
        Assert.Contains("\"previousBrowserTrustHours\":12", entry.Metadata);
        Assert.Contains("\"browserTrustChanged\":true", entry.Metadata);
        Assert.Contains(ReasonThatIsLongEnough, entry.Metadata);
    }

    [Fact]
    public async Task Issuing_a_trust_records_the_window_and_which_authority_set_it()
    {
        using var db = new TestDb();
        var owner = await SeedOperatorAsync(db);
        await using var context = db.ContextFor(null);
        Assert.True((await PolicyFor(context).ChangeAsync(
            Request(browserTrustHours: 720), Principal(owner), null)).Succeeded);

        await BrowserTrustFor(context).IssueAsync(owner.Id, "Chrome/140.0 Windows", null);

        await using var audit = db.ContextFor(null);
        var entry = await audit.Set<PlatformAuditLog>().AsNoTracking()
            .SingleAsync(row => row.Action == PlatformBrowserTrustService.CreatedAction);
        Assert.Contains("\"trustHours\":720", entry.Metadata);
        // Whether an Owner chose the window or the deployment default did — the difference between
        // a decision and an oversight, a month after the fact.
        Assert.Contains("policy-row", entry.Metadata);
    }

    // ==== revocation =================================================================================

    [Fact]
    public async Task Revoke_all_revokes_only_the_callers_own_remembered_browsers()
    {
        using var db = new TestDb();
        var mine = await SeedOperatorAsync(db, "mine@nexora.test");
        var theirs = await SeedOperatorAsync(db, "theirs@nexora.test");

        string myLaptop, myPhone, theirLaptop;
        await using (var issuing = db.ContextFor(null))
        {
            var trusts = BrowserTrustFor(issuing);
            myLaptop = (await trusts.IssueAsync(mine.Id, "Chrome/140.0 Mac OS X", null))!.Token;
            myPhone = (await trusts.IssueAsync(mine.Id, "Safari/605.1 Android", null))!.Token;
            theirLaptop = (await trusts.IssueAsync(theirs.Id, "Firefox/130.0 Windows", null))!.Token;
        }

        int revoked;
        await using (var sweep = db.ContextFor(null))
            revoked = await BrowserTrustFor(sweep).RevokeAllAsync(
                mine.Id, PlatformBrowserTrustService.OperatorRevokedAllReason, Principal(mine), null);

        Assert.Equal(2, revoked);

        await using var after = db.ContextFor(null);
        var trustsAfter = BrowserTrustFor(after);
        Assert.Null(await trustsAfter.RedeemAsync(mine.Id, myLaptop));
        Assert.Null(await trustsAfter.RedeemAsync(mine.Id, myPhone));
        // One operator revoking every OTHER operator's remembered browsers would be a denial of
        // service on the whole plane dressed up as a security action.
        Assert.NotNull(await trustsAfter.RedeemAsync(theirs.Id, theirLaptop));
        Assert.Empty(await trustsAfter.ListAsync(mine.Id));
        Assert.Single(await trustsAfter.ListAsync(theirs.Id));

        var entry = await after.Set<PlatformAuditLog>().AsNoTracking()
            .SingleAsync(row => row.Action == PlatformBrowserTrustService.RevokedAction);
        Assert.Equal(mine.Id, entry.ActorPlatformUserId);
        Assert.Contains("\"revokedCount\":2", entry.Metadata);
        Assert.Contains(PlatformBrowserTrustService.OperatorRevokedAllReason, entry.Metadata);
    }

    [Fact]
    public async Task Revoking_nothing_is_not_an_event()
    {
        using var db = new TestDb();
        var owner = await SeedOperatorAsync(db);
        await using var context = db.ContextFor(null);

        Assert.Equal(0, await BrowserTrustFor(context).RevokeAllAsync(
            owner.Id, PlatformBrowserTrustService.OperatorRevokedAllReason, Principal(owner), null));

        // An append-only audit table that grows by one row every time somebody opens a screen and
        // clicks a button that did nothing is an audit table nobody reads.
        Assert.Empty(await context.Set<PlatformAuditLog>()
            .Where(row => row.Action == PlatformBrowserTrustService.RevokedAction).ToListAsync());
    }

    // ==== harness ====================================================================================

    private static ChangePlatformMfaPolicyRequest Request(
        PlatformMfaMode mode = PlatformMfaMode.REQUIRED,
        int? hours = null,
        bool? browserTrustEnabled = null,
        int? browserTrustHours = null,
        long? expectedVersion = null) => new()
    {
        Mode = mode.ToString(),
        CurrentPassword = OwnerPassword,
        Reason = ReasonThatIsLongEnough,
        Confirmation = PlatformMfaPolicyOptions.ConfirmationPhraseFor(mode),
        DurationHours = hours,
        BrowserTrustEnabled = browserTrustEnabled,
        BrowserTrustHours = browserTrustHours,
        ExpectedVersion = expectedVersion
    };

    private static PlatformMfaPolicyService PolicyFor(
        ErpRfqAutomationContext context, PlatformMfaPolicyOptions? options = null, TimeProvider? clock = null) =>
        new(context, options ?? Local(),
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<PlatformMfaPolicyService>.Instance, clock);

    private static PlatformBrowserTrustService BrowserTrustFor(
        ErpRfqAutomationContext context, PlatformMfaPolicyOptions? options = null, TimeProvider? clock = null) =>
        new(context, options ?? Local(),
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            NullLogger<PlatformBrowserTrustService>.Instance, clock);

    private static PlatformAuthService AuthServiceWithTrust(ErpRfqAutomationContext context) =>
        new(context, JwtConfiguration(), NullLogger<PlatformAuthService>.Instance,
            PolicyFor(context), BrowserTrustFor(context));

    private static PlatformMfaPolicyOptions Local() => OptionsWith("Development");

    private static PlatformMfaPolicyOptions OptionsWith(string environmentName, int? browserTrustHours = null)
    {
        var values = new Dictionary<string, string?>
        {
            [PlatformMfaPolicyOptions.IsolatedTestInfrastructureKey] = "false"
        };
        if (browserTrustHours is { } hours)
            values[PlatformMfaPolicyOptions.BrowserTrustHoursKey] = hours.ToString();

        return PlatformMfaPolicyOptions.CreateFromConfiguration(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build(),
            new StubEnvironment(environmentName));
    }

    private static IConfiguration JwtConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Testing",
            ["Jwt:Key"] = "tenant-signing-key-that-is-at-least-32-bytes",
            ["Jwt:Issuer"] = "nexora-tests",
            ["Jwt:Audience"] = "RFQ"
        }).Build();

    private static long CurrentStep() => DateTimeOffset.UtcNow.ToUnixTimeSeconds() / PlatformTotp.StepSeconds;

    private static async Task<string> EnrollAsync(TestDb db, long platformUserId)
    {
        await using var context = db.ContextFor(null);
        var auth = new PlatformAuthService(context, JwtConfiguration(), NullLogger<PlatformAuthService>.Instance);
        var secret = (await auth.BeginMfaEnrollmentAsync(platformUserId)).Secret;
        await auth.ConfirmMfaEnrollmentAsync(platformUserId,
            new PlatformMfaEnrollmentConfirmRequest { TotpCode = PlatformTotp.CodeAt(secret, CurrentStep()) });
        return secret;
    }

    private static async Task<PlatformUser> SeedOperatorAsync(TestDb db, string email = "owner@nexora.test")
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

    private sealed class StubEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "ERP_RFQ_Automation";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    private sealed class FixedClock(DateTime at) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(at, TimeSpan.Zero);
    }
}
