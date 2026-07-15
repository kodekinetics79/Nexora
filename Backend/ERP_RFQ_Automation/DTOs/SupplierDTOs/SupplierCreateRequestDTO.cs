using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.SupplierDTOs
{
    public class SupplierCreateRequestDTO
    {
        [Required]
        public string Name { get; set; } = null!;
        public string? ContactEmail { get; set; }


        public string? PaymentTerms { get; set; }
        public string? AddressLine1 { get; set; }
        public string? AddressLine2 { get; set; }
        public int? CityId { get; set; }
        public int? CountryId { get; set; }
        public string? PostalCode { get; set; }
        public decimal? SuccessRate { get; set; }
        public int? AvgResponseTime { get; set; }
        public string? Tags { get; set; }
        public string? Comments { get; set; }
        public long? CurrencyId { get; set; }
        [Required]
        public long Buid { get; set; }
        public bool? IsActive { get; set; }
        [Required]
        public string CreatedBy { get; set; } = null!;
        public IFormFile? ImageFile { get; set; }
    }
}
