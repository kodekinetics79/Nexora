using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.DTOs.Dashboard;
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
            var totalOrderValue = await _context.Orders.Where(o => o.BusinessUnitId == businessUnitId).SumAsync(o => o.TotalAmount);
            var orderCount = await _context.Orders.CountAsync(o => o.BusinessUnitId == businessUnitId);
            var quoteCount = await _context.Quotes.CountAsync(q => q.BusinessUnitId == businessUnitId);
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
                TotalOrderValue = totalOrderValue,
                CustomerCount = customerCount,
                AvgQuoteValue = quoteCount > 0 ? await _context.Quotes.Where(q => q.BusinessUnitId == businessUnitId).AverageAsync(q => (decimal)q.TotalAmount) : 0m,
                AvgOrderValue = orderCount > 0 ? await _context.Orders.Where(o => o.BusinessUnitId == businessUnitId).AverageAsync(o => (decimal)o.TotalAmount) : 0m,
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
                data.VolumeTrend.Add(new MonthlyTrendDTO
                {
                    Month = m.ToString("MMM"),
                    Count = await _context.Rfqs.CountAsync(r => r.BusinessUnitId == businessUnitId && r.CreatedDate.Month == m.Month && r.CreatedDate.Year == m.Year),
                    Value = await _context.Orders.Where(o => o.BusinessUnitId == businessUnitId && o.OrderDate.Month == m.Month && o.OrderDate.Year == m.Year).SumAsync(o => o.TotalAmount)
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

            // 5. Efficiency Velocity (Actual categories with items)
            data.EfficiencyVelocity = await _context.ProductCategories
                .Where(c => c.BusinessUnitId == businessUnitId)
                .Select(c => new CategoryDistributionDTO
                {
                    CategoryName = c.CategoryName,
                    Count = _context.Rfqitems.Count(ri => ri.Rfq.BusinessUnitId == businessUnitId && ri.Product != null && ri.Product.CategoryId == c.Id),
                    Percentage = 0 
                })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToListAsync();

            // 6. Operational Health (Radar Chart with real KPI logic)
            data.OperationalHealth = new List<RadarDataDTO>
            {
                new RadarDataDTO { Subject = "Lead Conversion", A = data.Stats.ConversionRates.LeadToRfq, B = 70 },
                new RadarDataDTO { Subject = "Bid Capacity", A = data.Stats.BidRatio, B = 85 },
                new RadarDataDTO { Subject = "Win Rate", A = data.Stats.ConversionRates.QuoteToOrder, B = 40 },
                new RadarDataDTO { Subject = "Catalog Match", A = totalLineItems > 0 ? (double)_context.Rfqitems.Count(ri => ri.Rfq.BusinessUnitId == businessUnitId && ri.Product != null) / totalLineItems * 100 : 0, B = 60 },
                new RadarDataDTO { Subject = "AI Accuracy", A = totalLeads > 0 ? (double)_context.Leads.Where(l => l.BusinessUnitId == businessUnitId).Average(l => l.Aiconfidence ?? 0) * 100 : 0, B = 90 }
            };

            // 7. Response Integrity (Bubble chart simulation: Created Day vs Total Amount)
            data.ResponseIntegrity = await _context.Rfqs
                .Where(r => r.BusinessUnitId == businessUnitId)
                .OrderByDescending(r => r.CreatedDate)
                .Take(15)
                .Select(r => new ScatterDataDTO
                {
                    X = r.CreatedDate.Day,
                    Y = (double)(r.Quotes.Any() ? r.Quotes.Average(q => q.TotalAmount) : 0m),
                    Z = r.Rfqitems.Count * 5,
                    Name = r.Rfqno ?? "RFQ"
                }).ToListAsync();

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
                .Select(l => new { l.AssignTo, l.BidClosingDate })
                .ToListAsync();

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
                    OverdueLeads = myLeads.Count(l => IsOverdue(l.BidClosingDate, now)),
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
                OverdueLeads = unassignedLeads.Count(l => IsOverdue(l.BidClosingDate, now)),
                SentQuotes = orphanQuotes.Count,
                StaleQuotes = orphanQuotes.Count(q => IsStaleSentQuote(q.SentOn, q.RespondedOn, staleDays, now)),
                IsUnassignedBucket = true
            });

            return new TeamWorkloadDTO { Rows = rows, StaleQuoteDays = staleDays, GeneratedAt = now };
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

            // Value estimates from the leads' own priced lines (UnitPrice × Quantity);
            // nullable SUM so an empty set materializes as null, not a throw.
            var totalLeadValue = await _context.LeadItems
                .Where(li => li.Lead.BusinessUnitId == businessUnitId && li.UnitPrice > 0 && li.Quantity > 0)
                .SumAsync(li => (decimal?)(li.UnitPrice!.Value * li.Quantity)) ?? 0m;
            var acceptedLeadValue = await _context.LeadItems
                .Where(li => li.Lead.BusinessUnitId == businessUnitId
                             && li.Lead.LeadStatusId != null && acceptedLeadStatusIds.Contains(li.Lead.LeadStatusId.Value)
                             && li.Lead.LeadRejectedReasonId == null
                             && li.UnitPrice > 0 && li.Quantity > 0)
                .SumAsync(li => (decimal?)(li.UnitPrice!.Value * li.Quantity)) ?? 0m;

            // ── Stage 3+4: quoted / won (quote totals) ──
            var quotedCount = await _context.Quotes.CountAsync(q => q.BusinessUnitId == businessUnitId);
            var quotedValue = await _context.Quotes
                .Where(q => q.BusinessUnitId == businessUnitId)
                .SumAsync(q => q.TotalAmount) ?? 0m;

            var wonCount = await _context.Quotes.CountAsync(q =>
                q.BusinessUnitId == businessUnitId
                && q.StatusId != null && wonQuoteStatusIds.Contains(q.StatusId.Value));
            var wonValue = await _context.Quotes
                .Where(q => q.BusinessUnitId == businessUnitId
                            && q.StatusId != null && wonQuoteStatusIds.Contains(q.StatusId.Value))
                .SumAsync(q => q.TotalAmount) ?? 0m;

            // ── Losses grouped by outcome reason (name resolved via SetupMaster) ──
            var lostGroups = await _context.Quotes.AsNoTracking()
                .Where(q => q.BusinessUnitId == businessUnitId
                            && q.StatusId != null && lostQuoteStatusIds.Contains(q.StatusId.Value))
                .GroupBy(q => q.OutcomeReasonId)
                .Select(g => new { ReasonId = g.Key, Count = g.Count(), Value = g.Sum(q => q.TotalAmount) ?? 0m })
                .ToListAsync();

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
                    Value = g.Value
                })
                .OrderByDescending(r => r.Count)
                .ToList();

            // ── Weighted forecast over the open SENT pipeline:
            //    still waiting × 0.3 + responded-but-undecided × 0.5 ──
            var sentQuotes = await _context.Quotes.AsNoTracking()
                .Where(q => q.BusinessUnitId == businessUnitId
                            && q.StatusId != null && sentQuoteStatusIds.Contains(q.StatusId.Value))
                .Select(q => new { q.TotalAmount, q.RespondedOn })
                .ToListAsync();

            var awaiting = sentQuotes.Where(q => q.RespondedOn == null).ToList();
            var responded = sentQuotes.Where(q => q.RespondedOn != null).ToList();
            var awaitingValue = awaiting.Sum(q => q.TotalAmount ?? 0m);
            var respondedValue = responded.Sum(q => q.TotalAmount ?? 0m);

            // ── Quoted-vs-floor margin proxy. Floor = the pricing engine's cost
            //    basis (FinalLandedCost ?? UnitCost); only lines where that floor
            //    actually exists are sampled — never guessed. ──
            var marginRows = await _context.QuoteItems.AsNoTracking()
                .Where(qi => qi.Quote.BusinessUnitId == businessUnitId && qi.UnitPrice > 0)
                .Select(qi => new
                {
                    qi.UnitPrice,
                    Cost = qi.Product != null ? (qi.Product.FinalLandedCost ?? qi.Product.UnitCost) : null
                })
                .ToListAsync();

            var marginSamples = marginRows
                .Where(r => r.Cost.HasValue && r.Cost.Value > 0)
                .Select(r => (r.UnitPrice - r.Cost!.Value) / r.UnitPrice)
                .ToList();

            return new PipelineAnalyticsDTO
            {
                Funnel = new List<PipelineStageDTO>
                {
                    new() { Key = "leads", Label = "Requests received", Count = totalLeads, Value = Round2(totalLeadValue) },
                    new() { Key = "accepted", Label = "Accepted to work on", Count = acceptedLeads, Value = Round2(acceptedLeadValue) },
                    new() { Key = "quoted", Label = "Quotes created", Count = quotedCount, Value = Round2(quotedValue) },
                    new() { Key = "won", Label = "Won", Count = wonCount, Value = Round2(wonValue) }
                },
                LossReasons = lossReasons,
                WeightedForecast = Round2(awaitingValue * 0.3m + respondedValue * 0.5m),
                AwaitingResponseQuotes = awaiting.Count,
                AwaitingResponseValue = Round2(awaitingValue),
                RespondedQuotes = responded.Count,
                RespondedValue = Round2(respondedValue),
                AvgMarginPct = marginSamples.Count > 0
                    ? Math.Round(marginSamples.Average() * 100m, 1, MidpointRounding.AwayFromZero)
                    : null,
                MarginSampleLines = marginSamples.Count,
                TotalQuoteLines = marginRows.Count,
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
