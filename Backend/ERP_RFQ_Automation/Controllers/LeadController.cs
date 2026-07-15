using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.DTOs.LeadDTOs;
using ERP_RFQ_Automation.DTOs.AcceptedLeadDTOs;
using ERP_RFQ_Automation.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class LeadController : ControllerBase
{
    private readonly ILeadRepository _repository;
    public LeadController(ILeadRepository repository)
    {
        _repository = repository;
    }

    [HttpGet]
    public async Task<ActionResult<PaginatedResponseDTO<LeadResponseDTO>>> GetLeadList(
        [FromQuery] long? businessUnitId = null,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] long? id = null,
        [FromQuery] string? rfqno = null,
        [FromQuery] string? buyersName = null,
        [FromQuery] string? leadSource = null,
        [FromQuery] DateTime? startDate = null,
        [FromQuery] DateTime? endDate = null,
        [FromQuery] string? emailSource = null,
        [FromQuery] string? clientemail = null)
    {
        try
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId == 0)
                return BadRequest("Business Unit ID is required.");
            
            if (pageNumber < 1)
                return BadRequest("Page number must be greater than or equal to 1.");
            // Relaxed validation: Allow any page size up to 1000
            if (pageSize < 1 || pageSize > 1000)
                return BadRequest("Page size must be between 1 and 1000.");

            // Use explicit types for deconstruction to avoid inference errors
            (IEnumerable<LeadResponseDTO> leads, int totalCount) = await _repository.GetLeadListAsync(pageNumber, pageSize, id, rfqno, buyersName, leadSource, targetBUId, startDate, endDate, emailSource, clientemail);

            var response = new PaginatedResponseDTO<LeadResponseDTO>
            {
                Items = leads,
                TotalCount = totalCount,
                PageNumber = pageNumber,
                PageSize = pageSize
            };
            return Ok(response);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
        }
    }

    [HttpGet("email-configurations")]
    public async Task<ActionResult<IEnumerable<EmailConfigurationDropdownDTO>>> GetEmailConfigurations(
        [FromQuery] long? businessUnitId = null)
    {
        try
        {
            var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

            if (targetBUId == 0)
                return BadRequest("Business Unit ID is required.");

            var configurations = await _repository.GetActiveEmailConfigurationsAsync(targetBUId);
            return Ok(configurations);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
        }
    }

    // New endpoint for Accept
    [HttpPost("accept/{id}")]
    public async Task<ActionResult> AcceptLead(long id)
    {
        try
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            await _repository.AcceptLeadAsync(id, businessUnitId);
            return Ok("Lead accepted successfully.");
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error accepting lead: {ex.Message}");
        }
    }

    [HttpGet("rejection-reasons")]
    public async Task<ActionResult<IEnumerable<RejectionReasonDTO>>> GetRejectionReasons()
    {
        try
        {
            var reasons = await _repository.GetLeadRejectionReasonsAsync();
            return Ok(reasons);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving rejection reasons: {ex.Message}");
        }
    }

    // New endpoint for Reject
    [HttpPost("reject/{id}")]
    public async Task<ActionResult> RejectLead(long id, [FromQuery] long reasonId)
    {
        try
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            await _repository.RejectLeadAsync(id, reasonId, businessUnitId);
            return Ok("Lead rejected successfully.");
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error rejecting lead: {ex.Message}");
        }
    }

    [HttpGet("stats")]
    public async Task<ActionResult<LeadStatsDTO>> GetLeadStats()
    {
        try
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");
            
            var stats = await _repository.GetLeadStatsAsync(businessUnitId);
            return Ok(stats);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving stats: {ex.Message}");
        }
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<LeadResponseDTO>> GetLeadById(long id)
    {
        try
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");
            
            var lead = await _repository.GetLeadByIdAsync(id, businessUnitId);
            if (lead == null) return NotFound($"Lead with ID {id} not found.");
            
            return Ok(lead);
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving lead details: {ex.Message}");
        }
    }
}