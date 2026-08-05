using System;
using System.Collections.Generic;
using ERP_RFQ_Automation.DTOs.CurrencyDTOs;

namespace ERP_RFQ_Automation.DTOs.Dashboard
{
    public class DashboardStatsDTO
    {
        public int TotalLeads { get; set; }
        public int ActiveLeads { get; set; }
        public int TotalRfqs { get; set; }
        public int RfqsQuoted { get; set; }
        public double BidRatio { get; set; }
        public int TotalLineItems { get; set; }
        public int L1Quoted { get; set; }

        /// <summary>
        /// Order value converted to the business unit's base currency. NULL means "not
        /// answerable", never zero — see <see cref="OrderValueFx"/> for the reason and the
        /// per-currency breakdown. Was a raw cross-currency sum of Order.TotalAmount.
        /// </summary>
        public decimal? TotalOrderValue { get; set; }

        public double WinVolumeRatio { get; set; }

        /// <summary>Mean quote value in base currency; NULL when any quote is unconvertible.</summary>
        public decimal? AvgQuoteValue { get; set; }

        /// <summary>Mean order value in base currency; NULL when any order is unconvertible.</summary>
        public decimal? AvgOrderValue { get; set; }

        public int CustomerCount { get; set; }

        /// <summary>Conversion evidence behind <see cref="TotalOrderValue"/> and <see cref="AvgOrderValue"/>.</summary>
        public FxTotalEvidenceDTO? OrderValueFx { get; set; }

        /// <summary>Conversion evidence behind <see cref="AvgQuoteValue"/>.</summary>
        public FxTotalEvidenceDTO? QuoteValueFx { get; set; }

        public ConversionRatesDTO ConversionRates { get; set; } = new();

        // Trends and comparisons could be added here later
        public StatTrendDTO? LeadsTrend { get; set; }
        public StatTrendDTO? RfqsTrend { get; set; }
        public StatTrendDTO? OrdersTrend { get; set; }
    }

    public class ConversionRatesDTO
    {
        public double LeadToRfq { get; set; }
        public double RfqToQuote { get; set; }
        public double QuoteToOrder { get; set; }
    }

    public class StatTrendDTO
    {
        public string? Value { get; set; }
        public bool IsUp { get; set; }
    }

    public class MonthlyTrendDTO
    {
        public string? Month { get; set; }
        public int Count { get; set; }

        /// <summary>
        /// Monthly order value in base currency. NULL when that month contains an order whose
        /// currency has no approved rate — the month is then genuinely unknown, and plotting 0
        /// would fabricate a dip in the trend line.
        /// </summary>
        public decimal? Value { get; set; }

        /// <summary>ISO code <see cref="Value"/> is expressed in.</summary>
        public string? ValueCurrency { get; set; }

        /// <summary>Why <see cref="Value"/> is null, when applicable.</summary>
        public string? ValueUnavailableReason { get; set; }
    }

    public class CategoryDistributionDTO
    {
        public string? CategoryName { get; set; }
        public int Count { get; set; }
        public decimal Percentage { get; set; }
    }

    public class RadarDataDTO
    {
        public string? Subject { get; set; }
        public double A { get; set; }
        public double B { get; set; }
    }

    public class ScatterDataDTO
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }
        public string? Name { get; set; }
    }
    
    public class RecentItemDTO
    {
        public string? Id { get; set; }
        public string? Type { get; set; }
        public string? Description { get; set; }
        public string? Status { get; set; }
        public DateTime? Date { get; set; }
    }
    
    public class DashboardDataDTO
    {
        public DashboardStatsDTO? Stats { get; set; }
        public List<MonthlyTrendDTO>? VolumeTrend { get; set; }
        public List<CategoryDistributionDTO>? StatusDistribution { get; set; }
        public List<CategoryDistributionDTO>? EfficiencyVelocity { get; set; }
        public List<RadarDataDTO>? OperationalHealth { get; set; }
        public List<ScatterDataDTO>? ResponseIntegrity { get; set; }
        public List<RecentItemDTO>? RecentItems { get; set; }
        public List<CategoryDistributionDTO>? SourceDistribution { get; set; }
    }
}
