using System.Threading.Tasks;
using System.Security.Claims;
using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.DTOs.Dashboard;

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
