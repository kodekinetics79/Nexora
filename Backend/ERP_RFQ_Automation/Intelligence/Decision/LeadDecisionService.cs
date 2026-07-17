using System.Globalization;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Intelligence.Decision;

/// <summary>
/// Deterministic Lead Decision Brief (see DECISION-WIRING.md).
///
/// SIGNALS
///   coverage — lead items matched against Products with cheap batched queries:
///              exact ItemMaterialCode == PartNo, ManufacturerPartNumber ==
///              ModelNo/PartNo (single IN query, case-insensitive), then a bounded
///              name-contains ILIKE fallback for still-unmatched lines.
///   value    — per line: the lead's own UnitPrice, else the matched product's
///              FinalSalesPrice/SellingPrice, × quantity. Confidence is "high"
///              only when most lines were priced from real numbers.
///   margin   — avg (price − cost)/price over matched+costed lines, where cost is
///              FinalLandedCost ?? UnitCost. Null when unknowable.
///   customer — buyer resolved against Customers by name (ILIKE) and by the
///              ingest sender / client email; then past leads, quotes, and last-
///              24-month orders + value.
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

    // Name-contains fallback bounds — never scan the catalog wholesale.
    private const int MaxNameFallbackQueries = 10;
    private const int MaxNameCandidatesPerQuery = 15;
    private const int MinNameTokenLength = 4;

    private const int OrderLookbackMonths = 24;
    private const int SentinelYearFloor = 2000;
    private const int MaxSummaryLeads = 100;

    private readonly ErpRfqAutomationContext _db;

    public LeadDecisionService(ErpRfqAutomationContext db) => _db = db;

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
                SenderEmail = l.EmailIngests.FromEmail,
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

        // ---- 2 + 3. estimated value & margin potential ---------------------
        var coverageItems = new List<CoverageItem>(items.Count);
        var estimatedValue = 0m;
        var pricedLines = 0;
        var marginSamples = new List<decimal>();

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

            if (price.HasValue)
            {
                pricedLines++;
                if (li.Quantity > 0)
                    estimatedValue += price.Value * li.Quantity;
            }

            if (match is not null && price is > 0m)
            {
                var cost = FirstPositive(match.Product.FinalLandedCost, match.Product.UnitCost);
                if (cost.HasValue)
                    marginSamples.Add((price.Value - cost.Value) / price.Value);
            }

            coverageItems.Add(new CoverageItem
            {
                LeadItemId = li.Id,
                Description = li.ProductShortName ?? li.ProductShortDescription ?? li.ItemMaterialCode,
                Quantity = li.Quantity,
                Matched = match is not null,
                MatchType = match?.MatchType,
                ProductId = match?.Product.Id,
                InStock = match is not null && match.Product.QtyOnHand > 0,
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
            Items = coverageItems
        };

        // "high" only when most lines were priced from real numbers (the lead's
        // own price or a matched product's price) — honest about sparse data.
        var valueConfidence = totalItems > 0 && pricedLines * 2 > totalItems ? "high" : "low";

        var marginPotentialPct = marginSamples.Count > 0
            ? (decimal?)Math.Round(marginSamples.Average() * 100m, 1, MidpointRounding.AwayFromZero)
            : null;

        // Mixed currencies are reported honestly as null, never guessed.
        var currencies = items
            .Select(i => i.Currency?.Trim().ToUpperInvariant())
            .Where(c => !string.IsNullOrEmpty(c))
            .Distinct()
            .ToList();
        var currency = currencies.Count == 1 ? currencies[0] : null;

        // ---- 4. customer history -------------------------------------------
        var customer = await ResolveCustomerHistoryAsync(
            lead.Id, lead.BuyersName, lead.Clientemail ?? lead.SenderEmail, businessUnitId, now, ct);

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
            EstimatedValue = Round2(estimatedValue),
            ValueConfidence = valueConfidence,
            Currency = currency,
            MarginPotentialPct = marginPotentialPct,
            Customer = customer,
            Deadline = deadline
        };
        (brief.Recommendation, brief.Reasons) = Recommend(brief, lead.Aiconfidence);
        return brief;
    }

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
                Items = l.LeadItems.Select(li => new { li.ItemMaterialCode, li.UnitPrice, li.Quantity }).ToList()
            })
            .ToListAsync(ct);
        if (leads.Count == 0) return result;

        // Batched query 2 — every distinct material code across every lead in one
        // IN query; summaries use exact-code coverage only (the cheap signal).
        var codes = leads.SelectMany(l => l.Items)
            .Select(i => i.ItemMaterialCode?.Trim().ToLowerInvariant())
            .Where(c => !string.IsNullOrEmpty(c))
            .Select(c => c!)
            .Distinct()
            .ToList();

        var knownCodes = codes.Count == 0
            ? new HashSet<string>()
            : (await ActiveProducts(businessUnitId)
                   .Where(p => codes.Contains(p.PartNo.ToLower()))
                   .Select(p => p.PartNo.ToLower())
                   .ToListAsync(ct))
              .ToHashSet(StringComparer.Ordinal);

        var now = DateTime.UtcNow;
        foreach (var lead in leads)
        {
            var total = lead.Items.Count;
            var covered = lead.Items.Count(i =>
            {
                var code = i.ItemMaterialCode?.Trim().ToLowerInvariant();
                return !string.IsNullOrEmpty(code) && knownCodes.Contains(code!);
            });
            var coveragePct = Pct(covered, total);

            var estimatedValue = lead.Items
                .Where(i => i.UnitPrice is > 0m && i.Quantity > 0)
                .Sum(i => i.UnitPrice!.Value * i.Quantity);

            var (daysLeft, urgency) = DeadlineBand(lead.BidClosingDate, now);

            // Coarse recommendation: same coverage/overdue gates as the full
            // brief, but no customer or margin signals are consulted here.
            var overdue = urgency == LeadDecisionUrgency.Overdue;
            var recommendation =
                coveragePct < SkipCoveragePct || overdue ? LeadDecisionRecommendations.Skip
                : coveragePct >= BidCoveragePct ? LeadDecisionRecommendations.Bid
                : LeadDecisionRecommendations.Review;

            result[lead.Id] = new LeadDecisionSummary
            {
                LeadId = lead.Id,
                CoveragePct = coveragePct,
                EstimatedValue = Round2(estimatedValue),
                DaysLeft = daysLeft,
                Urgency = urgency,
                Recommendation = recommendation
            };
        }

        return result;
    }

    // ================================================================ catalog matching

    private sealed record ItemRow(
        long Id, string? ItemMaterialCode, string? ManufacturerPartNumber,
        string? ProductShortName, string? ProductShortDescription,
        int Quantity, decimal? UnitPrice, string? Currency);

    private sealed record ProductLite(
        long Id, string PartNo, string? ModelNo, string? ProductName,
        decimal QtyOnHand, decimal? UnitCost, decimal? SellingPrice,
        decimal? FinalLandedCost, decimal? FinalSalesPrice);

    private sealed record CatalogMatch(ProductLite Product, string MatchType);

    /// <summary>
    /// Cheap batched matching: one IN query for every material code / MPN, then a
    /// bounded ILIKE name-contains fallback ONLY for still-unmatched lines
    /// (capped queries, capped candidates — the catalog is never scanned).
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
                    p.QtyOnHand, p.UnitCost, p.SellingPrice, p.FinalLandedCost, p.FinalSalesPrice))
                .ToListAsync(ct);

            foreach (var p in exactRows)
            {
                byPartNo.TryAdd(p.PartNo.Trim().ToLowerInvariant(), p);
                if (!string.IsNullOrWhiteSpace(p.ModelNo))
                    byModelNo.TryAdd(p.ModelNo.Trim().ToLowerInvariant(), p);
            }
        }

        foreach (var li in items)
        {
            var code = li.ItemMaterialCode?.Trim().ToLowerInvariant();
            var mpn = li.ManufacturerPartNumber?.Trim().ToLowerInvariant();

            if (!string.IsNullOrEmpty(code) && byPartNo.TryGetValue(code!, out var byCode))
                result[li.Id] = new CatalogMatch(byCode, "code");
            else if (!string.IsNullOrEmpty(mpn)
                     && (byModelNo.TryGetValue(mpn!, out var byMpn) || byPartNo.TryGetValue(mpn!, out byMpn)))
                result[li.Id] = new CatalogMatch(byMpn, "mpn");
        }

        // ---- bounded name-contains fallback for the leftovers ----
        var fallbackQueriesRun = 0;
        var candidatesByToken = new Dictionary<string, List<ProductLite>>(StringComparer.Ordinal);

        foreach (var li in items)
        {
            if (result.ContainsKey(li.Id)) continue;

            var token = MostSignificantToken(li.ProductShortName ?? li.ProductShortDescription);
            if (token is null) continue;

            if (!candidatesByToken.TryGetValue(token, out var candidates))
            {
                if (fallbackQueriesRun >= MaxNameFallbackQueries) continue; // hard bound
                fallbackQueriesRun++;

                var pattern = $"%{EscapeLike(token)}%";
                candidates = await ActiveProducts(businessUnitId)
                    .Where(p => p.ProductName != null && EF.Functions.ILike(p.ProductName, pattern, "\\"))
                    .OrderBy(p => p.Id)
                    .Take(MaxNameCandidatesPerQuery)
                    .Select(p => new ProductLite(p.Id, p.PartNo, p.ModelNo, p.ProductName,
                        p.QtyOnHand, p.UnitCost, p.SellingPrice, p.FinalLandedCost, p.FinalSalesPrice))
                    .ToListAsync(ct);
                candidatesByToken[token] = candidates;
            }

            if (candidates.Count > 0)
                result[li.Id] = new CatalogMatch(candidates[0], "name");
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
        long leadId, string? buyersName, string? rawEmail, long businessUnitId, DateTime now, CancellationToken ct)
    {
        var history = new CustomerHistory();

        var name = string.IsNullOrWhiteSpace(buyersName) ? null : buyersName.Trim();
        var email = ExtractEmailAddress(rawEmail);

        // Past leads from the same buyer name (cheap signal even when no customer
        // record exists yet).
        if (name is not null)
        {
            var namePattern = EscapeLike(name);
            history.PastLeads = await _db.Leads.AsNoTracking()
                .CountAsync(l => l.BusinessUnitId == businessUnitId
                                 && l.Id != leadId
                                 && l.BuyersName != null
                                 && EF.Functions.ILike(l.BuyersName, namePattern, "\\"), ct);
        }

        if (name is null && email is null) return history; // nothing to resolve — graceful

        // Resolve the buyer against Customers: exact-but-case-insensitive name
        // (ILIKE with escaped pattern = equality, not a scan) or contact email.
        var nameEq = name is null ? null : EscapeLike(name);
        var emailLower = email?.ToLowerInvariant();

        var candidates = await _db.Customers.AsNoTracking()
            .Where(c => c.Buid == null || c.Buid == businessUnitId)
            .Where(c => (c.IsActive == null || c.IsActive == true))
            .Where(c => (nameEq != null && EF.Functions.ILike(c.Name, nameEq, "\\"))
                        || (emailLower != null && c.ContactEmail != null && c.ContactEmail.ToLower() == emailLower))
            .Select(c => new { c.Id, c.Name, c.ContactEmail })
            .Take(5)
            .ToListAsync(ct);

        var resolved = candidates
            .OrderByDescending(c => emailLower != null
                                    && string.Equals(c.ContactEmail, email, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (resolved is null) return history;

        history.IsExistingCustomer = true;
        history.CustomerName = resolved.Name;

        history.Quotes = await _db.Quotes.AsNoTracking()
            .CountAsync(q => q.CustomerId == resolved.Id && q.BusinessUnitId == businessUnitId, ct);

        var since = now.AddMonths(-OrderLookbackMonths);
        var recentOrders = _db.Orders.AsNoTracking()
            .Where(o => o.CustomerId == resolved.Id
                        && o.BusinessUnitId == businessUnitId
                        && o.OrderDate >= since);

        history.Orders = await recentOrders.CountAsync(ct);
        // Nullable projection so SUM over an empty set materializes as null, not a throw.
        history.TotalOrderValue = Round2(await recentOrders.SumAsync(o => (decimal?)o.TotalAmount, ct) ?? 0m);
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
    ///   skip   = coverage &lt; 20% OR overdue
    ///   bid    = coverage ≥ 60% AND not overdue AND (existing customer OR margin ≥ 15%)
    ///   review = everything else
    /// </summary>
    private static (string recommendation, List<string> reasons) Recommend(LeadDecisionBrief b, decimal? aiConfidence)
    {
        var reasons = new List<string>();
        var c = b.Coverage;
        var overdue = b.Deadline.Urgency == LeadDecisionUrgency.Overdue;

        // -- coverage / stock --
        if (c.TotalItems == 0)
            reasons.Add("No line items were extracted for this lead.");
        else if (c.CoveredItems == 0)
            reasons.Add($"None of the {N0(c.TotalItems)} items match our catalog.");
        else
            reasons.Add($"We stock {N0(c.CoveredItems)} of {N0(c.TotalItems)} items " +
                        $"({Fmt(c.CoveragePct)}% coverage, {N0(c.InStockItems)} on hand).");

        // -- value --
        if (b.EstimatedValue > 0)
        {
            var cur = b.Currency is null ? "" : $" {b.Currency}";
            reasons.Add(b.ValueConfidence == "high"
                ? $"Estimated value {N0Dec(b.EstimatedValue)}{cur}."
                : $"Rough estimated value {N0Dec(b.EstimatedValue)}{cur} — most lines had no usable price.");
        }
        else
            reasons.Add("No price information on any line — value unknown.");

        // -- margin --
        if (b.MarginPotentialPct is decimal margin)
        {
            reasons.Add(margin >= BidMarginPct
                ? $"Healthy margin potential (~{Fmt(margin)}%) on the items we can cost."
                : $"Thin margin potential (~{Fmt(margin)}%) on the items we can cost.");
        }

        // -- customer --
        if (b.Customer.IsExistingCustomer)
        {
            var spend = b.Customer.TotalOrderValue > 0
                ? $" — {N0Dec(b.Customer.TotalOrderValue)} in orders over the last {OrderLookbackMonths} months"
                : "";
            reasons.Add($"Existing customer ({b.Customer.CustomerName}){spend}.");
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
        if (c.CoveragePct < SkipCoveragePct || overdue)
            recommendation = LeadDecisionRecommendations.Skip;
        else if (c.CoveragePct >= BidCoveragePct
                 && (b.Customer.IsExistingCustomer || b.MarginPotentialPct >= BidMarginPct))
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
