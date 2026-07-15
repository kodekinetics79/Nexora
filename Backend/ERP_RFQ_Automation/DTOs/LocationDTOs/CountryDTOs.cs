namespace ERP_RFQ_Automation.DTOs.LocationDTOs
{
    public class CountryResponseDTO
    {
        public int CountryId { get; set; }
        public string CountryCode { get; set; } = null!;
        public string CountryName { get; set; } = null!;
        public string? Description { get; set; }
        public long Buid { get; set; }
        public bool IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedDate { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
    }

    public class CountryCreateDTO
    {
        public string CountryCode { get; set; } = null!;
        public string CountryName { get; set; } = null!;
        public string? Description { get; set; }
        public long Buid { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class CountryUpdateDTO : CountryCreateDTO
    {
    }
}
