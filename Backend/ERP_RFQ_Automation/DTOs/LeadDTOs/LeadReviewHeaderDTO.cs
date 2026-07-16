namespace ERP_RFQ_Automation.DTOs.Lead
{
    // Header fields a reviewer may edit while correcting a low-confidence extraction.
    // Only non-null fields are applied to the lead on save/approve.
    public class LeadReviewHeaderDTO
    {
        public string? Rfqno { get; set; }
        public string? BuyersName { get; set; }
        public DateTime? BidClosingDate { get; set; }
        public string? OpportunityNo { get; set; }
        public string? HeaderRemarks { get; set; }
    }
}
