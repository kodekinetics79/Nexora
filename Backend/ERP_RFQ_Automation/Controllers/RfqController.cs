using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DTOs.LookupDTOs;
using ERP_RFQ_Automation.DTOs.RfqDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    [ERP_RFQ_Automation.Platform.Entitlements.RequiresEntitlement(ERP_RFQ_Automation.Platform.Entitlements.TypedEntitlementCatalog.Rfq)]
    public class RfqController : ControllerBase
    {
        private readonly IRfqRepository _repository;
        private readonly Services.IQuoteService _quoteService;
        private readonly ErpRfqAutomationContext _context;
        private readonly ILifecycleApplicationService _lifecycle;
        private readonly ILogger<RfqController>? _logger;

        public RfqController(IRfqRepository repository, Services.IQuoteService quoteService, ErpRfqAutomationContext context, ILifecycleApplicationService lifecycle, ILogger<RfqController>? logger = null)
        {
            _repository = repository;
            _quoteService = quoteService;
            _context = context;
            _lifecycle = lifecycle;
            _logger = logger;
        }

        [HttpGet]
        [RequireModulePermission("RFQ Management", PermissionAction.View)]
        public async Task<ActionResult<PaginatedRfqResponseDTO>> GetAll(
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? search = null,
            [FromQuery] bool? isActive = null,
            [FromQuery] long? assignedToId = null,
            [FromQuery] string? createdBy = null,
            [FromQuery] long? rfqStatusId = null,
            [FromQuery] string? rfqStatusCode = null,
            [FromQuery] string? readiness = null)
        {
            try
            {
                if (!TryGetAuthenticatedBusinessUnitId(out var businessUnitId))
                    return Unauthorized();

                if (pageNumber < 1)
                    return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid RFQ query",
                        "Page number must be greater than or equal to 1."));

                // Relaxed validation: Allow any page size up to 1000
                if (pageSize < 1 || pageSize > 1000)
                    return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid RFQ query",
                        "Page size must be between 1 and 1000."));

                var (items, totalItems) = await _repository.GetAllAsync(businessUnitId, pageNumber, pageSize, search, isActive, assignedToId, createdBy, rfqStatusId, rfqStatusCode, readiness);
                
                return Ok(new PaginatedRfqResponseDTO
                {
                    Items = items,
                    TotalItems = totalItems,
                    PageNumber = pageNumber,
                    PageSize = pageSize,
                    TotalPages = (int)Math.Ceiling(totalItems / (double)pageSize)
                });
            }
            catch (Exception ex)
            {
                return InternalError(ex, "RFQ list retrieval failed.");
            }
        }

        [HttpGet("{id}")]
        [RequireModulePermission("RFQ Management", PermissionAction.View)]
        public async Task<ActionResult<RfqResponseDTO>> GetById(long id)
        {
            try
            {
                if (!TryGetAuthenticatedBusinessUnitId(out var businessUnitId))
                    return Unauthorized();

                var rfq = await _repository.GetByIdAsync(id, businessUnitId);
                return Ok(rfq);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (Exception ex)
            {
                return InternalError(ex, "RFQ retrieval failed.");
            }
        }

        /// <summary>
        /// Records whether Nexora will quote one RFQ line.
        ///
        /// <para>A partial bid is the normal case: an 84-line SEC bid list routinely yields 12
        /// quoted lines, the rest declined for stock, lead time or scope. Only lines explicitly
        /// marked <c>Quote</c> reach a Customer Quote Draft, and a <c>NoQuote</c> requires a
        /// reason — a silent decline is indistinguishable from an oversight when the buyer asks
        /// why a line is missing from our quote.</para>
        ///
        /// <para>Gated on <c>RFQ Management:Edit</c>: deciding what we bid on is a commercial
        /// act, not a view. The decision rules live in <c>Rfqitem.DecideParticipation</c> so
        /// this endpoint cannot be the only thing enforcing them.</para>
        /// </summary>
        [HttpPost("{id}/lines/{lineId}/participation")]
        [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
        public async Task<ActionResult> SetLineParticipation(
            long id, long lineId, [FromBody] RfqLineParticipationRequestDTO request, CancellationToken ct)
        {
            if (request is null)
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid request", "A decision is required."));
            if (!TryGetAuthenticatedBusinessUnitId(out var businessUnitId))
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid request", "Business Unit ID is required."));
            var actor = User.FindFirst(ClaimTypes.Email)?.Value ?? User.Identity?.Name;
            if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();

            // Tenant-scoped by the RFQ, and the line must belong to THAT RFQ — never trust a
            // caller-supplied line id to be inside the RFQ they named.
            var line = await _context.Rfqitems
                .Include(item => item.Rfq)
                .SingleOrDefaultAsync(item => item.Id == lineId
                                              && item.Rfqid == id
                                              && item.Rfq.BusinessUnitId == businessUnitId, ct);
            if (line is null) return NotFound();

            try
            {
                line.DecideParticipation(request.Decision, request.Reason, actor!, DateTime.UtcNow);
                await _context.SaveChangesAsync(ct);
                return Ok(new
                {
                    lineId = line.Id,
                    participationDecision = line.ParticipationDecision,
                    noQuoteReason = line.NoQuoteReason,
                    participationDecidedBy = line.ParticipationDecidedBy,
                    participationDecidedOn = line.ParticipationDecidedOn
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid decision", ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "Decision refused", ex.Message));
            }
        }

        [HttpPost("{id}/prepare-quote-draft")]
        [RequireModulePermission("RFQ Management", PermissionAction.View)]
        [RequireModulePermission("Quotations", PermissionAction.Create)]
        public async Task<ActionResult> PrepareQuoteDraft(long id, CancellationToken ct)
        {
            try
            {
                if (!TryGetAuthenticatedBusinessUnitId(out var businessUnitId))
                    return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid request",
                        "Business Unit ID is required."));
                var actor = User.FindFirst(ClaimTypes.Email)?.Value ?? User.Identity?.Name;
                if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();

                var quote = await _quoteService.PrepareDraftFromRfqAsync(id, businessUnitId, actor, ct);
                return Ok(quote);
            }
            catch (KeyNotFoundException)
            {
                return NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(Problem(StatusCodes.Status409Conflict, "Quote draft not prepared", ex.Message));
            }
        }

        [HttpPost]
        [RequireModulePermission("RFQ Management", PermissionAction.Create)]
        public async Task<ActionResult<RfqResponseDTO>> Create([FromBody] RfqCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);

                if (!long.TryParse(User.FindFirst("businessUnitId")?.Value, out var claimBUId) || claimBUId <= 0)
                    return Unauthorized();
                // No LeadId is NOT rejected here any more: the repository preserves the
                // serial-lineage invariant by creating a governed manual-origin shell lead
                // in the same transaction as the RFQ (Lead -> RFQ -> Quote keep sharing one
                // commercial case). Requests that cannot be anchored to a commercial case
                // (no lead AND no tenant-owned customer) surface as a structured 400 below.
                var actor = User.FindFirst(ClaimTypes.Email)?.Value ?? User.Identity?.Name;
                if (string.IsNullOrWhiteSpace(actor)) return Unauthorized();

            var rfq = new Rfq
            {
                Rfqno = string.Empty,
                BuyersName = request.BuyersName,
                RecDate = request.RecDate,
                BidClosingDate = request.BidClosingDate,
                BiddingDecision = request.BiddingDecision,
                AcknowledgmentDate = request.AcknowledgmentDate,
                SubDate = request.SubDate,
                HeaderRemarks = request.HeaderRemarks,
                OpportunityNo = request.OpportunityNo,
                Rfqtype = request.Rfqtype,
                RfqtypeId = request.RfqtypeId,
                DurationAgreement = request.DurationAgreement,
                LeadId = request.LeadId,
                CustomerId = request.CustomerId,
                CreatedBy = actor,
                CreatedDate = DateTime.UtcNow,
                BusinessUnitId = claimBUId,
                NoOfLineItems = request.Rfqitems?.Count ?? 0,
                Rfqitems = (request.Rfqitems ?? new List<RfqitemCreateRequestDTO>()).Select(i => new Rfqitem
                {
                    CompanyRef = i.CompanyRef,
                    CustomerAccountPortalId = i.CustomerAccountPortalId,
                    CustomerRfqno = i.CustomerRfqno,
                    ItemMaterialCode = i.ItemMaterialCode,
                    LineItemNo = i.LineItemNo,
                    ProductId = i.ProductId,
                    CommodityProduct = i.CommodityProduct,
                    ProductShortName = i.ProductShortName,
                    ProductShortDescription = i.ProductShortDescription,
                    Alternative = i.Alternative,
                    BuyerName = i.BuyerName,
                    Currency = i.Currency,
                    CurrencyId = i.CurrencyId,
                    UnitOfMeasure = i.UnitOfMeasure,
                    UomId = i.UomId,
                    UnitPrice = i.UnitPrice,
                    Quantity = i.Quantity,
                    StorageLocation = i.StorageLocation,
                    WarehouseId = i.WarehouseId,
                    ManufacturerName = i.ManufacturerName,
                    ManufacturerPartNumber = i.ManufacturerPartNumber,
                    SupplierId = i.SupplierId,
                    AlternateProductName = i.AlternateProductName,
                    AlternatePartNumber = i.AlternatePartNumber,
                    ItemText = i.ItemText,
                    MaterialPotext = i.MaterialPotext,
                    LeadTime = i.LeadTime,
                    RequiredDesiredDate = i.RequiredDesiredDate,
                    ReceivedDate = i.ReceivedDate,
                    BidClosingDateLine = i.BidClosingDateLine,
                    CreatedBy = actor,
                    CreatedDate = DateTime.UtcNow,
                    Aiconfidence = i.Aiconfidence
                }).ToList()
            };

            await _repository.AddAsync(rfq);

            var response = await _repository.GetByIdAsync(rfq.Id, rfq.BusinessUnitId);

            return CreatedAtAction(nameof(GetById), new { id = rfq.Id }, response);
            }
            catch (ArgumentException ex)
            {
                // The repository's request-validation failures (missing customer, a lead or
                // customer that is not owned by this tenant, unknown RFQ type). The message
                // is written for the user and safe to render.
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "RFQ not created", ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(Problem(StatusCodes.Status409Conflict, "RFQ not created", ex.Message));
            }
            catch (Exception ex)
            {
                return InternalError(ex, "RFQ creation failed.");
            }
        }

        [HttpPut("{id}")]
        [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromBody] RfqUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);

                if (!TryGetAuthenticatedBusinessUnitId(out var businessUnitId))
                    return Unauthorized();
                var actor = User.FindFirst(ClaimTypes.Email)?.Value ?? User.Identity?.Name;
                if (string.IsNullOrWhiteSpace(actor))
                    return Unauthorized();

            var rfq = new Rfq
            {
                Id = id,
                BuyersName = request.BuyersName,
                RecDate = request.RecDate,
                BidClosingDate = request.BidClosingDate,
                BiddingDecision = request.BiddingDecision,
                AcknowledgmentDate = request.AcknowledgmentDate,
                SubDate = request.SubDate,
                HeaderRemarks = request.HeaderRemarks,
                OpportunityNo = request.OpportunityNo,
                Rfqtype = request.Rfqtype,
                RfqtypeId = request.RfqtypeId,
                DurationAgreement = request.DurationAgreement,
                LeadId = request.LeadId,
                CustomerId = request.CustomerId,
                ModifiedBy = actor,
                ModifiedDate = DateTime.UtcNow,
                BusinessUnitId = businessUnitId,
                RfqstatusId = request.RfqstatusId,
                NoOfLineItems = request.Rfqitems?.Count ?? 0,
                Rfqitems = request.Rfqitems.Select(i => new Rfqitem
                {
                    Id = i.Id,
                    CompanyRef = i.CompanyRef,
                    CustomerAccountPortalId = i.CustomerAccountPortalId,
                    CustomerRfqno = i.CustomerRfqno,
                    ItemMaterialCode = i.ItemMaterialCode,
                    LineItemNo = i.LineItemNo,
                    ProductId = i.ProductId,
                    CommodityProduct = i.CommodityProduct,
                    ProductShortName = i.ProductShortName,
                    ProductShortDescription = i.ProductShortDescription,
                    Alternative = i.Alternative,
                    BuyerName = i.BuyerName,
                    Currency = i.Currency,
                    CurrencyId = i.CurrencyId,
                    UnitOfMeasure = i.UnitOfMeasure,
                    UomId = i.UomId,
                    UnitPrice = i.UnitPrice,
                    Quantity = i.Quantity,
                    StorageLocation = i.StorageLocation,
                    WarehouseId = i.WarehouseId,
                    ManufacturerName = i.ManufacturerName,
                    ManufacturerPartNumber = i.ManufacturerPartNumber,
                    SupplierId = i.SupplierId,
                    AlternateProductName = i.AlternateProductName,
                    AlternatePartNumber = i.AlternatePartNumber,
                    ItemText = i.ItemText,
                    MaterialPotext = i.MaterialPotext,
                    LeadTime = i.LeadTime,
                    RequiredDesiredDate = i.RequiredDesiredDate,
                    ReceivedDate = i.ReceivedDate,
                    BidClosingDateLine = i.BidClosingDateLine,
                    ModifiedBy = actor,
                    ModifiedDate = DateTime.UtcNow,
                    Aiconfidence = i.Aiconfidence
                }).ToList()
            };

            await _repository.UpdateAsync(rfq);

            return NoContent();
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(Problem(StatusCodes.Status404NotFound, "RFQ not found", ex.Message));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "RFQ not updated", ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(Problem(StatusCodes.Status409Conflict, "RFQ not updated", ex.Message));
            }
            catch (Exception ex)
            {
                return InternalError(ex, "RFQ update failed.");
            }
        }

        [HttpPost("{id}/approve")]
        [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
        public async Task<ActionResult> Approve(
            long id,
            [FromBody] ApproveRfqRequest? request = null)
        {
            // SEC-07: the business unit and the approver identity come from the token,
            // never from client input (was a spoofable ?approvedBy= query param).
            if (!TryGetAuthenticatedBusinessUnitId(out var businessUnitId))
                return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid request",
                    "Business Unit ID is required."));
            var approvedBy = User.Identity?.Name ?? "System";

            using var transaction = await _context.Database.BeginTransactionAsync();
            var quoteDelivered = false;
            try
            {
                var quoteId = await _repository.ApproveAsync(id, approvedBy, businessUnitId, request?.CustomerId);

                // ARCH-10: the quote is generated and committed regardless of whether the
                // notification email can be sent. A missing/failed email must NOT discard
                // the approved quote (previously it rolled the whole approval back).
                var quote = await _context.Quotes
                    .Include(q => q.Rfq).ThenInclude(r => r.Lead)
                    .Include(q => q.Customer)
                    .FirstOrDefaultAsync(q => q.Id == quoteId);

                string selectedEmail = (quote?.Customer?.ContactEmail ?? quote?.Rfq?.Lead?.Clientemail ?? "").Trim();
                if (!string.IsNullOrWhiteSpace(request?.RecipientEmail)
                    && !string.Equals(request.RecipientEmail.Trim(), selectedEmail, StringComparison.OrdinalIgnoreCase))
                {
                    await transaction.RollbackAsync();
                    return BadRequest(Problem(StatusCodes.Status400BadRequest, "RFQ not approved",
                        "The recipient must match the verified customer contact for this RFQ."));
                }

                string? emailWarning = null;
                if (!string.IsNullOrWhiteSpace(selectedEmail) && selectedEmail.Contains("@"))
                {
                    try
                    {
                        // WP-B3: the send can come back "held" (below-floor pricing) —
                        // the quote exists but the email is parked in the Approvals inbox.
                        var sendResult = await _quoteService.SendQuoteEmailAsync(
                            quoteId, businessUnitId, selectedEmail, request?.EmailSubject, request?.EmailBody,
                            new ERP_RFQ_Automation.DTOs.QuoteDTOs.QuoteSendOptions
                            {
                                RequestedByUserId = long.TryParse(
                                    User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value,
                                    out var approverId) ? approverId : null,
                                RequestedBy = User.FindFirst(ClaimTypes.Email)?.Value ?? approvedBy
                            });

                        // R5: the quote is created, but it is NOT emailed until a rep confirms
                        // where its prices came from. This auto-send path has no rep in front
                        // of it, so approval now generates the quote and stops short of the
                        // customer's inbox rather than sending an unconfirmed price.
                        if (sendResult.BlockedPendingPriceAttestation)
                        {
                            emailWarning = "Quote generated, but it was not sent: " +
                                           (sendResult.PriceAttestationReason ??
                                            "the price source has not been confirmed.") +
                                           " Open the quote, confirm the price source, and send it from there.";
                        }
                        else if (sendResult.Held)
                        {
                            emailWarning = "Quote generated, but sending is held for approval — pricing is below " +
                                           $"your floor ({sendResult.HoldSummary}). Track it in the Approvals inbox.";
                        }
                        else if (sendResult.FailedPermanently)
                        {
                            emailWarning = "Quote generated, but delivery requires operator review after repeated failures.";
                        }
                        else if (sendResult.Delivered)
                        {
                            quoteDelivered = true;
                        }
                        else
                        {
                            emailWarning = "Quote generated and delivery queued. Customer delivery is not yet confirmed.";
                        }
                    }
                    catch (Exception emailEx)
                    {
                        _logger?.LogError(emailEx, "Quote delivery enqueue failed for quote {QuoteId}; trace {TraceId}.", quoteId, HttpContext.TraceIdentifier);
                        emailWarning = "Quote generated, but delivery could not be queued. An operator must review it.";
                    }
                }
                else
                {
                    emailWarning = "Quote generated, but no recipient email was found — send it manually from the quote.";
                }

                await transaction.CommitAsync();
                await transaction.DisposeAsync();
                if (quoteDelivered)
                {
                    var state = await _lifecycle.GetRfqStateAsync(businessUnitId, id, default);
                    await _lifecycle.TransitionRfqAsync(businessUnitId, id,
                        new LifecycleActor(approvedBy, "QuoteDelivery"),
                        new LifecycleTransitionCommand("QUOTE_SENT", state.Version, null, null, "Api",
                            HttpContext.TraceIdentifier, $"quote-{quoteId}", $"quote-delivery:{businessUnitId}:{quoteId}"),
                        false, default);
                }
                return Ok(new { message = "RFQ approved and Quote generated successfully", quoteId, emailWarning });
            }
            catch (KeyNotFoundException ex)
            {
                try { await transaction.RollbackAsync(); } catch { /* already rolled back */ }
                return NotFound(Problem(StatusCodes.Status404NotFound, "RFQ not found", ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                try { await transaction.RollbackAsync(); } catch { /* already rolled back */ }
                return Conflict(Problem(StatusCodes.Status409Conflict, "RFQ not approved", ex.Message));
            }
            catch (Exception ex)
            {
                try { await transaction.RollbackAsync(); } catch { /* already rolled back */ }
                return InternalError(ex, "RFQ approval failed.");
            }
        }

        [HttpGet("stats")]
        [RequireModulePermission("RFQ Management", PermissionAction.View)]
        public async Task<ActionResult<RfqStatsDTO>> GetRfqStats()
        {
            try
            {
                if (!TryGetAuthenticatedBusinessUnitId(out var businessUnitId))
                    return BadRequest(Problem(StatusCodes.Status400BadRequest, "Invalid request",
                        "Business Unit ID is required."));

                var stats = await _repository.GetRfqStatsAsync(businessUnitId);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                return InternalError(ex, "RFQ statistics retrieval failed.");
            }
        }

        [HttpDelete("{id}")]
        // Was [Authorize(Policy = "CanDeleteRFQ")] — that legacy policy checks module
        // "RFQ", which does not match the Modules row / frontend name "RFQ Management".
        [RequireModulePermission("RFQ Management", PermissionAction.Delete)]
        public async Task<ActionResult> Delete(long id)
        {
            try
            {
                if (!TryGetAuthenticatedBusinessUnitId(out var businessUnitId))
                    return Unauthorized();

                await _repository.DeleteAsync(id, businessUnitId);
                return NoContent();
            }
            catch (Exception ex)
            {
                return InternalError(ex, "RFQ deletion failed.");
            }
        }

        private ObjectResult InternalError(Exception exception, string operation)
        {
            _logger?.LogError(exception, "{Operation} Trace {TraceId}.", operation, HttpContext.TraceIdentifier);
            var problem = new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "The request could not be completed."
            };
            problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
            return StatusCode(StatusCodes.Status500InternalServerError, problem);
        }

        /// <summary>
        /// RFC 7807 body carrying the request's trace identifier, so a caller reporting a
        /// failure gives support an id that ties straight back to the server log entry.
        /// Same pattern as SupplierController: the bare-string / anonymous-object bodies
        /// these replaced were not renderable by the frontend's error surface.
        /// </summary>
        private ProblemDetails Problem(int status, string title, string detail)
        {
            var problem = new ProblemDetails { Status = status, Title = title, Detail = detail };
            problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
            return problem;
        }

        public sealed record ApproveRfqRequest(
            string? RecipientEmail,
            string? EmailSubject,
            string? EmailBody,
            long? CustomerId);


        [HttpGet("lookups/RFQType")]
        public async Task<ActionResult<List<RFQTypeLookupDTO>>> GetItemStatuses()
        {
            return Ok(await _repository.GetRFQTypeAsync());
        }

        private bool TryGetAuthenticatedBusinessUnitId(out long businessUnitId) =>
            long.TryParse(User.FindFirst("businessUnitId")?.Value, out businessUnitId)
            && businessUnitId > 0;
    }
}
