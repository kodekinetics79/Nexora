namespace ERP_RFQ_Automation.DTOs.LocationDTOs
{
    public class CityResponseDTO
    {
        public int CityId { get; set; }
        public string CityName { get; set; } = null!;
        public int StateId { get; set; }
        public string? StateName { get; set; }
        public int CountryId { get; set; }
        public string? CountryName { get; set; }
        public long Buid { get; set; }
        public string? Description { get; set; }
        public bool IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedDate { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedDate { get; set; }
    }

    public class CityCreateDTO
    {
        public string CityName { get; set; } = null!;
        public int StateId { get; set; }
        public int CountryId { get; set; }
        public long Buid { get; set; }
        public string? Description { get; set; }
        public bool IsActive { get; set; } = true;
    }

    public class CityUpdateDTO : CityCreateDTO
    {
    }
}
