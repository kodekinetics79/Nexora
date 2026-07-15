using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.CustomerDTOs
{
    public class CustomerUpdateRequestDTO
    {
        [Required]
        public string Name { get; set; } = null!;
        public string? ContactEmail { get; set; }
        public string? ImageUrl { get; set; }
        public string? TaxId { get; set; }
        public decimal? CreditLimit { get; set; }
        public string? PaymentTerms { get; set; }
        public string? BillingAddressLine1 { get; set; }
        public string? BillingAddressLine2 { get; set; }
        public string? BillingCity { get; set; }
        public string? BillingState { get; set; }
        public string? BillingCountry { get; set; }
        public string? BillingPostalCode { get; set; }
        public string? ShippingAddressLine1 { get; set; }
        public string? ShippingAddressLine2 { get; set; }
        public string? ShippingCity { get; set; }
        public string? ShippingState { get; set; }
        public string? ShippingCountry { get; set; }
        public string? ShippingPostalCode { get; set; }
        public long? CurrencyId { get; set; }
        [Required]
        public long Buid { get; set; }
        public bool? IsActive { get; set; }
        [Required]
        public string ModifiedBy { get; set; } = null!;
        public IFormFile? ImageFile { get; set; }
    }
}
