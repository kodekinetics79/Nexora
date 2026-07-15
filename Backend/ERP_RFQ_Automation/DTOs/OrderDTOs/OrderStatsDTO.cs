namespace ERP_RFQ_Automation.DTOs.OrderDTOs
{
    public class OrderStatsDTO
    {
        public int TotalOrders { get; set; }
        public int PendingOrders { get; set; }
        public int CompletedOrders { get; set; }
        public decimal TotalRevenue { get; set; }
    }
}
