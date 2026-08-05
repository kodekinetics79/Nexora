using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs.OrderDTOs
{
    public class OrderStatsDTO
    {
        public int TotalOrders { get; set; }
        public int PendingOrders { get; set; }
        public int CompletedOrders { get; set; }

        /// <summary>
        /// Total order revenue converted into <see cref="TotalRevenueCurrency"/> using approved,
        /// effective-dated FX rates.
        ///
        /// NULL means "not answerable", never "zero". Order.TotalAmount is denominated by
        /// Order.CurrencyId, so when any order's currency has no approved rate — or carries no
        /// currency at all — no single figure is honest, and this is left null with
        /// <see cref="TotalRevenueUnavailableReason"/> explaining why. Callers must not coalesce
        /// it to 0. <see cref="RevenueByCurrency"/> is always populated and is the correct thing
        /// to display in that case.
        ///
        /// This was a non-nullable raw cross-currency sum before the FX work. Same shape and same
        /// contract as QuoteStatsDTO.TotalQuotedAmount.
        /// </summary>
        public decimal? TotalRevenue { get; set; }

        /// <summary>ISO code of the currency <see cref="TotalRevenue"/> is expressed in.</summary>
        public string? TotalRevenueCurrency { get; set; }

        /// <summary>True only when every order was converted from approved evidence.</summary>
        public bool TotalRevenueConverted { get; set; }

        /// <summary>User-facing explanation when <see cref="TotalRevenue"/> is null.</summary>
        public string? TotalRevenueUnavailableReason { get; set; }

        /// <summary>
        /// Per-currency breakdown, always populated regardless of conversion outcome. This is the
        /// honest answer when a blended total cannot be produced.
        /// </summary>
        public List<OrderStatsCurrencyBreakdownDTO> RevenueByCurrency { get; set; } = new();
    }

    /// <summary>One currency's share of order revenue, plus the rate applied to it.</summary>
    public class OrderStatsCurrencyBreakdownDTO
    {
        public long? CurrencyId { get; set; }
        public string? CurrencyCode { get; set; }

        /// <summary>Sum in the orders' own currency.</summary>
        public decimal Subtotal { get; set; }

        public int OrderCount { get; set; }

        /// <summary>Whether an approved rate was found for this currency.</summary>
        public bool Converted { get; set; }

        /// <summary>The approved rate applied, at 10dp (the General Ledger scale).</summary>
        public decimal? ExchangeRate { get; set; }

        /// <summary>Subtotal expressed in the base currency; null when not convertible.</summary>
        public decimal? ConvertedSubtotal { get; set; }

        /// <summary>Why this currency could not be converted, when applicable.</summary>
        public string? Reason { get; set; }
    }
}
