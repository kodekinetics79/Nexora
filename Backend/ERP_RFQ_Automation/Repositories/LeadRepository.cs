using ERP_RFQ_Automation.DTOs.AcceptedLeadDTOs;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.DTOs.LeadDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Repositories
{
    public class LeadRepository : ILeadRepository
    {
        private readonly ErpRfqAutomationContext _context;
        public LeadRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<(IEnumerable<LeadResponseDTO>, int TotalCount)> GetLeadListAsync(int pageNumber, int pageSize, long? id, string? rfqno, string? buyersName, string? leadSource, long businessUnitId, DateTime? startDate = null, DateTime? endDate = null, string? emailSource = null, string? clientemail = null)
        {
            var query = _context.Leads
                .AsNoTracking()
                .Include(l => l.BusinessUnit)
                .Include(l => l.EmailIngests)
                .Where(l => l.BusinessUnitId == businessUnitId)
                .Where(l => l.LeadStatusId == null);  // Added filter to show only leads with LeadStatusId = null

            // Apply filters
            if (id.HasValue)
                query = query.Where(l => l.Id == id.Value);
            if (!string.IsNullOrWhiteSpace(rfqno))
                query = query.Where(l => l.Rfqno != null && l.Rfqno.ToLower().Contains(rfqno.ToLower()));
            if (!string.IsNullOrWhiteSpace(buyersName))
                query = query.Where(l => l.BuyersName != null && l.BuyersName.ToLower().Contains(buyersName.ToLower()));
            if (!string.IsNullOrWhiteSpace(leadSource))
                query = query.Where(l => l.LeadSource.ToLower().Contains(leadSource.ToLower()));
            if (!string.IsNullOrWhiteSpace(emailSource))
                query = query.Where(l => l.EmailSource != null && l.EmailSource.ToLower().Contains(emailSource.ToLower()));
            if (!string.IsNullOrWhiteSpace(clientemail))
                query = query.Where(l => l.Clientemail != null && l.Clientemail.ToLower().Contains(clientemail.ToLower()));
            if (startDate.HasValue)
                query = query.Where(l => l.RecDate >= startDate.Value);
            if (endDate.HasValue)
                query = query.Where(l => l.RecDate <= endDate.Value);

            // Get total count before pagination
            var totalCount = await query.CountAsync();

            // Apply pagination and sorting (default: newest first)
            var leads = await query
                .OrderByDescending(l => l.CreatedDate)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Batch fetch attachments for the paged leads (merging Attachment table)
            var leadIds = leads.Select(l => l.Id).ToList();
            var attachments = await _context.Attachments
                .AsNoTracking()
                .Where(a => a.ParentType == "Lead" && leadIds.Contains(a.ParentId))
                .ToListAsync();

            var attachmentsGrouped = attachments.GroupBy(a => a.ParentId)
                .ToDictionary(g => g.Key, g => g.Select(att => new AttachmentResponseDTO
                {
                    Id = att.Id,
                    FileName = att.FileName,
                    FilePath = att.FilePath,
                    MimeType = att.MimeType,
                    FileSize = att.FileSize,
                    ContentType = att.ContentType,
                    CreatedOn = att.CreatedOn,
                    UploadedDate = att.UploadedDate
                }).ToList());

            // Batch load item counts for all leads in a single query
            var itemCounts = await _context.LeadItems
                .AsNoTracking()
                .Where(li => leadIds.Contains(li.LeadId))
                .GroupBy(li => li.LeadId)
                .Select(g => new { LeadId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.LeadId, x => x.Count);

            // Project to LeadResponseDTO (merging Lead, LeadItems, and Attachments)
            var leadDtos = leads.Select(l => new LeadResponseDTO
            {
                Id = l.Id,
                Rfqno = l.Rfqno,
                BuyersName = l.BuyersName,
                RecDate = l.RecDate,
                BidClosingDate = l.BidClosingDate,
                BiddingDecision = l.BiddingDecision,
                AcknowledgmentDate = l.AcknowledgmentDate,
                SubDate = l.SubDate,
                HeaderRemarks = l.HeaderRemarks,
                OpportunityNo = l.OpportunityNo,
                NoOfLineItems = l.NoOfLineItems,
                Rfqtype = l.Rfqtype,
                DurationAgreement = l.DurationAgreement,
                LeadSource = l.LeadSource,
                Aiconfidence = l.Aiconfidence,
                CreatedBy = l.CreatedBy,
                CreatedDate = l.CreatedDate,
                BusinessUnitId = l.BusinessUnitId,
                BusinessUnitName = l.BusinessUnit?.BusinessUnitName,
                EmailIngestsId = l.EmailIngestsId,
                ModifiedDate = l.ModifiedDate,
                EmailSource = l.EmailSource,
                Clientemail = l.Clientemail,
                LeadStatusId = l.LeadStatusId,
                ItemCount = itemCounts.TryGetValue(l.Id, out var count) ? count : 0,
                LeadItems = new List<LeadItemResponseDTO>(), // Empty list for list view
                Attachments = attachmentsGrouped.TryGetValue(l.Id, out var atts) ? atts : new List<AttachmentResponseDTO>()
            }).ToList();

            return (leadDtos, totalCount);
        }

        public async Task<IEnumerable<EmailConfigurationDropdownDTO>> GetActiveEmailConfigurationsAsync(long businessUnitId)
        {
            return await _context.EmailConfigurations
                .AsNoTracking()
                .Where(ec => ec.IsActive && ec.BusinessUnitId == businessUnitId && ec.Protocol == "IMAP")
                .Select(ec => new EmailConfigurationDropdownDTO
                {
                    Id = ec.Id,
                    BusinessUnitId = ec.BusinessUnitId,
                    EmailAddress = ec.EmailAddress
                })
                .ToListAsync();
        }

        // New implementation for Accept
        public async Task AcceptLeadAsync(long id, long businessUnitId)
        {
            var lead = await _context.Leads.FirstOrDefaultAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId);
            if (lead == null)
            {
                throw new Exception($"Lead with ID {id} not found in Business Unit {businessUnitId}.");
            }

            lead.LeadStatusId = 24;
            lead.ModifiedDate = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }

        // ARCH-01: convert an accepted lead into a real RFQ (with LeadId set), so the
        // end-to-end chain Lead -> RFQ -> Quote -> Order -> Shipment is continuous.
        // Status literals (24 = Lead Accepted, 34 = RFQ Draft) follow existing code
        // convention; resolving these via SetupMaster codes is tracked as ARCH-03.
        public async Task<(long RfqId, string Rfqno)> ConvertLeadToRfqAsync(long id, long businessUnitId, string createdBy)
        {
            var lead = await _context.Leads
                .Include(l => l.LeadItems)
                .FirstOrDefaultAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId);
            if (lead == null)
                throw new KeyNotFoundException($"Lead with ID {id} not found in Business Unit {businessUnitId}.");
            if (lead.LeadStatusId != 24)
                throw new InvalidOperationException("Only an accepted lead can be converted to an RFQ.");

            // Idempotency: never create a second RFQ for the same lead.
            var already = await _context.Rfqs
                .FirstOrDefaultAsync(r => r.LeadId == id && r.BusinessUnitId == businessUnitId);
            if (already != null)
                throw new InvalidOperationException($"This lead has already been converted to RFQ {already.Rfqno}.");

            // Reuse the lead's RFQ number when present and unique; otherwise derive one.
            var rfqno = !string.IsNullOrWhiteSpace(lead.Rfqno)
                ? lead.Rfqno!
                : $"RFQ-{id}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            if (await _context.Rfqs.AnyAsync(r => r.Rfqno == rfqno && r.BusinessUnitId == businessUnitId))
                rfqno = $"{rfqno}-{DateTime.UtcNow:yyyyMMddHHmmss}";

            var rfq = new Rfq
            {
                Rfqno = rfqno,
                BuyersName = lead.BuyersName,
                RecDate = lead.RecDate,
                BidClosingDate = lead.BidClosingDate,
                AcknowledgmentDate = lead.AcknowledgmentDate,
                SubDate = lead.SubDate,
                HeaderRemarks = lead.HeaderRemarks,
                OpportunityNo = lead.OpportunityNo,
                NoOfLineItems = lead.NoOfLineItems ?? lead.LeadItems.Count,
                Rfqtype = lead.Rfqtype,
                DurationAgreement = lead.DurationAgreement,
                LeadId = lead.Id,
                BusinessUnitId = businessUnitId,
                RfqstatusId = 34, // Draft
                CreatedBy = createdBy,
                CreatedDate = DateTime.UtcNow,
                Rfqitems = lead.LeadItems.Select(li => new Rfqitem
                {
                    CompanyRef = li.CompanyRef,
                    CustomerAccountPortalId = li.CustomerAccountPortalId,
                    CustomerRfqno = li.CustomerRfqno,
                    ItemMaterialCode = li.ItemMaterialCode,
                    LineItemNo = li.LineItemNo,
                    CommodityProduct = li.CommodityProduct,
                    ProductShortName = li.ProductShortName,
                    ProductShortDescription = li.ProductShortDescription,
                    Alternative = li.Alternative,
                    BuyerName = li.BuyerName,
                    Currency = li.Currency,
                    UnitOfMeasure = li.UnitOfMeasure,
                    UnitPrice = li.UnitPrice,
                    Quantity = li.Quantity,
                    StorageLocation = li.StorageLocation,
                    ManufacturerName = li.ManufacturerName,
                    ManufacturerPartNumber = li.ManufacturerPartNumber,
                    AlternateProductName = li.AlternateProductName,
                    AlternatePartNumber = li.AlternatePartNumber,
                    ItemText = li.ItemText,
                    MaterialPotext = li.MaterialPotext,
                    LeadTime = li.LeadTime,
                    ReceivedDate = li.ReceivedDate,
                    BidClosingDateLine = li.BidClosingDateLine,
                    Aiconfidence = li.Aiconfidence,
                    CreatedBy = createdBy,
                    CreatedDate = DateTime.UtcNow
                }).ToList()
            };

            using var tx = await _context.Database.BeginTransactionAsync();
            _context.Rfqs.Add(rfq);
            await _context.SaveChangesAsync();
            await tx.CommitAsync();
            return (rfq.Id, rfq.Rfqno);
        }

        public async Task<IEnumerable<RejectionReasonDTO>> GetLeadRejectionReasonsAsync()
        {
            return await _context.SetupMasters
                .AsNoTracking()
                .Where(s => s.SetupType == "LeadRejectedReason" && (s.IsActive == true || s.IsActive == null))
                .Select(s => new RejectionReasonDTO
                {
                    Id = s.SetupId,
                    Reason = s.SetupValue,
                    Description = s.Description
                })
                .ToListAsync();
        }

        // New implementation for Reject
        public async Task RejectLeadAsync(long id, long reasonId, long businessUnitId)
        {
            var lead = await _context.Leads.FirstOrDefaultAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId);
            if (lead == null)
            {
                throw new Exception($"Lead with ID {id} not found in Business Unit {businessUnitId}.");
            }

            lead.LeadStatusId = 25;
            lead.LeadRejectedReasonId = reasonId;
            lead.ModifiedDate = DateTime.UtcNow;
            await _context.SaveChangesAsync();
        }


        // Accepted Lead Sections 

        public async Task<(IEnumerable<AcceptedLeadResponseDTO>, int TotalCount)> GetAcceptedLeadsAsync(
      int pageNumber,
      int pageSize,
      long businessUnitId,
      long? assignedToId = null,
      string? searchTerm = null,
      DateTime? startDate = null,
      DateTime? endDate = null,
      bool excludeAssigned = false,
      bool onlyAssigned = false)
        {
            var query = _context.Leads
                .AsNoTracking()
                .Include(l => l.AssignToNavigation)
                .Where(l => l.BusinessUnitId == businessUnitId)
                .Where(l => l.LeadStatusId == 24) // Only Accepted
                .Where(l => !l.Rfqs.Any());  // Only show leads that have NOT yet been converted to an RFQ

            // Exclude assigned leads if requested (but only if we're not looking for a specific assignee)
            if (excludeAssigned && !assignedToId.HasValue)
                query = query.Where(l => l.AssignTo == null);

            // Only assigned leads if requested
            if (onlyAssigned)
                query = query.Where(l => l.AssignTo != null);

            // Filters...
            if (assignedToId.HasValue)
                query = query.Where(l => l.AssignTo == assignedToId.Value);

            if (startDate.HasValue)
                query = query.Where(l => l.RecDate >= startDate.Value);

            if (endDate.HasValue)
                query = query.Where(l => l.RecDate <= endDate.Value);

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var term = searchTerm.Trim().ToLower();
                query = query.Where(l =>
                    (l.Rfqno != null && l.Rfqno.ToLower().Contains(term)) ||
                    (l.BuyersName != null && l.BuyersName.ToLower().Contains(term)) ||
                    (l.EmailSource != null && l.EmailSource.ToLower().Contains(term)) ||
                    (l.Clientemail != null && l.Clientemail.ToLower().Contains(term)) ||
                    (l.AssignToNavigation != null &&
                     (l.AssignToNavigation.FirstName + " " + l.AssignToNavigation.LastName).ToLower().Contains(term))
                );
            }

            var totalCount = await query.CountAsync();

            var leads = await query
                .OrderByDescending(l => l.AssignOn ?? l.ModifiedDate ?? l.CreatedDate)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync();

            // Attachments batch loading (same as before)
            var leadIds = leads.Select(l => l.Id).ToList();
            var attachments = await _context.Attachments
                .AsNoTracking()
                .Where(a => a.ParentType == "Lead" && leadIds.Contains(a.ParentId))
                .ToListAsync();

            var attachmentsByLead = attachments
                .GroupBy(a => a.ParentId)
                .ToDictionary(g => g.Key, g => g.Select(a => new AttachmentResponseDTO
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    FilePath = a.FilePath,
                    MimeType = a.MimeType,
                    // ... other fields
                }).ToList());

            // Batch load item counts for all leads in a single query
            var itemCounts = await _context.LeadItems
                .AsNoTracking()
                .Where(li => leadIds.Contains(li.LeadId))
                .GroupBy(li => li.LeadId)
                .Select(g => new { LeadId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.LeadId, x => x.Count);

            var dtos = leads.Select(l => new AcceptedLeadResponseDTO
            {
                Id = l.Id,
                Rfqno = l.Rfqno,
                BuyersName = l.BuyersName,
                RecDate = l.RecDate,
                BidClosingDate = l.BidClosingDate,
                BiddingDecision = l.BiddingDecision,
                AcknowledgmentDate = l.AcknowledgmentDate,
                SubDate = l.SubDate,
                HeaderRemarks = l.HeaderRemarks,
                OpportunityNo = l.OpportunityNo,
                NoOfLineItems = l.NoOfLineItems,
                Rfqtype = l.Rfqtype,
                DurationAgreement = l.DurationAgreement,
                Aiconfidence = l.Aiconfidence,
                LeadSource = l.LeadSource,
                EmailSource = l.EmailSource,
                Clientemail = l.Clientemail,
                CreatedDate = l.CreatedDate,
                ModifiedDate = l.ModifiedDate,
                LeadStatusId = l.LeadStatusId,

                AssignedToId = l.AssignTo,
                AssignedToFullName = l.AssignToNavigation != null
                    ? $"{l.AssignToNavigation.FirstName} {l.AssignToNavigation.LastName}".Trim()
                    : "Unassigned",
                AssignedOn = l.AssignOn,
                AssignComment = l.AssignComment,

                // Optimized: Use pre-loaded count dictionary for O(1) lookup
                ItemCount = itemCounts.TryGetValue(l.Id, out var count) ? count : 0,
                LeadItems = new List<AcceptedLeadItemDTO>(), // Empty list for list view - items loaded in detail view

                Attachments = attachmentsByLead.TryGetValue(l.Id, out var atts) ? atts : new()
            }).ToList();

            return (dtos, totalCount);
        }

      public async Task AssignLeadAsync(long leadId, long assignedToUserId, long businessUnitId, string? comment = null)
        {
            var lead = await _context.Leads
                .Include(l => l.AssignToNavigation)
                .FirstOrDefaultAsync(l => l.Id == leadId && l.BusinessUnitId == businessUnitId);

            if (lead == null)
                throw new Exception($"Lead {leadId} not found in Business Unit {businessUnitId}");

            if (lead.LeadStatusId != 24)
                throw new Exception("Can only assign accepted leads (status = 24)");

            // Optional: validate user belongs to same business unit
            var userInSameBU = await _context.Users
                .AnyAsync(u => u.Id == assignedToUserId && u.Buid == businessUnitId);

            if (!userInSameBU)
                throw new Exception("Assigned user must belong to the same business unit");

            lead.AssignTo = assignedToUserId;
            lead.AssignOn = DateTime.UtcNow;           
            lead.AssignComment = comment;              
            lead.ModifiedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();
        }

        public async Task<IEnumerable<UserDropdownDTO>> GetUsersForAssignmentAsync(long businessUnitId)
        {
            return await _context.Users
                .AsNoTracking()
                .Where(u => u.Buid == businessUnitId && u.IsActive == true)
                .Select(u => new UserDropdownDTO
                {
                    Id = u.Id,
                    FullName = u.FirstName + " " + u.LastName
                })
                .OrderBy(u => u.FullName)
                .ToListAsync();
        }
        public async Task<AcceptedLeadResponseDTO?> GetAcceptedLeadByIdAsync(long id, long businessUnitId)
        {
            var lead = await _context.Leads
                .AsNoTracking()
                .Include(l => l.LeadItems)
                .Include(l => l.AssignToNavigation)
                .FirstOrDefaultAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId && l.LeadStatusId == 24);

            if (lead == null) return null;

            var attachments = await _context.Attachments
                .AsNoTracking()
                .Where(a => a.ParentType == "Lead" && a.ParentId == id)
                .Select(a => new AttachmentResponseDTO
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    FilePath = a.FilePath,
                    MimeType = a.MimeType,
                    FileSize = a.FileSize,
                    ContentType = a.ContentType,
                    CreatedOn = a.CreatedOn,
                    UploadedDate = a.UploadedDate
                })
                .ToListAsync();

            return new AcceptedLeadResponseDTO
            {
                Id = lead.Id,
                Rfqno = lead.Rfqno,
                BuyersName = lead.BuyersName,
                RecDate = lead.RecDate,
                BidClosingDate = lead.BidClosingDate,
                BiddingDecision = lead.BiddingDecision,
                AcknowledgmentDate = lead.AcknowledgmentDate,
                SubDate = lead.SubDate,
                HeaderRemarks = lead.HeaderRemarks,
                OpportunityNo = lead.OpportunityNo,
                NoOfLineItems = lead.NoOfLineItems,
                Rfqtype = lead.Rfqtype,
                DurationAgreement = lead.DurationAgreement,
                Aiconfidence = lead.Aiconfidence,
                LeadSource = lead.LeadSource,
                EmailSource = lead.EmailSource,
                Clientemail = lead.Clientemail,
                CreatedDate = lead.CreatedDate,
                ModifiedDate = lead.ModifiedDate,
                LeadStatusId = lead.LeadStatusId,
                AssignedToId = lead.AssignTo,
                AssignedToFullName = lead.AssignToNavigation != null
                    ? $"{lead.AssignToNavigation.FirstName} {lead.AssignToNavigation.LastName}".Trim()
                    : "Unassigned",
                AssignedOn = lead.AssignOn,
                AssignComment = lead.AssignComment,
                LeadItems = lead.LeadItems.Select(li => new AcceptedLeadItemDTO
                {
                    Id = li.Id,
                    CompanyRef = li.CompanyRef,
                    CustomerAccountPortalId = li.CustomerAccountPortalId,
                    CustomerRfqno = li.CustomerRfqno,
                    ItemMaterialCode = li.ItemMaterialCode,
                    CommodityProduct = li.CommodityProduct,
                    BuyerName = li.BuyerName,
                    LineItemNo = li.LineItemNo,
                    ProductShortName = li.ProductShortName,
                    Alternative = li.Alternative,
                    ProductShortDescription = li.ProductShortDescription,
                    Currency = li.Currency,
                    UnitOfMeasure = li.UnitOfMeasure,
                    UnitPrice = li.UnitPrice,
                    Quantity = li.Quantity,
                    StorageLocation = li.StorageLocation,
                    ManufacturerName = li.ManufacturerName,
                    ManufacturerPartNumber = li.ManufacturerPartNumber,
                    AlternateProductName = li.AlternateProductName,
                    AlternatePartNumber = li.AlternatePartNumber,
                    ItemText = li.ItemText,
                    MaterialPotext = li.MaterialPotext,
                    LeadTime = li.LeadTime,
                    ReceivedDate = li.ReceivedDate,
                    BidClosingDateLine = li.BidClosingDateLine,
                    Aiconfidence = li.Aiconfidence
                }).ToList(),
                Attachments = attachments
            };
        }

        public async Task<LeadResponseDTO?> GetLeadByIdAsync(long id, long businessUnitId)
        {
            var lead = await _context.Leads
                .AsNoTracking()
                .Include(l => l.BusinessUnit)
                .Include(l => l.LeadItems)
                .Include(l => l.AssignToNavigation)
                .FirstOrDefaultAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId);

            if (lead == null) return null;

            var attachments = await _context.Attachments
                .AsNoTracking()
                .Where(a => a.ParentType == "Lead" && a.ParentId == id)
                .Select(a => new AttachmentResponseDTO
                {
                    Id = a.Id,
                    FileName = a.FileName,
                    FilePath = a.FilePath,
                    MimeType = a.MimeType,
                    FileSize = a.FileSize,
                    ContentType = a.ContentType,
                    CreatedOn = a.CreatedOn,
                    UploadedDate = a.UploadedDate
                })
                .ToListAsync();

            return new LeadResponseDTO
            {
                Id = lead.Id,
                Rfqno = lead.Rfqno,
                BuyersName = lead.BuyersName,
                RecDate = lead.RecDate,
                BidClosingDate = lead.BidClosingDate,
                BiddingDecision = lead.BiddingDecision,
                AcknowledgmentDate = lead.AcknowledgmentDate,
                SubDate = lead.SubDate,
                OpportunityNo = lead.OpportunityNo,
                NoOfLineItems = lead.NoOfLineItems,
                Rfqtype = lead.Rfqtype,
                DurationAgreement = lead.DurationAgreement,
                LeadSource = lead.LeadSource,
                Aiconfidence = lead.Aiconfidence,
                CreatedBy = lead.CreatedBy,
                CreatedDate = lead.CreatedDate,
                BusinessUnitId = lead.BusinessUnitId,
                BusinessUnitName = lead.BusinessUnit?.BusinessUnitName,
                EmailIngestsId = lead.EmailIngestsId,
                ModifiedDate = lead.ModifiedDate,
                EmailSource = lead.EmailSource,
                Clientemail = lead.Clientemail,
                LeadStatusId = lead.LeadStatusId,
                
                // Assignment Info
                AssignedToId = lead.AssignTo,
                AssignedToFullName = lead.AssignToNavigation != null
                    ? $"{lead.AssignToNavigation.FirstName} {lead.AssignToNavigation.LastName}".Trim()
                    : null,
                AssignedOn = lead.AssignOn,
                AssignComment = lead.AssignComment,

                ItemCount = lead.LeadItems.Count,
                LeadItems = lead.LeadItems.Select(li => new LeadItemResponseDTO
                {
                    Id = li.Id,
                    CompanyRef = li.CompanyRef,
                    CustomerAccountPortalId = li.CustomerAccountPortalId,
                    CustomerRfqno = li.CustomerRfqno,
                    ItemMaterialCode = li.ItemMaterialCode,
                    CommodityProduct = li.CommodityProduct,
                    BuyerName = li.BuyerName,
                    LineItemNo = li.LineItemNo,
                    ProductShortName = li.ProductShortName,
                    Alternative = li.Alternative,
                    ProductShortDescription = li.ProductShortDescription,
                    Currency = li.Currency,
                    UnitOfMeasure = li.UnitOfMeasure,
                    UnitPrice = li.UnitPrice,
                    Quantity = li.Quantity,
                    StorageLocation = li.StorageLocation,
                    ManufacturerName = li.ManufacturerName,
                    ManufacturerPartNumber = li.ManufacturerPartNumber,
                    AlternateProductName = li.AlternateProductName,
                    AlternatePartNumber = li.AlternatePartNumber,
                    ItemText = li.ItemText,
                    MaterialPotext = li.MaterialPotext,
                    LeadTime = li.LeadTime,
                    ReceivedDate = li.ReceivedDate,
                    BidClosingDateLine = li.BidClosingDateLine,
                    Aiconfidence = li.Aiconfidence
                }).ToList(),
                Attachments = attachments
            };
        }

        public async Task<LeadStatsDTO> GetLeadStatsAsync(long businessUnitId)
        {
            var leads = await _context.Leads
                .AsNoTracking()
                .Where(l => l.BusinessUnitId == businessUnitId)
                .ToListAsync();

            var now = DateTime.UtcNow;
            var sevenDaysLater = now.AddDays(7);

            return new LeadStatsDTO
            {
                TotalActiveLeads = leads.Count(l => l.LeadStatusId == null),
                HighConfidenceLeads = leads.Count(l => l.Aiconfidence.HasValue && l.Aiconfidence.Value > 0.7m),
                ClosingSoonLeads = leads.Count(l => l.BidClosingDate.HasValue && l.BidClosingDate.Value >= now && l.BidClosingDate.Value <= sevenDaysLater && l.LeadStatusId == null),
                TotalLeadSources = leads.Select(l => l.LeadSource).Distinct().Count()
            };
        }

        // ==== Extraction review workbench ====

        // Marker prefixed to HeaderRemarks when an extraction persists as low-confidence.
        // Persisted format: "[NEEDS REVIEW] {reason} {originalRemark}" (see ExtractionWorker.PersistAsync).
        private const string NeedsReviewMarker = "[NEEDS REVIEW]";

        // Leads whose linked EmailIngest is flagged NeedsReview and that have not yet been
        // triaged (LeadStatusId == null). BU scoping is enforced by the global query filter;
        // the explicit BusinessUnitId predicate mirrors GetLeadListAsync for clarity.
        public async Task<(IEnumerable<LeadNeedsReviewItemDTO>, int TotalCount)> GetNeedsReviewLeadsAsync(int pageNumber, int pageSize, long businessUnitId, string? search = null)
        {
            var query = _context.Leads
                .AsNoTracking()
                .Include(l => l.EmailIngests)
                .Where(l => l.BusinessUnitId == businessUnitId)
                .Where(l => l.LeadStatusId == null)
                .Where(l => l.EmailIngests.ParseStatus == "NeedsReview");

            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim().ToLower();
                query = query.Where(l =>
                    (l.Rfqno != null && l.Rfqno.ToLower().Contains(term)) ||
                    (l.BuyersName != null && l.BuyersName.ToLower().Contains(term)));
            }

            var totalCount = await query.CountAsync();

            var leads = await query
                .OrderByDescending(l => l.RecDate)
                .Skip((pageNumber - 1) * pageSize)
                .Take(pageSize)
                .Select(l => new
                {
                    l.Id,
                    l.Rfqno,
                    l.BuyersName,
                    l.RecDate,
                    l.BidClosingDate,
                    l.LeadSource,
                    l.Aiconfidence,
                    l.HeaderRemarks,
                    ReceivedOn = (DateTime?)l.EmailIngests.CreatedOn,
                    ItemCount = l.LeadItems.Count
                })
                .ToListAsync();

            var dtos = leads.Select(l => new LeadNeedsReviewItemDTO
            {
                Id = l.Id,
                Rfqno = l.Rfqno,
                BuyersName = l.BuyersName,
                RecDate = l.RecDate,
                BidClosingDate = l.BidClosingDate,
                LeadSource = l.LeadSource,
                Aiconfidence = l.Aiconfidence,
                ItemCount = l.ItemCount,
                ReviewReason = ExtractReviewReason(l.HeaderRemarks),
                ReceivedOn = l.ReceivedOn
            }).ToList();

            return (dtos, totalCount);
        }

        // Persist reviewer corrections against a low-confidence lead and clear the review flag.
        // Loads the aggregate TRACKED (LeadItems + EmailIngests) so header/item edits, inserts and
        // deletes all flush in a single SaveChanges. Tenant ownership is enforced by the global
        // query filter; any BusinessUnitId in the payload is ignored by design.
        public async Task<LeadResponseDTO?> SubmitLeadReviewAsync(long id, long businessUnitId, LeadReviewSubmitDTO review)
        {
            var lead = await _context.Leads
                .Include(l => l.LeadItems)
                .Include(l => l.EmailIngests)
                .FirstOrDefaultAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId);

            if (lead == null) return null;

            var header = review.Header ?? new LeadReviewHeaderDTO();

            // Header edits: only apply provided (non-null) fields.
            if (header.Rfqno != null) lead.Rfqno = header.Rfqno;
            if (header.BuyersName != null) lead.BuyersName = header.BuyersName;
            if (header.BidClosingDate != null) lead.BidClosingDate = header.BidClosingDate;
            if (header.OpportunityNo != null) lead.OpportunityNo = header.OpportunityNo;

            // HeaderRemarks: a client-supplied value wins; otherwise strip the review marker
            // from the existing remark so the human note (if any) survives.
            lead.HeaderRemarks = header.HeaderRemarks ?? StripNeedsReviewPrefix(lead.HeaderRemarks);

            // Upsert items: match existing by Id, insert new (Id null/0), delete the rest.
            var items = review.Items ?? new List<LeadItemReviewDTO>();
            var keptIds = items.Where(i => i.Id.HasValue && i.Id.Value > 0)
                               .Select(i => i.Id!.Value)
                               .ToHashSet();

            var toRemove = lead.LeadItems.Where(li => !keptIds.Contains(li.Id)).ToList();
            if (toRemove.Count > 0)
                _context.LeadItems.RemoveRange(toRemove);

            foreach (var dto in items)
            {
                if (dto.Id.HasValue && dto.Id.Value > 0)
                {
                    var existing = lead.LeadItems.FirstOrDefault(li => li.Id == dto.Id.Value);
                    if (existing == null) continue; // stale/foreign id; ignore rather than trust it
                    ApplyItemFields(existing, dto);
                }
                else
                {
                    var created = new LeadItem { LeadId = lead.Id };
                    ApplyItemFields(created, dto);
                    lead.LeadItems.Add(created);
                }
            }

            // RemoveRange marks items Deleted but leaves them on the nav collection until save,
            // so exclude them when recomputing the resulting line-item count.
            lead.NoOfLineItems = lead.LeadItems.Count(li => !toRemove.Contains(li));

            // Clear the canonical NeedsReview flag regardless of action.
            if (lead.EmailIngests != null)
                lead.EmailIngests.ParseStatus = "Success";

            if (review.Action == "approve")
                lead.LeadStatusId = 24;

            lead.ModifiedDate = DateTime.UtcNow;

            await _context.SaveChangesAsync();

            // Reuse the canonical mapping for the response.
            return await GetLeadByIdAsync(id, businessUnitId);
        }

        // Only quantity is non-nullable on the model; a null quantity keeps the existing
        // value (update) or defaults to 0 (insert, via the model default).
        private static void ApplyItemFields(LeadItem item, LeadItemReviewDTO dto)
        {
            item.LineItemNo = dto.LineItemNo;
            item.ProductShortName = dto.ProductShortName;
            item.ProductShortDescription = dto.ProductShortDescription;
            item.CommodityProduct = dto.CommodityProduct;
            item.ItemMaterialCode = dto.ItemMaterialCode;
            item.Currency = dto.Currency;
            item.UnitOfMeasure = dto.UnitOfMeasure;
            item.UnitPrice = dto.UnitPrice;
            if (dto.Quantity.HasValue) item.Quantity = dto.Quantity.Value;
            item.ManufacturerName = dto.ManufacturerName;
            item.ManufacturerPartNumber = dto.ManufacturerPartNumber;
            item.AlternateProductName = dto.AlternateProductName;
            item.AlternatePartNumber = dto.AlternatePartNumber;
            item.ItemText = dto.ItemText;
            item.LeadTime = dto.LeadTime;
        }

        // Returns the review reason parsed from HeaderRemarks, or null when the lead is not
        // flagged. Persistence does not delimit the reason from the human remark, so the whole
        // text after the marker is returned; when nothing follows the marker the raw remark is used.
        private static string? ExtractReviewReason(string? headerRemarks)
        {
            if (string.IsNullOrWhiteSpace(headerRemarks)) return null;

            var trimmed = headerRemarks.TrimStart();
            if (!trimmed.StartsWith(NeedsReviewMarker, StringComparison.OrdinalIgnoreCase))
                return null;

            var afterMarker = trimmed.Substring(NeedsReviewMarker.Length).Trim();
            return string.IsNullOrWhiteSpace(afterMarker) ? trimmed : afterMarker;
        }

        // Removes the leading "[NEEDS REVIEW]" marker, keeping whatever human remark follows.
        private static string? StripNeedsReviewPrefix(string? headerRemarks)
        {
            if (string.IsNullOrWhiteSpace(headerRemarks)) return headerRemarks;

            var trimmed = headerRemarks.TrimStart();
            if (!trimmed.StartsWith(NeedsReviewMarker, StringComparison.OrdinalIgnoreCase))
                return headerRemarks;

            var stripped = trimmed.Substring(NeedsReviewMarker.Length).Trim();
            return stripped.Length == 0 ? null : stripped;
        }
    }
}