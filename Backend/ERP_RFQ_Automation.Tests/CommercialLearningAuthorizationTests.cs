using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialLearning;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

public sealed class CommercialLearningAuthorizationTests
{
    private const long TenantId = 98_501;
    private const long ActorUserId = 98_502;
    private const long PeerUserId = 98_503;

    [Fact]
    public async Task Standard_rep_cannot_read_peer_commercial_memory()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        SeedUsers(context);
        await context.SaveChangesAsync();
        var controller = Controller(context, manager: false);

        var response = await controller.SalesRep(PeerUserId, default);

        Assert.IsType<ForbidResult>(response.Result);
    }

    [Fact]
    public async Task Standard_rep_collection_is_restricted_to_own_commercial_memory()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        SeedUsers(context);
        await context.SaveChangesAsync();
        var controller = Controller(context, manager: false);

        var response = await controller.SalesReps(cancellationToken: default);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var memory = Assert.Single(Assert.IsAssignableFrom<IReadOnlyCollection<SalesRepCommercialMemory>>(ok.Value));
        Assert.Equal(ActorUserId, memory.SalesRepUserId);
    }

    [Fact]
    public async Task Manager_can_read_peer_commercial_memory()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        SeedUsers(context);
        await context.SaveChangesAsync();
        var controller = Controller(context, manager: true);

        var response = await controller.SalesRep(PeerUserId, default);

        var ok = Assert.IsType<OkObjectResult>(response.Result);
        var memory = Assert.IsType<SalesRepCommercialMemory>(ok.Value);
        Assert.Equal(PeerUserId, memory.SalesRepUserId);
    }

    [Fact]
    public async Task Manager_cannot_read_a_rep_outside_their_team()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(TenantId);
        SeedUsers(context);
        await context.SaveChangesAsync();
        var controller = Controller(context, manager: true);
        controller = WithScope(controller, context, new TestScopeResolver(AccountScopeTier.ManagedScope, [ActorUserId]));

        var response = await controller.SalesRep(PeerUserId, default);

        Assert.IsType<ForbidResult>(response.Result);
    }

    private static CommercialLearningController WithScope(
        CommercialLearningController template, ErpRfqAutomationContext context, IAccountTeamScopeResolver scope) =>
        new(new CommercialLearningService(context), null!, scope) { ControllerContext = template.ControllerContext };

    private static CommercialLearningController Controller(ErpRfqAutomationContext context, bool manager)
    {
        // A manager's team here holds both reps; ManagerOutsideTeam below narrows it to the actor.
        var controller = new CommercialLearningController(
            new CommercialLearningService(context), null!,
            new TestScopeResolver(manager ? AccountScopeTier.ManagedScope : AccountScopeTier.AssignedAccounts,
                manager ? [ActorUserId, PeerUserId] : [ActorUserId]));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("businessUnitId", TenantId.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, ActorUserId.ToString()),
                    new Claim("roleId", "98504")
                ], "test"))
            }
        };
        return controller;
    }

    private static void SeedUsers(ErpRfqAutomationContext context)
    {
        Seed.EnsureBusinessUnit(context, TenantId);
        context.Users.AddRange(
            User(ActorUserId, "actor@nexora.test"), User(PeerUserId, "peer@nexora.test"));
    }

    private static User User(long id, string email) => new()
    {
        Id = id,
        Buid = TenantId,
        FirstName = "Sales",
        LastName = id.ToString(),
        Email = email,
        PasswordHash = "not-used",
        ImageUrl = "not-used",
        IsActive = true,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };

    private sealed class TestScopeResolver(AccountScopeTier tier, long[] userIds) : IAccountTeamScopeResolver
    {
        public Task<AccountTeamScope> ResolveAsync(
            long userId, long roleId, long businessUnitId, DateTime asOfUtc, CancellationToken ct = default)
            => Task.FromResult(new AccountTeamScope(tier, userId, [], userIds));
    }
}
