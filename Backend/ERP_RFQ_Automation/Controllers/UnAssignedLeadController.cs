using ERP_RFQ_Automation.DTOs.AcceptedLeadDTOs;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.Interfaces;
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

        public UnAssignedLeadController(ILeadRepository repository)
        {
            _repository = repository;
        }

        [HttpGet]
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
        public async Task<ActionResult> AssignLead([FromBody] AssignLeadRequestDTO request)
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");

                // WP-A1 manager gate: only admin/manager roles may (re)assign leads.
                // The caller's roleId claim is resolved against the SetupMaster
                // "role" row (matched by code/name text, never a hardcoded id).
                if (!long.TryParse(User.FindFirst("roleId")?.Value, out var roleId)
                    || !await _repository.CanManageLeadAssignmentsAsync(roleId))
                {
                    return StatusCode(StatusCodes.Status403Forbidden,
                        new { error = "Only managers and admins can assign leads." });
                }

                var callerName = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                                 ?? User.FindFirst("email")?.Value
                                 ?? User.Identity?.Name;

                await _repository.AssignLeadAsync(
                    request.LeadId,
                    request.AssignedToUserId,
                    businessUnitId,
                    request.Comment,
                    callerName
                );

                return Ok(new { message = "Lead assigned successfully" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { error = ex.Message });
            }
        }
        [HttpGet("{id}")]
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
