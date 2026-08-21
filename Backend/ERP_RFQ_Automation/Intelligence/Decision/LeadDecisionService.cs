using System.Globalization;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Reporting;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Intelligence.Decision;

/// <summary>
/// Deterministic Lead Decision Brief (see DECISION-WIRING.md).
///
/// SIGNALS
///   coverage — lead items matched against Products with cheap batched queries:
///              exact ItemMaterialCode == PartNo or ManufacturerPartNumber ==
///              ModelNo/PartNo (single IN query, case-insensitive). Names are not
///              decision-grade product identities.
///   value    — per line: the lead's own UnitPrice, else the matched product's
///              FinalSalesPrice/SellingPrice, × quantity. Confidence is "high"
///              only when most lines were priced from real numbers.
///   margin   — value-weighted (price - cost)/price over the case's priced lines,
///              read from the SAME service and the SAME records as the board's
///              gross margin (IGrossMarginService.GetForCommercialCaseAsync over
///              CustomerQuoteSourcingDecision). Null, with a stated reason, until a
///              line has been priced from an approved supplier award — the product
///              master's cost columns carry no currency and are never a landed cost,
///              so they are not substituted for one.
///   customer — canonical tenant-qualified Lead.CustomerId first. Name/email
///              fallback is explicitly weaker and never decision-grade.
///   deadline — BidClosingDate vs now, sentinel dates (&lt; year 2000) ignored.
///
/// RECOMMENDATION (transparent rules, always explained in plain language):
///   skip   = coverage &lt; 20% OR overdue
///   bid    = coverage ≥ 60% AND not overdue AND (existing customer OR margin ≥ 15%)
///   review = everything else
///
/// Tenant safety: the global query filters already scope Leads/Quotes/Orders
/// (BusinessUnitId) and Customers/Products (Buid == null shared OR tenant), and
/// every query here ALSO carries an explicit BU predicate — same convention as
/// PricingEngine — so the service stays correct on tenant-less context paths
/// (background/agent execution). Missing data never throws: empty history, no
/// matches, no prices and no deadline all degrade gracefully.
/// </summary>
public sealed class LeadDecisionService : ILeadDecisionService
{
    // Recommendation rule thresholds (percent).
    private const decimal SkipCoveragePct = 20m;
    private const decimal BidCoveragePct = 60m;
    private const decimal BidMarginPct = 15m;

    // Lead.Aiconfidence below this adds a "verify first" reason.
    private const decimal LowExtractionConfidence = 0.70m;

    private const int MinNameTokenLength = 4;

    private const int OrderLookbackMonths = 24;
    private const int SentinelYearFloor = 2000;
    private const int MaxSummaryLeads = 100;

    private readonly ErpRfqAutomationContext _db;
    private readonly IGrossMarginService _margin;

    public LeadDecisionService(ErpRfqAutomationContext db, IGrossMarginService margin)
    {
        _db = db;
        _margin = margin ?? throw new ArgumentNullException(nameof(margin));
    }

    // ================================================================ full brief

    public async Task<LeadDecisionBrief> GetBriefAsync(long leadId, long businessUnitId, CancellationToken ct)
    {
        // One round trip for the header + lines. Explicit BU predicate on top of
        // the global filter (SEC-07 convention). EmailIngest carries the sender
        // address (FromEmail) — the only email linkage besides Lead.Clientemail.
        var lead = await _db.Leads.AsNoTracking()
            .Where(l => l.Id == leadId && l.BusinessUnitId == businessUnitId)
            .Select(l => new
            {
                l.Id,
                l.Rfqno,
                l.BuyersName,
                l.Clientemail,
                l.BidClosingDate,
                l.Aiconfidence,
                l.CustomerId,
                l.CommercialCaseId,
                SenderEmail = l.EmailIngests != null ? l.EmailIngests.FromEmail : l.Clientemail,
                Items = l.LeadItems
                    .OrderBy(li => li.Id)
                    .Select(li => new ItemRow(
                        li.Id,
                        li.ItemMaterialCode,
                        li.ManufacturerPartNumber,
                        li.ProductShortName,
                        li.ProductShortDescription,
                        li.Quantity,
                        li.UnitPrice,
                        li.Currency))
                    .ToList()
            })
            .FirstOrDefaultAsync(ct);

        if (lead is null)
            throw new KeyNotFoundException($"Lead {leadId} was not found in this business unit.");

        var now = DateTime.UtcNow;
        var items = lead.Items;

        // ---- 1. catalog coverage -------------------------------------------
        var matches = await MatchCatalogAsync(items, businessUnitId, ct);
        var availableByProduct = await AvailableToPromiseByProductAsync(
            matches.Values.Select(m => m.Product.Id).Distinct().ToArray(), businessUnitId, ct);

        // ---- 2 + 3. estimated value & margin potential ---------------------
        var coverageItems = new List<CoverageItem>(items.Count);
        var estimatedValue = 0m;
        var pricedLines = 0;
        var pricedLinesWithKnownCurrency = 0;

        foreach (var li in items)
        {
            matches.TryGetValue(li.Id, out var match);

            decimal? price = null;
            string? priceSource = null;
            if (li.UnitPrice is > 0m)
            {
                price = li.UnitPrice;
                priceSource = "lead";
            }
            else if (match is not null)
            {
                var catalogPrice = FirstPositive(match.Product.FinalSalesPrice, match.Product.SellingPrice);
                if (catalogPrice.HasValue) { price = catalogPrice; priceSource = "catalog"; }
            }

            // Product master prices have no currency field. They remain visible as
            // catalog evidence but cannot contribute to an authoritative total.
            if (price.HasValue && priceSource == "lead")
            {
                pricedLines++;
                if (!string.IsNullOrWhiteSpace(li.Currency)) pricedLinesWithKnownCurrency++;
                // A line with no stated quantity contributes nothing to the estimate. It is not
                // worth zero — it is unknown, and the brief says so through pricedLines rather
                // than by pretending the line has no value.
                if (li.Quantity is > 0)
                    estimatedValue += price.Value * li.Quantity.Value;
            }

            // Product master costs are not currency-qualified and are not landed costs, so no cost
            // is taken from this loop. Margin comes from the priced quote lines of the commercial
            // case instead — see the margin block below GetBriefAsync's coverage section.

            // LEDGER AUTHORITY: this used to render "in stock" from Product.QtyOnHand — a legacy
            // denormalised column that is not per-warehouse, nets off nothing that is already
            // reserved, allocated, quarantined, damaged, expired or protected as safety stock, and
            // that the availability engine cannot see at all. A rep reading the brief was shown a
            // number no other screen agreed with. The figure is now available-to-promise summed
            // over the tenant's real inventory rows, via InventoryQuantityMath.
            decimal? catalogQtyOnHand = match is null
                ? null
                : availableByProduct.GetValueOrDefault(match.Product.Id);
            var hasCatalogOnHand = catalogQtyOnHand is > 0m;

            coverageItems.Add(new CoverageItem
            {
                LeadItemId = li.Id,
                Description = li.ProductShortName ?? li.ProductShortDescription ?? li.ItemMaterialCode,
                Quantity = li.Quantity,
                Matched = match is not null,
                MatchType = match?.MatchType,
                ProductId = match?.Product.Id,
                InStock = hasCatalogOnHand,
                HasCatalogOnHand = hasCatalogOnHand,
                CatalogQtyOnHand = catalogQtyOnHand,
                UnitPrice = price,
                PriceSource = priceSource
            });
        }

        var totalItems = items.Count;
        var coveredItems = coverageItems.Count(i => i.Matched);
        var coverage = new CatalogCoverage
        {
            TotalItems = totalItems,
            CoveredItems = coveredItems,
            CoveragePct = Pct(coveredItems, totalItems),
            InStockItems = coverageItems.Count(i => i.InStock),
            CatalogOnHandItems = coverageItems.Count(i => i.HasCatalogOnHand),
            CatalogOnHandQuantity = coverageItems.Sum(i => i.CatalogQtyOnHand ?? 0m),
            Items = coverageItems
        };

        // "high" only when most lines were priced from real numbers (the lead's
        // own price or a matched product's price) — honest about sparse data.
        var valueConfidence = totalItems > 0 && pricedLines * 2 > totalItems ? "high" : "low";

        var currencies = items
            .Where(i => i.UnitPrice is > 0m)
            .Select(i => i.Currency?.Trim().ToUpperInvariant())
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .ToList();
        var hasOneKnownCurrency = pricedLines > 0
                                  && pricedLinesWithKnownCurrency == pricedLines
                                  && currencies.Count == 1;
        var currency = hasOneKnownCurrency ? currencies[0] : null;
        decimal? aggregateEstimatedValue = hasOneKnownCurrency ? Round2(estimatedValue) : null;

        // ---- 3b. margin, from the one place a landed cost is decision-grade ----
        // Not re-derived here: the same service, the same records and the same value-weighted
        // formula the board's gross margin uses, narrowed to this lead's commercial case. A second
        // calculation would be a second answer, and the two would drift the first time either moved.
        var marginEvidence = await _margin.GetForCommercialCaseAsync(
            businessUnitId, lead.CommercialCaseId, now, ct);
        var marginPotentialPct = marginEvidence.MarginPercent;
        var marginCostedItems = marginEvidence.CostedLines;

        // ---- 4. customer history -------------------------------------------
        var customer = await ResolveCustomerHistoryAsync(
            lead.Id, lead.CustomerId, lead.BuyersName, lead.Clientemail ?? lead.SenderEmail,
            businessUnitId, now, ct);

        // ---- 5. deadline feasibility ---------------------------------------
        var deadline = BuildDeadline(lead.BidClosingDate, totalItems, now);

        // ---- 6. recommendation ---------------------------------------------
        var brief = new LeadDecisionBrief
        {
            LeadId = lead.Id,
            Rfqno = lead.Rfqno,
            BuyersName = lead.BuyersName,
            ExtractionConfidence = lead.Aiconfidence,
            Coverage = coverage,
            EstimatedValue = aggregateEstimatedValue,
            ValueConfidence = aggregateEstimatedValue.HasValue ? valueConfidence : "unknown",
            Currency = currency,
            MarginPotentialPct = marginPotentialPct,
            MarginCostedItems = marginCostedItems,
            IsMarginComplete = totalItems > 0 && marginCostedItems == totalItems,
            Customer = customer,
            Deadline = deadline
        };
        // Coverage is only evidence when there is a catalog to be covered BY. A tenant
        // with no product identities scores 0% on every lead, which the rules below used
        // to read as "skip" — on 100% of inbound enquiries.
        var catalogAssessable = await CatalogHasIdentitiesAsync(businessUnitId, ct);
        (brief.Recommendation, brief.Reasons) = Recommend(
            brief, lead.Aiconfidence, catalogAssessable, marginEvidence.UnavailableReason);
        return brief;
    }

    /// <summary>
    /// Does this tenant hold any product the coverage rule could match against? One cheap
    /// existence check. False means coverage is unmeasurable, not zero — the distinction
    /// between "we stock none of this" and "we have not loaded a catalog" is the whole
    /// difference between advice and noise.
    /// </summary>
    private async Task<bool> CatalogHasIdentitiesAsync(long businessUnitId, CancellationToken ct) =>
        await ActiveProducts(businessUnitId).AnyAsync(p => p.PartNo != "", ct);

    // ================================================================ summaries

    public async Task<Dictionary<long, LeadDecisionSummary>> GetSummariesAsync(
        IEnumerable<long> leadIds, long businessUnitId, CancellationToken ct)
    {
        var result = new Dictionary<long, LeadDecisionSummary>();
        var ids = (leadIds ?? Enumerable.Empty<long>()).Distinct().Take(MaxSummaryLeads).ToArray();
        if (ids.Length == 0) return result;

        // Batched query 1 — all requested leads + minimal line fields at once.
        var leads = await _db.Leads.AsNoTracking()
            .Where(l => ids.Contains(l.Id) && l.BusinessUnitId == businessUnitId)
            .Select(l => new
            {
                l.Id,
                l.BidClosingDate,
                Items = l.LeadItems.Select(li => new
                {
                    li.ItemMaterialCode,
                    li.ManufacturerPartNumber,
                    li.UnitPrice,
                    li.Quantity,
                    li.Currency
                }).ToList()
            })
            .ToListAsync(ct);
        if (leads.Count == 0) return result;

        // Batched query 2 — every distinct catalogue number across every lead in one IN query.
        // The grid MUST read the same fields as the brief (MatchCatalogAsync): a buyer's material
        // number arrives in whichever field the door that read the document happens to have, and
        // the spreadsheet door routes every material-code heading into ManufacturerPartNumber by
        // design (NativeSpreadsheetParser.FieldAliases). Reading ItemMaterialCode alone scored
        // every spreadsheet-ingested lead 0% here — "We stock ~0%" and a Skip chip — while that
        // lead's own brief reported full coverage.
        var lines = leads.SelectMany(l => l.Items).ToList();
        var codes = lines
            .Select(i => i.ItemMaterialCode?.Trim().ToLowerInvariant())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .Distinct()
            .ToList();
        var mpns = lines
            .Select(i => i.ManufacturerPartNumber?.Trim().ToLowerInvariant())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .Distinct()
            .ToList();

        // Identifier -> product id, populated ONLY where exactly one product owns that
        // identifier. Same three fail-closed guards as MatchCatalogAsync (duplicate PartNo,
        // duplicate ModelNo, and a line whose two identifiers point at two different products):
        // without them the grid would count as covered what the brief refuses to match, which is
        // the identical contradiction mirrored.
        var byPartNo = new Dictionary<string, long>(StringComparer.Ordinal);
        var byModelNo = new Dictionary<string, long>(StringComparer.Ordinal);

        if (codes.Count > 0 || mpns.Count > 0)
        {
            var exactRows = await ActiveProducts(businessUnitId)
                .Where(p => codes.Contains(p.PartNo.ToLower())
                            || mpns.Contains(p.PartNo.ToLower())
                            || (p.ModelNo != null && mpns.Contains(p.ModelNo.ToLower())))
                .Select(p => new { p.Id, p.PartNo, p.ModelNo })
                .ToListAsync(ct);

            foreach (var group in exactRows.GroupBy(
                         p => p.PartNo.Trim().ToLowerInvariant(), StringComparer.Ordinal))
                if (group.Count() == 1) byPartNo[group.Key] = group.Single().Id;

            foreach (var group in exactRows
                         .Where(p => !string.IsNullOrWhiteSpace(p.ModelNo))
                         .GroupBy(p => p.ModelNo!.Trim().ToLowerInvariant(), StringComparer.Ordinal))
                if (group.Count() == 1) byModelNo[group.Key] = group.Single().Id;
        }

        // Same guard as the full brief: with no catalog loaded, coverage is 0% on every
        // lead and the skip rule fires on 100% of inbound. One existence check, hoisted
        // out of the per-lead loop.
        var catalogAssessable = await CatalogHasIdentitiesAsync(businessUnitId, ct);

        var now = DateTime.UtcNow;
        foreach (var lead in leads)
        {
            var total = lead.Items.Count;
            var covered = lead.Items.Count(i =>
            {
                var code = i.ItemMaterialCode?.Trim().ToLowerInvariant();
                var mpn = i.ManufacturerPartNumber?.Trim().ToLowerInvariant();
                var matched = new List<long>(3);
                if (!string.IsNullOrEmpty(code) && byPartNo.TryGetValue(code!, out var byCode))
                    matched.Add(byCode);
                if (!string.IsNullOrEmpty(mpn) && byModelNo.TryGetValue(mpn!, out var byModel))
                    matched.Add(byModel);
                if (!string.IsNullOrEmpty(mpn) && byPartNo.TryGetValue(mpn!, out var byPart))
                    matched.Add(byPart);
                return matched.Distinct().Count() == 1;
            });
            var coveragePct = Pct(covered, total);

            var pricedItems = lead.Items.Where(i => i.UnitPrice is > 0m && i.Quantity is > 0).ToList();
            var pricedCurrencies = pricedItems
                .Select(i => i.Currency?.Trim().ToUpperInvariant())
                .Where(c => !string.IsNullOrEmpty(c))
                .Distinct()
                .ToList();
            var canAggregateValue = pricedItems.Count > 0
                                    && pricedItems.All(i => !string.IsNullOrWhiteSpace(i.Currency))
                                    && pricedCurrencies.Count == 1;
            decimal? estimatedValue = canAggregateValue
                ? Round2(pricedItems.Sum(i => i.UnitPrice!.Value * i.Quantity!.Value))
                : null;

            var (daysLeft, urgency) = DeadlineBand(lead.BidClosingDate, now);

            // Summaries intentionally cannot emit actionable Bid because customer
            // identity and weighted margin evidence are not evaluated in this path.
            // Mirrors Recommend(): overdue is document evidence and still yields skip,
            // but a coverage-driven skip requires a catalog to have measured coverage
            // against, and a lead with no extracted lines has nothing to assess at all.
            var overdue = urgency == LeadDecisionUrgency.Overdue;
            var recommendation =
                overdue ? LeadDecisionRecommendations.Skip
                : !catalogAssessable || total == 0 ? LeadDecisionRecommendations.CannotAssess
                : coveragePct < SkipCoveragePct ? LeadDecisionRecommendations.Skip
                : LeadDecisionRecommendations.Review;

            result[lead.Id] = new LeadDecisionSummary
            {
                LeadId = lead.Id,
                CoveragePct = coveragePct,
                EstimatedValue = estimatedValue,
                DaysLeft = daysLeft,
                Urgency = urgency,
                Recommendation = recommendation,
                PolicyVersion = LeadDecisionPolicy.Version,
                Completeness = LeadDecisionCompleteness.Partial,
                IsActionable = false
            };
        }

        return result;
    }

    // ================================================================ catalog matching

    // Quantity is nullable because LeadItem.Quantity is: null means the document stated no
    // readable quantity, and the value guard (`Quantity > 0`) already excludes such a line from
    // every value estimate — a null is false there exactly as a 0 was, so no total changes.
    private sealed record ItemRow(
        long Id, string? ItemMaterialCode, string? ManufacturerPartNumber,
        string? ProductShortName, string? ProductShortDescription,
        int? Quantity, decimal? UnitPrice, string? Currency);

    // Product.QtyOnHand is deliberately NOT projected here: stock for the brief comes from the
    // Inventory rows via AvailableToPromiseByProductAsync, never from the product master.
    private sealed record ProductLite(
        long Id, string PartNo, string? ModelNo, string? ProductName,
        decimal? UnitCost, decimal? SellingPrice,
        decimal? FinalLandedCost, decimal? FinalSalesPrice);

    private sealed record CatalogMatch(ProductLite Product, string MatchType);

    /// <summary>
    /// Authoritative available-to-promise per product, summed across every warehouse row the
    /// tenant holds for it. Two batched queries (inventory rows, then their active holds) and the
    /// shared <see cref="ERP_RFQ_Automation.Inventory.InventoryQuantityMath.AvailableToPromise"/>
    /// formula, so this brief cannot drift from the availability screens or from the order
    /// allocator. Products with no inventory row are simply absent, which reads as zero.
    /// </summary>
    private async Task<Dictionary<long, decimal>> AvailableToPromiseByProductAsync(
        IReadOnlyCollection<long> productIds, long businessUnitId, CancellationToken ct)
    {
        if (productIds.Count == 0) return new Dictionary<long, decimal>();

        var rows = await _db.Set<Models.Inventory>().AsNoTracking()
            .Where(x => x.Buid == businessUnitId && x.ProductId != null && productIds.Contains(x.ProductId!.Value))
            .Select(x => new
            {
                x.Id,
                ProductId = x.ProductId!.Value,
                x.QtyOnHand,
                x.AllocatedQuantity,
                x.QuarantineQuantity,
                x.DamagedQuantity,
                x.ExpiredQuantity,
                x.SafetyStockQuantity
            })
            .ToListAsync(ct);
        if (rows.Count == 0) return new Dictionary<long, decimal>();

        var inventoryIds = rows.Select(x => x.Id).ToArray();
        var reserved = await _db.Set<ERP_RFQ_Automation.Inventory.StockReservation>().AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && inventoryIds.Contains(x.InventoryId)
                        && x.Status == ERP_RFQ_Automation.Inventory.StockReservationStatus.Active)
            .GroupBy(x => x.InventoryId)
            .Select(x => new { InventoryId = x.Key, Quantity = x.Sum(v => v.Quantity) })
            .ToDictionaryAsync(x => x.InventoryId, x => x.Quantity, ct);

        return rows
            .GroupBy(x => x.ProductId)
            .ToDictionary(
                group => group.Key,
                group => group.Sum(x => ERP_RFQ_Automation.Inventory.InventoryQuantityMath.AvailableToPromise(
                    x.QtyOnHand, reserved.GetValueOrDefault(x.Id), x.AllocatedQuantity,
                    x.QuarantineQuantity, x.DamagedQuantity, x.ExpiredQuantity, x.SafetyStockQuantity)));
    }

    /// <summary>
    /// Cheap batched matching using exact product identifiers only. Product names
    /// may be useful review evidence elsewhere but never contribute commercial
    /// coverage, price, cost, stock, margin, or Bid signals here.
    /// </summary>
    private async Task<Dictionary<long, CatalogMatch>> MatchCatalogAsync(
        IReadOnlyList<ItemRow> items, long businessUnitId, CancellationToken ct)
    {
        var result = new Dictionary<long, CatalogMatch>();
        if (items.Count == 0) return result;

        var codes = items.Select(i => i.ItemMaterialCode?.Trim().ToLowerInvariant())
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();
        var mpns = items.Select(i => i.ManufacturerPartNumber?.Trim().ToLowerInvariant())
            .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).Distinct().ToList();

        var byPartNo = new Dictionary<string, ProductLite>(StringComparer.Ordinal);
        var byModelNo = new Dictionary<string, ProductLite>(StringComparer.Ordinal);

        if (codes.Count > 0 || mpns.Count > 0)
        {
            var exactRows = await ActiveProducts(businessUnitId)
                .Where(p => codes.Contains(p.PartNo.ToLower())
                            || mpns.Contains(p.PartNo.ToLower())
                            || (p.ModelNo != null && mpns.Contains(p.ModelNo.ToLower())))
                .Select(p => new ProductLite(p.Id, p.PartNo, p.ModelNo, p.ProductName,
                    p.UnitCost, p.SellingPrice, p.FinalLandedCost, p.FinalSalesPrice))
                .ToListAsync(ct);

            foreach (var group in exactRows.GroupBy(
                         p => p.PartNo.Trim().ToLowerInvariant(), StringComparer.Ordinal))
                if (group.Count() == 1) byPartNo[group.Key] = group.Single();

            foreach (var group in exactRows
                         .Where(p => !string.IsNullOrWhiteSpace(p.ModelNo))
                         .GroupBy(p => p.ModelNo!.Trim().ToLowerInvariant(), StringComparer.Ordinal))
                if (group.Count() == 1) byModelNo[group.Key] = group.Single();
        }

        foreach (var li in items)
        {
            var code = li.ItemMaterialCode?.Trim().ToLowerInvariant();
            var mpn = li.ManufacturerPartNumber?.Trim().ToLowerInvariant();
            var candidates = new List<CatalogMatch>(3);
            if (!string.IsNullOrEmpty(code) && byPartNo.TryGetValue(code!, out var byCode))
                candidates.Add(new CatalogMatch(byCode, "code"));
            if (!string.IsNullOrEmpty(mpn) && byModelNo.TryGetValue(mpn!, out var byModel))
                candidates.Add(new CatalogMatch(byModel, "mpn"));
            if (!string.IsNullOrEmpty(mpn) && byPartNo.TryGetValue(mpn!, out var byPart))
                candidates.Add(new CatalogMatch(byPart, "mpn"));

            var exactProducts = candidates.GroupBy(candidate => candidate.Product.Id).ToArray();
            if (exactProducts.Length == 1)
                result[li.Id] = exactProducts[0].OrderBy(candidate => candidate.MatchType == "code" ? 0 : 1).First();
        }

        return result;
    }

    /// <summary>
    /// Tenant-visible active catalog. Products are master data (Buid == null =
    /// shared) — explicit predicate mirrors the global query filter so the
    /// service is safe on tenant-less context paths too.
    /// </summary>
    private IQueryable<Product> ActiveProducts(long businessUnitId) =>
        _db.Products.AsNoTracking()
            .Where(p => (p.Buid == null || p.Buid == businessUnitId)
                        && (p.IsActive == null || p.IsActive == true));

    // ================================================================ customer history

    private async Task<CustomerHistory> ResolveCustomerHistoryAsync(
        long leadId, long? canonicalCustomerId, string? buyersName, string? rawEmail,
        long businessUnitId, DateTime now, CancellationToken ct)
    {
        var history = new CustomerHistory();

        var name = string.IsNullOrWhiteSpace(buyersName) ? null : buyersName.Trim();
        var email = ExtractEmailAddress(rawEmail);

        if (canonicalCustomerId.HasValue)
        {
            var canonical = await _db.Customers.AsNoTracking()
                .Where(c => c.Id == canonicalCustomerId.Value
                            && c.Buid == businessUnitId
                            && (c.IsActive == null || c.IsActive == true))
                .Select(c => new { c.Id, c.Name })
                .SingleOrDefaultAsync(ct);

            if (canonical is null)
            {
                history.IdentityEvidence = CustomerIdentityEvidence.CanonicalInvalid;
                return history;
            }

            history.CustomerId = canonical.Id;
            history.CustomerName = canonical.Name;
            history.IsExistingCustomer = true;
            history.IsDecisionGradeIdentity = true;
            history.IdentityEvidence = CustomerIdentityEvidence.Canonical;
        }
        else
        {
            if (name is null && email is null) return history;

            var nameLower = name?.ToLowerInvariant();
            var emailLower = email?.ToLowerInvariant();
            var candidates = await _db.Customers.AsNoTracking()
                .Where(c => c.Buid == businessUnitId && (c.IsActive == null || c.IsActive == true))
                .Where(c => (nameLower != null && c.Name.ToLower() == nameLower)
                            || (emailLower != null && c.ContactEmail != null && c.ContactEmail.ToLower() == emailLower))
                .Select(c => new { c.Id, c.Name, c.ContactEmail })
                .Take(6)
                .ToListAsync(ct);

            var emailMatches = candidates
                .Where(c => emailLower != null
                            && string.Equals(c.ContactEmail, email, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var strongest = emailMatches.Count > 0
                ? emailMatches
                : candidates.Where(c => nameLower != null
                                        && string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();

            if (strongest.Count != 1)
            {
                if (strongest.Count > 1) history.IdentityEvidence = CustomerIdentityEvidence.HeuristicAmbiguous;
                return history;
            }

            var resolvedFallback = strongest[0];
            history.CustomerId = resolvedFallback.Id;
            history.CustomerName = resolvedFallback.Name;
            history.IsExistingCustomer = true;
            history.IsDecisionGradeIdentity = false;
            history.IdentityEvidence = emailMatches.Count == 1
                ? CustomerIdentityEvidence.HeuristicEmail
                : CustomerIdentityEvidence.HeuristicName;
        }

        var resolvedCustomerId = history.CustomerId!.Value;
        history.PastLeads = await _db.Leads.AsNoTracking()
            .CountAsync(l => l.BusinessUnitId == businessUnitId
                             && l.Id != leadId
                             && l.CustomerId == resolvedCustomerId, ct);

        var since = now.AddMonths(-OrderLookbackMonths);
        var recentQuotes = await _db.Quotes.AsNoTracking()
            .Where(q => q.CustomerId == resolvedCustomerId
                        && q.BusinessUnitId == businessUnitId
                        && q.SentOn.HasValue
                        && q.SentOn >= since)
            .Select(q => new { q.Id, SentOn = q.SentOn!.Value })
            .ToListAsync(ct);
        history.Quotes = recentQuotes.Count;
        var recentQuoteIds = recentQuotes.Select(q => q.Id).ToArray();
        var recentOrders = await _db.Orders.AsNoTracking()
            .Where(o => o.CustomerId == resolvedCustomerId
                        && o.BusinessUnitId == businessUnitId
                        && o.QuoteId.HasValue
                        && recentQuoteIds.Contains(o.QuoteId.Value)
                        && o.OrderDate >= since)
            .Select(o => new { o.TotalAmount, Currency = o.Currency == null ? null : o.Currency.Code })
            .ToListAsync(ct);

        history.Orders = recentOrders.Count;
        var latestQuoteEvidence = recentQuotes.Select(q => (DateTime?)q.SentOn).Max();
        var latestOrderEvidence = await _db.Orders.AsNoTracking()
            .Where(o => o.CustomerId == resolvedCustomerId
                        && o.BusinessUnitId == businessUnitId
                        && o.QuoteId.HasValue
                        && recentQuoteIds.Contains(o.QuoteId.Value)
                        && o.OrderDate >= since)
            .Select(o => (DateTime?)o.OrderDate)
            .MaxAsync(ct);
        history.EvidenceAsOfUtc = new[] { latestQuoteEvidence, latestOrderEvidence }
            .Where(value => value.HasValue)
            .Max();
        var orderCurrencies = recentOrders.Select(x => x.Currency?.Trim().ToUpperInvariant())
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray();
        if (recentOrders.Count > 0 && recentOrders.All(x => !string.IsNullOrWhiteSpace(x.Currency))
                                   && orderCurrencies.Length == 1)
        {
            history.TotalOrderValue = Round2(recentOrders.Sum(x => x.TotalAmount));
            history.TotalOrderCurrency = orderCurrencies[0];
        }
        return history;
    }

    // ================================================================ deadline

    private static DeadlineFeasibility BuildDeadline(DateTime? bidClosingDate, int totalItems, DateTime now)
    {
        var (daysLeft, urgency) = DeadlineBand(bidClosingDate, now);
        var usableDate = daysLeft.HasValue ? bidClosingDate : null;

        string? hint;
        if (daysLeft is null)
            hint = totalItems > 0 ? $"{N0(totalItems)} item(s) — no bid closing date on record." : null;
        else if (daysLeft < 0)
            hint = $"Closing date passed {N0(-daysLeft.Value)} day(s) ago.";
        else if (daysLeft == 0)
            hint = $"Closes today — {N0(totalItems)} item(s) to price today.";
        else
        {
            var perDay = (int)Math.Ceiling(totalItems / (double)daysLeft.Value);
            hint = $"{N0(totalItems)} item(s) across {N0(daysLeft.Value)} day(s) (~{N0(perDay)} lines/day).";
        }

        return new DeadlineFeasibility
        {
            BidClosingDate = usableDate,
            DaysLeft = daysLeft,
            Urgency = urgency,
            WorkloadHint = hint
        };
    }

    /// <summary>Null-safe band; sentinel dates before year 2000 are ignored.</summary>
    private static (int? daysLeft, string urgency) DeadlineBand(DateTime? bidClosingDate, DateTime now)
    {
        if (!bidClosingDate.HasValue || bidClosingDate.Value.Year < SentinelYearFloor)
            return (null, LeadDecisionUrgency.Unknown);

        var daysLeft = (bidClosingDate.Value.Date - now.Date).Days;
        var urgency = daysLeft < 0 ? LeadDecisionUrgency.Overdue
            : daysLeft <= 3 ? LeadDecisionUrgency.Critical
            : daysLeft <= 7 ? LeadDecisionUrgency.Soon
            : LeadDecisionUrgency.Comfortable;
        return (daysLeft, urgency);
    }

    // ================================================================ recommendation

    /// <summary>
    /// Transparent rules + plain-language reasons:
    ///   cannot-assess = no catalog identities to measure coverage against, and not overdue
    ///   skip          = overdue OR (coverage assessable AND coverage &lt; 20%)
    ///   bid           = coverage ≥ 60% AND not overdue AND (existing customer OR margin ≥ 15%)
    ///   review        = everything else
    ///
    /// <paramref name="catalogAssessable"/> is the guard that keeps the skip rule honest.
    /// Coverage is computed as matched-lines / total-lines against the tenant's product
    /// identities; with no products loaded that ratio is 0 for every lead ever received,
    /// and the rule then advised skipping 100% of inbound enquiries. A 0% coverage figure
    /// means "we stock none of this" only when there is a catalog; otherwise it means the
    /// question was never asked, and the honest output is no recommendation at all.
    /// Overdue is independent evidence — a passed bid date is a fact about the document,
    /// not about the catalog — so it still yields skip.
    /// </summary>
    private static (string recommendation, List<string> reasons) Recommend(
        LeadDecisionBrief b, decimal? aiConfidence, bool catalogAssessable, string? marginUnavailableReason)
    {
        var reasons = new List<string>();
        var c = b.Coverage;
        var overdue = b.Deadline.Urgency == LeadDecisionUrgency.Overdue;

        // -- coverage / stock --
        if (c.TotalItems == 0)
            reasons.Add("No line items were extracted for this lead.");
        else if (!catalogAssessable)
            reasons.Add("Catalog coverage cannot be assessed — no product identities are loaded for this business unit.");
        else if (c.CoveredItems == 0)
            reasons.Add($"None of the {N0(c.TotalItems)} items match our catalog.");
        else
            reasons.Add($"Exact catalog identities found for {N0(c.CoveredItems)} of {N0(c.TotalItems)} items " +
                        $"({Fmt(c.CoveragePct)}% coverage; {N0(c.CatalogOnHandItems)} have stock available to promise).");

        // -- value --
        if (b.EstimatedValue is > 0m)
        {
            var cur = b.Currency is null ? "" : $" {b.Currency}";
            reasons.Add(b.ValueConfidence == "high"
                ? $"Estimated value {N0Dec(b.EstimatedValue.Value)}{cur}."
                : $"Rough estimated value {N0Dec(b.EstimatedValue.Value)}{cur} — most lines had no usable price.");
        }
        else
            reasons.Add(b.Currency is null
                ? "Aggregate value is unavailable without one known currency for all priced lines."
                : "No price information on any line — value unknown.");

        // -- margin --
        if (b.MarginPotentialPct is decimal margin)
        {
            reasons.Add(margin >= BidMarginPct
                ? $"Healthy margin potential (~{Fmt(margin)}%) on the items we can cost."
                : $"Thin margin potential (~{Fmt(margin)}%) on the items we can cost.");
        }
        else if (marginUnavailableReason is { Length: > 0 })
        {
            // The gap is stated, never left blank. A blank reads like a loading state, and the
            // consumers of this brief must be able to tell "not measured" from "measured as thin".
            reasons.Add($"Margin is not measured yet — {char.ToLowerInvariant(marginUnavailableReason[0])}"
                        + marginUnavailableReason[1..]);
        }

        // -- customer --
        if (b.Customer.IsExistingCustomer)
        {
            var spend = b.Customer.TotalOrderValue is > 0m
                ? $" — {N0Dec(b.Customer.TotalOrderValue.Value)} {b.Customer.TotalOrderCurrency} in orders over the last {OrderLookbackMonths} months"
                : "";
            reasons.Add($"Existing customer ({b.Customer.CustomerName}){spend}.");
            if (!b.Customer.IsDecisionGradeIdentity)
                reasons.Add("Customer identity came from a weaker name/email match and requires confirmation.");
        }
        else if (b.Customer.PastLeads > 0)
            reasons.Add($"Buyer has sent us {N0(b.Customer.PastLeads)} lead(s) before but has never become a customer.");
        else
            reasons.Add("New buyer — no history with us.");

        // -- deadline --
        switch (b.Deadline.Urgency)
        {
            case LeadDecisionUrgency.Overdue:
                reasons.Add("Bid closing date has already passed.");
                break;
            case LeadDecisionUrgency.Critical:
                reasons.Add($"Deadline in {N0(b.Deadline.DaysLeft ?? 0)} day(s) — tight for {N0(c.TotalItems)} items.");
                break;
            case LeadDecisionUrgency.Soon:
                reasons.Add($"Deadline in {N0(b.Deadline.DaysLeft ?? 0)} day(s).");
                break;
            case LeadDecisionUrgency.Comfortable:
                reasons.Add($"Comfortable deadline — {N0(b.Deadline.DaysLeft ?? 0)} days left.");
                break;
            default:
                reasons.Add("No bid closing date on record — confirm the deadline with the buyer.");
                break;
        }

        // -- extraction quality --
        if (aiConfidence.HasValue && aiConfidence.Value < LowExtractionConfidence)
            reasons.Add("Low extraction confidence — verify the extracted lines before quoting.");

        string recommendation;
        if (overdue)
            recommendation = LeadDecisionRecommendations.Skip;
        else if (!catalogAssessable || c.TotalItems == 0)
            recommendation = LeadDecisionRecommendations.CannotAssess;
        else if (c.CoveragePct < SkipCoveragePct)
            recommendation = LeadDecisionRecommendations.Skip;
        else if (c.CoveragePct >= BidCoveragePct
                 && (b.Customer.IsDecisionGradeIdentity || b.MarginPotentialPct >= BidMarginPct))
            recommendation = LeadDecisionRecommendations.Bid;
        else
            recommendation = LeadDecisionRecommendations.Review;

        return (recommendation, reasons);
    }

    // ================================================================ helpers

    /// <summary>Pulls the address out of "Display Name &lt;user@host&gt;" / raw strings.</summary>
    internal static string? ExtractEmailAddress(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var lt = s.LastIndexOf('<');
        if (lt >= 0)
        {
            var gt = s.IndexOf('>', lt);
            s = (gt > lt ? s[(lt + 1)..gt] : s[(lt + 1)..]).Trim();
        }
        return s.Contains('@') ? s : null;
    }

    /// <summary>Longest usable token of the normalized name (≥ 4 chars), for the ILIKE fallback.</summary>
    internal static string? MostSignificantToken(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName)) return null;
        return rawName
            .Split(new[] { ' ', '\t', ',', ';', '/', '-', '(', ')', '[', ']', ':' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= MinNameTokenLength && t.Any(char.IsLetter))
            .OrderByDescending(t => t.Length)
            .FirstOrDefault()
            ?.ToLowerInvariant();
    }

    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    private static decimal? FirstPositive(params decimal?[] values) =>
        values.FirstOrDefault(v => v.HasValue && v.Value > 0);

    private static decimal Pct(int part, int whole) =>
        whole <= 0 ? 0m : Math.Round(100m * part / whole, 1, MidpointRounding.AwayFromZero);

    private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    private static string Fmt(decimal v) => v.ToString("0.#", CultureInfo.InvariantCulture);
    private static string N0(int v) => v.ToString("N0", CultureInfo.InvariantCulture);
    private static string N0Dec(decimal v) => v.ToString("N0", CultureInfo.InvariantCulture);
}
