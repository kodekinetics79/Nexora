namespace ERP_RFQ_Automation.DTOs.QuoteDTOs
{
    public class QuoteStatsDTO
    {
        public int TotalQuotes { get; set; }
        public int AcceptedQuotes { get; set; }
        public int PendingQuotes { get; set; }
        public int ExpiredQuotes { get; set; }
        public decimal TotalQuotedAmount { get; set; }
    }
}
