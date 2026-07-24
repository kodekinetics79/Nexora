using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.DTOs.LeadDTOs;
using ERP_RFQ_Automation.DTOs.AcceptedLeadDTOs;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using ERP_RFQ_Automation.Repositories;

[Route("api/[controller]")]
[ApiController]
[Authorize]
public class LeadController : ControllerBase
{
    private readonly ILeadRepository _repository;
    private readonly ILogger<LeadController>? _logger;
    public LeadController(ILeadRepository repository, ILogger<LeadController>? logger = null)
    {
        _repository = repository;
        _logger = logger;
    }

    private ObjectResult Unexpected(Exception exception, string operation)
    {
        var correlationId = HttpContext.TraceIdentifier;
        _logger?.LogError(exception, "Lead operation {Operation} failed. CorrelationId={CorrelationId}", operation, correlationId);
        return StatusCode(StatusCodes.Status500InternalServerError,
            new { message = "An unexpected error occurred.", correlationId });
    }

    [HttpGet]
    [RequireModulePermission("Leads", PermissionAction.View)]
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
            return Unexpected(ex, "list");
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
            return Unexpected(ex, "accepted-list");
        }
    }

    // New endpoint for Accept
    [HttpPost("accept/{id}")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult> AcceptLead(long id)
    {
        try
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            await _repository.AcceptLeadAsync(id, businessUnitId);
            return Ok("Lead accepted successfully.");
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (Exception ex)
        {
            return Unexpected(ex, "accept");
        }
    }

    // ARCH-01: convert an accepted lead into a real RFQ (closes the Lead -> RFQ gap).
    [HttpPost("{id}/convert-to-rfq")]
    [RequireModulePermission("Leads", PermissionAction.Create)]
    public async Task<ActionResult> ConvertLeadToRfq(long id)
    {
        try
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");

            var createdBy = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue(ClaimTypes.Email)
                ?? User.Identity?.Name
                ?? string.Empty;
            if (string.IsNullOrWhiteSpace(createdBy)) return Unauthorized();
            var (rfqId, rfqno) = await _repository.ConvertLeadToRfqAsync(id, businessUnitId, createdBy);
            return Ok(new { rfqId, rfqno, message = $"Lead converted to RFQ {rfqno}." });
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
        catch (Exception ex)
        {
            return Unexpected(ex, "convert");
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
            return Unexpected(ex, "rejection-reasons");
        }
    }

    // New endpoint for Reject
    [HttpPost("reject/{id}")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult> RejectLead(long id, [FromQuery] long reasonId)
    {
        try
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            await _repository.RejectLeadAsync(id, reasonId, businessUnitId);
            return Ok("Lead rejected successfully.");
        }
        catch (KeyNotFoundException ex) { return NotFound(ex.Message); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (Exception ex)
        {
            return Unexpected(ex, "reject");
        }
    }

    [HttpGet("stats")]
    [RequireModulePermission("Leads", PermissionAction.View)]
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
            return Unexpected(ex, "stats");
        }
    }

    // Extraction review workbench: list low-confidence leads awaiting human review.
    [HttpGet("needs-review")]
    [RequireModulePermission("Leads", PermissionAction.View)]
    public async Task<ActionResult<PaginatedResponseDTO<LeadNeedsReviewItemDTO>>> GetNeedsReviewLeads(
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 25,
        [FromQuery] string? search = null)
    {
        try
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");

            if (pageNumber < 1)
                return BadRequest("Page number must be greater than or equal to 1.");
            pageSize = Math.Clamp(pageSize, 1, 200);

            (IEnumerable<LeadNeedsReviewItemDTO> leads, int totalCount) = await _repository.GetNeedsReviewLeadsAsync(pageNumber, pageSize, businessUnitId, search);

            var response = new PaginatedResponseDTO<LeadNeedsReviewItemDTO>
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
            return Unexpected(ex, "needs-review");
        }
    }

    // Extraction review workbench: save corrections or explicitly approve commercial facts.
    [HttpPut("{id}/review")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult<LeadResponseDTO>> SubmitLeadReview(long id, [FromBody] LeadReviewSubmitDTO review)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");

            var reviewedBy = User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue(ClaimTypes.Email)
                ?? User.Identity?.Name
                ?? string.Empty;
            var lead = await _repository.SubmitLeadReviewAsync(id, businessUnitId, review, reviewedBy);
            if (lead == null) return NotFound($"Lead with ID {id} not found.");

            return Ok(lead);
        }
        catch (LeadReviewConflictException ex)
        {
            return Conflict(new { error = ex.Message });
        }
        catch (LeadReviewValidationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (Exception)
        {
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { error = "An unexpected error prevented the review from being saved." });
        }
    }

    // WP-A3: resolve a duplicate flag. "not_duplicate" clears the conversion
    // block; "confirm" records a human-confirmed duplicate (stays blocked).
    [HttpPost("{id}/duplicate-resolution")]
    [RequireModulePermission("Leads", PermissionAction.Edit)]
    public async Task<ActionResult<LeadResponseDTO>> ResolveDuplicate(long id, [FromBody] DuplicateResolutionRequestDTO request)
    {
        try
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);

            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");

            var resolvedBy = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value
                             ?? User.FindFirst("email")?.Value
                             ?? User.Identity?.Name
                             ?? "unknown";

            var lead = await _repository.ResolveDuplicateAsync(id, businessUnitId, request.Action, resolvedBy);
            if (lead == null) return NotFound($"Lead with ID {id} not found.");

            return Ok(lead);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(ex.Message);
        }
        catch (Exception ex)
        {
            return Unexpected(ex, "resolve-duplicate");
        }
    }

    [HttpGet("{id}")]
    [RequireModulePermission("Leads", PermissionAction.View)]
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
            return Unexpected(ex, "detail");
        }
    }
}
