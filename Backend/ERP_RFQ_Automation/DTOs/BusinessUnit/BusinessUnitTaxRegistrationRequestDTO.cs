using System.ComponentModel.DataAnnotations;

namespace ERP_RFQ_Automation.DTOs.BusinessUnit
{
    /// <summary>
    /// The only field of a business unit a tenant identity may change.
    ///
    /// <para>Provisioning a business unit stays with the platform control plane (see
    /// <c>BusinessUnitController.Create</c> / <c>Update</c>, both of which forbid tenant callers).
    /// A VAT registration number is not provisioning: it is a statutory identifier of the entity
    /// already trading here, it changes when the entity registers or re-registers, and the entity
    /// itself is the only party that knows it. Without a way to state it, the business unit cannot
    /// name itself as the claimant on the input tax it is already deducting from landed cost.</para>
    /// </summary>
    public class BusinessUnitTaxRegistrationRequestDTO
    {
        /// <summary>
        /// The registration number, or null/empty to clear it. Validated when present; a Saudi
        /// claim (all digits, leading 3) must be a well-formed 15-digit KSA VAT number.
        /// </summary>
        [Display(Name = "Tax registration number")]
        [ERP_RFQ_Automation.Tax.TaxRegistrationNumber]
        public string? TaxRegistrationNumber { get; set; }
    }
}
