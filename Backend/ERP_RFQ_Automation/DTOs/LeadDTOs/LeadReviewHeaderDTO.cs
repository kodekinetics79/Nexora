namespace ERP_RFQ_Automation.DTOs.Lead
{
    // Header fields a reviewer may edit while correcting a low-confidence extraction.
    // Only non-null fields are applied to the lead on save/approve.
    public class LeadReviewHeaderDTO
    {
        public string? Rfqno { get; set; }
        public string? BuyersName { get; set; }
        public DateTime? BidClosingDate { get; set; }

        /// <summary>
        /// FR-RFQ-04. The date the BUYER asked for delivery on — never a supplier lead
        /// time. Correctable here because extraction is the only thing that has ever
        /// written it, and an extraction the reviewer cannot correct is an extraction
        /// nobody can trust.
        /// </summary>
        public DateTime? RequiredDeliveryDate { get; set; }
        public string? DeliveryLocation { get; set; }
        public string? AgreementReference { get; set; }

        public string? OpportunityNo { get; set; }
        public string? HeaderRemarks { get; set; }
        public long? CustomerId { get; set; }
        public long? ContactId { get; set; }
    }
}
