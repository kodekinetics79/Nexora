using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.DTOs.QuoteDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Sla;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [Authorize]
    [Route("api/[controller]")]
    [ApiController]
    [ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Quotes)]
    public class QuoteController : ControllerBase
    {
        private readonly IQuoteRepository _repository;
        private readonly IQuoteService _quoteService;
        private readonly IQuoteOutcomeService _outcomeService;
        private readonly ILifecycleApplicationService _lifecycle;
        private readonly ErpRfqAutomationContext _context;
        private readonly ILogger<QuoteController>? _logger;

        public QuoteController(
            IQuoteRepository repository,
            IQuoteService quoteService,
            IQuoteOutcomeService outcomeService,
            ILifecycleApplicationService lifecycle,
            ErpRfqAutomationContext context,
            ILogger<QuoteController>? logger = null)
        {
            _repository = repository;
            _quoteService = quoteService;
            _outcomeService = outcomeService;
            _lifecycle = lifecycle;
            _context = context;
            _logger = logger;
        }

        private ObjectResult Unexpected(Exception exception, string operation)
        {
            var correlationId = HttpContext.TraceIdentifier;
            _logger?.LogError(exception, "Quote operation {Operation} failed. CorrelationId={CorrelationId}", operation, correlationId);
            return StatusCode(StatusCodes.Status500InternalServerError,
                new { message = "An unexpected error occurred.", correlationId });
        }

        [HttpGet]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<IEnumerable<QuoteResponseDTO>>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? search = null,
            [FromQuery] string? state = null)
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

                var (items, totalItems) = await _repository.GetAllAsync(targetBUId, pageNumber, pageSize, search, state);
                Response.Headers.Append("X-Total-Count", totalItems.ToString());
                return Ok(new { items, totalItems });
            }
            catch (Exception ex)
            {
                return Unexpected(ex, "list");
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
            catch (LifecycleValidationException ex)
            {
                return Conflict(ex.Message);
            }
            catch (LifecycleConflictException ex)
            {
                return Conflict(ex.Message);
            }
            catch (Exception ex)
            {
                return Unexpected(ex, "detail");
            }
        }

        [HttpPost]
        [RequireModulePermission("Quotations", PermissionAction.Create)]
        public async Task<ActionResult<QuoteResponseDTO>> Create([FromBody] QuoteCreateRequestDTO request)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId <= 0) return BadRequest("Business Unit ID is required.");
                if (!request.RfqId.HasValue) return BadRequest("An RFQ is required to create a Quote.");
                request.BusinessUnitId = claimBUId;
                request.CreatedBy = ActorEmail();

                var rfq = await _context.Rfqs.Include(item => item.Lead)
                    .SingleOrDefaultAsync(item => item.Id == request.RfqId && item.BusinessUnitId == claimBUId);
                if (rfq == null) return BadRequest("The selected RFQ was not found in this tenant.");
                if (rfq.Lead == null) return Conflict("The selected RFQ is not linked to a commercial case.");
                rfq.InheritCommercialIdentity(rfq.Lead);
                if (request.CustomerId.HasValue && request.CustomerId != rfq.CustomerId)
                    return Conflict("The selected customer does not match the RFQ commercial identity.");
                request.CustomerId = rfq.CustomerId;

                await using var transaction = await _context.Database.BeginTransactionAsync();
                var created = await _quoteService.CreateQuoteAsync(request);
                var quoteEntity = await _context.Quotes.SingleAsync(item => item.Id == created.Id);
                quoteEntity.InheritCommercialIdentity(rfq);
                await _context.SaveChangesAsync();
                await transaction.CommitAsync();
                var quote = await _repository.GetByIdAsync(created.Id, claimBUId);
                return CreatedAtAction(nameof(GetById), new { id = quote.Id, businessUnitId = quote.BusinessUnitId }, quote);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(ex.Message);
            }
            catch (LifecycleValidationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (LifecycleConflictException ex)
            {
                return Conflict(ex.Message);
            }
            catch (Exception ex)
            {
                return Unexpected(ex, "create");
            }
        }

        [HttpPut("{id}")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<IActionResult> Update(long id, [FromBody] QuoteUpdateRequestDTO request)
        {
            if (id != request.Id) return BadRequest("ID mismatch");

            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");
                var existing = await _context.Quotes.AsNoTracking()
                    .SingleOrDefaultAsync(item => item.Id == id && item.BusinessUnitId == businessUnitId);
                if (existing == null) return NotFound();
                if (request.CustomerId != existing.CustomerId)
                    return Conflict("Quote customer identity cannot be changed by an ordinary update.");
                if (request.StatusId != existing.StatusId)
                    return Conflict("Quote status must be changed through the governed lifecycle command.");
                request.ModifiedBy = ActorEmail();
                var result = await _quoteService.UpdateQuoteAsync(id, request);
                return Ok(result);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                return Unexpected(ex, "update");
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
                return Unexpected(ex, "delete");
            }
        }

        [HttpGet("{id}/pdf")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<IActionResult> DownloadPdf(long id)
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId <= 0) return Forbid();
                var bytes = await _quoteService.GenerateQuotePdfAsync(id, businessUnitId);
                return File(bytes, "application/pdf", $"Quote_{id}.pdf");
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
            }
        }

        [HttpPost("{id}/revision-impact/resolve")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<IActionResult> ResolveRevisionImpact(long id,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId <= 0) return BadRequest(new { message = "A valid businessUnitId claim is required." });
            try
            {
                var actor = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                    ?? User.Identity?.Name ?? "authenticated-user";
                await _quoteService.ResolveRevisionImpactAsync(id, businessUnitId, actor,
                    string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey, ct);
                return NoContent();
            }
            catch (KeyNotFoundException) { return NotFound(); }
        }

        [HttpPost("{id}/email")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<IActionResult> SendEmail(long id, [FromQuery] string recipientEmail)
        {
            if (string.IsNullOrEmpty(recipientEmail)) return BadRequest("Recipient email is required.");
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");
                // WP-B3: the send may be parked as a below-floor approval instead of
                // being performed; 409 tells the caller it is queued, not failed.
                var result = await _quoteService.SendQuoteEmailAsync(id, businessUnitId, recipientEmail, options: new QuoteSendOptions
                {
                    RequestedByUserId = ActorUserId(),
                    RequestedBy = ActorEmail()
                });

                if (result.Held)
                {
                    return Conflict(new
                    {
                        queuedForApproval = true,
                        approvalId = result.ApprovalId,
                        summary = result.HoldSummary,
                        message = "Sent for approval — pricing is below your floor. Track it in Approvals."
                    });
                }

                if (result.FailedPermanently)
                    return Conflict(new { message = "Quote delivery is in a failed terminal state and requires operator review.", errorCode = result.FailureCode });

                return Accepted(new
                {
                    queuedForDelivery = result.QueuedForDelivery,
                    delivered = result.Delivered,
                    replayed = result.Replayed,
                    message = result.Delivered ? "Quote delivery was already completed." : "Quote delivery queued."
                });
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (LifecycleValidationException ex)
            {
                return Conflict(ex.Message);
            }
            catch (LifecycleConflictException ex)
            {
                return Conflict(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { message = ex.Message });
            }
        }

        // -------- POST /api/Quote/{id}/revise (revisions-lite, WP-B4) --------
        // Clones a non-DRAFT quote (+items) as a new DRAFT revision (RevisionNo+1,
        // linked back). Draft / superseded / outcome-locked chains → 409.
        [HttpPost("{id}/revise")]
        [RequireModulePermission("Quotations", PermissionAction.Create)]
        public async Task<ActionResult<QuoteResponseDTO>> Revise(long id)
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                var revision = await _quoteService.ReviseQuoteAsync(id, businessUnitId, ActorEmail());
                return CreatedAtAction(nameof(GetById),
                    new { id = revision.Id, businessUnitId = revision.BusinessUnitId }, revision);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (InvalidOperationException ex)
            {
                // Draft, already revised, or chain locked by a recorded outcome.
                return Conflict(new { message = ex.Message });
            }
        }

        // -------- GET /api/Quote/{id}/revisions (revision-chain facts) --------
        [HttpGet("{id}/revisions")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<QuoteRevisionInfoDTO>> GetRevisionInfo(long id)
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");

                return Ok(await _quoteService.GetRevisionInfoAsync(id, businessUnitId));
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
        }

        [HttpGet("{id}/lifecycle")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<LifecycleStateView>> GetLifecycle(long id)
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");
                return Ok(await _lifecycle.GetQuoteStateAsync(businessUnitId, id, HttpContext.RequestAborted));
            }
            catch (LifecycleNotFoundException) { return NotFound(); }
            catch (LifecycleValidationException ex) { return Conflict(ex.Message); }
        }

        [HttpPost("{id}/status")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<ActionResult<LifecycleTransitionResult>> TransitionStatus(
            long id, [FromBody] QuoteLifecycleTransitionRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId <= 0) return BadRequest("Business Unit ID is required.");
                var result = await _lifecycle.TransitionQuoteAsync(
                    businessUnitId,
                    id,
                    new LifecycleActor(ActorEmail(), "AuthenticatedUser"),
                    new LifecycleTransitionCommand(
                        request.TargetStatusCode,
                        request.ExpectedVersion,
                        request.ReasonCode,
                        request.ReasonNotes,
                        "Api",
                        request.CorrelationId,
                        $"quote-{id}",
                        request.IdempotencyKey),
                    false,
                    HttpContext.RequestAborted);
                return Ok(result);
            }
            catch (LifecycleNotFoundException) { return NotFound(); }
            catch (LifecycleValidationException ex) { return BadRequest(ex.Message); }
            catch (LifecycleConflictException ex) { return Conflict(ex.Message); }
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
            catch (LifecycleValidationException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (LifecycleConflictException ex)
            {
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
        [RequireModulePermission("Quotations", PermissionAction.View)]
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

        private long? ActorUserId() =>
            long.TryParse(
                User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value,
                out var uid) ? uid : null;

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
                return Unexpected(ex, "stats");
            }
        }
    }
}
