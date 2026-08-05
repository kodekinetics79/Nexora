using System.Threading.Tasks;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.DTOs.Dashboard;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services.Measurement;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] // SEC-02: was anonymous and read whatever businessUnitId the caller put in the route
    public class DashboardController : ControllerBase
    {
        private readonly IDashboardRepository _repository;
        private readonly IRoleGate _roleGate;

        public DashboardController(IDashboardRepository repository, IRoleGate roleGate)
        {
            _repository = repository;
            _roleGate = roleGate;
        }

        [HttpGet("release-01")]
        [RequireModulePermission("Dashboard", PermissionAction.View)]
        public async Task<ActionResult<DashboardRelease01DTO>> GetRelease01(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            CancellationToken cancellationToken)
        {
            var businessUnitId = ClaimId("businessUnitId");
            var roleId = ClaimId("roleId");
            var userId = ClaimId(ClaimTypes.NameIdentifier);
            if (userId <= 0) userId = ClaimId("sub");
            if (businessUnitId <= 0 || roleId <= 0 || userId <= 0) return Forbid();

            var generatedAt = DateTime.UtcNow;
            var requestedTo = NormalizeUtc(to ?? generatedAt);
            var effectiveTo = to.HasValue && requestedTo.TimeOfDay == TimeSpan.Zero
                ? (requestedTo.AddDays(1) < generatedAt ? requestedTo.AddDays(1) : generatedAt)
                : requestedTo;
            var effectiveFrom = NormalizeUtc(from ?? effectiveTo.AddDays(-30));
            if (effectiveFrom >= effectiveTo)
                return BadRequest("The dashboard 'from' value must be earlier than 'to'.");
            if (effectiveTo > generatedAt)
                return BadRequest("The dashboard window cannot end in the future.");
            if (effectiveTo - effectiveFrom > TimeSpan.FromDays(366))
                return BadRequest("The dashboard window cannot exceed 366 days.");

            var tenantWide = await _roleGate.IsManagerOrAdminAsync(roleId, businessUnitId);
            var data = await _repository.GetRelease01Async(
                businessUnitId,
                tenantWide ? null : userId,
                tenantWide ? "tenant" : "assigned_to_me",
                effectiveFrom,
                effectiveTo,
                generatedAt,
                cancellationToken);
            return Ok(data);
        }

        /// <summary>
        /// Forward-looking workload: open enquiries bucketed by days to their bid closing
        /// date, with line counts. The pilot's landing view — /dashboard does not ship as
        /// the landing route while its KPI tiles are still link menus in metric clothing.
        /// </summary>
        [HttpGet("deadline-board")]
        [RequireModulePermission("Dashboard", PermissionAction.View)]
        public async Task<ActionResult<DeadlineBoardDTO>> GetDeadlineBoard(
            [FromQuery] int maxLeads = 200, CancellationToken cancellationToken = default)
        {
            var businessUnitId = ClaimId("businessUnitId");
            if (businessUnitId <= 0) return Forbid();
            return Ok(await _repository.GetDeadlineBoardAsync(businessUnitId, maxLeads, cancellationToken));
        }

        /// <summary>
        /// Documents in, leads out, and what survived: the funnel plus the coverage tiles
        /// that say whether what came out is usable.
        /// </summary>
        [HttpGet("document-yield")]
        [RequireModulePermission("Dashboard", PermissionAction.View)]
        public async Task<ActionResult<DocumentYieldDTO>> GetDocumentYield(
            [FromQuery] DateTime? from,
            [FromQuery] DateTime? to,
            CancellationToken cancellationToken = default)
        {
            var businessUnitId = ClaimId("businessUnitId");
            if (businessUnitId <= 0) return Forbid();

            var generatedAt = DateTime.UtcNow;
            var effectiveTo = NormalizeUtc(to ?? generatedAt);
            var effectiveFrom = NormalizeUtc(from ?? effectiveTo.AddDays(-90));
            if (effectiveFrom >= effectiveTo)
                return BadRequest("The 'from' value must be earlier than 'to'.");
            if (effectiveTo - effectiveFrom > TimeSpan.FromDays(732))
                return BadRequest("The window cannot exceed 732 days.");

            return Ok(await _repository.GetDocumentYieldAsync(
                businessUnitId, effectiveFrom, effectiveTo, cancellationToken));
        }

        /// <summary>
        /// MEASURED extraction accuracy, harvested from reviewers' own corrections.
        ///
        /// Returns no percentage for any field until 30 approved documents exist behind it
        /// — see AccuracyMeasurementService. Below that threshold the payload carries
        /// counts and an explicit "insufficient-data" status, so there is nothing for a
        /// caller to render as a figure. This endpoint is the ONLY place in the product
        /// permitted to state an accuracy number, and it will state none during the early
        /// pilot, which is the correct answer.
        /// </summary>
        [HttpGet("extraction-accuracy")]
        [RequireModulePermission("Dashboard", PermissionAction.View)]
        public async Task<ActionResult<ExtractionAccuracyReport>> GetExtractionAccuracy(
            [FromServices] ErpRfqAutomationContext db,
            CancellationToken cancellationToken)
        {
            var businessUnitId = ClaimId("businessUnitId");
            if (businessUnitId <= 0) return Forbid();
            return Ok(await new AccuracyMeasurementService(db)
                .GetFieldAccuracyAsync(businessUnitId, cancellationToken));
        }

        /// <summary>
        /// The correction signal itself: how often reviewers changed what the machine
        /// produced, per field, with denominators. Read straight from LeadReviewAudit so
        /// reviews recorded before the corpus table existed still count.
        ///
        /// This is NOT accuracy and the payload says so. It is reportable at any sample
        /// size because it reports what happened rather than estimating what will happen.
        /// </summary>
        [HttpGet("correction-signal")]
        [RequireModulePermission("Dashboard", PermissionAction.View)]
        public async Task<ActionResult<CorrectionSignalReport>> GetCorrectionSignal(
            [FromServices] ErpRfqAutomationContext db,
            [FromQuery] int maxReviews = 2000, CancellationToken cancellationToken = default)
        {
            var businessUnitId = ClaimId("businessUnitId");
            if (businessUnitId <= 0) return Forbid();
            return Ok(await new AccuracyMeasurementService(db)
                .GetCorrectionSignalAsync(businessUnitId, maxReviews, cancellationToken));
        }

        // The {businessUnitId} route segment is kept for backward compatibility with
        // the frontend, but the authoritative business unit is ALWAYS taken from the
        // authenticated user's claim — a caller cannot read another tenant's dashboard.
        [HttpGet("{businessUnitId}")]
        [RequireModulePermission("Dashboard", PermissionAction.View)]
        public async Task<ActionResult<DashboardDataDTO>> GetDashboardData(long businessUnitId)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (claimBUId <= 0) return Forbid();

            var data = await _repository.GetDashboardDataAsync(claimBUId);
            return Ok(data);
        }

        private long ClaimId(string claimType) =>
            long.TryParse(User.FindFirst(claimType)?.Value, out var id) ? id : 0;

        private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }
}
