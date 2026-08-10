namespace ERP_RFQ_Automation.Models;

// The trading entity's own tax registration number. Kept in a partial so the scaffolded
// BusinessUnit.cs stays untouched; column configuration lives in
// Models/ErpRfqAutomationContext.TaxRegistration.cs (ConfigureTaxRegistrationModel).
public partial class BusinessUnit
{
    /// <summary>
    /// The VAT/tax registration number of the entity that trades under this business unit — the
    /// number that appears as "Seller VAT number" on its outgoing tax invoices and as the claimant
    /// on any input-tax reclaim.
    ///
    /// <para>Before this column the only <c>TaxNumber</c> in the system belonged to
    /// <c>Platform.Tenant</c> — the SaaS control-plane record of who pays Nexora's subscription.
    /// That is a different legal fact from "which registered entity issued this invoice and is
    /// claiming this input tax", and using one for the other would put the wrong VAT number on a
    /// ZATCA submission.</para>
    ///
    /// <para>Nullable so existing tenants are not blocked mid-configuration, and validated when
    /// present by <see cref="ERP_RFQ_Automation.Tax.TaxRegistrationNumbers"/>.</para>
    /// </summary>
    public string? TaxRegistrationNumber { get; set; }
}
