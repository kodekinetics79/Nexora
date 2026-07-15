using System.Threading.Tasks;
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

        public DashboardController(IDashboardRepository repository)
        {
            _repository = repository;
        }

        // The {businessUnitId} route segment is kept for backward compatibility with
        // the frontend, but the authoritative business unit is ALWAYS taken from the
        // authenticated user's claim — a caller cannot read another tenant's dashboard.
        [HttpGet("{businessUnitId}")]
        public async Task<ActionResult<DashboardDataDTO>> GetDashboardData(long businessUnitId)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (claimBUId <= 0) return Forbid();

            var data = await _repository.GetDashboardDataAsync(claimBUId);
            return Ok(data);
        }
    }
}
