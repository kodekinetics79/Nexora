using ERP_RFQ_Automation.DTOs.LeadDTOs;
namespace ERP_RFQ_Automation.DTOs.Lead
{
    public class LeadResponseDTO
    {
        public long Id { get; set; }
        public long? CommercialCaseId { get; set; }
        public string? CommercialCaseReference { get; set; }
        public string? NexoraSerial => CommercialCaseReference;
        public long? CustomerId { get; set; }
        public long? ContactId { get; set; }
        public string? CustomerName { get; set; }
        public string? AccountOwnerName { get; set; }

        // ── Client organisation identity ────────────────────────────────────
        // UNRESOLVED | SUGGESTED | AMBIGUOUS | AUTO_MATCHED |
        // AUTO_MATCHED_CONTACT_UNRESOLVED | CONFIRMED |
        // CUSTOMER_CONFIRMED_CONTACT_UNRESOLVED | VERIFIED_EMAIL
        public string CustomerMatchStatus { get; set; } = "UNRESOLVED";

        /// <summary>Why the machine decided what it decided; one of CustomerMatchReasonCodes.</summary>
        public string? CustomerMatchReasonCode { get; set; }

        /// <summary>Strength of that signal, 0..1.</summary>
        public decimal? CustomerMatchConfidence { get; set; }

        /// <summary>One-line human-readable basis ("Shares the corporate sender domain se.com.sa").</summary>
        public string? CustomerMatchExplanation { get; set; }

        // Raw evidence, so an unresolved lead is a five-second decision instead of a dead end.
        public string? CustomerCompanyNameExtracted { get; set; }
        public string? CustomerCompanyEvidence { get; set; }
        public string? CustomerCompanyRegistrationId { get; set; }
        public string? CustomerBuyerEmailExtracted { get; set; }
        public string? CustomerPortalNameExtracted { get; set; }
        public string? SupplierNameOnDocument { get; set; }
        public string? SupplierAccountRefOnDocument { get; set; }

        /// <summary>Ranked machine proposals: up to 3 in the list projection, up to 5 on detail.</summary>
        public List<ClientCandidateDTO> ClientCandidates { get; set; } = new();
        public string? Rfqno { get; set; }
        public string? BuyersName { get; set; }
        public DateTime RecDate { get; set; }
        public DateTime? BidClosingDate { get; set; }

        // ── FR-RFQ-03 / FR-RFQ-04 intake fields ─────────────────────────────
        // These three were captured by extraction and read by nothing: no DTO, no
        // projection, no screen. A trader quoting a lead time could not see the date
        // the buyer asked for, and the Hijri closing date a Saudi tender publishes was
        // stored where no cross-check could reach it.

        /// <summary>
        /// FR-RFQ-04. The delivery date the BUYER asked for. Deliberately distinct from
        /// <see cref="BidClosingDate"/> (when the bid must be submitted) and from any
        /// supplier lead time (what we can promise). Null means the document did not
        /// state one — rendered as a visible gap, never as a blank.
        /// </summary>
        public DateTime? RequiredDeliveryDate { get; set; }

        /// <summary>
        /// FR-RFQ-04. <see cref="BidClosingDate"/> rendered in the Umm al-Qura calendar,
        /// alongside the Gregorian date rather than instead of it. A Hijri deadline read
        /// as a Gregorian one loses the bid, which is why this is a data-correctness
        /// cross-check rather than part of the deferred Arabic interface (decision R6).
        /// </summary>
        public string? BidClosingDateHijri { get; set; }

        /// <summary>
        /// FR-RFQ-03. The standing agreement / frame contract this inquiry is called off
        /// against. Distinct from <see cref="Rfqno"/> (the inquiry's own reference) and
        /// from <see cref="DurationAgreement"/> (free-text description, not an identifier).
        /// </summary>
        public string? AgreementReference { get; set; }

        public string? BiddingDecision { get; set; }
        public DateTime? AcknowledgmentDate { get; set; }
        public DateTime? SubDate { get; set; }
        public string? HeaderRemarks { get; set; }
        public string? OpportunityNo { get; set; }
        public int? NoOfLineItems { get; set; }
        public string? Rfqtype { get; set; }
        public string? DurationAgreement { get; set; }
        public string LeadSource { get; set; } = null!;
        public decimal? Aiconfidence { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedDate { get; set; }
        public long BusinessUnitId { get; set; }
        public string? BusinessUnitName { get; set; }
        public long? EmailIngestsId { get; set; }
        public DateTime? ModifiedDate { get; set; }
        public string? EmailSource { get; set; }
        public string? Clientemail { get; set; }
        public long? LeadStatusId { get; set; }
        public int LifecycleVersion { get; set; }
        public bool IsAccepted => LeadStatusId == 24;
        public bool IsRejected => LeadStatusId == 25;
        public long ReviewVersion { get; set; }
        public bool RequiresCommercialReview { get; set; }
        public bool CommercialFactsVerified { get; set; }
        public int CurrentRevisionNumber { get; set; }
        public DateTimeOffset? IngestedAtUtc { get; set; }

        // ── Ingestion audit (owner requirement: audit fairness) ──────────────
        // When the lead actually ENTERED Nexora: the earliest received_on across
        // its source-document occurrences, falling back to CreatedDate for
        // manual/legacy leads without occurrence lineage. Distinct from the
        // business dates (RecDate / BidClosingDate / SubDate) and from
        // IngestedAtUtc (the identity-pipeline processing timestamp).
        public DateTimeOffset? IngestedOn { get; set; }
        // Server-computed: IngestedOn is strictly after the business due date
        // (BidClosingDate, falling back to SubDate; sentinel dates < year 2000
        // are "no deadline"). Late-ingested leads are excluded from Nexora's
        // response-time / aging performance metrics — the flag makes that
        // exclusion auditable instead of silent.
        public bool LateIngested { get; set; }

        // WP-BOQ foundation: "product" | "service" | "mixed" | null (unclassified).
        public string? InquiryType { get; set; }
        // Distinct list badge for service-scope leads (service or mixed inquiries).
        public bool IsServiceInquiry => InquiryType is "service" or "mixed";

        // WP-A3 duplicate flag: null | "suspected" | "confirmed" | "not_duplicate".
        // Conversion is blocked while suspected/confirmed.
        public string? DuplicateStatus { get; set; }
        public long? DuplicateOfLeadId { get; set; }
        public string? DuplicateResolvedBy { get; set; }

        // Assignment Info
        public long? AssignedToId { get; set; }
        public string? AssignedToFullName { get; set; }
        public DateTime? AssignedOn { get; set; }
        public string? AssignComment { get; set; }
        public string? AssignmentReason => AssignComment;

        public int ItemCount { get; set; } // Optimized: Item count instead of loading all items
        public List<LeadItemResponseDTO> LeadItems { get; set; } = new List<LeadItemResponseDTO>();
        public List<AttachmentResponseDTO> Attachments { get; set; } = new List<AttachmentResponseDTO>();
    }

    /// <summary>
    /// One ranked machine proposal for a lead's client organisation, with the reason a rep
    /// can read. Never a link — a candidate is what the machine THINKS, and the customer on
    /// the lead stays null until a signal is strong enough or a person confirms.
    /// </summary>
    public class ClientCandidateDTO
    {
        /// <summary>1 = strongest.</summary>
        public int Rank { get; set; }
        public long CustomerId { get; set; }
        public string CustomerName { get; set; } = string.Empty;
        /// <summary>0..1.</summary>
        public decimal Confidence { get; set; }
        /// <summary>One of CustomerMatchReasonCodes.</summary>
        public string ReasonCode { get; set; } = string.Empty;
        /// <summary>"shares sender domain se.com.sa", "same portal vendor code 2004414".</summary>
        public string Explanation { get; set; } = string.Empty;
    }
}
