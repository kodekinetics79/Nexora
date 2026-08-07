namespace ERP_RFQ_Automation.Platform.Models;

/// <summary>
/// A customer organization — the first-class isolation + billing + lifecycle
/// boundary that sits ABOVE <c>BusinessUnit</c>. Lives in the (non-RLS'd)
/// platform schema. Provisioning creates the Tenant plus its primary
/// BusinessUnit transactionally. (ADR-0005 §1, §4)
///
/// <para><b>Why this record grew.</b> It used to be Name + Slug + PlanId, which made tenant
/// creation the weakest link in the whole product: it is the FIRST step of every customer
/// journey and everything downstream reads from it. A tenant with no legal identity cannot
/// print a compliant quote; with no base currency or UOM its commercial screens open empty;
/// with no plan and no rate card it consumes the platform for free forever. Each block below
/// exists because something concrete downstream was starved without it.</para>
/// </summary>
public class Tenant
{
    public long Id { get; set; }

    /// <summary>Trading / display name. What the operator and the product call this customer.</summary>
    public string Name { get; set; } = null!;

    /// <summary>URL/subdomain-safe identifier. Unique (see WIRING.md index).</summary>
    public string Slug { get; set; } = null!;

    public TenantStatus Status { get; set; } = TenantStatus.Provisioning;

    /// <summary>FK to <see cref="Plan"/>. Nullable until a plan is assigned.</summary>
    public long? PlanId { get; set; }

    /// <summary>
    /// The primary intra-tenant division created during provisioning. Bridges the
    /// new Tenant to the existing <c>BusinessUnit</c> scope until the Phase 0
    /// TenantId backfill lands. (ADR-0005 §1)
    /// </summary>
    public long? PrimaryBusinessUnitId { get; set; }

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;

    public string? CreatedBy { get; set; }

    public DateTime? ModifiedOn { get; set; }

    public string? ModifiedBy { get; set; }

    /// <summary>Reason captured on the last suspend/resume/archive action.</summary>
    public string? StatusReason { get; set; }

    public Plan? Plan { get; set; }

    // ==== Legal identity =====================================================================
    // Printed on quotes, orders, invoices and statements. QuoteConfiguration (per business unit)
    // is SEEDED FROM these values at provisioning, so a tenant's very first quote carries a real
    // letterhead instead of a blank one.

    /// <summary>
    /// Registered legal entity name, when it differs from the trading name — "Acme Trading LLC"
    /// vs "Acme". Commercial documents must carry the LEGAL name; <see cref="Name"/> is a label.
    /// </summary>
    public string? LegalName { get; set; }

    /// <summary>Company / commercial registration number (CR, CIN, company number).</summary>
    public string? RegistrationNumber { get; set; }

    /// <summary>VAT / GST / tax registration number. Required on tax invoices in most jurisdictions.</summary>
    public string? TaxNumber { get; set; }

    /// <summary>ISO-3166-1 alpha-2 country of incorporation, e.g. "SA", "AE", "GB".</summary>
    public string? CountryCode { get; set; }

    public string? Industry { get; set; }

    public string? Website { get; set; }

    public string? AddressLine1 { get; set; }

    public string? AddressLine2 { get; set; }

    public string? City { get; set; }

    public string? StateProvince { get; set; }

    public string? PostalCode { get; set; }

    /// <summary>Company switchboard number, as printed on documents.</summary>
    public string? Phone { get; set; }

    /// <summary>General company address (info@ / sales@), not the administrator's mailbox.</summary>
    public string? ContactEmail { get; set; }

    /// <summary>Absolute URL of the customer's logo, copied onto their quote template.</summary>
    public string? LogoUrl { get; set; }

    // ==== Operating defaults =================================================================
    // Seeded into the tenant's own reference data so its first user is not asked to build a
    // currency list and a UOM list before they can raise anything.

    /// <summary>ISO-4217 code of the tenant's base trading currency, e.g. "SAR", "USD".</summary>
    public string? BaseCurrencyCode { get; set; }

    /// <summary>IANA time zone id, e.g. "Asia/Riyadh". Deadlines and SLA clocks read this.</summary>
    public string? TimeZoneId { get; set; }

    /// <summary>BCP-47 locale for number/date formatting, e.g. "en-GB", "ar-SA".</summary>
    public string? Locale { get; set; }

    /// <summary>
    /// Where this tenant's data is permitted to reside. Recorded at provisioning because it is a
    /// contractual commitment made during the sale, not an infrastructure detail discovered later.
    /// </summary>
    public string? DataRegion { get; set; }

    // ==== Commercial / billing ===============================================================
    // See TenantBillingMode. These columns exist so a tenant can never quietly consume the
    // platform for free: what it is charged, from when, against which price list, and who
    // receives the invoice are all decided at creation and audited.

    /// <summary>
    /// Whether and how this tenant is charged. Defaults to <see cref="TenantBillingMode.Billable"/>
    /// — the safe default is "this customer pays", and every exemption is explicit and audited.
    /// </summary>
    public TenantBillingMode BillingMode { get; set; } = TenantBillingMode.Billable;

    /// <summary>
    /// Why this tenant is not billable. Required (API-enforced) for every mode other than
    /// <see cref="TenantBillingMode.Billable"/>, so free service is always a decision somebody
    /// signed for rather than an omission on a form.
    /// </summary>
    public string? BillingModeReason { get; set; }

    /// <summary>
    /// The price list this tenant's statements are computed against, pinned at creation.
    ///
    /// <para>Without it, statement compute fell back to "whichever active rate card happens to be
    /// effective", so a customer on negotiated terms was silently repriced whenever a new card was
    /// activated, and a period with no active card produced no statement at all — i.e. free
    /// service. Null keeps the legacy fallback for tenants that predate this column.</para>
    /// </summary>
    public long? RateCardId { get; set; }

    /// <summary>
    /// First day charges accrue. Usage before this date is metered but not billed, which is what
    /// makes a paid pilot expressible without hand-editing statements.
    /// </summary>
    public DateTime? BillingStartsOn { get; set; }

    /// <summary>
    /// End of the free trial. Required when <see cref="BillingMode"/> is
    /// <see cref="TenantBillingMode.Trial"/> — an open-ended trial is indistinguishable from free
    /// service, which is precisely the leak this column closes.
    /// </summary>
    public DateTime? TrialEndsOn { get; set; }

    public DateTime? ContractStartOn { get; set; }

    public DateTime? ContractEndOn { get; set; }

    /// <summary>Net payment terms in days (30 = Net 30). Printed on the statement.</summary>
    public int? PaymentTermsDays { get; set; }

    /// <summary>Customer PO / reference their AP department requires on every invoice.</summary>
    public string? PurchaseOrderReference { get; set; }

    /// <summary>Who at the customer receives statements. Distinct from the founding administrator.</summary>
    public string? BillingContactName { get; set; }

    public string? BillingContactEmail { get; set; }

    /// <summary>Invoice address when it differs from the registered address above.</summary>
    public string? BillingAddress { get; set; }

    /// <summary>Internal owner of this account (the operator's CSM/AE email), for escalation.</summary>
    public string? AccountOwnerEmail { get; set; }
}

/// <summary>
/// Whether a tenant is charged, and if not, under what named exemption.
///
/// <para>Stored as a string so a statement produced today can still be explained in a year.
/// The default is <see cref="Billable"/>: on this platform, revenue is the assumption and
/// free service is the exception that has to be argued for.</para>
/// </summary>
public enum TenantBillingMode
{
    /// <summary>Charged: base subscription from the plan plus metered usage from the rate card.</summary>
    Billable = 0,

    /// <summary>
    /// Time-boxed free evaluation. Requires <see cref="Tenant.TrialEndsOn"/>; statements are
    /// computed as zero-charge drafts until that date so conversion has a real baseline.
    /// </summary>
    Trial = 1,

    /// <summary>The operator's own workspaces (demo, support, QA). Never charged, never counted as revenue.</summary>
    Internal = 2,

    /// <summary>Reseller/partner tenants invoiced outside this system under a separate agreement.</summary>
    Partner = 3
}
