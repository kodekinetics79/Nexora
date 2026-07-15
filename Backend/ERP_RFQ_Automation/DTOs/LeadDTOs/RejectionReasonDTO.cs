namespace ERP_RFQ_Automation.DTOs.LeadDTOs
{
    public class RejectionReasonDTO
    {
        public long Id { get; set; }
        public string Reason { get; set; } = null!;
        public string? Description { get; set; }
    }
}
