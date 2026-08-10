namespace ERP_RFQ_Automation.ListViews;

/// <summary>
/// One selectable column on a list view. <paramref name="Key"/> is the stable
/// identifier a stored preference refers to; it must never be renamed once shipped,
/// because a rename silently discards every user's preference for that column.
/// </summary>
/// <param name="Key">Stable column key. Matches the grid field name on the client.</param>
/// <param name="Label">Human label shown in the column picker.</param>
/// <param name="DefaultVisible">Whether the column is on for a user with no stored preference.</param>
/// <param name="Locked">
/// A column the user may reorder but never hide (row actions, the primary identifier).
/// Locked columns are forced visible on read and on write.
/// </param>
public sealed record ListViewColumn(
    string Key,
    string Label,
    bool DefaultVisible = true,
    bool Locked = false);

/// <summary>
/// A list view's column contract. Defaults live here, in code, next to the grid that
/// renders them — never in the database. A user with no stored preference gets exactly
/// this, in this order.
/// </summary>
/// <param name="ViewKey">
/// Stable view identifier, e.g. <c>leads.list</c>. Part of the preference primary key.
/// </param>
/// <param name="CustomFieldEntityType">
/// The entity whose tenant-defined custom fields become additional selectable columns on
/// this view, or null when the view has no custom-field attach point. This is the seam
/// that makes the two features compose: a field a tenant defines on Customer shows up in
/// the Customers grid column picker without any further wiring.
/// </param>
/// <param name="Columns">Declared columns, in default display order.</param>
public sealed record ListViewDefinition(
    string ViewKey,
    string? CustomFieldEntityType,
    IReadOnlyList<ListViewColumn> Columns);

/// <summary>
/// The registry of list views that support per-user column preferences.
///
/// Adding a view is a two-line change here plus the shared client hook. Removing a
/// column from a view is safe: stored preferences referencing it are ignored on read
/// (see <c>ListViewPreferenceService</c>), never resurrected, and never throw.
/// </summary>
public static class ListViewCatalog
{
    /// <summary>Prefix that marks a column as backed by a tenant-defined custom field.</summary>
    public const string CustomFieldColumnPrefix = "cf:";

    /// <summary>Maximum number of column entries accepted in one saved preference.</summary>
    public const int MaximumStoredColumns = 200;

    private static readonly IReadOnlyDictionary<string, ListViewDefinition> Views =
        new Dictionary<string, ListViewDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            // CustomFieldEntityType is deliberately NULL: Lead (the header) carries no jsonb
            // value bag, so there is nothing for a tenant-defined field to attach to on this
            // grid. Offering an empty "Custom" section here would read as a bug. Custom fields
            // on the lead LINE live on the `lead.items` view, which attaches to LeadItem.
            ["leads.list"] = new("leads.list", null,
            [
                new("nexoraSerial", "Nexora Serial"),
                new("rfqno", "RFQ #"),
                new("client", "Client"),
                new("buyer", "Buyer contact"),
                new("recDate", "Received"),
                new("ingestedAtUtc", "Nexora ingestion date"),
                new("bidClosingDate", "Deadline"),
                // FR-RFQ-04. Two DIFFERENT dates and they are never interchangeable:
                // "Deadline" is when the bid must be back, "Required delivery" is when the
                // buyer wants the goods. Default-visible beside the deadline on purpose —
                // it was captured and shown to nobody, which is how a trader ends up
                // quoting a lead time against the wrong date.
                new("requiredDeliveryDate", "Required delivery"),
                // FR-RFQ-04. The same deadline in the Umm al-Qura calendar, which is how a
                // Saudi government tender publishes it. Hidden by default — nobody's grid
                // rearranges itself on deploy — but selectable, so a rep working a tender can
                // read the deadline in the calendar the tender was written in and check it
                // against the Gregorian one beside it. Rendered alongside, never instead of.
                new("bidClosingDateHijri", "Deadline (Hijri)", DefaultVisible: false),
                // FR-RFQ-03. The standing agreement this inquiry is called off against —
                // not the inquiry's own reference, which is the "RFQ #" column above.
                new("agreementReference", "Agreement reference", DefaultVisible: false),
                new("itemCount", "Items"),
                new("leadSource", "Source"),
                new("status", "Status"),
                new("decision", "Decision"),
                new("estimatedValue", "Estimated value", DefaultVisible: false),
                new("actions", "Actions", Locked: true)
            ]),

            ["customers.list"] = new("customers.list", "Customer",
            [
                new("docId", "Customer code"),
                new("name", "Name"),
                new("contactEmail", "Email"),
                new("billingCity", "Billing city"),
                new("billingCountry", "Billing country"),
                new("shippingCity", "Shipping city", DefaultVisible: false),
                new("shippingCountry", "Shipping country", DefaultVisible: false),
                new("isActive", "Status"),
                new("createdOn", "Created", DefaultVisible: false),
                new("actions", "Actions", Locked: true)
            ]),

            // Keys match the grid fields rendered by Frontend/src/pages/Suppliers/SuppliersPage.tsx.
            // A key here that the page does not render is ignored by the client; a key the page
            // renders that is missing here keeps its position at the end. Both degrade, neither
            // breaks — but they are kept aligned so the picker tells the truth.
            ["suppliers.list"] = new("suppliers.list", "Supplier",
            [
                new("docId", "Supplier code"),
                new("name", "Name"),
                new("contactEmail", "Email"),
                new("countryName", "Country"),
                new("currencyName", "Currency"),
                new("paymentTerms", "Payment terms"),
                new("governanceStatus", "Approval"),
                new("readinessStatus", "RFQ readiness"),
                new("isActive", "Status"),
                new("actions", "Actions", Locked: true)
            ]),

            // THE line grid the configurable-columns request was actually about. Keys match the
            // grid fields rendered by Frontend/src/pages/ExtractionReview/ExtractionReviewDetailPage.tsx.
            //
            // Two families of column live here and they are not the same kind of thing:
            //
            //   * The extraction columns are what the buyer's document said. They are editable
            //     in the workbench and every one of them is a stored LeadItem property.
            //   * The COMMERCIAL columns below the divider are what Nexora already knows about
            //     the part — availability, incoming, expected date, stock unit cost — read from
            //     the persisted lead-line commercial resolution. They are read-only, they are
            //     hidden by default (nobody's grid rearranges itself on deploy), and where no
            //     resolution exists for a line the cell says so rather than showing a 0 that
            //     reads as "none in stock".
            //
            // `checkStatus` and `actions` are Locked: they are reorderable but cannot be hidden,
            // because they are the review verdict and the row's only delete affordance.
            ["lead.items"] = new("lead.items", "LeadItem",
            [
                new("checkStatus", "Review status", Locked: true),
                new("lineItemNo", "Line #"),
                new("productShortName", "Product"),
                new("productShortDescription", "Description"),
                new("quantity", "Qty"),
                new("unitOfMeasure", "UoM"),
                new("unitPrice", "Unit price"),
                new("currency", "Currency"),
                new("manufacturerName", "Manufacturer"),
                new("manufacturerPartNumber", "MPN"),
                new("itemMaterialCode", "Material code"),
                new("leadTime", "Lead time", DefaultVisible: false),
                new("commodityProduct", "Commodity", DefaultVisible: false),
                new("alternateProductName", "Alt product", DefaultVisible: false),
                new("alternatePartNumber", "Alt part #", DefaultVisible: false),
                new("itemText", "Notes", DefaultVisible: false),

                // Already captured per customer, previously readable only in a panel BELOW the
                // grid — so a reviewer could not see the buyer's own Plant Code or Incoterms on
                // the line they were correcting.
                new("documentExtraColumns", "Customer document columns", DefaultVisible: false),

                // Commercial context. Every one of these is persisted by
                // Inventory/Commercial/CommercialLineResolutionApplicationService and was, until
                // now, reachable only from a separate panel that showed no line grid at all.
                new("stockAvailable", "Available now", DefaultVisible: false),
                new("stockIncoming", "Incoming", DefaultVisible: false),
                new("projectedShortage", "Projected shortage", DefaultVisible: false),
                new("supplyStatus", "Supply status", DefaultVisible: false),
                new("expectedAvailableOn", "Expected available", DefaultVisible: false),
                new("stockUnitCost", "Stock unit cost", DefaultVisible: false),

                new("actions", "Actions", Locked: true)
            ])
        };

    /// <summary>Every registered view key. Used by tests and by the client to validate wiring.</summary>
    public static IReadOnlyCollection<string> ViewKeys => (IReadOnlyCollection<string>)Views.Keys;

    /// <summary>Resolves a view definition, or null when the key is not registered.</summary>
    public static ListViewDefinition? Find(string? viewKey) =>
        !string.IsNullOrWhiteSpace(viewKey) && Views.TryGetValue(viewKey.Trim(), out var view) ? view : null;

    /// <summary>Builds the client-facing column key for a tenant-defined custom field.</summary>
    public static string CustomFieldColumnKey(string stableKey) => CustomFieldColumnPrefix + stableKey;

    /// <summary>True when the column key refers to a tenant-defined custom field.</summary>
    public static bool IsCustomFieldColumn(string columnKey) =>
        columnKey.StartsWith(CustomFieldColumnPrefix, StringComparison.Ordinal);
}
