using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.SupplierDTOs
{
    public class SupplierCreateRequestDTO
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

        /// <summary>
        /// The supplier's VAT/tax registration number. Optional — a supplier can be unregistered
        /// or foreign — but its absence blocks recoverable input-tax treatment on that supplier's
        /// lines (see <c>SupplierInputTaxRecoverabilityGuard</c>).
        /// </summary>
        [Display(Name = "Tax registration number")]
        [ERP_RFQ_Automation.Tax.TaxRegistrationNumber]
        public string? TaxRegistrationNumber { get; set; }
    }
}
