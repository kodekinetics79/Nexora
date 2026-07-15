namespace ERP_RFQ_Automation.DTOs.BusinessUnit
{
    public class BusinessUnitResponseDTO
    {
        public long Id { get; set; }
        public string BusinessUnitCode { get; set; } = null!;
        public string BusinessUnitName { get; set; } = null!;
        public string? Description { get; set; }
        public bool? IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }

}
