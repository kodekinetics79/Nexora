namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>How a tenant-plane table with no business-unit column of its own reaches a tenant.</summary>
/// <param name="ForeignKeyColumn">The column on THIS table pointing at the parent. Must be
/// <c>NOT NULL</c>, or the predicate built from it is incomplete by construction and the rows with
/// a null parent survive a purge that reports success.</param>
/// <param name="ParentTable">The table it points at, unquoted, in the <c>public</c> schema.</param>
/// <param name="ParentKeyColumn">The parent's key column.</param>
/// <param name="DiscriminatorColumn">
/// For a polymorphic parent link, the column that says WHICH kind of parent the row hangs off.
/// <c>public."Attachments"</c> is the only one in this schema: <c>ParentID</c> means nothing
/// without <c>ParentType</c>, and reading it without the discriminator would delete a material-lot
/// certificate because a lead somewhere shares its id.
/// </param>
/// <param name="DiscriminatorValue">The value of that column for this parent kind.</param>
public sealed record TenantPlaneParent(
    string ForeignKeyColumn,
    string ParentTable,
    string ParentKeyColumn,
    string? DiscriminatorColumn = null,
    string? DiscriminatorValue = null);

/// <summary>One classified tenant-plane table that carries no business-unit column.</summary>
/// <param name="Table">Table name within the <c>public</c> schema, unquoted.</param>
/// <param name="Classification">Destroy on purge, or it is not the customer's data at all.</param>
/// <param name="Reason">Why. Read by whoever adds the next table and has to make the same call.</param>
/// <param name="ReachedThrough">
/// One entry per parent kind. More than one is legal and necessary:
/// <c>public."Attachments"</c> hangs off leads AND off material lots, and a map that recorded only
/// the first would leave every lot certificate standing.
/// </param>
public sealed record TenantPlaneTable(
    string Table,
    TenantDataClass Classification,
    string Reason,
    IReadOnlyList<TenantPlaneParent>? ReachedThrough = null)
{
    public bool IsCustomerRecord => Classification == TenantDataClass.CustomerRecord;
}

/// <summary>
/// The declared destiny of every <c>public</c>-schema table that a business-unit column sweep
/// cannot see.
///
/// <para><b>Why this file exists.</b> <see cref="TenantPurgeExecutor"/> derives the tenant plane
/// from the catalogue — every table carrying a business unit column — and that rule is right for
/// the 205 tables it can see. It is silent about the rest. Fifteen <c>public</c> tables carry no
/// business unit column at all, and fourteen of them are unambiguously the customer's data:
/// <c>RFQItems</c>, <c>QuoteItems</c>, <c>LeadItems</c> and <c>OrderItems</c> are every line of
/// every enquiry, quote and order the tenant ever had. A purge swept none of them and reported
/// success.</para>
///
/// <para><b>The database already knew.</b> Each of these fourteen carries a
/// <c>nexora_tenant_isolation</c> row-level-security policy whose predicate walks the same parent
/// chain declared here — <c>public."EmailIngests"</c>'s policy joins <c>Email_Configurations</c> on
/// exactly <c>EmailConfigurationID</c>. The request path has always known these rows belong to a
/// tenant; only the purge did not. <c>TenantPurgeExecutor</c>'s post-condition now reads that same
/// policy catalogue as an INDEPENDENT authority, so a table declared tenant-scoped by the schema
/// and missing from this map stops a purge rather than being quietly skipped.</para>
///
/// <para><b>Every foreign key named here is NOT NULL, and that is load-bearing.</b> A nullable
/// parent link would mean the predicate matched some of the tenant's rows and left the rest —
/// silent partial success, the exact failure this class exists to end. Asserted by
/// <c>TenantPlaneClassificationTests</c> against the live catalogue rather than trusted.</para>
///
/// <para><b>Children are deleted before parents.</b> The purge runs under
/// <c>session_replication_role = 'replica'</c>, which suspends <c>ON DELETE CASCADE</c> along with
/// the append-only guards, so nothing collects these rows for us. A child selected through a
/// subquery on its parent must be deleted while that parent still exists, or the subquery matches
/// nothing and the DELETE is a well-formed no-op.</para>
/// </summary>
public static class TenantPlaneDataMap
{
    public const string Schema = "public";

    /// <summary>
    /// The <c>Attachments.ParentType</c> value for a lead's document. A literal rather than a
    /// reference because <c>LeadRepository</c>, <c>EvidenceRetentionService</c> and the
    /// row-level-security policy all spell it out the same way.
    /// </summary>
    public const string LeadAttachmentParentType = "Lead";

    public static readonly IReadOnlyList<TenantPlaneTable> Tables =
    [
        // ==== the customer's data, reached through a parent — destroyed ======================

        new("EmailIngests", TenantDataClass.CustomerRecord,
            "Every message the tenant's mailbox accepted, including the pointer to the raw .eml. "
            + "Scoped only through Email_Configurations, which a purge destroys — so before this "
            + "entry the rows were not merely left behind, they were left ORPHANED against a "
            + "deleted parent, and replica mode meant no foreign key complained.",
            [new TenantPlaneParent("EmailConfigurationID", "Email_Configurations", "ID")]),

        new("LeadItems", TenantDataClass.CustomerRecord,
            "Every line of every lead: part numbers, quantities, prices, buyer names.",
            [new TenantPlaneParent("LeadID", "Leads", "ID")]),

        new("RFQItems", TenantDataClass.CustomerRecord,
            "Every line of every RFQ. The single largest body of commercial data in the schema "
            + "with no business unit column of its own.",
            [new TenantPlaneParent("RFQID", "RFQ", "ID")]),

        new("QuoteItems", TenantDataClass.CustomerRecord,
            "Every priced line the tenant ever quoted a customer.",
            [new TenantPlaneParent("QuoteID", "Quotes", "ID")]),

        new("OrderItems", TenantDataClass.CustomerRecord,
            "Every ordered line. Reached through Orders rather than through Products or "
            + "CustomerAwardLineAllocations: OrderID is the owning link and the only one of the "
            + "five foreign keys on this table that is NOT NULL.",
            [new TenantPlaneParent("OrderID", "Orders", "ID")]),

        new("ShipmentItems", TenantDataClass.CustomerRecord,
            "What physically shipped, per line. Reached through Shipments and not through "
            + "OrderItems, so the chain is one hop rather than two and does not depend on another "
            + "indirect table having been swept first.",
            [new TenantPlaneParent("ShipmentID", "Shipments", "ID")]),

        new("ShipmentStatusHistory", TenantDataClass.CustomerRecord,
            "Delivery progress. Reached through Shipments; its other two foreign keys point at "
            + "Setup_Master status rows, which are a lookup and not an owner.",
            [new TenantPlaneParent("ShipmentId", "Shipments", "ID")]),

        new("ProductAttachments", TenantDataClass.CustomerRecord,
            "Datasheets and drawings filed against the tenant's own catalogue items.",
            [new TenantPlaneParent("InventoryID", "Products", "ID")]),

        new("SupplierPurchaseHistory", TenantDataClass.CustomerRecord,
            "What the tenant paid whom, per batch. Both foreign keys are NOT NULL and both lead "
            + "to tenant-scoped tables; Products is named because a purchase history row is a "
            + "fact about an item the tenant buys.",
            [new TenantPlaneParent("ProductId", "Products", "ID")]),

        new("custom_field_versions", TenantDataClass.CustomerRecord,
            "Every revision of a tenant-defined field, including its default values.",
            [new TenantPlaneParent("DefinitionId", "custom_field_definitions", "Id")]),

        new("custom_field_options", TenantDataClass.CustomerRecord,
            "Pick-list values on a tenant-defined field. Two hops from a business unit.",
            [new TenantPlaneParent("VersionId", "custom_field_versions", "Id")]),

        new("custom_field_rules", TenantDataClass.CustomerRecord,
            "Conditional show/hide rules on a tenant-defined field. Two hops.",
            [new TenantPlaneParent("VersionId", "custom_field_versions", "Id")]),

        new("custom_field_dependencies", TenantDataClass.CustomerRecord,
            "Which tenant-defined field depends on which. Reached through VersionId, the link to "
            + "the field that OWNS the dependency, rather than through DependsOnDefinitionId, "
            + "which points at the field depended UPON.",
            [new TenantPlaneParent("VersionId", "custom_field_versions", "Id")]),

        new("Attachments", TenantDataClass.CustomerRecord,
            "Files the customer uploaded. Polymorphic on (ParentType, ParentID) with no foreign "
            + "key at all, so nothing in the catalogue can derive this and nothing in the database "
            + "would complain about what it left behind. TWO parent kinds are written by this "
            + "application and both are here: the row-level-security policy admits only 'Lead', "
            + "which means a MaterialLotCertificate attachment is invisible to the tenant that "
            + "owns it AND survived every purge. A third kind appearing without an entry here is "
            + "caught by TenantPlaneClassificationTests before it can repeat that.",
            [
                new TenantPlaneParent(
                    "ParentID", "Leads", "ID",
                    DiscriminatorColumn: "ParentType",
                    DiscriminatorValue: LeadAttachmentParentType),
                new TenantPlaneParent(
                    "ParentID", "material_lots", "Id",
                    DiscriminatorColumn: "ParentType",
                    DiscriminatorValue: Traceability.MaterialLotCertificateService.CertificateParentType)
            ]),

        // ==== not the customer's data — never swept ==========================================
        // Declared rather than omitted. An unclassified table stops the purge (see
        // TenantPurgeExecutor.EnsureEveryTenantPlaneTableIsClassifiedAsync) precisely because a
        // table with no business unit column offers no way to guess, and both wrong answers are
        // damaging: destroying shared reference data breaks every other tenant, and skipping the
        // customer's rows is the defect this whole class exists to close.

        new("Module", TenantDataClass.OperatorRecord,
            "The product's own catalogue of modules. Shared by every tenant and owned by none; "
            + "per-tenant entitlement lives in TenantModuleEntitlements, which carries a business "
            + "unit column and is swept normally."),

        new("LoginAttempts", TenantDataClass.OperatorRecord,
            "Brute-force throttling state, keyed by a hashed attempt key across both planes. Not "
            + "attributable to a tenant even in principle, and destroying it would hand an "
            + "attacker a lockout reset."),

        new("FinanceProviderSecrets", TenantDataClass.OperatorRecord,
            "Deployment-wide provider credentials keyed by provider name. Operator "
            + "configuration; a tenant leaving does not revoke the platform's own keys."),

        new("Images", TenantDataClass.OperatorRecord,
            "Legacy polymorphic image table with no writer anywhere in the application, no "
            + "foreign key, and no row-level security. It holds nothing, and it is named here "
            + "rather than left unclassified so that giving it a writer forces this decision "
            + "again instead of silently creating a fifteenth unswept table."),

        new("BusinessUnits", TenantDataClass.CustomerRecord,
            "The tenant's own workspace row. Destroyed LAST and by primary key rather than "
            + "through this map, because its tenant column IS its primary key. Declared here so "
            + "the classification sweep does not report it as an unclassified survivor."),

        new("__EFMigrationsHistory", TenantDataClass.OperatorRecord,
            "The database's own schema ledger. Not the customer's data and not a record of them; "
            + "deleting from it would break the database rather than a tenant.")
    ];

    /// <summary>Tables reached through a parent, deepest chain first, so a child is deleted while
    /// the parent its subquery selects through still exists.</summary>
    public static IReadOnlyList<TenantPlaneTable> Destroyed { get; } =
        Tables.Where(t => t.IsCustomerRecord && t.ReachedThrough is { Count: > 0 })
              .OrderByDescending(Depth)
              .ThenBy(t => t.Table, StringComparer.Ordinal)
              .ToList();

    public static TenantPlaneTable? Find(string table) =>
        Tables.FirstOrDefault(t => string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How many hops separate a table from a business unit column. Used only to order the sweep;
    /// a cycle would loop forever, so the walk is bounded and a table that cannot be resolved
    /// within the bound is reported as depth 0 and ordered last, where the runtime classification
    /// check will refuse it by name.
    /// </summary>
    public static int Depth(TenantPlaneTable table)
    {
        var depth = 0;
        var current = table;
        for (var hop = 0; hop < Tables.Count; hop++)
        {
            var parent = current.ReachedThrough?.FirstOrDefault();
            if (parent is null) return depth;
            depth++;
            var next = Find(parent.ParentTable);
            if (next is null) return depth;
            current = next;
        }

        return depth;
    }
}
