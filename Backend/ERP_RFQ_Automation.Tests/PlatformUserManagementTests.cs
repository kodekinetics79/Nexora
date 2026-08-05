using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Platform operator management: Owner-only authorization, unique emails, the
/// self-deactivation and last-active-Owner safety rails, audited mutations, and
/// login rejection for deactivated accounts.
/// </summary>
public sealed class PlatformUserManagementTests
{
    private const long ActingOwnerId = 7;

    [Fact]
    public void Every_platform_user_endpoint_requires_the_owner_policy()
    {
        var authorize = typeof(PlatformUsersController)
            .GetCustomAttributes<AuthorizeAttribute>()
            .Single();
        Assert.Equal(PlatformPolicies.Owner, authorize.Policy);

        // No action may weaken the class-level Owner gate.
        foreach (var method in typeof(PlatformUsersController)
                     .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
            Assert.DoesNotContain(method.GetCustomAttributes<AuthorizeAttribute>(),
                a => a.Policy != PlatformPolicies.Owner);
        }
    }

    [Fact]
    public async Task Create_persists_a_bcrypt_hashed_active_user_and_audits()
    {
        using var db = new TestDb();
        await SeedOwner(db, ActingOwnerId);
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        var result = await controller.Create(new CreatePlatformUserRequest
        {
            Email = "ops@example.test",
            Password = "correct-horse-battery",
            Role = "SupportAdmin",
            DisplayName = "Ops Person"
        }, CancellationToken.None);

        var dto = Assert.IsType<PlatformUserDto>(Assert.IsType<CreatedAtActionResult>(result.Result).Value);
        Assert.Equal("SupportAdmin", dto.PlatformRole);
        Assert.True(dto.IsActive);

        await using var verification = db.ContextFor(null);
        var user = await verification.Set<PlatformUser>().SingleAsync(u => u.Email == "ops@example.test");
        Assert.True(BCrypt.Net.BCrypt.Verify("correct-horse-battery", user.PasswordHash));
        var audit = await verification.Set<PlatformAuditLog>().SingleAsync();
        Assert.Equal("platform-user.create", audit.Action);
        Assert.DoesNotContain("correct-horse-battery", audit.Metadata);
    }

    [Fact]
    public async Task Create_rejects_a_duplicate_email_case_insensitively()
    {
        using var db = new TestDb();
        await SeedOwner(db, ActingOwnerId, email: "Existing@Example.test");
        await using var context = db.ContextFor(null);

        var result = await Controller(context).Create(new CreatePlatformUserRequest
        {
            Email = "existing@example.TEST",
            Password = "irrelevant-password",
            Role = "ReadOnlyOps"
        }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Create_rejects_an_unknown_role()
    {
        using var db = new TestDb();
        await SeedOwner(db, ActingOwnerId);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).Create(new CreatePlatformUserRequest
        {
            Email = "new@example.test",
            Password = "irrelevant-password",
            Role = "SuperDuperAdmin"
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task The_last_active_owner_cannot_be_demoted()
    {
        using var db = new TestDb();
        var ownerId = await SeedOwner(db, ActingOwnerId);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).ChangeRole(ownerId,
            new ChangePlatformUserRoleRequest { Role = "ReadOnlyOps" }, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        await using var verification = db.ContextFor(null);
        Assert.Equal(PlatformRole.Owner,
            (await verification.Set<PlatformUser>().SingleAsync(u => u.Id == ownerId)).PlatformRole);
    }

    [Fact]
    public async Task An_owner_can_be_demoted_when_another_active_owner_remains()
    {
        using var db = new TestDb();
        await SeedOwner(db, ActingOwnerId);
        var secondOwnerId = await SeedOwner(db, 0, email: "second-owner@example.test");
        await using var context = db.ContextFor(null);

        var result = await Controller(context).ChangeRole(secondOwnerId,
            new ChangePlatformUserRoleRequest { Role = "BillingAdmin" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        await using var verification = db.ContextFor(null);
        Assert.Equal(PlatformRole.BillingAdmin,
            (await verification.Set<PlatformUser>().SingleAsync(u => u.Id == secondOwnerId)).PlatformRole);
        Assert.Equal("platform-user.role.change",
            (await verification.Set<PlatformAuditLog>().SingleAsync()).Action);
    }

    [Fact]
    public async Task An_owner_cannot_deactivate_their_own_account()
    {
        using var db = new TestDb();
        var selfId = await SeedOwner(db, ActingOwnerId);
        await SeedOwner(db, 0, email: "other-owner@example.test");
        await using var context = db.ContextFor(null);

        var result = await Controller(context).Deactivate(selfId, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
        await using var verification = db.ContextFor(null);
        Assert.True((await verification.Set<PlatformUser>().SingleAsync(u => u.Id == selfId)).IsActive);
    }

    [Fact]
    public async Task The_last_active_owner_cannot_be_deactivated()
    {
        using var db = new TestDb();
        long onlyOwnerId;
        await using (var seed = db.ContextFor(null))
        {
            // The acting token (sub=7) belongs to nobody in the table; the single
            // active Owner row is the deactivation target.
            var owner = NewUser("only-owner@example.test", PlatformRole.Owner);
            seed.Set<PlatformUser>().Add(owner);
            await seed.SaveChangesAsync();
            onlyOwnerId = owner.Id;
        }

        await using var context = db.ContextFor(null);
        var result = await Controller(context).Deactivate(onlyOwnerId, CancellationToken.None);

        Assert.IsType<ConflictObjectResult>(result.Result);
    }

    [Fact]
    public async Task Deactivate_and_reactivate_round_trip_with_audits()
    {
        using var db = new TestDb();
        await SeedOwner(db, ActingOwnerId);
        var targetId = await SeedUser(db, "support@example.test", PlatformRole.SupportAdmin);
        await using var context = db.ContextFor(null);
        var controller = Controller(context);

        Assert.IsType<OkObjectResult>((await controller.Deactivate(targetId, CancellationToken.None)).Result);
        await using (var mid = db.ContextFor(null))
        {
            Assert.False((await mid.Set<PlatformUser>().SingleAsync(u => u.Id == targetId)).IsActive);
        }

        Assert.IsType<OkObjectResult>((await controller.Reactivate(targetId, CancellationToken.None)).Result);
        await using var verification = db.ContextFor(null);
        Assert.True((await verification.Set<PlatformUser>().SingleAsync(u => u.Id == targetId)).IsActive);
        var actions = await verification.Set<PlatformAuditLog>().Select(a => a.Action).ToListAsync();
        Assert.Contains("platform-user.deactivate", actions);
        Assert.Contains("platform-user.reactivate", actions);
    }

    [Fact]
    public async Task Admin_password_reset_replaces_the_hash_and_never_logs_the_secret()
    {
        using var db = new TestDb();
        await SeedOwner(db, ActingOwnerId);
        var targetId = await SeedUser(db, "reset-me@example.test", PlatformRole.ReadOnlyOps);
        await using var context = db.ContextFor(null);

        var result = await Controller(context).ResetPassword(targetId,
            new ResetPlatformUserPasswordRequest { NewPassword = "brand-new-secret-42" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        await using var verification = db.ContextFor(null);
        var user = await verification.Set<PlatformUser>().SingleAsync(u => u.Id == targetId);
        Assert.True(BCrypt.Net.BCrypt.Verify("brand-new-secret-42", user.PasswordHash));
        var audit = await verification.Set<PlatformAuditLog>().SingleAsync(a => a.Action == "platform-user.password.reset");
        Assert.DoesNotContain("brand-new-secret-42", audit.Metadata);
        Assert.DoesNotContain(user.PasswordHash, audit.Metadata ?? string.Empty);
    }

    [Fact]
    public async Task Login_rejects_a_deactivated_platform_user()
    {
        using var db = new TestDb();
        await using (var seed = db.ContextFor(null))
        {
            var user = NewUser("inactive@example.test", PlatformRole.SupportAdmin);
            user.PasswordHash = BCrypt.Net.BCrypt.HashPassword("valid-password-123");
            user.IsActive = false;
            seed.Set<PlatformUser>().Add(user);
            await seed.SaveChangesAsync();
        }

        await using var context = db.ContextFor(null);
        var authService = new PlatformAuthService(context, AuthConfiguration(),
            NullLogger<PlatformAuthService>.Instance);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => authService.LoginAsync(
            new PlatformLoginRequest { Email = "inactive@example.test", Password = "valid-password-123" }));
    }

    private static IConfiguration AuthConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Jwt:Key"] = "tenant-signing-key-that-is-at-least-32-bytes",
            ["Jwt:Issuer"] = "nexora-tests",
            ["Jwt:Audience"] = "RFQ"
        }).Build();

    private static PlatformUsersController Controller(ErpRfqAutomationContext context) => new(
        context, new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", ActingOwnerId.ToString()),
                    new Claim("email", "acting-owner@example.test")
                ], "Platform"))
            }
        }
    };

    private static PlatformUser NewUser(string email, PlatformRole role) => new()
    {
        Email = email,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
        PlatformRole = role,
        IsActive = true,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };

    /// <summary>Seeds an active Owner. When <paramref name="id"/> is non-zero it is forced so the
    /// row matches the acting token's sub claim.</summary>
    private static async Task<long> SeedOwner(TestDb db, long id, string email = "acting-owner@example.test")
    {
        await using var seed = db.ContextFor(null);
        var owner = NewUser(email, PlatformRole.Owner);
        if (id > 0) owner.Id = id;
        seed.Set<PlatformUser>().Add(owner);
        await seed.SaveChangesAsync();
        return owner.Id;
    }

    private static async Task<long> SeedUser(TestDb db, string email, PlatformRole role)
    {
        await using var seed = db.ContextFor(null);
        var user = NewUser(email, role);
        seed.Set<PlatformUser>().Add(user);
        await seed.SaveChangesAsync();
        return user.Id;
    }
}
