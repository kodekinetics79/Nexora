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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class PlatformIamInvariantTests
{
    [Fact]
    public async Task Ai_policy_mutation_requires_Owner_and_refuses_SupportAdmin()
    {
        var action = typeof(TenantsController).GetMethod(nameof(TenantsController.UpdateAiPolicy))!;
        var actionGate = Assert.Single(action.GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(PlatformPolicies.Owner, actionGate.Policy);

        Assert.True(await SatisfiesAsync(PlatformPolicies.Owner, PlatformRole.Owner));
        Assert.False(await SatisfiesAsync(PlatformPolicies.Owner, PlatformRole.SupportAdmin));
        Assert.False(await SatisfiesAsync(PlatformPolicies.Owner, PlatformRole.BillingAdmin));
        Assert.False(await SatisfiesAsync(PlatformPolicies.Owner, PlatformRole.ReadOnlyOps));
    }

    [Fact]
    public async Task Sequential_owner_demotions_cannot_remove_the_final_active_Owner()
    {
        using var db = new TestDb();
        var first = await SeedOwner(db, "first-owner@example.test");
        var second = await SeedOwner(db, "second-owner@example.test");

        await using (var firstContext = db.ContextFor(null))
        {
            var result = await Controller(firstContext, first).ChangeRole(first,
                new ChangePlatformUserRoleRequest { Role = nameof(PlatformRole.SupportAdmin) },
                CancellationToken.None);
            Assert.IsType<OkObjectResult>(result.Result);
        }

        await using (var secondContext = db.ContextFor(null))
        {
            var result = await Controller(secondContext, second).ChangeRole(second,
                new ChangePlatformUserRoleRequest { Role = nameof(PlatformRole.SupportAdmin) },
                CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(result.Result);
        }

        await using var verification = db.ContextFor(null);
        Assert.Equal(1, await verification.Set<PlatformUser>()
            .CountAsync(user => user.IsActive && user.PlatformRole == PlatformRole.Owner));
    }

    [Fact]
    public async Task Sequential_cross_deactivations_cannot_remove_the_final_active_Owner()
    {
        using var db = new TestDb();
        var first = await SeedOwner(db, "first-deactivate@example.test");
        var second = await SeedOwner(db, "second-deactivate@example.test");

        await using (var firstContext = db.ContextFor(null))
        {
            var result = await Controller(firstContext, first).Deactivate(second, CancellationToken.None);
            Assert.IsType<OkObjectResult>(result.Result);
        }

        await using (var secondContext = db.ContextFor(null))
        {
            var result = await Controller(secondContext, second).Deactivate(first, CancellationToken.None);
            Assert.IsType<ConflictObjectResult>(result.Result);
        }

        await using var verification = db.ContextFor(null);
        Assert.Equal(1, await verification.Set<PlatformUser>()
            .CountAsync(user => user.IsActive && user.PlatformRole == PlatformRole.Owner));
    }

    private static async Task<bool> SatisfiesAsync(string policyName, PlatformRole role)
    {
        using var services = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => options.AddPlatformPolicies())
            .BuildServiceProvider();
        var policy = await services.GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(policyName);
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(PlatformAuthConstants.ScopeClaim, PlatformAuthConstants.PlatformScopeValue),
            new Claim(PlatformAuthConstants.PlatformRoleClaim, role.ToString()),
            new Claim(PlatformAuthConstants.AuthenticationMethodClaim,
                PlatformAuthConstants.MfaAuthenticationMethod)
        ], PlatformAuthConstants.Scheme));
        return (await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, null, policy!)).Succeeded;
    }

    internal static PlatformUsersController Controller(ErpRfqAutomationContext context, long actorId) => new(
        context, new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance))
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", actorId.ToString()),
                    new Claim("email", $"owner-{actorId}@example.test")
                ], PlatformAuthConstants.Scheme))
            }
        }
    };

    internal static async Task<long> SeedOwner(TestDb db, string email)
    {
        await using var context = db.ContextFor(null);
        var owner = NewOwner(email);
        context.Set<PlatformUser>().Add(owner);
        await context.SaveChangesAsync();
        return owner.Id;
    }

    internal static PlatformUser NewOwner(string email) => new()
    {
        Email = email,
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(Guid.NewGuid().ToString()),
        PlatformRole = PlatformRole.Owner,
        IsActive = true,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };
}
