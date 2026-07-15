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
    }
}
