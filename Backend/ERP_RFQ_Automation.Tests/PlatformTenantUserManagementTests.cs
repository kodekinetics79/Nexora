using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Administering a customer's own staff accounts from the platform plane.
///
/// <para>This endpoint family is a second door into <c>public."Users"</c>, opened by a product
/// decision — the primary door is still the tenant's own Super Administrator using
/// <c>Controllers/UserController</c>. A second door is only safe if it carries the same locks, so
/// these tests are written against the locks rather than the happy path: another tenant's roster
/// must never appear, an address that already exists anywhere must be refused, the plan's seat
/// ceiling must bind before an activation link is promised, a support administrator must not be
/// able to mint a second tenant owner, the last active administrator must not be removable, and
/// no mutation may survive a failed audit write.</para>
/// </summary>
public sealed class PlatformTenantUserManagementTests
{
    private const long ActingOperatorId = 9;
    private const string OperatorEmail = "operator@nexora.test";

    // ==== harness =============================================================================

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

    /// <summary>
    /// An audit trail that refuses to record. Stands in for the whole class of reasons the write
    /// can fail — a lost connection, a revoked grant — so the atomicity claim is tested by making
    /// the second half of the transaction fail rather than by reading the two rows and hoping.
    /// </summary>
    private sealed class RefusingAudit : IPlatformAuditService
    {
        public Task WriteAsync(ClaimsPrincipal actor, string action, string? targetType = null,
            string? targetId = null, object? metadata = null, long? actAsTenantId = null,
            HttpContext? httpContext = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("The audit trail is unavailable.");
    }

    private sealed record Fixture(
        long TenantId, long BusinessUnitId, long OwnerRoleId, long ManagerRoleId,
        long MemberRoleId, long FoundingAdminId, string FoundingAdminEmail);

    private static TenantUsersController Controller(
        ErpRfqAutomationContext context,
        string platformRole = nameof(PlatformRole.Owner),
        IEmailSender? sender = null,
        IPlatformAuditService? audit = null)
    {
        var invitations = new TenantAdminInvitationService(
            context,
            sender ?? new CapturingEmailSender(),
            Options.Create(new NotificationsOptions { AppBaseUrl = "https://app.nexora.test" }),
            Options.Create(new TenantOnboardingOptions()),
            NullLogger<TenantAdminInvitationService>.Instance);

        var entitlements = new EntitlementService(
            new TenantAccessService(context, new MemoryCache(new MemoryCacheOptions()),
                NullLogger<TenantAccessService>.Instance),
            context);

        return new TenantUsersController(
            context,
            audit ?? new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            invitations,
            entitlements,
            Options.Create(new TenantOnboardingOptions()),
            NullLogger<TenantUsersController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("sub", ActingOperatorId.ToString()),
                        new Claim("email", OperatorEmail),
                        new Claim(PlatformAuthConstants.PlatformRoleClaim, platformRole)
                    ], "Platform"))
                }
            }
        };
    }

    /// <summary>
    /// The shape provisioning leaves behind — a tenant, its primary business unit, the founding
    /// SUPER_ADMIN role at Owner rank and the one account holding it — plus the two starter roles
    /// <c>TenantBaselineCatalog</c> seeds below Admin.
    /// </summary>
    private static async Task<Fixture> SeedTenantAsync(
        TestDb db, string slug, long? planId = null, TenantStatus status = TenantStatus.Active)
    {
        await using var context = db.ContextFor(null);

        var businessUnit = new BusinessUnit
        {
            BusinessUnitCode = slug.ToUpperInvariant(),
            BusinessUnitName = $"Tenant {slug}",
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        context.BusinessUnits.Add(businessUnit);
        await context.SaveChangesAsync();

        var owner = NewRole(businessUnit.Id, "SUPER_ADMIN", "Super Administrator", RoleRanks.Owner);
        var manager = NewRole(businessUnit.Id, "SALES_MANAGER", "Sales Manager", RoleRanks.Manager);
        var member = NewRole(businessUnit.Id, "SALES_REP", "Sales Representative", RoleRanks.Member);
        context.SetupMasters.AddRange(owner, manager, member);

        var tenant = new Tenant
        {
            Name = $"Tenant {slug}",
            Slug = slug,
            Status = status,
            PlanId = planId,
            PrimaryBusinessUnitId = businessUnit.Id,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        context.Set<Tenant>().Add(tenant);
        await context.SaveChangesAsync();

        var email = $"founder@{slug}.test";
        var admin = new User
        {
            FirstName = "Founding",
            LastName = "Administrator",
            Email = email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
            ImageUrl = string.Empty,
            RoleId = owner.SetupId,
            Buid = businessUnit.Id,
            IsActive = true,
            CreatedBy = "tests",
            CreatedOn = DateTime.UtcNow
        };
        context.Users.Add(admin);

        // The acting operator has to exist: PlatformAuditService resolves the actor from the
        // token's sub claim and the audit row references it.
        if (!await context.Set<PlatformUser>().AnyAsync(u => u.Id == ActingOperatorId))
        {
            context.Set<PlatformUser>().Add(new PlatformUser
            {
                Id = ActingOperatorId,
                Email = OperatorEmail,
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
                PlatformRole = PlatformRole.Owner,
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });
        }
        await context.SaveChangesAsync();

        return new Fixture(tenant.Id, businessUnit.Id, owner.SetupId, manager.SetupId,
            member.SetupId, admin.Id, email);
    }

    private static SetupMaster NewRole(long businessUnitId, string code, string name, short rank) => new()
    {
        SetupType = "Role",
        SetupCode = code,
        SetupValue = name,
        BusinessUnitId = businessUnitId,
        RoleRank = rank,
        IsActive = true,
        CreatedBy = "tests",
        CreatedOn = DateTime.UtcNow
    };

    private static CreateTenantUserRequest NewUserRequest(long roleId, string email = "new.person@acme.test") => new()
    {
        Email = email,
        FirstName = "New",
        LastName = "Person",
        RoleId = roleId,
        Reason = "Second buyer joining before the founding administrator has signed in."
    };

    private static async Task<long> SeedPlanAsync(TestDb db, long id, int maxSeats)
    {
        await using var context = db.ContextFor(null);
        context.Set<Plan>().Add(new Plan
        {
            Id = id,
            Code = $"plan-{id}",
            Name = $"Plan {id}",
            MaxSeats = maxSeats,
            MaxDocsPerMonth = 1000,
            MaxConcurrentExtractionJobs = 2,
            Weight = 1
        });
        await context.SaveChangesAsync();
        return id;
    }

    // ==== authorization =======================================================================

    [Fact]
    public void Every_endpoint_is_gated_and_role_changes_are_owner_only()
    {
        var authorize = typeof(TenantUsersController).GetCustomAttributes<AuthorizeAttribute>().Single();
        Assert.Equal(PlatformPolicies.PlatformScope, authorize.Policy);

        foreach (var method in typeof(TenantUsersController)
                     .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                     .Where(m => m.GetCustomAttributes<HttpMethodAttribute>().Any()))
        {
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());

            var policy = method.GetCustomAttributes<AuthorizeAttribute>().Single().Policy;
            var expected = method.Name == nameof(TenantUsersController.ChangeUserRole)
                ? PlatformPolicies.Owner
                : PlatformPolicies.TenantAdmin;
            Assert.Equal(expected, policy);
        }
    }

    // ==== reads ===============================================================================

    [Fact]
    public async Task The_roster_is_scoped_to_the_tenants_own_business_unit()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        var globex = await SeedTenantAsync(db, "globex");
        await using var context = db.ContextFor(null);

        var result = await Controller(context).ListUsers(acme.TenantId, CancellationToken.None);

        var users = Assert.IsAssignableFrom<IEnumerable<TenantUserDto>>(
            Assert.IsType<OkObjectResult>(result.Result).Value).ToList();
        Assert.Equal([acme.FoundingAdminEmail], users.Select(u => u.Email));
        Assert.DoesNotContain(users, u => u.Email == globex.FoundingAdminEmail);
        Assert.Equal("Super Administrator", users[0].RoleName);
        Assert.Equal(RoleRanks.Owner, users[0].RoleRank);
    }

    [Fact]
    public async Task An_account_in_another_tenant_cannot_be_mutated_through_this_tenants_route()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        var globex = await SeedTenantAsync(db, "globex");
        await using var context = db.ContextFor(null);

        var result = await Controller(context).DeactivateUser(
            acme.TenantId, globex.FoundingAdminId,
            new TenantUserStatusChangeRequest { Reason = "Wrong tenant on purpose." },
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(result.Result);
        await using var verification = db.ContextFor(null);
        Assert.True((await verification.Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Id == globex.FoundingAdminId)).IsActive);
    }

    [Fact]
    public async Task The_role_list_marks_owner_rank_ungrantable_for_support_and_grantable_for_owner()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        await SeedTenantAsync(db, "globex");
        await using var context = db.ContextFor(null);

        var support = Assert.IsAssignableFrom<IEnumerable<TenantRoleDto>>(
            Assert.IsType<OkObjectResult>(
                (await Controller(context, nameof(PlatformRole.SupportAdmin))
                    .ListRoles(acme.TenantId, CancellationToken.None)).Result).Value).ToList();

        // Only this tenant's roles, and the seeded starter set is complete.
        Assert.Equal(["SUPER_ADMIN", "SALES_MANAGER", "SALES_REP"], support.Select(r => r.Code));
        Assert.Equal(1, support.Single(r => r.Code == "SUPER_ADMIN").ActiveUserCount);
        Assert.False(support.Single(r => r.Code == "SUPER_ADMIN").Grantable);
        Assert.NotNull(support.Single(r => r.Code == "SUPER_ADMIN").NotGrantableReason);
        Assert.True(support.Single(r => r.Code == "SALES_MANAGER").Grantable);

        var owner = Assert.IsAssignableFrom<IEnumerable<TenantRoleDto>>(
            Assert.IsType<OkObjectResult>(
                (await Controller(context).ListRoles(acme.TenantId, CancellationToken.None)).Result).Value);
        Assert.All(owner, role => Assert.True(role.Grantable));
    }

    // ==== create ==============================================================================

    [Fact]
    public async Task Creating_a_user_invites_them_and_leaves_the_account_dormant()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        var sender = new CapturingEmailSender();
        await using var context = db.ContextFor(null);

        var result = await Controller(context, sender: sender)
            .CreateUser(acme.TenantId, NewUserRequest(acme.MemberRoleId), CancellationToken.None);

        var response = Assert.IsType<CreateTenantUserResponse>(
            Assert.IsType<CreatedResult>(result.Result).Value);
        Assert.True(response.EmailDispatched);
        Assert.NotNull(response.Invitation);
        Assert.Equal("Pending", response.Invitation!.Status);
        // The link was transmitted, so no copy of it comes back through the API.
        Assert.Null(response.ActivationUrl);
        Assert.False(response.User.IsActive);
        Assert.True(response.User.AwaitingActivation);

        await using var verification = db.ContextFor(null);
        var created = await verification.Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Email == "new.person@acme.test");
        Assert.Equal(acme.BusinessUnitId, created.Buid);
        Assert.Equal(acme.MemberRoleId, created.RoleId);
        // Dormant AND stamped, so the seats meter never reads an unredeemed invitation as usage.
        Assert.False(created.IsActive);
        Assert.NotNull(created.DeactivatedAtUtc);

        var email = Assert.Single(sender.Sent);
        var audit = await verification.Set<PlatformAuditLog>()
            .SingleAsync(a => a.Action == "tenant.user.create");
        Assert.Equal(acme.TenantId, audit.ActAsTenantId);
        Assert.Contains("new.person@acme.test", audit.Metadata);
        // The token exists in exactly one place: the email. Never in the audit trail.
        var token = await verification.Set<TenantAdminInvitation>()
            .Where(i => i.UserId == created.Id).Select(i => i.TokenHash).SingleAsync();
        Assert.DoesNotContain(token, audit.Metadata ?? string.Empty);
        Assert.Contains("/activate/", email.TextBody ?? string.Empty);
    }

    [Fact]
    public async Task An_address_that_already_exists_anywhere_is_refused()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        var globex = await SeedTenantAsync(db, "globex");
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        // Same tenant, and case-insensitively: Sara@ and sara@ are one person to everybody
        // except a byte comparison.
        var sameTenant = await controller.CreateUser(acme.TenantId,
            NewUserRequest(acme.MemberRoleId, acme.FoundingAdminEmail.ToUpperInvariant()),
            CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(sameTenant.Result);

        // And an address held by a DIFFERENT customer, which is the one that matters: one
        // address maps to one account on one tenant.
        var otherTenant = await controller.CreateUser(acme.TenantId,
            NewUserRequest(acme.MemberRoleId, globex.FoundingAdminEmail), CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(otherTenant.Result);

        await using var verification = db.ContextFor(null);
        Assert.Equal(2, await verification.Users.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Support_cannot_grant_a_role_that_owns_the_tenant()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        await using var context = db.ContextFor(null);
        var support = Controller(context, nameof(PlatformRole.SupportAdmin));

        var refused = await support.CreateUser(
            acme.TenantId, NewUserRequest(acme.OwnerRoleId), CancellationToken.None);

        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsType<ObjectResult>(refused.Result).StatusCode);
        await using (var verification = db.ContextFor(null))
        {
            Assert.False(await verification.Users.IgnoreQueryFilters()
                .AnyAsync(u => u.Email == "new.person@acme.test"));
        }

        // The same operator may staff the customer's sales desk — the ceiling is the rank, not
        // the endpoint.
        var permitted = await support.CreateUser(
            acme.TenantId, NewUserRequest(acme.ManagerRoleId), CancellationToken.None);
        Assert.IsType<CreatedResult>(permitted.Result);
    }

    [Fact]
    public async Task A_role_belonging_to_another_tenant_is_not_assignable()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        var globex = await SeedTenantAsync(db, "globex");
        await using var context = db.ContextFor(null);

        var result = await Controller(context).CreateUser(
            acme.TenantId, NewUserRequest(globex.MemberRoleId), CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task The_plans_seat_ceiling_binds_before_an_activation_link_is_promised()
    {
        using var db = new TestDb();
        var planId = await SeedPlanAsync(db, 4001, maxSeats: 1);
        var acme = await SeedTenantAsync(db, "acme", planId);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).CreateUser(
            acme.TenantId, NewUserRequest(acme.MemberRoleId), CancellationToken.None);

        // Refused even though the invited account would be created INACTIVE and consume no seat
        // today: redemption flips IsActive with no entitlement check of its own, so this is the
        // only moment the plan can be consulted at all.
        var denial = Assert.IsType<ObjectResult>(result.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, denial.StatusCode);
        await using var verification = db.ContextFor(null);
        Assert.False(await verification.Users.IgnoreQueryFilters()
            .AnyAsync(u => u.Email == "new.person@acme.test"));
        Assert.False(await verification.Set<TenantAdminInvitation>().AnyAsync());
    }

    [Fact]
    public async Task An_operator_set_password_is_owner_only_and_is_recorded_as_one()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        await using var context = db.ContextFor(null);

        var request = NewUserRequest(acme.MemberRoleId);
        request.Activation = TenantUserActivationMethods.Password;
        request.Password = "Harbour-Crane-7#x2";

        var refused = await Controller(context, nameof(PlatformRole.SupportAdmin))
            .CreateUser(acme.TenantId, request, CancellationToken.None);
        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsType<ObjectResult>(refused.Result).StatusCode);

        var allowed = await Controller(context).CreateUser(acme.TenantId, request, CancellationToken.None);
        var response = Assert.IsType<CreateTenantUserResponse>(
            Assert.IsType<CreatedResult>(allowed.Result).Value);
        Assert.Null(response.Invitation);
        Assert.True(response.User.IsActive);

        await using var verification = db.ContextFor(null);
        var created = await verification.Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Email == "new.person@acme.test");
        Assert.True(BCrypt.Net.BCrypt.Verify("Harbour-Crane-7#x2", created.PasswordHash));
        var audit = await verification.Set<PlatformAuditLog>()
            .SingleAsync(a => a.Action == "tenant.user.create");
        Assert.Contains("\"PasswordSetByOperator\":true", audit.Metadata);
        Assert.DoesNotContain("Harbour-Crane-7#x2", audit.Metadata);
    }

    [Fact]
    public async Task A_failed_audit_write_takes_the_new_account_with_it()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        await using var context = db.ContextFor(null);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Controller(context, audit: new RefusingAudit())
                .CreateUser(acme.TenantId, NewUserRequest(acme.MemberRoleId), CancellationToken.None));

        await using var verification = db.ContextFor(null);
        // Neither the account nor its activation link survived: the mutation and its trail are
        // one transaction, so "created but unaudited" is not a reachable state.
        Assert.False(await verification.Users.IgnoreQueryFilters()
            .AnyAsync(u => u.Email == "new.person@acme.test"));
        Assert.False(await verification.Set<TenantAdminInvitation>().AnyAsync());
    }

    // ==== lifecycle ===========================================================================

    [Fact]
    public async Task The_last_active_administrator_can_be_neither_deactivated_nor_demoted()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        await using var context = db.ContextFor(null);
        var controller = Controller(context);
        var reason = new TenantUserStatusChangeRequest { Reason = "Left the company." };

        var deactivation = await controller.DeactivateUser(
            acme.TenantId, acme.FoundingAdminId, reason, CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(deactivation.Result);

        var demotion = await controller.ChangeUserRole(acme.TenantId, acme.FoundingAdminId,
            new ChangeTenantUserRoleRequest { RoleId = acme.ManagerRoleId, Reason = "Left the company." },
            CancellationToken.None);
        Assert.IsType<ConflictObjectResult>(demotion.Result);

        await using var verification = db.ContextFor(null);
        var admin = await verification.Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Id == acme.FoundingAdminId);
        Assert.True(admin.IsActive);
        Assert.Equal(acme.OwnerRoleId, admin.RoleId);
    }

    [Fact]
    public async Task A_second_active_administrator_releases_the_lockout_guard()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        await using (var seed = db.ContextFor(null))
        {
            seed.Users.Add(new User
            {
                FirstName = "Second",
                LastName = "Administrator",
                Email = "second.admin@acme.test",
                PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
                ImageUrl = string.Empty,
                RoleId = acme.OwnerRoleId,
                Buid = acme.BusinessUnitId,
                IsActive = true,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var result = await Controller(context).DeactivateUser(acme.TenantId, acme.FoundingAdminId,
            new TenantUserStatusChangeRequest { Reason = "Handed the tenant over." }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        await using var verification = db.ContextFor(null);
        var admin = await verification.Users.IgnoreQueryFilters()
            .SingleAsync(u => u.Id == acme.FoundingAdminId);
        Assert.False(admin.IsActive);
        Assert.NotNull(admin.DeactivatedAtUtc);
        Assert.Equal("tenant.user.deactivate",
            (await verification.Set<PlatformAuditLog>()
                .SingleAsync(a => a.Action.StartsWith("tenant.user.deactivate"))).Action);
    }

    [Fact]
    public async Task Deactivation_kills_the_activation_link_that_would_otherwise_undo_it()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        var created = Assert.IsType<CreateTenantUserResponse>(Assert.IsType<CreatedResult>(
            (await controller.CreateUser(acme.TenantId, NewUserRequest(acme.MemberRoleId),
                CancellationToken.None)).Result).Value);

        var result = await controller.DeactivateUser(acme.TenantId, created.User.Id,
            new TenantUserStatusChangeRequest { Reason = "Joined a different employer before starting." },
            CancellationToken.None);

        var dto = Assert.IsType<TenantUserDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal("Revoked", dto.Invitation!.Status);

        await using var verification = db.ContextFor(null);
        // Redemption sets IsActive = true. A link left live here would let its holder switch a
        // deactivated account back on, with no operator involved.
        var invitation = await verification.Set<TenantAdminInvitation>()
            .SingleAsync(i => i.UserId == created.User.Id);
        Assert.NotNull(invitation.RevokedAtUtc);
        Assert.Null(invitation.RedeemedAtUtc);
    }

    [Fact]
    public async Task Reactivation_restores_the_account_and_clears_the_deactivation_stamp()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        await using var context = db.ContextFor(null);
        var controller = Controller(context);
        var created = Assert.IsType<CreateTenantUserResponse>(Assert.IsType<CreatedResult>(
            (await controller.CreateUser(acme.TenantId, NewUserRequest(acme.MemberRoleId),
                CancellationToken.None)).Result).Value);

        var result = await controller.ReactivateUser(acme.TenantId, created.User.Id,
            new TenantUserStatusChangeRequest { Reason = "Start date confirmed after all." },
            CancellationToken.None);

        var dto = Assert.IsType<TenantUserDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.True(dto.IsActive);
        Assert.Null(dto.DeactivatedAtUtc);
        // Still holds no credential anybody knows — the console has to say so rather than
        // implying the account is usable.
        Assert.True(dto.AwaitingActivation);

        await using var verification = db.ContextFor(null);
        Assert.Contains("tenant.user.reactivate",
            await verification.Set<PlatformAuditLog>().Select(a => a.Action).ToListAsync());
    }

    [Fact]
    public async Task A_role_change_is_audited_with_both_ends_of_the_move()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        await using var context = db.ContextFor(null);
        var controller = Controller(context);
        var created = Assert.IsType<CreateTenantUserResponse>(Assert.IsType<CreatedResult>(
            (await controller.CreateUser(acme.TenantId, NewUserRequest(acme.MemberRoleId),
                CancellationToken.None)).Result).Value);

        var result = await controller.ChangeUserRole(acme.TenantId, created.User.Id,
            new ChangeTenantUserRoleRequest
            {
                RoleId = acme.ManagerRoleId,
                Reason = "Promoted to run the sales desk during the pilot."
            }, CancellationToken.None);

        var dto = Assert.IsType<TenantUserDto>(Assert.IsType<OkObjectResult>(result.Result).Value);
        Assert.Equal(acme.ManagerRoleId, dto.RoleId);
        Assert.Equal(RoleRanks.Manager, dto.RoleRank);

        await using var verification = db.ContextFor(null);
        var audit = await verification.Set<PlatformAuditLog>()
            .SingleAsync(a => a.Action == "tenant.user.role.change");
        Assert.Contains($"\"FromRoleId\":{acme.MemberRoleId}", audit.Metadata);
        Assert.Contains($"\"ToRoleId\":{acme.ManagerRoleId}", audit.Metadata);
        Assert.Equal(acme.TenantId, audit.ActAsTenantId);
    }

    [Fact]
    public async Task An_archived_tenant_gains_no_new_accounts()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme", status: TenantStatus.Archived);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).CreateUser(
            acme.TenantId, NewUserRequest(acme.MemberRoleId), CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task An_unknown_tenant_is_not_found_rather_than_empty()
    {
        using var db = new TestDb();
        await SeedTenantAsync(db, "acme");
        await using var context = db.ContextFor(null);

        Assert.IsType<NotFoundResult>(
            (await Controller(context).ListUsers(9_999, CancellationToken.None)).Result);
        Assert.IsType<NotFoundResult>(
            (await Controller(context).ListRoles(9_999, CancellationToken.None)).Result);
    }

    // ==== token revocation (docs/design/token-revocation.md) ====================================

    [Fact]
    public async Task Deactivating_a_user_rotates_the_token_revocation_stamp()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        await using var context = db.ContextFor(null);
        var created = await CreateMemberAsync(db, acme, "leaver@acme.example");
        var before = created.SecurityStamp;

        var result = await Controller(context).DeactivateUser(acme.TenantId, created.Id,
            new TenantUserStatusChangeRequest { Reason = "Left the company." }, CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);

        await using var verification = db.ContextFor(null);
        var row = await verification.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == created.Id);
        Assert.False(row.IsActive);
        Assert.NotEqual(before, row.SecurityStamp);
    }

    [Fact]
    public async Task Changing_a_users_role_rotates_the_token_revocation_stamp()
    {
        using var db = new TestDb();
        var acme = await SeedTenantAsync(db, "acme");
        await using var context = db.ContextFor(null);
        var created = await CreateMemberAsync(db, acme, "promoted@acme.example");
        var before = created.SecurityStamp;

        var result = await Controller(context).ChangeUserRole(acme.TenantId, created.Id,
            new ChangeTenantUserRoleRequest { RoleId = acme.ManagerRoleId, Reason = "Promoted." },
            CancellationToken.None);
        Assert.IsType<OkObjectResult>(result.Result);

        await using var verification = db.ContextFor(null);
        var row = await verification.Users.IgnoreQueryFilters().SingleAsync(u => u.Id == created.Id);
        Assert.Equal(acme.ManagerRoleId, row.RoleId);
        Assert.NotEqual(before, row.SecurityStamp);
    }

    private static async Task<User> CreateMemberAsync(TestDb db, Fixture tenant, string email)
    {
        await using var context = db.ContextFor(null);
        var user = new User
        {
            Buid = tenant.BusinessUnitId, RoleId = tenant.MemberRoleId, FirstName = "Member", LastName = "User",
            Email = email, PasswordHash = "not-used", ImageUrl = "n/a", IsActive = true,
            CreatedBy = "tests", CreatedOn = DateTime.UtcNow
        };
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }
}
