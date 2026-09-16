using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.Dashboard;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Reporting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers
{
    /// <summary>
    /// WP-B1: manager team-workload view. Lives in its own controller (route
    /// "api/dashboard/workload" — the literal segment outranks DashboardController's
    /// "{businessUnitId}" parameter, so there is no route ambiguity) so the
    /// existing DashboardController stays untouched.
    ///
    /// The business unit ALWAYS comes from the caller's JWT claim (SEC-02
    /// convention) — a manager can only ever see their own BU's team.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("api/dashboard")]
    public class WorkloadController : ControllerBase
    {
        private readonly IDashboardRepository _repository;
        private readonly IGrossMarginService _grossMargin;
        private readonly IAccountTeamScopeResolver _accountScope;

        /// <summary>Default gross-margin window when the caller states none.</summary>
        private const int DefaultMarginWindowDays = 90;

        /// <summary>
        /// Two years plus a leap day. Matches the document-yield ceiling and FR-PQH-01's retention
        /// floor, so the widest legitimate question is answerable and an unbounded scan is not.
        /// </summary>
        private const int MaximumMarginWindowDays = 732;

        public WorkloadController(
            IDashboardRepository repository,
            IGrossMarginService grossMargin,
            IAccountTeamScopeResolver accountScope)
        {
            _repository = repository;
            _grossMargin = grossMargin;
            _accountScope = accountScope;
        }

        /// <summary>
        /// GET /api/dashboard/workload — per-rep open/overdue leads and
        /// sent/stale quotes for the caller's BU, plus one unassigned bucket
        /// row. Managers/admins only.
        /// </summary>
        [HttpGet("workload")]
        [RequireManagerRole]
        [RequireModulePermission("Dashboard", PermissionAction.View)]
        public async Task<ActionResult<TeamWorkloadDTO>> GetWorkload(CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return Forbid();

            var data = await _repository.GetTeamWorkloadAsync(businessUnitId);
            return Ok(data);
        }

        /// <summary>
        /// GET /api/dashboard/pipeline-analytics?from&amp;to — WP-B2 stage funnel, loss reasons and
        /// weighted forecast, restricted to the rows the caller may read. Any user with Dashboard
        /// View.
        ///
        /// <para><b>Why [RequireManagerRole] is gone.</b> It was standing in for a scope the query
        /// did not have: <c>GetPipelineAnalyticsAsync</c> took a business unit and nothing else, so
        /// every predicate in it was tenant-wide. That left two outcomes and both were wrong — a
        /// sales representative was refused their own funnel outright, or, the day somebody
        /// removed the attribute to unblock them, was served the company's money under a heading
        /// that says "your accounts". The attribute could only come off once the QUERY was scoped,
        /// and it is scoped now: the resolved scope travels into the repository and is echoed back
        /// on the payload, so a reader can see whose figures they are looking at.</para>
        ///
        /// <para>The margin proxy this endpoint used to carry has been removed; see
        /// <see cref="GetGrossMargin"/>.</para>
        /// </summary>
        [HttpGet("pipeline-analytics")]
        [RequireModulePermission("Dashboard", PermissionAction.View)]
        public async Task<ActionResult<PipelineAnalyticsDTO>> GetPipelineAnalytics(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            var roleId = ClaimId("roleId");
            var userId = ClaimId(ClaimTypes.NameIdentifier);
            if (userId <= 0) userId = ClaimId("sub");
            if (businessUnitId <= 0 || roleId <= 0 || userId <= 0) return Forbid();

            // Both ends or neither. Half a window is not a period the payload can state, and a
            // funnel that silently ignored the half it was given would let the screen draw a
            // "period applied" seal over an all-time figure.
            if (from.HasValue != to.HasValue)
                return BadRequest(new { message = "State both ends of the period, or neither." });

            var now = DateTime.UtcNow;
            var effectiveFrom = NormalizeUtc(from);
            var effectiveTo = ThroughEndOfDay(NormalizeUtc(to), now);
            if (effectiveFrom >= effectiveTo)
                return BadRequest(new { message = "The reporting window must start before it ends." });
            if (effectiveTo > now.AddMinutes(1))
                return BadRequest(new { message = "The reporting window cannot end in the future." });

            var scope = await _accountScope.ResolveAsync(userId, roleId, businessUnitId, now, ct);

            var data = await _repository.GetPipelineAnalyticsAsync(
                businessUnitId, scope, effectiveFrom, effectiveTo, ct);
            return Ok(data);
        }

        /// <summary>
        /// GET /api/dashboard/gross-margin?from&amp;to — value-weighted gross margin over accepted
        /// quotes in the window, computed from the immutable quote-time sourcing decision.
        ///
        /// <para>Returns 200 with <c>status: "unavailable"</c> and a stated reason when the figure
        /// cannot be computed. That is not an error condition — "we cannot evidence this" is a valid
        /// and useful answer, and a 4xx would make the dashboard render a failure state instead of
        /// the explanation.</para>
        /// </summary>
        [HttpGet("gross-margin")]
        [RequireManagerRole]
        [RequireModulePermission("Dashboard", PermissionAction.View)]
        public async Task<ActionResult<GrossMarginDTO>> GetGrossMargin(
            [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return Forbid();

            var generatedAt = DateTime.UtcNow;
            var effectiveTo = ThroughEndOfDay(NormalizeUtc(to), generatedAt) ?? generatedAt;
            var effectiveFrom = NormalizeUtc(from) ?? effectiveTo.AddDays(-DefaultMarginWindowDays);

            if (effectiveFrom >= effectiveTo)
                return BadRequest(new { message = "The reporting window must start before it ends." });
            if ((effectiveTo - effectiveFrom).TotalDays > MaximumMarginWindowDays)
                return BadRequest(new
                {
                    message = $"The reporting window cannot exceed {MaximumMarginWindowDays} days."
                });
            if (effectiveTo > generatedAt.AddMinutes(1))
                return BadRequest(new { message = "The reporting window cannot end in the future." });

            var data = await _grossMargin.GetAsync(businessUnitId, effectiveFrom, effectiveTo, generatedAt, ct);
            return Ok(data);
        }

        private long GetBusinessUnitId() => ClaimId("businessUnitId");

        private long ClaimId(string claimType) =>
            long.TryParse(User.FindFirst(claimType)?.Value, out var id) ? id : 0;

        private static DateTime? NormalizeUtc(DateTime? value) => value is null
            ? null
            : value.Value.Kind == DateTimeKind.Utc
                ? value.Value
                : DateTime.SpecifyKind(value.Value, DateTimeKind.Utc);

        /// <summary>
        /// A date-only <c>to</c> ("2026-09-15", the period control's "today") means THROUGH that
        /// day, not up to its first second. The window end is exclusive, so midnight of the
        /// chosen day excluded every lead, quote and RFQ created that day: with "Last 30 days"
        /// selected, the funnel showed 0 received / 0 accepted / 0 quoted while six leads, an RFQ
        /// and a quote sat on today's date, and its seal read one day short of the band above it.
        /// This is the same rule <c>DashboardController.GetRelease01</c> applies, so every band
        /// under one period control covers the same days. Clamped to now: a day still in progress
        /// ends when the figures are generated, never in the future.
        /// </summary>
        private static DateTime? ThroughEndOfDay(DateTime? to, DateTime now)
        {
            if (to is null) return null;
            if (to.Value.TimeOfDay != TimeSpan.Zero) return to;
            var endOfDay = to.Value.AddDays(1);
            return endOfDay < now ? endOfDay : now;
        }
    }
}
