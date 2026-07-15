namespace ERP_RFQ_Automation.DTOs.CustomerDTOs
{
    public class CustomerResponseDTO
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public string? ContactEmail { get; set; }
        public string ImageUrl { get; set; } = null!;
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
        public string? CurrencyName { get; set; }
        public long? Buid { get; set; }
        public string? BusinessUnitName { get; set; }
        public bool? IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }

        public string? DocId { get; set; }
    }
}
