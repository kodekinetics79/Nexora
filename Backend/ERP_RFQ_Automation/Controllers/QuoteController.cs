using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Sla;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    public class QuoteController : ControllerBase
    {
        private readonly IQuoteRepository _repository;
        private readonly IQuoteService _quoteService;
        private readonly IQuoteOutcomeService _outcomeService;

        public QuoteController(IQuoteRepository repository, IQuoteService quoteService, IQuoteOutcomeService outcomeService)
        {
            _repository = repository;
            _quoteService = quoteService;
            _outcomeService = outcomeService;
        }

        [HttpGet]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<IEnumerable<QuoteResponseDTO>>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? search = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                if (pageNumber < 1)
                    return BadRequest("Page number must be greater than or equal to 1.");
                
                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest("Page size must be between 1 and 1000.");

                var (items, totalItems) = await _repository.GetAllAsync(targetBUId, pageNumber, pageSize, search);
                Response.Headers.Append("X-Total-Count", totalItems.ToString());
                return Ok(new { items, totalItems });
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Failed to fetch quotes: {ex.Message}");
            }
        }

        [HttpGet("{id}")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<QuoteResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var quote = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(quote);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving quote: {ex.Message}");
            }
        }

        [HttpPost]
        [RequireModulePermission("Quotations", PermissionAction.Create)]
        public async Task<ActionResult<QuoteResponseDTO>> Create([FromBody] QuoteCreateRequestDTO request)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                if (request.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                var quote = await _quoteService.CreateQuoteAsync(request);
                return CreatedAtAction(nameof(GetById), new { id = quote.Id, businessUnitId = quote.BusinessUnitId }, quote);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error creating quote: {ex.Message}");
            }
        }

        [HttpPut("{id}")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<IActionResult> Update(long id, [FromBody] QuoteUpdateRequestDTO request)
        {
            if (id != request.Id) return BadRequest("ID mismatch");

            try
            {
                var result = await _quoteService.UpdateQuoteAsync(id, request);
                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                return BadRequest(ex.Message);
            }
        }

        [HttpDelete("{id}")]
        [RequireModulePermission("Quotations", PermissionAction.Delete)]
        public async Task<IActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                await _repository.DeleteAsync(id, targetBUId);
                return NoContent();
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error deleting quote: {ex.Message}");
            }
        }

        [HttpGet("{id}/pdf")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<IActionResult> DownloadPdf(long id)
        {
            try
            {
                var bytes = await _quoteService.GenerateQuotePdfAsync(id);
                return File(bytes, "application/pdf", $"Quote_{id}.pdf");
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }

        [HttpPost("{id}/email")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<IActionResult> SendEmail(long id, [FromQuery] string recipientEmail)
        {
            if (string.IsNullOrEmpty(recipientEmail)) return BadRequest("Recipient email is required.");
            try
            {
                await _quoteService.SendQuoteEmailAsync(id, recipientEmail);
                return Ok("Email sent successfully.");
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }

        [HttpPost("{id}/status")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<ActionResult<QuoteResponseDTO>> TransitionStatus(long id, [FromQuery] string status, [FromQuery] string modifiedBy)
        {
            try
            {
                var result = await _quoteService.TransitionStatusAsync(id, status, modifiedBy);
                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
        }

        public sealed class QuoteOutcomeRequestDto
        {
            /// <summary>"won" | "lost" | "expired".</summary>
            public string Outcome { get; set; } = string.Empty;
            /// <summary>SetupMaster "QuoteOutcomeReason" code (required for lost/expired).</summary>
            public string? ReasonCode { get; set; }
            /// <summary>Optional free-text note (max 500 chars).</summary>
            public string? Note { get; set; }
        }

        // -------- POST /api/Quote/{id}/outcome (WP-A4) --------
        [HttpPost("{id}/outcome")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<ActionResult<QuoteResponseDTO>> SetOutcome(long id, [FromBody] QuoteOutcomeRequestDto request)
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                var result = await _outcomeService.SetOutcomeAsync(
                    id, businessUnitId, ActorEmail(), request.Outcome, request.ReasonCode, request.Note);
                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                // Terminal-state immutability (non-manager correction attempt).
                return Conflict(ex.Message);
            }
        }

        // -------- POST /api/Quote/{id}/mark-responded (WP-A4) --------
        [HttpPost("{id}/mark-responded")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<IActionResult> MarkResponded(long id)
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                await _outcomeService.MarkRespondedAsync(id, businessUnitId, ActorEmail());
                return Ok(new { id, responded = true });
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }

        // -------- GET /api/Quote/outcome-reasons (WP-A4 governed picklist) --------
        [HttpGet("outcome-reasons")]
        public async Task<IActionResult> GetOutcomeReasons()
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            var reasons = await _outcomeService.GetOutcomeReasonsAsync(businessUnitId);
            return Ok(reasons);
        }

        private string ActorEmail() =>
            User.FindFirst(ClaimTypes.Email)?.Value
            ?? User.FindFirst("email")?.Value
            ?? User.Identity?.Name
            ?? "unknown";

        [HttpGet("stats")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<QuoteStatsDTO>> GetQuoteStats()
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");
                
                var stats = await _repository.GetQuoteStatsAsync(businessUnitId);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving stats: {ex.Message}");
            }
        }
    }
}
