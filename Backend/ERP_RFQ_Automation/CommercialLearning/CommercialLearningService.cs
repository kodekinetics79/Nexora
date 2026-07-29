using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Inventory;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.CommercialLearning;

public sealed class CommercialLearningService(ErpRfqAutomationContext context)
{
    private static readonly DateTime DefaultPeriodFrom = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task<IReadOnlyCollection<ProductCommercialMemory>> GetProductsAsync(long businessUnitId,
        int limit, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var productIds = await context.Rfqitems.AsNoTracking()
            .Where(x => x.ProductId.HasValue && x.Rfq.BusinessUnitId == businessUnitId)
            .GroupBy(x => x.ProductId!.Value).OrderByDescending(x => x.Count()).Take(Math.Clamp(limit, 1, 200))
            .Select(x => x.Key).ToArrayAsync(cancellationToken);
        var results = new List<ProductCommercialMemory>(productIds.Length);
        foreach (var productId in productIds)
            results.Add(await GetProductAsync(businessUnitId, productId, cancellationToken));
        return results;
    }

    public async Task<ProductCommercialMemory> GetProductAsync(long businessUnitId, long productId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var product = await context.Products.AsNoTracking().SingleOrDefaultAsync(x => x.Buid == businessUnitId &&
            x.Id == productId, cancellationToken) ?? throw new KeyNotFoundException("Product was not found in this tenant.");
        var requestLines = await context.Rfqitems.AsNoTracking().Include(x => x.Rfq)
            .Where(x => x.ProductId == productId && x.Rfq.BusinessUnitId == businessUnitId).ToListAsync(cancellationToken);
        var rfqItemIds = requestLines.Select(x => x.Id).ToArray();
        var quoteLines = await context.QuoteItems.AsNoTracking().Include(x => x.Quote).ThenInclude(x => x.Status)
            .Where(x => x.ProductId == productId && x.Quote.BusinessUnitId == businessUnitId).ToListAsync(cancellationToken);
        var quoteIds = quoteLines.Select(x => x.QuoteId).Distinct().ToArray();
        var orderWins = await context.Orders.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
                x.SourceType == OrderSourceTypes.CustomerAward && x.QuoteId.HasValue && quoteIds.Contains(x.QuoteId.Value))
            .GroupBy(x => x.QuoteId!.Value).ToDictionaryAsync(x => x.Key,
                x => x.Max(order => order.OrderDate), cancellationToken);
        var decided = quoteLines.Where(x => x.Quote.OutcomeOn.HasValue || orderWins.ContainsKey(x.QuoteId)).ToArray();
        var won = decided.Where(x => orderWins.ContainsKey(x.QuoteId) || Outcome(x.Quote) == "WON").ToArray();
        var lost = decided.Where(x => !orderWins.ContainsKey(x.QuoteId) && Outcome(x.Quote) is "LOST" or "EXPIRED").ToArray();
        var currencyIds = quoteLines.Where(x => x.Quote.CurrencyId.HasValue).Select(x => x.Quote.CurrencyId!.Value)
            .Concat(context.CustomerQuoteSourcingDecisions.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
                rfqItemIds.Contains(x.RfqItemId)).Select(x => x.CurrencyId)).Distinct().ToArray();
        var currencies = await context.Currencies.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            currencyIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, cancellationToken);
        var landed = await context.CustomerQuoteSourcingDecisions.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && rfqItemIds.Contains(x.RfqItemId))
            .Select(x => new ValuePoint(x.CurrencyId, x.SupplierLandedUnitCost, x.CreatedOn, x.Id)).ToListAsync(cancellationToken);
        var lossReasonIds = lost.Where(x => x.Quote.OutcomeReasonId.HasValue)
            .Select(x => x.Quote.OutcomeReasonId!.Value).Distinct().ToArray();
        var reasonNames = await context.SetupMasters.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            lossReasonIds.Contains(x.SetupId)).ToDictionaryAsync(x => x.SetupId,
            x => new { Code = x.SetupCode ?? "UNSPECIFIED", Label = x.Description ?? x.SetupValue ?? "Unspecified" }, cancellationToken);
        var lossReasons = lost.GroupBy(x => x.Quote.OutcomeReasonId).Select(group =>
        {
            var value = group.Key.HasValue ? reasonNames.GetValueOrDefault(group.Key.Value) : null;
            return new CommercialReasonCount(value?.Code ?? "UNSPECIFIED", value?.Label ?? "Reason not recorded", group.Count());
        }).OrderByDescending(x => x.Count).ToArray();
        var wonPrices = won.Where(x => x.Quote.CurrencyId.HasValue)
            .Select(x => new ValuePoint(x.Quote.CurrencyId!.Value, x.UnitPrice,
                x.Quote.OutcomeOn ?? orderWins[x.QuoteId], x.QuoteId)).ToArray();
        var stockouts = await context.SourcingCases.AsNoTracking().CountAsync(x => x.BusinessUnitId == businessUnitId &&
            x.ProductId == productId && x.StockQuantity < x.RequestedQuantity, cancellationToken);
        var periodValues = requestLines.Select(x => x.Rfq.RecDate).Where(x => x.Year >= 2000)
            .Concat(quoteLines.Select(x => x.Quote.CreatedDate ?? x.Quote.QuoteDate ?? DateTime.UtcNow)).ToArray();
        var evidence = won.Select(x => new CommercialEvidenceLink("CustomerQuote", x.QuoteId, x.Quote.QuoteNo,
                x.Quote.OutcomeOn ?? orderWins.GetValueOrDefault(x.QuoteId),
                orderWins.ContainsKey(x.QuoteId) ? "CUSTOMER_ORDER_WIN" : "WON_OUTCOME"))
            .Concat(landed.Select(x => new CommercialEvidenceLink("PricingDecision", x.RecordId,
                $"Product {productId} landed cost", x.OccurredOn, "SUPPLIER_COST"))).Take(50).ToArray();
        var winningLeadTimes = won.Where(x => x.DeliveryLeadTime.HasValue).Select(x => (decimal)x.DeliveryLeadTime!.Value).ToArray();
        var lastWon = won.OrderByDescending(x => x.Quote.OutcomeOn ?? orderWins.GetValueOrDefault(x.QuoteId))
            .ThenByDescending(x => x.QuoteId).FirstOrDefault();
        var lastWonContext = lastWon is null || !lastWon.Quote.CurrencyId.HasValue ? null : new ProductWonContext(
            lastWon.QuoteId, lastWon.Quote.QuoteNo, lastWon.Quantity, lastWon.UnitPrice,
            lastWon.Quote.CurrencyId.Value, currencies.GetValueOrDefault(lastWon.Quote.CurrencyId.Value) ??
                $"Currency {lastWon.Quote.CurrencyId.Value}", lastWon.DeliveryLeadTime,
            lastWon.Quote.OutcomeOn ?? orderWins[lastWon.QuoteId]);
        return new ProductCommercialMemory(productId, product.PartNo,
            product.ProductName ?? product.Description ?? product.PartNo,
            periodValues.Length == 0 ? DefaultPeriodFrom : periodValues.Min(), DateTime.UtcNow,
            requestLines.Count, quoteLines.Count, decided.Length, won.Length, lost.Length, quoteLines.Count - decided.Length,
            decided.Length == 0 ? null : decimal.Round(100m * won.Length / decided.Length, 2), stockouts,
            winningLeadTimes.Length == 0 ? null : Median(winningLeadTimes),
            lastWonContext,
            Summaries(wonPrices, currencies), Summaries(landed, currencies), lossReasons, evidence);
    }

    public async Task<SupplierCommercialEvaluation> GetSupplierAsync(long businessUnitId, long supplierId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var supplier = await context.Suppliers.AsNoTracking().SingleOrDefaultAsync(x => x.Buid == businessUnitId &&
            x.Id == supplierId, cancellationToken) ?? throw new KeyNotFoundException("Supplier was not found in this tenant.");
        var revisions = await context.SupplierQuotes.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.SupplierId == supplierId).SelectMany(x => x.Revisions).ToListAsync(cancellationToken);
        var projected = await context.SupplierQuotedItems.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.SupplierId == supplierId && x.SourceSupplierQuoteId.HasValue).ToListAsync(cancellationToken);
        var projectedIds = projected.Select(x => x.Id).ToArray();
        var awards = await context.Set<ERP_RFQ_Automation.Agent.Models.SourcingAward>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.SupplierId == supplierId &&
                x.SupplierQuotedItemId.HasValue && projectedIds.Contains(x.SupplierQuotedItemId.Value)).ToListAsync(cancellationToken);
        var awardIds = awards.Select(x => x.Id).ToArray();
        var currencyIds = projected.Where(x => x.CurrencyId.HasValue).Select(x => x.CurrencyId!.Value).Distinct().ToArray();
        var currencies = await context.Currencies.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            currencyIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.Code, cancellationToken);
        var responseRows = await (from solicitation in context.Set<ERP_RFQ_Automation.Agent.Models.SupplierSolicitation>().AsNoTracking()
            where solicitation.BusinessUnitId == businessUnitId && solicitation.SupplierId == supplierId && solicitation.RespondedOn != null
            select new { solicitation.SentOn, solicitation.RespondedOn }).ToArrayAsync(cancellationToken);
        var responseDays = responseRows.Select(x => (decimal)(x.RespondedOn!.Value - x.SentOn).TotalDays).ToArray();
        var reliability = projected.Where(x => x.ReliabilitySnapshot.HasValue).Select(x => x.ReliabilitySnapshot!.Value).ToArray();
        var handoffs = await context.ProcurementHandoffs.AsNoTracking().Where(x =>
            x.BusinessUnitId == businessUnitId && x.SupplierId == supplierId)
            .OrderByDescending(x => x.CreatedOn).Take(50).ToListAsync(cancellationToken);
        var revisionIds = projected.Where(x => x.SourceSupplierQuoteRevisionId.HasValue)
            .Select(x => x.SourceSupplierQuoteRevisionId!.Value).Distinct().ToArray();
        var canonicalRevisions = await context.SupplierQuoteRevisions.AsNoTracking().Include(x => x.Lines)
            .Where(x => x.BusinessUnitId == businessUnitId && revisionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var bidQuality = BuildBidQuality(projected, canonicalRevisions);
        var supportedAwardIds = await context.CustomerQuoteSourcingDecisions.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && awardIds.Contains(x.SourcingAwardId))
            .Join(context.Orders.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
                    x.SourceType == OrderSourceTypes.CustomerAward && x.QuoteId.HasValue),
                decision => decision.QuoteId, order => order.QuoteId!.Value, (decision, _) => decision.SourcingAwardId)
            .Distinct().ToArrayAsync(cancellationToken);
        var supportedAwardSet = supportedAwardIds.ToHashSet();
        var quoteStatuses = await context.SupplierQuotes.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
                x.SupplierId == supplierId).ToDictionaryAsync(x => x.Id, x => x.InboxStatus, cancellationToken);
        bidQuality = BuildBidQuality(projected, canonicalRevisions, quoteStatuses, supplier);
        return new SupplierCommercialEvaluation(supplierId, supplier.Name, revisions.Count, awards.Count,
            supportedAwardSet.Count, bidQuality.EligibleOfferCount,
            responseDays.Length == 0 ? null : decimal.Round(responseDays.Average(), 2),
            reliability.Length == 0 ? null : decimal.Round(reliability.Average(), 2),
            Summaries(projected.Where(x => x.CurrencyId.HasValue && x.LandedUnitCost.HasValue)
                .Select(x => new ValuePoint(x.CurrencyId!.Value, x.LandedUnitCost!.Value,
                    x.QuoteDate ?? x.CreatedDate, x.Id)), currencies),
            awards.Select(x => new CommercialEvidenceLink("SourcingAward", x.Id, $"Award {x.Id}", x.CreatedOn,
                supportedAwardSet.Contains(x.Id) ? "SUPPORTED_CUSTOMER_ORDER" : "SELECTED_OFFER"))
                .Concat(handoffs.Select(x => new CommercialEvidenceLink("ProcurementHandoff", x.Id,
                    x.ExternalSupplierPoNumber ?? $"Handoff {x.Id}", x.LastSynchronizedOn ?? x.CreatedOn,
                    x.Status))).Take(50).ToArray(), bidQuality);
    }

    public async Task<IReadOnlyCollection<SupplierCommercialEvaluation>> GetSuppliersAsync(long businessUnitId,
        int limit, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var supplierIds = await context.SupplierQuotes.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId)
            .Select(x => x.SupplierId).Union(context.ProcurementHandoffs.AsNoTracking()
                .Where(x => x.BusinessUnitId == businessUnitId).Select(x => x.SupplierId))
            .Distinct().Take(Math.Clamp(limit, 1, 200)).ToArrayAsync(cancellationToken);
        var results = new List<SupplierCommercialEvaluation>(supplierIds.Length);
        foreach (var supplierId in supplierIds)
            results.Add(await GetSupplierAsync(businessUnitId, supplierId, cancellationToken));
        return results;
    }

    public async Task<InventoryDemandMemory> GetInventoryDemandAsync(long businessUnitId, long productId,
        CancellationToken cancellationToken = default)
    {
        var product = await GetProductAsync(businessUnitId, productId, cancellationToken);
        var requestLines = await context.Rfqitems.AsNoTracking().Include(x => x.Rfq).Where(x => x.ProductId == productId &&
            x.Rfq.BusinessUnitId == businessUnitId).ToListAsync(cancellationToken);
        var quoteLines = await context.QuoteItems.AsNoTracking().Include(x => x.Quote).Where(x => x.ProductId == productId &&
            x.Quote.BusinessUnitId == businessUnitId).ToListAsync(cancellationToken);
        var orderLines = await context.OrderItems.AsNoTracking().Include(x => x.Order)
            .Where(x => x.ProductId == productId && x.Order.BusinessUnitId == businessUnitId).ToListAsync(cancellationToken);
        var fulfilled = await context.ShipmentItems.AsNoTracking()
            .Where(x => x.OrderItem.ProductId == productId && x.Shipment.BusinessUnitId == businessUnitId &&
                x.Shipment.ActualDeliveryDate.HasValue)
            .SumAsync(x => (decimal?)x.Quantity, cancellationToken) ?? 0m;
        var observed = requestLines.Sum(x => (decimal)x.Quantity);
        var qualified = requestLines.Where(x => x.Quantity > 0 && x.Rfq.CustomerId.HasValue &&
            (!x.Rfq.BidClosingDate.HasValue || x.Rfq.BidClosingDate >= x.Rfq.RecDate))
            .Sum(x => (decimal)x.Quantity);
        var quoted = quoteLines.Sum(x => x.Quantity);
        var committed = orderLines.Sum(x => x.Quantity);
        var conversion = product.DecidedCount == 0 ? (decimal?)null : product.LineWinRatePercent;
        var weighted = decimal.Round(quoted * (conversion ?? 0m) / 100m, 4);
        var recentDemand = requestLines.Where(x => x.Rfq.RecDate >= DateTime.UtcNow.AddMonths(-12)).ToArray();
        var activeMonths = recentDemand.Select(x => new { x.Rfq.RecDate.Year, x.Rfq.RecDate.Month }).Distinct().Count();
        var supplierLeadTimes = await context.SupplierQuotedItems.AsNoTracking().Where(x =>
                x.BusinessUnitId == businessUnitId && x.ProductId == productId && x.LeadTimeDays.HasValue && x.IsActive)
            .Select(x => x.LeadTimeDays!.Value).ToArrayAsync(cancellationToken);
        var hasLeadTimeEvidence = supplierLeadTimes.Length >= 2;
        var demandConsistency = activeMonths >= 3;
        var eligible = CommercialLearningRules.CanRecommendStocking(product.DecidedCount, product.WonCount,
            demandConsistency, hasLeadTimeEvidence);
        var recommendation = eligible
            ? $"Stock review candidate: {activeMonths} active demand months and {supplierLeadTimes.Length} verified lead-time samples. Confirm margin, MOQ, carrying cost, shelf life and obsolescence before replenishment."
            : $"Awaiting sufficient evidence: {product.DecidedCount}/5 decided, {product.WonCount}/2 won, {activeMonths}/3 active demand months, {supplierLeadTimes.Length}/2 lead-time samples.";
        return new InventoryDemandMemory(productId, product.PartNumber, product.ProductName, observed, qualified,
            quoted, weighted, committed, fulfilled, product.DecidedCount, product.WonCount, conversion, eligible,
            recommendation, product.Evidence);
    }

    public async Task<CustomerCommercialMemory> GetCustomerAsync(long businessUnitId, long customerId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var customer = await context.Customers.AsNoTracking().SingleOrDefaultAsync(x => x.Buid == businessUnitId &&
            x.Id == customerId, cancellationToken) ?? throw new KeyNotFoundException("Customer was not found in this tenant.");
        var inquiryCount = await context.Leads.AsNoTracking().CountAsync(x => x.BusinessUnitId == businessUnitId &&
            x.CustomerId == customerId, cancellationToken);
        var quotes = await context.Quotes.AsNoTracking().Include(x => x.Status).Include(x => x.Currency)
            .Where(x => x.BusinessUnitId == businessUnitId && x.CustomerId == customerId).ToListAsync(cancellationToken);
        var orderWins = await context.Orders.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId
                && x.CustomerId == customerId && x.SourceType == OrderSourceTypes.CustomerAward && x.QuoteId.HasValue)
            .GroupBy(x => x.QuoteId!.Value).ToDictionaryAsync(x => x.Key,
                x => x.Max(order => order.OrderDate), cancellationToken);
        var decided = quotes.Where(x => x.OutcomeOn.HasValue || orderWins.ContainsKey(x.Id)).ToArray();
        var won = decided.Where(x => orderWins.ContainsKey(x.Id) || Outcome(x) == "WON").ToArray();
        var lost = decided.Where(x => !orderWins.ContainsKey(x.Id) && Outcome(x) is "LOST" or "EXPIRED").ToArray();
        var reasonIds = lost.Where(x => x.OutcomeReasonId.HasValue).Select(x => x.OutcomeReasonId!.Value).Distinct().ToArray();
        var reasons = await context.SetupMasters.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            reasonIds.Contains(x.SetupId)).ToDictionaryAsync(x => x.SetupId,
            x => new { Code = x.SetupCode ?? "UNSPECIFIED", Label = x.Description ?? x.SetupValue ?? "Unspecified" }, cancellationToken);
        var reasonCounts = lost.GroupBy(x => x.OutcomeReasonId).Select(group =>
        {
            var reason = group.Key.HasValue ? reasons.GetValueOrDefault(group.Key.Value) : null;
            return new CommercialReasonCount(reason?.Code ?? "UNSPECIFIED", reason?.Label ?? "Reason not recorded", group.Count());
        }).OrderByDescending(x => x.Count).ToArray();
        var currencyNames = quotes.Where(x => x.CurrencyId.HasValue).GroupBy(x => x.CurrencyId!.Value)
            .ToDictionary(x => x.Key, x => x.First().Currency?.Code ?? $"Currency {x.Key}");
        var values = won.Where(x => x.CurrencyId.HasValue && x.TotalAmount.HasValue)
            .Select(x => new ValuePoint(x.CurrencyId!.Value, x.TotalAmount!.Value,
                x.OutcomeOn ?? orderWins[x.Id], x.Id));
        return new CustomerCommercialMemory(customerId, customer.Name, inquiryCount, quotes.Count, decided.Length,
            won.Length, lost.Length, quotes.Count - decided.Length,
            decided.Length == 0 ? null : decimal.Round(100m * won.Length / decided.Length, 2),
            Summaries(values, currencyNames), reasonCounts,
            decided.OrderByDescending(x => x.OutcomeOn ?? orderWins.GetValueOrDefault(x.Id)).Take(50)
                .Select(x => new CommercialEvidenceLink("CustomerQuote", x.Id, x.QuoteNo,
                    x.OutcomeOn ?? orderWins.GetValueOrDefault(x.Id),
                    orderWins.ContainsKey(x.Id) ? "CUSTOMER_ORDER_WIN" : Outcome(x) + "_OUTCOME")).ToArray());
    }

    public async Task<IReadOnlyCollection<CustomerCommercialMemory>> GetCustomersAsync(long businessUnitId,
        int limit, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var customerIds = await context.Quotes.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
                x.CustomerId.HasValue)
            .GroupBy(x => x.CustomerId!.Value).OrderByDescending(x => x.Count()).Take(Math.Clamp(limit, 1, 200))
            .Select(x => x.Key).ToArrayAsync(cancellationToken);
        var results = new List<CustomerCommercialMemory>(customerIds.Length);
        foreach (var customerId in customerIds)
            results.Add(await GetCustomerAsync(businessUnitId, customerId, cancellationToken));
        return results;
    }

    public async Task<SalesRepCommercialMemory> GetSalesRepAsync(long businessUnitId, long userId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var user = await context.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Buid == businessUnitId &&
            x.Id == userId, cancellationToken) ?? throw new KeyNotFoundException("Sales Rep was not found in this tenant.");
        var assignments = await context.Set<ERP_RFQ_Automation.CommercialRouting.LeadAssignment>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.ToUserId == userId).ToListAsync(cancellationToken);
        var leadIds = assignments.Select(x => x.LeadId).Distinct().ToArray();
        var rfqLead = await context.Rfqs.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.LeadId.HasValue && leadIds.Contains(x.LeadId.Value))
            .Select(x => new { x.Id, LeadId = x.LeadId!.Value, x.RecDate }).ToArrayAsync(cancellationToken);
        var rfqIds = rfqLead.Select(x => x.Id).ToArray();
        var rfqById = rfqLead.ToDictionary(x => x.Id);
        var quotes = await context.Quotes.AsNoTracking().Include(x => x.Status).Where(x => x.BusinessUnitId == businessUnitId &&
            x.Rfqid.HasValue && rfqIds.Contains(x.Rfqid.Value)).ToListAsync(cancellationToken);
        var quoteIds = quotes.Select(x => x.Id).ToArray();
        var orderWins = await context.Orders.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId
                && x.SourceType == OrderSourceTypes.CustomerAward && x.QuoteId.HasValue
                && quoteIds.Contains(x.QuoteId.Value))
            .GroupBy(x => x.QuoteId!.Value).ToDictionaryAsync(x => x.Key,
                x => x.Max(order => order.OrderDate), cancellationToken);
        var ownedQuotes = quotes.Where(quote =>
        {
            var rfq = rfqById[quote.Rfqid!.Value];
            var asOf = quote.OutcomeOn ?? orderWins.GetValueOrDefault(quote.Id);
            if (asOf == default) asOf = DateTime.UtcNow;
            return assignments.Any(assignment => assignment.LeadId == rfq.LeadId &&
                assignment.EffectiveFrom <= asOf && (!assignment.EffectiveTo.HasValue || assignment.EffectiveTo > asOf));
        }).ToArray();
        var ownedQuoteIds = ownedQuotes.Select(x => x.Id).ToArray();
        var decided = ownedQuotes.Where(x => x.OutcomeOn.HasValue || orderWins.ContainsKey(x.Id)).ToArray();
        var won = decided.Where(x => orderWins.ContainsKey(x.Id) || Outcome(x) == "WON").ToArray();
        var lost = decided.Where(x => !orderWins.ContainsKey(x.Id) && Outcome(x) is "LOST" or "EXPIRED").ToArray();
        var reasonIds = lost.Where(x => x.OutcomeReasonId.HasValue).Select(x => x.OutcomeReasonId!.Value).Distinct().ToArray();
        var reasonCodes = await context.SetupMasters.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            reasonIds.Contains(x.SetupId)).ToDictionaryAsync(x => x.SetupId, x => x.SetupCode ?? "UNSPECIFIED", cancellationToken);
        string Reason(Quote quote) => quote.OutcomeReasonId.HasValue
            ? reasonCodes.GetValueOrDefault(quote.OutcomeReasonId.Value) ?? "UNSPECIFIED" : "UNSPECIFIED";
        var followUps = await context.FollowUpTasks.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.AssignedToUserId == userId).ToListAsync(cancellationToken);
        var activities = await context.CommercialActivities.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.SalesRepUserId == userId).ToListAsync(cancellationToken);
        var contributions = await context.SalesContributions.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.SalesRepUserId == userId).ToListAsync(cancellationToken);
        var firstActionHours = assignments.Select(assignment =>
        {
            var activity = activities.Where(x => x.LeadAssignmentId == assignment.Id &&
                    x.ActivityType != ERP_RFQ_Automation.CommercialIntelligence.Sales.CommercialActivityType.OpportunityCreated)
                .OrderBy(x => x.OccurredAtUtc).FirstOrDefault();
            return activity is null ? (decimal?)null : (decimal)(activity.OccurredAtUtc - assignment.EffectiveFrom).TotalHours;
        }).Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var turnaround = ownedQuotes.Where(x => x.CreatedDate.HasValue && x.Rfqid.HasValue)
            .Select(x => (decimal)(x.CreatedDate!.Value - rfqById[x.Rfqid!.Value].RecDate).TotalHours)
            .Where(x => x >= 0m).ToArray();
        var completedFollowUps = followUps.Count(x => x.Status == ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpStatus.Completed);
        var closedFollowUps = followUps.Count(x => x.Status is ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpStatus.Completed or ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpStatus.Cancelled);
        var weightedCoverage = CommercialLearningRules.CalculateWeightedCoverage(ownedQuoteIds,
            contributions.Where(x => x.AggregateType == "Quote" && quoteIds.Contains(x.AggregateId))
                .Select(x => (x.AggregateId, x.ContributionPercent)));
        var valueDecisions = decided.Where(x => x.TotalAmount.HasValue && x.CurrencyId.HasValue).ToArray();
        var singleValueCurrency = valueDecisions.Select(x => x.CurrencyId!.Value).Distinct().Count() == 1;
        var wonValue = won.Where(x => x.TotalAmount.HasValue && x.CurrencyId.HasValue).Sum(x => x.TotalAmount!.Value);
        var decidedValue = valueDecisions.Sum(x => x.TotalAmount!.Value);
        var coaching = decided.Length < 5 ? $"Awaiting sufficient evidence ({decided.Length}/5 decided opportunities)."
            : closedFollowUps > 0 && 100m * completedFollowUps / closedFollowUps < 80m
                ? "Review overdue and incomplete follow-up commitments."
                : lost.Count(x => CommercialLearningRules.ClassifyLoss(Reason(x)) == "EXECUTION_REVIEW") > 0
                    ? "Review execution-attributed losses with the Rep and linked evidence."
                    : "No evidence-backed coaching exception is currently above threshold.";
        return new SalesRepCommercialMemory(userId, $"{user.FirstName} {user.LastName}".Trim(),
            assignments.Count(x => !x.EffectiveTo.HasValue || x.EffectiveTo > DateTime.UtcNow),
            decided.Length, won.Length, lost.Length, lost.Count(x => CommercialLearningRules.ClassifyLoss(Reason(x)) == "COMMERCIAL_CONSTRAINT"),
            lost.Count(x => CommercialLearningRules.ClassifyLoss(Reason(x)) == "CUSTOMER_DECISION"),
            lost.Count(x => CommercialLearningRules.ClassifyLoss(Reason(x)) == "EXECUTION_REVIEW"),
            followUps.Count(x => x.Status is ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpStatus.Open or ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpStatus.InProgress),
            followUps.Count(x => x.Status == ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpStatus.Completed),
            decided.Length < 5 ? null : decimal.Round(100m * won.Length / decided.Length, 2), weightedCoverage,
            firstActionHours.Length == 0 ? null : decimal.Round(firstActionHours.Average(), 2),
            turnaround.Length == 0 ? null : decimal.Round(turnaround.Average(), 2),
            closedFollowUps == 0 ? null : decimal.Round(100m * completedFollowUps / closedFollowUps, 2),
            activities.Count(x => x.ActivityType is ERP_RFQ_Automation.CommercialIntelligence.Sales.CommercialActivityType.Note or
                ERP_RFQ_Automation.CommercialIntelligence.Sales.CommercialActivityType.Meeting),
            valueDecisions.Length < 5 || !singleValueCurrency || decidedValue <= 0m ? null
                : decimal.Round(100m * wonValue / decidedValue, 2), coaching,
            decided.OrderByDescending(x => x.OutcomeOn ?? orderWins.GetValueOrDefault(x.Id)).Take(50)
                .Select(x => new CommercialEvidenceLink("CustomerQuote", x.Id, x.QuoteNo,
                    x.OutcomeOn ?? orderWins.GetValueOrDefault(x.Id),
                    orderWins.ContainsKey(x.Id) ? "CUSTOMER_ORDER_WIN" : Outcome(x) + ":" + Reason(x))).ToArray());
    }

    public async Task<IReadOnlyCollection<SalesRepCommercialMemory>> GetSalesRepsAsync(long businessUnitId,
        int limit, CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var userIds = await context.Set<ERP_RFQ_Automation.CommercialRouting.LeadAssignment>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId)
            .GroupBy(x => x.ToUserId).OrderByDescending(x => x.Count()).Take(Math.Clamp(limit, 1, 200))
            .Select(x => x.Key).ToArrayAsync(cancellationToken);
        var results = new List<SalesRepCommercialMemory>(userIds.Length);
        foreach (var userId in userIds)
            results.Add(await GetSalesRepAsync(businessUnitId, userId, cancellationToken));
        return results;
    }

    public async Task<CommercialMemoryCard> GetLineCardAsync(long businessUnitId, long rfqItemId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var line = await context.Rfqitems.AsNoTracking().Include(x => x.Rfq).SingleOrDefaultAsync(x =>
            x.Id == rfqItemId && x.Rfq.BusinessUnitId == businessUnitId, cancellationToken)
            ?? throw new KeyNotFoundException("RFQ line was not found in this tenant.");
        ProductCommercialMemory? product = null;
        InventoryDemandMemory? inventory = null;
        if (line.ProductId.HasValue)
        {
            product = await GetProductAsync(businessUnitId, line.ProductId.Value, cancellationToken);
            inventory = await GetInventoryDemandAsync(businessUnitId, line.ProductId.Value, cancellationToken);
        }
        var supplierIds = await context.SupplierQuotedItems.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.RfqItemId == rfqItemId && x.SourceSupplierQuoteId.HasValue).Select(x => x.SupplierId).Distinct().ToArrayAsync(cancellationToken);
        var suppliers = new List<SupplierCommercialEvaluation>();
        foreach (var supplierId in supplierIds) suppliers.Add(await GetSupplierAsync(businessUnitId, supplierId, cancellationToken));
        var next = product is null ? "Resolve the Product identity before using commercial memory."
            : product.DecidedCount < 3 ? "Capture more verified outcomes before using predictive recommendations."
            : product.WonCount == 0 ? "Review recorded loss factors and broaden executable supplier coverage."
            : "Use the evidence ranges as decision support; current quote facts remain authoritative.";
        return new CommercialMemoryCard(line.Rfq.NexoraSerial ?? $"RFQ-{line.Rfqid}", line.Rfqid, line.Id,
            product, inventory, suppliers, next);
    }

    public async Task<RfqCommercialIntelligence> GetRfqIntelligenceAsync(long businessUnitId, long rfqId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var rfq = await context.Rfqs.AsNoTracking().Include(x => x.Rfqitems)
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.Id == rfqId, cancellationToken)
            ?? throw new KeyNotFoundException("RFQ was not found in this tenant.");
        var lines = rfq.Rfqitems.OrderBy(x => x.Id).ToArray();
        var lineIds = lines.Select(x => x.Id).ToArray();
        var productIds = lines.Where(x => x.ProductId.HasValue).Select(x => x.ProductId!.Value).Distinct().ToArray();
        var inventory = await context.Set<Models.Inventory>().AsNoTracking().Where(x => x.Buid == businessUnitId &&
            x.ProductId.HasValue && productIds.Contains(x.ProductId.Value)).ToArrayAsync(cancellationToken);
        var inventoryIds = inventory.Select(x => x.Id).ToArray();
        var reservations = await context.StockReservations.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
                inventoryIds.Contains(x.InventoryId) && x.Status == StockReservationStatus.Active)
            .GroupBy(x => x.InventoryId).Select(x => new { x.Key, Quantity = x.Sum(y => y.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Quantity, cancellationToken);
        var availableByProduct = inventory.GroupBy(x => x.ProductId!.Value).ToDictionary(x => x.Key,
            x => x.Sum(stock => Math.Max(0m, stock.QtyOnHand - reservations.GetValueOrDefault(stock.Id) -
                stock.AllocatedQuantity - stock.QuarantineQuantity - stock.DamagedQuantity -
                stock.ExpiredQuantity - stock.SafetyStockQuantity)));
        var offers = await context.SupplierQuotedItems.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.RfqId == rfqId && x.RfqItemId.HasValue && lineIds.Contains(x.RfqItemId.Value) && x.IsActive)
            .ToArrayAsync(cancellationToken);
        var supplierIds = offers.Select(x => x.SupplierId).Distinct().ToArray();
        var suppliers = await context.Suppliers.AsNoTracking().Where(x => x.Buid == businessUnitId &&
            supplierIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);
        var revisionIds = offers.Where(x => x.SourceSupplierQuoteRevisionId.HasValue)
            .Select(x => x.SourceSupplierQuoteRevisionId!.Value).Distinct().ToArray();
        var revisions = await context.SupplierQuoteRevisions.AsNoTracking().Include(x => x.Lines)
            .Include(x => x.ReviewDecisions)
            .Where(x => x.BusinessUnitId == businessUnitId && revisionIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);
        var sourceQuoteIds = offers.Where(x => x.SourceSupplierQuoteId.HasValue)
            .Select(x => x.SourceSupplierQuoteId!.Value).Distinct().ToArray();
        var quoteStatuses = await context.SupplierQuotes.AsNoTracking().Where(x =>
                x.BusinessUnitId == businessUnitId && sourceQuoteIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.InboxStatus, cancellationToken);
        var bidFlags = offers.GroupBy(x => x.SupplierId)
            .SelectMany(group => BuildBidQuality(group.ToArray(), revisions, quoteStatuses,
                suppliers.GetValueOrDefault(group.Key)).Flags)
            .GroupBy(x => x.SupplierQuotedItemId).ToDictionary(x => x.Key, x => (IReadOnlyCollection<BidQualityFlag>)x.ToArray());
        var awardRows = await context.Set<ERP_RFQ_Automation.Agent.Models.SourcingAward>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.RfqId == rfqId && x.RfqItemId.HasValue &&
                x.Status != "CANCELLED" && x.Status != "REJECTED")
            .ToArrayAsync(cancellationToken);
        var offersById = offers.ToDictionary(x => x.Id);
        var linesById = lines.ToDictionary(x => x.Id);
        var awarded = awardRows.Where(award => award.SupplierQuotedItemId.HasValue && award.Quantity > 0m &&
                offersById.TryGetValue(award.SupplierQuotedItemId.Value, out var offer) &&
                linesById.TryGetValue(award.RfqItemId!.Value, out var line) &&
                IsOfferEvidenceCurrent(offer, suppliers.GetValueOrDefault(offer.SupplierId), revisions,
                    quoteStatuses, line.RequiredDesiredDate) &&
                Math.Min(offer.Quantity, offer.AvailableQuantity ?? 0m) >= award.Quantity.Value)
            .GroupBy(x => x.RfqItemId!.Value)
            .ToDictionary(x => x.Key, x => x.Sum(y => y.Quantity ?? 0m));

        var remainingStock = new Dictionary<long, decimal>(availableByProduct);
        var lineResults = new List<RfqLineIntelligence>(lines.Length);
        foreach (var line in lines)
        {
            var requested = (decimal)line.Quantity;
            var stock = line.ProductId.HasValue
                ? Math.Min(requested, remainingStock.GetValueOrDefault(line.ProductId.Value)) : 0m;
            if (line.ProductId.HasValue) remainingStock[line.ProductId.Value] =
                Math.Max(0m, remainingStock.GetValueOrDefault(line.ProductId.Value) - stock);
            var awardQuantity = awarded.GetValueOrDefault(line.Id);
            var unfulfilled = Math.Max(0m, requested - stock - awardQuantity);
            var lineOffers = offers.Where(x => x.RfqItemId == line.Id).ToArray();
            var eligibleOffers = lineOffers.Where(x => IsOfferDecisionReady(x, suppliers.GetValueOrDefault(x.SupplierId),
                revisions, quoteStatuses, unfulfilled, line.RequiredDesiredDate)).ToArray();
            var blockers = new List<string>();
            if (!line.ProductId.HasValue) blockers.Add("Product identity requires review");
            if (requested <= 0m) blockers.Add("Requested quantity is invalid");
            if (string.IsNullOrWhiteSpace(line.UnitOfMeasure)) blockers.Add("Unit of measure requires review");
            if (string.IsNullOrWhiteSpace(line.ItemMaterialCode) &&
                string.IsNullOrWhiteSpace(line.ManufacturerPartNumber) &&
                string.IsNullOrWhiteSpace(line.ProductShortDescription))
                blockers.Add("Part or item identity requires review");
            if (unfulfilled > 0m && eligibleOffers.Length == 0) blockers.Add("No evidence-complete Supplier offer covers the remaining demand");
            if (unfulfilled > 0m && eligibleOffers.Length > 0) blockers.Add("Select and approve a Supplier offer for the remaining demand");
            if (awardQuantity > 0m && unfulfilled > 0m) blockers.Add("Supplier award covers only part of the remaining demand");
            var route = unfulfilled <= 0m && stock >= requested ? "STOCK_ONLY"
                : unfulfilled <= 0m && stock > 0m ? "SPLIT_STOCK_SOURCE"
                : unfulfilled <= 0m ? "SUPPLIER_ONLY" : stock > 0m ? "PARTIAL_REQUIRES_SOURCE" : "SOURCE_REQUIRED";
            lineResults.Add(new RfqLineIntelligence(line.Id,
                line.ManufacturerPartNumber ?? line.ItemMaterialCode ?? $"Line {line.Id}", requested, stock,
                unfulfilled, route, lineOffers.Length, eligibleOffers.Length, blockers,
                lineOffers.SelectMany(x => bidFlags.GetValueOrDefault(x.Id) ?? []).ToArray()));
        }

        var globalBlockers = new List<string>();
        if (!rfq.CustomerId.HasValue) globalBlockers.Add("Customer identity requires review");
        if (!rfq.LeadId.HasValue) globalBlockers.Add("Canonical Lead lineage is missing");
        if (string.IsNullOrWhiteSpace(rfq.NexoraSerial)) globalBlockers.Add("Nexora Serial lineage is missing");
        var allBlockers = globalBlockers.Concat(lineResults.SelectMany(x => x.Blockers)).ToArray();
        var deadline = rfq.BidClosingDate;
        var slaRisk = !deadline.HasValue ? "DEADLINE_NOT_RECORDED"
            : deadline <= DateTime.UtcNow ? "OVERDUE"
            : deadline <= DateTime.UtcNow.AddHours(24) ? "AT_RISK" : "ON_TRACK";
        var clarification = globalBlockers.Count > 0 || lineResults.Any(x => x.RequestedQuantity <= 0m ||
            x.PartNumber.StartsWith("Line ", StringComparison.Ordinal));
        var fulfilmentCoverage = lines.Length == 0 ? 0m :
            lineResults.Average(x => x.RequestedQuantity <= 0m ? 0m :
                Math.Min(1m, (x.RequestedQuantity - x.UnfulfilledQuantity) / x.RequestedQuantity));
        var readiness = decimal.Round((rfq.CustomerId.HasValue ? 15m : 0m) +
            (!string.IsNullOrWhiteSpace(rfq.NexoraSerial) ? 10m : 0m) + 55m * fulfilmentCoverage +
            (lines.Length > 0 ? 10m : 0m) + (slaRisk == "ON_TRACK" ? 10m : slaRisk == "AT_RISK" ? 5m : 0m), 2);
        var decision = lines.Length == 0 || slaRisk == "OVERDUE" ? "NO_QUOTE_REVIEW"
            : allBlockers.Length == 0 && lineResults.All(x => x.UnfulfilledQuantity == 0m)
                ? "VIABLE_READY" : "ACTIONABLE_WITH_BLOCKERS";
        var evidence = lineResults.Select(x => new CommercialEvidenceLink("RfqItem", x.RfqItemId,
            x.PartNumber, rfq.RecDate, x.FulfilmentRoute)).Take(50).ToArray();
        var next = decision == "VIABLE_READY"
            ? new ExplainableRecommendation("PREPARE_CUSTOMER_QUOTE", "Prepare Customer Quote",
                "Every RFQ line has a current evidence-backed fulfilment route.", .95m, true,
                $"/sales/quotes/create?rfqId={rfqId}", evidence)
            : decision == "NO_QUOTE_REVIEW"
                ? new ExplainableRecommendation("REVIEW_NO_QUOTE", "Review no-quote decision",
                    $"{allBlockers.Length} blockers remain and the customer deadline is {slaRisk.ToLowerInvariant().Replace('_', ' ')}.",
                    .9m, true, $"/procurement/rfqs/view/{rfqId}", evidence)
                : new ExplainableRecommendation("RECOVER_COVERAGE", "Recover line coverage",
                    $"Resolve {lineResults.Count(x => x.Blockers.Count > 0)} blocked lines before quoting.", .9m,
                    true, $"/procurement/rfqs/{rfqId}/sourcing", evidence);
        var pricingDecisions = await context.CustomerQuoteSourcingDecisions.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.RfqId == rfqId && lineIds.Contains(x.RfqItemId))
            .OrderByDescending(x => x.CreatedOn).ThenByDescending(x => x.Id).ToArrayAsync(cancellationToken);
        var currencyIds = offers.Where(x => x.CurrencyId.HasValue).Select(x => x.CurrencyId!.Value)
            .Concat(pricingDecisions.Select(x => x.CurrencyId))
            .Concat(lines.Where(x => x.CurrencyId.HasValue).Select(x => x.CurrencyId!.Value))
            .Distinct().ToArray();
        var currencyCodes = await context.Currencies.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && currencyIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, x => x.Code, cancellationToken);
        var predictivePricing = await BuildPredictivePricingAsync(businessUnitId, rfq.CustomerId,
            lines, currencyCodes, cancellationToken);
        return new RfqCommercialIntelligence(rfq.Id, rfq.Rfqno, rfq.NexoraSerial ?? $"RFQ-{rfq.Id}",
            readiness, decision, slaRisk, clarification, next, lineResults,
            BuildDigitalTwin(lineResults, lines.ToDictionary(x => x.Id, x => x.RequiredDesiredDate),
                offers, suppliers, revisions, quoteStatuses, pricingDecisions, predictivePricing, evidence,
                currencyCodes));
    }

    public async Task<LearningStudioSummary> GetStudioAsync(long businessUnitId,
        CancellationToken cancellationToken = default)
    {
        EnsureTenant(businessUnitId);
        var decisions = await context.SupplierQuoteReviewDecisions.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Status == SupplierQuoteReviewStatuses.Corrected)
            .Join(context.SupplierQuoteFieldEvidence.AsNoTracking(), d => d.SupplierQuoteFieldEvidenceId, e => e.Id,
                (d, e) => new { d, e }).OrderByDescending(x => x.d.ReviewedOn).Take(200).ToListAsync(cancellationToken);
        var grouped = decisions.GroupBy(x => new { x.e.FieldName, x.e.OriginalValue }).ToArray();
        var conflicting = grouped.Count(group => group.Select(x => x.d.CorrectedValue).Distinct().Count() > 1);
        var templateCount = await context.SupplierQuoteRevisions.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId)
            .Select(x => x.SourceIdentity).Distinct().CountAsync(cancellationToken);
        var productStats = await context.QuoteItems.AsNoTracking().Where(x => x.Quote.BusinessUnitId == businessUnitId &&
            x.ProductId.HasValue).GroupBy(x => x.ProductId!.Value).Select(x => new
            { ProductId = x.Key, Decided = x.Count(v => v.Quote.OutcomeOn != null) }).ToArrayAsync(cancellationToken);
        var signals = await new LearningGovernanceService(context)
            .BuildSignalsAsync(businessUnitId, cancellationToken);
        return new LearningStudioSummary(DateTime.UtcNow, decisions.Count, conflicting, templateCount,
            productStats.Count(x => x.Decided >= 3), productStats.Count(x => x.Decided < 3), signals);
    }

    private static bool IsOfferEvidenceCurrent(SupplierQuotedItem offer, Supplier? supplier,
        IReadOnlyDictionary<long, SupplierQuoteRevision> revisions,
        IReadOnlyDictionary<long, string> quoteStatuses, DateTime? requiredOn)
    {
        if (offer.SourceSupplierQuoteId is null || offer.SourceSupplierQuoteRevisionId is null ||
            offer.SourceSupplierQuoteLineId is null ||
            !revisions.TryGetValue(offer.SourceSupplierQuoteRevisionId.Value, out var revision) ||
            !quoteStatuses.TryGetValue(offer.SourceSupplierQuoteId.Value, out var quoteStatus) ||
            quoteStatus != SupplierQuoteInboxStatuses.ReadyForComparison ||
            !revision.Lines.Any(x => x.Id == offer.SourceSupplierQuoteLineId)) return false;
        if (supplier is null || supplier.GovernanceStatus is not (SupplierGovernanceStatuses.Approved or
            SupplierGovernanceStatuses.Preferred or SupplierGovernanceStatuses.Provisional) ||
            supplier.ReadinessStatus != SupplierReadinessStatuses.Ready ||
            supplier.ComplianceStatus is "BLOCKED" or "FAILED" or "RESTRICTED" ||
            supplier.RiskStatus is "BLOCKED" or "HIGH") return false;
        if (requiredOn.HasValue && (!offer.QuoteDate.HasValue || !offer.LeadTimeDays.HasValue ||
                offer.QuoteDate.Value.AddDays(offer.LeadTimeDays.Value) > requiredOn.Value)) return false;
        var latestCorrection = revision.ReviewDecisions.GroupBy(x => x.SupplierQuoteFieldEvidenceId)
            .Select(x => x.OrderByDescending(y => y.ReviewedOn).ThenByDescending(y => y.Id).First())
            .Where(x => x.Status == SupplierQuoteReviewStatuses.Corrected)
            .Select(x => (DateTime?)x.ReviewedOn).Max();
        if (latestCorrection > (offer.ModifiedDate ?? offer.CreatedDate)) return false;
        return offer.UnitPrice > 0m && offer.CurrencyId > 0 && offer.LandedUnitCost > 0m &&
            offer.LeadTimeDays is >= 0 && offer.ValidUntil > DateTime.UtcNow;
    }

    private static bool IsOfferDecisionReady(SupplierQuotedItem offer, Supplier? supplier,
        IReadOnlyDictionary<long, SupplierQuoteRevision> revisions,
        IReadOnlyDictionary<long, string> quoteStatuses, decimal requiredQuantity, DateTime? requiredOn = null)
    {
        if (requiredQuantity <= 0m || !IsOfferEvidenceCurrent(offer, supplier, revisions, quoteStatuses, requiredOn))
            return false;
        return Math.Min(offer.Quantity, offer.AvailableQuantity ?? 0m) >= requiredQuantity;
    }

    private async Task<IReadOnlyCollection<PredictivePriceLine>> BuildPredictivePricingAsync(
        long businessUnitId, long? customerId, IReadOnlyCollection<Rfqitem> currentLines,
        IReadOnlyDictionary<long, string> currencyCodes,
        CancellationToken cancellationToken)
    {
        var productIds = currentLines.Where(x => x.ProductId.HasValue)
            .Select(x => x.ProductId!.Value).Distinct().ToArray();
        if (productIds.Length == 0) return currentLines.Select(x => new PredictivePriceLine(x.Id,
            "PRODUCT_IDENTITY_REQUIRED", "SHADOW", null, null, null, null, null, 0, 0, null,
            "Exact Product identity is required before historical pricing can be reconciled.",
            ["No fuzzy Product matching is used for pricing."], [])).ToArray();

        var cutoff = DateTime.UtcNow.AddMonths(-24);
        var historyRows = await context.QuoteItems.AsNoTracking()
            .Where(x => x.Quote.BusinessUnitId == businessUnitId && x.ProductId.HasValue &&
                productIds.Contains(x.ProductId.Value) && x.Quote.CurrencyId.HasValue &&
                x.UnitPrice > 0m && (x.Quote.QuoteDate ?? x.Quote.CreatedDate ?? x.CreatedDate) >= cutoff)
            .OrderByDescending(x => x.Quote.QuoteDate ?? x.Quote.CreatedDate ?? x.CreatedDate)
            .Take(Math.Min(10_000, Math.Max(2_000, productIds.Length * 500)))
            .Select(x => new PricingHistoryRow(x.QuoteId, x.ProductId!.Value, x.Quote.CustomerId,
                x.Quote.CurrencyId!.Value, x.Quantity, x.UnitPrice, x.DeliveryLeadTime,
                x.Quote.QuoteDate ?? x.Quote.CreatedDate ?? x.CreatedDate ?? DefaultPeriodFrom,
                x.Quote.QuoteNo, x.Quote.OutcomeOn))
            .ToArrayAsync(cancellationToken);
        var history = historyRows.GroupBy(x => x.ProductId)
            .SelectMany(group => group.OrderByDescending(x => x.OccurredOn).Take(250)).ToArray();
        var quoteIds = history.Select(x => x.QuoteId).Distinct().ToArray();
        var orderedQuoteIds = quoteIds.Length == 0 ? new HashSet<long>() : (await context.Orders.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.SourceType == OrderSourceTypes.CustomerAward &&
                x.QuoteId.HasValue && quoteIds.Contains(x.QuoteId.Value))
            .Select(x => x.QuoteId!.Value).Distinct().ToArrayAsync(cancellationToken)).ToHashSet();

        var result = new List<PredictivePriceLine>(currentLines.Count);
        foreach (var line in currentLines.OrderBy(x => x.Id))
        {
            if (!line.ProductId.HasValue || !line.CurrencyId.HasValue)
            {
                result.Add(new PredictivePriceLine(line.Id, "CURRENCY_OR_PRODUCT_REQUIRED", "SHADOW",
                    null, null, null, null, null, 0, 0, null,
                    "Exact Product and currency evidence are required.",
                    ["No unstamped currency or cross-currency conversion is inferred."], []));
                continue;
            }

            var productCohort = history.Where(x => x.ProductId == line.ProductId.Value &&
                    x.CurrencyId == line.CurrencyId.Value)
                .GroupBy(x => x.QuoteId)
                .Select(group =>
                {
                    var rows = group.OrderBy(x => x.OccurredOn).ToArray();
                    var quantity = rows.Sum(x => Math.Max(0m, x.Quantity));
                    var unitPrice = quantity > 0m
                        ? rows.Sum(x => x.UnitPrice * Math.Max(0m, x.Quantity)) / quantity
                        : rows.Average(x => x.UnitPrice);
                    var sample = rows[^1];
                    var won = orderedQuoteIds.Contains(group.Key);
                    return new PricingOutcomeRow(group.Key, sample.CustomerId, sample.CurrencyId,
                        quantity, decimal.Round(unitPrice, 6), sample.OccurredOn, sample.QuoteNumber,
                        won, won || rows.Any(x => x.OutcomeOn.HasValue));
                })
                .Where(x => x.IsDecided)
                .OrderBy(x => x.OccurredOn)
                .ToArray();
            var customerCohort = customerId.HasValue
                ? productCohort.Where(x => x.CustomerId == customerId).ToArray() : [];
            var cohort = customerCohort.Length >= 3 ? customerCohort : productCohort;
            var quantityComparable = cohort.Where(x => x.Quantity > 0m && line.Quantity > 0 &&
                Math.Min(x.Quantity, (decimal)line.Quantity) / Math.Max(x.Quantity, (decimal)line.Quantity) >= .25m)
                .ToArray();
            if (quantityComparable.Length >= 3) cohort = quantityComparable;
            var wins = cohort.Where(x => x.IsCustomerOrderWin)
                .OrderBy(x => x.OccurredOn).ToArray();
            var prices = wins.Select(x => x.UnitPrice).OrderBy(x => x).ToArray();
            var sufficient = wins.Length >= 3;
            decimal? mape = null;
            var holdoutCount = 0;
            if (wins.Length >= 4)
            {
                var errors = new List<decimal>();
                for (var index = 3; index < wins.Length; index++)
                {
                    var holdout = wins[index];
                    var training = wins.Take(index).Select(x => x.UnitPrice).OrderBy(x => x).ToArray();
                    var predicted = Median(training);
                    errors.Add(holdout.UnitPrice == 0m ? 0m :
                        Math.Abs(predicted - holdout.UnitPrice) / holdout.UnitPrice * 100m);
                }
                holdoutCount = errors.Count;
                mape = decimal.Round(errors.Average(), 2);
            }
            var evidence = wins.OrderByDescending(x => x.OccurredOn).Take(10)
                .Select(x => new CommercialEvidenceLink("CustomerQuote", x.QuoteId, x.QuoteNumber,
                    x.OccurredOn, "ACTUAL_CUSTOMER_ORDER")).ToArray();
            result.Add(new PredictivePriceLine(line.Id, sufficient ? "READY_SHADOW" : "INSUFFICIENT_EVIDENCE",
                "SHADOW", sufficient ? Median(prices) : null, wins.LastOrDefault()?.UnitPrice,
                sufficient ? prices.First() : null, sufficient ? prices.Last() : null,
                null, cohort.Length, wins.Length, mape,
                $"Exact Product {line.ProductId.Value}; currency {line.CurrencyId.Value}; quantity {line.Quantity}; " +
                $"{(customerCohort.Length >= 3 ? "same Customer" : "tenant Product fallback")}; trailing 24 months; " +
                $"requested lead time {(line.RequiredDesiredDate.HasValue ? "recorded" : "not recorded") }.",
                ["Actual Customer Orders define wins.", "No FX or external market feed is used.",
                    "Open and undecided Quotes are excluded.",
                    "Prediction is advisory and cannot confirm a price."], evidence, holdoutCount,
                cohort.Length >= 3 ? decimal.Round((wins.Length + 1m) / (cohort.Length + 2m), 4) : null,
                line.CurrencyId, currencyCodes.GetValueOrDefault(line.CurrencyId.Value)));
        }
        return result;
    }

    private static OpportunityDigitalTwin BuildDigitalTwin(IReadOnlyCollection<RfqLineIntelligence> lines,
        IReadOnlyDictionary<long, DateTime?> requiredDates,
        IReadOnlyCollection<SupplierQuotedItem> offers, IReadOnlyDictionary<long, Supplier> suppliers,
        IReadOnlyDictionary<long, SupplierQuoteRevision> revisions,
        IReadOnlyDictionary<long, string> quoteStatuses,
        IReadOnlyCollection<CustomerQuoteSourcingDecision> pricingDecisions,
        IReadOnlyCollection<PredictivePriceLine> predictivePricing,
        IReadOnlyCollection<CommercialEvidenceLink> rfqEvidence,
        IReadOnlyDictionary<long, string> currencyCodes)
    {
        var scenarios = new List<OpportunityScenario>();
        var stockOnly = lines.Count > 0 && lines.All(x => x.StockQuantity >= x.RequestedQuantity);
        scenarios.Add(new OpportunityScenario("STOCK_ONLY", "Stock only", stockOnly,
            stockOnly ? "Current tenant ATP covers every requested line." : "Current ATP does not cover every line.",
            null, null, 0, DateTime.UtcNow, null, null, "UNASSESSED",
            "Inventory cost and currency are not stamped on the current ATP evidence.", stockOnly ? .95m : 1m,
            lines.Select(x => new ScenarioQuantityAllocation(x.RfqItemId, x.RequestedQuantity,
                Math.Min(x.StockQuantity, x.RequestedQuantity), 0m, null, DateTime.UtcNow)).ToArray(),
            [new ScenarioCostSource("INTERNAL_STOCK", "Internal inventory cost", null, null,
                "EVIDENCE_REQUIRED", null)],
            ["ATP excludes active reservations, allocation, quarantine, damage, expiry and safety stock."],
            ["Confirm inventory cost currency before margin approval."], rfqEvidence));

        var latestDecisionByOffer = pricingDecisions
            .GroupBy(x => x.SupplierQuotedItemId)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.CreatedOn).ThenByDescending(y => y.Id).First());

        OpportunityScenario OfferScenario(string code, string label, bool preserveStock,
            Func<IEnumerable<SupplierQuotedItem>, IOrderedEnumerable<SupplierQuotedItem>> rank,
            bool requireReliability = false, bool requirePartialImmediate = false,
            bool requireAuthorizedMargin = false, Func<SupplierQuoteLine, bool>? lineFilter = null)
        {
            var selected = new List<(SupplierQuotedItem Offer, decimal SupplierQuantity,
                decimal StockQuantity, RfqLineIntelligence Line, CustomerQuoteSourcingDecision? Decision)>();
            foreach (var line in lines.OrderBy(x => x.RfqItemId))
            {
                var required = preserveStock ? Math.Max(0m, line.RequestedQuantity - line.StockQuantity) : line.RequestedQuantity;
                if (required == 0m) continue;
                var choices = offers.Where(x => x.RfqItemId == line.RfqItemId &&
                    IsOfferDecisionReady(x, suppliers.GetValueOrDefault(x.SupplierId), revisions,
                        quoteStatuses, required, requiredDates.GetValueOrDefault(line.RfqItemId)));
                if (requireReliability) choices = choices.Where(x => x.ReliabilitySnapshot.HasValue);
                if (requireAuthorizedMargin) choices = choices.Where(x => latestDecisionByOffer.ContainsKey(x.Id));
                if (lineFilter is not null) choices = choices.Where(offer =>
                    revisions[offer.SourceSupplierQuoteRevisionId!.Value].Lines.Any(canonical =>
                        canonical.Id == offer.SourceSupplierQuoteLineId && lineFilter(canonical)));
                var choice = rank(choices).ThenBy(x => x.Id).FirstOrDefault();
                if (choice is null)
                    return new OpportunityScenario(code, label, false,
                        "At least one line lacks an evidence-complete, governed offer with sufficient availability.",
                        null, null, null, null, null, null, "BLOCKED",
                        "Required offer, reliability, margin or approval evidence is incomplete.", 1m, [], [],
                        ["Only current canonical Supplier Quote revisions are eligible."],
                        [requireAuthorizedMargin ? "Record an authorized Customer Quote sourcing decision." :
                            "Select and approve the final Supplier offer."], rfqEvidence);
                selected.Add((choice, required, preserveStock ? Math.Min(line.StockQuantity, line.RequestedQuantity) : 0m,
                    line, latestDecisionByOffer.GetValueOrDefault(choice.Id)));
            }
            if (selected.Count == 0)
                return new OpportunityScenario(code, label, false,
                    "No Supplier quantity is required; use the stock-only route.", null, null, null, null,
                    null, null, "NOT_APPLICABLE", "Current ATP covers every requested line.", 1m, [], [],
                    ["Supplier comparison is not applicable when no Supplier quantity is required."],
                    ["Use the stock-only route and verify internal inventory cost before margin approval."], rfqEvidence);
            if (requirePartialImmediate && !selected.Any(x => x.StockQuantity > 0m && x.SupplierQuantity > 0m))
                return new OpportunityScenario(code, label, false,
                    "No line can combine immediate ATP with evidence-complete sourced balance.",
                    null, null, null, null, null, null, "BLOCKED",
                    "Partial immediate delivery requires both current ATP and a governed offer.", 1m, [], [],
                    ["Immediate quantity is never inferred without ATP."],
                    ["Confirm a split delivery promise with the Customer."], rfqEvidence);
            var currencies = selected.Select(x => x.Offer.CurrencyId!.Value).Distinct().ToArray();
            if (currencies.Length > 1)
                return new OpportunityScenario(code, label, false,
                    "Selected offers use different currencies and no approved FX snapshot was supplied.",
                    null, null, null, null, null, null, "BLOCKED",
                    "Cross-currency risk cannot be quantified without approved FX evidence.", 1m, [], [],
                    ["Cross-currency amounts are never ranked without verified FX evidence."],
                    ["Provide an approved FX snapshot or compare currencies separately."],
                    selected.Select(x => new CommercialEvidenceLink("SupplierQuotedItem", x.Offer.Id,
                        x.Offer.QuoteReference ?? $"Offer {x.Offer.Id}", x.Offer.QuoteDate, "SCENARIO_SOURCE")).ToArray());
            var cost = selected.Sum(x => x.Offer.LandedUnitCost!.Value * x.SupplierQuantity);
            var hasUnpricedStock = selected.Any(x => x.StockQuantity > 0m);
            var lead = selected.Max(x => x.Offer.LeadTimeDays!.Value);
            var delivery = selected.Max(x =>
                (x.Offer.QuoteDate ?? x.Offer.CreatedDate).AddDays(x.Offer.LeadTimeDays!.Value));
            var validity = selected.Min(x => x.Offer.ValidUntil);
            var margin = selected.Count > 0 && selected.All(x => x.Decision is not null)
                ? decimal.Round(selected.Sum(x => x.Decision!.TargetMarginPercent * x.SupplierQuantity) /
                    selected.Sum(x => x.SupplierQuantity), 2) : (decimal?)null;
            var reliability = selected.Where(x => x.Offer.ReliabilitySnapshot.HasValue)
                .Select(x => x.Offer.ReliabilitySnapshot!.Value).ToArray();
            var risk = selected.Count == 0 ? "NOT_APPLICABLE" : reliability.Length != selected.Count ? "UNASSESSED"
                : reliability.Average() >= 90m ? "LOW" : reliability.Average() >= 75m ? "MEDIUM" : "HIGH";
            return new OpportunityScenario(code, label, true,
                hasUnpricedStock
                    ? $"Route selected {selected.Count} current Supplier offer(s); total landed cost is withheld until internal stock cost is verified."
                    : $"Deterministic comparison selected {selected.Count} current Supplier offer(s).",
                hasUnpricedStock ? null : decimal.Round(cost, 4), currencies.Single(), lead, delivery, validity, margin, risk,
                risk == "NOT_APPLICABLE" ? "No Supplier quantity is required for this route."
                    : risk == "UNASSESSED" ? "Supplier reliability evidence is incomplete."
                    : $"Selected offer reliability snapshot averages {decimal.Round(reliability.Average(), 1)}%.", .9m,
                selected.Select(x => new ScenarioQuantityAllocation(x.Line.RfqItemId, x.Line.RequestedQuantity,
                    x.StockQuantity, x.SupplierQuantity, x.Offer.Id,
                    (x.Offer.QuoteDate ?? x.Offer.CreatedDate).AddDays(x.Offer.LeadTimeDays!.Value))).ToArray(),
                selected.Select(x => new ScenarioCostSource("SUPPLIER_LANDED", "Supplier landed cost",
                    decimal.Round(x.Offer.LandedUnitCost!.Value * x.SupplierQuantity, 4), x.Offer.CurrencyId,
                    "VERIFIED", new CommercialEvidenceLink("SupplierQuotedItem", x.Offer.Id,
                        x.Offer.QuoteReference ?? $"Offer {x.Offer.Id}", x.Offer.QuoteDate, "COST_SOURCE")))
                    .Concat(selected.Where(x => x.StockQuantity > 0m).Select(x => new ScenarioCostSource(
                        "INTERNAL_STOCK", "Internal inventory cost", null, null, "EVIDENCE_REQUIRED", null))).ToArray(),
                [preserveStock ? "Uses current ATP first and sources only the shortage." : "Sources the full requested quantity.",
                    "Freight, duty and other captured costs are included in landed cost."],
                margin.HasValue ? ["Authorized margin exists; final Quote approval policy still applies."] :
                    ["Select and approve the final fulfilment route.", "Confirm margin before Customer Quote approval."],
                selected.Select(x => new CommercialEvidenceLink("SupplierQuotedItem", x.Offer.Id,
                    x.Offer.QuoteReference ?? $"Offer {x.Offer.Id}", x.Offer.QuoteDate, "SCENARIO_SOURCE")).ToArray(),
                currencyCodes.GetValueOrDefault(currencies.Single()));
        }

        scenarios.Add(OfferScenario("SUPPLIER_ONLY", "Supplier only", false,
            rows => rows.OrderBy(x => x.LandedUnitCost).ThenBy(x => x.LeadTimeDays)));
        scenarios.Add(OfferScenario("SPLIT_STOCK_SOURCE", "Split stock and source", true,
            rows => rows.OrderBy(x => x.LandedUnitCost).ThenBy(x => x.LeadTimeDays)));
        scenarios.Add(OfferScenario("FASTEST_DELIVERY", "Fastest delivery", true,
            rows => rows.OrderBy(x => x.LeadTimeDays).ThenBy(x => x.LandedUnitCost)));
        scenarios.Add(OfferScenario("LOWEST_LANDED_COST", "Lowest landed cost", true,
            rows => rows.OrderBy(x => x.LandedUnitCost).ThenBy(x => x.LeadTimeDays)));
        scenarios.Add(OfferScenario("BEST_MARGIN", "Best verified margin", true,
            rows => rows.OrderByDescending(x => latestDecisionByOffer[x.Id].TargetMarginPercent)
                .ThenBy(x => x.LandedUnitCost), requireAuthorizedMargin: true));
        scenarios.Add(OfferScenario("LOWEST_RISK", "Lowest verified risk", true,
            rows => rows.OrderByDescending(x => x.ReliabilitySnapshot).ThenBy(x => x.LandedUnitCost),
            requireReliability: true));
        scenarios.Add(new OpportunityScenario("APPROVED_ALTERNATE", "Approved alternate", false,
            "No authoritative alternate-product approval evidence is recorded for this RFQ.",
            null, null, null, null, null, null, "BLOCKED",
            "Supplier alternate declarations are evidence, not Customer or engineering approval.", 1m, [], [],
            ["An alternate is never treated as approved from Supplier data alone."],
            ["Capture explicit alternate approval before recommendation."], []));
        scenarios.Add(OfferScenario("PARTIAL_IMMEDIATE", "Partial immediate delivery", true,
            rows => rows.OrderBy(x => x.LeadTimeDays).ThenBy(x => x.LandedUnitCost),
            requirePartialImmediate: true));

        var bridges = pricingDecisions.GroupBy(x => x.RfqItemId).Select(group => group
                .OrderByDescending(x => x.CreatedOn).ThenByDescending(x => x.Id).First())
            .Select(decision =>
            {
                var offer = offers.SingleOrDefault(x => x.Id == decision.SupplierQuotedItemId);
                var evidence = new CommercialEvidenceLink("CustomerQuoteSourcingDecision", decision.Id,
                    $"Pricing decision {decision.Id}", decision.CreatedOn, "TARGET_BRIDGE");
                var validOffer = offer is not null && offer.CurrencyId == decision.CurrencyId &&
                    offer.Quantity > 0m && decision.CustomerUnitPrice > 0m &&
                    decision.TargetMarginPercent is >= 0m and < 100m &&
                    IsOfferEvidenceCurrent(offer, suppliers.GetValueOrDefault(offer.SupplierId), revisions,
                        quoteStatuses, requiredDates.GetValueOrDefault(decision.RfqItemId));
                if (!validOffer)
                    return new CustomerTargetBridge(decision.RfqItemId, "STALE_OR_INVALID_EVIDENCE",
                        decision.CustomerUnitPrice, decision.TargetMarginPercent, null, null, null,
                        decision.CurrencyId, "Target bridge withheld until the selected offer is current, canonical, currency-matched and valid.", evidence);
                var maximumLanded = decimal.Round(decision.CustomerUnitPrice *
                    (1m - decision.TargetMarginPercent / 100m), 6);
                var adjustments = decimal.Round(
                    (offer!.FreightCost + offer.DutyCost + offer.OtherCost + (offer.TaxAmount ?? 0m) -
                        (offer.DiscountAmount ?? 0m)) / offer.Quantity, 6);
                var maximumSupplier = decimal.Round(maximumLanded - adjustments, 6);
                return new CustomerTargetBridge(decision.RfqItemId,
                    maximumSupplier > 0m ? "VERIFIED_SOURCING_DECISION" : "TARGET_INFEASIBLE",
                    decision.CustomerUnitPrice, decision.TargetMarginPercent, maximumLanded, adjustments,
                    maximumSupplier, decision.CurrencyId,
                    "max landed = target price x (1 - gross margin); max supplier = max landed - per-unit freight, duty, tax and other captured cost, plus discount",
                    evidence);
            }).ToArray();
        var holdouts = predictivePricing.Where(x => x.BacktestHoldoutCount > 0 &&
            x.BacktestMeanAbsolutePercentError.HasValue).ToArray();
        var holdoutCount = holdouts.Sum(x => x.BacktestHoldoutCount);
        return new OpportunityDigitalTwin(DateTime.UtcNow,
            "Valid until Supplier Quote expiry or any inventory, reservation, governance, or Quote revision change.",
            "SHADOW", "digital-twin-v2.3", scenarios, bridges, predictivePricing,
            new PricingBacktestSummary(holdoutCount > 0 ? "MEASURED" : "INSUFFICIENT_HOLDOUTS",
                holdoutCount, holdoutCount == 0 ? null : decimal.Round(
                    holdouts.Sum(x => x.BacktestMeanAbsolutePercentError!.Value * x.BacktestHoldoutCount) /
                    holdoutCount, 2),
                "Same tenant, exact Product and currency, trailing 24 months; actual Customer Orders are wins.",
                "Sparse cohorts are withheld; no FX or market feed is inferred."),
            "Review evidence, then use the governed Sourcing and Customer Quote workflows.");
    }

    private static SupplierBidQualitySummary BuildBidQuality(
        IReadOnlyCollection<SupplierQuotedItem> offers,
        IReadOnlyDictionary<long, SupplierQuoteRevision> revisions,
        IReadOnlyDictionary<long, string>? quoteStatuses = null,
        Supplier? supplier = null)
    {
        var now = DateTime.UtcNow;
        var medians = offers.Where(x => x.LandedUnitCost.HasValue)
            .GroupBy(x => new { x.RfqItemId, x.CurrencyId })
            .Where(group => group.Count() >= 3)
            .ToDictionary(group => group.Key,
                group => Median(group.Select(x => x.LandedUnitCost!.Value).OrderBy(x => x).ToArray()));
        var quoteRevisionCounts = offers.Where(x => x.SourceSupplierQuoteId.HasValue)
            .GroupBy(x => x.SourceSupplierQuoteId!.Value).ToDictionary(x => x.Key, x => x.Max(y => y.QuoteRevision));
        var flags = new List<BidQualityFlag>();
        var complete = 0;
        var eligible = 0;
        foreach (var offer in offers.OrderByDescending(x => x.QuoteDate).ThenBy(x => x.Id))
        {
            revisions.TryGetValue(offer.SourceSupplierQuoteRevisionId ?? 0, out var revision);
            var line = revision?.Lines.SingleOrDefault(x => x.Id == offer.SourceSupplierQuoteLineId);
            var evidence = new CommercialEvidenceLink("SupplierQuotedItem", offer.Id,
                offer.QuoteReference ?? $"Offer {offer.Id}", offer.QuoteDate ?? offer.CreatedDate, "BID_QUALITY");
            void Flag(string code, string severity, string reason, decimal confidence) => flags.Add(
                new BidQualityFlag(offer.Id, code, severity, reason, confidence, evidence,
                    offer.SourceSupplierQuoteId.HasValue
                        ? $"/procurement/supplier-quotes/{offer.SourceSupplierQuoteId.Value}"
                        : $"/procurement/sourcing/{offer.RfqId}"));

            if (!offer.ValidUntil.HasValue) Flag("MISSING_VALIDITY", "WARNING",
                "Supplier validity was not confirmed in the captured Quote.", 1m);
            else if (offer.ValidUntil.Value <= now) Flag("STALE_EVIDENCE", "BLOCKER",
                "Supplier validity has expired; refresh the offer before selection.", 1m);
            if (!offer.AvailableQuantity.HasValue) Flag("UNCONFIRMED_STOCK", "BLOCKER",
                "Available quantity was not confirmed by the Supplier.", 1m);
            if (!offer.LeadTimeDays.HasValue) Flag("MISSING_LEAD_TIME", "WARNING",
                "Supplier lead time was not captured.", 1m);
            else if (offer.LeadTimeDays.Value == 0) Flag("UNREALISTIC_LEAD_TIME", "WARNING",
                "Zero-day lead time requires verification before commitment.", .9m);
            if (revision is null || string.IsNullOrWhiteSpace(revision.PaymentTerms) ||
                string.IsNullOrWhiteSpace(revision.Incoterms))
                Flag("INCOMPLETE_TERMS", "WARNING", "Payment terms or Incoterms are missing.", 1m);
            if (line?.IsAlternate == true && (string.IsNullOrWhiteSpace(line.Manufacturer) ||
                string.IsNullOrWhiteSpace(line.SupplierPartNumber)))
                Flag("SUSPICIOUS_ALTERNATE", "BLOCKER",
                    "An alternate was offered without complete manufacturer and part evidence.", 1m);
            if (offer.SourceSupplierQuoteId.HasValue &&
                quoteRevisionCounts.GetValueOrDefault(offer.SourceSupplierQuoteId.Value) >= 3)
                Flag("REVISION_VOLATILITY", "WARNING",
                    "This Supplier Quote has three or more commercial revisions.", .95m);
            var key = new { offer.RfqItemId, offer.CurrencyId };
            if (offer.LandedUnitCost.HasValue && medians.TryGetValue(key, out var median) && median > 0m &&
                (offer.LandedUnitCost.Value > median * 1.5m || offer.LandedUnitCost.Value < median * .5m))
                Flag("PRICE_OUTLIER", "WARNING",
                    "Landed cost differs by more than 50% from at least three comparable offers.", .85m);

            var isComplete = offer.UnitPrice.HasValue && offer.CurrencyId.HasValue && offer.AvailableQuantity.HasValue &&
                offer.LeadTimeDays.HasValue && offer.ValidUntil.HasValue;
            if (isComplete) complete++;
            if (isComplete && supplier is not null && quoteStatuses is not null &&
                IsOfferDecisionReady(offer, supplier, revisions, quoteStatuses, .0001m)) eligible++;
        }
        return new SupplierBidQualitySummary(offers.Count, complete, eligible,
            offers.Count == 0 ? null : decimal.Round(100m * complete / offers.Count, 2),
            flags.Count(x => x.Code is "MISSING_VALIDITY" or "MISSING_LEAD_TIME" or "INCOMPLETE_TERMS"),
            flags.Count(x => x.Code == "PRICE_OUTLIER"), flags.Count(x => x.Code == "REVISION_VOLATILITY"), flags);
    }

    private void EnsureTenant(long businessUnitId)
    {
        if (businessUnitId <= 0 || context.ScopedTenantId != businessUnitId)
            throw new UnauthorizedAccessException("The authenticated tenant context is required.");
    }

    private static string Outcome(Quote quote) => CommercialLearningRules.ResolveQuoteOutcome(quote);

    private static IReadOnlyCollection<CurrencyValueSummary> Summaries(IEnumerable<ValuePoint> values,
        IReadOnlyDictionary<long, string> currencies) => values.GroupBy(x => x.CurrencyId).Select(group =>
    {
        var ordered = group.OrderBy(x => x.OccurredOn).ToArray();
        var amounts = ordered.Select(x => x.Value).OrderBy(x => x).ToArray();
        return new CurrencyValueSummary(group.Key, currencies.GetValueOrDefault(group.Key) ?? $"Currency {group.Key}",
            ordered.Last().Value, Median(amounts), amounts.First(), amounts.Last(), amounts.Length);
    }).OrderBy(x => x.CurrencyCode).ToArray();

    private static decimal Median(IReadOnlyList<decimal> values) => values.Count % 2 == 1
        ? values[values.Count / 2] : (values[values.Count / 2 - 1] + values[values.Count / 2]) / 2m;
    private sealed record ValuePoint(long CurrencyId, decimal Value, DateTime OccurredOn, long RecordId);
    private sealed record PricingHistoryRow(long QuoteId, long ProductId, long? CustomerId, long CurrencyId,
        decimal Quantity, decimal UnitPrice, int? DeliveryLeadTimeDays, DateTime OccurredOn, string QuoteNumber,
        DateTime? OutcomeOn);
    private sealed record PricingOutcomeRow(long QuoteId, long? CustomerId, long CurrencyId,
        decimal Quantity, decimal UnitPrice, DateTime OccurredOn, string QuoteNumber,
        bool IsCustomerOrderWin, bool IsDecided);
}

public static class CommercialLearningRules
{
    public static decimal CalculateWeightedCoverage(IEnumerable<long> ownedQuoteIds,
        IEnumerable<(long QuoteId, decimal ContributionPercent)> contributions)
    {
        var owned = ownedQuoteIds.Distinct().ToHashSet();
        var contributed = contributions.Where(x => !owned.Contains(x.QuoteId))
            .GroupBy(x => x.QuoteId)
            .Sum(group => Math.Min(100m, group.Sum(x => Math.Max(0m, x.ContributionPercent)))) / 100m;
        return decimal.Round(owned.Count + contributed, 2);
    }

    private static readonly HashSet<string> CommercialConstraints =
        new(["PRICE", "LEAD_TIME", "NO_STOCK", "COMPLIANCE", "SUPPLIER_COST"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CustomerDecisions =
        new(["CUSTOMER_CANCELLED", "NO_RESPONSE", "LOST_COMPETITOR"], StringComparer.OrdinalIgnoreCase);

    public static bool CanRecommendStocking(int decidedCount, int wonCount) => decidedCount >= 5 && wonCount >= 2;
    public static bool CanRecommendStocking(int decidedCount, int wonCount, bool demandConsistent,
        bool leadTimeEvidence) => CanRecommendStocking(decidedCount, wonCount) && demandConsistent && leadTimeEvidence;
    public static string ResolveQuoteOutcome(Quote quote)
    {
        var code = quote.Status?.SetupCode?.ToUpperInvariant();
        return code is "ACCEPTED" or "ORDERED" || quote.StatusId == 44 ? "WON"
            : code == "EXPIRED" ? "EXPIRED" : "LOST";
    }

    public static string ClassifyLoss(string? reasonCode) => CommercialConstraints.Contains(reasonCode ?? "")
        ? "COMMERCIAL_CONSTRAINT" : CustomerDecisions.Contains(reasonCode ?? "")
            ? "CUSTOMER_DECISION" : "EXECUTION_REVIEW";
}
