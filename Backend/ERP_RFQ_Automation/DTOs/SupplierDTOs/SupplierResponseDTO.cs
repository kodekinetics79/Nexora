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
        /// <summary>
        /// The supplier's VAT/tax registration number, canonicalised. Null means "not captured",
        /// which is what makes this supplier's input tax non-recoverable.
        /// </summary>
        public string? TaxRegistrationNumber { get; set; }
        public long? Buid { get; set; }
        public string? BusinessUnitName { get; set; }
        public bool? IsActive { get; set; }
        /// <summary>
        /// AA-01 · raw jsonb bag of tenant-defined custom field values, keyed by stable key.
        /// Carried on the list payload so a custom-field column renders without a round trip
        /// per row.
        /// </summary>
        public string? CustomFields { get; set; }

        public string GovernanceStatus { get; set; } = null!;
        public string VerificationStatus { get; set; } = null!;
        public string ComplianceStatus { get; set; } = null!;
        public string RiskStatus { get; set; } = null!;
        public string ReadinessStatus { get; set; } = null!;
        public DateTime? EffectiveFrom { get; set; }
        public string? GovernanceReviewedBy { get; set; }
        public DateTime? GovernanceReviewedOn { get; set; }
        public Guid? ConcurrencyToken { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
        public string? DocId { get; set; }
    }
}
