namespace ERP_RFQ_Automation.DTOs.BusinessUnit
{
    public class BusinessUnitResponseDTO
    {
        public long Id { get; set; }
        public string BusinessUnitCode { get; set; } = null!;
        public string BusinessUnitName { get; set; } = null!;
        public string? Description { get; set; }
        /// <summary>
        /// The VAT/tax registration number of the entity trading under this business unit — the
        /// claimant on any input-tax reclaim. Distinct from the SaaS control-plane
        /// <c>Platform.Tenant.TaxNumber</c>, which identifies who pays for Nexora.
        /// </summary>
        public string? TaxRegistrationNumber { get; set; }
        public bool? IsActive { get; set; }
        public string CreatedBy { get; set; } = null!;
        public DateTime CreatedOn { get; set; }
        public string? ModifiedBy { get; set; }
        public DateTime? ModifiedOn { get; set; }
    }

}
