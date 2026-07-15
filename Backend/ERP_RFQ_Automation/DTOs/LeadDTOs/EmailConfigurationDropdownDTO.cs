namespace ERP_RFQ_Automation.DTOs.LeadDTOs
{
    public class EmailConfigurationDropdownDTO
    {
        public long Id { get; set; }
        public long BusinessUnitId { get; set; }
        public string EmailAddress { get; set; } = null!;
    }
}
