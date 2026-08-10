using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.DTOs.Dashboard;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;
using ERP_RFQ_Automation.Fx;
using ERP_RFQ_Automation.Interfaces;

namespace ERP_RFQ_Automation.Repositories
{
    public class DashboardRepository : IDashboardRepository
    {
        private readonly ErpRfqAutomationContext _context;

        public DashboardRepository(ErpRfqAutomationContext context)
        {
            _context = context;
        }

        public async Task<DashboardDataDTO> GetDashboardDataAsync(long businessUnitId)
        {
            var data = new DashboardDataDTO();
            var today = DateTime.UtcNow.Date;
            var thirtyDaysAgo = today.AddDays(-30);
            var sixtyDaysAgo = today.AddDays(-60);

            // 1. Core Metrics & Aggregations (Optimized: Queries directly to database)
            var totalLeads = await _context.Leads.CountAsync(l => l.BusinessUnitId == businessUnitId);
            var activeLeads = await _context.Leads.CountAsync(l => l.BusinessUnitId == businessUnitId && (l.LeadStatus == null || (l.LeadStatus.SetupValue != "Rejected" && l.LeadStatus.SetupValue != "Closed")));
            var totalRfqs = await _context.Rfqs.CountAsync(r => r.BusinessUnitId == businessUnitId);
            var rfqsQuoted = await _context.Rfqs.CountAsync(r => r.BusinessUnitId == businessUnitId && r.Quotes.Any());
            var totalLineItems = await _context.Rfqitems.CountAsync(ri => ri.Rfq.BusinessUnitId == businessUnitId);
            var l1Quoted = await _context.Rfqitems.CountAsync(ri => ri.Rfq.BusinessUnitId == businessUnitId && ri.UnitPrice > 0);

            // FX fix: `SumAsync(o => o.TotalAmount)` and the two AverageAsync calls below used to
            // add and average Order/Quote totals across currencies as bare decimals. Both entities
            // carry a CurrencyId that was never read. Amounts are now pulled with their currency
            // and converted to the base currency through approved, effective-dated rates; the
            // figures fail closed to null (with a surfaced reason) rather than blend AED with USD.
            var fx = new FxConversionService(_context);
            var orderAmounts = await _context.Orders.AsNoTracking()
                .Where(o => o.BusinessUnitId == businessUnitId)
                .Select(o => new { o.TotalAmount, o.CurrencyId })
                .ToListAsync();
            var quoteAmounts = await _context.Quotes.AsNoTracking()
                .Where(q => q.BusinessUnitId == businessUnitId)
                .Select(q => new { q.TotalAmount, q.CurrencyId })
                .ToListAsync();

            var orderTotalFx = await fx.TotalAsync(businessUnitId,
                orderAmounts.Select(o => new FxAmount(o.TotalAmount, o.CurrencyId)).ToArray(), today);
            var quoteTotalFx = await fx.TotalAsync(businessUnitId,
                quoteAmounts.Select(q => new FxAmount(q.TotalAmount ?? 0m, q.CurrencyId)).ToArray(), today);

            var orderCount = orderAmounts.Count;
            var quoteCount = quoteAmounts.Count;
            var customerCount = await _context.Customers.CountAsync(c => c.Buid == businessUnitId);

            // 2. Trend Calculations (Current 30d vs Previous 30d)
            var currentLeadsCount = await _context.Leads.CountAsync(l => l.BusinessUnitId == businessUnitId && l.CreatedDate >= thirtyDaysAgo);
            var previousLeadsCount = await _context.Leads.CountAsync(l => l.BusinessUnitId == businessUnitId && l.CreatedDate >= sixtyDaysAgo && l.CreatedDate < thirtyDaysAgo);
            
            var currentRfqsCount = await _context.Rfqs.CountAsync(r => r.BusinessUnitId == businessUnitId && r.CreatedDate >= thirtyDaysAgo);
            var previousRfqsCount = await _context.Rfqs.CountAsync(r => r.BusinessUnitId == businessUnitId && r.CreatedDate >= sixtyDaysAgo && r.CreatedDate < thirtyDaysAgo);

            var currentOrdersCount = await _context.Orders.CountAsync(o => o.BusinessUnitId == businessUnitId && o.CreatedOn >= thirtyDaysAgo);
            var previousOrdersCount = await _context.Orders.CountAsync(o => o.BusinessUnitId == businessUnitId && o.CreatedOn >= sixtyDaysAgo && o.CreatedOn < thirtyDaysAgo);

            data.Stats = new DashboardStatsDTO
            {
                TotalLeads = totalLeads,
                ActiveLeads = activeLeads,
                TotalRfqs = totalRfqs,
                RfqsQuoted = rfqsQuoted,
                TotalLineItems = totalLineItems,
                L1Quoted = l1Quoted,
                TotalOrderValue = orderTotalFx.Total,
                CustomerCount = customerCount,
                // The mean is taken on the CONVERTED total, so it is a mean of comparable
                // quantities. When the total is unavailable the mean is too — an average of
                // partially-converted values would be a different kind of lie.
                AvgQuoteValue = quoteCount > 0 && quoteTotalFx.Total.HasValue
                    ? FxConversionService.RoundMoney(quoteTotalFx.Total.Value / quoteCount)
                    : (decimal?)null,
                AvgOrderValue = orderCount > 0 && orderTotalFx.Total.HasValue
                    ? FxConversionService.RoundMoney(orderTotalFx.Total.Value / orderCount)
                    : (decimal?)null,
                OrderValueFx = FxTotalEvidenceDTO.From(orderTotalFx),
                QuoteValueFx = FxTotalEvidenceDTO.From(quoteTotalFx),
                BidRatio = totalRfqs > 0 ? (double)rfqsQuoted / totalRfqs * 100 : 0,
                WinVolumeRatio = totalRfqs > 0 ? (double)orderCount / totalRfqs * 100 : 0,
                ConversionRates = new ConversionRatesDTO
                {
                    LeadToRfq = totalLeads > 0 ? (double)totalRfqs / totalLeads * 100 : 0,
                    RfqToQuote = totalRfqs > 0 ? (double)rfqsQuoted / totalRfqs * 100 : 0,
                    QuoteToOrder = quoteCount > 0 ? (double)orderCount / quoteCount * 100 : 0
                },
                LeadsTrend = CalculateTrend(currentLeadsCount, previousLeadsCount),
                RfqsTrend = CalculateTrend(currentRfqsCount, previousRfqsCount),
                OrdersTrend = CalculateTrend(currentOrdersCount, previousOrdersCount)
            };

            // 3. Volume Trend (Last 6 Months)
            var months = Enumerable.Range(0, 6)
                .Select(i => today.AddMonths(-i))
                .OrderBy(d => d)
                .ToList();

            data.VolumeTrend = new List<MonthlyTrendDTO>();
            foreach (var m in months)
            {
                // FX fix: each bucket used to be a raw cross-currency SumAsync. Because the
                // currency mix varies month to month, the SHAPE of the trend line — not just its
                // level — was an artefact of that mix. Each month is now converted on its own and
                // fails closed independently, so one unconvertible month cannot distort the rest.
                var monthAmounts = await _context.Orders.AsNoTracking()
                    .Where(o => o.BusinessUnitId == businessUnitId && o.OrderDate.Month == m.Month && o.OrderDate.Year == m.Year)
                    .Select(o => new { o.TotalAmount, o.CurrencyId })
                    .ToListAsync();
                var monthFx = await fx.TotalAsync(businessUnitId,
                    monthAmounts.Select(o => new FxAmount(o.TotalAmount, o.CurrencyId)).ToArray(), today);

                data.VolumeTrend.Add(new MonthlyTrendDTO
                {
                    Month = m.ToString("MMM"),
                    Count = await _context.Rfqs.CountAsync(r => r.BusinessUnitId == businessUnitId && r.CreatedDate.Month == m.Month && r.CreatedDate.Year == m.Year),
                    Value = monthFx.Total,
                    ValueCurrency = monthFx.TargetCurrencyCode,
                    ValueUnavailableReason = monthFx.UnavailableReason
                });
            }

            // 4. Status Distribution (RFQs)
            data.StatusDistribution = await _context.Rfqs
                .Where(r => r.BusinessUnitId == businessUnitId)
                .GroupBy(r => r.Rfqstatus != null ? r.Rfqstatus.SetupValue : "Unknown")
                .Select(g => new CategoryDistributionDTO
                {
                    CategoryName = g.Key,
                    Count = g.Count(),
                    Percentage = totalRfqs > 0 ? (decimal)g.Count() / totalRfqs * 100 : 0
                }).ToListAsync();

            // 5/6/7. WITHDRAWN FOR THE PILOT — three chart series that asserted more than
            // the data supports. Each is returned empty rather than deleted from the DTO so
            // the contract holds while the presentation layer removes the panels.
            //
            //   EfficiencyVelocity — product categories with an item count and a Percentage
            //     hardcoded to 0. A percentage column that is always zero is not a missing
            //     value, it is a wrong one, and it renders as a flat bar chart implying the
            //     categories carry no volume.
            //
            //   OperationalHealth (radar) — five subjects, five targets, none sourced. The
            //     B values (70/85/40/60/90) were invented; a chart drawn against an invented
            //     target tells a reader they are behind or ahead of nothing. Two of the
            //     subjects were worse than unsourced:
            //       * "Catalog Match" reported 5 matched lines out of 5 as 100%, a perfect
            //         score off a five-line sample.
            //       * "AI Accuracy" averaged Lead.Aiconfidence and multiplied by 100. That
            //         column is not an accuracy: on the structured path the normalizer
            //         writes a literal 1.0 when a cell parsed and 0.2 when it did not, and
            //         on the model path it is the model's own self-report. Averaging it and
            //         labelling the result "AI Accuracy" published a number that had never
            //         been measured against anything. Measured accuracy now lives at
            //         /api/Dashboard/extraction-accuracy, which returns no percentage until
            //         a field has 30 approved documents behind it.
            //
            //   ResponseIntegrity (bubble) — X was the DAY OF THE MONTH the RFQ was created
            //     plotted against mean quote value. Day-of-month is not a variable; the
            //     chart's shape was an artefact of the calendar.
            data.EfficiencyVelocity = new List<CategoryDistributionDTO>();
            data.OperationalHealth = new List<RadarDataDTO>();
            data.ResponseIntegrity = new List<ScatterDataDTO>();

            // 8. Recent Activities (Real-time timeline)
            var recentLeads = await _context.Leads.Where(l => l.BusinessUnitId == businessUnitId).OrderByDescending(l => l.CreatedDate).Take(10).Select(l => new RecentItemDTO { Id = l.Rfqno, Type = "Lead", Description = l.BuyersName, Status = l.LeadStatus != null ? l.LeadStatus.SetupValue : "Open", Date = l.CreatedDate }).ToListAsync();
            var recentRfqs = await _context.Rfqs.Where(r => r.BusinessUnitId == businessUnitId).OrderByDescending(r => r.CreatedDate).Take(10).Select(r => new RecentItemDTO { Id = r.Rfqno, Type = "RFQ", Description = r.Customer != null ? r.Customer.Name : "Project", Status = r.Rfqstatus != null ? r.Rfqstatus.SetupValue : "Open", Date = r.CreatedDate }).ToListAsync();
            var recentOrders = await _context.Orders.Where(o => o.BusinessUnitId == businessUnitId).OrderByDescending(o => o.CreatedOn).Take(10).Select(o => new RecentItemDTO { Id = o.OrderNo, Type = "Order", Description = o.OrderNo, Status = o.Status != null ? o.Status.SetupValue : "Confirmed", Date = o.CreatedOn }).ToListAsync();

            data.RecentItems = recentLeads.Union(recentRfqs).Union(recentOrders)
                .OrderByDescending(x => x.Date)
                .Take(12)
                .ToList();

            // 9. Source Distribution (Email vs Manual vs Folder)
            data.SourceDistribution = await _context.Leads
                .Where(l => l.BusinessUnitId == businessUnitId)
                .GroupBy(l => l.LeadSource ?? "Manual")
                .Select(g => new CategoryDistributionDTO
                {
                    CategoryName = g.Key,
                    Count = g.Count(),
                    Percentage = totalLeads > 0 ? (decimal)g.Count() / totalLeads * 100 : 0
                }).ToListAsync();

            return data;
        }

        private StatTrendDTO CalculateTrend(int current, int previous)
        {
            if (previous == 0) return new StatTrendDTO { Value = current > 0 ? "100%" : "0%", IsUp = current > 0 };
            double diff = ((double)(current - previous) / previous) * 100;
            return new StatTrendDTO
            {
                Value = $"{Math.Abs(diff):F1}%",
                IsUp = diff >= 0
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // PILOT ANALYTICS 1/3 — deadline board
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Every open enquiry bucketed by how long is left to answer it, with the line-item
        /// count that says how much work each bucket actually is.
        ///
        /// This is the one analytic a trading desk uses every morning, and it needs nothing
        /// the pilot tenant will not have: no customer identity, no catalog, no FX, no
        /// lifecycle events. Just Lead.BidClosingDate and a line count, both of which are
        /// populated today.
        ///
        /// TWO DISCLOSURES ARE PART OF THE ANSWER, not footnotes:
        ///   * leads with NO usable closing date are counted separately rather than dropped
        ///     into a comfortable bucket — 27 leads with a silent gap look like 27 leads
        ///     under control;
        ///   * leads that ENTERED Nexora after their own closing date are flagged, because
        ///     they are overdue on arrival and counting them against handling performance
        ///     books a loss that predates the product. Same rule as the workload view.
        /// </summary>
        public async Task<DeadlineBoardDTO> GetDeadlineBoardAsync(
            long businessUnitId, int maxLeads = 200, CancellationToken cancellationToken = default)
        {
            var now = DateTime.UtcNow;
            var today = now.Date;

            // "Open" mirrors GetDashboardDataAsync's ActiveLeads: untriaged leads count,
            // because untriaged is precisely the state the deadline board exists to surface.
            var rows = await _context.Leads.AsNoTracking()
                .Where(l => l.BusinessUnitId == businessUnitId)
                .Where(l => l.LeadStatus == null
                            || (l.LeadStatus.SetupValue != "Rejected" && l.LeadStatus.SetupValue != "Closed"))
                .Where(l => l.LeadRejectedReasonId == null)
                .Select(l => new
                {
                    l.Id,
                    l.Rfqno,
                    l.BuyersName,
                    l.BidClosingDate,
                    l.SubDate,
                    l.CreatedDate,
                    LineItems = l.LeadItems.Count,
                    AwaitingReview = l.EmailIngests == null
                        ? !l.CommercialFactsVerified
                        : l.EmailIngests.ParseStatus == "NeedsReview"
                })
                .ToListAsync(cancellationToken);

            var earliestReceivedOn = await ERP_RFQ_Automation.LeadIdentity.LeadIngestionAudit
                .EarliestSourceReceivedOnAsync(_context, businessUnitId, rows.Select(r => r.Id).ToList());

            var leads = rows.Select(r =>
            {
                var hasDate = r.BidClosingDate.HasValue && r.BidClosingDate.Value.Year >= SentinelYearFloor;
                int? daysLeft = hasDate ? (r.BidClosingDate!.Value.Date - today).Days : null;
                var lateIngested = ERP_RFQ_Automation.LeadIdentity.LeadIngestionAudit.IsLateIngested(
                    earliestReceivedOn.TryGetValue(r.Id, out var receivedOn) ? receivedOn : null,
                    r.CreatedDate, r.BidClosingDate, r.SubDate);
                return new DeadlineLeadDTO(
                    r.Id, r.Rfqno, r.BuyersName,
                    hasDate ? r.BidClosingDate : null,
                    daysLeft,
                    BucketKey(daysLeft),
                    r.LineItems,
                    r.AwaitingReview,
                    lateIngested);
            }).ToList();

            var buckets = BucketOrder
                .Select(bucket => new DeadlineBucketDTO(
                    bucket.Key,
                    bucket.Label,
                    leads.Count(l => l.Bucket == bucket.Key),
                    leads.Where(l => l.Bucket == bucket.Key).Sum(l => l.LineItems)))
                .ToList();

            // Most urgent first; leads with no date sort last but are never hidden.
            var ordered = leads
                .OrderBy(l => l.DaysLeft.HasValue ? 0 : 1)
                .ThenBy(l => l.DaysLeft ?? int.MaxValue)
                .ThenByDescending(l => l.LineItems)
                .Take(Math.Clamp(maxLeads, 1, 1000))
                .ToList();

            return new DeadlineBoardDTO(
                now,
                leads.Count,
                leads.Sum(l => l.LineItems),
                leads.Count(l => l.DaysLeft is null),
                leads.Count(l => l.LateIngested),
                buckets,
                ordered);
        }

        private static readonly (string Key, string Label)[] BucketOrder =
        {
            ("overdue", "Past deadline"),
            ("today", "Closing today"),
            ("days_1_3", "1–3 days"),
            ("days_4_7", "4–7 days"),
            ("days_8_30", "8–30 days"),
            ("later", "More than 30 days"),
            ("unknown", "No closing date")
        };

        private static string BucketKey(int? daysLeft) => daysLeft switch
        {
            null => "unknown",
            < 0 => "overdue",
            0 => "today",
            <= 3 => "days_1_3",
            <= 7 => "days_4_7",
            <= 30 => "days_8_30",
            _ => "later"
        };

        // ════════════════════════════════════════════════════════════════════
        // PILOT ANALYTICS 3/3 — document yield and review funnel
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// What happened to every document the tenant sent us, end to end, and how much of
        /// what we produced from them is actually usable.
        ///
        /// Yield and quality are ONE question. Nine leads out of fifty-four resolved jobs is
        /// not a 16.7% inefficiency to be optimised later; it is forty-five enquiries that
        /// entered the building and produced nothing, and it belongs on the same screen as
        /// the field-completeness tiles. Every stage carries the previous stage as its
        /// denominator, so a reader can see exactly where the loss is rather than being
        /// handed a single composite score.
        ///
        /// The concentration line exists because it is the fact most likely to mislead:
        /// when a handful of documents carry almost all the lines, any line-weighted
        /// quality figure is a statement about those few documents and nothing else.
        /// </summary>
        public async Task<DocumentYieldDTO> GetDocumentYieldAsync(
            long businessUnitId, DateTime from, DateTime to, CancellationToken cancellationToken = default)
        {
            var fromOffset = new DateTimeOffset(DateTime.SpecifyKind(from, DateTimeKind.Utc));
            var toOffset = new DateTimeOffset(DateTime.SpecifyKind(to, DateTimeKind.Utc));

            var documents = await _context
                .Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.SourceDocument>().AsNoTracking()
                .Where(d => d.BusinessUnitId == businessUnitId && d.CreatedOn >= fromOffset && d.CreatedOn <= toOffset)
                .Select(d => new { d.Id, d.SecurityStatus })
                .ToListAsync(cancellationToken);

            var jobs = await _context.Set<ERP_RFQ_Automation.Extraction.ExtractionJob>().AsNoTracking()
                .Where(j => j.BusinessUnitId == businessUnitId && j.CreatedOn >= from && j.CreatedOn <= to)
                .Select(j => new { j.Id, j.Status, j.ResultLeadId })
                .ToListAsync(cancellationToken);

            var leadIds = jobs.Where(j => j.ResultLeadId.HasValue).Select(j => j.ResultLeadId!.Value)
                .Distinct().ToList();

            var lines = await _context.LeadItems.AsNoTracking()
                .Where(li => li.Lead.BusinessUnitId == businessUnitId && leadIds.Contains(li.LeadId))
                .Select(li => new { li.LeadId, HasExtraFields = li.ExtraFields != null })
                .ToListAsync(cancellationToken);

            var reviewedLeadIds = await _context.Set<LeadReviewAudit>().AsNoTracking()
                .Where(a => a.BusinessUnitId == businessUnitId && a.Action == "approve" && leadIds.Contains(a.LeadId))
                .Select(a => a.LeadId)
                .Distinct()
                .ToListAsync(cancellationToken);

            var evidencedLeadIds = await _context
                .Set<ERP_RFQ_Automation.DocumentIntelligence.Persistence.CanonicalInquiry>().AsNoTracking()
                .Where(i => i.BusinessUnitId == businessUnitId && i.LeadId.HasValue && leadIds.Contains(i.LeadId!.Value))
                .Select(i => i.LeadId!.Value)
                .Distinct()
                .ToListAsync(cancellationToken);

            var submitted = documents.Count;
            var cleared = documents.Count(d =>
                d.SecurityStatus == ERP_RFQ_Automation.DocumentIntelligence.Persistence.DocumentSecurityStatus.Cleared);
            var jobsCreated = jobs.Count;
            var jobsSucceeded = jobs.Count(j => j.Status == ERP_RFQ_Automation.Extraction.ExtractionStatus.Succeeded);
            var leadsProduced = leadIds.Count;
            var linesProduced = lines.Count;

            var stages = new List<FunnelStageDTO>
            {
                Stage("submitted", "Documents submitted", submitted, null,
                    "Source documents recorded for this tenant in the window."),
                Stage("cleared", "Cleared security", cleared, submitted,
                    "Documents whose security status is Cleared / documents submitted."),
                Stage("jobs", "Extraction jobs created", jobsCreated, cleared,
                    "Extraction jobs created in the window / documents cleared."),
                Stage("succeeded", "Extraction succeeded", jobsSucceeded, jobsCreated,
                    "Jobs in Succeeded state / extraction jobs created."),
                Stage("leads", "Leads produced", leadsProduced, jobsSucceeded,
                    "Distinct leads bound to a succeeded job / jobs succeeded. THIS is document yield."),
                Stage("reviewed", "Leads approved by a reviewer", reviewedLeadIds.Count, leadsProduced,
                    "Leads with at least one approved human review / leads produced.")
            };

            var coverage = new List<CoverageTileDTO>
            {
                Tile("lines", "Line items extracted", linesProduced, linesProduced,
                    "Line items on the leads produced in this window."),
                Tile("extra-fields", "Lines preserving the customer's own columns",
                    lines.Count(l => l.HasExtraFields), linesProduced,
                    "Lines carrying ExtraFields (unmapped source columns kept verbatim) / lines extracted."),
                Tile("evidence", "Leads with a source-address evidence ledger",
                    evidencedLeadIds.Count, leadsProduced,
                    "Leads with a canonical inquiry, i.e. a per-field link back to the cell it came from / leads produced. "
                    + "Populated on the structured spreadsheet path; PDF and OCR runs do not yet retain word boxes.")
            };

            // Line concentration: how much of the corpus the biggest documents account for.
            var linesByLead = lines.GroupBy(l => l.LeadId).Select(g => g.Count())
                .OrderByDescending(count => count).ToList();
            var topCount = Math.Min(2, linesByLead.Count);
            var topLines = linesByLead.Take(topCount).Sum();
            decimal? topShare = linesProduced > 0
                ? decimal.Round(topLines * 100m / linesProduced, 1)
                : null;

            return new DocumentYieldDTO(
                DateTime.UtcNow, from, to, stages, coverage, topCount, topShare,
                topShare is null
                    ? "No lines were produced in this window, so no concentration can be reported."
                    : $"The {topCount} largest document(s) account for {topShare}% of all lines "
                      + $"({topLines} of {linesProduced}). Any line-weighted quality figure is "
                      + "predominantly a statement about those documents.");
        }

        private static FunnelStageDTO Stage(string key, string label, long numerator, long? denominator,
            string definition) => new(key, label, numerator, denominator,
            denominator is null or 0 ? null : decimal.Round(numerator * 100m / denominator.Value, 1), definition);

        private static CoverageTileDTO Tile(string key, string label, long covered, long total,
            string definition) => new(key, label, covered, total,
            total == 0 ? null : decimal.Round(covered * 100m / total, 1), definition);

        // ════════════════════════════════════════════════════════════════════
        // WP-B1: manager team-workload view
        // ════════════════════════════════════════════════════════════════════

        /// <summary>Extraction sentinel floor — dates before this year are "unknown", never overdue.</summary>
        private const int SentinelYearFloor = 2000;

        public async Task<TeamWorkloadDTO> GetTeamWorkloadAsync(long businessUnitId)
        {
            var now = DateTime.UtcNow;
            var acceptedLeadStatusIds = await ResolveStatusIdsAsync("LeadStatus", "ACCEPTED", "Accepted", legacyId: 24);
            var sentQuoteStatusIds = await ResolveStatusIdsAsync("QuoteStatus", "SENT", "Sent", legacyId: 43);
            var staleDays = await GetStaleQuoteDaysAsync(businessUnitId);

            // Active reps of this BU — one query; rows are built for every rep so
            // managers also see who has capacity (zeros are informative).
            var users = await _context.Users.AsNoTracking()
                .Where(u => u.Buid == businessUnitId && u.IsActive != false)
                .Select(u => new { u.Id, u.FirstName, u.LastName, u.Email })
                .ToListAsync();

            // Open leads = accepted, never rejected. Same definition the SLA sweep
            // uses; overdue requires a REAL closing date (sentinels < 2000 ignored).
            var leadRows = await _context.Leads.AsNoTracking()
                .Where(l => l.BusinessUnitId == businessUnitId
                            && l.LeadStatusId != null && acceptedLeadStatusIds.Contains(l.LeadStatusId.Value)
                            && l.LeadRejectedReasonId == null)
                .Select(l => new { l.Id, l.AssignTo, l.BidClosingDate, l.SubDate, l.CreatedDate })
                .ToListAsync();

            // ── Ingestion audit (owner requirement: audit fairness) ──────────
            // A lead INGESTED into Nexora after its business due date was already
            // past deadline on arrival: counting it as "overdue" would book a
            // loss that predates Nexora against Nexora's aging performance.
            // Such leads are EXCLUDED from the overdue metric below, and the
            // number excluded is surfaced on the payload so the exclusion is
            // visible, never silent. Ingestion timestamp = earliest source
            // received_on (occurrence chain), CreatedDate fallback — the same
            // shared rule the lead read models use (LeadIngestionAudit).
            var earliestReceivedOn = await ERP_RFQ_Automation.LeadIdentity.LeadIngestionAudit
                .EarliestSourceReceivedOnAsync(_context, businessUnitId, leadRows.Select(l => l.Id).ToList());
            var lateIngestedLeadIds = leadRows
                .Where(l => ERP_RFQ_Automation.LeadIdentity.LeadIngestionAudit.IsLateIngested(
                    earliestReceivedOn.TryGetValue(l.Id, out var receivedOn) ? receivedOn : null,
                    l.CreatedDate, l.BidClosingDate, l.SubDate))
                .Select(l => l.Id)
                .ToHashSet();

            // SENT quotes; ownership is resolved from Quote.CreatedBy (free-text
            // identity) by email or "First Last" — the same rule as the SLA
            // stale-quote digest, so both features agree on who owns a quote.
            var quoteRows = await _context.Quotes.AsNoTracking()
                .Where(q => q.BusinessUnitId == businessUnitId
                            && q.StatusId != null && sentQuoteStatusIds.Contains(q.StatusId.Value))
                .Select(q => new { q.CreatedBy, q.SentOn, q.RespondedOn })
                .ToListAsync();

            var rows = new List<TeamWorkloadRowDTO>();
            var matchedQuoteOwners = new HashSet<int>(); // indices into quoteRows already attributed

            foreach (var u in users)
            {
                var fullName = $"{u.FirstName} {u.LastName}".Trim();
                var myLeads = leadRows.Where(l => l.AssignTo == u.Id).ToList();

                var myQuotes = new List<(DateTime? SentOn, DateTime? RespondedOn)>();
                for (var i = 0; i < quoteRows.Count; i++)
                {
                    var q = quoteRows[i];
                    if (string.Equals(u.Email, q.CreatedBy, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(fullName, q.CreatedBy, StringComparison.OrdinalIgnoreCase))
                    {
                        myQuotes.Add((q.SentOn, q.RespondedOn));
                        matchedQuoteOwners.Add(i);
                    }
                }

                rows.Add(new TeamWorkloadRowDTO
                {
                    UserId = u.Id,
                    Name = fullName.Length > 0 ? fullName : u.Email,
                    Email = u.Email,
                    OpenLeads = myLeads.Count,
                    // Audit fairness: late-ingested leads (entered Nexora after
                    // their due date) are excluded — arriving late is not aging.
                    OverdueLeads = myLeads.Count(l =>
                        IsOverdue(l.BidClosingDate, now) && !lateIngestedLeadIds.Contains(l.Id)),
                    SentQuotes = myQuotes.Count,
                    StaleQuotes = myQuotes.Count(q => IsStaleSentQuote(q.SentOn, q.RespondedOn, staleDays, now))
                });
            }

            // Unassigned bucket: leads nobody owns + quotes whose CreatedBy matched
            // no active BU user (owner unknown ⇒ effectively unowned work).
            var unassignedLeads = leadRows.Where(l => l.AssignTo == null || !users.Any(u => u.Id == l.AssignTo)).ToList();
            var orphanQuotes = quoteRows.Where((q, i) => !matchedQuoteOwners.Contains(i)).ToList();

            rows = rows
                .OrderByDescending(r => r.OpenLeads)
                .ThenByDescending(r => r.SentQuotes)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            rows.Add(new TeamWorkloadRowDTO
            {
                UserId = null,
                Name = "Unassigned",
                Email = null,
                OpenLeads = unassignedLeads.Count,
                // Audit fairness: same late-ingested exclusion as the rep rows.
                OverdueLeads = unassignedLeads.Count(l =>
                    IsOverdue(l.BidClosingDate, now) && !lateIngestedLeadIds.Contains(l.Id)),
                SentQuotes = orphanQuotes.Count,
                StaleQuotes = orphanQuotes.Count(q => IsStaleSentQuote(q.SentOn, q.RespondedOn, staleDays, now)),
                IsUnassignedBucket = true
            });

            return new TeamWorkloadDTO
            {
                Rows = rows,
                StaleQuoteDays = staleDays,
                GeneratedAt = now,
                // Audit visibility: open leads the overdue metric excluded because
                // they were ingested after their due date. Reported so the
                // exclusion is never silent.
                LateIngestedExcludedLeads = leadRows.Count(l =>
                    IsOverdue(l.BidClosingDate, now) && lateIngestedLeadIds.Contains(l.Id))
            };
        }

        private static bool IsOverdue(DateTime? bidClosingDate, DateTime now) =>
            bidClosingDate.HasValue && bidClosingDate.Value.Year >= SentinelYearFloor && bidClosingDate.Value < now;

        /// <summary>Row is already filtered to SENT status; mirrors SlaComputed.IsStale.</summary>
        private static bool IsStaleSentQuote(DateTime? sentOn, DateTime? respondedOn, int staleDays, DateTime now) =>
            ERP_RFQ_Automation.Sla.SlaComputed.IsStale("SENT", sentOn, respondedOn, staleDays, now);

        // ════════════════════════════════════════════════════════════════════
        // WP-B2: pipeline / margin analytics
        // ════════════════════════════════════════════════════════════════════

        public async Task<PipelineAnalyticsDTO> GetPipelineAnalyticsAsync(long businessUnitId)
        {
            var now = DateTime.UtcNow;
            var acceptedLeadStatusIds = await ResolveStatusIdsAsync("LeadStatus", "ACCEPTED", "Accepted", legacyId: 24);
            var sentQuoteStatusIds = await ResolveStatusIdsAsync("QuoteStatus", "SENT", "Sent", legacyId: 43);
            var wonQuoteStatusIds = (await ResolveStatusIdsAsync("QuoteStatus", "ACCEPTED", "Accepted", legacyId: 44))
                .Concat(await ResolveStatusIdsAsync("QuoteStatus", "ORDERED", "Ordered", legacyId: null))
                .Distinct().ToList();
            var lostQuoteStatusIds = (await ResolveStatusIdsAsync("QuoteStatus", "REJECTED", "Rejected", legacyId: 45))
                .Concat(await ResolveStatusIdsAsync("QuoteStatus", "EXPIRED", "Expired", legacyId: null))
                .Distinct().ToList();

            // ── Stage 1+2: leads received / leads accepted (counts + priced-line value) ──
            var totalLeads = await _context.Leads.CountAsync(l => l.BusinessUnitId == businessUnitId);
            var acceptedLeads = await _context.Leads.CountAsync(l =>
                l.BusinessUnitId == businessUnitId
                && l.LeadStatusId != null && acceptedLeadStatusIds.Contains(l.LeadStatusId.Value)
                && l.LeadRejectedReasonId == null);

            // FX: LeadItem carries a FREE-TEXT currency code with no FK to Currency, so codes are
            // mapped to currency ids for this business unit; an unrecognised or blank code yields
            // a null currency, which the conversion engine treats as unconvertible rather than
            // silently folding into the total.
            var fx = new FxConversionService(_context);
            var currencyIdByCode = await _context.Currencies.AsNoTracking()
                .Where(c => c.BusinessUnitId == businessUnitId)
                .ToDictionaryAsync(c => c.Code.ToUpperInvariant(), c => c.Id);

            long? MapCode(string? code) =>
                !string.IsNullOrWhiteSpace(code) && currencyIdByCode.TryGetValue(code.Trim().ToUpperInvariant(), out var id)
                    ? id
                    : (long?)null;

            // Value estimates from the leads' own priced lines (UnitPrice × Quantity).
            var leadLines = await _context.LeadItems.AsNoTracking()
                .Where(li => li.Lead.BusinessUnitId == businessUnitId && li.UnitPrice > 0 && li.Quantity > 0)
                .Select(li => new
                {
                    li.UnitPrice,
                    li.Quantity,
                    li.Currency,
                    Accepted = li.Lead.LeadStatusId != null && acceptedLeadStatusIds.Contains(li.Lead.LeadStatusId.Value)
                               && li.Lead.LeadRejectedReasonId == null
                })
                .ToListAsync();

            // Both non-null by the `UnitPrice > 0 && Quantity > 0` filter on the query above; a
            // line with no stated quantity is excluded there, exactly as a zero one was, so the
            // pipeline value is unchanged by quantity becoming nullable.
            var totalLeadFx = await fx.TotalAsync(businessUnitId,
                leadLines.Select(li => new FxAmount(li.UnitPrice!.Value * li.Quantity!.Value, MapCode(li.Currency))).ToArray(), now);
            var acceptedLeadFx = await fx.TotalAsync(businessUnitId,
                leadLines.Where(li => li.Accepted)
                    .Select(li => new FxAmount(li.UnitPrice!.Value * li.Quantity!.Value, MapCode(li.Currency))).ToArray(), now);

            // ── Stage 3+4: quoted / won (quote totals) ──
            var pipelineQuotes = await _context.Quotes.AsNoTracking()
                .Where(q => q.BusinessUnitId == businessUnitId)
                .Select(q => new { q.TotalAmount, q.CurrencyId, q.StatusId, q.OutcomeReasonId, q.RespondedOn })
                .ToListAsync();

            var quotedCount = pipelineQuotes.Count;
            var quotedFx = await fx.TotalAsync(businessUnitId,
                pipelineQuotes.Select(q => new FxAmount(q.TotalAmount ?? 0m, q.CurrencyId)).ToArray(), now);

            var wonQuotes = pipelineQuotes
                .Where(q => q.StatusId != null && wonQuoteStatusIds.Contains(q.StatusId.Value)).ToList();
            var wonCount = wonQuotes.Count;
            var wonFx = await fx.TotalAsync(businessUnitId,
                wonQuotes.Select(q => new FxAmount(q.TotalAmount ?? 0m, q.CurrencyId)).ToArray(), now);

            // ── Losses grouped by outcome reason (name resolved via SetupMaster) ──
            // Each reason group is converted independently, so one unconvertible group does not
            // suppress the others.
            var lostQuotes = pipelineQuotes
                .Where(q => q.StatusId != null && lostQuoteStatusIds.Contains(q.StatusId.Value)).ToList();
            var lostGroups = new List<(long? ReasonId, int Count, FxTotalResult Fx)>();
            foreach (var group in lostQuotes.GroupBy(q => q.OutcomeReasonId))
            {
                var groupFx = await fx.TotalAsync(businessUnitId,
                    group.Select(q => new FxAmount(q.TotalAmount ?? 0m, q.CurrencyId)).ToArray(), now);
                lostGroups.Add((group.Key, group.Count(), groupFx));
            }

            var reasonIds = lostGroups.Where(g => g.ReasonId.HasValue).Select(g => g.ReasonId!.Value).Distinct().ToList();
            var reasonNames = reasonIds.Count == 0
                ? new Dictionary<long, string>()
                : await _context.SetupMasters.AsNoTracking()
                    .Where(s => reasonIds.Contains(s.SetupId))
                    .ToDictionaryAsync(s => s.SetupId, s => s.Description ?? s.SetupValue);

            var lossReasons = lostGroups
                .Select(g => new PipelineLossReasonDTO
                {
                    Reason = g.ReasonId.HasValue && reasonNames.TryGetValue(g.ReasonId.Value, out var name)
                        ? name
                        : "No reason recorded",
                    Count = g.Count,
                    Value = g.Fx.Total,
                    ValueCurrency = g.Fx.TargetCurrencyCode,
                    ValueUnavailableReason = g.Fx.UnavailableReason
                })
                .OrderByDescending(r => r.Count)
                .ToList();

            // ── Weighted forecast over the open SENT pipeline:
            //    still waiting × 0.3 + responded-but-undecided × 0.5 ──
            // FX fix: both buckets used to be raw cross-currency sums that were then weighted and
            // ADDED to each other. Each bucket is now converted to base currency first; if either
            // cannot be converted the forecast fails closed to null rather than compounding the
            // error through the weighting.
            var sentQuotes = pipelineQuotes
                .Where(q => q.StatusId != null && sentQuoteStatusIds.Contains(q.StatusId.Value)).ToList();

            var awaiting = sentQuotes.Where(q => q.RespondedOn == null).ToList();
            var responded = sentQuotes.Where(q => q.RespondedOn != null).ToList();
            var awaitingFx = await fx.TotalAsync(businessUnitId,
                awaiting.Select(q => new FxAmount(q.TotalAmount ?? 0m, q.CurrencyId)).ToArray(), now);
            var respondedFx = await fx.TotalAsync(businessUnitId,
                responded.Select(q => new FxAmount(q.TotalAmount ?? 0m, q.CurrencyId)).ToArray(), now);

            var forecastAvailable = awaitingFx.Total.HasValue && respondedFx.Total.HasValue;
            var forecastReason = awaitingFx.UnavailableReason ?? respondedFx.UnavailableReason;

            // ── Gross margin is NOT computed here any more. ──
            // It used to be: average of per-line (unitPrice - (Product.FinalLandedCost ?? UnitCost))
            // / unitPrice, over every quote line ever written. Three defects in one figure.
            //   1. WRONG COST. FinalLandedCost is not a landed cost. SupplierPurchaseHistoryRepository
            //      sets it to the last purchase row's bare UnitPrice, ignoring freight, duty and
            //      currency; it is also free-typed in the product form and imported from a
            //      spreadsheet column. The cost the PRICE was built on lives on
            //      CustomerQuoteSourcingDecision.SupplierLandedUnitCost and was never read.
            //   2. UNWEIGHTED MEAN OF RATIOS. A 1-unit line at 60% and a 10,000-unit line at 5%
            //      reported 32.5%, which is the gross margin of nothing.
            //   3. NO PERIOD AND NO OUTCOME FILTER. Drafts and lost bids were in the sample.
            // Reporting/GrossMarginService computes it value-weighted from the sourcing decision,
            // period-filtered on accepted quotes, and returns "unavailable" rather than a number
            // when the evidence is not there. Exposed at GET /api/dashboard/gross-margin.

            return new PipelineAnalyticsDTO
            {
                Funnel = new List<PipelineStageDTO>
                {
                    new() { Key = "leads", Label = "Requests received", Count = totalLeads,
                            Value = totalLeadFx.Total, ValueCurrency = totalLeadFx.TargetCurrencyCode,
                            ValueUnavailableReason = totalLeadFx.UnavailableReason },
                    new() { Key = "accepted", Label = "Accepted to work on", Count = acceptedLeads,
                            Value = acceptedLeadFx.Total, ValueCurrency = acceptedLeadFx.TargetCurrencyCode,
                            ValueUnavailableReason = acceptedLeadFx.UnavailableReason },
                    new() { Key = "quoted", Label = "Quotes created", Count = quotedCount,
                            Value = quotedFx.Total, ValueCurrency = quotedFx.TargetCurrencyCode,
                            ValueUnavailableReason = quotedFx.UnavailableReason },
                    new() { Key = "won", Label = "Won", Count = wonCount,
                            Value = wonFx.Total, ValueCurrency = wonFx.TargetCurrencyCode,
                            ValueUnavailableReason = wonFx.UnavailableReason }
                },
                LossReasons = lossReasons,
                WeightedForecast = forecastAvailable
                    ? Round2(awaitingFx.Total!.Value * 0.3m + respondedFx.Total!.Value * 0.5m)
                    : (decimal?)null,
                ForecastCurrency = awaitingFx.TargetCurrencyCode,
                ForecastUnavailableReason = forecastAvailable ? null : forecastReason,
                AwaitingResponseQuotes = awaiting.Count,
                AwaitingResponseValue = awaitingFx.Total,
                RespondedQuotes = responded.Count,
                RespondedValue = respondedFx.Total,
                FunnelScope = PipelineAnalyticsDTO.AllTimeScope,
                GeneratedAt = now
            };
        }

        public async Task<DashboardRelease01DTO> GetRelease01Async(
            long businessUnitId,
            long? ownerUserId,
            string roleScope,
            DateTime from,
            DateTime to,
            DateTime generatedAt,
            CancellationToken cancellationToken = default)
        {
            if (businessUnitId <= 0) throw new ArgumentOutOfRangeException(nameof(businessUnitId));
            if (from >= to) throw new ArgumentException("The dashboard reporting window is invalid.");
            if (generatedAt < to) throw new ArgumentException("The generated-at boundary cannot precede the reporting window.");

            var scopedLeads = _context.Leads.AsNoTracking()
                .Where(lead => lead.BusinessUnitId == businessUnitId
                    && (!ownerUserId.HasValue || lead.AssignTo == ownerUserId.Value));

            var leadRows = await scopedLeads
                .Where(lead => lead.CreatedDate < to)
                .Select(lead => new Release01LeadRow(
                    lead.Id,
                    lead.CommercialCaseId,
                    lead.CommercialCaseReference,
                    lead.CreatedDate,
                    lead.RequiresCommercialReview,
                    lead.CommercialFactsVerified,
                    lead.LeadStatus != null ? lead.LeadStatus.SetupCode : null,
                    lead.LeadStatus != null ? lead.LeadStatus.SetupValue : null))
                .ToListAsync(cancellationToken);

            var lifecycleRows = await (
                from lifecycleEvent in _context.CommercialLifecycleEvents.AsNoTracking()
                join lead in scopedLeads
                    on new { lifecycleEvent.BusinessUnitId, lifecycleEvent.CommercialCaseId }
                    equals new { lead.BusinessUnitId, lead.CommercialCaseId }
                where lifecycleEvent.BusinessUnitId == businessUnitId
                    && lifecycleEvent.AggregateType == "Lead"
                    && lifecycleEvent.OccurredOn < generatedAt
                    && (lifecycleEvent.NewStatusCode == "RECEIVED"
                        || lifecycleEvent.NewStatusCode == "QUALIFIED"
                        || lifecycleEvent.NewStatusCode == "DISQUALIFIED")
                select new Release01LifecycleRow(
                    lifecycleEvent.Id,
                    lifecycleEvent.AggregateId,
                    lifecycleEvent.CommercialCaseId,
                    lifecycleEvent.CommercialCaseReference,
                    lifecycleEvent.NewStatusCode,
                    lifecycleEvent.OccurredOn))
                .ToListAsync(cancellationToken);

            var kpis = new List<DashboardRelease01KpiDTO>();
            AddLeadsReceivedKpi(kpis, leadRows, lifecycleRows, from, to);
            AddLeadsRequiringReviewKpi(kpis, leadRows);
            AddQualificationKpis(kpis, lifecycleRows, from, to);

            kpis.Add(Insufficient("assignment_sla", "Assignment SLA", "percentage",
                "Cases assigned within the configured SLA divided by received cases requiring assignment.",
                "LEAD_ASSIGNED events and a versioned assignment-SLA policy are not yet present in the authoritative event spine."));
            kpis.Add(Insufficient("active_workload", "Active workload", "weighted_work",
                "Measured weighted nonterminal Lead, RFQ, and Quote work at generatedAt.",
                "Current routing capacity is not based on certified workload weights."));
            kpis.Add(Insufficient("rfqs_created", "RFQs created", "count",
                "Distinct commercial cases with RFQ_CREATED in [from,to).",
                "RFQ_CREATED is not yet recorded as an authoritative commercial event."));
            kpis.Add(Insufficient("lead_to_rfq_conversion", "Lead to RFQ conversion", "percentage",
                "Received cases in the window that later have RFQ_CREATED, subject to a disclosed maturity cutoff.",
                "The event spine does not yet contain RFQ_CREATED or a release maturity policy."));
            kpis.Add(Insufficient("quotes_ready", "Quotes ready", "count",
                "Distinct latest quote revisions with QUOTE_READY in [from,to).",
                "Quote lifecycle events and revision governance are not yet authoritative."));
            kpis.Add(Insufficient("quote_value_sent", "Quote value sent", "currency",
                "Latest valid quote value at QUOTE_SENT in tenant base currency.",
                "QUOTE_SENT events, revision governance, and an authoritative base-currency conversion boundary are incomplete."));
            kpis.Add(Insufficient("quote_response_rate", "Quote response rate", "percentage",
                "Mature sent cases with QUOTE_RESPONDED divided by mature QUOTE_SENT cases.",
                "Quote response events and a release maturity policy are not yet authoritative."));
            kpis.Add(Insufficient("win_rate", "Win rate", "percentage",
                "Latest QUOTE_WON outcomes divided by latest QUOTE_WON plus QUOTE_LOST outcomes in the window.",
                "Quote outcomes currently bypass the governed commercial event spine."));
            kpis.Add(Insufficient("partial_outcome_rate", "Partial outcome rate", "percentage",
                "QUOTE_PARTIAL cases divided by cases with a terminal quote outcome in the window.",
                "QUOTE_PARTIAL is not yet an authoritative commercial event."));
            kpis.Add(Insufficient("no_quote_rate", "No-quote rate", "percentage",
                "NO_QUOTE decisions divided by cases reaching a quote decision in the window.",
                "NO_QUOTE decisions and required reasons are not yet on the authoritative event spine."));
            kpis.Add(Insufficient("follow_ups_overdue", "Follow-ups overdue", "count",
                "Open FOLLOW_UP_DUE events past due without a later FOLLOW_UP_COMPLETED event.",
                "Follow-up due and completion events are not yet authoritative."));
            kpis.Add(Insufficient("order_conversion", "Order conversion", "percentage",
                "Mature QUOTE_SENT cases with ORDER_CREATED divided by mature QUOTE_SENT cases.",
                "Quote and order milestone events and a release maturity policy are incomplete."));
            kpis.Add(Insufficient("straight_through_processing_rate", "Straight-through processing rate", "percentage",
                "Completed lead-processing runs without human review divided by completed runs.",
                "Processing path and review outcome are not yet linked to the commercial event cohort."));
            kpis.Add(Insufficient("extraction_correction_rate", "Extraction correction rate", "percentage",
                "User-corrected reviewed fields divided by reviewed extracted fields.",
                "An append-only correction and reviewer-decision ledger is not yet available."));

            return new DashboardRelease01DTO
            {
                GeneratedAt = generatedAt,
                Filter = new DashboardRelease01FilterDTO { From = from, To = to },
                RoleScope = new DashboardRelease01RoleScopeDTO
                {
                    Scope = roleScope,
                    OwnerUserId = ownerUserId
                },
                Kpis = kpis
            };
        }

        private static void AddLeadsReceivedKpi(
            ICollection<DashboardRelease01KpiDTO> kpis,
            IReadOnlyCollection<Release01LeadRow> leads,
            IReadOnlyCollection<Release01LifecycleRow> lifecycleRows,
            DateTime from,
            DateTime to)
        {
            var operational = leads
                .Where(lead => lead.CreatedDate >= from && lead.CreatedDate < to)
                .GroupBy(lead => lead.CommercialCaseId)
                .Select(group => group.First())
                .ToList();
            var received = lifecycleRows
                .Where(row => row.StatusCode == "RECEIVED" && row.OccurredOn >= from && row.OccurredOn < to)
                .GroupBy(row => row.CommercialCaseId)
                .Select(group => group.OrderBy(row => row.OccurredOn).ThenBy(row => row.EventId).First())
                .ToList();
            var complete = operational.Count == received.Count
                && operational.Select(row => row.CommercialCaseId).Order().SequenceEqual(
                    received.Select(row => row.CommercialCaseId).Order());

            kpis.Add(new DashboardRelease01KpiDTO
            {
                Key = "leads_received",
                Label = "Leads received",
                State = complete ? DashboardRelease01Contract.Available : DashboardRelease01Contract.InsufficientData,
                Value = complete ? received.Count : null,
                Unit = "count",
                Numerator = received.Count,
                Denominator = operational.Count,
                Definition = "Distinct commercial cases with LEAD_RECEIVED in [from,to).",
                InsufficientDataReason = complete ? null :
                    "Operational leads in the window do not reconcile one-to-one with LEAD_RECEIVED events.",
                DrillDownIdentifiers = received.Select(row => Identifier(row, "received")).ToList()
            });
        }

        private static void AddLeadsRequiringReviewKpi(
            ICollection<DashboardRelease01KpiDTO> kpis,
            IReadOnlyCollection<Release01LeadRow> leads)
        {
            var review = leads.Where(IsLeadReviewOpen)
                .OrderBy(lead => lead.Id)
                .ToList();
            kpis.Add(new DashboardRelease01KpiDTO
            {
                Key = "leads_requiring_review",
                Label = "Leads requiring review",
                State = DashboardRelease01Contract.Available,
                Value = review.Count,
                Unit = "count",
                Definition = "Tenant- and role-visible leads received before the window end whose authoritative current record requires commercial review.",
                DrillDownIdentifiers = review.Select(lead => new DashboardRelease01DrillDownIdentifierDTO
                {
                    RecordType = "lead",
                    RecordId = lead.Id,
                    CommercialCaseId = lead.CommercialCaseId,
                    NexoraSerial = lead.NexoraSerial,
                    Classification = "review_required"
                }).ToList()
            });
        }

        private static void AddQualificationKpis(
            ICollection<DashboardRelease01KpiDTO> kpis,
            IReadOnlyCollection<Release01LifecycleRow> lifecycleRows,
            DateTime from,
            DateTime to)
        {
            var decisions = lifecycleRows
                .Where(row => row.OccurredOn >= from && row.OccurredOn < to
                    && row.StatusCode is "QUALIFIED" or "DISQUALIFIED")
                .GroupBy(row => row.CommercialCaseId)
                .Select(group => group.OrderBy(row => row.OccurredOn).ThenBy(row => row.EventId).First())
                .ToList();
            var qualified = decisions.Where(row => row.StatusCode == "QUALIFIED").ToList();

            kpis.Add(new DashboardRelease01KpiDTO
            {
                Key = "qualification_rate",
                Label = "Qualification rate",
                State = decisions.Count > 0 ? DashboardRelease01Contract.Available : DashboardRelease01Contract.InsufficientData,
                Value = decisions.Count > 0 ? Math.Round((decimal)qualified.Count / decisions.Count * 100m, 2) : null,
                Unit = "percentage",
                Numerator = qualified.Count,
                Denominator = decisions.Count,
                Definition = "First valid QUALIFIED decisions divided by first valid QUALIFIED plus DISQUALIFIED decisions in [from,to), per commercial case.",
                InsufficientDataReason = decisions.Count > 0 ? null : "No governed qualification decisions exist in the reporting window.",
                DrillDownIdentifiers = decisions.Select(row => Identifier(row, row.StatusCode.ToLowerInvariant())).ToList()
            });

            var durations = new List<(Release01LifecycleRow Decision, decimal Hours)>();
            var missingReceived = new List<Release01LifecycleRow>();
            foreach (var decision in decisions)
            {
                var received = lifecycleRows
                    .Where(row => row.CommercialCaseId == decision.CommercialCaseId
                        && row.StatusCode == "RECEIVED" && row.OccurredOn <= decision.OccurredOn)
                    .OrderBy(row => row.OccurredOn)
                    .ThenBy(row => row.EventId)
                    .FirstOrDefault();
                if (received == null)
                {
                    missingReceived.Add(decision);
                    continue;
                }

                durations.Add((decision, (decimal)(decision.OccurredOn - received.OccurredOn).TotalHours));
            }

            var complete = decisions.Count > 0 && missingReceived.Count == 0;
            kpis.Add(new DashboardRelease01KpiDTO
            {
                Key = "median_time_to_qualify",
                Label = "Median time to qualify",
                State = complete ? DashboardRelease01Contract.Available : DashboardRelease01Contract.InsufficientData,
                Value = complete ? Median(durations.Select(row => row.Hours).ToList()) : null,
                Unit = "hours",
                Denominator = decisions.Count,
                Definition = "Median elapsed hours from the first valid LEAD_RECEIVED event to the latest qualification decision in [from,to), per commercial case.",
                InsufficientDataReason = complete ? null : decisions.Count == 0
                    ? "No governed qualification decisions exist in the reporting window."
                    : "One or more qualification decisions have no preceding LEAD_RECEIVED event.",
                DrillDownIdentifiers = durations.Select(row =>
                {
                    var identifier = Identifier(row.Decision, row.Decision.StatusCode.ToLowerInvariant());
                    identifier.DurationHours = Math.Round(row.Hours, 2);
                    return identifier;
                }).Concat(missingReceived.Select(row => Identifier(row, "missing_received_event"))).ToList()
            });
        }

        private static DashboardRelease01KpiDTO Insufficient(
            string key, string label, string unit, string definition, string reason) => new()
        {
            Key = key,
            Label = label,
            State = DashboardRelease01Contract.InsufficientData,
            Unit = unit,
            Definition = definition,
            InsufficientDataReason = reason
        };

        private static DashboardRelease01DrillDownIdentifierDTO Identifier(
            Release01LifecycleRow row, string classification) => new()
        {
            RecordType = "lead",
            RecordId = row.LeadId,
            CommercialCaseId = row.CommercialCaseId,
            NexoraSerial = row.NexoraSerial,
            Classification = classification,
            OccurredAt = row.OccurredOn
        };

        private static string CanonicalStatus(string? code, string? value)
        {
            var status = string.IsNullOrWhiteSpace(code) ? value : code;
            return (status ?? string.Empty).Trim().Replace(' ', '_').ToUpperInvariant();
        }

        private static bool IsLeadReviewOpen(Release01LeadRow lead)
        {
            var status = CanonicalStatus(lead.StatusCode, lead.StatusValue);
            if (status is "DISQUALIFIED" or "CONVERTED_TO_RFQ" or "LOST" or "CANCELLED"
                or "COMPLETED" or "DUPLICATED") return false;
            return status == "UNDER_REVIEW"
                || lead.RequiresCommercialReview && !lead.CommercialFactsVerified;
        }

        private static decimal Median(IReadOnlyList<decimal> values)
        {
            var ordered = values.Order().ToArray();
            var middle = ordered.Length / 2;
            return Math.Round(ordered.Length % 2 == 1
                ? ordered[middle]
                : (ordered[middle - 1] + ordered[middle]) / 2m, 2);
        }

        private sealed record Release01LeadRow(
            long Id,
            long CommercialCaseId,
            string NexoraSerial,
            DateTime CreatedDate,
            bool RequiresCommercialReview,
            bool CommercialFactsVerified,
            string? StatusCode,
            string? StatusValue);

        private sealed record Release01LifecycleRow(
            long EventId,
            long LeadId,
            long CommercialCaseId,
            string NexoraSerial,
            string StatusCode,
            DateTime OccurredOn);

        // ════════════════════════════════════════════════════════════════════
        // Shared status/SLA resolution helpers (Wave B)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Resolves every SetupMaster id carrying a status, by SetupType + SetupCode
        /// (falling back to a case-insensitive SetupValue match for tenants whose
        /// rows have no code), then appends the documented legacy id so
        /// pre-SetupMaster tenants keep working — the exact pattern of
        /// SlaSweepWorker.GetStatusIdsAsync / QuoteService.LegacyQuoteStatusIds.
        /// Never queries by magic id alone.
        /// </summary>
        private async Task<List<long>> ResolveStatusIdsAsync(string setupType, string setupCode, string displayValue, long? legacyId)
        {
            var typeLower = setupType.ToLowerInvariant();
            var valueLower = displayValue.ToLowerInvariant();

            var ids = await _context.SetupMasters.AsNoTracking()
                .Where(s => s.SetupType.ToLower() == typeLower
                            && (s.SetupCode == setupCode || s.SetupValue.ToLower() == valueLower))
                .Select(s => s.SetupId)
                .ToListAsync();

            if (legacyId.HasValue && !ids.Contains(legacyId.Value)) ids.Add(legacyId.Value);
            return ids;
        }

        /// <summary>The BU's configured stale threshold, or the SLA default (7 days).</summary>
        private async Task<int> GetStaleQuoteDaysAsync(long businessUnitId)
        {
            return await _context.Set<ERP_RFQ_Automation.Sla.SlaPolicy>().AsNoTracking()
                .Where(p => p.BusinessUnitId == businessUnitId)
                .Select(p => (int?)p.StaleQuoteDays)
                .FirstOrDefaultAsync()
                ?? ERP_RFQ_Automation.Sla.SlaPolicy.Default(businessUnitId).StaleQuoteDays;
        }

        private static decimal Round2(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);
    }
}
