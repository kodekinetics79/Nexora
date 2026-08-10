using System;

namespace ERP_RFQ_Automation.DTOs.QuoteDTOs
{
    /// <summary>
    /// Caller-supplied context for sending a quote email (WP-B3). The requester
    /// identity comes from the authenticated caller and is stamped onto any
    /// below-floor hold; <see cref="BypassFloorHold"/> is ONLY set by the
    /// approve_below_floor_quote tool when completing an already-approved send —
    /// it must never be reachable from client input.
    /// </summary>
    public sealed class QuoteSendOptions
    {
        /// <summary>Skip the below-floor check (approved-hold execution path only).</summary>
        public bool BypassFloorHold { get; init; }

        public long? RequestedByUserId { get; init; }
        public string? RequestedBy { get; init; }
    }

    /// <summary>
    /// Outcome of a quote-send attempt: either the email went out, or the send
    /// was parked as a below-floor approval (WP-B3) and nothing was emailed.
    /// </summary>
    public sealed class QuoteSendResult
    {
        /// <summary>true when the send was held for approval instead of being performed.</summary>
        public bool Held { get; init; }

        /// <summary>The pending AgentApproval id when <see cref="Held"/>.</summary>
        public Guid? ApprovalId { get; init; }

        /// <summary>Plain-language hold summary ("Quote #Q-1042: 3 line(s) below floor by up to 12%").</summary>
        public string? HoldSummary { get; init; }
        /// <summary>
        /// R5: true when nothing was sent because the quote's current prices are not covered
        /// by a price-provenance attestation — either none was ever recorded, or a price
        /// changed after the last one. The rep must confirm the price source again.
        /// </summary>
        public bool BlockedPendingPriceAttestation { get; init; }

        /// <summary>Plain-language reason when <see cref="BlockedPendingPriceAttestation"/>.</summary>
        public string? PriceAttestationReason { get; init; }

        /// <summary>
        /// R17: true when nothing was sent because a line's OUTPUT TAX was never derived — the
        /// business unit has no output tax rate configured, or the line has not been priced, or a
        /// non-standard tax category carries no stated reason.
        ///
        /// <para>This fails closed on purpose. A quotation that leaves the building with no VAT
        /// separately stated is DEEMED VAT-inclusive under KSA law, so the seller silently funds
        /// 15/115 of it out of margin. There is no version of that which is better than the rep
        /// seeing an error.</para>
        /// </summary>
        public bool BlockedPendingTaxDerivation { get; init; }

        /// <summary>Plain-language reason when <see cref="BlockedPendingTaxDerivation"/>.</summary>
        public string? TaxDerivationReason { get; init; }

        public bool QueuedForDelivery { get; init; }
        public bool Delivered { get; init; }
        public bool Replayed { get; init; }
        public bool FailedPermanently { get; init; }
        public string? FailureCode { get; init; }

        public static QuoteSendResult Queued(bool delivered, bool replayed) => new()
        {
            QueuedForDelivery = !delivered,
            Delivered = delivered,
            Replayed = replayed
        };

        public static QuoteSendResult Failed(string failureCode) => new()
        {
            FailedPermanently = true,
            FailureCode = failureCode
        };

        public static QuoteSendResult HeldForApproval(Guid approvalId, string? summary) =>
            new() { Held = true, ApprovalId = approvalId, HoldSummary = summary };

        public static QuoteSendResult AwaitingPriceAttestation(string reason) =>
            new() { BlockedPendingPriceAttestation = true, PriceAttestationReason = reason };

        public static QuoteSendResult AwaitingTaxDerivation(string reason) =>
            new() { BlockedPendingTaxDerivation = true, TaxDerivationReason = reason };
    }

    /// <summary>What the rep submits when confirming where a quote's prices came from (R5).</summary>
    public sealed class QuotePriceAttestationRequest
    {
        /// <summary>SALES_MANAGER or SUPPLIER_QUOTE.</summary>
        public string Source { get; set; } = string.Empty;

        /// <summary>The supplier quote reference, or the manager's name.</summary>
        public string SourceReference { get; set; } = string.Empty;
    }

    /// <summary>One line price inside an attestation view.</summary>
    public sealed class QuotePriceAttestationLineDTO
    {
        public long QuoteItemId { get; set; }
        public long? RfqItemId { get; set; }
        public string? ItemDescription { get; set; }
        public decimal Quantity { get; set; }
        public decimal UnitPrice { get; set; }
    }

    /// <summary>
    /// Whether this quote may be sent, what was last confirmed, and the prices a fresh
    /// confirmation would cover. Drives the confirm-before-send dialog.
    /// </summary>
    public sealed class QuotePriceAttestationStatusDTO
    {
        public long QuoteId { get; set; }

        /// <summary>true when the current prices are covered by a recorded confirmation.</summary>
        public bool Satisfied { get; set; }

        /// <summary>Why the send would be refused; null when satisfied.</summary>
        public string? Reason { get; set; }

        /// <summary>Set when a confirmation exists, even if a later price change voided it.</summary>
        public string? Source { get; set; }
        public string? SourceReference { get; set; }
        public string? ConfirmedBy { get; set; }
        public DateTime? ConfirmedOn { get; set; }

        /// <summary>true when a confirmation exists but no longer matches the current prices.</summary>
        public bool SupersededByPriceChange { get; set; }

        public long? CurrencyId { get; set; }
        public string? CurrencyCode { get; set; }

        /// <summary>The quote's prices as they stand now.</summary>
        public List<QuotePriceAttestationLineDTO> CurrentLines { get; set; } = new();

        /// <summary>The prices recorded by the last confirmation (empty when there is none).</summary>
        public List<QuotePriceAttestationLineDTO> AttestedLines { get; set; } = new();
    }

    /// <summary>Revision-chain facts for one quote (revisions-lite, WP-B4).</summary>
    public sealed class QuoteRevisionInfoDTO
    {
        public long QuoteId { get; set; }
        public string QuoteNo { get; set; } = string.Empty;

        /// <summary>1 = original, 2 = first revision, …</summary>
        public int RevisionNo { get; set; }

        /// <summary>The quote this one replaces (null on the original).</summary>
        public long? RevisionOfQuoteId { get; set; }
        public string? RevisionOfQuoteNo { get; set; }

        /// <summary>The newer revision that replaces this quote, when one exists.</summary>
        public long? SupersededByQuoteId { get; set; }
        public string? SupersededByQuoteNo { get; set; }

        /// <summary>true once any revision in the chain has a recorded outcome — the chain is closed.</summary>
        public bool ChainLocked { get; set; }

        /// <summary>true when a new revision may be created from THIS quote (non-draft, latest, chain open).</summary>
        public bool CanRevise { get; set; }
    }
}
