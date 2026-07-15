namespace ERP_RFQ_Automation.DTOs.UomDTOs
{
    public class UomResponseDTO
    {
        public int UomId { get; set; }
        public long BusinessUnitId { get; set; }
        public string UomCode { get; set; } = null!;
        public string UomName { get; set; } = null!;
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedDate { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
    }

    public class UomCreateDTO
    {
        public long BusinessUnitId { get; set; }
        public string UomCode { get; set; } = null!;
        public string UomName { get; set; } = null!;
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class UomUpdateDTO : UomCreateDTO
    {
    }
}
