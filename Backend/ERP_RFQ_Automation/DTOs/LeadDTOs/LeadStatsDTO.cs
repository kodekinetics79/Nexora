namespace ERP_RFQ_Automation.DTOs.LeadDTOs
{
    public class LeadStatsDTO
    {
        public int TotalActiveLeads { get; set; }
        public int HighConfidenceLeads { get; set; }
        public int ClosingSoonLeads { get; set; }
        public int TotalLeadSources { get; set; }
    }
}

