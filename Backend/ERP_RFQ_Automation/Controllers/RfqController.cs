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
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class RfqController : ControllerBase
    {
        private readonly IRfqRepository _repository;
        private readonly Services.IQuoteService _quoteService;
        private readonly ErpRfqAutomationContext _context;

        public RfqController(IRfqRepository repository, Services.IQuoteService quoteService, ErpRfqAutomationContext context)
        {
            _repository = repository;
            _quoteService = quoteService;
            _context = context;
        }

        [HttpGet]
        [RequireModulePermission("RFQ Management", PermissionAction.View)]
        public async Task<ActionResult<PaginatedRfqResponseDTO>> GetAll(
            [FromQuery] long? businessUnitId = null,
            [FromQuery] int pageNumber = 1,
            [FromQuery] int pageSize = 10,
            [FromQuery] string? search = null,
            [FromQuery] bool? isActive = null,
            [FromQuery] long? assignedToId = null,
            [FromQuery] string? createdBy = null,
            [FromQuery] long? rfqStatusId = null)
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

                var (items, totalItems) = await _repository.GetAllAsync(targetBUId, pageNumber, pageSize, search, isActive, assignedToId, createdBy, rfqStatusId);
                
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
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving data: {ex.Message}");
            }
        }

        [HttpGet("{id}")]
        [RequireModulePermission("RFQ Management", PermissionAction.View)]
        public async Task<ActionResult<RfqResponseDTO>> GetById(long id, [FromQuery] long? businessUnitId = null)
        {
            try
            {
                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);

                if (targetBUId <= 0)
                    return BadRequest("Business Unit ID is required.");

                var rfq = await _repository.GetByIdAsync(id, targetBUId);
                return Ok(rfq);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error retrieving rfq: {ex.Message}");
            }
        }

        [HttpPost]
        [RequireModulePermission("RFQ Management", PermissionAction.Create)]
        public async Task<ActionResult<RfqResponseDTO>> Create([FromBody] RfqCreateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                if (request.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            var rfq = new Rfq
            {
                Rfqno = request.Rfqno,
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
                CreatedBy = request.CreatedBy,
                CreatedDate = DateTime.UtcNow,
                BusinessUnitId = request.BusinessUnitId,
                RfqstatusId = request.RfqstatusId,
                NoOfLineItems = request.Rfqitems?.Count ?? 0,
                Rfqitems = request.Rfqitems.Select(i => new Rfqitem
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
                    CreatedBy = request.CreatedBy,
                    CreatedDate = DateTime.UtcNow,
                    Aiconfidence = i.Aiconfidence
                }).ToList()
            };

            await _repository.AddAsync(rfq);

            var response = await _repository.GetByIdAsync(rfq.Id, rfq.BusinessUnitId);

            return CreatedAtAction(nameof(GetById), new { id = rfq.Id, businessUnitId = rfq.BusinessUnitId }, response);
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        [HttpPut("{id}")]
        [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
        public async Task<ActionResult> Update(long id, [FromBody] RfqUpdateRequestDTO request)
        {
            try
            {
                if (!ModelState.IsValid) return BadRequest(ModelState);

                var claimBUId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (claimBUId > 0) request.BusinessUnitId = claimBUId;

                if (request.BusinessUnitId <= 0) return BadRequest("Business Unit ID is required.");

            var rfq = new Rfq
            {
                Id = id,
                Rfqno = request.Rfqno,
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
                ModifiedBy = request.ModifiedBy,
                ModifiedDate = DateTime.UtcNow,
                BusinessUnitId = request.BusinessUnitId,
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
                    ModifiedBy = request.ModifiedBy,
                    ModifiedDate = DateTime.UtcNow,
                    Aiconfidence = i.Aiconfidence
                }).ToList()
            };

            await _repository.UpdateAsync(rfq);

            return NoContent();
            }
            catch (Exception ex)
            {
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error: {ex.Message}");
            }
        }

        [HttpPost("{id}/approve")]
        [RequireModulePermission("RFQ Management", PermissionAction.Edit)]
        public async Task<ActionResult> Approve(
            long id,
            [FromQuery] string? recipientEmail = null,
            [FromQuery] string? emailSubject = null,
            [FromQuery] string? emailBody = null,
            [FromQuery] long? customerId = null)
        {
            // SEC-07: the business unit and the approver identity come from the token,
            // never from client input (was a spoofable ?approvedBy= query param).
            var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
            if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");
            var approvedBy = User.Identity?.Name ?? "System";

            using var transaction = await _context.Database.BeginTransactionAsync();
            try
            {
                var quoteId = await _repository.ApproveAsync(id, approvedBy, businessUnitId, customerId);

                // ARCH-10: the quote is generated and committed regardless of whether the
                // notification email can be sent. A missing/failed email must NOT discard
                // the approved quote (previously it rolled the whole approval back).
                var quote = await _context.Quotes
                    .Include(q => q.Rfq).ThenInclude(r => r.Lead)
                    .Include(q => q.Customer)
                    .FirstOrDefaultAsync(q => q.Id == quoteId);

                string selectedEmail = (!string.IsNullOrEmpty(recipientEmail)
                    ? recipientEmail
                    : (quote?.Customer?.ContactEmail ?? quote?.Rfq?.Lead?.Clientemail ?? "")).Trim();

                string? emailWarning = null;
                if (!string.IsNullOrWhiteSpace(selectedEmail) && selectedEmail.Contains("@"))
                {
                    try
                    {
                        await _quoteService.SendQuoteEmailAsync(quoteId, selectedEmail, emailSubject, emailBody);
                    }
                    catch (Exception emailEx)
                    {
                        emailWarning = $"Quote generated, but the notification email could not be sent: {emailEx.Message}";
                    }
                }
                else
                {
                    emailWarning = "Quote generated, but no recipient email was found — send it manually from the quote.";
                }

                await transaction.CommitAsync();
                return Ok(new { message = "RFQ approved and Quote generated successfully", quoteId, emailWarning });
            }
            catch (KeyNotFoundException ex)
            {
                try { await transaction.RollbackAsync(); } catch { /* already rolled back */ }
                return NotFound(ex.Message);
            }
            catch (Exception ex)
            {
                try { await transaction.RollbackAsync(); } catch { /* already rolled back */ }
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpGet("stats")]
        [RequireModulePermission("RFQ Management", PermissionAction.View)]
        public async Task<ActionResult<RfqStatsDTO>> GetRfqStats()
        {
            try
            {
                var businessUnitId = long.Parse(User.FindFirst("businessUnitId")?.Value ?? "0");
                if (businessUnitId == 0) return BadRequest("Business Unit ID is required.");
                
                var stats = await _repository.GetRfqStatsAsync(businessUnitId);
                return Ok(stats);
            }
            catch (Exception ex)
            {
                return StatusCode(500, $"Internal server error: {ex.Message}");
            }
        }

        [HttpDelete("{id}")]
        // Was [Authorize(Policy = "CanDeleteRFQ")] — that legacy policy checks module
        // "RFQ", which does not match the Modules row / frontend name "RFQ Management".
        [RequireModulePermission("RFQ Management", PermissionAction.Delete)]
        public async Task<ActionResult> Delete(long id, [FromQuery] long? businessUnitId = null)
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
                return StatusCode(StatusCodes.Status500InternalServerError, $"Error deleting rfq: {ex.Message}");
            }
        }


        [HttpGet("lookups/RFQType")]
        public async Task<ActionResult<List<RFQTypeLookupDTO>>> GetItemStatuses()
        {
            return Ok(await _repository.GetRFQTypeAsync());
        }
    }
}