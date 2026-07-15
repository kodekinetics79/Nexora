using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.DTOs.Dashboard;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DashboardController : ControllerBase
    {
        private readonly IDashboardRepository _repository;

        public DashboardController(IDashboardRepository repository)
        {
            _repository = repository;
        }

        [HttpGet("{businessUnitId}")]
        public async Task<ActionResult<DashboardDataDTO>> GetDashboardData(long businessUnitId)
        {
            var data = await _repository.GetDashboardDataAsync(businessUnitId);
            return Ok(data);
        }
    }
}
