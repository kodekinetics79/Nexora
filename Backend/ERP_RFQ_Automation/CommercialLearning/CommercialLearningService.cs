using ERP_RFQ_Automation.Models;
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
        var decided = quoteLines.Where(x => x.Quote.OutcomeOn.HasValue).ToArray();
        var won = decided.Where(x => Outcome(x.Quote) == "WON").ToArray();
        var lost = decided.Where(x => Outcome(x.Quote) is "LOST" or "EXPIRED").ToArray();
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
            .Select(x => new ValuePoint(x.Quote.CurrencyId!.Value, x.UnitPrice, x.Quote.OutcomeOn!.Value, x.QuoteId)).ToArray();
        var stockouts = await context.SourcingCases.AsNoTracking().CountAsync(x => x.BusinessUnitId == businessUnitId &&
            x.ProductId == productId && x.StockQuantity < x.RequestedQuantity, cancellationToken);
        var periodValues = requestLines.Select(x => x.Rfq.RecDate).Where(x => x.Year >= 2000)
            .Concat(quoteLines.Select(x => x.Quote.CreatedDate ?? x.Quote.QuoteDate ?? DateTime.UtcNow)).ToArray();
        var evidence = won.Select(x => new CommercialEvidenceLink("CustomerQuote", x.QuoteId, x.Quote.QuoteNo,
                x.Quote.OutcomeOn, "WON_OUTCOME"))
            .Concat(landed.Select(x => new CommercialEvidenceLink("PricingDecision", x.RecordId,
                $"Product {productId} landed cost", x.OccurredOn, "SUPPLIER_COST"))).Take(50).ToArray();
        var winningLeadTimes = won.Where(x => x.DeliveryLeadTime.HasValue).Select(x => (decimal)x.DeliveryLeadTime!.Value).ToArray();
        var lastWon = won.OrderByDescending(x => x.Quote.OutcomeOn).ThenByDescending(x => x.QuoteId).FirstOrDefault();
        var lastWonContext = lastWon is null || !lastWon.Quote.CurrencyId.HasValue ? null : new ProductWonContext(
            lastWon.QuoteId, lastWon.Quote.QuoteNo, lastWon.Quantity, lastWon.UnitPrice,
            lastWon.Quote.CurrencyId.Value, currencies.GetValueOrDefault(lastWon.Quote.CurrencyId.Value) ??
                $"Currency {lastWon.Quote.CurrencyId.Value}", lastWon.DeliveryLeadTime, lastWon.Quote.OutcomeOn!.Value);
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
        var wonSupport = await context.CustomerQuoteSourcingDecisions.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && awardIds.Contains(x.SourcingAwardId) &&
                context.Quotes.Any(q => q.BusinessUnitId == businessUnitId && q.Id == x.QuoteId && q.OutcomeOn != null &&
                    (q.Status!.SetupCode == "ACCEPTED" || q.StatusId == 44))).CountAsync(cancellationToken);
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
        return new SupplierCommercialEvaluation(supplierId, supplier.Name, revisions.Count, awards.Count, wonSupport,
            projected.Count(x => x.IsActive && x.ValidUntil > DateTime.UtcNow),
            responseDays.Length == 0 ? null : decimal.Round(responseDays.Average(), 2),
            reliability.Length == 0 ? null : decimal.Round(reliability.Average(), 2),
            Summaries(projected.Where(x => x.CurrencyId.HasValue && x.LandedUnitCost.HasValue)
                .Select(x => new ValuePoint(x.CurrencyId!.Value, x.LandedUnitCost!.Value,
                    x.QuoteDate ?? x.CreatedDate, x.Id)), currencies),
            awards.Select(x => new CommercialEvidenceLink("SourcingAward", x.Id, $"Award {x.Id}", x.CreatedOn,
                wonSupport > 0 ? "SUPPORTED_WIN" : "SELECTED_OFFER"))
                .Concat(handoffs.Select(x => new CommercialEvidenceLink("ProcurementHandoff", x.Id,
                    x.ExternalSupplierPoNumber ?? $"Handoff {x.Id}", x.LastSynchronizedOn ?? x.CreatedOn,
                    x.Status))).Take(50).ToArray());
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
        var requestLines = await context.Rfqitems.AsNoTracking().Where(x => x.ProductId == productId &&
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
        var quoted = quoteLines.Sum(x => x.Quantity);
        var committed = orderLines.Sum(x => x.Quantity);
        var conversion = product.DecidedCount == 0 ? (decimal?)null : product.LineWinRatePercent;
        var weighted = decimal.Round(quoted * (conversion ?? 0m) / 100m, 4);
        var eligible = CommercialLearningRules.CanRecommendStocking(product.DecidedCount, product.WonCount);
        var recommendation = eligible
            ? "Review stocking economics with margin, supplier lead time, MOQ, carrying cost, shelf life, and demand consistency."
            : $"Insufficient verified conversion evidence for a stocking recommendation ({product.DecidedCount} decided, {product.WonCount} won).";
        return new InventoryDemandMemory(productId, product.PartNumber, product.ProductName, observed, observed,
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
        var rfqIds = await context.Rfqs.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.LeadId.HasValue && leadIds.Contains(x.LeadId.Value)).Select(x => x.Id).ToArrayAsync(cancellationToken);
        var quotes = await context.Quotes.AsNoTracking().Include(x => x.Status).Where(x => x.BusinessUnitId == businessUnitId &&
            x.Rfqid.HasValue && rfqIds.Contains(x.Rfqid.Value)).ToListAsync(cancellationToken);
        var quoteIds = quotes.Select(x => x.Id).ToArray();
        var orderWins = await context.Orders.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId
                && x.SourceType == OrderSourceTypes.CustomerAward && x.QuoteId.HasValue
                && quoteIds.Contains(x.QuoteId.Value))
            .GroupBy(x => x.QuoteId!.Value).ToDictionaryAsync(x => x.Key,
                x => x.Max(order => order.OrderDate), cancellationToken);
        var decided = quotes.Where(x => x.OutcomeOn.HasValue || orderWins.ContainsKey(x.Id)).ToArray();
        var won = decided.Where(x => orderWins.ContainsKey(x.Id) || Outcome(x) == "WON").ToArray();
        var lost = decided.Where(x => !orderWins.ContainsKey(x.Id) && Outcome(x) is "LOST" or "EXPIRED").ToArray();
        var reasonIds = lost.Where(x => x.OutcomeReasonId.HasValue).Select(x => x.OutcomeReasonId!.Value).Distinct().ToArray();
        var reasonCodes = await context.SetupMasters.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            reasonIds.Contains(x.SetupId)).ToDictionaryAsync(x => x.SetupId, x => x.SetupCode ?? "UNSPECIFIED", cancellationToken);
        string Reason(Quote quote) => quote.OutcomeReasonId.HasValue
            ? reasonCodes.GetValueOrDefault(quote.OutcomeReasonId.Value) ?? "UNSPECIFIED" : "UNSPECIFIED";
        var followUps = await context.FollowUpTasks.AsNoTracking().Where(x => x.BusinessUnitId == businessUnitId &&
            x.AssignedToUserId == userId).ToListAsync(cancellationToken);
        return new SalesRepCommercialMemory(userId, $"{user.FirstName} {user.LastName}".Trim(), leadIds.Length,
            decided.Length, won.Length, lost.Length, lost.Count(x => CommercialLearningRules.ClassifyLoss(Reason(x)) == "COMMERCIAL_CONSTRAINT"),
            lost.Count(x => CommercialLearningRules.ClassifyLoss(Reason(x)) == "CUSTOMER_DECISION"),
            lost.Count(x => CommercialLearningRules.ClassifyLoss(Reason(x)) == "EXECUTION_REVIEW"),
            followUps.Count(x => x.Status is ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpStatus.Open or ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpStatus.InProgress),
            followUps.Count(x => x.Status == ERP_RFQ_Automation.CommercialIntelligence.Sales.FollowUpStatus.Completed),
            decided.Length == 0 ? null : decimal.Round(100m * won.Length / decided.Length, 2),
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
        var signals = grouped.Take(50).Select(group =>
        {
            var latest = group.OrderByDescending(x => x.d.ReviewedOn).First();
            var conflict = group.Select(x => x.d.CorrectedValue).Distinct().Count() > 1;
            return new LearningSignal("SUPPLIER_QUOTE_CORRECTION", group.Key.FieldName,
                latest.d.CorrectedValue ?? "", group.Count(), latest.d.ReviewedOn,
                conflict ? "CONFLICT_REVIEW" : group.Count() >= 3 ? "REUSABLE" : "OBSERVING",
                $"SupplierQuoteEvidence:{latest.e.Id}");
        }).ToArray();
        return new LearningStudioSummary(DateTime.UtcNow, decisions.Count, conflicting, templateCount,
            productStats.Count(x => x.Decided >= 3), productStats.Count(x => x.Decided < 3), signals);
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
}

public static class CommercialLearningRules
{
    private static readonly HashSet<string> CommercialConstraints =
        new(["PRICE", "LEAD_TIME", "NO_STOCK", "COMPLIANCE", "SUPPLIER_COST"], StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CustomerDecisions =
        new(["CUSTOMER_CANCELLED", "NO_RESPONSE", "LOST_COMPETITOR"], StringComparer.OrdinalIgnoreCase);

    public static bool CanRecommendStocking(int decidedCount, int wonCount) => decidedCount >= 5 && wonCount >= 2;
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
