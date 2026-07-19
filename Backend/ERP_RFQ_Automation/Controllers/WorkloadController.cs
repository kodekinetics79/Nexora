using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.Dashboard;
using ERP_RFQ_Automation.Interfaces;
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

        public WorkloadController(IDashboardRepository repository)
        {
            _repository = repository;
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
        /// GET /api/dashboard/pipeline-analytics — WP-B2 stage funnel, loss
        /// reasons, weighted forecast and quoted-vs-floor margin proxy for the
        /// caller's BU. Any user with Dashboard View.
        /// </summary>
        [HttpGet("pipeline-analytics")]
        [RequireModulePermission("Dashboard", PermissionAction.View)]
        public async Task<ActionResult<PipelineAnalyticsDTO>> GetPipelineAnalytics(CancellationToken ct)
        {
            var businessUnitId = GetBusinessUnitId();
            if (businessUnitId <= 0) return Forbid();

            var data = await _repository.GetPipelineAnalyticsAsync(businessUnitId);
            return Ok(data);
        }

        private long GetBusinessUnitId() =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out var id) ? id : 0;
    }
}
