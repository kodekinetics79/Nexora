using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.CustomerDTOs
{
    public class CustomerCreateRequestDTO
    {
        [Required, StringLength(255), RegularExpression(@".*\S.*", ErrorMessage = "Name cannot be blank.")]
        public string Name { get; set; } = null!;
        [EmailAddress, StringLength(320)]
        public string? ContactEmail { get; set; }
        [StringLength(100)]
        public string? ImageUrl { get; set; }
        [StringLength(255)]
        public string? BillingAddressLine1 { get; set; }
        [StringLength(255)]
        public string? BillingAddressLine2 { get; set; }
        [StringLength(100)]
        public string? BillingCity { get; set; }
        [StringLength(100)]
        public string? BillingState { get; set; }
        [StringLength(100)]
        public string? BillingCountry { get; set; }
        [StringLength(20)]
        public string? BillingPostalCode { get; set; }
        [StringLength(255)]
        public string? ShippingAddressLine1 { get; set; }
        [StringLength(255)]
        public string? ShippingAddressLine2 { get; set; }
        [StringLength(100)]
        public string? ShippingCity { get; set; }
        [StringLength(100)]
        public string? ShippingState { get; set; }
        [StringLength(100)]
        public string? ShippingCountry { get; set; }
        [StringLength(20)]
        public string? ShippingPostalCode { get; set; }

        // ── FR-CST-01 customer master ────────────────────────────────────────
        /// <summary>KSA commercial registration; 10 digits, or a foreign registration carrying its
        /// country prefix. Empty means NOT CAPTURED and is stored as NULL, never "".</summary>
        [MasterData.CommercialRegistrationNumber, StringLength(30)]
        public string? CommercialRegistrationNumber { get; set; }

        /// <summary>VAT registration number, validated by the SAME rule as the supplier and
        /// business-unit fields (ERP_RFQ_Automation.Tax.TaxRegistrationNumbers).</summary>
        [Tax.TaxRegistrationNumber, StringLength(50)]
        public string? TaxRegistrationNumber { get; set; }

        /// <summary>GOVERNMENT | SEMI_GOVERNMENT | PRIVATE. Empty means NOT CLASSIFIED — it is not
        /// defaulted to PRIVATE.</summary>
        [MasterData.CustomerSector, StringLength(20)]
        public string? Sector { get; set; }

        /// <summary>Region, as a key into the tenant's own region master (SetState) rather than a
        /// typed string — the same list routing resolves sales territory against.</summary>
        public int? RegionStateId { get; set; }

        /// <summary>FR-CST-02 — the account team that owns this customer. Empty means NO ACCOUNT
        /// TEAM, which leaves the record readable tenant-wide; assigning a team is what narrows it.</summary>
        public long? AccountTeamId { get; set; }

        public bool? IsActive { get; set; }
        public IFormFile? ImageFile { get; set; }
    }
}
