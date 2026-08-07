using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Provisioning a tenant must mint its founding Super Administrator in the same transaction.
///
/// <para><b>The defect this pins.</b> The platform portal's Provision created Tenant +
/// BusinessUnit + lifecycle statuses and STOPPED. Every portal-provisioned tenant was a shell
/// with no role and no user — nobody could ever log into it, so the founder's entire intended
/// journey (Platform Admin → customer account → that customer's Super Admin → sub accounts)
/// broke at its first step, invisibly: the tenant showed "Active" on the Tenants screen.</para>
/// </summary>
public sealed class TenantProvisioningFoundingAdminTests
{
    private sealed class NullAudit : IPlatformAuditService
    {
        public Task WriteAsync(ClaimsPrincipal actor, string action, string targetType, string targetId,
            object? metadata = null, long? actAsTenantId = null, HttpContext? httpContext = null,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private static TenantsController Controller(ErpRfqAutomationContext context)
    {
        var controller = new TenantsController(
            context, new NullAudit(), NullLogger<TenantsController>.Instance,
            new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            new MultiTenancy.TenantScopeAccessor(),
            ProvisioningFixture.Baseline(context),
            ProvisioningFixture.Invitations(context));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("email", "owner@nexora.app"), new Claim("platformRole", "Owner")], "Platform"))
            }
        };
        return controller;
    }

    /// <summary>
    /// A plan every fixture attaches, because a Billable tenant with none is refused outright —
    /// that refusal is the subject of <see cref="TenantProvisioningCommercialTests"/> and would
    /// only obscure the identity behaviour under test here.
    /// </summary>
    private static async Task<long> PlanIdAsync(ErpRfqAutomationContext context)
    {
        var plan = new Platform.Models.Plan
        {
            Code = $"pro-{Guid.NewGuid():N}", Name = "Pro", MonthlyPriceUsd = 499m
        };
        context.Set<Platform.Models.Plan>().Add(plan);
        await context.SaveChangesAsync();
        return plan.Id;
    }

    /// <summary>
    /// Defaults to the PASSWORD path. The product default is an emailed activation link, but the
    /// one-time-credential contract still has to hold for the customers who are onboarded on a
    /// call, and that is what these tests pin.
    /// </summary>
    private static ProvisionTenantRequest Request(
        string slug, string email, long planId, string? password = null) => new()
    {
        Name = $"Tenant {slug}",
        Slug = slug,
        PlanId = planId,
        BaseCurrencyCode = "USD",
        AdminEmail = email,
        AdminFirstName = "Founding",
        AdminLastName = "Admin",
        AdminActivation = AdminActivationMethods.Password,
        AdminPassword = password
    };

    [Fact]
    public async Task Provision_creates_a_tenant_whose_founding_admin_can_actually_authenticate()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var result = await Controller(context).Provision(
            Request("founding-admin-tenant", "ceo@customer.example", await PlanIdAsync(context)),
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var response = Assert.IsType<ProvisionTenantResponse>(created.Value);

        // The one-time generated credential: present because none was supplied…
        Assert.False(string.IsNullOrEmpty(response.FoundingAdmin.GeneratedPassword));
        Assert.Equal("ceo@customer.example", response.FoundingAdmin.Email);
        Assert.Equal("Super Administrator", response.FoundingAdmin.RoleName);

        await using var verify = db.ContextFor(null);
        var user = await verify.Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Email == "ceo@customer.example");
        var tenant = await verify.Set<Platform.Models.Tenant>().IgnoreQueryFilters()
            .SingleAsync(t => t.Slug == "founding-admin-tenant");

        // …and it VERIFIES against the stored hash — the credential handed to the customer is
        // the credential that logs in. Only the hash is stored; the cleartext exists nowhere.
        Assert.True(BCrypt.Net.BCrypt.Verify(response.FoundingAdmin.GeneratedPassword, user.PasswordHash));
        Assert.NotEqual(response.FoundingAdmin.GeneratedPassword, user.PasswordHash);

        // Bound to the tenant's primary business unit, active, and holding a role whose RANK is
        // Owner — the property PermissionHandler's rank rule actually reads. Without rank >= Admin
        // the founding admin would land in a tenant where every module check denies them.
        Assert.Equal(tenant.PrimaryBusinessUnitId, user.Buid);
        Assert.True(user.IsActive);
        var role = await verify.SetupMasters.IgnoreQueryFilters()
            .SingleAsync(s => s.SetupId == user.RoleId);
        Assert.Equal(RoleRanks.Owner, role.RoleRank);
        Assert.Equal("Role", role.SetupType);
        Assert.Equal(tenant.PrimaryBusinessUnitId, role.BusinessUnitId);
    }

    [Fact]
    public async Task The_default_path_invites_the_administrator_and_mints_no_credential_at_all()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var request = Request("invited-admin-tenant", "founder@customer.example", await PlanIdAsync(context));
        request.AdminActivation = null;   // the product default
        request.AdminPassword = null;

        var response = Assert.IsType<ProvisionTenantResponse>(Assert.IsType<CreatedAtActionResult>(
            (await Controller(context).Provision(request, CancellationToken.None)).Result).Value);

        // Nothing the operator can read aloud: no password exists yet, because the administrator
        // has not chosen one. That is the whole point of the invite path.
        Assert.Null(response.FoundingAdmin.GeneratedPassword);
        var invitation = Assert.IsType<AdminInvitationDto>(response.FoundingAdmin.Invitation);
        Assert.Contains("/activate/", invitation.ActivationUrl);
        Assert.True(invitation.ExpiresAtUtc > DateTime.UtcNow);

        await using var verify = db.ContextFor(null);
        var user = await verify.Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Email == "founder@customer.example");

        // Dormant until the link is redeemed. An account nobody can sign into must not occupy a
        // billable seat while the invitation is in flight, and the seats meter reads this flag.
        Assert.False(user.IsActive);

        // The stored hash is real but corresponds to a value that was generated, hashed and
        // discarded — there is no plaintext for it anywhere, so it cannot be handed over by
        // accident or recovered under pressure.
        Assert.False(string.IsNullOrWhiteSpace(user.PasswordHash));

        // Only the token's SHA-256 is persisted; the cleartext lives in the response and the
        // recipient's mailbox and nowhere else.
        var stored = await verify.TenantAdminInvitations.IgnoreQueryFilters()
            .SingleAsync(i => i.UserId == user.Id);
        Assert.DoesNotContain(invitation.ActivationUrl, stored.TokenHash);
    }

    [Fact]
    public async Task An_operator_supplied_password_is_used_and_never_echoed_back()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);

        var result = await Controller(context).Provision(
            Request("supplied-password-tenant", "ops@customer.example", await PlanIdAsync(context),
                password: "Agreed-Beforehand-9!"),
            CancellationToken.None);

        var response = Assert.IsType<ProvisionTenantResponse>(
            Assert.IsType<CreatedAtActionResult>(result.Result).Value);

        // Supplied by the operator, so the response must NOT carry it — GeneratedPassword is only
        // for credentials the operator has never seen.
        Assert.Null(response.FoundingAdmin.GeneratedPassword);

        await using var verify = db.ContextFor(null);
        var user = await verify.Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Email == "ops@customer.example");
        Assert.True(BCrypt.Net.BCrypt.Verify("Agreed-Beforehand-9!", user.PasswordHash));
    }

    [Fact]
    public async Task A_duplicate_admin_email_is_refused_with_a_clear_conflict_and_nothing_is_created()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var controller = Controller(context);
        var planId = await PlanIdAsync(context);

        Assert.IsType<CreatedAtActionResult>((await controller.Provision(
            Request("first-tenant", "shared@customer.example", planId), CancellationToken.None)).Result);

        // Users.Email is globally unique: one address, one account, one tenant. The second
        // provision must fail as a NAMED conflict, not a transaction rollback surfacing as
        // "Provisioning failed."
        var second = await controller.Provision(
            Request("second-tenant", "shared@customer.example", planId), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(second.Result);

        await using var verify = db.ContextFor(null);
        Assert.False(await verify.Set<Platform.Models.Tenant>().IgnoreQueryFilters()
            .AnyAsync(t => t.Slug == "second-tenant"));
        Assert.Equal(1, await verify.Users.IgnoreQueryFilters()
            .CountAsync(u => u.Email == "shared@customer.example"));
    }

    [Fact]
    public void Generated_passwords_are_long_diverse_and_free_of_lookalike_characters()
    {
        // This credential gets read aloud or retyped during customer handover, so 0/O and 1/l/I
        // are excluded by design. Sampled rather than proven — the alphabet is the contract.
        for (var i = 0; i < 200; i++)
        {
            var password = (string)typeof(TenantsController)
                .GetMethod("GenerateInitialPassword",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
                .Invoke(null, null)!;

            Assert.Equal(20, password.Length);
            Assert.Contains(password, char.IsUpper);
            Assert.Contains(password, char.IsLower);
            Assert.Contains(password, char.IsDigit);
            Assert.Contains(password, c => !char.IsLetterOrDigit(c));
            Assert.DoesNotContain(password, c => c is '0' or 'O' or '1' or 'l' or 'I' or 'o');
        }
    }
}
