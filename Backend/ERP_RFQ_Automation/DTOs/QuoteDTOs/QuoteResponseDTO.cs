using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.QuoteDTOs
{
    public class QuoteResponseDTO
    {
        public long Id { get; set; }
        public string QuoteNo { get; set; } = null!;
        public long? RfqId { get; set; }
        public string? RfqNo { get; set; }
        public long? LeadId { get; set; }
        public int SourceLeadRevision { get; set; }
        public int SourceRfqRevision { get; set; }
        public string? RevisionImpact { get; set; }
        public long? CommercialCaseId { get; set; }
        public string? NexoraSerial { get; set; }
        public long? ContactId { get; set; }
        public int LifecycleVersion { get; set; }
        public int Version { get; set; }
        public long? CustomerId { get; set; }
        public string? CustomerName { get; set; }
        public long BusinessUnitId { get; set; }
        public string? BusinessUnitName { get; set; }
        public string? CustomerEmail { get; set; } // Added for persistence
        public DateTime? QuoteDate { get; set; }
        public DateTime? ValidUntil { get; set; }
        public long? StatusId { get; set; }
        public string? StatusValue { get; set; }
        /// <summary>Stable machine code of the status (DRAFT/SENT/ACCEPTED/REJECTED/EXPIRED/ORDERED).</summary>
        public string? StatusCode { get; set; }
        public long? CurrencyId { get; set; }
        public string? CurrencyCode { get; set; }
        public decimal? TotalAmount { get; set; }
        public string? HeaderRemarks { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime? CreatedDate { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public long? DiscountTypeId { get; set; }
        public string? DiscountTypeName { get; set; }
        public decimal? DiscountValue { get; set; }
        public int ItemCount { get; set; } // Optimized: Item count for list views

        // ==== Outcome capture + SLA staleness (WP-A4) ====
        public DateTime? SentOn { get; set; }
        public DateTime? RespondedOn { get; set; }
        public DateTime? OutcomeOn { get; set; }
        public long? OutcomeReasonId { get; set; }
        public string? OutcomeReasonName { get; set; }
        public string? OutcomeNote { get; set; }
        /// <summary>Computed: SENT, no response, and older than the BU's stale threshold.</summary>
        public bool IsStale { get; set; }
        /// <summary>Days since the quote was sent (null when never sent).</summary>
        public int? DaysSinceSent { get; set; }

        public List<QuoteItemResponseDTO> QuoteItems { get; set; } = new List<QuoteItemResponseDTO>();
    }

    public class QuoteItemResponseDTO
    {
        public long Id { get; set; }
        public long QuoteId { get; set; }
        public long? RfqItemId { get; set; }
        public long? ProductId { get; set; }
        public string? ProductName { get; set; }
        public string? ItemDescription { get; set; }
        public decimal Quantity { get; set; }
        /// <summary>Unit the quantity is quoted in (EA, PACK, M, …). Null on legacy lines.</summary>
        public string? UnitOfMeasure { get; set; }
        /// <summary>The buyer's own line reference from their RFQ (SAP "00010", "OPT-29", …).
        /// Null on legacy lines — display falls back to a synthetic index.</summary>
        public string? CustomerLineRef { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal? Discount { get; set; }
        public decimal? TaxAmount { get; set; }
        public int? DeliveryLeadTime { get; set; }
        public long? DiscountTypeId { get; set; }
        public string? DiscountTypeName { get; set; }
        public decimal? DiscountValue { get; set; }

        // ---- What the customer actually asked for, read through the linked RFQ line ----
        //
        // These are NOT stored on QuoteItem. Rfqitem already carries every one of them and
        // QuoteItem.RfqitemId already points at it, so copying them onto the quote line would
        // duplicate governed data and create two versions of the buyer's request that can
        // drift. They are projected read-only instead.
        //
        // Why they must be visible: a sales engineer pricing a line was being shown quantity,
        // UoM and a description — and no manufacturer and no part number. On an industrial bid
        // list where "BALL VALVE 2IN" is a dozen different parts at a dozen different prices,
        // that is not enough information to quote, and the buyer's own requested date was
        // invisible too. Null on manually-created quote lines with no RFQ line behind them.

        /// <summary>Manufacturer named on the customer's RFQ line.</summary>
        public string? RequestedManufacturerName { get; set; }

        /// <summary>Manufacturer part number from the customer's RFQ line.</summary>
        public string? RequestedManufacturerPartNumber { get; set; }

        /// <summary>Customer's own material/item code (e.g. an SAP material number).</summary>
        public string? RequestedItemMaterialCode { get; set; }

        /// <summary>Alternate/substitute part number offered on the customer's RFQ line.</summary>
        public string? RequestedAlternatePartNumber { get; set; }

        /// <summary>Delivery date the CUSTOMER requested. Deliberately distinct from
        /// <see cref="DeliveryLeadTime"/>, which is what Nexora will COMMIT to and stays null
        /// until costing. Conflating the two would let a request masquerade as a promise.</summary>
        public DateTime? RequestedDeliveryDate { get; set; }

        /// <summary>Lead time in days stated on the customer's RFQ line, if any.</summary>
        public int? RequestedLeadTimeDays { get; set; }

        /// <summary>Currency stated on the customer's RFQ line. The quote's own currency is
        /// header-level and is a separate commercial decision.</summary>
        public string? RequestedCurrency { get; set; }
    }

    public class QuoteCreateRequestDTO
    {
        public string? QuoteNo { get; set; }
        public long? RfqId { get; set; }
        public long? CustomerId { get; set; }
        [Required]
        public long BusinessUnitId { get; set; }
        public DateTime? QuoteDate { get; set; }
        public DateTime? ValidUntil { get; set; }
        public long? StatusId { get; set; }
        public long? CurrencyId { get; set; }
        public decimal? TotalAmount { get; set; }
        public string? HeaderRemarks { get; set; }
        [Required]
        public string CreatedBy { get; set; } = null!;
        public long? DiscountTypeId { get; set; }
        public decimal? DiscountValue { get; set; }

        public List<QuoteItemCreateRequestDTO> QuoteItems { get; set; } = new List<QuoteItemCreateRequestDTO>();
    }

    public sealed class QuoteLifecycleTransitionRequestDTO
    {
        [Required]
        public string TargetStatusCode { get; set; } = null!;

        [Range(1, int.MaxValue)]
        public int ExpectedVersion { get; set; }

        public string? ReasonCode { get; set; }
        public string? ReasonNotes { get; set; }

        [Required]
        [StringLength(160)]
        public string IdempotencyKey { get; set; } = null!;

        [Required]
        [StringLength(100)]
        public string CorrelationId { get; set; } = null!;
    }

    public class QuoteItemCreateRequestDTO
    {
        public long? RfqItemId { get; set; }
        public long? ProductId { get; set; }
        public string? ItemDescription { get; set; }
        public string? UnitOfMeasure { get; set; }
        public string? CustomerLineRef { get; set; }
        [Required]
        [Range(double.Epsilon, double.MaxValue, ErrorMessage = "Quantity must be greater than zero.")]
        public decimal Quantity { get; set; }
        [Required]
        [Range(double.Epsilon, double.MaxValue, ErrorMessage = "Unit price must be greater than zero.")]
        public decimal UnitPrice { get; set; }
        [Required]
        public decimal TotalAmount { get; set; }
        public decimal? Discount { get; set; }
        [Range(0, double.MaxValue, ErrorMessage = "Tax cannot be negative.")]
        public decimal? TaxAmount { get; set; }
        public int? DeliveryLeadTime { get; set; }
        public long? DiscountTypeId { get; set; }
        public decimal? DiscountValue { get; set; }
    }

    public class QuoteUpdateRequestDTO
    {
        public long Id { get; set; }
        [Required]
        public string QuoteNo { get; set; } = null!;
        public long? CustomerId { get; set; }
        public DateTime? QuoteDate { get; set; }
        public DateTime? ValidUntil { get; set; }
        public long? StatusId { get; set; }
        public long? CurrencyId { get; set; }
        public decimal? TotalAmount { get; set; }
        public string? HeaderRemarks { get; set; }
        [Required]
        public string ModifiedBy { get; set; } = null!;
        public long? DiscountTypeId { get; set; }
        public decimal? DiscountValue { get; set; }

        public List<QuoteItemUpdateRequestDTO> QuoteItems { get; set; } = new List<QuoteItemUpdateRequestDTO>();
    }

    public class QuoteItemUpdateRequestDTO
    {
        public long? Id { get; set; } // Null for new items during update
        public long? RfqItemId { get; set; }
        public long? ProductId { get; set; }
        public string? ItemDescription { get; set; }
        // Null means "not supplied" — UpdateQuoteAsync preserves the stored value rather
        // than stripping the unit / buyer reference from an RFQ-born line.
        public string? UnitOfMeasure { get; set; }
        public string? CustomerLineRef { get; set; }
        // NOTE: no [Range] here — deleted items (IsDeleted=true) may carry placeholder values.
        // Non-positive quantity/price and negative tax are enforced server-side in
        // UpdateQuoteAsync for items that will remain on the quote (FIN-12).
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal? Discount { get; set; }
        public decimal? TaxAmount { get; set; }
        public int? DeliveryLeadTime { get; set; }
        public bool IsDeleted { get; set; } = false; // Flag to delete item
        public long? DiscountTypeId { get; set; }
        public decimal? DiscountValue { get; set; }
    }
}
