namespace ERP_RFQ_Automation.DTOs.Lead
{
    // Row shape for the extraction "needs review" workbench list.
    public class LeadNeedsReviewItemDTO
    {
        public long Id { get; set; }
        public string? Rfqno { get; set; }
        public string? BuyersName { get; set; }
        public DateTime RecDate { get; set; }
        public DateTime? BidClosingDate { get; set; }
        public string LeadSource { get; set; } = null!;
        public decimal? Aiconfidence { get; set; }
        public int ItemCount { get; set; }
        public string? ReviewReason { get; set; }
        public DateTime? ReceivedOn { get; set; }
        public long ReviewVersion { get; set; }
    }
}
