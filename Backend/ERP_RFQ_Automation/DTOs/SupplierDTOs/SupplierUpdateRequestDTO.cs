using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.SupplierDTOs
{
    public class SupplierUpdateRequestDTO
    {
        [Required]
        public string Name { get; set; } = null!;
        [EmailAddress]
        public string? ContactEmail { get; set; }


        public string? PaymentTerms { get; set; }
        public string? AddressLine1 { get; set; }
        public string? AddressLine2 { get; set; }
        public int? CityId { get; set; }
        public int? CountryId { get; set; }
        public string? PostalCode { get; set; }
        public string? Tags { get; set; }
        public string? Comments { get; set; }
        public long? CurrencyId { get; set; }
        public Guid? ConcurrencyToken { get; set; }
    }
}
