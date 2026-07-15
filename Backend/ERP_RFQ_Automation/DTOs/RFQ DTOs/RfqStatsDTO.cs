namespace ERP_RFQ_Automation.DTOs.RfqDTOs
{
    public class RfqStatsDTO
    {
        public int TotalRfqs { get; set; }
        public int DraftRfqs { get; set; }
        public int SubmittedRfqs { get; set; }
        public int ClosingSoonRfqs { get; set; }
    }
}
