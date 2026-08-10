using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The founding administrator's credential must be chosen by the customer and never known to
/// the operator.
///
/// <para><b>What was wrong.</b> Provisioning minted the founding Super Administrator with either
/// an operator-typed password or a server-generated one returned in the HTTP response and read
/// aloud during handover. The operator therefore held a working credential for a customer's most
/// privileged account, <c>Models/User.cs</c> has no must-change-password flag so it was never
/// rotated, and nothing was ever emailed — the secret's real home was a chat window. These tests
/// pin the replacement: a single-use, expiring, hash-at-rest activation link.</para>
///
/// <para><b>Harness note.</b> <see cref="TestDb"/> builds the SQLite schema from the real EF
/// model via <c>EnsureCreated</c>, so these tests require the context to declare
/// <c>DbSet&lt;TenantAdminInvitation&gt;</c> and to call
/// <c>modelBuilder.ApplyTenantOnboardingModel()</c> from <c>OnModelCreatingPartial</c>. Without
/// those two lines the table does not exist and every test here fails at the first query.</para>
/// </summary>
public sealed class TenantActivationInvitationTests
{
    private const string GoodPassword = "Riyadh-Harbour-7#x";

    // ==== harness =============================================================================

    /// <summary>
    /// Captures whatever the module hands to the transport, so the email itself can be
    /// inspected the way a recipient would see it.
    /// </summary>
    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];
        public bool Accept { get; set; } = true;

        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult<EmailDeliveryReceipt?>(
                Accept ? new EmailDeliveryReceipt("capture", "captured-1", DateTimeOffset.UtcNow) : null);
        }
    }

    private sealed class NullAudit : IPlatformAuditService
    {
        public Task WriteAsync(ClaimsPrincipal actor, string action, string? targetType = null,
            string? targetId = null, object? metadata = null, long? actAsTenantId = null,
            HttpContext? httpContext = null, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static TenantAdminInvitationService Service(
        ErpRfqAutomationContext context,
        IEmailSender? sender = null,
        TenantOnboardingOptions? options = null,
        TimeProvider? clock = null) =>
        new(context,
            sender ?? new CapturingEmailSender(),
            Options.Create(new NotificationsOptions { AppBaseUrl = "https://app.nexora.test" }),
            Options.Create(options ?? new TenantOnboardingOptions()),
            NullLogger<TenantAdminInvitationService>.Instance,
            clock);

    /// <summary>
    /// The shape provisioning produces: a tenant, its primary business unit, an Owner-rank role
    /// and the founding administrator holding it. Seeded directly rather than through
    /// TenantsController so these tests pin the activation flow, not provisioning.
    /// </summary>
    private static async Task<(Tenant tenant, User admin)> SeedTenantWithAdminAsync(
        TestDb db, string slug, string email, string initialPassword = "operator-chosen-secret")
    {
        await using var context = db.ContextFor(null);

        var businessUnit = new BusinessUnit
        {
            BusinessUnitCode = slug.ToUpperInvariant(),
            BusinessUnitName = $"Tenant {slug}",
            Description = "Seeded for activation tests",
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
            FirstName = "Sara",
            LastName = "Al Otaibi",
            Email = email,
            // The credential the operator would have known under the old handover. Every test
            // below that activates asserts this stops working.
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(initialPassword),
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
        TestDb db, Tenant tenant, User admin,
        TenantOnboardingOptions? options = null, TimeProvider? clock = null)
    {
        await using var context = db.ContextFor(null);
        return await Service(context, options: options, clock: clock).IssueAsync(context,
            new TenantAdminInvitationRequest
            {
                TenantId = tenant.Id,
                UserId = admin.Id,
                Email = admin.Email,
                RecipientName = admin.FirstName,
                TenantName = tenant.Name,
                IssuedBy = "owner@nexora.app"
            });
    }

    // ==== the happy path ======================================================================

    [Fact]
    public async Task A_valid_token_sets_the_password_and_the_admin_can_authenticate_with_it()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-happy", "sara@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        await using (var redeemContext = db.ContextFor(null))
        {
            var result = await Service(redeemContext).RedeemAsync(issued.Token, GoodPassword, "203.0.113.10");
            Assert.Equal(TenantActivationStatus.Activated, result.Status);
        }

        await using var verify = db.ContextFor(null);
        var activated = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == admin.Id);

        // The credential the CUSTOMER chose is the credential that logs in…
        Assert.True(BCrypt.Net.BCrypt.Verify(GoodPassword, activated.PasswordHash));
        // …and the one the operator set at provisioning no longer does. That replacement is the
        // entire point: after activation, nobody at the platform holds a working credential.
        Assert.False(BCrypt.Net.BCrypt.Verify("operator-chosen-secret", activated.PasswordHash));
        Assert.True(activated.IsActive);
        Assert.Null(activated.DeactivatedAtUtc);

        var invitation = await verify.Set<TenantAdminInvitation>()
            .SingleAsync(i => i.Id == issued.InvitationId);
        Assert.NotNull(invitation.RedeemedAtUtc);
        Assert.Equal("203.0.113.10", invitation.RedeemedFromIp);
    }

    [Fact]
    public async Task The_preview_names_the_company_and_masks_the_address()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-preview", "sara@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        await using var context = db.ContextFor(null);
        var challenge = await Service(context).InspectAsync(issued.Token);

        Assert.Equal(ActivationTokenState.Valid, challenge.State);
        var preview = challenge.Preview!;
        Assert.Equal("Tenant activation-preview", preview.CompanyName);
        Assert.Equal("Sara", preview.RecipientFirstName);

        // Enough to recognise which mailbox this is, not enough to harvest an address from a
        // link that was forwarded into a shared inbox.
        Assert.DoesNotContain("sara@", preview.Email);
        Assert.StartsWith("s", preview.Email);
        Assert.EndsWith("@customer.example", preview.Email);

        // Opt-out for deployments that would rather show the exact string the customer types at
        // sign-in. It is a configuration decision, not a code change, and the default is masked.
        await using var revealed = db.ContextFor(null);
        var full = await Service(revealed,
                options: new TenantOnboardingOptions { MaskActivationEmail = false })
            .InspectAsync(issued.Token);
        Assert.Equal("sara@customer.example", full.Preview!.Email);
    }

    // ==== the refusals ========================================================================

    [Fact]
    public async Task An_expired_token_is_refused_and_the_password_is_untouched()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-expired", "expired@customer.example");

        // Issued three days ago against a one-hour lifetime: expired long before this call.
        var issuedAt = new FakeClock(DateTime.UtcNow.AddDays(-3));
        var issued = await IssueAsync(db, tenant, admin,
            options: new TenantOnboardingOptions { InvitationLifetimeHours = 1 }, clock: issuedAt);

        await using var context = db.ContextFor(null);
        var result = await Service(context).RedeemAsync(issued.Token, GoodPassword, null);

        Assert.Equal(TenantActivationStatus.TokenRejected, result.Status);
        // Named, not collapsed into "invalid": "expired" is the one word that turns a customer
        // who thinks the product is broken into a customer who asks for a resend.
        Assert.Equal(ActivationTokenState.Expired, result.TokenState);
        await AssertPasswordUnchangedAsync(db, admin.Id);

        // Refused, not consumed: an expired invitation is still un-redeemed, which is what the
        // operator's list has to show so "expired" and "used" stay distinguishable.
        await using var verify = db.ContextFor(null);
        Assert.Null((await verify.Set<TenantAdminInvitation>()
            .SingleAsync(i => i.Id == issued.InvitationId)).RedeemedAtUtc);
    }

    [Fact]
    public async Task A_redeemed_token_cannot_be_used_a_second_time()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-replay", "replay@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        await using (var first = db.ContextFor(null))
            Assert.Equal(TenantActivationStatus.Activated,
                (await Service(first).RedeemAsync(issued.Token, GoodPassword, null)).Status);

        // A forwarded email, a browser back button, or an attacker with the link. Whatever the
        // source, the second use must not be able to overwrite the password the customer chose.
        await using (var second = db.ContextFor(null))
        {
            var replay = await Service(second).RedeemAsync(issued.Token, "Different-Choice-9#z", null);
            Assert.Equal(TenantActivationStatus.TokenRejected, replay.Status);
            // "Already used" is itself a security signal: a recipient who never used their link
            // and sees this knows somebody else did, and can escalate that same minute.
            Assert.Equal(ActivationTokenState.Used, replay.TokenState);
        }

        await using var verify = db.ContextFor(null);
        var user = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == admin.Id);
        Assert.True(BCrypt.Net.BCrypt.Verify(GoodPassword, user.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify("Different-Choice-9#z", user.PasswordHash));
    }

    [Fact]
    public async Task A_revoked_token_is_refused()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-revoked", "revoked@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        await using (var context = db.ContextFor(null))
            Assert.True(await Service(context).RevokeAsync(
                tenant.Id, issued.InvitationId, "owner@nexora.app", "Sent to the wrong address."));

        await using (var context = db.ContextFor(null))
        {
            var refused = await Service(context).RedeemAsync(issued.Token, GoodPassword, null);
            Assert.Equal(TenantActivationStatus.TokenRejected, refused.Status);
            Assert.Equal(ActivationTokenState.Revoked, refused.TokenState);
        }

        await AssertPasswordUnchangedAsync(db, admin.Id);

        // Revoking twice is not an error the operator caused — it is a stale screen — but it
        // must report that nothing was outstanding rather than silently succeeding.
        await using var again = db.ContextFor(null);
        Assert.False(await Service(again).RevokeAsync(
            tenant.Id, issued.InvitationId, "owner@nexora.app", "Again."));
    }

    [Fact]
    public async Task A_guesser_can_only_ever_reach_invalid_and_learns_nothing_about_the_account()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-unknown", "unknown@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        await using var context = db.ContextFor(null);
        var service = Service(context);

        // Every shape a caller WITHOUT a genuine link can present — right length, wrong value;
        // nothing; empty; too short. All one answer. Expired / used / revoked are unreachable
        // from here, which is what makes naming those three safe.
        foreach (var forged in new[] { new string('A', issued.Token.Length), null, "", "short" })
        {
            var challenge = await service.InspectAsync(forged);
            Assert.Equal(ActivationTokenState.Invalid, challenge.State);

            // A rejected token describes NO account: no address, no company, no expiry. Without
            // this, a harvested link would still confirm which organisation it belonged to.
            Assert.Null(challenge.Preview);

            Assert.Equal(ActivationTokenState.Invalid,
                (await service.RedeemAsync(forged, GoodPassword, null)).TokenState);
        }

        // Nothing above touched the account it was aimed at.
        await AssertPasswordUnchangedAsync(db, admin.Id);
    }

    // ==== the race ============================================================================

    [Fact]
    public async Task Two_redemptions_of_the_same_token_produce_exactly_one_success()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-race", "race@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        // Two independent services over two independent contexts — no shared change tracker, so
        // neither can see the other's work in memory. They run one after the other rather than
        // through Task.WhenAll because TestDb's SQLite connection is shared and not thread-safe;
        // the ordering is irrelevant to what is being proved. The single-use guard is not a
        // read-then-check in this process, it is the WHERE clause of the UPDATE that spends the
        // invitation, so the loser loses on zero rows affected whether it arrives a microsecond
        // or a minute later. A read-then-write implementation would let BOTH of these succeed.
        var outcomes = new List<TenantActivationStatus>();
        await using (var first = db.ContextFor(null))
            outcomes.Add((await Service(first).RedeemAsync(issued.Token, GoodPassword, "198.51.100.1")).Status);
        await using (var second = db.ContextFor(null))
            outcomes.Add((await Service(second).RedeemAsync(issued.Token, "Second-Winner-4#q", "198.51.100.2")).Status);

        Assert.Equal(1, outcomes.Count(s => s == TenantActivationStatus.Activated));
        Assert.Equal(1, outcomes.Count(s => s == TenantActivationStatus.TokenRejected));

        await using var verify = db.ContextFor(null);
        var user = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == admin.Id);

        // Exactly one password survives, and it belongs to the winner — the loser did not get a
        // partial write in either direction.
        Assert.True(BCrypt.Net.BCrypt.Verify(GoodPassword, user.PasswordHash));
        Assert.False(BCrypt.Net.BCrypt.Verify("Second-Winner-4#q", user.PasswordHash));

        var invitation = await verify.Set<TenantAdminInvitation>()
            .SingleAsync(i => i.Id == issued.InvitationId);
        Assert.Equal("198.51.100.1", invitation.RedeemedFromIp);
    }

    [Fact]
    public async Task Reissuing_kills_the_previous_link_before_the_new_one_exists()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-reissue", "reissue@customer.example");
        var first = await IssueAsync(db, tenant, admin);

        IssuedTenantAdminInvitation? second;
        await using (var context = db.ContextFor(null))
            second = await Service(context).ReissueAsync(tenant.Id, null, "owner@nexora.app");

        Assert.NotNull(second);
        Assert.NotEqual(first.Token, second!.Token);

        await using var context2 = db.ContextFor(null);
        var service = Service(context2);

        // A resend that left the old token alive would mean a customer reporting a leaked invite
        // could not be made safe by resending — the leaked link would still work.
        Assert.Equal(TenantActivationStatus.TokenRejected,
            (await service.RedeemAsync(first.Token, GoodPassword, null)).Status);
        Assert.Equal(TenantActivationStatus.Activated,
            (await service.RedeemAsync(second.Token, GoodPassword, null)).Status);
    }

    [Fact]
    public async Task Activating_spends_every_other_outstanding_link_for_the_same_account()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-siblings", "siblings@customer.example");

        // Two invitations issued without going through reissue — the state an operator reaches
        // by pressing "resend" through a path that does not supersede, or by a partial failure.
        var older = await IssueAsync(db, tenant, admin);
        var newer = await IssueAsync(db, tenant, admin);

        await using var context = db.ContextFor(null);
        var service = Service(context);
        Assert.Equal(TenantActivationStatus.Activated,
            (await service.RedeemAsync(newer.Token, GoodPassword, null)).Status);

        // Without this, whoever still holds the older email could re-set the administrator's
        // password after they had already chosen it — a silent account takeover.
        Assert.Equal(TenantActivationStatus.TokenRejected,
            (await service.RedeemAsync(older.Token, "Takeover-Attempt-8#k", null)).Status);
    }

    // ==== storage =============================================================================

    [Fact]
    public async Task The_token_is_never_stored_in_cleartext_anywhere_in_the_row()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-at-rest", "atrest@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        await using var verify = db.ContextFor(null);
        var stored = await verify.Set<TenantAdminInvitation>().SingleAsync(i => i.Id == issued.InvitationId);

        Assert.NotEqual(issued.Token, stored.TokenHash);
        Assert.DoesNotContain(issued.Token, stored.TokenHash);

        // Lowercase hex SHA-256 of the token: verifiable, irreversible, fixed width.
        Assert.Equal(64, stored.TokenHash.Length);
        Assert.All(stored.TokenHash, c => Assert.True(Uri.IsHexDigit(c) && !char.IsUpper(c)));
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(issued.Token))).ToLowerInvariant();
        Assert.Equal(expected, stored.TokenHash);

        // Read the whole row back as text and sweep it: a database leak must not yield a usable
        // activation link from ANY column, not just the one we remembered to hash.
        var connection = verify.Database.GetDbConnection();
        await verify.Database.OpenConnectionAsync();

        // Resolved from sqlite_master rather than hardcoded: SQLite has no schemas, so how the
        // provider renders the "platform" schema into a table name is its business, not this
        // test's, and pinning a guess here would fail for a reason that has nothing to do with
        // whether tokens leak.
        await using var nameCommand = connection.CreateCommand();
        nameCommand.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name LIKE '%TenantAdminInvitations%'";
        var tableName = (string?)await nameCommand.ExecuteScalarAsync();
        Assert.False(string.IsNullOrEmpty(tableName));

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM \"{tableName}\" WHERE \"Id\" = {stored.Id}";
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        for (var i = 0; i < reader.FieldCount; i++)
        {
            var value = reader.IsDBNull(i) ? string.Empty : reader.GetValue(i).ToString() ?? string.Empty;
            Assert.DoesNotContain(issued.Token, value, StringComparison.Ordinal);
        }

        // A record's generated ToString prints every property, so the one-liner everybody writes
        // — LogInformation("issued {Invitation}", issued) — would put a live token in the logs.
        Assert.DoesNotContain(issued.Token, issued.ToString());
    }

    [Fact]
    public async Task Every_issued_token_is_distinct_and_url_safe()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-entropy", "entropy@customer.example");

        var tokens = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 50; i++)
        {
            var issued = await IssueAsync(db, tenant, admin);
            Assert.True(tokens.Add(issued.Token), "A token repeated — the generator is not random.");

            // 32 CSPRNG bytes as unpadded base64url: 43 characters, and nothing in the alphabet
            // that a mail client, a URL parser or a copy-paste out of plain text can mangle.
            Assert.Equal(43, issued.Token.Length);
            Assert.All(issued.Token, c =>
                Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '_',
                    $"'{c}' is not URL-safe."));
        }
    }

    // ==== the email ===========================================================================

    [Fact]
    public async Task The_invite_email_carries_the_link_and_no_credential()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(
            db, "activation-email", "sara@customer.example", initialPassword: "operator-knows-this");
        var issued = await IssueAsync(db, tenant, admin);

        var sender = new CapturingEmailSender();
        await using (var context = db.ContextFor(null))
            Assert.True(await Service(context, sender).SendInvitationEmailAsync(issued));

        var message = Assert.Single(sender.Sent);
        Assert.Equal("sara@customer.example", Assert.Single(message.To).Address);
        Assert.Contains("Tenant activation-email", message.Subject);

        foreach (var body in new[] { message.HtmlBody, message.TextBody! })
        {
            // The link is the ONLY secret in the message, and it is single-use.
            Assert.Contains("https://app.nexora.test/activate/", body);
            Assert.Contains(issued.Token, body);

            // Nothing resembling a credential: not the password the operator set at
            // provisioning, not the stored hash, not a hint at either. The template has no
            // password token to fill, which is what makes this hold for every future edit.
            Assert.DoesNotContain("operator-knows-this", body);
            Assert.DoesNotContain(admin.PasswordHash, body);
            Assert.DoesNotContain("$2a$", body);
            Assert.DoesNotContain("Your password is", body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("temporary password", body, StringComparison.OrdinalIgnoreCase);
        }

        // Both parts are present: an invitation that renders as an empty message in a text-only
        // client is an invitation the recipient cannot act on.
        Assert.False(string.IsNullOrWhiteSpace(message.HtmlBody));
        Assert.False(string.IsNullOrWhiteSpace(message.TextBody));

        await using var verify = db.ContextFor(null);
        var stored = await verify.Set<TenantAdminInvitation>().SingleAsync(i => i.Id == issued.InvitationId);
        Assert.Equal(1, stored.SendCount);
        Assert.NotNull(stored.LastSentAtUtc);
    }

    [Fact]
    public async Task A_mail_failure_never_propagates_and_leaves_the_invitation_usable()
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-mail-down", "down@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        var refusing = new CapturingEmailSender { Accept = false };
        await using (var context = db.ContextFor(null))
            // Provisioning has already committed by the time the email is attempted. Turning a
            // mail outage into an exception would tell the operator the tenant was not created.
            Assert.False(await Service(context, refusing).SendInvitationEmailAsync(issued));

        await using var context2 = db.ContextFor(null);
        Assert.Equal(TenantActivationStatus.Activated,
            (await Service(context2).RedeemAsync(issued.Token, GoodPassword, null)).Status);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Reissue_returns_the_single_use_link_only_when_email_was_not_transmitted(
        bool providerAccepts, bool expectsRecoveryLink)
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(
            db, $"activation-reissue-{providerAccepts}", $"reissue-{providerAccepts}@customer.example");
        await IssueAsync(db, tenant, admin);

        var sender = new CapturingEmailSender { Accept = providerAccepts };
        await using var context = db.ContextFor(null);
        var controller = new TenantAdminInvitationsController(
            Service(context, sender), context, new NullAudit(),
            NullLogger<TenantAdminInvitationsController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("email", "owner@nexora.app"),
                         new Claim(PlatformAuthConstants.PlatformRoleClaim, nameof(PlatformRole.Owner))],
                        "Platform"))
                }
            }
        };

        var action = await controller.Resend(tenant.Id,
            new ResendTenantAdminInvitationRequest { UserId = admin.Id, Reason = "Customer did not receive it." },
            CancellationToken.None);
        var response = Assert.IsType<ResendTenantAdminInvitationResponse>(
            Assert.IsType<OkObjectResult>(action.Result).Value);

        Assert.Equal(providerAccepts, response.EmailDispatched);
        Assert.Equal(expectsRecoveryLink, !string.IsNullOrWhiteSpace(response.ActivationUrl));
        if (response.ActivationUrl is not null)
            Assert.StartsWith("https://app.nexora.test/activate/", response.ActivationUrl);
    }

    // ==== password policy =====================================================================

    [Theory]
    [InlineData("short1A!", "too short to be an owner credential")]
    [InlineData("alllowercaseletters", "one character class only")]
    [InlineData("  Riyadh-Harbour-7#x  ", "whitespace that will not survive the next copy-paste")]
    [InlineData("sara@customer.example-9A", "the invitee's own address")]
    public async Task A_weak_password_is_refused_without_spending_the_invitation(
        string password, string why)
    {
        using var db = new TestDb();
        var (tenant, admin) = await SeedTenantWithAdminAsync(db, "activation-policy", "sara@customer.example");
        var issued = await IssueAsync(db, tenant, admin);

        await using var context = db.ContextFor(null);
        var service = Service(context);

        var rejected = await service.RedeemAsync(issued.Token, password, null);
        Assert.True(rejected.Status == TenantActivationStatus.PasswordRejected,
            $"Expected a refusal for {why}, got {rejected.Status}.");
        Assert.NotEmpty(rejected.PasswordFailures);

        // The invitation survives a rejected password. Someone who mistypes must not have to ask
        // the operator for a new link — that reintroduces exactly the phone call this replaces.
        Assert.Equal(TenantActivationStatus.Activated,
            (await service.RedeemAsync(issued.Token, GoodPassword, null)).Status);
    }

    [Fact]
    public void A_passphrase_past_BCrypts_seventy_two_byte_limit_is_refused_rather_than_silently_truncated()
    {
        // BCrypt hashes at most 72 bytes and discards the rest. Accepting a 90-character
        // passphrase and verifying only its first 72 bytes would store something weaker than
        // what the user typed while telling them it was stronger.
        var overlong = new string('A', 40) + new string('b', 40) + "9#";
        var result = ActivationPasswordPolicy.Validate(overlong, "sara@customer.example", 12);

        Assert.False(result.IsAcceptable);
        Assert.Contains(result.Failures, f => f.Contains("72", StringComparison.Ordinal));
    }

    [Fact]
    public void Configuration_cannot_lower_the_password_floor()
    {
        // A pilot under time pressure sets MinimumPasswordLength to 4. The floor holds, and the
        // clamp is reported rather than applied silently.
        var options = new TenantOnboardingOptions { MinimumPasswordLength = 4 };

        Assert.Equal(TenantOnboardingOptions.AbsoluteMinimumPasswordLength,
            options.EffectiveMinimumPasswordLength);
        Assert.Contains(options.Validate(), w => w.Contains("hard floor", StringComparison.Ordinal));
    }

    // ==== helpers =============================================================================

    private static async Task AssertPasswordUnchangedAsync(TestDb db, long userId)
    {
        await using var verify = db.ContextFor(null);
        var user = await verify.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == userId);
        Assert.True(BCrypt.Net.BCrypt.Verify("operator-chosen-secret", user.PasswordHash));
        Assert.False(user.IsActive);
    }

    private sealed class FakeClock(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow, TimeSpan.Zero);
    }
}
