namespace ERP_RFQ_Automation.Models;

// The supplier's tax registration number. Kept in a partial so the scaffolded Supplier.cs stays
// untouched; column configuration lives in Models/ErpRfqAutomationContext.TaxRegistration.cs
// (ConfigureTaxRegistrationModel) and the integration owner generates the migration from it.
public partial class Supplier
{
    /// <summary>
    /// The supplier's VAT/tax registration number, canonicalised by
    /// <see cref="ERP_RFQ_Automation.Tax.TaxRegistrationNumbers.Normalize"/>.
    ///
    /// <para>Nullable, because a supplier can legitimately be unregistered (below the VAT
    /// threshold) or foreign. Null is a real answer, not missing data — but it is an answer with
    /// a consequence: no input tax charged by this supplier may be treated as recoverable, because
    /// a reclaim we cannot attach to a registered counterparty is the position ZATCA disallows.
    /// That refusal is enforced by
    /// <c>OrderToCash.SupplierInputTaxRecoverabilityGuard.EnsureSupplierInputTaxIsClaimableAsync</c>.</para>
    ///
    /// <para>This field records what the supplier told us. It is NOT a supplier tax invoice, and
    /// it does not by itself substantiate a reclaim — see the guard's remarks.</para>
    /// </summary>
    public string? TaxRegistrationNumber { get; set; }
}
