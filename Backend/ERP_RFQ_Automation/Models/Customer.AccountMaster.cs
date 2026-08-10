namespace ERP_RFQ_Automation.Models;

// FR-CST-01 — the customer master fields the BRD names, and FR-CST-02's account team.
//
// Kept in a partial so the scaffolded Customer.cs stays untouched; column configuration lives in
// Models/ErpRfqAutomationContext.CustomerMaster.cs (ApplyCustomerMasterModel) and the migration
// owner generates the DDL from it. Every field below is covered by the master-data audit
// interceptor without any change to it: MasterDataEntityDescriptor derives the audited column list
// from EF metadata rather than a hand-written include-list, so a column added here is audited the
// day it is added.
public partial class Customer
{
    /// <summary>
    /// The customer's commercial registration ("CR") number, canonicalised by
    /// <see cref="ERP_RFQ_Automation.MasterData.CommercialRegistrationNumbers.Normalize"/>.
    ///
    /// <para>Nullable. Null means NOT CAPTURED and is rendered as a visible gap, never as a blank
    /// that reads like a loading state. It is never an empty string.</para>
    ///
    /// <para>A KSA commercial registration is exactly 10 digits. A foreign customer has none, so
    /// the Saudi rule is applied only to a value that CLAIMS to be Saudi — see the validator for
    /// the claim test and why a foreign registration must carry its country prefix.</para>
    /// </summary>
    public string? CommercialRegistrationNumber { get; set; }

    /// <summary>
    /// The customer's VAT registration number, canonicalised and validated by the SAME
    /// <see cref="ERP_RFQ_Automation.Tax.TaxRegistrationNumbers"/> definition that already governs
    /// <see cref="Supplier.TaxRegistrationNumber"/> and <see cref="BusinessUnit.TaxRegistrationNumber"/>.
    /// There is deliberately no second definition of what a VAT number is in this platform.
    ///
    /// <para>Null is a real answer — a customer can be below the VAT threshold or foreign — and it
    /// has a consequence downstream: a ZATCA-compliant tax invoice to a registered buyer must carry
    /// the buyer's VAT number, so an invoice raised against a null here cannot claim to be a B2B
    /// tax invoice. That consumer is recorded as owing this field rather than silently defaulting.</para>
    /// </summary>
    public string? TaxRegistrationNumber { get; set; }

    /// <summary>
    /// Government / Semi-Government / Private, from the closed list in
    /// <see cref="ERP_RFQ_Automation.MasterData.CustomerSectors"/>. Stored as the canonical code,
    /// not the display label, so a renamed label cannot orphan stored rows.
    ///
    /// <para>Null means NOT CLASSIFIED. It is not defaulted to <c>PRIVATE</c>: a customer whose
    /// sector nobody has stated is a different thing from a customer somebody classified as
    /// private, and the difference decides whether government procurement rules apply.</para>
    /// </summary>
    public string? Sector { get; set; }

    /// <summary>
    /// The customer's region, as a foreign key into the tenant's OWN region master
    /// (<see cref="SetState"/>) — the same governed list <c>RoutingScopeKeys.Territory</c> already
    /// resolves sales territory against, and the one Gate 7's delivery addresses attach to.
    ///
    /// <para>It is a key and not a string on purpose: the free-typed <see cref="BillingState"/> /
    /// <see cref="ShippingState"/> columns already exist and are exactly why routing has to resolve
    /// wording against aliases at read time. This column states the region once, governed.</para>
    ///
    /// <para>Null means NOT STATED, and the routing derivation continues to fall back to the stated
    /// address wording — it is not treated as "no region".</para>
    /// </summary>
    public int? RegionStateId { get; set; }

    /// <summary>
    /// FR-CST-02 — the account team this customer belongs to, as a relationship into
    /// <see cref="Models.Team"/> rather than a string.
    ///
    /// <para>It is a key because the question the requirement actually asks is "is this customer
    /// mine", and that has to be answerable inside a SQL predicate, not by a string comparison in
    /// application memory. Membership is read from <c>SalesTeamMembership</c> (users to teams,
    /// effective-dated); this column supplies the missing customers-to-teams edge, so the join
    /// customer → team → membership → user closes and
    /// <see cref="ERP_RFQ_Automation.Authorization.AccountTeamScopeResolver"/> can push the whole
    /// thing into the database.</para>
    ///
    /// <para><b>Null means NO ACCOUNT TEAM IS ASSIGNED, and that is not the same as "restricted to
    /// nobody".</b> A customer with no assigned account team is not restricted by account team,
    /// because there is no team to restrict it to; it stays visible to every caller who holds the
    /// Customers permission, exactly as it was before this column existed, and it is rendered as a
    /// visible gap ("No account team") rather than a blank. Assigning a team is the act that
    /// NARROWS access. The alternative — treating null as "nobody may see it" — would make every
    /// pre-existing customer disappear the moment the column shipped.</para>
    /// </summary>
    public long? AccountTeamId { get; set; }

    /// <summary>The assigned account team. Restricted delete: a team that owns accounts must be
    /// reassigned before it can be removed, rather than silently orphaning the accounts.</summary>
    public virtual Team? AccountTeam { get; set; }

    /// <summary>The governed region row. See <see cref="RegionStateId"/>.</summary>
    public virtual SetState? RegionState { get; set; }
}
