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

        /// <summary>
        /// The supplier's VAT/tax registration number. Optional — a supplier can be unregistered
        /// or foreign — but its absence blocks recoverable input-tax treatment on that supplier's
        /// lines (see <c>SupplierInputTaxRecoverabilityGuard</c>).
        /// </summary>
        [Display(Name = "Tax registration number")]
        [ERP_RFQ_Automation.Tax.TaxRegistrationNumber]
        public string? TaxRegistrationNumber { get; set; }

        /// <summary>
        /// The customer's commercial classification of this supplier. Optional — blank means "not
        /// yet classified", which every existing supplier legitimately is. Any other value must be
        /// one of <c>SupplierTiers</c>; an unrecognised one is refused rather than stored, because a
        /// tier the customer did not choose is worse than no tier at all.
        /// </summary>
        [Display(Name = "Tier")]
        // Refuses an oversized value at model binding, before any validation or canonicalisation
        // runs on it. The column is 32 characters; this is the request-side bound that keeps a
        // multi-megabyte form field from ever reaching the canonicaliser.
        [StringLength(SupplierTierInput.MaximumCanonicalisableLength)]
        [SupplierTier]
        public string? Tier { get; set; }

        /// <summary>
        /// Days of credit this supplier extends. Optional — null means NOT CONFIGURED, and 0 is the
        /// positive assertion "cash on delivery". Negative credit is not a thing.
        /// </summary>
        [Display(Name = "Credit days")]
        [Range(0, int.MaxValue, ErrorMessage = "Credit days cannot be negative.")]
        public int? CreditDays { get; set; }
    }
}
