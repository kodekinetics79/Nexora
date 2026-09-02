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

    /// <summary>
    /// ISO-4217 code of the tenant's FUNCTIONAL currency — the base its quotes, ledger and FX
    /// conversions are kept in, e.g. "SAR", "USD". Seeded as the tenant's base <c>Currency</c> row.
    /// NOT the billing currency: Nexora bills every tenant in
    /// <see cref="Billing.PlatformBillingCurrency"/> (USD, carried on the pinned rate card), and
    /// activation compares the rate card to that constant, never to this column.
    /// </summary>
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

    // ==== Deployment profile =================================================================
    // What this tenant's deployment is FOR, and therefore which production prerequisites it is
    // entitled to defer. Server-authoritative and persisted, deliberately: the alternative that
    // was rejected is inferring "this looks like a test tenant" from the hostname, the slug or
    // ASPNETCORE_ENVIRONMENT, all of which are properties of the PROCESS rather than of the
    // customer record, and all of which would let a production tenant inherit a relaxed gate
    // because somebody ran the API with the wrong environment variable.

    /// <summary>
    /// Defaults to <see cref="TenantDeploymentProfile.Production"/>. The default is the strict
    /// one on purpose: a tenant nobody has classified is a tenant that fails closed.
    /// </summary>
    public TenantDeploymentProfile DeploymentProfile { get; set; } = TenantDeploymentProfile.Production;

    /// <summary>
    /// Why this tenant is not on the production profile. Required by the API for every profile
    /// other than <see cref="TenantDeploymentProfile.Production"/>, for the same reason
    /// <see cref="BillingModeReason"/> is required for every mode other than Billable: a relaxed
    /// gate is always a decision somebody signed for.
    /// </summary>
    public string? DeploymentProfileReason { get; set; }

    /// <summary>
    /// The Owner who approved the non-production profile, by email. Absence is what makes a DEMO
    /// tenant UNAPPROVED — and an unapproved DEMO defers nothing and fails closed exactly as
    /// PRODUCTION does, so a row edited directly in the database cannot buy itself a deferral.
    /// </summary>
    public string? DeploymentProfileApprovedBy { get; set; }

    public DateTime? DeploymentProfileApprovedOn { get; set; }

    /// <summary>
    /// Which product modules and capabilities THIS customer has, as a JSON object over the closed
    /// <c>TypedEntitlementCatalog</c> — the same wire shape as <see cref="Plan.Features"/>.
    ///
    /// <para><b>This column is the authority, and the plan is not.</b> Entitlements used to resolve
    /// from <c>Plan.Features</c>, which meant module access was a property of a PRICE TIER rather
    /// than of a customer: granting one tenant Procurement, or revoking Inventory from one tenant,
    /// could only be done by moving them to a different plan — which also moved their seats, their
    /// document quota and what they are charged. Operators therefore cloned plans per customer, and
    /// the plan catalogue stopped describing the commercial offer. A plan now carries capacity and
    /// price; this column carries scope of access.</para>
    ///
    /// <para><b>Absent means denied, and that is deliberate.</b> The store default is <c>{}</c> and
    /// the column is NOT NULL, so a row inserted by anything that does not know about it — an older
    /// code path, a hand-written INSERT, a restored backup — grants nothing rather than everything.
    /// 20260817090000 backfilled every existing tenant from its plan's features, so no live customer
    /// changed what it could reach on the day this shipped.</para>
    ///
    /// <para>A plan's features are still copied in at PROVISIONING time so a new tenant does not
    /// start from nothing — a copy taken once, not an inheritance re-read on every request. Editing
    /// a plan afterwards does not reach back into tenants already provisioned from it.</para>
    /// </summary>
    public string Entitlements { get; set; } = "{}";
}

/// <summary>
/// What a tenant's deployment is for. Decides which production prerequisites may be recorded as
/// DEFERRED or EXTERNALLY_BLOCKED instead of failing activation outright.
///
/// <para><b>This never weakens the production gate.</b> A
/// <see cref="Production"/> tenant is evaluated exactly as it was before this enum existed: every
/// unsatisfied control blocks. The other two profiles change nothing about what is CHECKED or how
/// a control is decided — only about whether a named, catalogued external prerequisite is allowed
/// to stand in the way of a workspace that exists to be tested. In every profile the deferred
/// prerequisites stay visible as production blockers and make production-readiness certification
/// impossible.</para>
///
/// <para>Stored as its NAME, like <see cref="TenantBillingMode"/>: an activation decision recorded
/// today has to remain explainable years later, and reordering this enum must never reclassify a
/// historical LOCAL_TEST activation as a PRODUCTION one.</para>
/// </summary>
public enum TenantDeploymentProfile
{
    /// <summary>A real customer. Every control is a hard gate. The default.</summary>
    Production = 0,

    /// <summary>
    /// A workspace stood up to exercise the product on a laptop or a CI runner. Nobody's data is
    /// in it, no invoice is printed from it, and the third-party estate a real customer brings
    /// (their storage, their ERP, their identity provider, their tax authority) does not exist.
    /// </summary>
    LocalTest = 1,

    /// <summary>
    /// A sales or training workspace shown to real people. Deferral applies only once an Owner
    /// has recorded an approval — see <see cref="Tenant.DeploymentProfileApprovedBy"/>.
    /// </summary>
    Demo = 2
}

/// <summary>
/// Wire vocabulary for <see cref="TenantDeploymentProfile"/>. SCREAMING_SNAKE on the wire matches
/// the vocabulary the activation decision already speaks (<c>PROSPECT</c>, <c>RESTRICTED</c>,
/// <c>PURGED</c>), so a console reading one payload never has to switch conventions halfway down it.
/// </summary>
public static class TenantDeploymentProfiles
{
    public const string Production = "PRODUCTION";
    public const string LocalTest = "LOCAL_TEST";
    public const string Demo = "DEMO";

    public static string ToWire(TenantDeploymentProfile profile) => profile switch
    {
        TenantDeploymentProfile.LocalTest => LocalTest,
        TenantDeploymentProfile.Demo => Demo,
        _ => Production
    };

    /// <summary>
    /// Parses a wire code. Unrecognised input is REFUSED rather than defaulted, because the two
    /// plausible defaults are both wrong: defaulting to Production silently ignores an operator's
    /// request, and defaulting to LocalTest hands out a relaxed gate on a typo.
    /// </summary>
    public static bool TryParse(string? value, out TenantDeploymentProfile profile)
    {
        switch (value?.Trim().ToUpperInvariant())
        {
            case Production: profile = TenantDeploymentProfile.Production; return true;
            case LocalTest: profile = TenantDeploymentProfile.LocalTest; return true;
            case Demo: profile = TenantDeploymentProfile.Demo; return true;
            default: profile = TenantDeploymentProfile.Production; return false;
        }
    }

    public static IReadOnlyList<string> All => [Production, LocalTest, Demo];
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
