using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Security.PasswordReset;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A tenant user who forgets their password must be able to get back in without anybody at the
/// platform touching their credential.
///
/// <para><b>What was wrong.</b> There was no recovery path at all. The only way back into an
/// account was for somebody with database access to overwrite
/// <c>public."Users"."Password_Hash"</c> — which puts a working credential for a live account in
/// an operator's hands, the exact defect <c>TenantActivationInvitationTests</c> pins as fixed for
/// the FIRST credential. <c>ActivateAccountPage</c> meanwhile told users to "use forgot password
/// on the sign-in page", which did not exist. These tests pin the replacement: a single-use,
/// short-lived, hash-at-rest reset link, and a request endpoint that discloses nothing.</para>
///
/// <para><b>Harness note.</b> <see cref="TestDb"/> builds the SQLite schema from the real EF
/// model via <c>EnsureCreated</c>, so these tests require the context to declare
/// <c>DbSet&lt;PasswordResetToken&gt;</c> and to call
/// <c>modelBuilder.ApplyPasswordResetModel()</c> from <c>OnModelCreatingPartial</c>. Without those
/// two lines the table does not exist and every test here fails at the first query.</para>
/// </summary>
public sealed class TenantPasswordResetTests
{
    private const string GoodPassword = "Dammam-Causeway-5#p";
    private const string KnownPassword = "the-password-they-forgot";

    // ==== harness =============================================================================

    /// <summary>
    /// Captures whatever the module hands to the transport, so the email itself can be inspected
    /// the way a recipient would see it.
    /// </summary>
    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public bool Accept { get; set; } = true;
        public Exception? Throw { get; set; }

        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            if (Throw is not null) throw Throw;
            Sent.Add(message);
            return Task.FromResult<EmailDeliveryReceipt?>(
                Accept ? new EmailDeliveryReceipt("capture", "captured-1", DateTimeOffset.UtcNow) : null);
        }
    }

    private static PasswordResetService Service(
        ErpRfqAutomationContext context,
        IEmailSender? sender = null,
        TenantOnboardingOptions? options = null,
        TimeProvider? clock = null,
        ILoginAttemptThrottle? throttle = null) =>
        new(context,
            sender ?? new CapturingEmailSender(),
            throttle ?? new LoginAttemptThrottle(
                context, new LoginThrottleOptions(), NullLogger<LoginAttemptThrottle>.Instance),
            Options.Create(new NotificationsOptions { AppBaseUrl = "https://app.nexora.test" }),
            Options.Create(options ?? new TenantOnboardingOptions()),
            NullLogger<PasswordResetService>.Instance,
            clock);

    /// <summary>
    /// The shape a live tenant has: a tenant, its primary business unit, an Owner-rank role and a
    /// user holding it with a password they are about to forget.
    /// </summary>
    private static async Task<(Tenant tenant, User user)> SeedAsync(
        TestDb db, string slug, string email, bool active = true)
    {
        await using var context = db.ContextFor(null);

        var businessUnit = new BusinessUnit
        {
            BusinessUnitCode = slug.ToUpperInvariant(),
            BusinessUnitName = $"Tenant {slug}",
            Description = "Seeded for password-reset tests",
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
            FirstName = "Layla",
            LastName = "Al Harbi",
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(KnownPassword),
            ImageUrl = string.Empty,
            RoleId = role.SetupId,
            Buid = businessUnit.Id,
            IsActive = active,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();

        return (tenant, user);
    }

    /// <summary>
    /// Runs the request path and digs the cleartext token back out of the captured email — the
    /// only place it exists. Doing it this way rather than returning it from the service is
    /// itself part of what is being tested: there is no API that hands a caller a live token.
    /// </summary>
    private static async Task<string?> RequestAndReadTokenAsync(
        TestDb db, string email, string? ip = "203.0.113.9",
        TenantOnboardingOptions? options = null, TimeProvider? clock = null)
    {
        var sender = new CapturingEmailSender();
        await using var context = db.ContextFor(null);
        await Service(context, sender, options, clock).RequestResetAsync(email, ip);

        if (sender.Sent.Count == 0) return null;

        var body = sender.Sent[^1].TextBody!;
        var marker = "https://app.nexora.test/reset-password/";
        var start = body.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, "The reset email did not carry a link the page can open.");

        var token = body[(start + marker.Length)..];
        var end = token.IndexOfAny(['\n', '\r', ' ']);
        return end < 0 ? token : token[..end];
    }

    // ==== the property with no precedent in this codebase =====================================

    /// <summary>
    /// THE enumeration test. A public form that answers differently for a known and an unknown
    /// address is a directory of a customer's staff, handed to anybody who asks — one request at
    /// a time, against a leaked address list, at whatever rate the limiter allows.
    ///
    /// <para>Asserted at the SERVICE boundary here and again at the HTTP boundary in
    /// <c>TenantPasswordResetEndpointTests</c>, because they are different failures: the service
    /// could leak by throwing, and the controller could leak by branching.</para>
    /// </summary>
    [Fact]
    public async Task An_unknown_address_and_a_known_one_are_indistinguishable_to_the_caller()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-enum", "layla@customer.example");

        await using var context = db.ContextFor(null);
        var sender = new CapturingEmailSender();
        var service = Service(context, sender);

        // Every shape a prober can present. None throws, none returns anything, and the method
        // signature is what guarantees the second half: RequestResetAsync is Task, not
        // Task<something>, so there is no result for a controller to accidentally reveal.
        foreach (var probe in new[]
                 {
                     "nobody@customer.example",           // no such account
                     "layla@another-company.example",     // right local part, wrong domain
                     "not-an-email-at-all",               // not an address
                     "",                                  // empty
                     new string('x', 400) + "@x.example"  // absurd
                 })
        {
            await service.RequestResetAsync(probe, "198.51.100.2");
        }

        // Nothing was sent and nothing was minted for any of them…
        Assert.Empty(sender.Sent);
        Assert.Empty(await context.Set<PasswordResetToken>().ToListAsync());

        // …and the real address behaves identically at the boundary while doing the work.
        await service.RequestResetAsync(user.Email, "198.51.100.2");
        Assert.Single(sender.Sent);
        Assert.Single(await context.Set<PasswordResetToken>().ToListAsync());
    }

    /// <summary>
    /// The three states a REAL address can be in that would each be a tempting place to answer
    /// differently. A deactivated account is the sharpest: telling a caller "that account is
    /// disabled" confirms the address AND leaks an administrative decision to a stranger.
    /// </summary>
    [Fact]
    public async Task A_deactivated_account_gets_no_link_and_the_caller_cannot_tell()
    {
        using var db = new TestDb();
        await SeedAsync(db, "reset-inactive", "dormant@customer.example", active: false);

        await using var context = db.ContextFor(null);
        var sender = new CapturingEmailSender();

        await Service(context, sender).RequestResetAsync("dormant@customer.example", null);

        // No token, no email — a deactivated user must not be able to bring themselves back with
        // a link they mailed to themselves. Deactivation is an administrative decision and
        // self-service recovery is not an appeal against it.
        Assert.Empty(sender.Sent);
        Assert.Empty(await context.Set<PasswordResetToken>().ToListAsync());
    }

    [Fact]
    public async Task A_mail_outage_is_invisible_to_the_caller_and_does_not_throw()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-mail-down", "down@customer.example");

        await using var context = db.ContextFor(null);

        // Two ways the transport fails: a refusal (null receipt — how IEmailSender says "not
        // accepted") and an exception. Neither may reach the caller, because a caller who can
        // see a mail failure can see that the address was real enough to try.
        await Service(context, new CapturingEmailSender { Accept = false })
            .RequestResetAsync(user.Email, null);
        await Service(context, new CapturingEmailSender { Throw = new InvalidOperationException("SMTP down") })
            .RequestResetAsync(user.Email, null);

        // The tokens exist and are usable once mail is working again — the second superseded the
        // first, which is the ordinary supersede rule, not a mail-specific behaviour.
        var rows = await context.Set<PasswordResetToken>().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(0, row.SendCount));
    }

    // ==== the happy path ======================================================================

    [Fact]
    public async Task A_valid_token_sets_the_password_and_the_old_one_stops_working()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-happy", "layla@customer.example");

        var token = await RequestAndReadTokenAsync(db, user.Email);
        Assert.NotNull(token);

        await using (var context = db.ContextFor(null))
        {
            var result = await Service(context).CompleteAsync(token, GoodPassword, "203.0.113.10");
            Assert.Equal(PasswordResetStatus.Completed, result.Status);
        }

        await using var verify = db.ContextFor(null);
        var updated = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == user.Id);

        Assert.True(BCrypt.Net.BCrypt.Verify(GoodPassword, updated.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify(KnownPassword, updated.PasswordHash));
        Assert.Equal("password-reset", updated.ModifiedBy);

        var row = await verify.Set<PasswordResetToken>().SingleAsync();
        Assert.NotNull(row.RedeemedAtUtc);
        Assert.Equal("203.0.113.10", row.RedeemedFromIp);
        Assert.Equal("203.0.113.9", row.RequestedFromIp);
    }

    /// <summary>
    /// The reset changes the credential and nothing else. <c>IsActive</c> is the column that
    /// matters: the identity role HOLDS the grant to write it (activation needs it), so the only
    /// thing stopping a reset from reactivating an account is this code choosing not to.
    /// </summary>
    [Fact]
    public async Task Completing_a_reset_changes_the_credential_and_nothing_about_the_account()
    {
        using var db = new TestDb();
        var (tenant, user) = await SeedAsync(db, "reset-scope", "scope@customer.example");

        var token = await RequestAndReadTokenAsync(db, user.Email);
        await using (var context = db.ContextFor(null))
            Assert.Equal(PasswordResetStatus.Completed,
                (await Service(context).CompleteAsync(token, GoodPassword, null)).Status);

        await using var verify = db.ContextFor(null);
        var updated = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == user.Id);

        Assert.Equal(user.RoleId, updated.RoleId);
        Assert.Equal(user.Buid, updated.Buid);
        Assert.Equal(user.Email, updated.Email);
        Assert.True(updated.IsActive);

        // The tenant the row was tagged with, so an offboarding purge can find it. Resolved from
        // the user's business unit through Tenants.PrimaryBusinessUnitId — reading exactly the two
        // columns nexora_identity_app is granted on that table.
        Assert.Equal(tenant.Id, (await verify.Set<PasswordResetToken>().SingleAsync()).TenantId);
    }

    [Fact]
    public async Task The_preview_masks_the_address_and_names_the_person()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-preview", "layla@customer.example");
        var token = await RequestAndReadTokenAsync(db, user.Email);

        await using var context = db.ContextFor(null);
        var challenge = await Service(context).InspectAsync(token);

        Assert.Equal(PasswordResetTokenState.Valid, challenge.State);
        var preview = challenge.Preview!;
        Assert.Equal("Layla", preview.RecipientFirstName);
        Assert.Equal(12, preview.MinimumPasswordLength);

        // Masked with NO opt-out, which is where this deliberately diverges from activation. A
        // reset link is caused by whoever typed an address into a public form — possibly not the
        // owner — so the exact string is never echoed back.
        Assert.DoesNotContain("layla@", preview.Email);
        Assert.StartsWith("l", preview.Email);
        Assert.EndsWith("@customer.example", preview.Email);
    }

    // ==== the refusals ========================================================================

    [Fact]
    public async Task An_expired_token_is_refused_and_the_password_is_untouched()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-expired", "expired@customer.example");

        // Issued two hours ago against the default one-hour window.
        var clock = new FakeClock(DateTime.UtcNow.AddHours(-2));
        var token = await RequestAndReadTokenAsync(db, user.Email, clock: clock);

        await using var context = db.ContextFor(null);
        var result = await Service(context).CompleteAsync(token, GoodPassword, null);

        Assert.Equal(PasswordResetStatus.TokenRejected, result.Status);
        // Named, not collapsed into "invalid": "expired" is the one word that turns a customer who
        // thinks the product is broken into a customer who clicks "send me another".
        Assert.Equal(PasswordResetTokenState.Expired, result.TokenState);
        await AssertPasswordUnchangedAsync(db, user.Id);

        // Refused, not consumed: an expired token is still un-redeemed, which is what keeps
        // "expired" and "used" distinguishable in the row an operator would read.
        await using var verify = db.ContextFor(null);
        Assert.Null((await verify.Set<PasswordResetToken>().SingleAsync()).RedeemedAtUtc);
    }

    /// <summary>
    /// The window is an hour, not the invitation's 72 — and configuration can shorten it but
    /// never lengthen it past a day.
    /// </summary>
    [Fact]
    public void The_reset_window_is_short_and_configuration_cannot_stretch_it()
    {
        Assert.Equal(TimeSpan.FromHours(1), new TenantOnboardingOptions().ResetLifetime);

        // A security team wanting fifteen minutes gets fifteen minutes.
        Assert.Equal(TimeSpan.FromMinutes(15),
            new TenantOnboardingOptions { ResetLifetimeMinutes = 15 }.ResetLifetime);

        // Somebody tired of resending sets it to a fortnight. The ceiling holds, and the clamp is
        // reported rather than applied silently.
        var stretched = new TenantOnboardingOptions { ResetLifetimeMinutes = 14 * 24 * 60 };
        Assert.Equal(
            TimeSpan.FromMinutes(TenantOnboardingOptions.AbsoluteMaximumResetLifetimeMinutes),
            stretched.ResetLifetime);
        Assert.Contains(stretched.Validate(), w => w.Contains("ceiling", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_spent_token_cannot_be_used_a_second_time()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-replay", "replay@customer.example");
        var token = await RequestAndReadTokenAsync(db, user.Email);

        await using (var first = db.ContextFor(null))
            Assert.Equal(PasswordResetStatus.Completed,
                (await Service(first).CompleteAsync(token, GoodPassword, null)).Status);

        // A forwarded email, a browser back button, or somebody who intercepted the message.
        // Whatever the source, the second use must not overwrite the password just chosen.
        await using (var second = db.ContextFor(null))
        {
            var replay = await Service(second).CompleteAsync(token, "Different-Choice-9#z", null);
            Assert.Equal(PasswordResetStatus.TokenRejected, replay.Status);
            // "Already used" is itself a security signal: a recipient who never used their link
            // and sees this knows somebody else did, and can escalate that same minute.
            Assert.Equal(PasswordResetTokenState.Used, replay.TokenState);
        }

        await using var verify = db.ContextFor(null);
        var updated = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == user.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(GoodPassword, updated.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify("Different-Choice-9#z", updated.PasswordHash));
    }

    [Fact]
    public async Task A_guesser_can_only_ever_reach_invalid_and_learns_nothing_about_the_account()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-guess", "guess@customer.example");
        var real = await RequestAndReadTokenAsync(db, user.Email);

        await using var context = db.ContextFor(null);
        var service = Service(context);

        // Every shape a caller WITHOUT a genuine link can present. All one answer — expired, used
        // and revoked are unreachable from here, which is what makes naming those three safe.
        foreach (var forged in new[] { new string('A', real!.Length), null, "", "short" })
        {
            var challenge = await service.InspectAsync(forged);
            Assert.Equal(PasswordResetTokenState.Invalid, challenge.State);

            // A rejected token describes NO account: no address, no expiry. Without this, a
            // harvested link would still confirm which mailbox it belonged to.
            Assert.Null(challenge.Preview);

            Assert.Equal(PasswordResetTokenState.Invalid,
                (await service.CompleteAsync(forged, GoodPassword, null)).TokenState);
        }

        await AssertPasswordUnchangedAsync(db, user.Id);
    }

    /// <summary>
    /// The token is bound to the account, so a link that outlives its account — deleted, or
    /// deactivated after the mail went out — cannot set anybody's password.
    /// </summary>
    [Fact]
    public async Task A_token_whose_account_was_deactivated_after_it_was_sent_stops_working()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-deactivated", "later@customer.example");
        var token = await RequestAndReadTokenAsync(db, user.Email);

        await using var context = db.ContextFor(null);
        await context.Users.IgnoreQueryFilters().Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false));

        var service = Service(context);
        Assert.Equal(PasswordResetTokenState.Invalid, (await service.InspectAsync(token)).State);
        Assert.Equal(PasswordResetTokenState.Invalid,
            (await service.CompleteAsync(token, GoodPassword, null)).TokenState);

        await AssertPasswordUnchangedAsync(db, user.Id);
    }

    // ==== supersede and the race ==============================================================

    /// <summary>
    /// Requesting a second link kills the first, in one transaction, so there is never an instant
    /// where two work. This is the mechanism that makes "I got a reset email I did not ask for"
    /// self-repairable: the customer requests their own, and the stranger's link dies.
    /// </summary>
    [Fact]
    public async Task Requesting_again_kills_the_previous_link_before_the_new_one_exists()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-supersede", "again@customer.example");

        var stranger = await RequestAndReadTokenAsync(db, user.Email, "198.51.100.77");
        var owner = await RequestAndReadTokenAsync(db, user.Email, "203.0.113.4");
        Assert.NotEqual(stranger, owner);

        await using var context = db.ContextFor(null);
        var service = Service(context);

        Assert.Equal(PasswordResetTokenState.Revoked,
            (await service.CompleteAsync(stranger, GoodPassword, null)).TokenState);
        Assert.Equal(PasswordResetStatus.Completed,
            (await service.CompleteAsync(owner, GoodPassword, null)).Status);
    }

    /// <summary>
    /// The sibling sweep at COMPLETION, which is a separate mechanism from the supersede at
    /// request time and covers what that one cannot: rows that became live through any path the
    /// request transaction did not run.
    /// </summary>
    [Fact]
    public async Task Completing_spends_every_other_outstanding_link_for_the_same_account()
    {
        using var db = new TestDb();
        var (tenant, user) = await SeedAsync(db, "reset-siblings", "siblings@customer.example");

        var older = await RequestAndReadTokenAsync(db, user.Email);
        var newer = await RequestAndReadTokenAsync(db, user.Email);
        Assert.NotNull(tenant);

        // Put the older row back into the LIVE state, after the newer one exists. That is the
        // state a partial failure — or any future path that mints without superseding — would
        // leave behind, and undoing the request-time supersede here is what makes this test prove
        // the COMPLETION sweep rather than re-prove the sweep the previous test already covers.
        await using (var seed = db.ContextFor(null))
        {
            var revived = await seed.Set<PasswordResetToken>()
                .Where(t => t.RevokedAtUtc != null)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(t => t.RevokedAtUtc, (DateTime?)null)
                    .SetProperty(t => t.RevokedBy, (string?)null)
                    .SetProperty(t => t.RevocationReason, (string?)null));
            Assert.Equal(1, revived);
        }

        await using var context = db.ContextFor(null);
        var service = Service(context);
        Assert.Equal(PasswordResetStatus.Completed,
            (await service.CompleteAsync(newer, GoodPassword, null)).Status);

        // Without the sweep, whoever holds the older email could re-set the password after the
        // owner had already chosen one — a silent account takeover.
        Assert.Equal(PasswordResetStatus.TokenRejected,
            (await service.CompleteAsync(older, "Takeover-Attempt-8#k", null)).Status);
    }

    [Fact]
    public async Task Two_completions_of_the_same_token_produce_exactly_one_success()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-race", "race@customer.example");
        var token = await RequestAndReadTokenAsync(db, user.Email);

        // Two independent services over two independent contexts — no shared change tracker, so
        // neither can see the other's work in memory. They run one after the other rather than
        // through Task.WhenAll because TestDb's SQLite connection is shared and not thread-safe;
        // the ordering is irrelevant to what is being proved. The single-use guard is not a
        // read-then-check in this process, it is the WHERE clause of the UPDATE that spends the
        // row, so the loser loses on zero rows affected whether it arrives a microsecond or a
        // minute later. A read-then-write implementation would let BOTH of these succeed.
        var outcomes = new List<PasswordResetStatus>();
        await using (var first = db.ContextFor(null))
            outcomes.Add((await Service(first).CompleteAsync(token, GoodPassword, "198.51.100.1")).Status);
        await using (var second = db.ContextFor(null))
            outcomes.Add((await Service(second).CompleteAsync(token, "Second-Winner-4#q", "198.51.100.2")).Status);

        Assert.Equal(1, outcomes.Count(s => s == PasswordResetStatus.Completed));
        Assert.Equal(1, outcomes.Count(s => s == PasswordResetStatus.TokenRejected));

        await using var verify = db.ContextFor(null);
        var updated = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == user.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(GoodPassword, updated.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify("Second-Winner-4#q", updated.PasswordHash));
        Assert.Equal("198.51.100.1", (await verify.Set<PasswordResetToken>().SingleAsync()).RedeemedFromIp);
    }

    // ==== password policy =====================================================================

    /// <summary>
    /// The SAME policy activation applies, at the same floor. A laxer rule on the recovery path
    /// would mean an account's credential strength depended on which door it was set through —
    /// and the recovery door is the one a hurried person uses.
    /// </summary>
    [Theory]
    [InlineData("short1A!", "too short")]
    [InlineData("alllowercaseletters", "one character class only")]
    [InlineData("  Dammam-Causeway-5#p  ", "whitespace that will not survive the next copy-paste")]
    [InlineData("policy@customer.example-9A", "the user's own address")]
    public async Task A_weak_password_is_refused_without_spending_the_token(string password, string why)
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-policy", "policy@customer.example");
        var token = await RequestAndReadTokenAsync(db, user.Email);

        await using var context = db.ContextFor(null);
        var service = Service(context);

        var rejected = await service.CompleteAsync(token, password, null);
        Assert.True(rejected.Status == PasswordResetStatus.PasswordRejected,
            $"Expected a refusal for {why}, got {rejected.Status}.");
        Assert.NotEmpty(rejected.PasswordFailures);

        // The link survives a rejected password. Someone who mistypes must not have to go back to
        // the sign-in page and start the whole flow again — that is how a recovery flow turns into
        // the support call it was built to end.
        Assert.Equal(PasswordResetStatus.Completed,
            (await service.CompleteAsync(token, GoodPassword, null)).Status);
    }

    // ==== the lockout the reset has to clear ==================================================

    /// <summary>
    /// The defect a naive implementation ships with: "I forgot my password, guessed five times,
    /// got locked out, reset it — and still cannot sign in for an hour."
    ///
    /// <para>Five wrong guesses is EXACTLY the sequence that precedes clicking "forgot password",
    /// and <c>LoginAttemptThrottle</c>'s progressive window would then refuse the brand-new
    /// credential. Clearing it is not a weakening: reaching that line requires having spent a live
    /// single-use token mailed to the account's own address, which is a stronger demonstration of
    /// control than the password the counter was protecting.</para>
    /// </summary>
    [Fact]
    public async Task Completing_a_reset_clears_the_sign_in_lockout_the_forgetting_caused()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-lockout", "locked@customer.example");

        await using var context = db.ContextFor(null);
        var throttle = new LoginAttemptThrottle(
            context, new LoginThrottleOptions(), NullLogger<LoginAttemptThrottle>.Instance);

        // The default policy locks out after five consecutive failures (SEC-H6).
        for (var attempt = 0; attempt < 5; attempt++)
            await throttle.RegisterFailureAsync(LoginPlane.Tenant, user.Email);
        Assert.True((await throttle.CheckAsync(LoginPlane.Tenant, user.Email)).IsLockedOut);

        var token = await RequestAndReadTokenAsync(db, user.Email);
        Assert.Equal(PasswordResetStatus.Completed,
            (await Service(context, throttle: throttle).CompleteAsync(token, GoodPassword, null)).Status);

        Assert.False((await throttle.CheckAsync(LoginPlane.Tenant, user.Email)).IsLockedOut);
    }

    /// <summary>The counter is only cleared by SUCCESS. A rejected password must not buy relief.</summary>
    [Fact]
    public async Task A_rejected_password_does_not_clear_the_sign_in_lockout()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-lockout-weak", "stilllocked@customer.example");

        await using var context = db.ContextFor(null);
        var throttle = new LoginAttemptThrottle(
            context, new LoginThrottleOptions(), NullLogger<LoginAttemptThrottle>.Instance);
        for (var attempt = 0; attempt < 5; attempt++)
            await throttle.RegisterFailureAsync(LoginPlane.Tenant, user.Email);

        var token = await RequestAndReadTokenAsync(db, user.Email);
        Assert.Equal(PasswordResetStatus.PasswordRejected,
            (await Service(context, throttle: throttle).CompleteAsync(token, "password", null)).Status);

        Assert.True((await throttle.CheckAsync(LoginPlane.Tenant, user.Email)).IsLockedOut);
    }

    // ==== storage and the link ================================================================

    [Fact]
    public async Task The_token_is_never_stored_in_cleartext_anywhere_in_the_row()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-at-rest", "atrest@customer.example");
        var token = await RequestAndReadTokenAsync(db, user.Email);

        await using var verify = db.ContextFor(null);
        var stored = await verify.Set<PasswordResetToken>().SingleAsync();

        Assert.NotEqual(token, stored.TokenHash);
        Assert.DoesNotContain(token!, stored.TokenHash, StringComparison.Ordinal);

        // Lowercase hex SHA-256 of the token: verifiable, irreversible, fixed width.
        Assert.Equal(64, stored.TokenHash.Length);
        Assert.All(stored.TokenHash, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(token!))).ToLowerInvariant();
        Assert.Equal(expected, stored.TokenHash);

        // Read the whole row back as text and sweep it: a database leak must not yield a usable
        // reset link from ANY column, not just the one we remembered to hash. The address is
        // checked too — this table deliberately does not store one, unlike an invitation, because
        // an anonymous stranger can cause rows here and it must not become an address ledger.
        var connection = verify.Database.GetDbConnection();
        await verify.Database.OpenConnectionAsync();

        // Resolved from sqlite_master rather than hardcoded: SQLite has no schemas, so how the
        // provider renders the "platform" schema into a table name is its business, not this
        // test's, and pinning a guess here would fail for a reason that has nothing to do with
        // whether tokens leak.
        await using var nameCommand = connection.CreateCommand();
        nameCommand.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '%PasswordResetTokens%'";
        var tableName = (string?)await nameCommand.ExecuteScalarAsync();
        Assert.False(string.IsNullOrEmpty(tableName));

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{tableName}\" WHERE \"Id\" = {stored.Id}";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var value = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i).ToString() ?? string.Empty;
            Assert.DoesNotContain(token!, value, StringComparison.Ordinal);
            Assert.DoesNotContain(user.Email, value, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Every_issued_token_is_distinct_and_url_safe()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-entropy", "entropy@customer.example");

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 25; i++)
        {
            var token = await RequestAndReadTokenAsync(db, user.Email);
            Assert.True(tokens.Add(token!), "A token repeated — the generator is not random.");

            // 32 CSPRNG bytes as unpadded base64url: 43 characters, and nothing in the alphabet a
            // mail client, a URL parser or a copy-paste out of plain text can mangle.
            Assert.Equal(43, token!.Length);
            Assert.All(token, c =>
                Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_', $"'{c}' is not URL-safe."));
        }
    }

    [Fact]
    public async Task The_reset_email_carries_the_link_no_credential_and_the_did_not_ask_line()
    {
        using var db = new TestDb();
        var (_, user) = await SeedAsync(db, "reset-email", "layla@customer.example");

        var sender = new CapturingEmailSender();
        await using (var context = db.ContextFor(null))
            await Service(context, sender).RequestResetAsync(user.Email, null);

        var message = Assert.Single(sender.Sent);
        Assert.Equal("layla@customer.example", Assert.Single(message.To).Address);

        foreach (var body in new[] { message.HtmlBody, message.TextBody! })
        {
            // Frontend/src/App.tsx routes "/reset-password/:token" — a path segment, not a query
            // parameter. A link built the other way 404s in the SPA and the customer sees a blank
            // page with no way to tell us what went wrong.
            Assert.Contains("https://app.nexora.test/reset-password/", body);

            // The single most important line in the message: the person whose address somebody
            // ELSE typed into the form has to be told that doing nothing is the correct action.
            Assert.Contains("did not ask for this", body, StringComparison.OrdinalIgnoreCase);

            // Nothing resembling a credential. The template has no password token to fill, which
            // is what makes this hold for every future edit.
            Assert.DoesNotContain(KnownPassword, body);
            Assert.DoesNotContain(user.PasswordHash, body);
            Assert.DoesNotContain("$2a$", body);
            Assert.DoesNotContain("temporary password", body, StringComparison.OrdinalIgnoreCase);

            // No company name, unlike the invitation: telling a stranger which organisation an
            // address belongs to is the disclosure the request endpoint refuses to make over HTTP,
            // and it would be perverse to make it over SMTP instead.
            Assert.DoesNotContain("Tenant reset-email", body);
        }

        Assert.False(string.IsNullOrWhiteSpace(message.HtmlBody));
        Assert.False(string.IsNullOrWhiteSpace(message.TextBody));

        await using var verify = db.ContextFor(null);
        var stored = await verify.Set<PasswordResetToken>().SingleAsync();
        Assert.Equal(1, stored.SendCount);
        Assert.NotNull(stored.LastSentAtUtc);
    }

    /// <summary>
    /// Case is not a credential. A user who signs in as "Layla@Customer.Example" and types it
    /// that way into the reset form must get their link — and because the endpoint is built never
    /// to say whether it found an account, a mismatch here would be a silent, unreportable dead
    /// end for that user forever.
    /// </summary>
    [Fact]
    public async Task The_address_is_matched_case_insensitively_exactly_as_login_matches_it()
    {
        using var db = new TestDb();
        await SeedAsync(db, "reset-case", "layla@customer.example");

        var token = await RequestAndReadTokenAsync(db, "  Layla@Customer.Example  ");
        Assert.NotNull(token);
    }

    // ==== helpers =============================================================================

    private static async Task AssertPasswordUnchangedAsync(TestDb db, long userId)
    {
        await using var verify = db.ContextFor(null);
        var user = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        Assert.True(BCrypt.Net.BCrypt.Verify(KnownPassword, user.PasswordHash));
    }

    private sealed class FakeClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
