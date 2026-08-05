using System;
using System.Collections.Generic;
using System.Linq;
using ERP_RFQ_Automation.Fx;

namespace ERP_RFQ_Automation.DTOs.CurrencyDTOs
{
    /// <summary>
    /// The audit trail behind any converted figure on the commercial side.
    ///
    /// Attached next to a money field so a reader can always tell three things apart:
    /// a real converted total, a total that is genuinely zero, and a total that could not be
    /// produced at all. Without this, a null or a 0 is ambiguous — which is exactly the failure
    /// mode the FX work exists to remove.
    /// </summary>
    public class FxTotalEvidenceDTO
    {
        /// <summary>True only when every contributing amount was converted from approved evidence.</summary>
        public bool Converted { get; set; }

        /// <summary>ISO code the converted figure is expressed in (the base currency).</summary>
        public string? CurrencyCode { get; set; }

        /// <summary>User-facing explanation when conversion failed. Null when it succeeded.</summary>
        public string? UnavailableReason { get; set; }

        /// <summary>Per-currency breakdown; always populated, even when the blended total is null.</summary>
        public List<FxCurrencyComponentDTO> ByCurrency { get; set; } = new();

        public static FxTotalEvidenceDTO From(FxTotalResult result) => new()
        {
            Converted = result.Converted,
            CurrencyCode = result.TargetCurrencyCode,
            UnavailableReason = result.UnavailableReason,
            ByCurrency = result.Components.Select(c => new FxCurrencyComponentDTO
            {
                CurrencyId = c.CurrencyId,
                CurrencyCode = c.CurrencyCode,
                Subtotal = c.Subtotal,
                RowCount = c.RowCount,
                Converted = c.Converted,
                ExchangeRate = c.Rate,
                ConvertedSubtotal = c.ConvertedSubtotal,
                Reason = c.Reason
            }).ToList()
        };
    }

    /// <summary>One currency's contribution to a total, with the approved rate applied to it.</summary>
    public class FxCurrencyComponentDTO
    {
        public long? CurrencyId { get; set; }
        public string? CurrencyCode { get; set; }

        /// <summary>Subtotal in the rows' own currency.</summary>
        public decimal Subtotal { get; set; }

        /// <summary>How many source rows contributed.</summary>
        public int RowCount { get; set; }

        public bool Converted { get; set; }

        /// <summary>The approved rate applied, at 10dp (the General Ledger scale).</summary>
        public decimal? ExchangeRate { get; set; }

        public decimal? ConvertedSubtotal { get; set; }

        /// <summary>Why this currency could not be converted, when applicable.</summary>
        public string? Reason { get; set; }
    }

    /// <summary>An effective-dated FX rate as returned by the API.</summary>
    public class FxRateResponseDTO
    {
        public long Id { get; set; }
        public long BusinessUnitID { get; set; }
        public long FromCurrencyId { get; set; }
        public string? FromCurrencyCode { get; set; }
        public long ToCurrencyId { get; set; }
        public string? ToCurrencyCode { get; set; }
        public decimal Rate { get; set; }
        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public string Source { get; set; } = null!;
        public string Status { get; set; } = null!;
        public string? ApprovedBy { get; set; }
        public DateTime? ApprovedOn { get; set; }
        public long Version { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>Request to record a new effective-dated rate. It starts unapproved and therefore inert.</summary>
    public class FxRateCreateRequestDTO
    {
        public long FromCurrencyId { get; set; }
        public long ToCurrencyId { get; set; }

        /// <summary>One unit of FromCurrency equals this many units of ToCurrency. Must be &gt; 0.</summary>
        public decimal Rate { get; set; }

        public DateTime EffectiveFrom { get; set; }
        public DateTime? EffectiveTo { get; set; }
        public string? Source { get; set; }
        public string? Note { get; set; }
    }

    /// <summary>The frozen rate that was applied to a document — the auditor's answer.</summary>
    public class FxRateSnapshotResponseDTO
    {
        public long Id { get; set; }
        public string DocumentType { get; set; } = null!;
        public long DocumentId { get; set; }
        public long FromCurrencyId { get; set; }
        public string? FromCurrencyCode { get; set; }
        public long ToCurrencyId { get; set; }
        public string? ToCurrencyCode { get; set; }
        public decimal Rate { get; set; }
        public long? FxRateId { get; set; }
        public DateTime RateEffectiveFrom { get; set; }
        public string ResolutionPath { get; set; } = null!;
        public DateTime AsOf { get; set; }
        public DateTime CapturedOn { get; set; }
        public string CapturedBy { get; set; } = null!;
    }
}
