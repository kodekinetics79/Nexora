using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.CommercialIntelligence.Growth;

public sealed class GrowthIntelligenceService(ErpRfqAutomationContext context)
{
    private const int MaxPeriodDays = 366;
    private const int MaxRowsPerSource = 10_000;

    public async Task<CoachingRecoveryWorkspace> GetCoachingRecoveryWorkspaceAsync(long businessUnitId,
        DateTime periodFromUtc, DateTime periodToUtc, DateTime asOfUtc, GrowthAccessScope access,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        ValidatePeriod(periodFromUtc, periodToUtc, asOfUtc);
        if (!access.IsManager && access.RequestedSalesRepUserId is not null &&
            access.RequestedSalesRepUserId != access.UserId)
            throw new UnauthorizedAccessException("Sales representatives may only view their own coaching evidence.");

        var repScope = access.EffectiveRepId;
        var leads = await context.Leads.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.CreatedDate <= asOfUtc)
            .OrderByDescending(x => x.CreatedDate).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var leadIds = leads.Select(x => x.Id).ToArray();
        var assignments = await context.Set<LeadAssignment>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && leadIds.Contains(x.LeadId) &&
                x.EffectiveFrom <= asOfUtc && (x.EffectiveTo == null || x.EffectiveTo >= periodFromUtc))
            .OrderBy(x => x.EffectiveFrom).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var rfqs = await context.Rfqs.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.RecDate <= asOfUtc)
            .OrderByDescending(x => x.RecDate).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var rfqIds = rfqs.Select(x => x.Id).ToArray();
        var rfqLines = await context.Rfqitems.AsNoTracking().Where(x => rfqIds.Contains(x.Rfqid))
            .OrderBy(x => x.Id).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var quotes = await context.Quotes.AsNoTracking().Include(x => x.Status)
            .Where(x => x.BusinessUnitId == businessUnitId && x.CreatedDate <= asOfUtc &&
                (x.Rfqid == null || rfqIds.Contains(x.Rfqid.Value)))
            .OrderByDescending(x => x.CreatedDate).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var quoteIds = quotes.Select(x => x.Id).ToArray();
        var customerAwardOrders = await context.Orders.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.SourceType == OrderSourceTypes.CustomerAward &&
                x.CurrencyId.HasValue && x.OrderDate <= asOfUtc)
            .OrderByDescending(x => x.OrderDate).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var activities = await context.CommercialActivities.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.OccurredAtUtc <= asOfUtc)
            .OrderByDescending(x => x.OccurredAtUtc).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var followUps = await context.FollowUpTasks.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.CreatedAtUtc <= asOfUtc)
            .OrderByDescending(x => x.CreatedAtUtc).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var contributions = await context.SalesContributions.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.RecognizedAtUtc <= asOfUtc)
            .OrderByDescending(x => x.RecognizedAtUtc).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var sourcingCases = await context.SourcingCases.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && rfqIds.Contains(x.RfqId))
            .OrderByDescending(x => x.UpdatedOn).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var sourcingCaseIds = sourcingCases.Select(x => x.Id).ToArray();
        var sourcingDecisions = await context.CustomerQuoteSourcingDecisions.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && sourcingCaseIds.Contains(x.SourcingCaseId))
            .OrderByDescending(x => x.CreatedOn).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var solicitations = await context.Set<SupplierSolicitation>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && rfqIds.Contains(x.RfqId))
            .OrderByDescending(x => x.UpdatedOn).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var awards = await context.Set<SourcingAward>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && rfqIds.Contains(x.RfqId))
            .OrderByDescending(x => x.CreatedOn).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var supplierQuotes = await context.SupplierQuotes.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && rfqIds.Contains(x.RfqId))
            .OrderByDescending(x => x.UpdatedOn).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var supplierQuoteIds = supplierQuotes.Select(x => x.Id).ToArray();
        var supplierRevisions = await context.SupplierQuoteRevisions.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && supplierQuoteIds.Contains(x.SupplierQuoteId))
            .OrderByDescending(x => x.CapturedOn).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var lifecycle = await context.CommercialLifecycleEvents.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId &&
                ((x.AggregateType == "Rfq" && rfqIds.Contains(x.AggregateId)) ||
                 (x.AggregateType == "Quote" && quoteIds.Contains(x.AggregateId))))
            .OrderByDescending(x => x.OccurredOn).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var reasonIds = quotes.Where(x => x.OutcomeReasonId.HasValue).Select(x => x.OutcomeReasonId!.Value)
            .Distinct().ToArray();
        var reasons = await context.SetupMasters.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && reasonIds.Contains(x.SetupId))
            .ToDictionaryAsync(x => x.SetupId, x => x.SetupCode ?? x.SetupValue, cancellationToken);

        var ownerByLead = assignments.GroupBy(x => x.LeadId).ToDictionary(x => x.Key,
            x => x.OrderByDescending(a => a.EffectiveFrom).First().ToUserId);
        foreach (var lead in leads.Where(x => x.AssignTo.HasValue && !ownerByLead.ContainsKey(x.Id)))
            ownerByLead[lead.Id] = lead.AssignTo!.Value;
        var ownerByRfq = rfqs.Where(x => x.LeadId.HasValue && ownerByLead.ContainsKey(x.LeadId.Value))
            .ToDictionary(x => x.Id, x => ownerByLead[x.LeadId!.Value]);
        var latestRfqState = lifecycle.Where(x => x.AggregateType == "Rfq").GroupBy(x => x.AggregateId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(e => e.AggregateVersion)
                .ThenByDescending(e => e.OccurredOn).First());
        var latestQuoteState = lifecycle.Where(x => x.AggregateType == "Quote").GroupBy(x => x.AggregateId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(e => e.AggregateVersion)
                .ThenByDescending(e => e.OccurredOn).First());

        var findings = BuildCoachingFindings(businessUnitId, periodFromUtc, periodToUtc, asOfUtc, repScope,
            leads, assignments, rfqs, rfqLines, quotes, activities, followUps, contributions, sourcingCases,
            sourcingDecisions, ownerByLead, ownerByRfq, latestRfqState, reasons);
        var recoveries = BuildRecoveryRows(businessUnitId, periodFromUtc, asOfUtc, repScope, rfqs, rfqLines,
            quotes, customerAwardOrders, activities, followUps, sourcingCases, solicitations, awards, supplierQuotes,
            supplierRevisions, latestRfqState, latestQuoteState, ownerByRfq);

        var findingKeys = findings.Select(x => x.FindingKey).ToArray();
        var acknowledgements = findingKeys.Length == 0
            ? []
            : await context.Set<SalesCoachingAcknowledgement>().AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId && findingKeys.Contains(x.FindingKey))
                .OrderByDescending(x => x.CreatedAtUtc).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var latestAcknowledgements = acknowledgements.GroupBy(x => x.FindingKey).ToDictionary(x => x.Key,
            x => ToView(x.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).First()));
        findings = findings.Select(x => x with
            { LatestAcknowledgement = latestAcknowledgements.GetValueOrDefault(x.FindingKey) }).ToArray();

        var incompleteSources = IncompleteSources(("Leads", leads.Length), ("Lead assignments", assignments.Length),
            ("RFQs", rfqs.Length), ("RFQ lines", rfqLines.Length), ("Quotes", quotes.Length),
            ("Customer awards", customerAwardOrders.Length), ("Commercial activities", activities.Length),
            ("Follow-ups", followUps.Length), ("Sales contributions", contributions.Length),
            ("Sourcing cases", sourcingCases.Length), ("Sourcing decisions", sourcingDecisions.Length),
            ("Supplier solicitations", solicitations.Length), ("Sourcing awards", awards.Length),
            ("Supplier quotes", supplierQuotes.Length), ("Supplier quote revisions", supplierRevisions.Length),
            ("Lifecycle events", lifecycle.Length), ("Acknowledgements", acknowledgements.Length));
        return new CoachingRecoveryWorkspace(GrowthIntelligencePolicy.Version, periodFromUtc, periodToUtc,
            asOfUtc, repScope, findings, recoveries, new(incompleteSources.Length == 0, incompleteSources));
    }

    public async Task<CustomerHealth> GetCustomerHealthAsync(long businessUnitId, long customerId,
        DateTime periodFromUtc, DateTime periodToUtc, DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        ValidatePeriod(periodFromUtc, periodToUtc, asOfUtc);
        var customer = await context.Customers.AsNoTracking().SingleOrDefaultAsync(
            x => x.Buid == businessUnitId && x.Id == customerId, cancellationToken)
            ?? throw new KeyNotFoundException("Customer was not found in this tenant.");
        var duration = periodToUtc - periodFromUtc;
        var previousFrom = periodFromUtc - duration;
        var rfqs = await context.Rfqs.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.CustomerId == customerId && x.RecDate >= previousFrom && x.RecDate <= asOfUtc)
            .OrderByDescending(x => x.RecDate).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var rfqIds = rfqs.Select(x => x.Id).ToArray();
        var latestRfqStates = await context.CommercialLifecycleEvents.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.AggregateType == "Rfq" &&
                rfqIds.Contains(x.AggregateId))
            .OrderByDescending(x => x.AggregateVersion).ThenByDescending(x => x.OccurredOn)
            .Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var latestRfqState = latestRfqStates.GroupBy(x => x.AggregateId).ToDictionary(x => x.Key,
            x => x.First());
        var rfqLines = await context.Rfqitems.AsNoTracking().Where(x => rfqIds.Contains(x.Rfqid))
            .OrderBy(x => x.Id).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var quotes = await context.Quotes.AsNoTracking().Include(x => x.Status).Where(x => x.BusinessUnitId == businessUnitId &&
            x.CustomerId == customerId && x.CreatedDate <= asOfUtc)
            .OrderByDescending(x => x.CreatedDate).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var quoteIds = quotes.Select(x => x.Id).ToArray();
        var quoteItems = await context.QuoteItems.AsNoTracking().Where(x => quoteIds.Contains(x.QuoteId))
            .OrderBy(x => x.Id).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var orders = await context.Orders.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.CustomerId == customerId && x.OrderDate <= asOfUtc)
            .OrderByDescending(x => x.OrderDate).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var currencyIds = quotes.Where(x => x.CurrencyId.HasValue).Select(x => x.CurrencyId!.Value)
            .Concat(orders.Where(x => x.CurrencyId.HasValue).Select(x => x.CurrencyId!.Value)).Distinct().ToArray();
        var currencies = await context.Currencies.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            currencyIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, cancellationToken);
        var leadIds = rfqs.Where(x => x.LeadId.HasValue).Select(x => x.LeadId!.Value).Distinct().ToArray();
        var leadRevisionCount = await context.Set<LeadRevision>().AsNoTracking().CountAsync(x =>
            x.BusinessUnitId == businessUnitId && leadIds.Contains(x.LeadId), cancellationToken);
        var revisionDifferences = await context.Set<LeadRevisionDifference>().AsNoTracking().Where(x =>
                x.BusinessUnitId == businessUnitId && leadIds.Contains(x.Revision.LeadId))
            .GroupBy(x => x.Scope).Select(group => new
            {
                Scope = group.Key,
                Compared = group.Count(),
                Changed = group.Count(x => x.ChangeType != LeadRevisionChangeType.Unchanged)
            }).ToArrayAsync(cancellationToken);
        var followUps = await context.FollowUpTasks.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.CustomerId == customerId && x.CreatedAtUtc <= asOfUtc)
            .OrderByDescending(x => x.CreatedAtUtc).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var activities = await context.CommercialActivities.AsNoTracking().Where(x =>
            x.BusinessUnitId == businessUnitId && x.CustomerId == customerId && x.OccurredAtUtc <= asOfUtc)
            .OrderByDescending(x => x.OccurredAtUtc).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        var assignments = await context.Set<LeadAssignment>().AsNoTracking().Where(x =>
            x.BusinessUnitId == businessUnitId && leadIds.Contains(x.LeadId) && x.EffectiveFrom <= asOfUtc)
            .OrderByDescending(x => x.EffectiveFrom).Take(MaxRowsPerSource).ToArrayAsync(cancellationToken);
        // Inquiries this customer sent that ended before a quotation existed. They are decided
        // commercial outcomes and belong in the conversion denominator alongside decided quotes.
        var leadStageLosses = await context.Leads.AsNoTracking().CountAsync(x =>
            x.BusinessUnitId == businessUnitId && x.CustomerId == customerId &&
            x.OutcomeOn.HasValue && x.OutcomeOn >= periodFromUtc && x.OutcomeOn < periodToUtc,
            cancellationToken);

        var currentRfqs = rfqs.Where(x => x.RecDate >= periodFromUtc && x.RecDate < periodToUtc).ToArray();
        var previousRfqs = rfqs.Where(x => x.RecDate >= previousFrom && x.RecDate < periodFromUtc).ToArray();
        var currentRfqIds = currentRfqs.Select(x => x.Id).ToHashSet();
        var eligibleRfqIds = currentRfqs.Where(x => GrowthIntelligenceRules.IsViableRfq(x, rfqLines,
            latestRfqState.GetValueOrDefault(x.Id)?.NewStatusCode, asOfUtc)).Select(x => x.Id).ToHashSet();
        var quotedRfqIds = quotes.Where(x => x.Rfqid.HasValue && eligibleRfqIds.Contains(x.Rfqid.Value))
            .Select(x => x.Rfqid!.Value).Distinct().ToHashSet();
        var currentQuotes = quotes.Where(x => x.CreatedDate >= periodFromUtc && x.CreatedDate < periodToUtc).ToArray();
        var wonQuoteIds = orders.Where(x => x.SourceType == OrderSourceTypes.CustomerAward && x.QuoteId.HasValue)
            .Select(x => x.QuoteId!.Value).ToHashSet();
        var decided = currentQuotes.Where(x => x.OutcomeOn.HasValue || wonQuoteIds.Contains(x.Id)).ToArray();
        var won = decided.Where(x => wonQuoteIds.Contains(x.Id) || IsExplicitWonQuote(x)).ToArray();

        var acceptedPrices = won.Where(x => x.CurrencyId.HasValue).SelectMany(quote =>
        {
            var order = orders.Where(x => x.QuoteId == quote.Id && x.SourceType == OrderSourceTypes.CustomerAward)
                .OrderBy(x => x.OrderDate).FirstOrDefault();
            var acceptedAt = order?.OrderDate ?? quote.OutcomeOn;
            if (!acceptedAt.HasValue) return [];
            var currencyId = quote.CurrencyId!.Value;
            return quoteItems.Where(x => x.QuoteId == quote.Id).Select(item => new AcceptedPriceEvidence(
                quote.Id, item.Id, quote.QuoteNo, currencyId,
                currencies.GetValueOrDefault(currencyId) ?? $"Currency {currencyId}", item.Quantity,
                item.UnitPrice, acceptedAt.Value, order is null ? "QuoteOutcome" : "CustomerAward",
                order?.Id ?? quote.Id, GrowthIntelligenceRules.Route("Quote", quote.Id)));
        }).OrderByDescending(x => x.AcceptedAtUtc).Take(50).ToArray();

        var due = followUps.Where(x => x.Status is FollowUpStatus.Open or FollowUpStatus.InProgress &&
            x.DueAtUtc < periodToUtc).ToArray();
        var completed = followUps.Where(x => x.Status == FollowUpStatus.Completed &&
            x.UpdatedAtUtc >= periodFromUtc && x.UpdatedAtUtc < periodToUtc).ToArray();
        var responded = completed.Count(task => activities.Any(activity =>
            activity.ActivityType == CommercialActivityType.CustomerResponded &&
            activity.OccurredAtUtc >= task.CreatedAtUtc && activity.OccurredAtUtc <= task.UpdatedAtUtc));
        var followUpHealth = new FollowUpHealth(due.Length, due.Count(x => x.DueAtUtc < asOfUtc), completed.Length,
            responded, GrowthIntelligenceRules.Percent(responded, completed.Length));

        var commercialDates = activities.Select(x => (DateTime?)x.OccurredAtUtc)
            .Concat(rfqs.Select(x => (DateTime?)x.RecDate))
            .Concat(quotes.Select(x => x.SentOn ?? x.OutcomeOn ?? x.CreatedDate))
            .Concat(orders.Select(x => (DateTime?)x.OrderDate)).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var lastActivity = commercialDates.Length == 0 ? (DateTime?)null : commercialDates.Max();
        var quoteCoverage = GrowthIntelligenceRules.Percent(quotedRfqIds.Count, eligibleRfqIds.Count);
        var conversion = GrowthIntelligenceRules.Percent(won.Length, decided.Length);
        var decidedInquiries = decided.Length + leadStageLosses;
        var inquiryConversion = GrowthIntelligenceRules.Percent(won.Length, decidedInquiries);
        var opportunities = currentRfqs.Where(x =>
                (!latestRfqState.TryGetValue(x.Id, out var state) ||
                    !GrowthIntelligenceRules.IsTerminalRfqStatus(state.NewStatusCode)) &&
                (!x.BidClosingDate.HasValue || x.BidClosingDate >= asOfUtc))
            .Select(x => new AccountOpportunity(x.Id, x.Rfqno, x.NexoraSerial,
                assignments.Where(a => a.LeadId == x.LeadId && a.EffectiveTo == null)
                    .OrderByDescending(a => a.EffectiveFrom).Select(a => (long?)a.ToUserId).FirstOrDefault(),
                x.RecDate, x.BidClosingDate, quotes.Any(q => q.Rfqid == x.Id),
                GrowthIntelligenceRules.Route("Rfq", x.Id)))
            .OrderBy(x => x.DecisionDeadlineUtc ?? DateTime.MaxValue).Take(50).ToArray();
        var assessment = GrowthIntelligenceRules.AssessCustomerHealth(asOfUtc, lastActivity, followUpHealth,
            quoteCoverage, inquiryConversion, decidedInquiries);
        var nextAction = GrowthIntelligenceRules.SelectNextCustomerAction(customerId, opportunities, followUps,
            asOfUtc);
        var quoteRevisionCount = quotes.Count(x => x.RevisionNo > 1);
        var fieldDifferences = revisionDifferences.SingleOrDefault(x => x.Scope == "Field");
        var lineDifferences = revisionDifferences.SingleOrDefault(x => x.Scope == "Line");
        var incompleteSources = IncompleteSources(("RFQs", rfqs.Length), ("RFQ lifecycle events", latestRfqStates.Length),
            ("RFQ lines", rfqLines.Length), ("Quotes", quotes.Length), ("Quote lines", quoteItems.Length),
            ("Orders", orders.Length), ("Follow-ups", followUps.Length), ("Commercial activities", activities.Length),
            ("Lead assignments", assignments.Length));

        return new CustomerHealth(GrowthIntelligencePolicy.Version, customerId, customer.Name, asOfUtc,
            new PeriodComparison(periodFromUtc, periodToUtc, previousFrom, periodFromUtc),
            new TrendMetric(currentRfqs.Length, previousRfqs.Length,
                GrowthIntelligenceRules.TrendPercent(currentRfqs.Length, previousRfqs.Length)),
            new QuoteFunnelMetric(eligibleRfqIds.Count, quotedRfqIds.Count, quoteCoverage, decided.Length,
                won.Length, conversion, leadStageLosses, inquiryConversion), acceptedPrices,
            new MarginAvailability("unavailable", null, 0,
                "No immutable quote-time landed-cost evidence is linked to accepted customer quote lines."),
            new RevisionBurden(leadRevisionCount, fieldDifferences?.Changed ?? 0, fieldDifferences?.Compared ?? 0,
                lineDifferences?.Changed ?? 0, lineDifferences?.Compared ?? 0, quoteRevisionCount,
                GrowthIntelligenceRules.Percent(fieldDifferences?.Changed ?? 0, fieldDifferences?.Compared ?? 0),
                GrowthIntelligenceRules.Percent(lineDifferences?.Changed ?? 0, lineDifferences?.Compared ?? 0)),
            followUpHealth, lastActivity, assessment, opportunities, nextAction,
            new(incompleteSources.Length == 0, incompleteSources));
    }

    public async Task<SalesCoachingAcknowledgement> AcknowledgeFindingAsync(long businessUnitId,
        AcknowledgeCoachingFindingCommand command, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        if (command.ManagerUserId <= 0) throw new ArgumentException("A manager user is required.", nameof(command));
        var managerExists = await context.Users.AsNoTracking().AnyAsync(x => x.Id == command.ManagerUserId &&
            x.Buid == businessUnitId && x.IsActive != false, cancellationToken);
        if (!managerExists) throw new UnauthorizedAccessException("The manager is not active in this tenant.");

        var workspace = await GetCoachingRecoveryWorkspaceAsync(businessUnitId, command.PeriodFromUtc,
            command.PeriodToUtc, command.AsOfUtc,
            new GrowthAccessScope(command.ManagerUserId, true, command.SalesRepUserId), cancellationToken);
        var currentFinding = workspace.CoachingFindings.SingleOrDefault(x => x.FindingKey == command.FindingKey)
            ?? throw new InvalidOperationException("The coaching finding is stale or no longer supported by current evidence.");
        var requestHash = GrowthIntelligenceRules.AcknowledgementRequestHash(command, currentFinding);
        var existing = await context.Set<SalesCoachingAcknowledgement>().SingleOrDefaultAsync(x =>
            x.BusinessUnitId == businessUnitId && x.IdempotencyKey == command.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(existing.RequestHash),
                    Convert.FromHexString(requestHash)))
                throw new InvalidOperationException("The idempotency key was already used for a different manager decision.");
            return existing;
        }

        var acknowledgement = GrowthIntelligenceRules.CreateAcknowledgement(businessUnitId, command,
            currentFinding, requestHash);
        context.Set<SalesCoachingAcknowledgement>().Add(acknowledgement);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (GrowthIntelligenceRules.IsIdempotencyUniqueViolation(exception))
        {
            context.Entry(acknowledgement).State = EntityState.Detached;
            var replay = await context.Set<SalesCoachingAcknowledgement>().AsNoTracking().SingleAsync(x =>
                x.BusinessUnitId == businessUnitId && x.IdempotencyKey == command.IdempotencyKey.Trim(),
                cancellationToken);
            if (!GrowthIntelligenceRules.IsIdempotentReplay(replay, requestHash))
                throw new InvalidOperationException("The idempotency key was already used for another decision.");
            return replay;
        }
        return acknowledgement;
    }

    private static CoachingFinding[] BuildCoachingFindings(long businessUnitId, DateTime periodFromUtc,
        DateTime periodToUtc, DateTime asOfUtc, long? repScope, IReadOnlyCollection<Lead> leads,
        IReadOnlyCollection<LeadAssignment> assignments, IReadOnlyCollection<Rfq> rfqs,
        IReadOnlyCollection<Rfqitem> rfqLines,
        IReadOnlyCollection<Quote> quotes, IReadOnlyCollection<CommercialActivity> activities,
        IReadOnlyCollection<FollowUpTask> followUps, IReadOnlyCollection<SalesContribution> contributions,
        IReadOnlyCollection<SourcingCase> sourcingCases,
        IReadOnlyCollection<CustomerQuoteSourcingDecision> sourcingDecisions,
        IReadOnlyDictionary<long, long> ownerByLead, IReadOnlyDictionary<long, long> ownerByRfq,
        IReadOnlyDictionary<long, CommercialLifecycleEvent> latestRfqState,
        IReadOnlyDictionary<long, string> reasonCodes)
    {
        var findings = new List<CoachingFinding>();
        var leadById = leads.ToDictionary(x => x.Id);
        var rfqById = rfqs.ToDictionary(x => x.Id);
        var contributionsByAggregate = contributions.GroupBy(x => (x.AggregateType, x.AggregateId))
            .ToDictionary(x => x.Key, x => x.Select(c => c.SalesRepUserId).Distinct().Order().ToArray());

        foreach (var assignment in assignments.Where(x => x.EffectiveFrom >= periodFromUtc &&
                     x.EffectiveFrom < periodToUtc && (!repScope.HasValue || x.ToUserId == repScope)))
        {
            var first = activities.Where(x => x.LeadAssignmentId == assignment.Id &&
                    x.SalesRepUserId == assignment.ToUserId && x.OccurredAtUtc >= assignment.EffectiveFrom &&
                    GrowthIntelligenceRules.IsQualifyingFirstResponse(x.ActivityType))
                .OrderBy(x => x.OccurredAtUtc).FirstOrDefault();
            var hours = first is null ? (decimal?)null
                : decimal.Round((decimal)(first.OccurredAtUtc - assignment.EffectiveFrom).TotalHours, 2);
            var missingAndOverdue = hours is null &&
                assignment.EffectiveFrom.AddHours(GrowthIntelligencePolicy.FirstResponseHours) < asOfUtc;
            if (missingAndOverdue || hours > GrowthIntelligencePolicy.FirstResponseHours)
            {
                leadById.TryGetValue(assignment.LeadId, out var lead);
                findings.Add(CreateFinding(businessUnitId, "SLOW_OR_MISSING_FIRST_RESPONSE", assignment.ToUserId,
                    assignment.ToUserId, [], "LeadAssignment", assignment.Id,
                    lead?.CustomerId, lead?.CommercialCaseReference, hours,
                    GrowthIntelligencePolicy.FirstResponseHours, 1, hours.HasValue ? 1m : .95m, asOfUtc,
                    hours.HasValue ? "Review the delayed first response and agree a faster next action."
                        : "Contact the customer and record a qualifying first response.",
                    GrowthIntelligenceRules.Route("Lead", assignment.LeadId),
                    [Fact("ASSIGNED_AT", assignment.EffectiveFrom),
                     Fact("FIRST_RESPONSE_AT", first?.OccurredAtUtc, "CommercialActivity", first?.Id ?? 0)]));
            }
        }

        foreach (var task in followUps.Where(x => x.Status is FollowUpStatus.Open or FollowUpStatus.InProgress &&
                     x.DueAtUtc < asOfUtc && (!repScope.HasValue || x.AssignedToUserId == repScope)))
        {
            var route = GrowthIntelligenceRules.Route(task.AggregateType, task.AggregateId);
            findings.Add(CreateFinding(businessUnitId, "OVERDUE_FOLLOW_UP", task.AssignedToUserId,
                task.AssignedToUserId, [], "FollowUpTask", task.Id, task.CustomerId, null,
                decimal.Round((decimal)(asOfUtc - task.DueAtUtc).TotalHours, 2), 0m, 1, 1m, asOfUtc,
                "Complete or reschedule the overdue follow-up with a recorded reason.", route,
                [Fact("DUE_AT", task.DueAtUtc, "FollowUpTask", task.Id),
                 new EvidenceFact("STATUS", task.Status.ToString(), "FollowUpTask", task.Id, task.UpdatedAtUtc)]));
        }

        bool ActiveRfq(long rfqId) => !latestRfqState.TryGetValue(rfqId, out var state) ||
            state.NewStatusCode is not ("LOST" or "EXPIRED" or "COMPLETED" or "CANCELLED" or "AWARDED");
        foreach (var rfq in rfqs.Where(x => x.RecDate >= periodFromUtc && x.RecDate < periodToUtc &&
                     ActiveRfq(x.Id) &&
                     ownerByRfq.ContainsKey(x.Id) && (!repScope.HasValue || ownerByRfq[x.Id] == repScope)))
        {
            var rep = ownerByRfq[rfq.Id];
            var contributors = contributionsByAggregate.GetValueOrDefault(("Rfq", rfq.Id)) ?? [];
            var nextActionExists = GrowthIntelligenceRules.HasRfqSpecificOpenFollowUp(rfq.Id, followUps);
            if (!nextActionExists)
                findings.Add(CreateFinding(businessUnitId, "NO_NEXT_ACTION", rep, rep, contributors, "Rfq", rfq.Id,
                    rfq.CustomerId, rfq.NexoraSerial, 0m, 1m, 1, 1m, asOfUtc,
                    "Create a dated next action for this RFQ.", GrowthIntelligenceRules.Route("Rfq", rfq.Id),
                    [new EvidenceFact("OPEN_FOLLOW_UP_COUNT", "0", "Rfq", rfq.Id, rfq.RecDate)]));
            if (!rfq.BidClosingDate.HasValue)
                findings.Add(CreateFinding(businessUnitId, "MISSING_DECISION_DEADLINE", rep, rep, contributors,
                    "Rfq", rfq.Id, rfq.CustomerId, rfq.NexoraSerial, null, null, 1, 1m, asOfUtc,
                    "Confirm and record the customer decision deadline.", GrowthIntelligenceRules.Route("Rfq", rfq.Id),
                    [new EvidenceFact("BID_CLOSING_DATE", "MISSING", "Rfq", rfq.Id, rfq.RecDate)]));
        }

        foreach (var sourcing in sourcingCases.Where(x => ActiveRfq(x.RfqId) &&
                     !sourcingDecisions.Any(d => d.SourcingCaseId == x.Id) &&
                     ownerByRfq.ContainsKey(x.RfqId) && (!repScope.HasValue || ownerByRfq[x.RfqId] == repScope)))
        {
            var rep = ownerByRfq[sourcing.RfqId];
            findings.Add(CreateFinding(businessUnitId, "MISSING_TARGET_PRICE_EVIDENCE", rep, rep, [],
                "SourcingCase", sourcing.Id, sourcing.CustomerId, sourcing.NexoraSerial, 0m, 1m, 1, 1m, asOfUtc,
                "Review the sourcing evidence and record an approved customer pricing decision.",
                GrowthIntelligenceRules.Route("Rfq", sourcing.RfqId),
                [new EvidenceFact("CUSTOMER_QUOTE_SOURCING_DECISION_COUNT", "0", "SourcingCase", sourcing.Id,
                    sourcing.UpdatedOn)]));
        }

        var eligibleByRep = rfqs.Where(x => x.RecDate >= periodFromUtc && x.RecDate < periodToUtc &&
                ownerByRfq.ContainsKey(x.Id) && GrowthIntelligenceRules.IsViableRfq(x, rfqLines,
                    latestRfqState.GetValueOrDefault(x.Id)?.NewStatusCode, asOfUtc))
            .GroupBy(x => ownerByRfq[x.Id]);
        foreach (var group in eligibleByRep.Where(x => !repScope.HasValue || x.Key == repScope))
        {
            var rfqIds = group.Select(x => x.Id).ToHashSet();
            var quoted = quotes.Where(x => x.Rfqid.HasValue && rfqIds.Contains(x.Rfqid.Value))
                .Select(x => x.Rfqid!.Value).Distinct().Count();
            var coverage = GrowthIntelligenceRules.Percent(quoted, rfqIds.Count);
            if (rfqIds.Count >= GrowthIntelligencePolicy.MinimumCoverageSample &&
                coverage < GrowthIntelligencePolicy.WeakQuoteCoveragePercent)
                findings.Add(CreateFinding(businessUnitId, "WEAK_QUOTE_COVERAGE", group.Key, group.Key, [],
                    "SalesRep", group.Key, null, null, coverage,
                    GrowthIntelligencePolicy.WeakQuoteCoveragePercent, rfqIds.Count, 1m, asOfUtc,
                    "Review viable RFQs that did not receive a customer quote.", "/sales/today",
                    [new EvidenceFact("ELIGIBLE_RFQ_COUNT", rfqIds.Count.ToString(CultureInfo.InvariantCulture),
                         "SalesRep", group.Key, periodToUtc),
                     new EvidenceFact("QUOTED_RFQ_COUNT", quoted.ToString(CultureInfo.InvariantCulture),
                         "SalesRep", group.Key, periodToUtc)]));
        }

        var executionLosses = quotes.Where(x => x.OutcomeOn >= periodFromUtc && x.OutcomeOn < periodToUtc &&
                x.OutcomeReasonId.HasValue && x.Rfqid.HasValue && ownerByRfq.ContainsKey(x.Rfqid.Value) &&
                GrowthIntelligenceRules.IsExecutionClassifiedLoss(
                    reasonCodes.GetValueOrDefault(x.OutcomeReasonId.Value)))
            .GroupBy(x => ownerByRfq[x.Rfqid!.Value]);
        foreach (var group in executionLosses.Where(x => x.Count() >= GrowthIntelligencePolicy.RepeatedExecutionLossCount &&
                     (!repScope.HasValue || x.Key == repScope)))
            findings.Add(CreateFinding(businessUnitId, "REPEATED_EXECUTION_LOSSES", group.Key, group.Key, [],
                "SalesRep", group.Key, null, null, group.Count(), GrowthIntelligencePolicy.RepeatedExecutionLossCount,
                group.Count(), 1m, asOfUtc, "Review the repeated execution losses and agree one corrective action.",
                GrowthIntelligenceRules.Route("SalesRep", group.Key), group.Select(x => new EvidenceFact("EXECUTION_LOSS",
                    reasonCodes.GetValueOrDefault(x.OutcomeReasonId!.Value) ?? "UNSPECIFIED", "Quote", x.Id,
                    x.OutcomeOn)).ToArray()));

        return findings.OrderBy(x => x.Code).ThenBy(x => x.SalesRepUserId)
            .ThenBy(x => x.SourceAggregateId).ToArray();
    }

    private static RecoveryRow[] BuildRecoveryRows(long businessUnitId, DateTime periodFromUtc, DateTime asOfUtc,
        long? repScope, IReadOnlyCollection<Rfq> rfqs, IReadOnlyCollection<Rfqitem> rfqLines,
        IReadOnlyCollection<Quote> quotes, IReadOnlyCollection<Order> customerAwardOrders,
        IReadOnlyCollection<CommercialActivity> activities,
        IReadOnlyCollection<FollowUpTask> followUps, IReadOnlyCollection<SourcingCase> sourcingCases,
        IReadOnlyCollection<SupplierSolicitation> solicitations, IReadOnlyCollection<SourcingAward> awards,
        IReadOnlyCollection<SupplierQuote> supplierQuotes, IReadOnlyCollection<SupplierQuoteRevision> supplierRevisions,
        IReadOnlyDictionary<long, CommercialLifecycleEvent> latestRfqState,
        IReadOnlyDictionary<long, CommercialLifecycleEvent> latestQuoteState,
        IReadOnlyDictionary<long, long> ownerByRfq)
    {
        var rows = new List<RecoveryRow>();
        bool InScope(long rfqId) => !repScope.HasValue || ownerByRfq.GetValueOrDefault(rfqId) == repScope;
        bool ActiveRfq(long rfqId) => !latestRfqState.TryGetValue(rfqId, out var state) ||
            state.NewStatusCode is not ("LOST" or "EXPIRED" or "COMPLETED" or "CANCELLED" or "AWARDED");

        foreach (var rfq in rfqs.Where(x => x.RecDate >= periodFromUtc && InScope(x.Id) && ActiveRfq(x.Id) &&
                     (!x.BidClosingDate.HasValue || x.BidClosingDate >= asOfUtc) &&
                     rfqLines.Any(line => line.Rfqid == x.Id && line.Quantity > 0) &&
                     !quotes.Any(q => q.Rfqid == x.Id)))
            rows.Add(Recovery(businessUnitId, "VIABLE_RFQ_NOT_QUOTED", ownerByRfq.GetValueOrDefault(rfq.Id),
                rfq.CustomerId, rfq.NexoraSerial, "Rfq", rfq.Id, "A viable RFQ has no customer quote.",
                "Create customer quote", GrowthIntelligenceRules.Route("Rfq", rfq.Id), asOfUtc,
                [new EvidenceFact("CUSTOMER_QUOTE_COUNT", "0", "Rfq", rfq.Id, rfq.RecDate)]));

        var customerAwardQuoteIds = customerAwardOrders.Where(x => x.QuoteId.HasValue)
            .Select(x => x.QuoteId!.Value).ToHashSet();
        foreach (var quote in quotes.Where(x => x.SentOn.HasValue && x.RespondedOn == null &&
                     x.SentOn.Value.AddDays(GrowthIntelligencePolicy.QuoteFollowUpDays) <= asOfUtc &&
                     x.Rfqid.HasValue && InScope(x.Rfqid.Value) && !followUps.Any(task =>
                         task.AggregateType == "Quote" && task.AggregateId == x.Id &&
                         task.Status is FollowUpStatus.Open or FollowUpStatus.InProgress) &&
                     !GrowthIntelligenceRules.IsTerminalQuote(x, customerAwardQuoteIds.Contains(x.Id),
                         latestQuoteState.GetValueOrDefault(x.Id)?.NewStatusCode, asOfUtc) &&
                     !quotes.Any(revision => revision.RevisionOfQuoteId == x.Id)))
            rows.Add(Recovery(businessUnitId, "QUOTE_NOT_FOLLOWED", ownerByRfq.GetValueOrDefault(quote.Rfqid!.Value),
                quote.CustomerId, quote.NexoraSerial, "Quote", quote.Id, "A sent quote has no response or open follow-up.",
                "Open quote and create follow-up", GrowthIntelligenceRules.Route("Quote", quote.Id), asOfUtc,
                [Fact("SENT_ON", quote.SentOn, "Quote", quote.Id)]));

        foreach (var supplierQuote in supplierQuotes.Where(x => ActiveRfq(x.RfqId) && InScope(x.RfqId)))
        {
            var revision = supplierRevisions.Where(x => x.SupplierQuoteId == supplierQuote.Id &&
                    x.RevisionNumber == supplierQuote.CurrentRevisionNumber)
                .OrderByDescending(x => x.CapturedOn).FirstOrDefault();
            if (revision?.ValidUntil is not null && revision.ValidUntil <= asOfUtc)
                rows.Add(Recovery(businessUnitId, "SUPPLIER_OFFER_EXPIRED",
                    ownerByRfq.GetValueOrDefault(supplierQuote.RfqId), null, supplierQuote.NexoraSerial,
                    "SupplierQuote", supplierQuote.Id, "The current supplier offer expired while the RFQ is active.",
                    "Request refreshed supplier quote", GrowthIntelligenceRules.Route("SupplierQuote", supplierQuote.Id), asOfUtc,
                    [Fact("VALID_UNTIL", revision.ValidUntil, "SupplierQuoteRevision", revision.Id)]));
        }

        foreach (var sourcing in sourcingCases.Where(x => x.UnfulfilledQuantity > 0 && InScope(x.RfqId) &&
                     ActiveRfq(x.RfqId) && x.Status is SourcingCaseStatuses.Draft or
                         SourcingCaseStatuses.InternalSearch or SourcingCaseStatuses.DiscoveryRequired &&
                     !solicitations.Any(s => s.SourcingCaseId == x.Id && s.Status is SolicitationStatus.Sent or
                         SolicitationStatus.Responded) && !awards.Any(a => a.RfqId == x.RfqId &&
                         (!a.RfqItemId.HasValue || a.RfqItemId == x.RfqItemId) && a.Status == "APPROVED")))
            rows.Add(Recovery(businessUnitId, "STOCKOUT_BLOCK", ownerByRfq.GetValueOrDefault(sourcing.RfqId),
                sourcing.CustomerId, sourcing.NexoraSerial, "SourcingCase", sourcing.Id,
                "Authoritative demand remains unfulfilled without supplier outreach or an award.",
                "Open sourcing case", GrowthIntelligenceRules.Route("Rfq", sourcing.RfqId), asOfUtc,
                [new EvidenceFact("UNFULFILLED_QUANTITY", sourcing.UnfulfilledQuantity.ToString(CultureInfo.InvariantCulture),
                    "SourcingCase", sourcing.Id, sourcing.UpdatedOn)]));

        foreach (var pair in latestRfqState.Where(x => x.Value.NewStatusCode == "NEEDS_REVIEW" && InScope(x.Key)))
        {
            var rfq = rfqs.Single(x => x.Id == pair.Key);
            rows.Add(Recovery(businessUnitId, "CLARIFICATION_REQUIRED", ownerByRfq.GetValueOrDefault(rfq.Id),
                rfq.CustomerId, rfq.NexoraSerial, "Rfq", rfq.Id, "The RFQ is awaiting governed clarification.",
                "Request clarification", GrowthIntelligenceRules.Route("Rfq", rfq.Id), asOfUtc,
                [new EvidenceFact("REASON_CODE", pair.Value.ReasonCode ?? "UNSPECIFIED",
                    "CommercialLifecycleEvent", pair.Value.Id, pair.Value.OccurredOn)]));
        }

        foreach (var pair in latestRfqState.Where(x => x.Value.NewStatusCode == "LOST" && InScope(x.Key)))
        {
            var rfq = rfqs.Single(x => x.Id == pair.Key);
            var later = activities.Where(x => x.OccurredAtUtc > pair.Value.OccurredOn &&
                    (x.CustomerId == rfq.CustomerId || x.AggregateType == "Rfq" && x.AggregateId == rfq.Id) &&
                    x.ActivityType is CommercialActivityType.Call or CommercialActivityType.Email or
                        CommercialActivityType.Meeting or CommercialActivityType.CustomerResponded)
                .OrderBy(x => x.OccurredAtUtc).FirstOrDefault();
            if (later is not null)
                rows.Add(Recovery(businessUnitId, "LOST_OPPORTUNITY_REOPEN",
                    ownerByRfq.GetValueOrDefault(rfq.Id), rfq.CustomerId, rfq.NexoraSerial, "Rfq", rfq.Id,
                    "Customer activity was recorded after the RFQ was lost.", "Review for manager-authorized reopen",
                    GrowthIntelligenceRules.Route("Rfq", rfq.Id), asOfUtc,
                    [Fact("LOST_ON", pair.Value.OccurredOn, "CommercialLifecycleEvent", pair.Value.Id),
                     Fact("LATER_CUSTOMER_ACTIVITY", later.OccurredAtUtc, "CommercialActivity", later.Id)]));
        }

        var customerOwner = rfqs.Where(x => x.CustomerId.HasValue && ownerByRfq.ContainsKey(x.Id))
            .GroupBy(x => x.CustomerId!.Value).ToDictionary(x => x.Key,
                x => ownerByRfq[x.OrderByDescending(r => r.RecDate).First().Id]);
        var dormantInputs = customerAwardOrders.Where(x => x.CurrencyId.HasValue && x.TotalAmount >= 0m)
            .GroupBy(x => new { x.CustomerId, CurrencyId = x.CurrencyId!.Value })
            .Select(group =>
            {
                var customerId = group.Key.CustomerId;
                var dates = group.Select(x => x.OrderDate)
                    .Concat(rfqs.Where(x => x.CustomerId == customerId).Select(x => x.RecDate))
                    .Concat(quotes.Where(x => x.CustomerId == customerId)
                        .Select(x => x.SentOn ?? x.OutcomeOn ?? x.CreatedDate ?? DateTime.MinValue))
                    .Concat(activities.Where(x => x.CustomerId == customerId).Select(x => x.OccurredAtUtc));
                return new DormantCustomerObservation(customerId, group.Key.CurrencyId,
                    group.Sum(x => x.TotalAmount), dates.Max());
            }).ToArray();
        foreach (var dormant in GrowthIntelligenceRules.SelectDormantHighValueCustomers(dormantInputs, asOfUtc)
                     .Where(x => !repScope.HasValue || customerOwner.GetValueOrDefault(x.CustomerId) == repScope))
            rows.Add(Recovery(businessUnitId, "DORMANT_HIGH_VALUE_CUSTOMER",
                customerOwner.GetValueOrDefault(dormant.CustomerId), dormant.CustomerId, null, "Customer",
                dormant.CustomerId, "A high-value customer in a sufficient single-currency cohort is dormant.",
                "Create account follow-up", GrowthIntelligenceRules.Route("Customer", dormant.CustomerId), asOfUtc,
                [new EvidenceFact("HISTORICAL_ACCEPTED_VALUE",
                     dormant.HistoricalAcceptedValue.ToString(CultureInfo.InvariantCulture), "Customer",
                     dormant.CustomerId, dormant.LastCommercialActivityAtUtc),
                 new EvidenceFact("CURRENCY_ID", dormant.CurrencyId.ToString(CultureInfo.InvariantCulture),
                     "Customer", dormant.CustomerId, dormant.LastCommercialActivityAtUtc),
                 Fact("LAST_COMMERCIAL_ACTIVITY", dormant.LastCommercialActivityAtUtc, "Customer",
                     dormant.CustomerId)]));

        return rows.OrderBy(x => x.Code).ThenBy(x => x.SourceAggregateId).ToArray();
    }

    private static CoachingFinding CreateFinding(long businessUnitId, string code, long repId, long? ownerId,
        IReadOnlyCollection<long> contributorIds, string aggregateType, long aggregateId, long? customerId,
        string? nexoraSerial, decimal? observed, decimal? threshold, int sampleSize, decimal confidence,
        DateTime generatedAtUtc, string recommendation, string route, IReadOnlyCollection<EvidenceFact> evidence)
    {
        var version = GrowthIntelligenceRules.SourceVersion(evidence);
        var key = GrowthIntelligenceRules.StableFindingKey(businessUnitId, code, repId, aggregateType,
            aggregateId, version);
        return new CoachingFinding(key, code, repId, ownerId, contributorIds, aggregateType, aggregateId,
            version, customerId, nexoraSerial, observed, threshold, sampleSize, confidence, generatedAtUtc,
            recommendation, route, evidence, null);
    }

    private static RecoveryRow Recovery(long businessUnitId, string code, long? repId, long? customerId,
        string? nexoraSerial, string aggregateType, long aggregateId, string summary, string action, string route,
        DateTime generatedAtUtc, IReadOnlyCollection<EvidenceFact> evidence)
    {
        var version = GrowthIntelligenceRules.SourceVersion(evidence);
        return new RecoveryRow(GrowthIntelligenceRules.StableRecoveryKey(businessUnitId, code, aggregateType,
            aggregateId, version), code, repId, customerId, nexoraSerial, aggregateType, aggregateId, version,
            summary, action, route, generatedAtUtc, evidence);
    }

    private static EvidenceFact Fact(string code, DateTime? value, string sourceType = "LeadAssignment",
        long sourceId = 0) => new(code, value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ??
        "MISSING", sourceType, sourceId, value);

    private static CoachingAcknowledgementView ToView(SalesCoachingAcknowledgement x) =>
        new(x.Id, x.DecisionCode, x.Reason, x.ManagerUserId, x.CreatedAtUtc, x.PolicyVersion);

    private static bool IsExplicitWonQuote(Quote quote) => quote.OutcomeOn.HasValue &&
        quote.Status?.SetupCode?.Trim().ToUpperInvariant() is "ACCEPTED" or "ORDERED";

    private void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0 || context.ScopedTenantId != businessUnitId)
            throw new UnauthorizedAccessException("The authenticated tenant context is required.");
    }

    private static void ValidatePeriod(DateTime fromUtc, DateTime toUtc, DateTime asOfUtc)
    {
        if (fromUtc.Kind != DateTimeKind.Utc || toUtc.Kind != DateTimeKind.Utc || asOfUtc.Kind != DateTimeKind.Utc)
            throw new ArgumentException("Growth intelligence periods must be UTC.");
        if (fromUtc >= toUtc || toUtc > asOfUtc || toUtc - fromUtc > TimeSpan.FromDays(MaxPeriodDays))
            throw new ArgumentOutOfRangeException(nameof(toUtc), "The bounded period is invalid.");
    }

    private static string[] IncompleteSources(params (string Name, int Count)[] sources) => sources
        .Where(source => source.Count >= MaxRowsPerSource).Select(source => source.Name).Order().ToArray();
}

public static class GrowthIntelligenceRules
{
    private static readonly HashSet<string> ExecutionLossReasons = new(StringComparer.OrdinalIgnoreCase)
        { "INCORRECT_COMMITMENT", "MISSED_DEADLINE", "NO_FOLLOW_UP", "PRICING_ERROR", "SLOW_RESPONSE", "WRONG_PART" };

    public static bool IsQualifyingFirstResponse(CommercialActivityType type) => type is
        CommercialActivityType.Call or CommercialActivityType.Email or CommercialActivityType.Meeting or
        CommercialActivityType.QuoteSent;

    public static bool IsExecutionClassifiedLoss(string? reasonCode) =>
        !string.IsNullOrWhiteSpace(reasonCode) && ExecutionLossReasons.Contains(reasonCode.Trim());

    public static bool IsTerminalRfqStatus(string? statusCode) => statusCode?.Trim().ToUpperInvariant() is
        "LOST" or "EXPIRED" or "COMPLETED" or "CANCELLED" or "AWARDED";

    public static bool IsViableRfq(Rfq rfq, IEnumerable<Rfqitem> lines, string? lifecycleStatus,
        DateTime asOfUtc) => !IsTerminalRfqStatus(lifecycleStatus) &&
        (!rfq.BidClosingDate.HasValue || rfq.BidClosingDate >= asOfUtc) &&
        lines.Any(line => line.Rfqid == rfq.Id && line.Quantity > 0);

    public static bool IsTerminalQuote(Quote quote, bool hasCustomerAward, string? lifecycleStatus,
        DateTime asOfUtc) => hasCustomerAward || quote.OutcomeOn.HasValue ||
        quote.ValidUntil.HasValue && quote.ValidUntil < asOfUtc ||
        quote.Status?.SetupCode?.Trim().ToUpperInvariant() is "ACCEPTED" or "ORDERED" or "REJECTED" or
            "EXPIRED" or "CANCELLED" ||
        lifecycleStatus?.Trim().ToUpperInvariant() is "AWARDED" or "PARTIALLY_AWARDED" or "LOST" or
            "EXPIRED" or "CANCELLED" or "COMPLETED";

    public static bool HasRfqSpecificOpenFollowUp(long rfqId, IEnumerable<FollowUpTask> followUps) =>
        followUps.Any(task => task.AggregateType == "Rfq" && task.AggregateId == rfqId &&
            task.Status is FollowUpStatus.Open or FollowUpStatus.InProgress);

    public static bool IsIdempotencyUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException postgres && IsIdempotencyUniqueViolation(
            postgres.SqlState, postgres.ConstraintName);

    public static bool IsIdempotencyUniqueViolation(string? sqlState, string? constraintName) =>
        sqlState == PostgresErrorCodes.UniqueViolation &&
        constraintName == "IX_sales_coaching_acknowledgements_BusinessUnitId_IdempotencyK~";

    public static decimal? Percent(int numerator, int denominator) => denominator <= 0
        ? null : decimal.Round(100m * numerator / denominator, 2);

    public static decimal? TrendPercent(int current, int previous) => previous <= 0
        ? null : decimal.Round(100m * (current - previous) / previous, 2);

    public static IReadOnlyCollection<FirstResponseObservation> MissingOrSlowFirstResponses(
        IEnumerable<FirstResponseObservation> observations, DateTime asOfUtc)
        => observations.Where(x =>
            (!x.FirstQualifyingResponseAtUtc.HasValue &&
             x.AssignedAtUtc.AddHours(GrowthIntelligencePolicy.FirstResponseHours) < asOfUtc) ||
            (x.FirstQualifyingResponseAtUtc.HasValue &&
             (x.FirstQualifyingResponseAtUtc.Value - x.AssignedAtUtc).TotalHours >
             GrowthIntelligencePolicy.FirstResponseHours)).ToArray();

    public static IReadOnlyCollection<DormantCustomerObservation> SelectDormantHighValueCustomers(
        IEnumerable<DormantCustomerObservation> observations, DateTime asOfUtc)
    {
        var rows = observations.ToArray();
        var validCustomers = rows.GroupBy(x => x.CustomerId).Where(x => x.Select(v => v.CurrencyId).Distinct().Count() == 1)
            .Select(x => x.Single()).ToArray();
        return validCustomers.GroupBy(x => x.CurrencyId)
            .Where(x => x.Count() >= GrowthIntelligencePolicy.MinimumDormantCustomerCohort)
            .SelectMany(group =>
            {
                var ordered = group.Select(x => x.HistoricalAcceptedValue).OrderBy(x => x).ToArray();
                var threshold = ordered[(int)Math.Ceiling(.75m * ordered.Length) - 1];
                return group.Where(x => x.HistoricalAcceptedValue >= threshold &&
                    x.LastCommercialActivityAtUtc <= asOfUtc.AddDays(-GrowthIntelligencePolicy.DormancyDays));
            }).OrderBy(x => x.CustomerId).ToArray();
    }

    public static HealthAssessment AssessCustomerHealth(DateTime asOfUtc, DateTime? lastActivity,
        FollowUpHealth followUp, decimal? quoteCoverage, decimal? conversion, int decidedSample)
    {
        var reasons = new List<string>();
        if (!lastActivity.HasValue)
            return new HealthAssessment("insufficient-evidence", ["No persisted commercial activity."],
                GrowthIntelligencePolicy.Version, 0);
        if (followUp.OverdueCount > 0) reasons.Add($"{followUp.OverdueCount} follow-up(s) are overdue.");
        if (lastActivity <= asOfUtc.AddDays(-GrowthIntelligencePolicy.DormancyDays))
            reasons.Add("No commercial activity in the dormancy window.");
        if (quoteCoverage.HasValue && quoteCoverage < GrowthIntelligencePolicy.WeakQuoteCoveragePercent)
            reasons.Add("RFQ-to-quote coverage is below policy threshold.");
        if (decidedSample < 3) reasons.Add("Quote conversion sample is insufficient for a strong conclusion.");
        var band = reasons.Any(x => x.Contains("dormancy", StringComparison.OrdinalIgnoreCase)) ? "dormant"
            : followUp.OverdueCount > 0 ? "at-risk"
            : quoteCoverage.HasValue && quoteCoverage < GrowthIntelligencePolicy.WeakQuoteCoveragePercent ? "watch"
            : decidedSample < 3 || !conversion.HasValue ? "insufficient-evidence" : "healthy";
        return new HealthAssessment(band, reasons, GrowthIntelligencePolicy.Version,
            Math.Max(decidedSample, followUp.CompletedCount));
    }

    public static NextGrowthAction? SelectNextCustomerAction(long customerId,
        IReadOnlyCollection<AccountOpportunity> opportunities, IReadOnlyCollection<FollowUpTask> followUps,
        DateTime asOfUtc)
    {
        var overdue = followUps.Where(x => x.Status is FollowUpStatus.Open or FollowUpStatus.InProgress &&
                x.DueAtUtc < asOfUtc).OrderBy(x => x.DueAtUtc).FirstOrDefault();
        if (overdue is not null)
            return new NextGrowthAction("COMPLETE_OVERDUE_FOLLOW_UP", "Complete overdue follow-up",
                Route(overdue.AggregateType, overdue.AggregateId), GrowthIntelligencePolicy.Version,
                [new EvidenceFact("DUE_AT", overdue.DueAtUtc.ToString("O", CultureInfo.InvariantCulture),
                    "FollowUpTask", overdue.Id, overdue.DueAtUtc)]);
        var unquoted = opportunities.OrderBy(x => x.DecisionDeadlineUtc ?? DateTime.MaxValue)
            .FirstOrDefault(x => !x.HasCustomerQuote);
        return unquoted is null ? null : new NextGrowthAction("CREATE_CUSTOMER_QUOTE", "Create customer quote",
            unquoted.Route, GrowthIntelligencePolicy.Version,
            [new EvidenceFact("CUSTOMER_QUOTE_COUNT", "0", "Rfq", unquoted.RfqId, unquoted.ReceivedAtUtc)]);
    }

    public static string Route(string aggregateType, long aggregateId) => aggregateType switch
    {
        "Lead" => $"/procurement/leads/view/{aggregateId}",
        "Rfq" => $"/procurement/rfqs/view/{aggregateId}",
        "Quote" => $"/sales/quotes/view/{aggregateId}",
        "Customer" => $"/customers/{aggregateId}",
        "SalesRep" => $"/sales/reps/{aggregateId}",
        "SupplierQuote" => $"/procurement/supplier-quotes/{aggregateId}",
        _ => "/sales/today"
    };

    public static string SourceVersion(IEnumerable<EvidenceFact> evidence) => Sha256(string.Join("\n",
        evidence.OrderBy(x => x.SourceType, StringComparer.Ordinal).ThenBy(x => x.SourceId)
            .ThenBy(x => x.Code, StringComparer.Ordinal).ThenBy(x => x.Value, StringComparer.Ordinal)
            .Select(x => $"{x.SourceType}|{x.SourceId}|{x.Code}|{x.Value}|{x.ObservedAtUtc:O}")));

    public static string StableFindingKey(long businessUnitId, string code, long salesRepUserId,
        string aggregateType, long aggregateId, string sourceVersion) => Sha256(string.Join('|',
        GrowthIntelligencePolicy.Version, businessUnitId, code, salesRepUserId, aggregateType, aggregateId,
        sourceVersion));

    public static string StableRecoveryKey(long businessUnitId, string code, string aggregateType,
        long aggregateId, string sourceVersion) => Sha256(string.Join('|', GrowthIntelligencePolicy.Version,
        businessUnitId, code, aggregateType, aggregateId, sourceVersion));

    public static string AcknowledgementRequestHash(AcknowledgeCoachingFindingCommand command,
        CoachingFinding finding) => Sha256(string.Join('|', command.FindingKey,
        command.DecisionCode.Trim().ToUpperInvariant(), command.Reason.Trim(), command.ManagerUserId,
        finding.SourceAggregateVersion, SourceVersion(finding.Evidence), GrowthIntelligencePolicy.Version));

    public static SalesCoachingAcknowledgement CreateAcknowledgement(long businessUnitId,
        AcknowledgeCoachingFindingCommand command, CoachingFinding currentFinding, string requestHash)
    {
        if (!string.Equals(command.FindingKey, currentFinding.FindingKey, StringComparison.Ordinal))
            throw new InvalidOperationException("The coaching finding is stale.");
        var decision = command.DecisionCode.Trim().ToUpperInvariant();
        if (decision is not ("ACKNOWLEDGED" or "RESOLVED" or "DISMISSED"))
            throw new ArgumentException("The manager decision is invalid.", nameof(command));
        if (string.IsNullOrWhiteSpace(command.Reason) || string.IsNullOrWhiteSpace(command.IdempotencyKey) ||
            string.IsNullOrWhiteSpace(command.CorrelationId))
            throw new ArgumentException("Decision reason, idempotency key, and correlation ID are required.",
                nameof(command));
        return new SalesCoachingAcknowledgement
        {
            BusinessUnitId = businessUnitId,
            FindingKey = currentFinding.FindingKey,
            FindingCode = currentFinding.Code,
            SalesRepUserId = currentFinding.SalesRepUserId,
            ManagerUserId = command.ManagerUserId,
            DecisionCode = decision,
            Reason = command.Reason.Trim(),
            SourceAggregateType = currentFinding.SourceAggregateType,
            SourceAggregateId = currentFinding.SourceAggregateId,
            SourceAggregateVersion = currentFinding.SourceAggregateVersion,
            EvidenceSnapshotJson = JsonSerializer.Serialize(currentFinding.Evidence.OrderBy(x => x.SourceType)
                .ThenBy(x => x.SourceId).ThenBy(x => x.Code)),
            PolicyVersion = GrowthIntelligencePolicy.Version,
            FindingGeneratedAtUtc = currentFinding.GeneratedAtUtc,
            IdempotencyKey = command.IdempotencyKey.Trim(),
            RequestHash = requestHash,
            CorrelationId = command.CorrelationId.Trim(),
            // NORMALISED TO WHAT POSTGRESQL CAN STORE, not written raw.
            //
            // A .NET DateTime counts 100-ns ticks; PostgreSQL `timestamp` counts MICROSECONDS, and
            // Npgsql converts by integer division. So a value whose seventh fractional digit is
            // non-zero — nine times in ten — comes back from the row one tick smaller than the
            // value this method returned.
            //
            // That is not cosmetic here. AcknowledgeFindingAsync answers the FIRST request with
            // this in-memory entity and an idempotent REPLAY with the row read back, so the same
            // idempotency key returned two different `acknowledgedAt` values to the caller — the
            // one thing an idempotent endpoint promises it will not do. It also made CI red on
            // main roughly nine runs in ten while passing locally on the tenth, which is how it
            // survived: the suite looked flaky rather than wrong.
            //
            // Rounding to storable precision at creation makes the value the API returns the value
            // it stored. Same normalisation as SubscriptionInvoiceService.NormalizePostgreSqlTimestamp
            // and UsageMeteringService.NormalizeTimestamp, for the same reason.
            CreatedAtUtc = NormalizePostgreSqlTimestamp(command.AsOfUtc)
        };
    }

    /// <summary>
    /// Truncates to PostgreSQL's microsecond resolution so a persisted value round-trips equal.
    /// Deliberately does NOT call ToUniversalTime: every caller already supplies a UTC instant
    /// (the field is AsOfUtc, set from DateTime.UtcNow), and converting a Kind=Unspecified value
    /// would silently shift it by the host's offset. This changes precision only.
    /// </summary>
    private static DateTime NormalizePostgreSqlTimestamp(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    public static bool IsIdempotentReplay(SalesCoachingAcknowledgement existing, string requestHash)
    {
        if (existing.RequestHash.Length != 64 || requestHash.Length != 64) return false;
        return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(existing.RequestHash),
            Convert.FromHexString(requestHash));
    }

    private static string Sha256(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
