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
        private readonly ERP_RFQ_Automation.Intelligence.Pricing.IPriceAttestationService _attestations;
        private readonly ICommercialAccessContext? _commercialAccess;
        private readonly ILogger<QuoteController>? _logger;

        public QuoteController(
            IQuoteRepository repository,
            IQuoteService quoteService,
            IQuoteOutcomeService outcomeService,
            ILifecycleApplicationService lifecycle,
            ErpRfqAutomationContext context,
            ERP_RFQ_Automation.Intelligence.Pricing.IPriceAttestationService attestations,
            ILogger<QuoteController>? logger = null,
            ICommercialAccessContext? commercialAccess = null)
        {
            _repository = repository;
            _quoteService = quoteService;
            _outcomeService = outcomeService;
            _lifecycle = lifecycle;
            _context = context;
            _attestations = attestations;
            _logger = logger;
            _commercialAccess = commercialAccess;
        }

        private async Task<bool> CanAccessQuoteAsync(long id, CancellationToken ct = default) =>
            _commercialAccess != null && await _commercialAccess.CanAccessQuoteAsync(id, ct);

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

                var actor = _commercialAccess == null ? null : await _commercialAccess.ResolveAsync(HttpContext.RequestAborted);
                if (actor == null || actor.BusinessUnitId != targetBUId) return Forbid();
                var (items, totalItems) = await _repository.GetAllAsync(targetBUId, pageNumber, pageSize, search, state, actor.AccountScope);
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

                var actor = _commercialAccess == null ? null : await _commercialAccess.ResolveAsync(HttpContext.RequestAborted);
                if (actor == null || actor.BusinessUnitId != targetBUId) return NotFound();
                var quote = await _repository.GetByIdAsync(id, targetBUId, actor.AccountScope);
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
                if (_commercialAccess == null
                    || !await _commercialAccess.CanAccessRfqAsync(request.RfqId.Value, HttpContext.RequestAborted))
                    return NotFound();
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

                // NO transaction is opened here, deliberately.
                //
                // This action used to open one and then re-stamp the created quote's commercial
                // identity inside it. Two things were wrong with that. Program.cs configures
                // EnableRetryOnFailure, so NpgsqlRetryingExecutionStrategy is installed and refuses
                // a user-initiated transaction outside its delegate — POST /api/Quote therefore
                // threw "does not support user-initiated transactions" on every PostgreSQL request.
                // And had it not, CreateQuoteAsync opens its OWN serializable transaction inside its
                // own execution strategy, which cannot nest inside an already-open one.
                //
                // QuoteService owns the persistence invariant and stamps the selected RFQ's
                // commercial identity before INSERT. The controller owns access/customer checks;
                // neither layer relies on a second corrective write after the quote exists.
                var created = await _quoteService.CreateQuoteAsync(request);
                var actorScope = await _commercialAccess.ResolveAsync(HttpContext.RequestAborted);
                if (actorScope == null) return Forbid();
                var quote = await _repository.GetByIdAsync(created.Id, claimBUId, actorScope.AccountScope);
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
                if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();
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

        /// <summary>
        /// Removes a quotation, with a stated reason.
        ///
        /// <para>This route used to hard-delete the row, and with it the quote's R5 price
        /// attestations and R7 validity extensions, on nothing more than the Quotations Delete
        /// permission. It is now a reasoned, audited removal: past DRAFT the quote is withdrawn
        /// rather than destroyed, a tombstone is written in every case, and the evidence tables
        /// survive by database constraint. See <c>QuoteRepository.RemoveAsync</c>.</para>
        ///
        /// <para><paramref name="reason"/> is mandatory. It is the difference between a removal
        /// that can be explained a year later and one that cannot.</para>
        /// </summary>
        [HttpDelete("{id}")]
        [RequireModulePermission("Quotations", PermissionAction.Delete)]
        public async Task<IActionResult> Delete(long id, [FromQuery] string? reason = null,
            [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");
                if (string.IsNullOrWhiteSpace(reason))
                    return BadRequest("A reason is required to remove a quotation.");
                if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();

                var outcome = await _repository.RemoveAsync(id, targetBUId, reason, ActorEmail());
                if (outcome == null) return NotFound();

                // 200 with the outcome, not 204: "withdrawn, still on file" and "discarded" are
                // different things and the caller should be able to tell the user which happened.
                return Ok(new
                {
                    quoteNo = outcome.QuoteNo,
                    mode = outcome.Mode,
                    removedOn = outcome.RemovedOn,
                    deleted = outcome.WasDeleted,
                    message = outcome.WasDeleted
                        ? $"Draft {outcome.QuoteNo} was discarded. The removal is on record."
                        : $"Quote {outcome.QuoteNo} was withdrawn. It stays on file with its price " +
                          "confirmations and validity history intact."
                });
            }
            catch (QuoteRemovalRefusedException ex)
            {
                return Conflict(ex.Message);
            }
            catch (Exception ex)
            {
                return Unexpected(ex, "delete");
            }
        }

        /// <summary>
        /// The customer-facing quotation document.
        ///
        /// <para>R5: this route had no price-attestation check at all, which made it the widest
        /// hole in the gate — the PDF is the commercial offer, and anyone with View could pull it
        /// and forward it with nobody having attested to a single price on it. The check lives in
        /// <see cref="IQuoteService.GenerateQuotePdfAsync"/> so the quote-delivery worker is
        /// covered by the same code, and it fails closed: 409 with the rep-facing reason, no
        /// bytes.</para>
        /// </summary>
        [HttpGet("{id}/pdf")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<IActionResult> DownloadPdf(long id)
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId <= 0) return Forbid();
                if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();
                var bytes = await _quoteService.GenerateQuotePdfAsync(
                    id, businessUnitId, ct: HttpContext.RequestAborted);
                return File(bytes, "application/pdf", $"Quote_{id}.pdf");
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationRequiredException ex)
            {
                // priceAttestationRequired mirrors the SendEmail contract, so the client can
                // re-open the same confirm-price dialog instead of showing a dead end.
                return Conflict(new { priceAttestationRequired = true, message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                // The document is intentionally blocked before price attestation when its
                // commercial facts are incomplete. Keep that distinct in the wire contract so
                // clients send the rep to Commercial Review instead of asking them to attest to
                // prices that do not exist yet.
                return Conflict(new { commercialReviewRequired = true, message = ex.Message });
            }
        }

        // -------- POST /api/Quote/{id}/extend-validity (R7) --------

        /// <summary>
        /// Holds an already-issued quote's price open until a later date, with a mandatory reason
        /// that is recorded as an auditable row (Decision Register R7).
        ///
        /// <para>Deliberately NOT a revision: the commercial offer is unchanged, so the revision
        /// number does not move. Authorization is the same Quotations:Edit permission that governs
        /// every other commercial action on a quote (send, outcome, price attestation) — extending
        /// validity is a commercial act on the quote, not a new kind of privilege.</para>
        /// </summary>
        [HttpPost("{id}/extend-validity")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<ActionResult<QuoteValidityExtensionResultDTO>> ExtendValidity(
            long id,
            [FromBody] QuoteExtendValidityRequestDTO request,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey)
        {
            if (!ModelState.IsValid) return BadRequest(ModelState);
            if (request?.ValidUntil is null)
                return BadRequest(new { message = "Choose the new date the quote stays valid until." });

            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId <= 0) return BadRequest(new { message = "Business Unit ID is required." });
            if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();

            try
            {
                var result = await _quoteService.ExtendQuoteValidityAsync(
                    id, businessUnitId, request.ValidUntil.Value, request.Reason,
                    ActorEmail(), ActorUserId(),
                    string.IsNullOrWhiteSpace(idempotencyKey) ? Guid.NewGuid().ToString("N") : idempotencyKey,
                    HttpContext.RequestAborted);
                return Ok(result);
            }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
            catch (InvalidOperationException ex) { return Conflict(new { message = ex.Message }); }
            catch (Exception ex) { return Unexpected(ex, "extend-validity"); }
        }

        // -------- GET /api/Quote/{id}/validity-extensions (R7: the reason must be readable) --------
        [HttpGet("{id}/validity-extensions")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<IReadOnlyList<QuoteValidityExtensionDTO>>> GetValidityExtensions(long id)
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId <= 0) return BadRequest(new { message = "Business Unit ID is required." });
            if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();
            try
            {
                return Ok(await _quoteService.GetValidityExtensionsAsync(id, businessUnitId, HttpContext.RequestAborted));
            }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (Exception ex) { return Unexpected(ex, "validity-extensions"); }
        }

        [HttpPost("{id}/revision-impact/resolve")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<IActionResult> ResolveRevisionImpact(long id,
            [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId <= 0) return BadRequest(new { message = "A valid businessUnitId claim is required." });
            if (!await CanAccessQuoteAsync(id, ct)) return NotFound();
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
                if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();
                // WP-B3: the send may be parked as a below-floor approval instead of
                // being performed; 409 tells the caller it is queued, not failed.
                var result = await _quoteService.SendQuoteEmailAsync(id, businessUnitId, recipientEmail, options: new QuoteSendOptions
                {
                    RequestedByUserId = ActorUserId(),
                    RequestedBy = ActorEmail()
                });

                // R5: nothing was sent — the price source is unconfirmed, or a price changed
                // after it was confirmed. The client re-opens the confirmation dialog.
                if (result.BlockedPendingPriceAttestation)
                {
                    return Conflict(new
                    {
                        priceAttestationRequired = true,
                        message = result.PriceAttestationReason
                    });
                }

                // R17: nothing was sent — a line's output tax was never derived. Reported as its
                // own condition rather than folded into the attestation branch, because the fix is
                // different: set the output tax rate in Commercial Policy settings, or price the
                // line, or give the non-standard line its reason.
                if (result.BlockedPendingTaxDerivation)
                {
                    return Conflict(new
                    {
                        taxDerivationRequired = true,
                        message = result.TaxDerivationReason
                    });
                }

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
                if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();

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
                if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();

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
                if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();
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
                if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();
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
                if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();

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
                if (!await CanAccessQuoteAsync(id, HttpContext.RequestAborted)) return NotFound();

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

        // ================================================================
        // R5 price-provenance attestation (Decision Register R5).
        // The rep confirms where the prices came from BEFORE the quote is emailed;
        // SendEmail above refuses any quote whose current prices are not covered.
        // ================================================================

        /// <summary>
        /// Whether this quote may be sent, what was last confirmed, and the prices a fresh
        /// confirmation would cover. Drives the confirm-before-send dialog.
        /// </summary>
        [HttpGet("{id}/price-attestation")]
        [RequireModulePermission("Quotations", PermissionAction.View)]
        public async Task<ActionResult<QuotePriceAttestationStatusDTO>> GetPriceAttestation(
            long id, CancellationToken ct)
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId <= 0) return BadRequest(new { message = "Business Unit ID is required." });
            if (!await CanAccessQuoteAsync(id, ct)) return NotFound();
            try
            {
                var state = await _attestations.EvaluateAsync(id, businessUnitId, ct);
                return Ok(await BuildAttestationStatusAsync(id, businessUnitId, state, ct));
            }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (Exception ex) { return Unexpected(ex, "price-attestation-read"); }
        }

        /// <summary>
        /// Records the rep's confirmation over the quote's CURRENT prices. Any later price
        /// change voids it and this must be called again.
        /// </summary>
        [HttpPost("{id}/price-attestation")]
        [RequireModulePermission("Quotations", PermissionAction.Edit)]
        public async Task<ActionResult<QuotePriceAttestationStatusDTO>> ConfirmPriceAttestation(
            long id, [FromBody] QuotePriceAttestationRequest request, CancellationToken ct)
        {
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId <= 0) return BadRequest(new { message = "Business Unit ID is required." });
            if (request is null) return BadRequest(new { message = "A price source and reference are required." });
            if (!await CanAccessQuoteAsync(id, ct)) return NotFound();
            try
            {
                await _attestations.AttestAsync(
                    id, businessUnitId, request.Source, request.SourceReference,
                    ActorUserId(), ActorEmail(), ct);

                var state = await _attestations.EvaluateAsync(id, businessUnitId, ct);
                return Ok(await BuildAttestationStatusAsync(id, businessUnitId, state, ct));
            }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
            catch (Exception ex) { return Unexpected(ex, "price-attestation-confirm"); }
        }

        private async Task<QuotePriceAttestationStatusDTO> BuildAttestationStatusAsync(
            long quoteId, long businessUnitId,
            ERP_RFQ_Automation.Intelligence.Pricing.PriceAttestationState state,
            CancellationToken ct)
        {
            var currencyId = await _context.Quotes.AsNoTracking()
                .Where(q => q.Id == quoteId && q.BusinessUnitId == businessUnitId)
                .Select(q => q.CurrencyId)
                .FirstOrDefaultAsync(ct);
            var currencyCode = currencyId is null ? null : await _context.Currencies.AsNoTracking()
                .Where(c => c.Id == currencyId.Value && c.BusinessUnitId == businessUnitId)
                .Select(c => c.Code)
                .FirstOrDefaultAsync(ct);

            var attestedLines = state.Latest is null
                ? new List<QuotePriceAttestationLineDTO>()
                : await _context.QuotePriceAttestationLines.AsNoTracking()
                    .Where(l => l.BusinessUnitId == businessUnitId && l.AttestationId == state.Latest.Id)
                    .OrderBy(l => l.QuoteItemId)
                    .Select(l => new QuotePriceAttestationLineDTO
                    {
                        QuoteItemId = l.QuoteItemId,
                        RfqItemId = l.RfqItemId,
                        ItemDescription = l.ItemDescription,
                        Quantity = l.Quantity,
                        UnitPrice = l.UnitPrice
                    })
                    .ToListAsync(ct);

            return new QuotePriceAttestationStatusDTO
            {
                QuoteId = quoteId,
                Satisfied = state.Satisfied,
                Reason = state.Reason,
                Source = state.Latest?.Source,
                SourceReference = state.Latest?.SourceReference,
                ConfirmedBy = state.Latest?.ConfirmedBy,
                ConfirmedOn = state.Latest?.ConfirmedOn,
                SupersededByPriceChange = state.Latest is not null && !state.Satisfied,
                CurrencyId = currencyId,
                CurrencyCode = currencyCode,
                CurrentLines = state.CurrentLines.Select(l => new QuotePriceAttestationLineDTO
                {
                    QuoteItemId = l.QuoteItemId,
                    RfqItemId = l.RfqItemId,
                    ItemDescription = l.ItemDescription,
                    Quantity = l.Quantity,
                    UnitPrice = l.UnitPrice
                }).ToList(),
                AttestedLines = attestedLines
            };
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
                
                var actor = _commercialAccess == null ? null : await _commercialAccess.ResolveAsync(HttpContext.RequestAborted);
                if (actor == null || actor.BusinessUnitId != businessUnitId) return Forbid();
                var stats = await _repository.GetQuoteStatsAsync(businessUnitId, actor.AccountScope);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                return Unexpected(ex, "stats");
            }
        }
    }
}
