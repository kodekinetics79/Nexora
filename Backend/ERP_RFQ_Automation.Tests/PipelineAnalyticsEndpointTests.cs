using System.Reflection;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DTOs.Dashboard;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Reporting;
using ERP_RFQ_Automation.Services.Measurement;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The endpoint half of the pipeline-analytics scope. The repository tests prove the QUERY is
/// scoped; these prove the action actually hands it a scope resolved from the caller's own claims,
/// because this codebase has twice shipped a guard on a method the real caller never invoked.
///
/// <para><c>[RequireManagerRole]</c> is asserted ABSENT on purpose. It was standing in for the
/// missing scope, and while it stood there a rep was refused their own funnel; the moment somebody
/// removed it to unblock them, the same unscoped query served company-wide money under a personal
/// heading. Its removal is only safe because the scope now travels into the query, so the two
/// facts are asserted together — remove the scope and this file has to be edited to stay green.</para>
/// </summary>
public sealed class PipelineAnalyticsEndpointTests
{
    private const long BusinessUnitId = 5_400;
    private const long RoleId = 9;
    private const long UserId = 77;

    [Fact]
    public void The_action_is_open_to_any_dashboard_reader_and_not_only_to_managers()
    {
        var method = typeof(WorkloadController).GetMethod(nameof(WorkloadController.GetPipelineAnalytics))!;

        Assert.Empty(method.GetCustomAttributes<RequireManagerRoleAttribute>());

        var permission = Assert.Single(method.GetCustomAttributes<RequireModulePermissionAttribute>());
        Assert.Equal("Dashboard", permission.ModuleName);
        Assert.Equal(PermissionAction.View, permission.Action);
    }

    /// <summary>
    /// The workload board next door still belongs to managers. Dropping the attribute from the
    /// funnel is a statement about the funnel, not a blanket relaxation of the controller.
    /// </summary>
    [Fact]
    public void The_team_workload_board_still_requires_a_manager()
    {
        var method = typeof(WorkloadController).GetMethod(nameof(WorkloadController.GetWorkload))!;
        Assert.Single(method.GetCustomAttributes<RequireManagerRoleAttribute>());
    }

    [Theory]
    [InlineData(AccountScopeTier.Tenant, "tenant")]
    [InlineData(AccountScopeTier.ManagedScope, "managed_scope")]
    [InlineData(AccountScopeTier.AssignedAccounts, "assigned_accounts")]
    public async Task The_action_passes_the_scope_it_resolved_from_the_callers_claims(
        AccountScopeTier tier, string expected)
    {
        var repository = new CapturingDashboardRepository();
        var controller = ControllerFor(repository, tier, Principal(BusinessUnitId, RoleId, UserId));

        var result = await controller.GetPipelineAnalytics(null, null, default);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(BusinessUnitId, repository.BusinessUnitId);
        Assert.NotNull(repository.Scope);
        Assert.Equal(expected, repository.Scope!.ScopeName);
        Assert.Equal(UserId, repository.Scope.UserId);
        Assert.Null(repository.From);
        Assert.Null(repository.To);
    }

    [Fact]
    public async Task An_incomplete_identity_is_refused_rather_than_widened()
    {
        var repository = new CapturingDashboardRepository();
        var controller = ControllerFor(repository, AccountScopeTier.Tenant,
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("businessUnitId", BusinessUnitId.ToString())], "test")));

        var result = await controller.GetPipelineAnalytics(null, null, default);

        Assert.IsType<ForbidResult>(result.Result);
        Assert.Null(repository.Scope);
    }

    [Fact]
    public async Task A_period_travels_to_the_repository_and_half_a_period_is_refused()
    {
        var repository = new CapturingDashboardRepository();
        var controller = ControllerFor(repository, AccountScopeTier.Tenant,
            Principal(BusinessUnitId, RoleId, UserId));
        var to = DateTime.UtcNow.AddMinutes(-1);
        var from = to.AddDays(-30);

        var windowed = await controller.GetPipelineAnalytics(from, to, default);

        Assert.IsType<OkObjectResult>(windowed.Result);
        Assert.Equal(from, repository.From);
        Assert.Equal(to, repository.To);

        Assert.IsType<BadRequestObjectResult>(
            (await controller.GetPipelineAnalytics(from, null, default)).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await controller.GetPipelineAnalytics(to, from, default)).Result);
        Assert.IsType<BadRequestObjectResult>(
            (await controller.GetPipelineAnalytics(from, DateTime.UtcNow.AddDays(2), default)).Result);
    }

    /// <summary>
    /// The period control sends "today" as a bare date. The window end is exclusive, so taking
    /// that literally excluded everything created today: bands 2 and 3 showed 0 received /
    /// 0 accepted / 0 quoted over six leads, an RFQ and a quote all dated today, and sealed
    /// "17 Aug – 14 Sep" under the same "Last 30 days" button whose band 1 sealed "– 15 Sep".
    /// A date-only end must reach the repository as the end of that day, clamped to now.
    /// </summary>
    [Fact]
    public async Task A_date_only_to_of_today_covers_the_whole_of_today_up_to_now()
    {
        var repository = new CapturingDashboardRepository();
        var controller = ControllerFor(repository, AccountScopeTier.Tenant,
            Principal(BusinessUnitId, RoleId, UserId));
        var today = DateTime.UtcNow.Date;
        var from = today.AddDays(-29);
        var createdToday = DateTime.UtcNow.AddMinutes(-2);

        var result = await controller.GetPipelineAnalytics(from, today, default);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(from, repository.From);
        Assert.NotNull(repository.To);
        // Not midnight: a lead created two minutes ago falls inside [from, to).
        Assert.True(repository.To > createdToday, $"to={repository.To:O} excludes a lead created today at {createdToday:O}");
        Assert.True(repository.To <= DateTime.UtcNow.AddMinutes(1), "to must not run into the future");
        Assert.True(repository.To <= today.AddDays(1), "to must not spill into tomorrow");
    }

    [Fact]
    public async Task A_date_only_to_in_the_past_covers_the_whole_of_that_day()
    {
        var repository = new CapturingDashboardRepository();
        var controller = ControllerFor(repository, AccountScopeTier.Tenant,
            Principal(BusinessUnitId, RoleId, UserId));
        var day = DateTime.UtcNow.Date.AddDays(-3);

        var result = await controller.GetPipelineAnalytics(day.AddDays(-30), day, default);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(day.AddDays(1), repository.To);
    }

    [Fact]
    public async Task The_gross_margin_band_applies_the_same_inclusive_end()
    {
        var margin = new CapturingGrossMarginService();
        var controller = new WorkloadController(new CapturingDashboardRepository(), margin, new StubScopeResolver(AccountScopeTier.Tenant))
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = Principal(BusinessUnitId, RoleId, UserId) } }
        };
        var today = DateTime.UtcNow.Date;

        var result = await controller.GetGrossMargin(today.AddDays(-29), today, default);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.True(margin.To > DateTime.UtcNow.AddMinutes(-2), $"to={margin.To:O} stops at midnight");
        Assert.True(margin.To <= DateTime.UtcNow.AddMinutes(1));
    }

    private static WorkloadController ControllerFor(
        IDashboardRepository repository, AccountScopeTier tier, ClaimsPrincipal user)
        => new(repository, new UnusedGrossMarginService(), new StubScopeResolver(tier))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = user }
            }
        };

    private static ClaimsPrincipal Principal(long businessUnitId, long roleId, long userId)
        => new(new ClaimsIdentity(
            [
                new Claim("businessUnitId", businessUnitId.ToString()),
                new Claim("roleId", roleId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString())
            ], "test"));

    private sealed class StubScopeResolver(AccountScopeTier tier) : IAccountTeamScopeResolver
    {
        public Task<AccountTeamScope> ResolveAsync(
            long userId, long roleId, long businessUnitId, DateTime asOfUtc, CancellationToken ct = default)
            => Task.FromResult(tier == AccountScopeTier.Tenant
                ? AccountTeamScope.TenantWide(userId)
                : new AccountTeamScope(tier, userId, [], [userId]));
    }

    private sealed class CapturingDashboardRepository : IDashboardRepository
    {
        public long BusinessUnitId { get; private set; }
        public AccountTeamScope? Scope { get; private set; }
        public DateTime? From { get; private set; }
        public DateTime? To { get; private set; }

        public Task<PipelineAnalyticsDTO> GetPipelineAnalyticsAsync(
            long businessUnitId, AccountTeamScope scope, DateTime? from = null, DateTime? to = null,
            CancellationToken cancellationToken = default)
        {
            BusinessUnitId = businessUnitId;
            Scope = scope;
            From = from;
            To = to;
            return Task.FromResult(new PipelineAnalyticsDTO());
        }

        public Task<DashboardDataDTO> GetDashboardDataAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<TeamWorkloadDTO> GetTeamWorkloadAsync(long businessUnitId) => throw new NotSupportedException();
        public Task<DeadlineBoardDTO> GetDeadlineBoardAsync(
            long businessUnitId, int maxLeads = 200, CancellationToken cancellationToken = default,
            AccountTeamScope? accessScope = null) => throw new NotSupportedException();
        public Task<DocumentYieldDTO> GetDocumentYieldAsync(
            long businessUnitId, DateTime from, DateTime to, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task<DashboardRelease01DTO> GetRelease01Async(
            long businessUnitId, AccountTeamScope scope, DateTime from, DateTime to, DateTime generatedAt,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class CapturingGrossMarginService : IGrossMarginService
    {
        public DateTime From { get; private set; }
        public DateTime To { get; private set; }

        public Task<GrossMarginDTO> GetAsync(
            long businessUnitId, DateTime from, DateTime to, DateTime generatedAt,
            CancellationToken ct = default)
        {
            From = from;
            To = to;
            return Task.FromResult(new GrossMarginDTO());
        }

        public Task<CommercialCaseMarginEvidence> GetForCommercialCaseAsync(
            long businessUnitId, long commercialCaseId, DateTime asOf, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedGrossMarginService : IGrossMarginService
    {
        public Task<GrossMarginDTO> GetAsync(
            long businessUnitId, DateTime from, DateTime to, DateTime generatedAt,
            CancellationToken ct = default) => throw new NotSupportedException();

        public Task<CommercialCaseMarginEvidence> GetForCommercialCaseAsync(
            long businessUnitId, long commercialCaseId, DateTime asOf, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
