using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.DTOs.QuoteDTOs
{
    public class QuoteStatsDTO
    {
        public int TotalQuotes { get; set; }
        public int AcceptedQuotes { get; set; }
        public int PendingQuotes { get; set; }
        public int ExpiredQuotes { get; set; }

        /// <summary>
        /// Total quoted value converted into <see cref="TotalQuotedAmountCurrency"/> using approved,
        /// effective-dated FX rates.
        ///
        /// NULL means "not answerable", never "zero". Quotes are denominated per row
        /// (Quote.CurrencyId), so when any quote's currency has no approved rate — or carries no
        /// currency at all — no single figure is honest, and this is left null with
        /// <see cref="TotalQuotedAmountUnavailableReason"/> explaining why. Callers must not
        /// coalesce it to 0. <see cref="QuotedAmountsByCurrency"/> is always populated and is the
        /// correct thing to display in that case.
        ///
        /// This was non-nullable and was a raw cross-currency sum before the FX work; the frontend
        /// (Frontend/src/pages/Dashboard/briefing.ts) already models it as `number | null`.
        /// </summary>
        public decimal? TotalQuotedAmount { get; set; }

        /// <summary>ISO code of the currency <see cref="TotalQuotedAmount"/> is expressed in.</summary>
        public string? TotalQuotedAmountCurrency { get; set; }

        /// <summary>True only when every quote was converted from approved evidence.</summary>
        public bool TotalQuotedAmountConverted { get; set; }

        /// <summary>User-facing explanation when <see cref="TotalQuotedAmount"/> is null.</summary>
        public string? TotalQuotedAmountUnavailableReason { get; set; }

        /// <summary>
        /// Per-currency breakdown, always populated regardless of conversion outcome. This is the
        /// honest answer when a blended total cannot be produced.
        /// </summary>
        public List<QuoteStatsCurrencyBreakdownDTO> QuotedAmountsByCurrency { get; set; } = new();
    }

    /// <summary>One currency's share of the quoted pipeline, plus the rate applied to it.</summary>
    public class QuoteStatsCurrencyBreakdownDTO
    {
        public long? CurrencyId { get; set; }
        public string? CurrencyCode { get; set; }

        /// <summary>Sum in the quotes' own currency.</summary>
        public decimal Subtotal { get; set; }

        public int QuoteCount { get; set; }

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
