using ERP_RFQ_Automation.DTOs.LookupDTOs;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.CommercialCases.Promotion;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.DTOs.RfqDTOs;
using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Authorization;
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

        public async Task<(IEnumerable<RfqResponseDTO>, int TotalItems)> GetAllAsync(long businessUnitId, int pageNumber = 1, int pageSize = 10, string? search = null, bool? isActive = null, long? assignedToId = null, string? createdBy = null, long? rfqStatusId = null, string? rfqStatusCode = null, string? readiness = null, AccountTeamScope? accessScope = null)
        {
            IQueryable<Rfq> query = _context.Rfqs
                .AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId)
                .Include(r => r.BusinessUnit)
                .Include(r => r.Lead)
                .Include(r => r.Rfqstatus)
                .Include(r => r.RfqtypeNavigation)
                .Include(r => r.Customer);

            if (accessScope != null)
                query = query.InCommercialScope(_context, businessUnitId, accessScope, DateTime.UtcNow);

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
                // The RFQ's OWN commercial identity, never one derived from its lead.
                //
                // These three fields used to fall back through `?? r.Lead.CommercialCaseId`, which
                // is the same silent foreign-key substitution that made the case column decorative
                // in the timeline reader: an RFQ with a NULL column displayed a case anyway, so the
                // rows the case workspace now reports as traceability gaps were masked on the one
                // screen a user would notice them. A null here is the truth and the UI renders it
                // as "not linked".
                CommercialCaseId = r.CommercialCaseId,
                CommercialCaseReference = r.NexoraSerial,
                NexoraSerial = r.NexoraSerial,
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
                PromotionId = r.PromotionId,
                SourceLeadRevisionId = r.SourceLeadRevisionId,
                ParticipationDecisionId = r.ParticipationDecisionId,
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

        public async Task<RfqResponseDTO> GetByIdAsync(long id, long businessUnitId, AccountTeamScope? accessScope = null)
        {
            var query = _context.Rfqs
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
                .Where(r => r.Id == id && r.BusinessUnitId == businessUnitId);
            if (accessScope != null)
                query = query.InCommercialScope(_context, businessUnitId, accessScope, DateTime.UtcNow);
            var rfq = await query.FirstOrDefaultAsync();

            if (rfq == null)
                throw new KeyNotFoundException($"RFQ with ID {id} not found in Business Unit {businessUnitId}.");

            var contactName = rfq.ContactId.HasValue
                ? await _context.Contacts.AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.Id == rfq.ContactId.Value)
                    .Select(x => (x.FirstName + " " + x.LastName).Trim())
                    .SingleOrDefaultAsync()
                : null;
            var accountOwnerName = rfq.CustomerId.HasValue
                ? await (from ownership in _context.Set<CustomerOwnership>().AsNoTracking()
                         join user in _context.Users.AsNoTracking() on ownership.PrimaryUserId equals user.Id
                         where ownership.BusinessUnitId == businessUnitId
                               && ownership.CustomerId == rfq.CustomerId.Value
                               && ownership.IsActive && ownership.EffectiveTo == null
                         orderby ownership.Priority descending, ownership.EffectiveFrom descending
                         select (user.FirstName + " " + user.LastName).Trim()).FirstOrDefaultAsync()
                : null;
            var opportunityOwnerName = rfq.Lead?.AssignTo is long ownerId
                ? await _context.Users.AsNoTracking()
                    .Where(x => x.Buid == businessUnitId && x.Id == ownerId)
                    .Select(x => (x.FirstName + " " + x.LastName).Trim())
                    .SingleOrDefaultAsync()
                : null;
            var promotion = rfq.PromotionId.HasValue
                ? await _context.Set<RfqPromotion>().AsNoTracking()
                    .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == rfq.PromotionId.Value)
                : null;
            var participationVersion = rfq.ParticipationDecisionId.HasValue
                ? await _context.Set<LeadParticipationDecision>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.Id == rfq.ParticipationDecisionId.Value)
                    .Select(x => (int?)x.Sequence).SingleOrDefaultAsync()
                : null;
            var sourceLeadRevisionNumber = rfq.SourceLeadRevisionId.HasValue
                ? await _context.Set<LeadRevision>().AsNoTracking()
                    .Where(x => x.BusinessUnitId == businessUnitId && x.Id == rfq.SourceLeadRevisionId.Value)
                    .Select(x => (int?)x.RevisionNumber).SingleOrDefaultAsync()
                : null;

            return new RfqResponseDTO
            {
                Id = rfq.Id,
                // See the list projection above: the RFQ states its own case or states none.
                CommercialCaseId = rfq.CommercialCaseId,
                CommercialCaseReference = rfq.NexoraSerial,
                NexoraSerial = rfq.NexoraSerial,
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
                PromotionId = rfq.PromotionId,
                SourceLeadRevisionId = rfq.SourceLeadRevisionId,
                SourceLeadRevisionNumber = sourceLeadRevisionNumber,
                ParticipationDecisionId = rfq.ParticipationDecisionId,
                ParticipationVersion = participationVersion,
                PromotedAtUtc = promotion?.PromotedAtUtc,
                PromotedBy = promotion?.PromotedBy,
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
                ContactName = contactName,
                CustomerName = rfq.Customer != null ? rfq.Customer.Name : null,
                CustomerEmail = rfq.Customer != null ? rfq.Customer.ContactEmail : null,
                LeadEmail = rfq.Lead != null ? rfq.Lead.Clientemail : null,
                AccountOwnerName = accountOwnerName,
                OpportunityOwnerName = opportunityOwnerName,
                // The detail projection used to leave ItemCount at its default 0 while the list
                // projection above populated it. RfqResponseDTO.Readiness is derived from
                // ItemCount, so GET /api/Rfq/{id} answered "Review Required" for every RFQ that
                // has ever existed, including ones the list endpoint had just reported as ready.
                // The lines are already materialised by the Include above, so this costs nothing.
                ItemCount = rfq.Rfqitems.Count,
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
                    Aiconfidence = i.Aiconfidence,
                    SourceLeadItemRevisionId = i.SourceLeadItemRevisionId,
                    ParticipationDecision = i.ParticipationDecision,
                    NoQuoteReason = i.NoQuoteReason,
                    ParticipationDecidedBy = i.ParticipationDecidedBy,
                    ParticipationDecidedOn = i.ParticipationDecidedOn
                }).ToList()
            };
        }

        public async Task AddAsync(Rfq rfq)
        {
            await Task.CompletedTask;
            throw new InvalidOperationException(
                "Direct formal RFQ creation is retired. Create or reconcile a canonical Lead Revision, commit participation, and promote approved Bid lines through RFQ Promotion.");
#pragma warning disable CS0162
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

            if (rfq.LeadId.HasValue)
            {
                throw new InvalidOperationException(
                    "A lead-linked RFQ can only be created by RFQ Promotion after a committed current-revision participation decision. Remove LeadId for a standalone manual RFQ, or use the Lead participation workflow.");
            }

            // Manual "Create RFQ" without a lead. The serial-lineage invariant — every RFQ
            // belongs to a commercial case (Lead -> RFQ -> Quote share one Nexora Serial) —
            // is PRESERVED, not bypassed: a governed manual-origin shell lead is created in
            // the SAME transaction as the RFQ and the RFQ inherits its commercial identity.
            //
            // A customer is still required, because "an RFQ's lead has a resolved customer"
            // is an existing gate on BOTH creation paths (see the lead branch above and
            // LeadRepository.ConvertLeadToRfqAsync); a shell lead without a customer would
            // produce an RFQ no quote can ever be addressed to.
            if (!rfq.CustomerId.HasValue)
                throw new ArgumentException(
                    "Select a customer (or start from a lead) so the RFQ can be anchored to a commercial case.");

            var customerId = rfq.CustomerId.Value;
            var customerExists = await _context.Customers.AsNoTracking().AnyAsync(c =>
                c.Id == customerId && c.Buid == rfq.BusinessUnitId && c.IsActive != false);
            if (!customerExists)
                throw new ArgumentException("The selected customer was not found in this business unit.");

            // ExecuteAsync + an explicit transaction is the same pattern as
            // LeadRepository.ConvertLeadToRfqAsync: production configures a retrying
            // execution strategy (Program.cs EnableRetryOnFailure), which rejects
            // user-initiated transactions outside a strategy delegate.
            var strategy = _context.Database.CreateExecutionStrategy();
            await strategy.ExecuteAsync(async () =>
            {
                _context.ChangeTracker.Clear();
                await using var transaction = await _context.Database.BeginTransactionAsync();

                var lead = BuildShellLead(rfq);
                // "CUSTOMER_CONFIRMED": the creating user explicitly picked this customer;
                // the null ContactId says the contact is unresolved. (The review flow's
                // longer CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED literal does not fit the
                // production varchar(32) CustomerMatchStatus column.)
                lead.ResolveCommercialIdentity(customerId, null, "CUSTOMER_CONFIRMED");
                // Born already converted: this lead exists BECAUSE its RFQ exists. Seeding the
                // status at INSERT (like the RFQ's DRAFT below) keeps it out of the triage
                // queues (LeadStatusId == null means "new lead to review") and makes
                // ConvertLeadToRfqAsync idempotent for it. The lifecycle governance
                // interceptor only gates status CHANGES; the insert-time status is recorded
                // in LeadStatusHistories by the database trigger / SQLite fallback.
                lead.LeadStatusId = await LifecycleStatusCatalog.ResolveIdAsync(
                    _context, rfq.BusinessUnitId, "Lead", "CONVERTED_TO_RFQ");
                _context.Leads.Add(lead);
                // First save: PostgreSQL's TR_Leads_AssignCommercialCase trigger (or the
                // LeadPersistenceRules fallback on the relational-SQLite test lane) allocates
                // the commercial case, and EF reads the generated CommercialCaseId /
                // CommercialCaseReference back with the INSERT result.
                await _context.SaveChangesAsync();

                rfq.LeadId = lead.Id;
                rfq.InheritCommercialIdentity(lead);
                await PersistNewRfqAsync(rfq);

                await transaction.CommitAsync();
            });
#pragma warning restore CS0162
        }

        /// <summary>
        /// The lead-linked half of <see cref="AddAsync"/>: gates, creates, transitions and
        /// records the promotion atomically. Serializable, like ConvertLeadToRfqAsync — the
        /// existence check and the insert must not interleave with a concurrent conversion
        /// (and the RFQ."LeadID" partial unique index backstops whatever still slips through).
        /// </summary>
        private async Task AddForLeadAsync(Rfq rfq, long leadId)
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            try
            {
                await strategy.ExecuteAsync(async () =>
                {
                    _context.ChangeTracker.Clear();
                    await using var transaction = await _context.Database
                        .BeginTransactionAsync(System.Data.IsolationLevel.Serializable);

                    // Tenant scoping is enforced here, not by the caller: a LeadId from another
                    // business unit is indistinguishable from a LeadId that does not exist.
                    var lead = await _context.Leads
                        .Include(l => l.LeadStatus)
                        .SingleOrDefaultAsync(l => l.Id == leadId && l.BusinessUnitId == rfq.BusinessUnitId);
                    if (lead == null)
                        throw new ArgumentException($"Lead ID {leadId} does not exist in this business unit.");

                    // One lead, one RFQ. The conversion endpoints resolve a repeat to the
                    // existing RFQ; this door is an explicit "create" so a repeat is refused,
                    // naming the RFQ the caller should open instead.
                    var existing = await _context.Rfqs.AsNoTracking()
                        .FirstOrDefaultAsync(r => r.LeadId == leadId && r.BusinessUnitId == rfq.BusinessUnitId);
                    if (existing != null)
                        throw new InvalidOperationException(
                            $"Lead {leadId} was already converted to RFQ #{existing.Id} ({existing.Rfqno}). "
                            + "Open that RFQ instead of creating a second one for the same lead.");

                    LeadConversionGate.EnsureEligible(lead);

                    rfq.InheritCommercialIdentity(lead);
                    await PersistNewRfqAsync(rfq);

                    // The same governed transition + dedicated promotion event the conversion
                    // endpoints record, so a lead converted through this door reads identically
                    // in the lifecycle stream and never resurfaces in the accepted-leads queue.
                    var actor = new LifecycleActor(
                        string.IsNullOrWhiteSpace(rfq.CreatedBy) ? "System" : rfq.CreatedBy.Trim(),
                        "AuthenticatedUser");
                    var lifecycle = new LifecycleApplicationService(_context);
                    await lifecycle.TransitionLeadInCurrentTransactionAsync(
                        lead.BusinessUnitId, lead.Id, actor,
                        new LifecycleTransitionCommand("CONVERTED_TO_RFQ", lead.LifecycleVersion, null, null,
                            "Api", $"conversion-{lead.Id}", $"rfq-{rfq.Id}",
                            $"lead-conversion:{lead.BusinessUnitId}:{lead.Id}"), false, default);
                    await lifecycle.RecordLeadPromotedToRfqInCurrentTransactionAsync(
                        lead.BusinessUnitId, lead.Id, rfq.Id, actor, $"conversion-{lead.Id}", default);

                    await transaction.CommitAsync();
                });
            }
            catch (DbUpdateException ex) when (LeadConversionGate.IsDuplicateKey(ex))
            {
                // Lost the race against the RFQ."LeadID" partial unique index: a concurrent
                // conversion won. Same refusal as the pre-insert check above, so a race and a
                // repeat read identically to the caller instead of surfacing as a 500.
                _context.ChangeTracker.Clear();
                var winner = await _context.Rfqs.AsNoTracking()
                    .FirstOrDefaultAsync(r => r.LeadId == leadId && r.BusinessUnitId == rfq.BusinessUnitId);
                if (winner == null) throw; // Not our index after all — surface the truth.
                throw new InvalidOperationException(
                    $"Lead {leadId} was already converted to RFQ #{winner.Id} ({winner.Rfqno}). "
                    + "Open that RFQ instead of creating a second one for the same lead.");
            }
        }

        /// <summary>
        /// Shared tail of both creation paths: server-authoritative DRAFT status, the
        /// server-generated RFQ number, and the insert itself.
        /// </summary>
        private async Task PersistNewRfqAsync(Rfq rfq)
        {
            rfq.RfqstatusId = await LifecycleStatusCatalog.ResolveIdAsync(
                _context, rfq.BusinessUnitId, "Rfq", "DRAFT");

            var sequence = await NextRfqSequenceAsync();
            rfq.Rfqno = $"NXR-RFQ-{rfq.BusinessUnitId}-{DateTime.UtcNow:yyyy}-{sequence:D8}";

            rfq.CreatedDate = DateTime.UtcNow;

            throw new InvalidOperationException(
                "Retired generic RFQ persistence path reached. Use the RFQ Promotion service.");
        }

        private async Task<long> NextRfqSequenceAsync()
        {
            if (_context.Database.IsNpgsql())
                return await _context.Database.SqlQueryRaw<long>(
                    "SELECT nextval('public.nexora_rfq_number_seq') AS \"Value\"").SingleAsync();

            // Relational-SQLite test lane has no PostgreSQL sequences. Mirror the sequence's
            // monotonic high-water behaviour (same approach as LeadPersistenceRules for the
            // commercial-case allocation): one more than the highest issued numeric suffix.
            var issuedNumbers = await _context.Rfqs.IgnoreQueryFilters().AsNoTracking()
                .Select(r => r.Rfqno).ToListAsync();
            var highWater = 0L;
            foreach (var issued in issuedNumbers)
            {
                var match = System.Text.RegularExpressions.Regex.Match(issued ?? string.Empty, "([0-9]+)$");
                if (match.Success && long.TryParse(match.Groups[1].Value, out var value) && value > highWater)
                    highWater = value;
            }
            return highWater + 1;
        }

        /// <summary>
        /// The governed manual-origin lead that anchors a leadless "Create RFQ" to a
        /// commercial case. It mirrors the commercial header facts the user typed, marks
        /// its provenance ("manual-rfq", the creating actor, no AI confidence), and needs
        /// no extraction review (RequiresCommercialReview=false: the facts are
        /// human-entered, not AI-extracted). Line items stay on the RFQ — the lead is the
        /// lineage anchor, not a copy of the document.
        /// </summary>
        private static Lead BuildShellLead(Rfq rfq) => new()
        {
            BuyersName = Truncate(rfq.BuyersName, 510),
            RecDate = rfq.RecDate == default ? DateTime.UtcNow : rfq.RecDate,
            BidClosingDate = rfq.BidClosingDate,
            AcknowledgmentDate = rfq.AcknowledgmentDate,
            SubDate = rfq.SubDate,
            HeaderRemarks = rfq.HeaderRemarks,
            OpportunityNo = Truncate(rfq.OpportunityNo, 100),
            NoOfLineItems = rfq.Rfqitems?.Count ?? rfq.NoOfLineItems ?? 0,
            Rfqtype = Truncate(rfq.Rfqtype, 50),
            DurationAgreement = Truncate(rfq.DurationAgreement, 100),
            LeadSource = "manual-rfq",
            Aiconfidence = null,
            ReviewVersion = 1,
            RequiresCommercialReview = false,
            CommercialFactsVerified = false,
            // Leads.CreatedBy is varchar(20) in the production schema; the full actor
            // identity is preserved on the RFQ itself (CreatedBy varchar(40)).
            CreatedBy = Truncate(string.IsNullOrWhiteSpace(rfq.CreatedBy) ? "System" : rfq.CreatedBy, 20)!,
            CreatedDate = DateTime.UtcNow,
            BusinessUnitId = rfq.BusinessUnitId,
            EmailIngestsId = null
        };

        private static string? Truncate(string? value, int maxLength)
            => value is null || value.Length <= maxLength ? value : value[..maxLength];

        public async Task UpdateAsync(Rfq rfq)
        {
            var existing = await _context.Rfqs.FirstOrDefaultAsync(r => r.Id == rfq.Id);

            if (existing == null)
                throw new KeyNotFoundException($"RFQ with ID {rfq.Id} not found.");

            if (existing.BusinessUnitId != rfq.BusinessUnitId)
                throw new ArgumentException("Cannot change the Business Unit of an RFQ.");

            // Update fields
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

        /// <summary>
        /// The quote number this path has always issued — <c>QT-</c> plus the RFQ number — held
        /// inside the column it is written to.
        ///
        /// <para>Rfq.Rfqno is varchar(200) and Quote.QuoteNo is varchar(50), and nothing sat
        /// between them. That is not a theoretical overflow: the conversion paths copy the
        /// BUYER'S OWN reference into Rfqno verbatim, and the manual-upload door builds
        /// <c>RFQ_{filename}_{timestamp}</c>, so a 35-character filename or an industrial buyer's
        /// long purchase reference produced a 50+ character quote number and Postgres refused the
        /// INSERT with "value too long". The failure landed inside approve-RFQ, which creates the
        /// quote and mails it in one request, so the whole approval died on a string length.</para>
        ///
        /// <para>The TAIL is kept rather than the head. Everything that discriminates one of these
        /// numbers from another is at the end — the collision suffix the conversion paths append,
        /// the upload timestamp, the sequence in NXR-RFQ-{buid}-{yyyy}-{seq}. Truncating from the
        /// front would map every long reference sharing a prefix onto one number; truncating from
        /// the back keeps them apart. Numbers that fit today are returned byte-for-byte unchanged,
        /// so this alters no existing document's identity. It is a length guard, not a numbering
        /// scheme — how documents are numbered is a product decision this method does not make.</para>
        /// </summary>
        internal static string QuoteNumberFromRfq(string? rfqno)
        {
            const int QuoteNoMaxLength = 50; // Quotes.QuoteNo, varchar(50)
            const string Prefix = "QT-";

            var reference = (rfqno ?? string.Empty).Trim();
            var candidate = Prefix + reference;
            if (candidate.Length <= QuoteNoMaxLength) return candidate;

            var keep = QuoteNoMaxLength - Prefix.Length;
            return Prefix + reference.Substring(reference.Length - keep);
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

            // FX fix (source of the corruption): the quote header currency used to be sampled
            // from an ARBITRARY line — `rfq.Rfqitems.FirstOrDefault()?.CurrencyId` — and every
            // line was then summed into Quote.TotalAmount regardless of its own currency. On a
            // mixed-currency RFQ that wrote a header currency true of at most one line and a
            // total true of none, and every downstream aggregate (quote stats, order revenue,
            // margin floors, award scoring) inherited it. This is the write that manufactured
            // the corrupt state, so it is fixed here rather than papered over on the read side.
            //
            // Rfqitem carries the only currency evidence in this graph: Rfq has no header
            // currency column and QuoteItem has no currency column at all. The header currency
            // is therefore DERIVED from unanimous line evidence, never sampled, and a disagreeing
            // RFQ fails closed. Converting the lines instead is not an option — QuoteItem cannot
            // record what currency a converted price is in, so conversion would silently restate
            // the prices the customer is quoted.
            var headerCurrencyId = await ResolveQuoteHeaderCurrencyAsync(rfq);

            // QUANTITY GATE. This method does not just create a Quote — RfqController.ApproveAsync
            // calls SendQuoteEmailAsync in the SAME request, and QuoteDeliveryWorker then mails the
            // PDF unattended. Nothing between this line and the customer's inbox displays a
            // quantity, so this is the last place a bad one can be stopped.
            //
            // A quantity of 0 means "never established" (the ingestion doors write 0 when the
            // source had no readable quantity). Quoting it would tell the customer we are
            // offering zero of what they asked for; historically the Excel door wrote 1 instead,
            // which is worse because it looks deliberate. Fail closed and name the lines.
            var unquotableLines = rfq.Rfqitems
                .Where(i => i.Quantity <= 0)
                .Select(i => i.ProductShortName ?? i.ProductShortDescription ?? $"line {i.Id}")
                .ToArray();

            if (unquotableLines.Length > 0)
            {
                throw new InvalidOperationException(
                    "Cannot approve: no quantity was established for " +
                    $"{string.Join(", ", unquotableLines)}. Confirm the quantity in extraction review before approving — " +
                    "approving sends the quote to the customer.");
            }

            // Create Quote
            var quote = new Quote
            {
                QuoteNo = QuoteNumberFromRfq(rfq.Rfqno),
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
                CurrencyId = headerCurrencyId,
                FinancialCalculationVersion = 2,
                QuoteItems = rfq.Rfqitems.Select(i => new QuoteItem
                {
                    RfqitemId = i.Id,
                    ProductId = i.ProductId,
                    ItemDescription = i.ProductShortDescription ?? i.ProductShortName ?? i.ItemText,
                    Quantity = i.Quantity!.Value,
                    // Carry the unit and the buyer's own line reference onto the quote line —
                    // the printed document must say what "Qty 500" is 500 OF, and must let the
                    // buyer match our line back to their RFQ line (SAP "00010", "OPT-29", …).
                    UnitOfMeasure = i.UnitOfMeasure,
                    CustomerLineRef = i.LineItemNo,
                    UnitPrice = i.UnitPrice ?? 0,
                    TotalAmount = i.Quantity!.Value * (i.UnitPrice ?? 0),
                    CreatedBy = approvedBy,
                    CreatedDate = DateTime.UtcNow
                }).ToList()
            };
            quote.InheritCommercialIdentity(rfq);

            // Safe to add: ResolveQuoteHeaderCurrencyAsync has already established that every
            // line is denominated in `headerCurrencyId` (or that no line declares a currency at
            // all, in which case the header is left NULL and no reader can claim one).
            quote.TotalAmount = quote.QuoteItems.Sum(i => i.TotalAmount);

            _context.Quotes.Add(quote);
            await _context.SaveChangesAsync();

            return quote.Id;
        }

        /// <summary>
        /// The single currency every RFQ line agrees on, or null when NO line carries any
        /// currency evidence — that state is unchanged from before this fix and claims nothing.
        ///
        /// Throws when the lines disagree, or when only some of them declare a currency. Those
        /// are exactly the two cases where a single header currency and a single summed total
        /// would both be false, and the schema has nowhere to record the truth.
        /// </summary>
        private async Task<long?> ResolveQuoteHeaderCurrencyAsync(Rfq rfq)
        {
            var lines = rfq.Rfqitems.ToList();
            if (lines.Count == 0) return null;

            // The FK is authoritative; the free-text Rfqitem.Currency code is accepted as a
            // fallback because extracted and legacy lines often carry only that. A code is
            // admissible only when it resolves to exactly one ACTIVE currency in THIS business
            // unit — an ambiguous code stays unresolved rather than picking a row.
            var idByCode = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (lines.Any(i => i.CurrencyId == null && !string.IsNullOrWhiteSpace(i.Currency)))
            {
                var currencies = await _context.Currencies.AsNoTracking()
                    .Where(c => c.BusinessUnitId == rfq.BusinessUnitId && c.IsActive == true)
                    .Select(c => new { c.Id, c.Code })
                    .ToListAsync();
                foreach (var group in currencies.GroupBy(c => c.Code.Trim(), StringComparer.OrdinalIgnoreCase))
                    if (group.Count() == 1)
                        idByCode[group.Key] = group.Single().Id;
            }

            long? Resolve(Rfqitem item)
            {
                if (item.CurrencyId.HasValue) return item.CurrencyId.Value;
                var code = item.Currency?.Trim();
                return !string.IsNullOrEmpty(code) && idByCode.TryGetValue(code, out var id) ? id : null;
            }

            var resolved = lines.Select(Resolve).ToList();
            var declared = resolved.Where(id => id.HasValue).Select(id => id!.Value).Distinct().ToList();

            // Nothing anywhere declares a currency: there is no currency to get wrong. The quote
            // keeps a NULL header currency so no downstream reader can assert one.
            if (declared.Count == 0) return null;

            if (declared.Count > 1)
                throw new InvalidOperationException(
                    $"RFQ {rfq.Rfqno} prices its lines in {declared.Count} different currencies " +
                    $"({string.Join(", ", await CurrencyLabelsAsync(rfq.BusinessUnitId, declared))}). " +
                    "A quote carries one header currency and one total, so the RFQ lines must be " +
                    "normalised to a single currency, or quoted separately, before a quote can be generated.");

            var undeclared = resolved.Count(id => id is null);
            if (undeclared > 0)
                throw new InvalidOperationException(
                    $"RFQ {rfq.Rfqno} prices {undeclared} of {lines.Count} line(s) in no recognised currency " +
                    $"while the rest are in {string.Join(", ", await CurrencyLabelsAsync(rfq.BusinessUnitId, declared))}. " +
                    "Set a currency on every line before a quote can be generated, so the quote total is not a " +
                    "sum of unlike amounts.");

            return declared[0];
        }

        /// <summary>Human-readable ISO codes for an id set, for use in fail-closed messages.</summary>
        private async Task<IReadOnlyList<string>> CurrencyLabelsAsync(long businessUnitId, IReadOnlyCollection<long> ids)
        {
            var codes = await _context.Currencies.AsNoTracking()
                .Where(c => c.BusinessUnitId == businessUnitId && ids.Contains(c.Id))
                .Select(c => new { c.Id, c.Code })
                .ToListAsync();
            var byId = codes.GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Code);
            return ids.OrderBy(id => id)
                .Select(id => byId.TryGetValue(id, out var code) ? code : $"currency #{id}")
                .ToList();
        }

        public async Task DeleteAsync(long id, long businessUnitId)
        {
            var rfq = await _context.Rfqs
                .Include(r => r.Rfqitems)
                .Include(r => r.Rfqstatus)
                .FirstOrDefaultAsync(r => r.Id == id && r.BusinessUnitId == businessUnitId);

            if (rfq is null)
                throw new KeyNotFoundException($"RFQ with ID {id} was not found in Business Unit {businessUnitId}.");

            RfqDeletionGovernance.EnsureHardDeletable(rfq);
            _context.Rfqitems.RemoveRange(rfq.Rfqitems);
            _context.Rfqs.Remove(rfq);
            await _context.SaveChangesAsync();
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

        public async Task<RfqStatsDTO> GetRfqStatsAsync(long businessUnitId, AccountTeamScope? accessScope = null)
        {
            var query = _context.Rfqs
                .AsNoTracking()
                .Where(r => r.BusinessUnitId == businessUnitId);
            if (accessScope != null)
                query = query.InCommercialScope(_context, businessUnitId, accessScope, DateTime.UtcNow);
            var rfqs = await query.ToListAsync();

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
