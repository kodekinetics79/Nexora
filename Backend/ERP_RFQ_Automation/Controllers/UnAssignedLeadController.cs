using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.AcceptedLeadDTOs;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.CommercialRouting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class UnAssignedLeadController : ControllerBase
    {
        private readonly ILeadRepository _repository;
        private readonly ICommercialRoutingApplicationService _routing;

        public UnAssignedLeadController(
            ILeadRepository repository,
            ICommercialRoutingApplicationService routing)
        {
            _repository = repository;
            _routing = routing;
        }

        [HttpGet]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<ActionResult<PaginatedResponseDTO<AcceptedLeadResponseDTO>>> GetAcceptedLeads(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? assignedToId = null,
            [FromQuery] string? search = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null,
            [FromQuery] bool excludeAssigned = false,
            [FromQuery] bool onlyAssigned = false)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0)
                return BadRequest("Business Unit ID is required");

            if (pageNumber < 1)
                return BadRequest("Page number must be greater than or equal to 1.");

            // Relaxed validation: Allow any page size up to 1000
            if (pageSize < 1 || pageSize > 1000)
                return BadRequest("Page size must be between 1 and 1000.");

            var (leads, total) = await _repository.GetAcceptedLeadsAsync(
                pageNumber, pageSize, targetBUId, assignedToId, search, startDate, endDate, excludeAssigned, onlyAssigned);

            return Ok(new PaginatedResponseDTO<AcceptedLeadResponseDTO>
            {
                Items = leads,
                TotalCount = total,
                PageNumber = pageNumber,
                PageSize = pageSize
            });
        }

        [HttpGet("assigned")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<ActionResult<PaginatedResponseDTO<AcceptedLeadResponseDTO>>> GetAssignedLeads(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] long? assignedToId = null,
            [FromQuery] string? search = null,
            [FromQuery] DateTime? startDate = null,
            [FromQuery] DateTime? endDate = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0)
                return BadRequest("Business Unit ID is required");

            if (pageNumber < 1)
                return BadRequest("Page number must be greater than or equal to 1.");

            // Relaxed validation: Allow any page size up to 1000
            if (pageSize < 1 || pageSize > 1000)
                return BadRequest("Page size must be between 1 and 1000.");

            var (leads, total) = await _repository.GetAcceptedLeadsAsync(
                pageNumber, pageSize, targetBUId, assignedToId, search, startDate, endDate, excludeAssigned: false, onlyAssigned: true);

            return Ok(new PaginatedResponseDTO<AcceptedLeadResponseDTO>
            {
                Items = leads,
                TotalCount = total,
                PageNumber = pageNumber,
                PageSize = pageSize
            });
        }

        [HttpGet("users-for-assignment")]
        public async Task<ActionResult<IEnumerable<UserDropdownDTO>>> GetAssignmentUsers(
            [FromQuery] long? businessUnitId = null)
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId <= 0)
                return BadRequest("Business Unit ID is required");

            var users = await _repository.GetUsersForAssignmentAsync(targetBUId);
            return Ok(users);
        }

        [HttpPost("assign")]
        [RequireManagerRole]
        [RequireModulePermission("Leads", PermissionAction.Edit)]
        public async Task<ActionResult> AssignLead([FromBody] AssignLeadRequestDTO request)
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");

                var userClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                                ?? User.FindFirst("sub")?.Value;
                long? assignedByUserId = long.TryParse(userClaim, out var parsedUserId) ? parsedUserId : null;
                await _routing.AssignLeadAsync(businessUnitId, new ManualAssignLeadCommand(
                    request.LeadId,
                    request.AssignedToUserId,
                    assignedByUserId,
                    request.IdempotencyKey,
                    request.CorrelationId,
                    AssignmentScope.LeadOnly,
                    request.Comment,
                    EnforceExpectedAssignee: true,
                    request.ExpectedAssigneeId),
                    HttpContext.RequestAborted);

                return Ok(new { message = "Lead assigned successfully" });
            }
            catch (RoutingNotFoundException ex)
            {
                return NotFound(new { error = ex.Message });
            }
            catch (RoutingConflictException ex)
            {
                return Conflict(new { error = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { error = ex.Message });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
        [HttpGet("{id}")]
        [RequireModulePermission("Leads", PermissionAction.View)]
        public async Task<ActionResult<AcceptedLeadResponseDTO>> GetAcceptedLeadById(long id)
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var lead = await _repository.GetAcceptedLeadByIdAsync(id, businessUnitId);
            if (lead == null)
                return NotFound($"Accepted lead with ID {id} not found");

            return Ok(lead);
        }
    }
}
