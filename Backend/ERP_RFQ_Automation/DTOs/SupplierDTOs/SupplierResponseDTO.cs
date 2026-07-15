namespace ERP_RFQ_Automation.DTOs.SupplierDTOs
{
    public class SupplierResponseDTO
    {
        public long Id { get; set; }
        public string Name { get; set; } = null!;
        public string? ContactEmail { get; set; }
        public string ImageUrl { get; set; } = null!;

        public string? PaymentTerms { get; set; }
        public string? AddressLine1 { get; set; }
        public string? AddressLine2 { get; set; }
        public int? CityId { get; set; }
        public string? CityName { get; set; }
        public int? CountryId { get; set; }
        public string? CountryName { get; set; }
        public string? PostalCode { get; set; }
        public decimal? SuccessRate { get; set; }
        public int? AvgResponseTime { get; set; }
        public string? Tags { get; set; }
        public string? Comments { get; set; }
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
