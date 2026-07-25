using ERP_RFQ_Automation.DTOs.LookupDTOs;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.DTOs.RfqDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Repositories
{
    public class RfqRepository : IRfqRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public RfqRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<RfqResponseDTO>, int TotalItems)> GetAllAsync(long businessUnitId, int pageNumber = 1, int pageSize = 10, string? search = null, bool? isActive = null, long? assignedToId = null, string? createdBy = null, long? rfqStatusId = null, string? rfqStatusCode = null, string? readiness = null)
        {
            IQueryable<Rfq> query = _context.Rfqs
                .AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId)
                .Include(r => r.BusinessUnit)
                .Include(r => r.Lead)
                .Include(r => r.Rfqstatus)
                .Include(r => r.RfqtypeNavigation)
                .Include(r => r.Customer);

            if (rfqStatusId.HasValue)
            {
                query = query.Where(r => r.RfqstatusId == rfqStatusId.Value);
            }
            if (!string.IsNullOrWhiteSpace(rfqStatusCode))
            {
                var code = rfqStatusCode.Trim().ToUpper();
                query = query.Where(r => r.Rfqstatus != null &&
                    (r.Rfqstatus.SetupCode != null && r.Rfqstatus.SetupCode.ToUpper() == code ||
                     r.Rfqstatus.SetupValue.ToUpper() == code));
            }
            if (string.Equals(readiness, "ready-for-quote", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(r => r.CustomerId != null && r.LeadId != null
                    && r.Rfqitems.Any()
                    && !r.Rfqitems.Any(item => item.Quantity <= 0
                        || item.UnitOfMeasure == null || item.UnitOfMeasure == ""
                        || (item.ItemMaterialCode == null || item.ItemMaterialCode == "")
                           && (item.ManufacturerPartNumber == null || item.ManufacturerPartNumber == "")
                           && (item.ProductShortDescription == null || item.ProductShortDescription == "")));
            }

            if (assignedToId.HasValue || !string.IsNullOrWhiteSpace(createdBy))
            {
                query = query.Where(r =>
                    (assignedToId.HasValue && r.Lead != null && r.Lead.AssignTo == assignedToId.Value) ||
                    (!string.IsNullOrWhiteSpace(createdBy) && r.CreatedBy == createdBy)
                );
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                search = search.Trim().ToLower();
                query = query.Where(r => r.Rfqno.ToLower().Contains(search)
                    || (r.NexoraSerial != null && r.NexoraSerial.ToLower().Contains(search))
                    || (r.BuyersName != null && r.BuyersName.ToLower().Contains(search)));
            }

            var totalItems = await query.CountAsync();

            var rfqs = await query
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Batch load item counts for all RFQs in a single query
            var rfqIds = rfqs.Select(r => r.Id).ToList();
            var itemCounts = await _context.Rfqitems
                .AsNoTracking()
                .Where(ri => rfqIds.Contains(ri.Rfqid))
                .GroupBy(ri => ri.Rfqid)
                .Select(g => new { RfqId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.RfqId, x => x.Count);

            var dtos = rfqs.Select(r => new RfqResponseDTO
            {
                Id = r.Id,
                CommercialCaseId = r.CommercialCaseId ?? (r.Lead != null ? r.Lead.CommercialCaseId : null),
                CommercialCaseReference = r.NexoraSerial ?? (r.Lead != null ? r.Lead.CommercialCaseReference : null),
                NexoraSerial = r.NexoraSerial ?? (r.Lead != null ? r.Lead.CommercialCaseReference : null),
                Rfqno = r.Rfqno,
                BuyersName = r.BuyersName,
                RecDate = r.RecDate,
                BidClosingDate = r.BidClosingDate,
                BiddingDecision = r.BiddingDecision,
                AcknowledgmentDate = r.AcknowledgmentDate,
                SubDate = r.SubDate,
                HeaderRemarks = r.HeaderRemarks,
                OpportunityNo = r.OpportunityNo,
                NoOfLineItems = r.NoOfLineItems,
                Rfqtype = r.Rfqtype,
                RfqtypeId = r.RfqtypeId,
                DurationAgreement = r.DurationAgreement,
                LeadId = r.LeadId,
                ActiveLeadRevision = r.Lead?.CurrentRevisionNumber ?? 1,
                CreatedBy = r.CreatedBy,
                CreatedDate = r.CreatedDate,
                ModifiedBy = r.ModifiedBy,
                ModifiedDate = r.ModifiedDate,
                BusinessUnitId = r.BusinessUnitId,
                BusinessUnitName = r.BusinessUnit?.BusinessUnitName,
                RfqstatusId = r.RfqstatusId,
                RfqstatusValue = r.Rfqstatus?.SetupValue,
                RfqstatusCode = r.Rfqstatus?.SetupCode,
                LifecycleVersion = r.LifecycleVersion,
                CustomerId = r.CustomerId,
                ContactId = r.ContactId,
                CustomerName = r.Customer != null ? r.Customer.Name : null,
                CustomerEmail = r.Customer != null ? r.Customer.ContactEmail : null,
                LeadEmail = r.Lead != null ? r.Lead.Clientemail : null,
                ItemCount = itemCounts.TryGetValue(r.Id, out var count) ? count : 0,
                Rfqitems = new List<RfqitemResponseDTO>() // Empty list for list view
            }).ToList();

            return (dtos, totalItems);
        }

        public async Task<RfqResponseDTO> GetByIdAsync(long id, long businessUnitId)
        {
            var rfq = await _context.Rfqs
                .Include(r => r.BusinessUnit)
                .Include(r => r.Lead)
                .Include(r => r.Rfqstatus)
                .Include(r => r.RfqtypeNavigation)
                .Include(r => r.Rfqitems).ThenInclude(i => i.CurrencyNavigation)
                .Include(r => r.Rfqitems).ThenInclude(i => i.Product)
                .Include(r => r.Rfqitems).ThenInclude(i => i.Supplier)
                .Include(r => r.Rfqitems).ThenInclude(i => i.Uom)
                .Include(r => r.Rfqitems).ThenInclude(i => i.Warehouse)
                .Include(r => r.Customer)
                .FirstOrDefaultAsync(r => r.Id == id && r.BusinessUnitId == businessUnitId);

            if (rfq == null)
                throw new KeyNotFoundException($"RFQ with ID {id} not found in Business Unit {businessUnitId}.");

            return new RfqResponseDTO
            {
                Id = rfq.Id,
                CommercialCaseId = rfq.CommercialCaseId ?? rfq.Lead?.CommercialCaseId,
                CommercialCaseReference = rfq.NexoraSerial ?? rfq.Lead?.CommercialCaseReference,
                NexoraSerial = rfq.NexoraSerial ?? rfq.Lead?.CommercialCaseReference,
                Rfqno = rfq.Rfqno,
                BuyersName = rfq.BuyersName,
                RecDate = rfq.RecDate,
                BidClosingDate = rfq.BidClosingDate,
                BiddingDecision = rfq.BiddingDecision,
                AcknowledgmentDate = rfq.AcknowledgmentDate,
                SubDate = rfq.SubDate,
                HeaderRemarks = rfq.HeaderRemarks,
                OpportunityNo = rfq.OpportunityNo,
                NoOfLineItems = rfq.NoOfLineItems,
                Rfqtype = rfq.Rfqtype,
                RfqtypeId = rfq.RfqtypeId,
                DurationAgreement = rfq.DurationAgreement,
                LeadId = rfq.LeadId,
                ActiveLeadRevision = rfq.Lead?.CurrentRevisionNumber ?? 1,
                CreatedBy = rfq.CreatedBy,
                CreatedDate = rfq.CreatedDate,
                ModifiedBy = rfq.ModifiedBy,
                ModifiedDate = rfq.ModifiedDate,
                BusinessUnitId = rfq.BusinessUnitId,
                BusinessUnitName = rfq.BusinessUnit?.BusinessUnitName,
                RfqstatusId = rfq.RfqstatusId,
                RfqstatusValue = rfq.Rfqstatus?.SetupValue,
                RfqstatusCode = rfq.Rfqstatus?.SetupCode,
                LifecycleVersion = rfq.LifecycleVersion,
                CustomerId = rfq.CustomerId,
                ContactId = rfq.ContactId,
                CustomerName = rfq.Customer != null ? rfq.Customer.Name : null,
                CustomerEmail = rfq.Customer != null ? rfq.Customer.ContactEmail : null,
                LeadEmail = rfq.Lead != null ? rfq.Lead.Clientemail : null,
                Rfqitems = rfq.Rfqitems.Select(i => new RfqitemResponseDTO
                {
                    Id = i.Id,
                    Rfqid = i.Rfqid,
                    CompanyRef = i.CompanyRef,
                    CustomerAccountPortalId = i.CustomerAccountPortalId,
                    CustomerRfqno = i.CustomerRfqno,
                    ItemMaterialCode = i.ItemMaterialCode,
                    LineItemNo = i.LineItemNo,
                    ProductId = i.ProductId,
                    ProductName = i.Product?.ProductName,
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
                    WarehouseName = i.Warehouse?.WarehouseName,
                    ManufacturerName = i.ManufacturerName,
                    ManufacturerPartNumber = i.ManufacturerPartNumber,
                    SupplierId = i.SupplierId,
                    SupplierName = i.Supplier?.Name,
                    AlternateProductName = i.AlternateProductName,
                    AlternatePartNumber = i.AlternatePartNumber,
                    ItemText = i.ItemText,
                    MaterialPotext = i.MaterialPotext,
                    LeadTime = i.LeadTime,
                    RequiredDesiredDate = i.RequiredDesiredDate,
                    ReceivedDate = i.ReceivedDate,
                    BidClosingDateLine = i.BidClosingDateLine,
                    CreatedBy = i.CreatedBy,
                    CreatedDate = i.CreatedDate,
                    ModifiedBy = i.ModifiedBy,
                    ModifiedDate = i.ModifiedDate,
                    Aiconfidence = i.Aiconfidence
                }).ToList()
            };
        }

        public async Task AddAsync(Rfq rfq)
        {
            // Validate uniqueness of Rfqno within BusinessUnit
            var rfqNoExists = await _context.Rfqs.AnyAsync(r => r.Rfqno == rfq.Rfqno && r.BusinessUnitId == rfq.BusinessUnitId);
            if (rfqNoExists)
                throw new ArgumentException($"RFQ number {rfq.Rfqno} already exists in this Business Unit.");

            // Validate FKs
            var buExists = await _context.BusinessUnits.AnyAsync(b => b.Id == rfq.BusinessUnitId);
            if (!buExists)
                throw new ArgumentException($"Business Unit ID {rfq.BusinessUnitId} does not exist.");

            if (rfq.RfqtypeId.HasValue)
            {
                var typeExists = await _context.SetupMasters.AnyAsync(sm => sm.SetupId == rfq.RfqtypeId);
                if (!typeExists)
                    throw new ArgumentException($"RFQ Type ID {rfq.RfqtypeId} does not exist.");
            }

            if (!rfq.LeadId.HasValue)
                throw new ArgumentException("A tenant-owned lead is required so the RFQ belongs to a commercial case.");
            var lead = await _context.Leads.SingleOrDefaultAsync(l =>
                l.Id == rfq.LeadId.Value && l.BusinessUnitId == rfq.BusinessUnitId);
            if (lead == null)
                throw new ArgumentException($"Lead ID {rfq.LeadId} does not exist in this business unit.");
            if (!lead.CustomerId.HasValue)
                throw new ArgumentException("Resolve the lead customer before creating an RFQ.");
            rfq.InheritCommercialIdentity(lead);
            rfq.RfqstatusId = await LifecycleStatusCatalog.ResolveIdAsync(
                _context, rfq.BusinessUnitId, "Rfq", "DRAFT");

            rfq.CreatedDate = DateTime.UtcNow;

            _context.Rfqs.Add(rfq);
            await _context.SaveChangesAsync();
        }

        public async Task UpdateAsync(Rfq rfq)
        {
            var existing = await _context.Rfqs.FirstOrDefaultAsync(r => r.Id == rfq.Id);

            if (existing == null)
                throw new KeyNotFoundException($"RFQ with ID {rfq.Id} not found.");

            if (existing.BusinessUnitId != rfq.BusinessUnitId)
                throw new ArgumentException("Cannot change the Business Unit of an RFQ.");

            // Uniqueness check for Rfqno (exclude self)
            var rfqNoExists = await _context.Rfqs.AnyAsync(r => r.Rfqno == rfq.Rfqno && r.BusinessUnitId == rfq.BusinessUnitId && r.Id != rfq.Id);
            if (rfqNoExists)
                throw new ArgumentException($"RFQ number {rfq.Rfqno} already exists in this Business Unit.");

            // Update fields
            existing.Rfqno = rfq.Rfqno;
            existing.BuyersName = rfq.BuyersName;
            existing.RecDate = rfq.RecDate;
            existing.BidClosingDate = rfq.BidClosingDate;
            existing.BiddingDecision = rfq.BiddingDecision;
            existing.AcknowledgmentDate = rfq.AcknowledgmentDate;
            existing.SubDate = rfq.SubDate;
            existing.HeaderRemarks = rfq.HeaderRemarks;
            existing.OpportunityNo = rfq.OpportunityNo;
            existing.NoOfLineItems = rfq.NoOfLineItems;
            existing.Rfqtype = rfq.Rfqtype;
            existing.RfqtypeId = rfq.RfqtypeId;
            existing.DurationAgreement = rfq.DurationAgreement;
            if (rfq.LeadId != existing.LeadId || rfq.CustomerId != existing.CustomerId)
                throw new InvalidOperationException("RFQ commercial identity cannot be changed by an ordinary update.");
            existing.ModifiedBy = rfq.ModifiedBy;
            existing.ModifiedDate = DateTime.UtcNow;
            existing.BusinessUnitId = rfq.BusinessUnitId;
            if (rfq.RfqstatusId != existing.RfqstatusId)
                throw new InvalidOperationException("RFQ status must be changed through the governed lifecycle command.");

            await _context.SaveChangesAsync();
        }

        public async Task<long> ApproveAsync(long id, string approvedBy, long businessUnitId, long? customerId = null)
        {
            var rfq = await _context.Rfqs
                .Include(r => r.Rfqitems)
                .Include(r => r.Lead)
                .Include(r => r.Rfqstatus)
                // SEC-07: scope to the caller's business unit so one tenant cannot
                // approve (and generate/email a quote for) another tenant's RFQ.
                .FirstOrDefaultAsync(r => r.Id == id && r.BusinessUnitId == businessUnitId);

            if (rfq == null) throw new KeyNotFoundException($"RFQ with ID {id} not found.");

            var existingQuoteId = await _context.Quotes
                .Where(q => q.Rfqid == id && q.BusinessUnitId == businessUnitId)
                .Select(q => (long?)q.Id).FirstOrDefaultAsync();
            if (existingQuoteId.HasValue) return existingQuoteId.Value;

            var lifecycleStatus = LifecyclePolicy.Canonicalize(
                "Rfq", rfq.Rfqstatus?.SetupCode, rfq.Rfqstatus?.SetupValue);
            if (lifecycleStatus != "QUOTE_PREPARATION")
                throw new InvalidOperationException(
                    "A quote can only be generated when the RFQ lifecycle is in QUOTE_PREPARATION.");

            rfq.ModifiedBy = approvedBy;
            rfq.ModifiedDate = DateTime.UtcNow;

            if (rfq.Lead == null)
                throw new InvalidOperationException("The RFQ must be linked to a lead before a Quote can be created.");
            rfq.InheritCommercialIdentity(rfq.Lead);
            if (customerId.HasValue && customerId != rfq.CustomerId)
                throw new InvalidOperationException("The selected customer does not match the RFQ commercial identity.");

            // Create Quote
            var quote = new Quote
            {
                QuoteNo = $"QT-{rfq.Rfqno}",
                Rfqid = rfq.Id,
                CustomerId = rfq.CustomerId,
                BusinessUnitId = rfq.BusinessUnitId,
                QuoteDate = DateTime.UtcNow,
                ValidUntil = DateTime.UtcNow.AddDays(30), // Default 30 days validity
                StatusId = await LifecycleStatusCatalog.ResolveIdAsync(
                    _context, rfq.BusinessUnitId, "Quote", "DRAFT"),
                CreatedBy = approvedBy,
                CreatedDate = DateTime.UtcNow,
                HeaderRemarks = rfq.HeaderRemarks,
                CurrencyId = rfq.Rfqitems.FirstOrDefault()?.CurrencyId,
                FinancialCalculationVersion = 2,
                QuoteItems = rfq.Rfqitems.Select(i => new QuoteItem
                {
                    RfqitemId = i.Id,
                    ProductId = i.ProductId,
                    ItemDescription = i.ProductShortDescription ?? i.ProductShortName ?? i.ItemText,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice ?? 0,
                    TotalAmount = i.Quantity * (i.UnitPrice ?? 0),
                    CreatedBy = approvedBy,
                    CreatedDate = DateTime.UtcNow
                }).ToList()
            };
            quote.InheritCommercialIdentity(rfq);

            quote.TotalAmount = quote.QuoteItems.Sum(i => i.TotalAmount);

            _context.Quotes.Add(quote);
            await _context.SaveChangesAsync();

            return quote.Id;
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var rfq = await _context.Rfqs
                .Include(r => r.Rfqitems)
                .FirstOrDefaultAsync(r => r.Id == id && r.BusinessUnitId == businessUnitId);

            if (rfq != null)
            {
                _context.Rfqitems.RemoveRange(rfq.Rfqitems);
                _context.Rfqs.Remove(rfq);
                await _context.SaveChangesAsync();
            }
        }


        public async Task<List<RFQTypeLookupDTO>> GetRFQTypeAsync()
        {
            return await _context.SetupMasters
                .Where(sm => sm.SetupType.Contains("RFQType") && sm.IsActive == true)
                .Select(sm => new RFQTypeLookupDTO
                {
                    Id = sm.SetupId,
                    RFQType = sm.SetupValue
                })
                .OrderBy(sm => sm.RFQType)
                .ToListAsync();
        }

        public async Task<RfqStatsDTO> GetRfqStatsAsync(long businessUnitId)
        {
            var rfqs = await _context.Rfqs
                .AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId)
                .ToListAsync();

            var now = DateTime.UtcNow;
            var sevenDaysLater = now.AddDays(7);

            return new RfqStatsDTO
            {
                TotalRfqs = rfqs.Count,
                DraftRfqs = rfqs.Count(r => r.RfqstatusId == 34),
                SubmittedRfqs = rfqs.Count(r => r.RfqstatusId == 35),
                ClosingSoonRfqs = rfqs.Count(r => r.BidClosingDate.HasValue && r.BidClosingDate.Value >= now && r.BidClosingDate.Value <= sevenDaysLater)
            };
        }
    }
}
