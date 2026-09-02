using ERP_RFQ_Automation.Authorization;

namespace ERP_RFQ_Automation.Platform.Services;

/// <summary>
/// The reference data and role templates a freshly provisioned <c>BusinessUnit</c> is opened with.
///
/// <para><b>Why the data lives apart from the seeder.</b> Everything here is a commercial
/// judgement — which units a trading desk transacts in, which modules a sales representative may
/// write to — and each entry has to be arguable on its own. The mechanics of check-then-insert are
/// not commercial judgements at all. Splitting them is the same separation
/// <see cref="ModuleCatalog"/> and <c>ModuleCatalogReconciler</c> already use, and it means a
/// change to what a role may do is a change to a list, never to a query.</para>
/// </summary>
public static class TenantBaselineCatalog
{
    // ── Units of measure ────────────────────────────────────────────────────────────────────
    // Exactly the canonical codes UomCanonicalizer emits — no more, no fewer.
    //
    // That correspondence is the whole point rather than a convenience. SetUomVocabulary indexes
    // a tenant's SetUom rows by folded code and name, and UomCanonicalizer consults it to attach
    // Rfqitem.UomId / OrderItem.UomId to an extracted line. A tenant with no rows resolves every
    // extracted unit to a canonical STRING with a null foreign key, so the unit shows on the line
    // but joins to nothing; a tenant whose list omits, say, M2 gets the same silent null on every
    // square-metre line a customer sends. Seeding the canonicaliser's own vocabulary makes the
    // invariant exact in both directions: every unit Nexora can resolve has a tenant row to point
    // at, and every unit it deliberately REFUSES to resolve (PACK, PALLET, LENGTH, TON — see the
    // Refusals table there) is absent here too, so no seeded row can quietly legitimise a
    // quantity nobody stated.

    public sealed record UnitOfMeasureDefinition(string Code, string Name, string Description);

    public static readonly IReadOnlyList<UnitOfMeasureDefinition> UnitsOfMeasure =
    [
        // Count and assemblies.
        new("EA", "Each", "Individual item — the count unit behind each/pcs/piece/Nos"),
        new("SET", "Set", "Assembly or kit priced and delivered as one line"),
        new("PR", "Pair", "Two matched items; kept separate from Each because it carries a multiplier"),
        new("DZ", "Dozen", "Twelve items; kept separate from Each because it carries a multiplier"),
        new("LOT", "Lot", "Whole scope of supply priced as a single lump sum"),
        new("AU", "Activity unit", "Unit of activity rather than a physical item (SAP Activ.unit)"),

        // Length.
        new("MM", "Millimetre", "Length in millimetres"),
        new("CM", "Centimetre", "Length in centimetres"),
        new("M", "Metre", "Length in metres, including linear and running metres"),
        new("KM", "Kilometre", "Length in kilometres"),
        new("IN", "Inch", "Length in inches"),
        new("FT", "Foot", "Length in feet"),
        new("YD", "Yard", "Length in yards"),

        // Area and volume.
        new("M2", "Square metre", "Area in square metres"),
        new("FT2", "Square foot", "Area in square feet"),
        new("M3", "Cubic metre", "Volume in cubic metres"),
        new("L", "Litre", "Volume in litres"),
        new("ML", "Millilitre", "Volume in millilitres"),

        // Mass.
        new("KG", "Kilogram", "Mass in kilograms"),
        new("G", "Gram", "Mass in grams"),
        new("MT", "Metric tonne", "Mass in metric tonnes (1,000 kg)"),
        new("LB", "Pound", "Mass in pounds"),

        // Effort and duration.
        new("HR", "Hour", "Effort in hours, including man-hours"),
        new("DAY", "Day", "Effort in days, including man-days"),
        new("WK", "Week", "Duration in weeks"),
        new("MTH", "Month", "Duration in months"),
        new("YR", "Year", "Duration in years")
    ];

    // ── Discount types ──────────────────────────────────────────────────────────────────────
    // QuoteService.CalculateQuoteTotals resolves the discount type by SetupCode and applies the
    // discount ONLY when the code reads PERCENTAGE or FIXED (Services/QuoteService.cs:489, :524).
    // Anything else falls through the if/else and contributes zero. With no rows at all the
    // discount fields on the quote form have nothing to select, so the commonest concession in
    // trading — "5% off the line" — cannot be expressed on a tenant's first quote.

    public static readonly IReadOnlyList<(string Code, string Label, string Description)> DiscountTypes =
    [
        ("PERCENTAGE", "Percentage", "Discount entered as a percentage of the line or quote value"),
        ("FIXED", "Fixed amount", "Discount entered as an absolute amount in the quote currency")
    ];

    // ── Reference lists ─────────────────────────────────────────────────────────────────────
    // The Setup_Master lists a commercial screen reads by SetupType and that nothing seeded until
    // now. Copied value-for-value from business unit 1 on the live database (2026-09-02), which
    // is the only tenant that ever had them — every later tenant was provisioned without them,
    // and each absence fails somewhere other than where it is caused:
    //
    //   ShipmentStatus     ShipmentController.CreateShipment refuses any status that is not an
    //                      active row of this type for the tenant, so a tenant with no rows cannot
    //                      despatch at all ("The shipment status is not active for this business
    //                      unit" on a form whose status picker was empty).
    //   PaymentMethod      Orders.PaymentMethodID is a foreign key to this list; the order form's
    //                      payment method picker renders empty.
    //   LeadRejectedReason Leads.LeadRejectedReasonID is a foreign key to this list; a lead cannot
    //                      be rejected with a reason.
    //   RFQType            The RFQ form's type picker (Direct vs Agreement pricing).
    //   QuoteOutcomeReason The reason recorded when a quote is lost or withdrawn.
    //
    // Lifecycle states (LeadStatus, RFQStatus, QuoteStatus, OrderStatus, PaymentStatus) are NOT
    // here: they are governed by LifecyclePolicy and seeded from LifecycleStatusCatalog, and
    // DiscountType keeps its own entry above because QuoteService acts on its codes.
    //
    // These are seeded only when the tenant has NO row of the type at all. A list a customer has
    // already shaped — renamed, deactivated, or trimmed — is theirs; adding "missing" entries to
    // it would reinstate choices they removed on purpose.

    public sealed record ReferenceListEntry(string Code, string Label, string Description);

    public sealed record ReferenceList(string SetupType, IReadOnlyList<ReferenceListEntry> Entries);

    public static readonly IReadOnlyList<ReferenceList> ReferenceLists =
    [
        new("ShipmentStatus",
        [
            new("PENDING", "Pending", "Shipment is pending"),
            new("SHIPPED", "Shipped", "Shipment dispatched"),
            new("IN_TRANSIT", "In Transit", "Shipment in transit"),
            new("DELIVERED", "Delivered", "Shipment delivered"),
            new("CANCELLED", "Cancelled", "Shipment cancelled")
        ]),
        new("PaymentMethod",
        [
            new("BANK_TRANSFER", "Bank Transfer", "Direct Bank Transfer"),
            new("CASH", "Cash", "Cash Payment"),
            new("CHECK", "Check", "Cheque Payment"),
            new("CREDIT_CARD", "Credit Card", "Credit Card Payment")
        ]),
        new("LeadRejectedReason",
        [
            new("MANUFACTURER_DIRECT", "Manufacturer Direct", "Customer prefers to deal directly with manufacturer"),
            new("NOT_OUR_BUSINESS", "Not Our Business", "Requirement does not match our business offerings"),
            new("NO_SOURCE", "No Source", "Lead has no valid source information"),
            new("OBSOLETE_ITEM", "Obsolete Item/System", "Requested item or system is obsolete"),
            new("SHORT_BCD", "Short BCD", "Remaining shelf life (BCD) is too short")
        ]),
        new("RFQType",
        [
            new("DIRECT", "Direct Bidding", "Direct RFQ type - one-time purchase"),
            new("AGREEMENT", "Agreement", "Agreement-based RFQ - contract pricing")
        ]),
        new("QuoteOutcomeReason",
        [
            new("PRICE", "Price too high", "Price too high"),
            new("LEAD_TIME", "Lead time too long", "Lead time too long"),
            new("NO_STOCK", "Item unavailable", "Item unavailable"),
            new("LOST_COMPETITOR", "Lost to competitor", "Lost to competitor"),
            new("CUSTOMER_CANCELLED", "Customer cancelled", "Customer cancelled"),
            new("NO_RESPONSE", "No response", "No response"),
            new("AUTO_EXPIRED", "Expired automatically", "Expired automatically"),
            new("OTHER", "Other", "Other")
        ])
    ];

    // ── Currencies ──────────────────────────────────────────────────────────────────────────
    // A hand-held table rather than anything derived from CultureInfo/RegionInfo. Two reasons.
    // Mapping an ISO-4217 code back to a name through the framework means scanning every culture
    // and taking whichever region matches first, which is neither stable nor obviously correct.
    // More importantly, Currency.CurrencyName and Currency.Symbol are PRINTED on the customer's
    // quotes and statements: reference data written into a customer's database must not vary with
    // the ICU version that happens to be installed on the node that provisioned them.
    //
    // Gulf currencies carry their Latin abbreviation as the symbol rather than their Arabic glyph,
    // because the symbol is rendered into the quote PDF by a font chosen for Latin commercial
    // documents; an Arabic glyph the font lacks prints as a replacement box on the very first
    // document the customer sends out. A tenant that wants the glyph can set it — this is the
    // opening value, not a constraint.

    public sealed record CurrencyDefinition(string Code, string Name, string Symbol);

    private static readonly IReadOnlyDictionary<string, CurrencyDefinition> KnownCurrencies =
        new[]
        {
            new CurrencyDefinition("AED", "UAE Dirham", "AED"),
            new CurrencyDefinition("SAR", "Saudi Riyal", "SAR"),
            new CurrencyDefinition("QAR", "Qatari Riyal", "QAR"),
            new CurrencyDefinition("KWD", "Kuwaiti Dinar", "KWD"),
            new CurrencyDefinition("BHD", "Bahraini Dinar", "BHD"),
            new CurrencyDefinition("OMR", "Omani Rial", "OMR"),
            new CurrencyDefinition("EGP", "Egyptian Pound", "EGP"),
            new CurrencyDefinition("JOD", "Jordanian Dinar", "JOD"),
            new CurrencyDefinition("USD", "US Dollar", "$"),
            new CurrencyDefinition("EUR", "Euro", "€"),
            new CurrencyDefinition("GBP", "Pound Sterling", "£"),
            new CurrencyDefinition("CHF", "Swiss Franc", "CHF"),
            new CurrencyDefinition("INR", "Indian Rupee", "₹"),
            new CurrencyDefinition("PKR", "Pakistani Rupee", "PKR"),
            new CurrencyDefinition("TRY", "Turkish Lira", "TRY"),
            new CurrencyDefinition("CNY", "Chinese Yuan", "CNY"),
            new CurrencyDefinition("JPY", "Japanese Yen", "¥"),
            new CurrencyDefinition("SGD", "Singapore Dollar", "SGD"),
            new CurrencyDefinition("ZAR", "South African Rand", "ZAR"),
            new CurrencyDefinition("CAD", "Canadian Dollar", "CAD"),
            new CurrencyDefinition("AUD", "Australian Dollar", "AUD")
        }.ToDictionary(definition => definition.Code, StringComparer.Ordinal);

    /// <summary>
    /// The row to write for an ISO-4217 code. A code outside the table above is still accepted:
    /// it becomes its own name and symbol, which is honest ("USD"/"USD" reads as an unfinished
    /// setting an administrator can complete) and is far better than refusing to provision a
    /// customer whose currency we simply have not listed yet.
    /// </summary>
    public static CurrencyDefinition ResolveCurrency(string code) =>
        KnownCurrencies.TryGetValue(code, out var known) ? known : new CurrencyDefinition(code, code, code);

    // ── Roles ───────────────────────────────────────────────────────────────────────────────

    /// <summary>What one starter role may do on one module. Every flag is stated, including
    /// <see cref="CanView"/>: since RC-3/RC-4 the existence of a RolePermissions row is NOT the
    /// read grant, and a row with all four flags false grants nothing at all.</summary>
    public sealed record ModuleGrant(string Module, bool CanView, bool CanCreate, bool CanEdit, bool CanDelete);

    public sealed record StarterRole(
        string Code, string Name, string Description, short Rank, IReadOnlyList<ModuleGrant> Grants);

    private static ModuleGrant Read(string module) => new(module, true, false, false, false);
    private static ModuleGrant ReadAdd(string module) => new(module, true, true, false, false);
    private static ModuleGrant ReadAmend(string module) => new(module, true, false, true, false);
    private static ModuleGrant Work(string module) => new(module, true, true, true, false);
    private static ModuleGrant Own(string module) => new(module, true, true, true, true);

    /// <summary>
    /// The four desks of a trading business, alongside the founding Super Administrator that
    /// provisioning creates separately.
    ///
    /// <para><b>Why only ONE role here sits at <see cref="RoleRanks.Admin"/>, and why it grants
    /// nothing.</b> <c>PermissionHandler</c> satisfies EVERY module requirement for a role at Admin
    /// or above before it reads a single RolePermissions row. A starter role at that tier carrying
    /// a curated set of ticked boxes would therefore hold the entire tenant while the Roles &amp;
    /// Permissions matrix described something narrower — a permission screen that actively lies.
    ///
    /// The answer is not to refuse the tier: a customer needs a delegated administrator, and
    /// withholding one is why every user in the live tenant sits at Owner. The answer is to make
    /// the tier's honesty structural. <c>SYSTEM_ADMIN</c> carries an EMPTY grant list, so there is
    /// no curated matrix to contradict, and the roles screen renders one sentence in place of the
    /// checkboxes rather than 212 controls that change nothing. A grant list on this role is not a
    /// nicety this catalogue happens to omit — it is the defect, which is why
    /// <c>TenantBaselineRoleTemplateTests</c> asserts that a role at Admin or above holds no grants
    /// at all.</para>
    ///
    /// <para><b>Why the sales manager is a strict superset of the sales representative.</b>
    /// <c>RoleGate.CanManageRoleAsync</c> refuses to let one role administer another that holds a
    /// grant the caller lacks. If the manager did not dominate the representative, the manager
    /// could not edit their own team's role — the first administrative act a sales desk performs.
    /// The other two desks are deliberately NOT dominated by anyone below Owner: procurement and
    /// finance answer to the tenant owner, not to sales.</para>
    /// </summary>
    public static readonly IReadOnlyList<StarterRole> StarterRoles =
    [
        new("SYSTEM_ADMIN", "System Administrator",
            "Administers everything in this organization. Individual permission lines do not apply " +
            "to this role — its authority comes from its level, not from a list.",
            RoleRanks.Admin,
            // Deliberately empty, and asserted to be. Every module check succeeds by rank before a
            // RolePermissions row is consulted, so any entry here would be decorative: ticking it
            // would grant nothing that was not already held, and un-ticking it would revoke
            // nothing. The empty list is what lets the roles screen tell the truth in one sentence.
            []),

        new("SALES_MANAGER", "Sales Manager",
            "Runs the sales desk: owns the pipeline end to end, sets quote branding and terms, and " +
            "sees what customers owe without being able to move money.",
            RoleRanks.Manager,
            [
                Read("Dashboard"),
                Own("Leads"),
                Own("RFQ Management"),
                Own("Quotations"),
                Work("Orders"),
                Work("Shipments"),
                Work("Customers"),
                Work("Customer Awards"),
                Read("Suppliers"),
                Read("Supplier History"),
                Read("Products"),
                Read("Product Categories"),

                // The letterhead, terms and colours every quote this desk sends is printed with.
                // QuoteConfigurationController.CreateOrUpdate gates on Edit, so View alone would
                // render a form that cannot be saved.
                ReadAmend("Quote Configuration"),

                // Read-only on the user list because lead assignment is a name-picker over it.
                // Create/Edit stay with administration — assigning work is not the same authority
                // as minting the people it is assigned to.
                Read("Users"),

                // Credit awareness, not credit authority. A manager quoting a customer who is on
                // stop needs to know before they quote; nothing here lets them raise, adjust or
                // write off a receivable.
                Read("Accounts Receivable"),
                Read("Customer Statements"),
                Read("Collection Controls"),

                // Sec-D4: quotes convert at an approved FX rate, so this desk has to be able to
                // SEE which rate its numbers came from. It cannot raise one and cannot approve
                // one — "Exchange Rate Approval" is absent here on purpose, for the same reason
                // the finance desk below has no reconciliation-approval authority.
                Read("Currencies"),
                Read("Exchange Rates")
            ]),

        new("SALES_REP", "Sales Representative",
            "Works enquiries through to quotes and orders for their own customers. No finance, no " +
            "supplier negotiation, no administration.",
            RoleRanks.Member,
            [
                Read("Dashboard"),

                // Create and edit but never delete. A lead, RFQ or quote is the evidence trail
                // behind a commercial commitment; withdrawing one is a status change, and erasing
                // one is an act the desk's manager owns.
                Work("Leads"),
                Work("RFQ Management"),
                Work("Quotations"),

                // Raise the order that a won quote becomes, but not amend one afterwards: an
                // accepted order is a commitment to the customer, and changing it after the fact
                // is the manager's call.
                ReadAdd("Orders"),
                Read("Shipments"),

                Work("Customers"),
                Read("Customer Awards"),

                // Supplier data is procurement's to maintain; a representative reads it to answer
                // "have we bought this before, and at what price".
                Read("Suppliers"),
                Read("Supplier History"),
                Read("Products"),
                Read("Product Categories"),

                // The currency picker on a quote is a read of this module. Without it the quote
                // screen renders an empty dropdown rather than a denial, which is worse.
                Read("Currencies"),
                Read("Exchange Rates")
            ]),

        new("PROCUREMENT_OFFICER", "Procurement Officer",
            "Sources the supply side: suppliers, supplier quotes and negotiation, purchase orders " +
            "and goods receipts, and the product catalogue they populate.",
            RoleRanks.Member,
            [
                Read("Dashboard"),

                // Sourcing updates an existing RFQ's supply side. Raising and withdrawing the RFQ
                // itself belongs to the sales desk that owns the customer enquiry.
                ReadAmend("RFQ Management"),

                Work("Suppliers"),
                Work("Supplier History"),
                ReadAmend("Supplier Negotiation"),
                Work("Products"),
                Work("Product Categories"),

                // "Orders" gates SUPPLIER purchase orders as well as customer sales orders
                // (ProcurementController raises, issues and cancels purchase orders and posts
                // goods receipts behind Orders Create/Edit). One module name covers both
                // directions of trade, so purchase-order authority cannot currently be granted
                // without also granting sales-order write. That is a limitation of the module
                // list, not a decision taken here — a procurement officer who cannot issue a
                // purchase order has no job.
                Work("Orders"),

                // Supplier quotes arrive in the supplier's currency and are compared in the
                // tenant's, so this desk reads the rate that made the comparison. Raising and
                // approving rates both stay with finance.
                Read("Currencies"),
                Read("Exchange Rates")
            ]),

        new("FINANCE_OFFICER", "Finance Officer",
            "Runs receivables and collections and prepares bank reconciliation. Deliberately holds " +
            "no approval or ledger-control authority.",
            RoleRanks.Member,
            [
                Read("Dashboard"),

                // The commercial context behind an invoice; neither is editable from here.
                Read("Customers"),
                Read("Orders"),

                Work("Accounts Receivable"),
                Work("Customer Payments"),
                Work("Customer Statements"),
                Work("Dunning Cases"),
                Work("Dunning Notices"),
                Read("Dunning Policies"),

                // Segregation of duties, stated as read-only rather than omitted: an officer must
                // be able to SEE the refunds, adjustments and write-offs raised against the ledger
                // they are reconciling. Every one of those is money leaving or a debt being
                // forgiven, so raising them stays with the tenant's owner until they delegate it
                // deliberately.
                Read("Customer Refunds"),
                Read("Receivable Adjustments"),
                Read("Receivable Write-offs"),
                Read("Collection Controls"),

                // Prepare a reconciliation; never approve one.
                Read("Bank Accounts"),
                ReadAdd("Bank Statement Import"),
                Work("Bank Reconciliation"),
                ReadAdd("Bank Adjustments"),

                // Enquiry on the ledger, no posting. General Ledger Posting, Ledger Control,
                // Period Close, Bank Reconciliation Approval, Bank Adjustment Approval and both
                // Bank Matching Rule modules are all absent on purpose: they are the controls that
                // make the preparer and the approver different people, and a starter role that
                // held them would collapse that distinction on day one.
                Read("General Ledger"),
                Read("Accounting Periods"),

                // Sec-D4: finance maintains the currency list and RAISES exchange rates. It does
                // not approve them. "Exchange Rate Approval" is withheld exactly as Bank
                // Reconciliation Approval is above — an approved rate re-bases every quote, the
                // below-floor pricing guard and the AI spend cap, and a starter role that could
                // both raise and approve one would make the maker-checker rule in
                // CurrencyController true only on paper.
                Work("Currencies"),
                ReadAdd("Exchange Rates")
            ])
    ];
}
