using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.UserDTO;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A tenant administrator adding a colleague sends an activation link by default and never
/// holds the colleague's password. Same token machinery as the platform console
/// (<c>TenantAdminInvitationService</c>: 256-bit CSPRNG, SHA-256 at rest, single use, 72 h),
/// reached through <c>POST /api/User</c> with <c>Activation=invite</c>.
/// </summary>
public sealed class TenantUserInvitationTests
{
    private const long CallerUserId = 66_001;
    private const string StrongPassword = "Riyadh-Harbour-7#x";

    [Fact]
    public async Task Creating_a_user_with_activation_invite_creates_an_invitation_and_emails_the_link()
    {
        using var harness = new Harness();
        var fixture = await harness.SeedTenantAsync("noor-sons");
        var controller = harness.Controller(fixture);

        var result = await controller.Create(Request(fixture, activation: "invite"));

        var created = Assert.IsType<UserResponseDTO>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal("invite", created.ActivationMethod);
        Assert.True(created.InvitationEmailDispatched);
        Assert.NotNull(created.InvitationExpiresAtUtc);

        await using var db = harness.Database.ContextFor(null);
        var invitation = Assert.Single(await db.TenantAdminInvitations.Where(i => i.UserId == created.Id).ToListAsync());
        Assert.Equal(fixture.TenantId, invitation.TenantId);
        Assert.Equal("sara@noor-sons.test", invitation.Email);
        Assert.Equal("Tenant noor-sons", invitation.TenantName);        // the business unit's name
        Assert.Matches("^[0-9a-f]{64}$", invitation.TokenHash);        // SHA-256 hex, never cleartext
        Assert.Null(invitation.RedeemedAtUtc);
        Assert.Equal(1, invitation.SendCount);
        Assert.Equal(harness.Clock.Now.AddHours(72), invitation.ExpiresAtUtc);

        var email = Assert.Single(harness.Sender.Sent);
        Assert.Equal("sara@noor-sons.test", Assert.Single(email.To).Address);
        Assert.Contains("/activate/", email.HtmlBody);
        Assert.Equal(fixture.BusinessUnitId, email.OwningBusinessUnitId);   // item A: the tenant's own sender
        Assert.DoesNotContain(invitation.TokenHash, email.HtmlBody);
    }

    [Fact]
    public async Task An_invited_account_is_dormant_and_its_password_hash_is_unusable_until_redeemed()
    {
        using var harness = new Harness();
        var fixture = await harness.SeedTenantAsync("noor-sons");
        var controller = harness.Controller(fixture);

        var created = Assert.IsType<UserResponseDTO>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Create(Request(fixture, activation: "invite"))).Result).Value);

        await using var db = harness.Database.ContextFor(null);
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == created.Id);
        Assert.False(user.IsActive);
        Assert.NotNull(user.DeactivatedAtUtc);
        Assert.StartsWith("$2", user.PasswordHash);
        // Nothing anyone holds verifies against it: not the empty string, not the token, not
        // the address, not the placeholder anybody might guess.
        var token = harness.LastIssuedToken();
        foreach (var guess in new[] { "", token, "sara@noor-sons.test", "Welcome@123", "password" })
            Assert.False(BCrypt.Net.BCrypt.Verify(guess, user.PasswordHash), $"'{guess}' must not verify");
    }

    [Fact]
    public async Task Redeeming_the_link_sets_the_password_and_activates_the_account()
    {
        using var harness = new Harness();
        var fixture = await harness.SeedTenantAsync("noor-sons");
        var controller = harness.Controller(fixture);
        var created = Assert.IsType<UserResponseDTO>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Create(Request(fixture, activation: "invite"))).Result).Value);
        var token = harness.LastIssuedToken();

        var redeemed = await harness.Invitations.RedeemAsync(token, StrongPassword, "203.0.113.7");

        Assert.Equal(TenantActivationStatus.Activated, redeemed.Status);
        Assert.True(redeemed.SignInAvailable);
        await using var db = harness.Database.ContextFor(null);
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == created.Id);
        Assert.True(user.IsActive);
        Assert.Null(user.DeactivatedAtUtc);
        Assert.True(BCrypt.Net.BCrypt.Verify(StrongPassword, user.PasswordHash));
        var invitation = await db.TenantAdminInvitations.SingleAsync(i => i.UserId == created.Id);
        Assert.NotNull(invitation.RedeemedAtUtc);

        // Single use.
        var again = await harness.Invitations.RedeemAsync(token, StrongPassword, "203.0.113.7");
        Assert.Equal(TenantActivationStatus.TokenRejected, again.Status);
        Assert.Equal(ActivationTokenState.Used, again.TokenState);
    }

    [Fact]
    public async Task An_expired_link_is_refused_and_the_account_stays_dormant()
    {
        using var harness = new Harness();
        var fixture = await harness.SeedTenantAsync("noor-sons");
        var controller = harness.Controller(fixture);
        var created = Assert.IsType<UserResponseDTO>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Create(Request(fixture, activation: "invite"))).Result).Value);
        var token = harness.LastIssuedToken();
        var hashBefore = (await harness.Database.ContextFor(null).Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Id == created.Id)).PasswordHash;

        harness.Clock.Now = harness.Clock.Now.AddHours(73);
        var refused = await harness.Invitations.RedeemAsync(token, StrongPassword, "203.0.113.7");

        Assert.Equal(TenantActivationStatus.TokenRejected, refused.Status);
        Assert.Equal(ActivationTokenState.Expired, refused.TokenState);
        await using var db = harness.Database.ContextFor(null);
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == created.Id);
        Assert.False(user.IsActive);
        Assert.Equal(hashBefore, user.PasswordHash);
    }

    [Fact]
    public async Task A_password_with_no_activation_field_still_creates_an_active_account_for_older_clients()
    {
        using var harness = new Harness();
        var fixture = await harness.SeedTenantAsync("noor-sons");
        var controller = harness.Controller(fixture);

        var request = Request(fixture, activation: null);
        request.Password = StrongPassword;
        var created = Assert.IsType<UserResponseDTO>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Create(request)).Result).Value);

        Assert.Equal("password", created.ActivationMethod);
        Assert.Null(created.InvitationEmailDispatched);
        Assert.Empty(harness.Sender.Sent);
        await using var db = harness.Database.ContextFor(null);
        var user = await db.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == created.Id);
        Assert.True(user.IsActive);
        Assert.True(BCrypt.Net.BCrypt.Verify(StrongPassword, user.PasswordHash));
        Assert.Empty(await db.TenantAdminInvitations.Where(i => i.UserId == created.Id).ToListAsync());
    }

    [Fact]
    public async Task Neither_a_password_nor_an_invitation_is_a_bad_request_only_when_password_was_chosen()
    {
        using var harness = new Harness();
        var fixture = await harness.SeedTenantAsync("noor-sons");
        var controller = harness.Controller(fixture);

        var request = Request(fixture, activation: "password");
        request.Password = null;
        Assert.IsType<BadRequestObjectResult>((await controller.Create(request)).Result);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task A_business_unit_that_is_not_a_tenants_primary_unit_cannot_invite()
    {
        using var harness = new Harness();
        var fixture = await harness.SeedTenantAsync("noor-sons", registerTenant: false);
        var controller = harness.Controller(fixture);

        var result = await controller.Create(Request(fixture, activation: "invite"));

        Assert.IsType<ConflictObjectResult>(result.Result);
        Assert.Empty(harness.Sender.Sent);
        await using var db = harness.Database.ContextFor(null);
        Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Email == "sara@noor-sons.test"));
    }

    [Fact]
    public async Task Resending_supersedes_the_earlier_link_and_emails_a_new_one()
    {
        using var harness = new Harness();
        var fixture = await harness.SeedTenantAsync("noor-sons");
        var controller = harness.Controller(fixture);
        var created = Assert.IsType<UserResponseDTO>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Create(Request(fixture, activation: "invite"))).Result).Value);
        var first = harness.LastIssuedToken();

        var resend = await controller.ResendInvitation(created.Id);

        Assert.IsType<OkObjectResult>(resend);
        Assert.Equal(2, harness.Sender.Sent.Count);
        Assert.Equal(fixture.BusinessUnitId, harness.Sender.Sent[1].OwningBusinessUnitId);
        var second = harness.LastIssuedToken();
        Assert.NotEqual(first, second);
        Assert.Equal(ActivationTokenState.Revoked,
            (await harness.Invitations.RedeemAsync(first, StrongPassword, "203.0.113.7")).TokenState);
        Assert.Equal(TenantActivationStatus.Activated,
            (await harness.Invitations.RedeemAsync(second, StrongPassword, "203.0.113.7")).Status);
    }

    // ==== audit 2026-09-04: resend is not a reactivation path ======================================

    [Fact]
    public async Task Resending_cannot_reactivate_an_account_that_was_created_with_a_password()
    {
        using var harness = new Harness();
        var fixture = await harness.SeedTenantAsync("noor-sons");
        var controller = harness.Controller(fixture);
        var request = Request(fixture, activation: "password");
        request.Password = StrongPassword;
        var created = Assert.IsType<UserResponseDTO>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Create(request)).Result).Value);
        Assert.Empty(harness.Sender.Sent);
        await DeactivateAsync(harness, created.Id);

        var resend = await controller.ResendInvitation(created.Id);

        // A holder of Users:Create must not be able to undo a deactivation by mailing the
        // account a link that flips IsActive back on when redeemed.
        Assert.IsType<ConflictObjectResult>(resend);
        Assert.Empty(harness.Sender.Sent);
    }

    [Fact]
    public async Task Resending_cannot_reactivate_an_invitee_who_already_redeemed_a_link()
    {
        using var harness = new Harness();
        var fixture = await harness.SeedTenantAsync("noor-sons");
        var controller = harness.Controller(fixture);
        var created = Assert.IsType<UserResponseDTO>(Assert.IsType<CreatedAtActionResult>(
            (await controller.Create(Request(fixture, activation: "invite"))).Result).Value);
        Assert.Equal(TenantActivationStatus.Activated,
            (await harness.Invitations.RedeemAsync(harness.LastIssuedToken(), StrongPassword, "203.0.113.7")).Status);
        await DeactivateAsync(harness, created.Id);

        var resend = await controller.ResendInvitation(created.Id);

        Assert.IsType<ConflictObjectResult>(resend);
        Assert.Single(harness.Sender.Sent);
    }

    private static async Task DeactivateAsync(Harness harness, long userId)
    {
        await using var context = harness.Database.ContextFor(null);
        Assert.Equal(1, await context.Users.IgnoreQueryFilters().Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsActive, (bool?)false)
                .SetProperty(u => u.DeactivatedAtUtc, (DateTime?)DateTime.UtcNow)));
    }

    // ==== harness =================================================================================

    private static UserCreateRequestDTO Request(Fixture fixture, string? activation) => new()
    {
        FirstName = "Sara", LastName = "Al-Amri", Email = "sara@noor-sons.test",
        Buid = fixture.BusinessUnitId, RoleId = fixture.MemberRoleId, IsActive = true,
        Activation = activation
    };

    private sealed record Fixture(long TenantId, long BusinessUnitId, long MemberRoleId);

    private sealed class Harness : IDisposable
    {
        public TestDb Database { get; } = new();
        public CapturingEmailSender Sender { get; } = new();
        public MutableClock Clock { get; } = new(new DateTime(2026, 9, 2, 12, 0, 0, DateTimeKind.Utc));
        public TenantAdminInvitationService Invitations { get; }
        private readonly ErpRfqAutomationContext _serviceContext;

        public Harness()
        {
            _serviceContext = Database.ContextFor(null);
            Invitations = new TenantAdminInvitationService(
                _serviceContext, Sender,
                Options.Create(new NotificationsOptions { AppBaseUrl = "https://app.nexora.test" }),
                Options.Create(new TenantOnboardingOptions()),
                NullLogger<TenantAdminInvitationService>.Instance,
                Clock);
        }

        public async Task<Fixture> SeedTenantAsync(string slug, bool registerTenant = true)
        {
            await using var context = Database.ContextFor(null);
            var unit = new BusinessUnit
            {
                BusinessUnitCode = slug.ToUpperInvariant(), BusinessUnitName = $"Tenant {slug}",
                IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            };
            context.BusinessUnits.Add(unit);
            await context.SaveChangesAsync();

            var owner = Role(unit.Id, "SUPER_ADMIN", "Super Administrator", RoleRanks.Owner);
            var member = Role(unit.Id, "SALES_REP", "Sales Representative", RoleRanks.Member);
            context.SetupMasters.AddRange(owner, member);
            await context.SaveChangesAsync();

            context.Users.Add(new User
            {
                Id = CallerUserId, FirstName = "Owner", LastName = "Admin", Email = $"owner@{slug}.test",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()), ImageUrl = string.Empty,
                RoleId = owner.SetupId, Buid = unit.Id, IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
            });

            long tenantId = 0;
            if (registerTenant)
            {
                var tenant = new Tenant
                {
                    Name = $"Tenant {slug}", Slug = slug, Status = TenantStatus.Active,
                    PrimaryBusinessUnitId = unit.Id, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
                };
                context.Set<Tenant>().Add(tenant);
                await context.SaveChangesAsync();
                tenantId = tenant.Id;
            }
            await context.SaveChangesAsync();
            return new Fixture(tenantId, unit.Id, member.SetupId);
        }

        /// <summary>
        /// The controller over the SAME DbContext the invitation service holds, as in the composed
        /// application (both are scoped): the invitation joins the user's transaction.
        /// </summary>
        public UserController Controller(Fixture fixture) =>
            new(new UserRepository(_serviceContext), new StubWebHostEnvironment(), new StubRoleGate(),
                invitations: Invitations, context: _serviceContext)
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext
                    {
                        User = new ClaimsPrincipal(new ClaimsIdentity(
                        [
                            new Claim("businessUnitId", fixture.BusinessUnitId.ToString()),
                            new Claim("roleId", "1"),
                            new Claim(ClaimTypes.NameIdentifier, CallerUserId.ToString()),
                            new Claim(ClaimTypes.Email, "owner@noor-sons.test")
                        ], "test"))
                    }
                }
            };

        /// <summary>The cleartext of the most recently emailed link — read the way a recipient
        /// would, out of the email, since the service never returns it anywhere else.</summary>
        public string LastIssuedToken()
        {
            var body = Sender.Sent[^1].TextBody ?? Sender.Sent[^1].HtmlBody;
            var marker = "/activate/";
            var start = body.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
            var end = start;
            while (end < body.Length && (char.IsLetterOrDigit(body[end]) || body[end] is '-' or '_')) end++;
            return body[start..end];
        }

        public void Dispose()
        {
            _serviceContext.Dispose();
            Database.Dispose();
        }

        private static SetupMaster Role(long businessUnitId, string code, string name, short rank) => new()
        {
            SetupType = "Role", SetupCode = code, SetupValue = name, BusinessUnitId = businessUnitId,
            RoleRank = rank, IsActive = true, CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        };
    }

    private sealed class MutableClock(DateTime now) : TimeProvider
    {
        public DateTime Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => new(Now, TimeSpan.Zero);
    }

    private sealed class CapturingEmailSender : IEmailSender
    {
        public List<EmailMessage> Sent { get; } = [];

        public Task<EmailDeliveryReceipt?> SendAsync(EmailMessage message, CancellationToken ct = default)
        {
            Sent.Add(message);
            return Task.FromResult<EmailDeliveryReceipt?>(new EmailDeliveryReceipt("capture", $"captured-{Sent.Count}", DateTimeOffset.UtcNow));
        }
    }

    private sealed class StubRoleGate : IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) => Task.FromResult(true);
        public Task<short> GetRoleRankAsync(long roleId, long businessUnitId) => Task.FromResult(RoleRanks.Owner);
        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) => Task.FromResult(true);
        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId) => Task.FromResult(true);
    }

    private sealed class StubWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; }
            = new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
    }
}
