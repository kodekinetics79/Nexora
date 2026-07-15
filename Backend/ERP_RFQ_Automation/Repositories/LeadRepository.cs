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
    }
}