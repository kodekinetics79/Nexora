using ERP_RFQ_Automation.Deduplication;
using ERP_RFQ_Automation.DTOs.AcceptedLeadDTOs;
using ERP_RFQ_Automation.DTOs.Lead;
using ERP_RFQ_Automation.DTOs.LeadDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using System.Data;
using System.Text.Json;
using ERP_RFQ_Automation.Inventory.Commercial;
using ERP_RFQ_Automation.CommercialRouting;

namespace ERP_RFQ_Automation.Repositories
{
    public sealed class LeadReviewConflictException : Exception
    {
        public LeadReviewConflictException(string message) : base(message) { }
    }

    public sealed class LeadReviewValidationException : Exception
    {
        public LeadReviewValidationException(string message) : base(message) { }
    }

    public class LeadRepository : ILeadRepository
    {
        /// <summary>How many machine client proposals the LIST projection carries per row.</summary>
        private const int ListCandidateCount = 3;

        /// <summary>How many the DETAIL projection and the resolve dialog carry.</summary>
        private const int DetailCandidateCount = 5;

        private readonly ErpRfqAutomationContext _context;
        private readonly ISlaPolicyReader _slaPolicy;
        private readonly ILeadDuplicateDetector? _duplicateDetector;
        private readonly ILogger<LeadRepository>? _logger;
        private readonly ERP_RFQ_Automation.Metrics.IMetricRecorder? _metrics;
        private readonly ICommercialLineResolutionApplicationService? _lineResolution;
        private readonly ERP_RFQ_Automation.CustomerResolution.ICustomerAliasLearner? _aliasLearner;

        // Optional dependencies keep existing constructions (tests, pre-wiring DI)
        // compiling and running: duplicate detection / metrics / alias learning
        // degrade to no-ops, the SLA reader falls back to the flat default threshold.
        public LeadRepository(
            ErpRfqAutomationContext context,
            ISlaPolicyReader? slaPolicy = null,
            ILeadDuplicateDetector? duplicateDetector = null,
            ILogger<LeadRepository>? logger = null,
            ERP_RFQ_Automation.Metrics.IMetricRecorder? metrics = null,
            ICommercialLineResolutionApplicationService? lineResolution = null,
            ERP_RFQ_Automation.CustomerResolution.ICustomerAliasLearner? aliasLearner = null)
        {
            _context = context;
            _slaPolicy = slaPolicy ?? new DefaultSlaPolicyReader();
            _duplicateDetector = duplicateDetector;
            _logger = logger;
            _metrics = metrics;
            _lineResolution = lineResolution;
            _aliasLearner = aliasLearner;
        }

        /// <summary>
        /// The ranked client proposals the machine persisted for this lead, newest pass only.
        /// Read-only: the resolve dialog shows them with their reasons, and confirming one
        /// goes through the review endpoint like any other human decision.
        /// </summary>
        public async Task<List<ClientCandidateDTO>> GetClientCandidatesAsync(long id, long businessUnitId)
        {
            return await (
                from candidate in _context.Set<ERP_RFQ_Automation.CustomerResolution.LeadCustomerMatchCandidate>().AsNoTracking()
                join customer in _context.Customers.AsNoTracking()
                    on new { Buid = (long?)candidate.BusinessUnitId, Id = candidate.CustomerId }
                    equals new { Buid = customer.Buid, Id = customer.Id }
                where candidate.BusinessUnitId == businessUnitId && candidate.LeadId == id
                      && candidate.Rank <= DetailCandidateCount
                orderby candidate.Rank
                select new ClientCandidateDTO
                {
                    Rank = candidate.Rank,
                    CustomerId = candidate.CustomerId,
                    CustomerName = customer.Name,
                    Confidence = candidate.Confidence,
                    ReasonCode = candidate.ReasonCode,
                    Explanation = candidate.Explanation
                }).ToListAsync();
        }

        public async Task<(IEnumerable<LeadResponseDTO>, int TotalCount)> GetLeadListAsync(int pageNumber, int pageSize, long? id, string? rfqno, string? buyersName, string? leadSource, long businessUnitId, DateTime? startDate = null, DateTime? endDate = null, string? emailSource = null, string? clientemail = null, string? view = null)
        {
            var query = _context.Leads
                .AsNoTracking()
                .Include(l => l.BusinessUnit)
                .Include(l => l.EmailIngests)
                .Where(l => l.BusinessUnitId == businessUnitId);

            if (!string.Equals(view, "revisions", StringComparison.OrdinalIgnoreCase))
                query = query.Where(l => l.LeadStatusId == null);

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
            if (string.Equals(view, "duplicates", StringComparison.OrdinalIgnoreCase))
                query = query.Where(l => l.DuplicateStatus == "suspected" || l.DuplicateStatus == "confirmed");
            else if (string.Equals(view, "revisions", StringComparison.OrdinalIgnoreCase))
                query = query.Where(l => l.CurrentRevisionNumber > 1);
            else if (string.Equals(view, "ready-for-rfq", StringComparison.OrdinalIgnoreCase))
                query = query.Where(l => l.CommercialFactsVerified && !l.RequiresCommercialReview
                    && l.CustomerId != null && l.BidClosingDate != null && l.LeadItems.Any()
                    && !l.Rfqs.Any());

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

            // Ingestion audit (owner requirement): earliest source received_on per
            // paged lead — the authoritative "when did this lead enter Nexora".
            var earliestReceivedOn = await ERP_RFQ_Automation.LeadIdentity.LeadIngestionAudit
                .EarliestSourceReceivedOnAsync(_context, businessUnitId, leadIds);

            // CLIENT COLUMN: the list projection has always carried CustomerMatchStatus and
            // never CustomerName, which is exactly why a rep looking at the leads list could
            // not tell which client company a lead came from. Both the resolved name and the
            // top machine proposals are batch-loaded here — two queries for the whole page,
            // never one per row — so the list can render a client for EVERY lead: the name,
            // the suggestion, or an explicit "unknown", but never an empty cell.
            var linkedCustomerIds = leads.Where(l => l.CustomerId.HasValue)
                .Select(l => l.CustomerId!.Value).Distinct().ToList();
            var customerNames = linkedCustomerIds.Count == 0
                ? new Dictionary<long, string>()
                : await _context.Customers.AsNoTracking()
                    .Where(c => c.Buid == businessUnitId && linkedCustomerIds.Contains(c.Id))
                    .Select(c => new { c.Id, c.Name })
                    .ToDictionaryAsync(c => c.Id, c => c.Name);

            var candidateRows = await (
                from candidate in _context.Set<ERP_RFQ_Automation.CustomerResolution.LeadCustomerMatchCandidate>().AsNoTracking()
                join customer in _context.Customers.AsNoTracking()
                    on new { Buid = (long?)candidate.BusinessUnitId, Id = candidate.CustomerId }
                    equals new { Buid = customer.Buid, Id = customer.Id }
                where candidate.BusinessUnitId == businessUnitId && leadIds.Contains(candidate.LeadId)
                      && candidate.Rank <= ListCandidateCount
                orderby candidate.LeadId, candidate.Rank
                select new { candidate.LeadId, candidate.Rank, candidate.CustomerId, customer.Name, candidate.Confidence, candidate.ReasonCode, candidate.Explanation })
                .ToListAsync();
            var candidatesByLead = candidateRows
                .GroupBy(row => row.LeadId)
                .ToDictionary(group => group.Key, group => group
                    .OrderBy(row => row.Rank)
                    .Select(row => new ClientCandidateDTO
                    {
                        Rank = row.Rank,
                        CustomerId = row.CustomerId,
                        CustomerName = row.Name,
                        Confidence = row.Confidence,
                        ReasonCode = row.ReasonCode,
                        Explanation = row.Explanation
                    }).ToList());

            // Project to LeadResponseDTO (merging Lead, LeadItems, and Attachments)
            var leadDtos = leads.Select(l =>
            {
                // Audit fairness: occurrence-derived ingestion timestamp with the
                // documented CreatedDate fallback for manual/legacy leads; a lead
                // ingested after its due date is flagged, never silently normal.
                var ingestedOn = LeadIdentity.LeadIngestionAudit.ResolveIngestionTimestamp(
                    earliestReceivedOn.TryGetValue(l.Id, out var receivedOn) ? receivedOn : null,
                    l.CreatedDate);
                return new LeadResponseDTO
                {
                    Id = l.Id,
                    CommercialCaseId = l.CommercialCaseId,
                    CommercialCaseReference = l.CommercialCaseReference,
                    CustomerId = l.CustomerId,
                    ContactId = l.ContactId,
                    CustomerName = l.CustomerId.HasValue && customerNames.TryGetValue(l.CustomerId.Value, out var name)
                        ? name
                        : null,
                    CustomerMatchStatus = l.CustomerMatchStatus,
                    CustomerMatchReasonCode = l.CustomerMatchReasonCode,
                    CustomerMatchConfidence = l.CustomerMatchConfidence,
                    CustomerMatchExplanation = l.CustomerMatchExplanation,
                    CustomerCompanyNameExtracted = l.CustomerCompanyNameExtracted,
                    CustomerCompanyEvidence = l.CustomerCompanyEvidence,
                    CustomerCompanyRegistrationId = l.CustomerCompanyRegistrationId,
                    CustomerBuyerEmailExtracted = l.CustomerBuyerEmailExtracted,
                    CustomerPortalNameExtracted = l.CustomerPortalNameExtracted,
                    SupplierNameOnDocument = l.SupplierNameOnDocument,
                    SupplierAccountRefOnDocument = l.SupplierAccountRefOnDocument,
                    ClientCandidates = candidatesByLead.TryGetValue(l.Id, out var proposals)
                        ? proposals
                        : new List<ClientCandidateDTO>(),
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
                    IngestedAtUtc = l.IngestedAtUtc,
                    IngestedOn = ingestedOn,
                    LateIngested = LeadIdentity.LeadIngestionAudit.IsLateIngested(
                        ingestedOn, l.BidClosingDate, l.SubDate),
                    BusinessUnitId = l.BusinessUnitId,
                    BusinessUnitName = l.BusinessUnit?.BusinessUnitName,
                    EmailIngestsId = l.EmailIngestsId,
                    ModifiedDate = l.ModifiedDate,
                    EmailSource = l.EmailSource,
                    Clientemail = l.Clientemail,
                    LeadStatusId = l.LeadStatusId,
                    LifecycleVersion = l.LifecycleVersion,
                    ReviewVersion = l.ReviewVersion,
                    RequiresCommercialReview = l.RequiresCommercialReview,
                    CommercialFactsVerified = l.CommercialFactsVerified,
                    InquiryType = l.InquiryType, // WP-BOQ: service/mixed list badge
                    DuplicateStatus = l.DuplicateStatus,
                    DuplicateOfLeadId = l.DuplicateOfLeadId,
                    ItemCount = itemCounts.TryGetValue(l.Id, out var count) ? count : 0,
                    LeadItems = new List<LeadItemResponseDTO>(), // Empty list for list view
                    Attachments = attachmentsGrouped.TryGetValue(l.Id, out var atts) ? atts : new List<AttachmentResponseDTO>()
                };
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
            var exists = await _context.Leads.AnyAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId);
            if (!exists) throw new KeyNotFoundException($"Lead with ID {id} not found in Business Unit {businessUnitId}.");
            throw new InvalidOperationException(
                "Lead acceptance has moved to the governed lifecycle. Advance the lead to UNDER_REVIEW, then transition it to QUALIFIED.");
        }

        // ARCH-01: convert an accepted lead into a real RFQ (with LeadId set), so the
        // end-to-end chain Lead -> RFQ -> Quote -> Order -> Shipment is continuous.
        // Status literals (24 = Lead Accepted, 34 = RFQ Draft) follow existing code
        // convention; resolving these via SetupMaster codes is tracked as ARCH-03.
        public async Task<(long RfqId, string Rfqno)> ConvertLeadToRfqAsync(long id, long businessUnitId, string createdBy)
        {
            if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
            if (string.IsNullOrWhiteSpace(createdBy)) throw new ArgumentException("Authenticated actor is required.", nameof(createdBy));

            var strategy = _context.Database.CreateExecutionStrategy();
            return await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction = await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable);
                var lead = await _context.Leads
                    .Include(l => l.LeadItems)
                    .Include(l => l.LeadStatus)
                    .FirstOrDefaultAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId)
                    ?? throw new KeyNotFoundException($"Lead with ID {id} not found in Business Unit {businessUnitId}.");

                var lifecycleCode = LifecyclePolicy.Canonicalize("Lead", lead.LeadStatus?.SetupCode, lead.LeadStatus?.SetupValue);
                var already = await _context.Rfqs
                    .FirstOrDefaultAsync(r => r.LeadId == id && r.BusinessUnitId == businessUnitId);
                if (already != null && lifecycleCode == "CONVERTED_TO_RFQ")
                {
                    already.InheritCommercialIdentity(lead);
                    await _context.SaveChangesAsync();
                    if (_lineResolution is not null)
                    {
                        await _lineResolution.ResolveLeadAsync(businessUnitId, lead.Id, 10);
                        await _lineResolution.LinkRfqAsync(businessUnitId, lead.Id, already.Id);
                    }
                    await transaction.CommitAsync();
                    return (already.Id, already.Rfqno);
                }
                if (lifecycleCode != "QUALIFIED")
                    throw new InvalidOperationException("Only a qualified lead can be converted to an RFQ.");
                if (lead.DuplicateStatus is "suspected" or "confirmed")
                    throw new InvalidOperationException(
                        $"This lead is flagged as a possible duplicate of lead #{lead.DuplicateOfLeadId}; resolve the duplicate flag first.");
                if (lead.RequiresCommercialReview && !lead.CommercialFactsVerified)
                    throw new InvalidOperationException(
                        "AI-extracted commercial facts must be approved in extraction review before RFQ conversion.");
                if (!lead.CustomerId.HasValue)
                    throw new InvalidOperationException("Resolve the lead customer before creating an RFQ.");

                if (_lineResolution is not null)
                    await _lineResolution.ResolveLeadAsync(businessUnitId, lead.Id, 10);

                var rfq = already ?? await CreateRfqFromLeadAsync(lead, businessUnitId, createdBy);
                if (already == null)
                {
                    _context.Rfqs.Add(rfq);
                    await _context.SaveChangesAsync();
                }
                else
                {
                    rfq.InheritCommercialIdentity(lead);
                    await _context.SaveChangesAsync();
                }

                if (_lineResolution is not null)
                    await _lineResolution.LinkRfqAsync(businessUnitId, lead.Id, rfq.Id);

                await new LifecycleApplicationService(_context).TransitionLeadInCurrentTransactionAsync(
                    lead.BusinessUnitId, lead.Id, new LifecycleActor(createdBy.Trim(), "AuthenticatedUser"),
                    new LifecycleTransitionCommand("CONVERTED_TO_RFQ", lead.LifecycleVersion, null, null,
                        "Api", $"conversion-{lead.Id}", $"rfq-{rfq.Id}",
                        $"lead-conversion:{lead.BusinessUnitId}:{lead.Id}"), false, default);
                await transaction.CommitAsync();
                return (rfq.Id, rfq.Rfqno);
            });
        }

        private async Task<Rfq> CreateRfqFromLeadAsync(Lead lead, long businessUnitId, string createdBy)
        {
            var rfqno = !string.IsNullOrWhiteSpace(lead.Rfqno)
                ? lead.Rfqno
                : $"RFQ-{lead.Id}-{DateTime.UtcNow:yyyyMMddHHmmss}";
            if (await _context.Rfqs.AnyAsync(r => r.Rfqno == rfqno && r.BusinessUnitId == businessUnitId))
                rfqno = $"{rfqno}-{DateTime.UtcNow:yyyyMMddHHmmss}";

            var now = DateTime.UtcNow;
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
                RfqstatusId = await LifecycleStatusCatalog.ResolveIdAsync(_context, businessUnitId, "Rfq", "DRAFT"),
                CreatedBy = createdBy.Trim(),
                CreatedDate = now,
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
                    CreatedBy = createdBy.Trim(),
                    CreatedDate = now
                }).ToList()
            };
            rfq.InheritCommercialIdentity(lead);
            return rfq;
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
            var exists = await _context.Leads.AnyAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId);
            if (!exists) throw new KeyNotFoundException($"Lead with ID {id} not found in Business Unit {businessUnitId}.");
            throw new InvalidOperationException(
                "Lead rejection has moved to the governed lifecycle. Use a DISQUALIFIED or CANCELLED transition with a reason code.");
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
                .Where(l => l.LeadStatus != null &&
                    (l.LeadStatus.SetupCode == "QUALIFIED" || l.LeadStatus.SetupValue == "Accepted" || l.LeadStatus.SetupValue == "Qualified"))
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

            // WP-A1 unassigned-aging: one policy read per request (SLA engine will
            // supply the real reader; default is a flat 2h).
            var unassignedThresholdHours = await _slaPolicy.GetUnassignedHoursAsync(businessUnitId);
            var nowUtc = DateTime.UtcNow;

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

                // WP-A1 unassigned-aging (rule: accepted + unassigned + sitting
                // longer than the tenant's unassigned-hours threshold).
                UnassignedHours = l.AssignTo == null
                    ? (int)Math.Max(0, (nowUtc - (l.ModifiedDate ?? l.CreatedDate)).TotalHours)
                    : null,
                IsUnassignedOverdue = l.LeadStatus != null &&
                    (l.LeadStatus.SetupCode == "QUALIFIED" || l.LeadStatus.SetupValue == "Accepted" || l.LeadStatus.SetupValue == "Qualified")
                    && l.AssignTo == null
                    && (nowUtc - (l.ModifiedDate ?? l.CreatedDate)).TotalHours > unassignedThresholdHours,

                // WP-BOQ: service/mixed list badge
                InquiryType = l.InquiryType,

                // WP-A3 duplicate flag (list badge support)
                DuplicateStatus = l.DuplicateStatus,
                DuplicateOfLeadId = l.DuplicateOfLeadId,

                // Optimized: Use pre-loaded count dictionary for O(1) lookup
                ItemCount = itemCounts.TryGetValue(l.Id, out var count) ? count : 0,
                LeadItems = new List<AcceptedLeadItemDTO>(), // Empty list for list view - items loaded in detail view

                Attachments = attachmentsByLead.TryGetValue(l.Id, out var atts) ? atts : new()
            }).ToList();

            return (dtos, totalCount);
        }

        // WP-A3: resolve a duplicate flag. "not_duplicate" clears the block (the
        // pair link is kept for audit); "confirm" keeps the lead blocked.
        public async Task<LeadResponseDTO?> ResolveDuplicateAsync(long id, long businessUnitId, string action, string resolvedBy)
        {
            var normalized = action?.Trim().ToLowerInvariant();
            if (normalized != "not_duplicate" && normalized != "confirm")
                throw new ArgumentException("Action must be \"not_duplicate\" or \"confirm\".");

            var lead = await _context.Leads
                .FirstOrDefaultAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId);
            if (lead == null) return null;

            if (lead.DuplicateStatus == null)
                throw new InvalidOperationException("This lead is not flagged as a duplicate.");

            lead.DuplicateStatus = normalized == "confirm" ? "confirmed" : "not_duplicate";
            lead.DuplicateResolvedBy = resolvedBy;
            lead.ModifiedDate = DateTime.UtcNow;
            await _context.SaveChangesAsync();

            return await GetLeadByIdAsync(id, businessUnitId);
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
                .Include(l => l.LeadStatus)
                .FirstOrDefaultAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId && l.LeadStatus != null &&
                    (l.LeadStatus.SetupCode == "QUALIFIED" || l.LeadStatus.SetupValue == "Accepted" || l.LeadStatus.SetupValue == "Qualified"));

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
                CommercialCaseId = lead.CommercialCaseId,
                CommercialCaseReference = lead.CommercialCaseReference,
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
                InquiryType = lead.InquiryType, // WP-BOQ: service/mixed badge
                DuplicateStatus = lead.DuplicateStatus,
                DuplicateOfLeadId = lead.DuplicateOfLeadId,
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

            var customerName = lead.CustomerId.HasValue
                ? await _context.Customers.AsNoTracking().Where(x => x.Buid == businessUnitId && x.Id == lead.CustomerId.Value)
                    .Select(x => x.Name).SingleOrDefaultAsync()
                : null;
            var accountOwnerName = lead.CustomerId.HasValue
                ? await (from ownership in _context.Set<CustomerOwnership>().AsNoTracking()
                         join owner in _context.Users.AsNoTracking() on ownership.PrimaryUserId equals owner.Id
                         where ownership.BusinessUnitId == businessUnitId && ownership.CustomerId == lead.CustomerId.Value &&
                               ownership.IsActive && ownership.EffectiveTo == null && owner.Buid == businessUnitId
                         orderby ownership.Priority descending, ownership.EffectiveFrom descending
                         select (owner.FirstName + " " + owner.LastName).Trim()).FirstOrDefaultAsync()
                : null;

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

            // Ingestion audit (owner requirement): earliest source received_on for
            // this lead, CreatedDate fallback — same rule as the list view.
            var earliestReceivedOn = await LeadIdentity.LeadIngestionAudit
                .EarliestSourceReceivedOnAsync(_context, businessUnitId, new[] { lead.Id });
            var ingestedOn = LeadIdentity.LeadIngestionAudit.ResolveIngestionTimestamp(
                earliestReceivedOn.TryGetValue(lead.Id, out var receivedOn) ? receivedOn : null,
                lead.CreatedDate);
            var detailCandidates = await GetClientCandidatesAsync(id, businessUnitId);

            return new LeadResponseDTO
            {
                Id = lead.Id,
                CommercialCaseId = lead.CommercialCaseId,
                CommercialCaseReference = lead.CommercialCaseReference,
                CustomerId = lead.CustomerId,
                ContactId = lead.ContactId,
                CustomerName = customerName,
                AccountOwnerName = accountOwnerName,
                CustomerMatchStatus = lead.CustomerMatchStatus,
                CustomerMatchReasonCode = lead.CustomerMatchReasonCode,
                CustomerMatchConfidence = lead.CustomerMatchConfidence,
                CustomerMatchExplanation = lead.CustomerMatchExplanation,
                CustomerCompanyNameExtracted = lead.CustomerCompanyNameExtracted,
                CustomerCompanyEvidence = lead.CustomerCompanyEvidence,
                CustomerCompanyRegistrationId = lead.CustomerCompanyRegistrationId,
                CustomerBuyerEmailExtracted = lead.CustomerBuyerEmailExtracted,
                CustomerPortalNameExtracted = lead.CustomerPortalNameExtracted,
                SupplierNameOnDocument = lead.SupplierNameOnDocument,
                SupplierAccountRefOnDocument = lead.SupplierAccountRefOnDocument,
                ClientCandidates = detailCandidates,
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
                LifecycleVersion = lead.LifecycleVersion,
                ReviewVersion = lead.ReviewVersion,
                RequiresCommercialReview = lead.RequiresCommercialReview,
                CommercialFactsVerified = lead.CommercialFactsVerified,
                CurrentRevisionNumber = lead.CurrentRevisionNumber,
                IngestedAtUtc = lead.IngestedAtUtc,

                // Ingestion audit (owner requirement: audit fairness)
                IngestedOn = ingestedOn,
                LateIngested = LeadIdentity.LeadIngestionAudit.IsLateIngested(
                    ingestedOn, lead.BidClosingDate, lead.SubDate),

                // WP-BOQ: service/mixed badge
                InquiryType = lead.InquiryType,

                // WP-A3 duplicate flag
                DuplicateStatus = lead.DuplicateStatus,
                DuplicateOfLeadId = lead.DuplicateOfLeadId,
                DuplicateResolvedBy = lead.DuplicateResolvedBy,

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
                    Aiconfidence = li.Aiconfidence,
                    // Verbatim unrecognized customer-document columns (jsonb -> dict);
                    // tolerant parse returns null for absent/malformed payloads.
                    ExtraFields = ExtraFieldsJson.Deserialize(li.ExtraFields)
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
                .Where(l => l.EmailIngests == null || l.EmailIngests.ParseStatus == "NeedsReview");

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
                    ReceivedOn = l.EmailIngests != null ? (DateTime?)l.EmailIngests.CreatedOn : l.CreatedDate,
                    ItemCount = l.LeadItems.Count,
                    l.ReviewVersion
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
                ReceivedOn = l.ReceivedOn,
                ReviewVersion = l.ReviewVersion
            }).ToList();

            return (dtos, totalCount);
        }

        // Persist reviewer corrections against a low-confidence lead; only approval clears the review flag.
        // Loads the aggregate TRACKED (LeadItems + EmailIngests) so header/item edits, inserts and
        // deletes all flush in a single SaveChanges. Tenant ownership is enforced by the global
        // query filter; any BusinessUnitId in the payload is ignored by design.
        public async Task<LeadResponseDTO?> SubmitLeadReviewAsync(
            long id, long businessUnitId, LeadReviewSubmitDTO review, string reviewedBy = "system")
        {
            var lead = await _context.Leads
                .Include(l => l.LeadItems)
                .Include(l => l.EmailIngests)
                .FirstOrDefaultAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId);

            if (lead == null) return null;

            var action = review.Action?.Trim().ToLowerInvariant();
            if (action is not ("save" or "approve"))
                throw new LeadReviewValidationException("Action must be 'save' or 'approve'.");
            if (string.IsNullOrWhiteSpace(reviewedBy))
                throw new LeadReviewValidationException("Reviewer identity is required.");
            if (!review.ExpectedVersion.HasValue)
                throw new LeadReviewValidationException("Expected review version is required.");
            if (review.ExpectedVersion.Value != lead.ReviewVersion)
                throw new LeadReviewConflictException(
                    $"Review version {review.ExpectedVersion} is stale; current version is {lead.ReviewVersion}.");
            // The needs-review queue (GetNeedsReviewLeadsAsync, above) lists BOTH email-door
            // leads flagged NeedsReview AND upload-door leads, which have no EmailIngest at
            // all. This gate used to require an EmailIngest, so every upload-door lead was
            // offered for review and then refused at submit: the approval path was closed
            // for them, and with it the ONLY source of measured correction evidence the
            // product has. An upload-door lead has no ParseStatus to read, so its review
            // state is the lead's own — untriaged, unverified — plus the same authoritative
            // source-document evidence approval already demands.
            var awaitingReview = lead.EmailIngests != null
                ? string.Equals(lead.EmailIngests.ParseStatus, "NeedsReview", StringComparison.OrdinalIgnoreCase)
                : lead.LeadStatusId == null
                  && !lead.CommercialFactsVerified
                  && (await SourceOccurrenceIdsAsync(lead.Id, businessUnitId)).Count > 0;
            if (!awaitingReview)
                throw new LeadReviewConflictException("This lead is no longer awaiting extraction review.");

            var items = review.Items ?? new List<LeadItemReviewDTO>();
            ValidateReviewItems(items, action);
            var submittedIds = items.Where(i => i.Id is > 0).Select(i => i.Id!.Value).ToArray();
            if (submittedIds.Length != submittedIds.Distinct().Count())
                throw new LeadReviewValidationException("A line item cannot be submitted more than once.");
            var existingIds = lead.LeadItems.Select(i => i.Id).ToHashSet();
            var staleIds = submittedIds.Where(idValue => !existingIds.Contains(idValue)).ToArray();
            if (staleIds.Length > 0)
                throw new LeadReviewConflictException(
                    $"Line item(s) {string.Join(", ", staleIds)} are stale or do not belong to this lead.");
            if (action == "approve" && string.IsNullOrWhiteSpace(review.Reason))
                throw new LeadReviewValidationException("An approval reason is required.");
            if (action == "approve")
                await EnsureApprovalEvidenceAsync(lead.Id, businessUnitId);

            var beforeJson = SerializeReviewSnapshot(lead);
            var fromVersion = lead.ReviewVersion;

            var header = review.Header ?? new LeadReviewHeaderDTO();
            long? previousCustomerId = null;
            var shouldLearnIdentity = false;

            if (header.ContactId.HasValue && !header.CustomerId.HasValue && !lead.CustomerId.HasValue)
                throw new LeadReviewValidationException("A customer is required when selecting a contact.");
            if (header.CustomerId.HasValue)
            {
                var customerExists = await _context.Customers.AsNoTracking().AnyAsync(customer =>
                    customer.Id == header.CustomerId.Value && customer.Buid == businessUnitId && customer.IsActive != false);
                if (!customerExists)
                    throw new LeadReviewValidationException("The selected customer was not found in this tenant.");

                if (header.ContactId.HasValue)
                {
                    var contactExists = await _context.Contacts.AsNoTracking().AnyAsync(contact =>
                        contact.Id == header.ContactId.Value && contact.CustomerId == header.CustomerId.Value
                        && contact.IsActive != false);
                    if (!contactExists)
                        throw new LeadReviewValidationException("The selected contact does not belong to the selected customer.");
                }

                // LEARNING INTENT is recorded here, alongside the human decision that
                // creates it; the write happens after the audit row exists (below), so the
                // learned identity edge can point at the exact before/after image that
                // justifies it.
                previousCustomerId = lead.CustomerId;
                lead.ResolveCommercialIdentity(
                    header.CustomerId.Value,
                    header.ContactId,
                    header.ContactId.HasValue ? "CONFIRMED" : "CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED");
                // P6 approval gate: only an explicit "approve" carrying an explicitly chosen
                // customer teaches. A machine AUTO_MATCHED result must NEVER become an
                // alias — that is the path by which one machine mistake would bootstrap
                // itself into authoritative knowledge.
                shouldLearnIdentity = action == "approve";
            }

            // WP-B4 passive metric (hook b): capture which fields the reviewer
            // actually changed vs. the stored values, BEFORE the edits are applied.
            var headerChanged = new List<string>();
            if (header.Rfqno != null && header.Rfqno != lead.Rfqno) headerChanged.Add("rfqno");
            if (header.BuyersName != null && header.BuyersName != lead.BuyersName) headerChanged.Add("buyersName");
            if (header.BidClosingDate != null && header.BidClosingDate != lead.BidClosingDate) headerChanged.Add("bidClosingDate");
            if (header.OpportunityNo != null && header.OpportunityNo != lead.OpportunityNo) headerChanged.Add("opportunityNo");
            if (header.HeaderRemarks != null && header.HeaderRemarks != lead.HeaderRemarks) headerChanged.Add("headerRemarks");
            if (header.CustomerId.HasValue && header.CustomerId != lead.CustomerId) headerChanged.Add("customerId");
            if (header.ContactId.HasValue && header.ContactId != lead.ContactId) headerChanged.Add("contactId");
            var itemFieldChanges = new Dictionary<string, int>();
            var itemsChanged = 0;
            var itemsAdded = 0;

            // Header edits: only apply provided (non-null) fields.
            if (header.Rfqno != null) lead.Rfqno = header.Rfqno;
            if (header.BuyersName != null) lead.BuyersName = header.BuyersName;
            if (header.BidClosingDate != null) lead.BidClosingDate = header.BidClosingDate;
            if (header.OpportunityNo != null) lead.OpportunityNo = header.OpportunityNo;

            // HeaderRemarks: a client-supplied value wins; otherwise strip the review marker
            // from the existing remark so the human note (if any) survives.
            lead.HeaderRemarks = header.HeaderRemarks ?? (action == "approve"
                ? StripNeedsReviewPrefix(lead.HeaderRemarks)
                : lead.HeaderRemarks);

            // Upsert items: match existing by Id, insert new (Id null/0), delete the rest.
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
                    if (existing == null)
                        throw new LeadReviewConflictException($"Line item {dto.Id.Value} changed during review.");

                    // WP-B4 metric: diff BEFORE the upsert overwrites the stored values.
                    var changed = DiffItemFields(existing, dto);
                    if (changed.Count > 0)
                    {
                        itemsChanged++;
                        foreach (var field in changed)
                            itemFieldChanges[field] = itemFieldChanges.TryGetValue(field, out var n) ? n + 1 : 1;
                    }

                    ApplyItemFields(existing, dto);
                }
                else
                {
                    var created = new LeadItem { LeadId = lead.Id };
                    ApplyItemFields(created, dto);
                    lead.LeadItems.Add(created);
                    itemsAdded++;
                }
            }

            // RemoveRange marks items Deleted but leaves them on the nav collection until save,
            // so exclude them when recomputing the resulting line-item count.
            lead.NoOfLineItems = lead.LeadItems.Count(li => !toRemove.Contains(li));

            if (lead.EmailIngests != null)
                lead.EmailIngests.ParseStatus = action == "approve" ? "Success" : "NeedsReview";

            lead.CommercialFactsVerified = action == "approve";
            if (action == "approve")
            {
                lead.ReviewApprovedBy = reviewedBy.Trim();
                lead.ReviewApprovedOn = DateTime.UtcNow;
            }
            else
            {
                lead.ReviewApprovedBy = null;
                lead.ReviewApprovedOn = null;
            }

            var reviewedOn = DateTime.UtcNow;
            lead.ModifiedDate = reviewedOn;
            lead.ReviewVersion++;

            var ownsTransaction = _context.Database.CurrentTransaction == null;
            await using var transaction = ownsTransaction
                ? await _context.Database.BeginTransactionAsync()
                : null;
            string? learningSkipped = null;
            try
            {
                // The first flush assigns database IDs to inserted lines. The audit is
                // built and written by the second flush inside the same transaction, so its
                // after image is exact without sacrificing atomicity. The audit is
                // CONSTRUCTED here rather than above so there is no window in which an
                // AfterJson placeholder could reach the database: the row has never
                // existed without its real after image.
                await _context.SaveChangesAsync();
                var afterJson = SerializeReviewSnapshot(lead);
                var audit = new LeadReviewAudit
                {
                    BusinessUnitId = businessUnitId,
                    LeadId = lead.Id,
                    FromVersion = fromVersion,
                    ToVersion = lead.ReviewVersion,
                    Action = action,
                    ReviewedBy = reviewedBy.Trim(),
                    Reason = string.IsNullOrWhiteSpace(review.Reason) ? null : review.Reason.Trim(),
                    BeforeJson = beforeJson,
                    AfterJson = afterJson,
                    ReviewedOn = reviewedOn
                };
                _context.Set<LeadReviewAudit>().Add(audit);
                await _context.SaveChangesAsync();

                // GOLDEN CORPUS: an approved review is a human assertion that the after
                // image is correct, which makes it the only ground truth this product has.
                // It is captured HERE — inside the review's own transaction, flushed with
                // it, committing with it or not at all — and deliberately NOT through
                // IMetricRecorder, which swallows its own exceptions. A corpus row that can
                // silently fail to appear yields a biased sample, and a biased sample
                // produces a confident wrong number instead of an honest missing one.
                if (action == "approve")
                    await CaptureExtractionCorpusAsync(lead, audit, businessUnitId, beforeJson, afterJson, reviewedOn);

                // LEARNING LOOP: a further flush inside the SAME transaction, so what the
                // reviewer taught commits with the review that taught it or not at all.
                // A learning failure must never fail the review — it is wrapped in a
                // savepoint and reported as a reason on the correction metric instead.
                if (shouldLearnIdentity && _aliasLearner != null && header.CustomerId.HasValue)
                    learningSkipped = await LearnClientIdentityAsync(
                        businessUnitId, lead, header.CustomerId.Value, previousCustomerId, audit.Id);

                if (transaction != null)
                    await transaction.CommitAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (transaction != null)
                    await transaction.RollbackAsync();
                throw new LeadReviewConflictException("The lead changed while this review was being saved. Refresh and retry.");
            }
            catch (DbUpdateException)
            {
                if (transaction != null)
                    await transaction.RollbackAsync();
                throw new LeadReviewConflictException("The review changed while this update was being saved. Refresh and retry.");
            }

            // WP-B4 passive metric (hook b): what the reviewer corrected. Additive,
            // null-safe, and the recorder never throws — review flow is unaffected.
            if (_metrics != null && (headerChanged.Count > 0 || itemsChanged > 0 || itemsAdded > 0
                                     || toRemove.Count > 0 || learningSkipped != null))
            {
                await _metrics.RecordAsync(businessUnitId,
                    ERP_RFQ_Automation.Metrics.MetricEventTypes.ExtractionCorrected, lead.Id, new
                    {
                        leadId = lead.Id,
                        action = review.Action,
                        headerChanged,
                        itemsAdded,
                        itemsRemoved = toRemove.Count,
                        itemsChanged,
                        itemFieldChanges,
                        learningSkipped
                    });
            }

            // Reuse the canonical mapping for the response.
            return await GetLeadByIdAsync(id, businessUnitId);
        }

        /// <summary>
        /// Turns the reviewer's client correction into durable identity knowledge, inside the
        /// review's own transaction.
        ///
        /// Guarded by a SAVEPOINT: a failed write inside a PostgreSQL transaction aborts the
        /// whole transaction, so without one a learning error would silently roll back a
        /// perfectly good review. On failure the savepoint is rolled back, anything the
        /// learner staged is detached, and the review commits unchanged.
        /// Returns a reason string when learning was skipped, otherwise null.
        /// </summary>
        private async Task<string?> LearnClientIdentityAsync(
            long businessUnitId, Lead lead, long customerId, long? previousCustomerId, long reviewAuditId)
        {
            const string savepoint = "client_identity_learning";
            var currentTransaction = _context.Database.CurrentTransaction;
            var savepointCreated = false;
            try
            {
                if (currentTransaction is not null && _context.Database.IsNpgsql())
                {
                    await currentTransaction.CreateSavepointAsync(savepoint);
                    savepointCreated = true;
                }

                var learned = await _aliasLearner!.LearnFromReviewAsync(
                    businessUnitId, lead, customerId, previousCustomerId, reviewAuditId);
                await _context.SaveChangesAsync();

                if (savepointCreated)
                    await currentTransaction!.ReleaseSavepointAsync(savepoint);

                _logger?.LogInformation(
                    "Client identity learning for lead {LeadId}: {Learned} learned, {Reinforced} reinforced, {Expired} expired.",
                    lead.Id, learned.Learned, learned.Reinforced, learned.Expired);
                return learned.SkippedReason;
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex,
                    "Client identity learning failed for lead {LeadId}; the review is unaffected.", lead.Id);
                if (savepointCreated)
                {
                    try { await currentTransaction!.RollbackToSavepointAsync(savepoint); }
                    catch (Exception rollbackFailure)
                    {
                        _logger?.LogError(rollbackFailure,
                            "Rolling back the client-identity learning savepoint failed for lead {LeadId}.", lead.Id);
                        throw;
                    }
                }
                foreach (var entry in _context.ChangeTracker
                             .Entries<ERP_RFQ_Automation.CommercialRouting.CustomerIdentifier>()
                             .Where(entry => entry.State != EntityState.Unchanged)
                             .ToList())
                    entry.State = EntityState.Detached;
                return "learningFailed";
            }
        }

        /// <summary>
        /// Reduces an approved review to labelled corpus cells and stages them for the
        /// caller's flush. Runs inside <c>SubmitLeadReviewAsync</c>'s transaction, so the
        /// label and the approval that created it are the same commit.
        ///
        /// Accuracy is never pooled across pipelines, so the row records which path
        /// produced the values being scored (<see cref="ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath"/>,
        /// or "unknown" when the lead has no ingestion occurrence). A deterministic
        /// spreadsheet parse and a language-model read are different systems and an
        /// average over both describes neither.
        ///
        /// A re-approval of the same lead writes a new audit and therefore a new set of
        /// cells; the unique index on (BU, audit, scope, field) keeps one row per cell per
        /// review, and the accuracy service counts documents by distinct review, so a
        /// re-reviewed lead cannot inflate its own sample.
        /// </summary>
        private async Task CaptureExtractionCorpusAsync(
            Lead lead, LeadReviewAudit audit, long businessUnitId,
            string beforeJson, string afterJson, DateTime capturedOn)
        {
            var observations = ERP_RFQ_Automation.Services.Measurement.ExtractionCorpusProjection
                .Diff(beforeJson, afterJson);
            if (observations.Count == 0) return;

            var occurrenceId = (await SourceOccurrenceIdsAsync(lead.Id, businessUnitId))
                .Select(id => (long?)id).FirstOrDefault();

            // Ordered in memory, not in SQL: IngestedAtUtc is a DateTimeOffset and SQLite —
            // which the repository test suite runs on — cannot ORDER BY that type. A lead
            // has a handful of ingestion occurrences at most, so the sort is free.
            var occurrences = await _context.Set<ERP_RFQ_Automation.LeadIdentity.LeadIngestionOccurrence>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == lead.Id)
                .Select(x => new { x.IngestedAtUtc, x.ProcessingPath })
                .ToListAsync();
            var path = occurrences
                .OrderByDescending(x => x.IngestedAtUtc)
                .Select(x => (ERP_RFQ_Automation.LeadIdentity.LeadProcessingPath?)x.ProcessingPath)
                .FirstOrDefault();

            var entries = observations.Select(observation => new ExtractionCorpusEntry
            {
                BusinessUnitId = businessUnitId,
                LeadId = lead.Id,
                LeadReviewAuditId = audit.Id,
                SourceDocumentOccurrenceId = occurrenceId,
                ExtractionPath = path?.ToString() ?? "unknown",
                Scope = observation.Scope,
                FieldName = observation.FieldName,
                ObservedCount = observation.Observed,
                CorrectedCount = observation.Corrected,
                FieldCorrect = observation.Correct,
                CapturedOn = capturedOn,
                ApprovedBy = audit.ReviewedBy
            });

            await _context.Set<ExtractionCorpusEntry>().AddRangeAsync(entries);
            await _context.SaveChangesAsync();
        }

        /// <summary>
        /// Source-document occurrences bound to this lead, from either the ingestion
        /// occurrence chain or the extraction job. Shared by the review gate and the
        /// approval evidence check so both answer "is there a document behind this lead?"
        /// the same way.
        /// </summary>
        private async Task<List<long>> SourceOccurrenceIdsAsync(long leadId, long businessUnitId)
        {
            var occurrenceIds = await _context.Set<ERP_RFQ_Automation.LeadIdentity.LeadIngestionOccurrence>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == leadId
                    && x.SourceDocumentOccurrenceId.HasValue)
                .Select(x => x.SourceDocumentOccurrenceId!.Value)
                .ToListAsync();
            occurrenceIds.AddRange(await _context.Set<ERP_RFQ_Automation.Extraction.ExtractionJob>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && x.ResultLeadId == leadId
                    && x.SourceDocumentOccurrenceId.HasValue)
                .Select(x => x.SourceDocumentOccurrenceId!.Value)
                .ToListAsync());
            return occurrenceIds.Distinct().ToList();
        }

        private async Task EnsureApprovalEvidenceAsync(long leadId, long businessUnitId)
        {
            var occurrenceIds = await SourceOccurrenceIdsAsync(leadId, businessUnitId);
            if (occurrenceIds.Count == 0)
                throw new LeadReviewValidationException(
                    "Approval requires authoritative source-document evidence.");

            var evidence = await _context.Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocumentOccurrence>()
                .AsNoTracking()
                .Include(x => x.SourceDocument)
                .Where(x => x.BusinessUnitId == businessUnitId && occurrenceIds.Contains(x.Id))
                .ToListAsync();
            var eligible = evidence.Count == occurrenceIds.Count && evidence.All(x =>
                x.SourceDocument.SecurityStatus
                    == ERP_RFQ_Automation.DocumentIntelligence.Persistence.DocumentSecurityStatus.Cleared
                && x.IntakeStatus is ERP_RFQ_Automation.DocumentIntelligence.Persistence.IntakeOccurrenceStatus.Resolved
                    or ERP_RFQ_Automation.DocumentIntelligence.Persistence.IntakeOccurrenceStatus.ReviewRequired
                && !string.IsNullOrWhiteSpace(x.SourceDocument.ObjectBucket)
                && !string.IsNullOrWhiteSpace(x.SourceDocument.ObjectKey)
                && !string.IsNullOrWhiteSpace(x.SourceDocument.ObjectVersion));
            if (!eligible)
                throw new LeadReviewValidationException(
                    "Approval requires cleared, integrity-valid source-document evidence.");
        }

        // WP-B4: field-level diff between a stored lead item and the reviewer's
        // submission — the exact field set ApplyItemFields writes. Camel-cased
        // names are the metric payload contract (Sla/WAVEB-WIRING.md).
        private static void ValidateReviewItems(IReadOnlyCollection<LeadItemReviewDTO> items, string action)
        {
            if (action == "approve" && items.Count == 0)
                throw new LeadReviewValidationException("At least one line item is required for approval.");

            foreach (var item in items)
            {
                if (action == "approve" && item.Quantity is null or <= 0)
                    throw new LeadReviewValidationException("Every approved line requires a quantity greater than zero.");
                if (action == "save" && item.Quantity is <= 0)
                    throw new LeadReviewValidationException("Quantity must be greater than zero when supplied.");
                if (item.UnitPrice is < 0)
                    throw new LeadReviewValidationException("Unit price cannot be negative.");
                if (item.LeadTime is < 0)
                    throw new LeadReviewValidationException("Lead time cannot be negative.");
                if (!string.IsNullOrWhiteSpace(item.Currency)
                    && (item.Currency.Trim().Length != 3 || !item.Currency.Trim().All(char.IsLetter)))
                    throw new LeadReviewValidationException("Currency must be a three-letter code.");
                if (item.UnitPrice.HasValue && string.IsNullOrWhiteSpace(item.Currency))
                    throw new LeadReviewValidationException("Currency is required when unit price is supplied.");
                if (action == "approve"
                    && string.IsNullOrWhiteSpace(item.ProductShortName)
                    && string.IsNullOrWhiteSpace(item.ItemMaterialCode))
                    throw new LeadReviewValidationException(
                        "Each approved line requires a product name or material code.");
            }
        }

        private static string SerializeReviewSnapshot(Lead lead, IReadOnlyCollection<LeadItem>? removed = null)
        {
            var removedIds = removed?.Select(item => item.Id).ToHashSet() ?? new HashSet<long>();
            return JsonSerializer.Serialize(new
            {
                lead.Id,
                lead.ReviewVersion,
                lead.Rfqno,
                lead.BuyersName,
                lead.BidClosingDate,
                lead.OpportunityNo,
                lead.HeaderRemarks,
                lead.CustomerId,
                lead.ContactId,
                lead.CustomerMatchStatus,
                lead.RequiresCommercialReview,
                lead.CommercialFactsVerified,
                lead.ReviewApprovedBy,
                lead.ReviewApprovedOn,
                ParseStatus = lead.EmailIngests?.ParseStatus,
                Items = lead.LeadItems
                    .Where(item => !removedIds.Contains(item.Id))
                    .OrderBy(item => item.Id)
                    .Select(item => new
                    {
                        item.Id,
                        item.LineItemNo,
                        item.ProductShortName,
                        item.ProductShortDescription,
                        item.CommodityProduct,
                        item.ItemMaterialCode,
                        item.Currency,
                        item.UnitOfMeasure,
                        item.UnitPrice,
                        item.Quantity,
                        item.ManufacturerName,
                        item.ManufacturerPartNumber,
                        item.AlternateProductName,
                        item.AlternatePartNumber,
                        item.ItemText,
                        item.LeadTime,
                        item.ExtraFields
                    })
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        }

        private static List<string> DiffItemFields(LeadItem item, LeadItemReviewDTO dto)
        {
            var changed = new List<string>();
            void Check<T>(string name, T stored, T submitted)
            {
                if (!EqualityComparer<T>.Default.Equals(stored, submitted)) changed.Add(name);
            }

            Check("lineItemNo", item.LineItemNo, dto.LineItemNo);
            Check("productShortName", item.ProductShortName, dto.ProductShortName);
            Check("productShortDescription", item.ProductShortDescription, dto.ProductShortDescription);
            Check("commodityProduct", item.CommodityProduct, dto.CommodityProduct);
            Check("itemMaterialCode", item.ItemMaterialCode, dto.ItemMaterialCode);
            Check("currency", item.Currency, dto.Currency);
            // Diff against the CANONICAL form of what the reviewer typed, because that is
            // what ApplyItemFields will store. Diffing the raw text made every unchanged
            // form submit record a spurious "unitOfMeasure" correction (stored "EA" vs
            // typed "each") into the review audit — polluting the one signal that says
            // what extraction actually got wrong.
            Check("unitOfMeasure", item.UnitOfMeasure,
                Services.Uom.UomCanonicalizer.CanonicalizeForStorage(dto.UnitOfMeasure));
            Check("unitPrice", item.UnitPrice, dto.UnitPrice);
            if (dto.Quantity.HasValue) Check("quantity", item.Quantity, dto.Quantity.Value);
            Check("manufacturerName", item.ManufacturerName, dto.ManufacturerName);
            Check("manufacturerPartNumber", item.ManufacturerPartNumber, dto.ManufacturerPartNumber);
            Check("alternateProductName", item.AlternateProductName, dto.AlternateProductName);
            Check("alternatePartNumber", item.AlternatePartNumber, dto.AlternatePartNumber);
            Check("itemText", item.ItemText, dto.ItemText);
            Check("leadTime", item.LeadTime, dto.LeadTime);
            if (dto.ExtraFields != null)
                Check("extraFields", item.ExtraFields, ExtraFieldsJson.Serialize(dto.ExtraFields));
            return changed;
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
            // The reviewer-correction door is the FIFTH UnitOfMeasure write path, and the
            // only one a human drives. Without this, a reviewer typing "each" stored it raw
            // and defeated the ingestion canonicalisation for exactly the rows a human
            // touched. Same policy as LeadItemMapper: spelling is settled, packaging and
            // form-factor words are kept verbatim, null stays null.
            item.UnitOfMeasure = Services.Uom.UomCanonicalizer.CanonicalizeForStorage(dto.UnitOfMeasure);
            item.UnitPrice = dto.UnitPrice;
            if (dto.Quantity.HasValue) item.Quantity = dto.Quantity.Value;
            item.ManufacturerName = dto.ManufacturerName;
            item.ManufacturerPartNumber = dto.ManufacturerPartNumber;
            item.AlternateProductName = dto.AlternateProductName;
            item.AlternatePartNumber = dto.AlternatePartNumber;
            item.ItemText = dto.ItemText;
            item.LeadTime = dto.LeadTime;
            // ExtraFields are captured at extraction time and must survive review round
            // trips: only overwrite when the reviewer explicitly supplies a value.
            if (dto.ExtraFields != null)
                item.ExtraFields = ExtraFieldsJson.Serialize(dto.ExtraFields);
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
