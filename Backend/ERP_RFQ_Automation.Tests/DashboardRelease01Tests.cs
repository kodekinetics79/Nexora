using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.Dashboard;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

public sealed class DashboardRelease01Tests
{
    private const long BusinessUnitId = 501;
    private static readonly DateTime From = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime GeneratedAt = new(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Release01Endpoint_RequiresDashboardViewPermission()
    {
        var method = typeof(DashboardController).GetMethod(nameof(DashboardController.GetRelease01));
        var permission = Assert.Single(method!.GetCustomAttributes<RequireModulePermissionAttribute>());

        Assert.Equal("Dashboard", permission.ModuleName);
        Assert.Equal(PermissionAction.View, permission.Action);
    }

    [Theory]
    [InlineData(true, "tenant", null)]
    [InlineData(false, "assigned_to_me", 77L)]
    public async Task Release01Endpoint_DerivesRoleScopeAndTenantOnlyFromClaims(
        bool manager, string expectedScope, long? expectedOwner)
    {
        var repository = new CapturingDashboardRepository();
        var endpointTo = DateTime.UtcNow.AddMinutes(-1);
        var endpointFrom = endpointTo.AddDays(-30);
        var controller = new DashboardController(repository, new StubRoleGate(manager))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = Principal(BusinessUnitId, roleId: 9, userId: 77)
                }
            }
        };

        var result = await controller.GetRelease01(endpointFrom, endpointTo, default);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(BusinessUnitId, repository.BusinessUnitId);
        Assert.Equal(expectedScope, repository.RoleScope);
        Assert.Equal(expectedOwner, repository.OwnerUserId);
        Assert.Equal(endpointFrom, repository.From);
        Assert.Equal(endpointTo, repository.To);
    }

    [Fact]
    public async Task Release01Endpoint_RejectsMissingAuthenticatedScopeClaims()
    {
        var controller = new DashboardController(new CapturingDashboardRepository(), new StubRoleGate(true))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("businessUnitId", BusinessUnitId.ToString())], "test"))
                }
            }
        };

        var result = await controller.GetRelease01(From, To, default);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Release01Endpoint_DateOnlyTodayIncludesRecordsThroughGeneratedAt()
    {
        var repository = new CapturingDashboardRepository();
        var today = DateTime.UtcNow.Date;
        var controller = new DashboardController(repository, new StubRoleGate(true))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = Principal(BusinessUnitId, roleId: 9, userId: 77)
            }}
        };

        var result = await controller.GetRelease01(today.AddDays(-30), today, default);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(repository.To > today);
        Assert.True(repository.To <= DateTime.UtcNow);
    }

    [Fact]
    public async Task Repository_ReconcilesQualificationKpisAndAppliesOwnerScope()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(BusinessUnitId);
        var first = SeedLead(context, 1, ownerId: 77, createdAt: From.AddDays(1));
        var second = SeedLead(context, 2, ownerId: 88, createdAt: From.AddDays(2));
        await context.SaveChangesAsync();
        AddLifecycle(context, first, receivedAt: From.AddDays(1), decisionAt: From.AddDays(1).AddHours(10), "QUALIFIED");
        AddLifecycle(context, second, receivedAt: From.AddDays(2), decisionAt: From.AddDays(3).AddHours(6), "DISQUALIFIED");
        await context.SaveChangesAsync();
        var repository = new DashboardRepository(context);

        var result = await repository.GetRelease01Async(
            BusinessUnitId, 77, "assigned_to_me", From, To, GeneratedAt);

        Assert.Equal(GeneratedAt, result.GeneratedAt);
        Assert.Equal(From, result.Filter.From);
        Assert.Equal(To, result.Filter.To);
        Assert.Equal("assigned_to_me", result.RoleScope.Scope);
        Assert.Equal(77, result.RoleScope.OwnerUserId);

        var received = Kpi(result, "leads_received");
        Assert.Equal(DashboardRelease01Contract.Available, received.State);
        Assert.Equal(1m, received.Value);
        Assert.Single(received.DrillDownIdentifiers);
        Assert.Equal(first.Id, received.DrillDownIdentifiers[0].RecordId);
        Assert.Equal(first.CommercialCaseReference, received.DrillDownIdentifiers[0].NexoraSerial);

        var rate = Kpi(result, "qualification_rate");
        Assert.Equal(DashboardRelease01Contract.Available, rate.State);
        Assert.Equal(100m, rate.Value);
        Assert.Equal(1, rate.Numerator);
        Assert.Equal(1, rate.Denominator);
        Assert.Single(rate.DrillDownIdentifiers);

        var median = Kpi(result, "median_time_to_qualify");
        Assert.Equal(DashboardRelease01Contract.Available, median.State);
        Assert.Equal(10m, median.Value);
        Assert.Equal(10m, Assert.Single(median.DrillDownIdentifiers).DurationHours);

        Assert.Equal(DashboardRelease01Contract.InsufficientData, Kpi(result, "win_rate").State);
        Assert.Null(Kpi(result, "win_rate").Value);
    }

    [Fact]
    public async Task Repository_TenantScopeUsesOneCohortForRateAndMedian()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(BusinessUnitId);
        var first = SeedLead(context, 11, ownerId: 77, createdAt: From.AddDays(1));
        var second = SeedLead(context, 12, ownerId: 88, createdAt: From.AddDays(2));
        await context.SaveChangesAsync();
        AddLifecycle(context, first, From.AddDays(1), From.AddDays(1).AddHours(10), "QUALIFIED");
        AddLifecycle(context, second, From.AddDays(2), From.AddDays(3).AddHours(6), "DISQUALIFIED");
        await context.SaveChangesAsync();

        var result = await new DashboardRepository(context).GetRelease01Async(
            BusinessUnitId, null, "tenant", From, To, GeneratedAt);

        var rate = Kpi(result, "qualification_rate");
        Assert.Equal(50m, rate.Value);
        Assert.Equal(2, rate.DrillDownIdentifiers.Count);
        Assert.Equal(rate.Denominator, rate.DrillDownIdentifiers.Count);

        var median = Kpi(result, "median_time_to_qualify");
        Assert.Equal(20m, median.Value);
        Assert.Equal(2, median.DrillDownIdentifiers.Count);
    }

    [Fact]
    public async Task Repository_UsesFirstValidQualificationDecisionPerCase()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(BusinessUnitId);
        var lead = SeedLead(context, 13, ownerId: 77, createdAt: From.AddDays(1));
        await context.SaveChangesAsync();
        AddLifecycle(context, lead, From.AddDays(1), From.AddDays(1).AddHours(10), "QUALIFIED");
        var disqualified = EnsureStatus(context, 90_003, "DISQUALIFIED");
        context.CommercialLifecycleEvents.Add(Event(
            lead, disqualified.SetupId, "DISQUALIFIED", From.AddDays(1).AddHours(20), 3,
            90_002, "QUALIFIED"));
        await context.SaveChangesAsync();

        var result = await new DashboardRepository(context).GetRelease01Async(
            BusinessUnitId, null, "tenant", From, To, GeneratedAt);

        Assert.Equal(100m, Kpi(result, "qualification_rate").Value);
        Assert.Equal(10m, Kpi(result, "median_time_to_qualify").Value);
    }

    [Fact]
    public async Task Repository_ReportsInsufficientDataWhenReceivedEventsDoNotReconcile()
    {
        using var database = new TestDb();
        await using var context = database.ContextFor(BusinessUnitId);
        SeedLead(context, 21, ownerId: 77, createdAt: From.AddDays(1));
        await context.SaveChangesAsync();

        var result = await new DashboardRepository(context).GetRelease01Async(
            BusinessUnitId, null, "tenant", From, To, GeneratedAt);

        var received = Kpi(result, "leads_received");
        Assert.Equal(DashboardRelease01Contract.InsufficientData, received.State);
        Assert.Null(received.Value);
        Assert.Equal(0, received.Numerator);
        Assert.Equal(1, received.Denominator);
        Assert.NotNull(received.InsufficientDataReason);
    }

    private static Lead SeedLead(
        ErpRfqAutomationContext context, long id, long ownerId, DateTime createdAt)
    {
        var lead = Seed.Lead(context, id, BusinessUnitId);
        EnsureUser(context, ownerId);
        lead.AssignTo = ownerId;
        lead.CreatedDate = createdAt;
        return lead;
    }

    private static void EnsureUser(ErpRfqAutomationContext context, long id)
    {
        if (context.Users.Local.Any(user => user.Id == id) || context.Users.Find(id) != null) return;
        context.Users.Add(new User
        {
            Id = id,
            FirstName = "Dashboard",
            LastName = $"User {id}",
            Email = $"dashboard-{id}@example.com",
            PasswordHash = "not-used",
            ImageUrl = "n/a",
            Buid = BusinessUnitId,
            IsActive = true,
            CreatedBy = "dashboard-test",
            CreatedOn = From
        });
    }

    private static void AddLifecycle(
        ErpRfqAutomationContext context,
        Lead lead,
        DateTime receivedAt,
        DateTime decisionAt,
        string decision)
    {
        var receivedStatus = EnsureStatus(context, 90_001, "RECEIVED");
        var decisionStatus = EnsureStatus(context, decision == "QUALIFIED" ? 90_002 : 90_003, decision);
        context.CommercialLifecycleEvents.AddRange(
            Event(lead, receivedStatus.SetupId, "RECEIVED", receivedAt, 1),
            Event(lead, decisionStatus.SetupId, decision, decisionAt, 2, receivedStatus.SetupId, "RECEIVED"));
    }

    private static SetupMaster EnsureStatus(ErpRfqAutomationContext context, long id, string code)
    {
        var tracked = context.SetupMasters.Local.FirstOrDefault(status => status.SetupId == id);
        if (tracked != null) return tracked;
        var existing = context.SetupMasters.Find(id);
        if (existing != null) return existing;
        var status = new SetupMaster
        {
            SetupId = id,
            BusinessUnitId = BusinessUnitId,
            SetupType = "LeadStatus",
            SetupCode = code,
            SetupValue = code,
            IsActive = true,
            CreatedBy = "dashboard-test",
            CreatedOn = From
        };
        context.SetupMasters.Add(status);
        return status;
    }

    private static CommercialLifecycleEvent Event(
        Lead lead,
        long statusId,
        string statusCode,
        DateTime occurredOn,
        int version,
        long? previousStatusId = null,
        string? previousStatusCode = null) => new()
    {
        BusinessUnitId = BusinessUnitId,
        CommercialCaseId = lead.CommercialCaseId,
        CommercialCaseReference = lead.CommercialCaseReference,
        AggregateType = "Lead",
        AggregateId = lead.Id,
        EventType = "StatusTransitioned",
        PreviousStatusId = previousStatusId,
        PreviousStatusCode = previousStatusCode,
        NewStatusId = statusId,
        NewStatusCode = statusCode,
        AggregateVersion = version,
        ActorId = "dashboard-test",
        ActorSource = "Test",
        OccurredOn = occurredOn,
        PolicyVersion = "test-v1",
        Source = "Test",
        CorrelationId = $"corr-{lead.Id}-{version}",
        RequestReference = $"request-{lead.Id}-{version}",
        IdempotencyKey = $"dashboard-{lead.Id}-{version}",
        RequestHash = new string((char)('a' + version), 64)
    };

    private static DashboardRelease01KpiDTO Kpi(DashboardRelease01DTO dashboard, string key) =>
        Assert.Single(dashboard.Kpis, kpi => kpi.Key == key);

    private static ClaimsPrincipal Principal(long businessUnitId, long roleId, long userId) =>
        new(new ClaimsIdentity(
        [
            new Claim("businessUnitId", businessUnitId.ToString()),
            new Claim("roleId", roleId.ToString()),
            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
        ], "test"));

    private sealed class StubRoleGate(bool manager) : IRoleGate
    {
        public Task<bool> IsSuperAdminAsync(long roleId, long businessUnitId) => Task.FromResult(false);
        public Task<bool> IsManagerOrAdminAsync(long roleId, long businessUnitId) => Task.FromResult(manager);
        public Task<bool> CanManageRoleAsync(long callerRoleId, long? targetRoleId, long businessUnitId) =>
            Task.FromResult(false);
    }

    private sealed class CapturingDashboardRepository : IDashboardRepository
    {
        public long BusinessUnitId { get; private set; }
        public long? OwnerUserId { get; private set; }
        public string? RoleScope { get; private set; }
        public DateTime From { get; private set; }
        public DateTime To { get; private set; }

        public Task<DashboardRelease01DTO> GetRelease01Async(
            long businessUnitId,
            long? ownerUserId,
            string roleScope,
            DateTime from,
            DateTime to,
            DateTime generatedAt,
            CancellationToken cancellationToken = default)
        {
            BusinessUnitId = businessUnitId;
            OwnerUserId = ownerUserId;
            RoleScope = roleScope;
            From = from;
            To = to;
            return Task.FromResult(new DashboardRelease01DTO());
        }

        public Task<DashboardDataDTO> GetDashboardDataAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<TeamWorkloadDTO> GetTeamWorkloadAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<PipelineAnalyticsDTO> GetPipelineAnalyticsAsync(long businessUnitId) => throw new NotSupportedException();
    }
}
