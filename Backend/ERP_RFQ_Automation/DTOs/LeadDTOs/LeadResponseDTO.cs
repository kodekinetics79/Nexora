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
        public string CustomerMatchStatus { get; set; } = "UNRESOLVED";
        public string? Rfqno { get; set; }
        public string? BuyersName { get; set; }
        public DateTime RecDate { get; set; }
        public DateTime? BidClosingDate { get; set; }
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
        public long EmailIngestsId { get; set; }
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

        public int ItemCount { get; set; } // Optimized: Item count instead of loading all items
        public List<LeadItemResponseDTO> LeadItems { get; set; } = new List<LeadItemResponseDTO>();
        public List<AttachmentResponseDTO> Attachments { get; set; } = new List<AttachmentResponseDTO>();
    }
}
