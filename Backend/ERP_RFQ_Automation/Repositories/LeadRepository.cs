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
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Authorization;

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
        private readonly ILogger<LeadRepository>? _logger;
        private readonly ERP_RFQ_Automation.Metrics.IMetricRecorder? _metrics;
        private readonly ICommercialLineResolutionApplicationService? _lineResolution;
        private readonly ERP_RFQ_Automation.CustomerResolution.ICustomerAliasLearner? _aliasLearner;

        // Optional dependencies keep existing constructions (tests, pre-wiring DI)
        // compiling and running: metrics / alias learning degrade to no-ops, the SLA
        // reader falls back to the flat default threshold.
        public LeadRepository(
            ErpRfqAutomationContext context,
            ISlaPolicyReader? slaPolicy = null,
            ILogger<LeadRepository>? logger = null,
            ERP_RFQ_Automation.Metrics.IMetricRecorder? metrics = null,
            ICommercialLineResolutionApplicationService? lineResolution = null,
            ERP_RFQ_Automation.CustomerResolution.ICustomerAliasLearner? aliasLearner = null)
        {
            _context = context;
            _slaPolicy = slaPolicy ?? new DefaultSlaPolicyReader();
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

        /// <summary>
        /// The tenant's LeadStatus rows that mean the enquiry is over: DISQUALIFIED, LOST,
        /// CANCELLED, COMPLETED, DUPLICATED — read through <see cref="LifecyclePolicy"/> rather
        /// than restated as a SQL string list, because the alias table (REJECTED → DISQUALIFIED,
        /// CLOSED → COMPLETED) cannot be expressed in a WHERE clause and a second, divergent
        /// definition of "finished" is how a lead ends up open in one screen and closed in another.
        ///
        /// <para>The type is normalised the same way <c>LifecycleStatusCatalog.ResolveIdAsync</c>
        /// normalises it, so a tenant carrying legacy "Lead Status" rows is read identically to a
        /// seeded one. IsActive is deliberately NOT filtered: retiring the "Cancelled" row does not
        /// un-cancel the leads still pointing at it.</para>
        ///
        /// <para>Unresolvable ids fail OPEN — the lead is shown. On a board that counts down to a
        /// bid deadline, a stale row a person can dismiss costs less than a live tender nobody
        /// ever sees again.</para>
        /// </summary>
        private async Task<List<long>> FinishedLeadStatusIdsAsync(long businessUnitId)
        {
            var statuses = await _context.SetupMasters.AsNoTracking()
                .Where(s => s.BusinessUnitId == businessUnitId
                    && s.SetupType.ToLower().Replace(" ", "") == "leadstatus")
                .Select(s => new { s.SetupId, s.SetupCode, s.SetupValue })
                .ToListAsync();

            return statuses
                .Where(s => LifecyclePolicy.IsTerminal(
                    "Lead", LifecyclePolicy.Canonicalize("Lead", s.SetupCode, s.SetupValue)))
                .Select(s => s.SetupId)
                .ToList();
        }

        /// <summary>Which owner the leads list was narrowed to. See <see cref="ParseLeadListView"/>.</summary>
        private enum LeadListOwnerFilter { None, Unassigned, Mine }

        /// <summary>
        /// Splits the list's <c>view</c> parameter into the QUEUE view and the OWNER filter.
        ///
        /// <para>They compose rather than replace each other: "Unassigned" and "Mine" narrow
        /// whichever queue the reader is already looking at, so they travel as a second
        /// comma-separated token on the one parameter this repository is handed
        /// (<c>view=revisions,unassigned</c>). A single bare token — every value that has ever
        /// been sent — parses exactly as it did before.</para>
        ///
        /// <para><c>mine</c> carries the reader's own user id (<c>mine:42</c>) because
        /// <c>LeadController</c> forwards no identity to this method. That is safe to accept from
        /// the caller: the business-unit predicate is applied first and unconditionally, so this
        /// token can only ever NARROW the rows the caller is already authorised to read, exactly
        /// as <c>GetAcceptedLeadsAsync(assignedToId:)</c> already does for the outstanding queue.
        /// A <c>mine</c> with no readable id matches nothing — "we cannot name the reader" must
        /// never render as somebody else's leads under a "Mine" label.</para>
        /// </summary>
        private static (string? QueueView, LeadListOwnerFilter Owner, long? OwnerUserId) ParseLeadListView(string? view)
        {
            if (string.IsNullOrWhiteSpace(view)) return (null, LeadListOwnerFilter.None, null);

            string? queueView = null;
            var owner = LeadListOwnerFilter.None;
            long? ownerUserId = null;

            foreach (var token in view.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (string.Equals(token, "unassigned", StringComparison.OrdinalIgnoreCase))
                {
                    owner = LeadListOwnerFilter.Unassigned;
                }
                else if (token.StartsWith("mine", StringComparison.OrdinalIgnoreCase)
                    && (token.Length == 4 || token[4] == ':'))
                {
                    owner = LeadListOwnerFilter.Mine;
                    if (token.Length > 5 && long.TryParse(token[5..], out var parsed)) ownerUserId = parsed;
                }
                else
                {
                    queueView ??= token;
                }
            }

            return (queueView, owner, ownerUserId);
        }

        public async Task<(IEnumerable<LeadResponseDTO>, int TotalCount)> GetLeadListAsync(int pageNumber, int pageSize, long? id, string? rfqno, string? buyersName, string? leadSource, long businessUnitId, DateTime? startDate = null, DateTime? endDate = null, string? emailSource = null, string? clientemail = null, string? view = null, AccountTeamScope? accessScope = null)
        {
            var (queueView, ownerFilter, ownerUserId) = ParseLeadListView(view);

            var query = _context.Leads
                .AsNoTracking()
                .Include(l => l.BusinessUnit)
                .Include(l => l.EmailIngests)
                // The owner is projected onto every list row now (see below), so the navigation
                // has to travel with the page or each row costs a lazy round trip.
                .Include(l => l.AssignToNavigation)
                .Where(l => l.BusinessUnitId == businessUnitId);

            if (accessScope != null)
                query = query.InCommercialScope(_context, businessUnitId, accessScope, DateTime.UtcNow);

            // The default list is the untriaged inbox — RfqRepository spells LeadStatusId == null
            // out as "new lead to review". "open" and "revisions" deliberately escape it: ANY
            // lifecycle transition stamps a status, so a queue that keeps this filter is a queue
            // that empties itself the moment a rep starts working, which is precisely how the
            // deadline board lost every tender the day after it was advanced.
            var openWorkView = string.Equals(queueView, "open", StringComparison.OrdinalIgnoreCase);
            if (!openWorkView && !string.Equals(queueView, "revisions", StringComparison.OrdinalIgnoreCase))
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
            if (string.Equals(queueView, "duplicates", StringComparison.OrdinalIgnoreCase))
                query = query.Where(l => l.DuplicateStatus == "suspected" || l.DuplicateStatus == "confirmed");
            else if (string.Equals(queueView, "revisions", StringComparison.OrdinalIgnoreCase))
                query = query.Where(l => l.CurrentRevisionNumber > 1);
            else if (openWorkView)
            {
                // "Open" is the whole live pipeline, not the inbox: untriaged mail AND everything
                // someone has already advanced. Only genuinely finished work is dropped, and which
                // states those are comes from the governed lifecycle policy, never from a second
                // list of strings restated here.
                var finished = await FinishedLeadStatusIdsAsync(businessUnitId);
                query = query.Where(l => l.LeadStatusId == null || !finished.Contains(l.LeadStatusId.Value));
            }
            else if (string.Equals(queueView, "ready-for-rfq", StringComparison.OrdinalIgnoreCase))
                // Advisory queue criteria only. RFQ Promotion performs the authoritative
                // revision, fit, participation and commercial-fact checks before creation.
                query = query.Where(l => l.CommercialFactsVerified && !l.RequiresCommercialReview
                    && l.CustomerId != null && l.LeadItems.Any()
                    && !l.Rfqs.Any());

            // The owner filter narrows whichever queue the reader is on — it is applied AFTER the
            // queue view, never instead of it, so "Revisions" plus "Unassigned" means both.
            if (ownerFilter == LeadListOwnerFilter.Unassigned)
                query = query.Where(l => l.AssignTo == null);
            else if (ownerFilter == LeadListOwnerFilter.Mine)
            {
                // No readable reader id matches nothing rather than everything: see ParseLeadListView.
                var mineUserId = ownerUserId ?? -1;
                query = query.Where(l => l.AssignTo == mineUserId);
            }

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

            // LIFECYCLE STATE: the projection has always carried LeadStatusId — a tenant-local
            // integer no screen can read — and nothing else, so every consumer that needed to know
            // whether work had started had to guess. The deadline board guessed by asking for the
            // untriaged view and dropped the rest. Canonical code for logic, the tenant's own label
            // for a human, one query for the whole page.
            var pagedStatusIds = leads.Where(l => l.LeadStatusId.HasValue)
                .Select(l => l.LeadStatusId!.Value).Distinct().ToList();
            var statusRows = pagedStatusIds.Count == 0
                ? new Dictionary<long, (string Code, string Label)>()
                : (await _context.SetupMasters.AsNoTracking()
                        .Where(s => s.BusinessUnitId == businessUnitId && pagedStatusIds.Contains(s.SetupId))
                        .Select(s => new { s.SetupId, s.SetupCode, s.SetupValue })
                        .ToListAsync())
                    .ToDictionary(
                        s => s.SetupId,
                        s => (LifecyclePolicy.Canonicalize("Lead", s.SetupCode, s.SetupValue), s.SetupValue));

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
                // No status row at all is a state too — the enquiry has never been triaged — and is
                // reported as null rather than invented as "Received".
                (string Code, string Label)? status = l.LeadStatusId.HasValue
                    && statusRows.TryGetValue(l.LeadStatusId.Value, out var statusRow)
                        ? statusRow
                        : null;
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
                    // FR-RFQ-03 / FR-RFQ-04 intake fields. Projected on the LIST as well as
                    // the detail because the required delivery date belongs beside the
                    // deadline in the grid: those are two different dates and a trader who
                    // sees only one of them quotes against the wrong one.
                    RequiredDeliveryDate = l.RequiredDeliveryDate,
                    BidClosingDateHijri = l.BidClosingDateHijri,
                    AgreementReference = l.AgreementReference,
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
                    LeadStatusCode = status?.Code,
                    LeadStatusLabel = status?.Label,
                    LifecycleVersion = l.LifecycleVersion,
                    ReviewVersion = l.ReviewVersion,
                    RequiresCommercialReview = l.RequiresCommercialReview,
                    CommercialFactsVerified = l.CommercialFactsVerified,
                    InquiryType = l.InquiryType, // WP-BOQ: service/mixed list badge
                    DuplicateStatus = l.DuplicateStatus,
                    DuplicateOfLeadId = l.DuplicateOfLeadId,
                    DuplicateResolvedBy = l.DuplicateResolvedBy,
                    // This list offers a "revisions" view that filters on CurrentRevisionNumber > 1
                    // (see the query above), and then returned every matching row reporting
                    // revision 0 because this projection never set the column. A view whose whole
                    // purpose is revised leads could not say which revision any of them was.
                    CurrentRevisionNumber = l.CurrentRevisionNumber,
                    // WHO OWNS THIS ENQUIRY. The DTO has carried these four properties since
                    // governed assignment shipped and this projection — the one behind
                    // /api/Lead, the first screen a rep opens — never set any of them, so every
                    // row on the leads list reported itself unowned no matter who it belonged to.
                    // AssignmentVersion travels with them because it is the optimistic-concurrency
                    // token PUT /api/commercial-routing/leads/{id}/owner demands, and without it
                    // no list row can offer an assign action at all.
                    AssignedToId = l.AssignTo,
                    AssignedToFullName = l.AssignToNavigation != null
                        ? $"{l.AssignToNavigation.FirstName} {l.AssignToNavigation.LastName}".Trim()
                        : null,
                    AssignedOn = l.AssignOn,
                    AssignComment = l.AssignComment,
                    AssignedByUserId = l.AssignedByUserId,
                    AssignmentMethod = l.AssignmentMethod,
                    ManualAssignmentOverride = l.ManualAssignmentOverride,
                    AssignmentVersion = l.AssignmentVersion,
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

        // Compatibility tombstone. RFQ Promotion is the sole Lead-origin creation path.
        public Task<(long RfqId, string Rfqno)> ConvertLeadToRfqAsync(long id, long businessUnitId, string createdBy)
        {
            return Task.FromException<(long RfqId, string Rfqno)>(new InvalidOperationException(
                "Direct LeadRepository RFQ creation is retired. Commit the current Lead Revision participation decision and invoke RFQ Promotion."));
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
                    // Completed from the "// ... other fields" this projection used to stop at.
                    // The detail projection of this same DTO has always set them; this one is what
                    // the accepted-leads grid reads, and FileSize arriving undefined is what makes
                    // a size renderer print NaN KB rather than nothing.
                    FileSize = a.FileSize,
                    ContentType = a.ContentType,
                    CreatedOn = a.CreatedOn,
                    UploadedDate = a.UploadedDate
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
                // The commercial identity, on the LIST as well as the detail. The detail projection
                // of this same DTO has always carried both; dropping them here meant which endpoint
                // a screen happened to call decided whether the lead had a Nexora serial at all —
                // and the serial is the reference a customer quotes back at us.
                CommercialCaseId = l.CommercialCaseId,
                CommercialCaseReference = l.CommercialCaseReference,
                Rfqno = l.Rfqno,
                BuyersName = l.BuyersName,
                RecDate = l.RecDate,
                BidClosingDate = l.BidClosingDate,
                // FR-RFQ-04 — the buyer's own date, beside the submission deadline.
                RequiredDeliveryDate = l.RequiredDeliveryDate,
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
                AssignmentMethod = l.AssignmentMethod,
                ManualAssignmentOverride = l.ManualAssignmentOverride,
                AssignmentVersion = l.AssignmentVersion,

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
                // FR-RFQ-04 — the buyer's own date, beside the submission deadline.
                RequiredDeliveryDate = lead.RequiredDeliveryDate,
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
                AssignmentMethod = lead.AssignmentMethod,
                ManualAssignmentOverride = lead.ManualAssignmentOverride,
                AssignmentVersion = lead.AssignmentVersion,
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
            // Same lifecycle-state read as the list projection. A field populated on the list and
            // silently null on the detail would make which endpoint a screen happened to call
            // decide whether the lead has a status — the exact class of split truth this
            // projection has been corrected for before (see CommercialCaseReference above).
            var detailStatusRow = lead.LeadStatusId.HasValue
                ? await _context.SetupMasters.AsNoTracking()
                    .Where(s => s.BusinessUnitId == businessUnitId && s.SetupId == lead.LeadStatusId.Value)
                    .Select(s => new { s.SetupCode, s.SetupValue })
                    .FirstOrDefaultAsync()
                : null;
            var detailCandidates = await GetClientCandidatesAsync(id, businessUnitId);
            var emailProvenance = lead.EmailIngestsId.HasValue
                ? await (from ingest in _context.EmailIngests.AsNoTracking()
                         where ingest.Id == lead.EmailIngestsId.Value
                               && ingest.EmailConfiguration.BusinessUnitId == businessUnitId
                         join assembly in _context.EmailInquiryAssemblies.AsNoTracking()
                             on ingest.Id equals assembly.EmailIngestId into assemblies
                         from assembly in assemblies.DefaultIfEmpty()
                         select new
                         {
                             ingest.MessageId,
                             ingest.EmailSubject,
                             Sender = ingest.FromEmail,
                             ReceivedAtUtc = assembly == null ? null : assembly.ReceivedAtUtc
                         }).SingleOrDefaultAsync()
                : null;

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
                // FR-RFQ-03 / FR-RFQ-04 intake fields — see LeadResponseDTO for why each
                // is distinct from the date or reference sitting next to it.
                RequiredDeliveryDate = lead.RequiredDeliveryDate,
                BidClosingDateHijri = lead.BidClosingDateHijri,
                AgreementReference = lead.AgreementReference,
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
                EmailMessageId = emailProvenance?.MessageId,
                EmailSubject = emailProvenance?.EmailSubject,
                EmailSender = emailProvenance?.Sender,
                EmailReceivedAtUtc = emailProvenance?.ReceivedAtUtc,
                ModifiedDate = lead.ModifiedDate,
                EmailSource = lead.EmailSource,
                Clientemail = lead.Clientemail,
                LeadStatusId = lead.LeadStatusId,
                LeadStatusCode = detailStatusRow == null
                    ? null
                    : LifecyclePolicy.Canonicalize("Lead", detailStatusRow.SetupCode, detailStatusRow.SetupValue),
                LeadStatusLabel = detailStatusRow?.SetupValue,
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
                AssignmentMethod = lead.AssignmentMethod,
                ManualAssignmentOverride = lead.ManualAssignmentOverride,
                AssignmentVersion = lead.AssignmentVersion,
                AssignedByUserId = lead.AssignedByUserId,

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
                    ExtraFields = ExtraFieldsJson.Deserialize(li.ExtraFields),
                    // AA-01 · the tenant-defined bag, carried raw so the line grid can
                    // materialise a custom-field column per the user's saved layout.
                    CustomFields = li.CustomFieldsJson
                }).ToList(),
                Attachments = attachments
            };
        }

        public async Task<LeadStatsDTO> GetLeadStatsAsync(long businessUnitId, AccountTeamScope? accessScope = null)
        {
            var query = _context.Leads
                .AsNoTracking()
                .Where(l => l.BusinessUnitId == businessUnitId);
            if (accessScope != null)
                query = query.InCommercialScope(_context, businessUnitId, accessScope, DateTime.UtcNow);
            var leads = await query.ToListAsync();

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
        public async Task<(IEnumerable<LeadNeedsReviewItemDTO>, int TotalCount)> GetNeedsReviewLeadsAsync(int pageNumber, int pageSize, long businessUnitId, string? search = null, AccountTeamScope? accessScope = null)
        {
            var query = _context.Leads
                .AsNoTracking()
                .Include(l => l.EmailIngests)
                .Where(l => l.BusinessUnitId == businessUnitId)
                .Where(l => l.LeadStatusId == null)
                .Where(l => l.EmailIngests == null || l.EmailIngests.ParseStatus == "NeedsReview");

            if (accessScope != null)
                query = query.InCommercialScope(_context, businessUnitId, accessScope, DateTime.UtcNow);

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

            // Per-line verdicts from the evidence ledger, for this page of leads only. The
            // client has rendered this count since it replaced the confidence percentage and
            // nothing served it. It is read, never re-derived: the same CanonicalLineItem
            // status the review screen shows, so the queue and the workbench cannot disagree.
            var leadIds = leads.Select(l => l.Id).ToList();
            var ledgerLines = await _context
                .Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.CanonicalLineItem>()
                .AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId
                            && x.Inquiry.LeadId != null
                            && leadIds.Contains(x.Inquiry.LeadId!.Value))
                .GroupBy(x => x.Inquiry.LeadId!.Value)
                .Select(g => new
                {
                    LeadId = g.Key,
                    NeedingCheck = g.Count(x => x.ValidationStatus
                        != ERP_RFQ_Automation.DocumentIntelligence.Persistence.CanonicalValidationStatus.Valid)
                })
                .ToListAsync();
            var needingCheckByLead = ledgerLines.ToDictionary(x => x.LeadId, x => x.NeedingCheck);

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
                ReviewVersion = l.ReviewVersion,
                // Null, not zero: a document whose extraction path wrote no ledger has no
                // per-line verdict at all, and "0 of 8 need a check" would be a claim we
                // cannot support. The client falls back to the bare line count.
                LinesNeedingCheck = needingCheckByLead.TryGetValue(l.Id, out var needing)
                    ? needing
                    : null
            }).ToList();

            return (dtos, totalCount);
        }

        // Persist reviewer corrections against a low-confidence lead; only approval clears the review flag.
        // Loads the aggregate TRACKED (LeadItems + EmailIngests) so header/item edits, inserts and
        // deletes all flush in a single SaveChanges. Tenant ownership is enforced by the global
        // query filter; any BusinessUnitId in the payload is ignored by design.
        /// <summary>
        /// Entry point for the lead review submit. The whole method — load, mutate, transaction and
        /// commit — is the retriable unit, because the review's own transaction is opened partway
        /// down and <c>NpgsqlRetryingExecutionStrategy</c> (Program.cs <c>EnableRetryOnFailure</c>)
        /// refuses any transaction opened outside a strategy delegate. Without this,
        /// <c>POST /api/Lead/{id}/review</c> threw "does not support user-initiated transactions"
        /// on every PostgreSQL request.
        ///
        /// <para>The unit starts at the ENTRY, not at the transaction, because the entity graph is
        /// loaded and mutated before the transaction opens: a retry must re-read the lead rather
        /// than re-apply the failed attempt's edits to a stale tracked instance. Hence the
        /// <c>ChangeTracker.Clear()</c> on every attempt.</para>
        /// </summary>
        public Task<LeadResponseDTO?> SubmitLeadReviewAsync(
            long id, long businessUnitId, LeadReviewSubmitDTO review, string reviewedBy = "system")
        {
            // A caller that already owns a transaction owns the retriable unit too; nesting a
            // strategy inside it would be wrong, and the core path honours the ambient transaction.
            if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction is not null)
                return SubmitLeadReviewCoreAsync(id, businessUnitId, review, reviewedBy);

            var strategy = _context.Database.CreateExecutionStrategy();
            return strategy.ExecuteAsync(() =>
            {
                _context.ChangeTracker.Clear();
                return SubmitLeadReviewCoreAsync(id, businessUnitId, review, reviewedBy);
            });
        }

        /// <summary>
        /// Records that sales requested missing commercial information without moving the lead
        /// through a technical lifecycle state. The lead and immutable review audit share one
        /// atomic flush; ReviewVersion is the optimistic-concurrency fence.
        /// </summary>
        public async Task<LeadResponseDTO?> RequestClarificationAsync(
            long id, long businessUnitId, LeadClarificationRequestDTO request, string requestedBy)
        {
            if (string.IsNullOrWhiteSpace(requestedBy))
                throw new LeadReviewValidationException("Requester identity is required.");
            var note = request.Note?.Trim();
            if (string.IsNullOrWhiteSpace(note) || note.Length < 3)
                throw new LeadReviewValidationException("A clarification note is required.");

            var lead = await _context.Leads
                .Include(item => item.LeadItems)
                .Include(item => item.EmailIngests)
                .SingleOrDefaultAsync(item => item.Id == id && item.BusinessUnitId == businessUnitId);
            if (lead == null) return null;
            if (request.ExpectedReviewVersion != lead.ReviewVersion)
                throw new LeadReviewConflictException(
                    $"Review version {request.ExpectedReviewVersion} is stale; current version is {lead.ReviewVersion}.");

            var beforeJson = SerializeReviewSnapshot(lead);
            var fromVersion = lead.ReviewVersion;
            var requestedOn = DateTime.UtcNow;
            lead.RequiresCommercialReview = true;
            lead.CommercialFactsVerified = false;
            lead.ReviewApprovedBy = null;
            lead.ReviewApprovedOn = null;
            lead.ModifiedDate = requestedOn;
            lead.ReviewVersion++;

            _context.Set<LeadReviewAudit>().Add(new LeadReviewAudit
            {
                BusinessUnitId = businessUnitId,
                LeadId = lead.Id,
                FromVersion = fromVersion,
                ToVersion = lead.ReviewVersion,
                Action = "clarification",
                ReviewedBy = requestedBy.Trim(),
                Reason = note,
                BeforeJson = beforeJson,
                AfterJson = SerializeReviewSnapshot(lead),
                ReviewedOn = requestedOn
            });

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new LeadReviewConflictException("The lead changed while clarification was requested. Refresh and retry.");
            }
            catch (DbUpdateException)
            {
                throw new LeadReviewConflictException("The clarification request conflicted with another review. Refresh and retry.");
            }

            return await GetLeadByIdAsync(id, businessUnitId);
        }

        private async Task<LeadResponseDTO?> SubmitLeadReviewCoreAsync(
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
            // state is the lead's own — unverified, plus the same authoritative
            // source-document evidence approval already demands.
            //
            // LIFECYCLE POSITION IS NOT REVIEW STATE, and treating it as one was a ONE-WAY TRAP.
            //
            // This condition also required LeadStatusId == null, as a proxy for "untriaged".
            // But advancing the governed lifecycle SETS LeadStatusId, and the very first hop a
            // user can make from the lead screen — RECEIVED -> PENDING_IDENTIFICATION — does
            // exactly that. From that moment the approval path was shut. QUALIFIED, however,
            // REQUIRES the approval (LifecycleApplicationService: "AI-extracted commercial facts
            // must be approved before the lead can be qualified"), and the lifecycle offers no
            // edge back to PENDING_IDENTIFICATION. So an upload-door lead advanced before its
            // figures were approved could never be qualified, never become an RFQ, and never be
            // recovered — with no message saying why.
            //
            // Reproduced on the live tenant: lead 467 was approvable while LeadStatusId was
            // null, and returned 409 "This lead is no longer awaiting extraction review" once
            // it was UNDER_REVIEW. Leads 466 and 467 both reached that dead end.
            //
            // The question this gate exists to ask is whether the extracted facts have been
            // verified yet, and whether there is authoritative evidence to verify them against.
            // Both remaining clauses ask exactly that. The lifecycle clause asked something else
            // entirely and is simply dropped: approving is now idempotent with respect to where
            // the lead sits, so it can be done before advancing, after advancing, or from the
            // dead end a lead is already in.
            var awaitingReview = lead.EmailIngests != null
                ? string.Equals(lead.EmailIngests.ParseStatus, "NeedsReview", StringComparison.OrdinalIgnoreCase)
                : !lead.CommercialFactsVerified
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
            if (header.RequiredDeliveryDate != null && header.RequiredDeliveryDate != lead.RequiredDeliveryDate) headerChanged.Add("requiredDeliveryDate");
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
            if (header.RequiredDeliveryDate != null) lead.RequiredDeliveryDate = header.RequiredDeliveryDate;
            if (header.OpportunityNo != null) lead.OpportunityNo = header.OpportunityNo;

            // HeaderRemarks: a client-supplied value wins; otherwise strip the review marker
            // from the existing remark so the human note (if any) survives.
            lead.HeaderRemarks = header.HeaderRemarks ?? (action == "approve"
                ? StripNeedsReviewPrefix(lead.HeaderRemarks)
                : lead.HeaderRemarks);

            // A review never mutates or deletes a canonical line already referenced by an
            // immutable LeadRevision. Build a fresh current projection, archive the previous
            // projection, then append a human revision below.
            var keptIds = items.Where(i => i.Id.HasValue && i.Id.Value > 0)
                               .Select(i => i.Id!.Value)
                               .ToHashSet();
            var previousProjection = lead.LeadItems.Where(li => li.IsCurrentRevisionProjection).ToList();
            var toRemove = previousProjection.Where(li => !keptIds.Contains(li.Id)).ToList();
            foreach (var previous in previousProjection) previous.IsCurrentRevisionProjection = false;
            var replacementItems = new List<LeadItem>();

            foreach (var dto in items)
            {
                LeadItem created;
                if (dto.Id.HasValue && dto.Id.Value > 0)
                {
                    var existing = previousProjection.FirstOrDefault(li => li.Id == dto.Id.Value);
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

                    created = new LeadItem();
                    _context.Entry(created).CurrentValues.SetValues(existing);
                    created.Id = 0;
                    created.EvidenceSourceLeadItemId = existing.EvidenceSourceLeadItemId ?? existing.Id;
                }
                else
                {
                    created = new LeadItem { LeadId = lead.Id };
                    itemsAdded++;
                }
                created.IsCurrentRevisionProjection = true;
                ApplyItemFields(created, dto);
                replacementItems.Add(created);
                lead.LeadItems.Add(created);
            }

            lead.NoOfLineItems = replacementItems.Count;

            if (lead.EmailIngests != null)
                lead.EmailIngests.ParseStatus = action == "approve" ? "Success" : "NeedsReview";

            lead.CommercialFactsVerified = action == "approve";
            if (action == "approve")
            {
                // The review that RequiresCommercialReview asks for has now happened, so the
                // demand is satisfied and is cleared. Leaving it set was not a stricter
                // posture, it was a dead end: the "ready-for-rfq" queue selects on
                // `CommercialFactsVerified && !RequiresCommercialReview` (see the list view
                // above), so every AI-extracted lead — including fully approved ones — was
                // invisible there forever, and the queue that tells a user WHICH leads to
                // convert has therefore always been empty. Conversion itself already gates on
                // the pair together (`RequiresCommercialReview && !CommercialFactsVerified`),
                // so clearing the flag on approval loses no protection.
                lead.RequiresCommercialReview = false;
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
                await new LeadIdentityApplicationService(_context).AppendHumanRevisionAsync(
                    businessUnitId, lead.Id, reviewedBy.Trim(),
                    string.IsNullOrWhiteSpace(review.Reason) ? $"Human extraction review: {action}." : review.Reason.Trim(),
                    $"lead-review-revision:{businessUnitId}:{lead.Id}:{lead.ReviewVersion}");
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
        /// Links a lead to the client organisation a HUMAN picked — the second governed door
        /// onto <c>Lead.ResolveCommercialIdentity</c>, and the one that is open for a lead's
        /// whole life.
        ///
        /// <para><b>The defect this exists to close.</b> Until now the ONLY human path that
        /// could set a lead's customer was <c>SubmitLeadReviewAsync</c>, and that method opens
        /// with a gate: the lead must still be awaiting extraction review
        /// (<c>EmailIngests.ParseStatus == "NeedsReview"</c>, or for an upload-door lead
        /// <c>!CommercialFactsVerified</c> plus source evidence). Extraction review ENDS. The
        /// worker sets <c>ParseStatus = "Success"</c> the moment extraction succeeds
        /// (<c>ExtractionWorker.cs:1452</c>), and an approve sets it too
        /// (<c>SubmitLeadReviewCoreAsync</c>, above). So on the ordinary happy path — a
        /// document that extracted cleanly and never needed a reviewer — the client-linking
        /// door was shut before anyone ever saw the lead, and every attempt came back
        /// <c>"This lead is no longer awaiting extraction review."</c></para>
        ///
        /// <para>That was terminal, not cosmetic. A lead cannot be QUALIFIED and cannot be
        /// converted to an RFQ without a customer, so an enquiry the machine could not match
        /// to an existing client record could never reach a quote by ANY route: the machine
        /// had nothing to match against and the human was locked out. The live tenant shows
        /// the shape of it — enquiries sitting on the deadline board reading "Not linked to a
        /// client record" against names like "Fulton County Government" that have no customer
        /// record at all.</para>
        ///
        /// <para>The gate itself is CORRECT and is left exactly as it is: it protects a
        /// method that rewrites the entire line-item set from a client-held snapshot. The
        /// error was bundling a commercial decision with a lifetime of its own into a
        /// document-correction workflow that closes. This command is that decision on its
        /// own, and it writes exactly two fields.</para>
        ///
        /// <para>Everything the review path guarantees about a client link is preserved here:
        /// the tenant/active check on the customer, the ownership check on the contact, the
        /// human-grade status (so <c>IsHumanDecided</c> is true and the machine resolver will
        /// never overwrite it), the immutable audit row, and the alias-learning loop inside
        /// the same transaction.</para>
        /// </summary>
        public Task<LeadResponseDTO?> LinkClientAsync(
            long id, long businessUnitId, LeadClientLinkRequestDTO request, string linkedBy = "system")
        {
            // Same reasoning as SubmitLeadReviewAsync: a caller that already owns a
            // transaction owns the retriable unit, and nesting a strategy inside it is wrong.
            if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction is not null)
                return LinkClientCoreAsync(id, businessUnitId, request, linkedBy);

            var strategy = _context.Database.CreateExecutionStrategy();
            return strategy.ExecuteAsync(() =>
            {
                _context.ChangeTracker.Clear();
                return LinkClientCoreAsync(id, businessUnitId, request, linkedBy);
            });
        }

        private async Task<LeadResponseDTO?> LinkClientCoreAsync(
            long id, long businessUnitId, LeadClientLinkRequestDTO request, string linkedBy)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (string.IsNullOrWhiteSpace(linkedBy))
                throw new LeadReviewValidationException("Reviewer identity is required.");
            if (!request.CustomerId.HasValue || request.CustomerId.Value <= 0)
                throw new LeadReviewValidationException("A customer is required.");

            var customerId = request.CustomerId.Value;

            // LeadItems and EmailIngests are loaded because SerializeReviewSnapshot reads
            // both; a snapshot missing them would record a false before/after image.
            var lead = await _context.Leads
                .Include(l => l.LeadItems)
                .Include(l => l.EmailIngests)
                .FirstOrDefaultAsync(l => l.Id == id && l.BusinessUnitId == businessUnitId);

            if (lead == null) return null;

            if (request.ExpectedVersion.HasValue && request.ExpectedVersion.Value != lead.ReviewVersion)
                throw new LeadReviewConflictException(
                    $"Review version {request.ExpectedVersion} is stale; current version is {lead.ReviewVersion}.");

            var customerExists = await _context.Customers.AsNoTracking().AnyAsync(customer =>
                customer.Id == customerId && customer.Buid == businessUnitId && customer.IsActive != false);
            if (!customerExists)
                throw new LeadReviewValidationException("The selected customer was not found in this tenant.");

            if (request.ContactId.HasValue)
            {
                var contactExists = await _context.Contacts.AsNoTracking().AnyAsync(contact =>
                    contact.Id == request.ContactId.Value && contact.CustomerId == customerId
                    && contact.IsActive != false);
                if (!contactExists)
                    throw new LeadReviewValidationException("The selected contact does not belong to the selected customer.");
            }

            // RE-POINTING GUARD. Rfq.InheritCommercialIdentity refuses to accept a lead whose
            // customer differs from the one the RFQ already carries, and it is the RFQ that
            // every downstream document (quote, order, invoice) is addressed from. Moving the
            // lead underneath a converted RFQ would leave the two disagreeing about who the
            // client is, with the RFQ unable to ever re-inherit. Setting a customer where
            // there was none is always safe — conversion already requires one, so a lead with
            // no customer has no RFQ.
            if (lead.CustomerId.HasValue && lead.CustomerId.Value != customerId)
            {
                var hasRfq = await _context.Rfqs.AsNoTracking().IgnoreQueryFilters()
                    .AnyAsync(r => r.LeadId == lead.Id && r.BusinessUnitId == businessUnitId);
                if (hasRfq)
                    throw new LeadReviewConflictException(
                        "This lead has already been converted to an RFQ, so its client cannot be changed. "
                        + "Correct the client on the RFQ instead, or reject and re-raise the enquiry.");
            }

            var beforeJson = SerializeReviewSnapshot(lead);
            var fromVersion = lead.ReviewVersion;
            var previousCustomerId = lead.CustomerId;

            // Human-grade statuses, identical to the review path's, so a link made here and a
            // link made in review are indistinguishable downstream — and both are protected
            // from the machine resolver by LeadCustomerMatchStatuses.IsHumanDecided.
            lead.ResolveCommercialIdentity(
                customerId,
                request.ContactId,
                request.ContactId.HasValue
                    ? LeadCustomerMatchStatuses.Confirmed
                    : LeadCustomerMatchStatuses.CustomerConfirmedContactUnresolved);

            // Deliberately NOT touched: ParseStatus, CommercialFactsVerified,
            // RequiresCommercialReview, ReviewApprovedBy/On. Naming the buyer is not a
            // statement that the extracted figures are correct, and quietly marking them
            // verified here would let a lead skip the approval that qualification demands.
            var linkedOn = DateTime.UtcNow;
            lead.ModifiedDate = linkedOn;

            // The audit table is unique on (tenant, lead, ToVersion), so the version must
            // advance for the row to exist at all. It is also the lead's concurrency token,
            // which is the behaviour we want: a review workbench holding a stale version
            // finds out that the client changed underneath it.
            lead.ReviewVersion++;

            var ownsTransaction = _context.Database.CurrentTransaction == null;
            await using var transaction = ownsTransaction
                ? await _context.Database.BeginTransactionAsync()
                : null;
            try
            {
                await _context.SaveChangesAsync();
                await new LeadIdentityApplicationService(_context).AppendHumanRevisionAsync(
                    businessUnitId, lead.Id, linkedBy.Trim(),
                    string.IsNullOrWhiteSpace(request.Reason) ? "Human client identity link." : request.Reason.Trim(),
                    $"lead-client-link-revision:{businessUnitId}:{lead.Id}:{lead.ReviewVersion}");

                var audit = new LeadReviewAudit
                {
                    BusinessUnitId = businessUnitId,
                    LeadId = lead.Id,
                    FromVersion = fromVersion,
                    ToVersion = lead.ReviewVersion,
                    // 11 characters; the column is varchar(20).
                    Action = "link-client",
                    ReviewedBy = linkedBy.Trim(),
                    Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
                    BeforeJson = beforeJson,
                    AfterJson = SerializeReviewSnapshot(lead),
                    ReviewedOn = linkedOn
                };
                _context.Set<LeadReviewAudit>().Add(audit);
                await _context.SaveChangesAsync();

                // LEARNING LOOP. The P6 approval gate exists to keep a MACHINE match from
                // bootstrapping itself into an authoritative alias; the customer here is
                // always one a person typed or clicked, which is exactly the signal P6 wants
                // to keep. Same savepoint discipline as the review path: a learning failure
                // never fails the link.
                if (_aliasLearner != null)
                    await LearnClientIdentityAsync(businessUnitId, lead, customerId, previousCustomerId, audit.Id);

                if (transaction != null)
                    await transaction.CommitAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (transaction != null)
                    await transaction.RollbackAsync();
                throw new LeadReviewConflictException("The lead changed while the client was being linked. Refresh and retry.");
            }
            catch (DbUpdateException)
            {
                if (transaction != null)
                    await transaction.RollbackAsync();
                throw new LeadReviewConflictException("The client link could not be saved. Refresh and retry.");
            }

            _logger?.LogInformation(
                "Lead {LeadId} linked to customer {CustomerId} (contact {ContactId}) by {LinkedBy}.",
                lead.Id, customerId, request.ContactId, linkedBy);

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
                // Identity baselines are excluded: a baseline is always the NEWEST occurrence on
                // the lead, so without this filter it would win the OrderByDescending below and
                // relabel this corpus entry's ExtractionPath as Deterministic — moving a
                // published extraction-accuracy bound onto a pipeline that never ran.
                .Where(x => x.BusinessUnitId == businessUnitId && x.LeadId == lead.Id
                            && x.RecordKind == ERP_RFQ_Automation.LeadIdentity.LeadOccurrenceRecordKind.Ingestion)
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
                    .Where(item => item.IsCurrentRevisionProjection && !removedIds.Contains(item.Id))
                    .OrderBy(item => item.Id)
                    .Select(item => new
                    {
                        // A correction creates a new immutable projection row. Accuracy and
                        // audit diffs must follow the logical evidence-bearing line, otherwise
                        // every unchanged field is falsely counted as a deletion plus insertion.
                        Id = item.EvidenceSourceLeadItemId ?? item.Id,
                        ProjectionId = item.Id,
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

        // A null quantity in the payload means "the reviewer did not touch it": the existing
        // value is kept on update, and on insert the row is left with a NULL quantity, which is
        // the honest record of a line whose quantity nobody has yet stated. It used to default
        // to 0 there, because the model could not express "unknown"; it now can, and approval
        // already refuses a line whose quantity is null or non-positive.
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
