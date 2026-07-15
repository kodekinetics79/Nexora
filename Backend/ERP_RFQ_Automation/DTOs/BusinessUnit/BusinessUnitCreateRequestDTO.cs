namespace ERP_RFQ_Automation.DTOs.BusinessUnit
{
    public class BusinessUnitCreateRequestDTO
    {
        public string BusinessUnitCode { get; set; } = null!;
        public string BusinessUnitName { get; set; } = null!;
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
        public string? CreatedBy { get; set; }
    }

}
