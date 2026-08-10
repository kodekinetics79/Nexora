using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using ERP_RFQ_Automation.Models;

namespace ERP_RFQ_Automation.MasterData;

/// <summary>
/// Everything the audit needs to know about one master-data type: how to read its tenant, its key
/// and its display label, which of its columns are not worth a trail row, and how to classify the
/// ones that are.
///
/// <para><b>The audited field list is derived from EF metadata, not written down here.</b> The
/// interceptor walks <c>entry.Properties</c> and audits everything except the exclusions below, so
/// a column added to <see cref="Supplier"/> or <see cref="Product"/> tomorrow is audited the day it
/// is added. A hand-maintained include-list is the failure mode this closes: it silently stops
/// covering the newest — and therefore least-reviewed — fields.</para>
/// </summary>
internal sealed class MasterDataEntityDescriptor
{
    private MasterDataEntityDescriptor(
        string entityType,
        Func<object, long?> tenantId,
        Func<object, long> key,
        Func<object, string?> label,
        Func<object, string?> createdBy,
        Func<object, string?> modifiedBy,
        IEnumerable<string> excluded)
    {
        EntityType = entityType;
        TenantId = tenantId;
        Key = key;
        Label = label;
        CreatedBy = createdBy;
        ModifiedBy = modifiedBy;
        _excluded = excluded.ToFrozenSet(StringComparer.Ordinal);
    }

    public string EntityType { get; }
    public Func<object, long?> TenantId { get; }
    public Func<object, long> Key { get; }
    public Func<object, string?> Label { get; }
    public Func<object, string?> CreatedBy { get; }
    public Func<object, string?> ModifiedBy { get; }

    private readonly FrozenSet<string> _excluded;

    public bool IsExcluded(string propertyName) => _excluded.Contains(propertyName);

    /// <summary>
    /// Columns that carry no reviewable change.
    ///
    /// <para><c>Id</c> and <c>Buid</c> are the audit row's own <c>EntityId</c> and
    /// <c>BusinessUnitId</c>. <c>CreatedBy/CreatedOn/ModifiedBy/ModifiedOn</c> are attribution, and
    /// the audit row carries stronger attribution than they do — recording them as "changes" would
    /// add two noise rows to every single edit. <c>ConcurrencyToken</c> is an optimistic-locking
    /// artefact that changes on every write by definition.</para>
    /// </summary>
    private static readonly string[] CommonExclusions =
    [
        "Id", "Buid", "CreatedBy", "CreatedOn", "ModifiedBy", "ModifiedOn", "ConcurrencyToken"
    ];

    private static readonly MasterDataEntityDescriptor CustomerDescriptor = new(
        MasterDataEntityTypes.Customer,
        entity => ((Customer)entity).Buid,
        entity => ((Customer)entity).Id,
        entity => ((Customer)entity).Name,
        entity => ((Customer)entity).CreatedBy,
        entity => ((Customer)entity).ModifiedBy,
        CommonExclusions);

    private static readonly MasterDataEntityDescriptor SupplierDescriptor = new(
        MasterDataEntityTypes.Supplier,
        entity => ((Supplier)entity).Buid,
        entity => ((Supplier)entity).Id,
        entity => ((Supplier)entity).Name,
        entity => ((Supplier)entity).CreatedBy,
        entity => ((Supplier)entity).ModifiedBy,
        CommonExclusions);

    private static readonly MasterDataEntityDescriptor ProductDescriptor = new(
        MasterDataEntityTypes.Product,
        entity => ((Product)entity).Buid,
        entity => ((Product)entity).Id,
        entity => ((Product)entity).PartNo,
        entity => ((Product)entity).CreatedBy,
        entity => ((Product)entity).ModifiedBy,
        [
            .. CommonExclusions,
            // Stock is not master data and it is not this trail's business. Product.QtyOnHand is a
            // legacy denormalised column; the authoritative balance lives in Inventory and every
            // movement against it is already recorded in InventoryMovements. Auditing it here
            // would push thousands of stock-movement rows into the master-data trail and drown the
            // handful of cost and price edits the trail exists to make visible.
            "QtyOnHand"
        ]);

    /// <summary>The descriptor for a tracked entity, or null when the entity is not governed
    /// master data. One type check per tracked entry — this is the interceptor's fast path.</summary>
    public static MasterDataEntityDescriptor? For(object entity) => entity switch
    {
        Product => ProductDescriptor,
        Customer => CustomerDescriptor,
        Supplier => SupplierDescriptor,
        _ => null
    };

    // ------------------------------------------------------------------ classification

    private static readonly FrozenSet<string> CommercialFields = new[]
    {
        // Product — FinalLandedCost above all: it is the cost basis reported margin is computed
        // from, it is hand-editable on the product screen and through column 28 of the import
        // sheet, and until E44 it moved with no record of who moved it or why.
        "UnitCost", "SellingPrice", "FinalLandedCost", "FinalSalesPrice",
        // Supplier — the terms that decide when money leaves.
        "PaymentTerms"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> PersonalFields = new[]
    {
        "ContactEmail",
        "AddressLine1", "AddressLine2", "PostalCode",
        "BillingAddressLine1", "BillingAddressLine2", "BillingCity", "BillingState",
        "BillingCountry", "BillingPostalCode",
        "ShippingAddressLine1", "ShippingAddressLine2", "ShippingCity", "ShippingState",
        "ShippingCountry", "ShippingPostalCode"
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Labels a field <c>COMMERCIAL</c>, <c>PERSONAL</c> or neither.
    ///
    /// <para><b>Both values are recorded in full, including the before value.</b> That is a
    /// deliberate decision, not an oversight. Redacting the previous landed cost or the previous
    /// payment terms would leave a trail that cannot answer the single question it was built for —
    /// "what was this before somebody changed it" — and the audit reader is already behind the
    /// same module permission as the record itself, so it discloses nothing the caller could not
    /// read from the record directly.</para>
    ///
    /// <para>Nothing on these three entities is a SECRET (no password, token or key column), so no
    /// field is withheld. If one is ever added it must be excluded outright rather than classified,
    /// because a rotated credential's previous value in an append-only table is a credential that
    /// cannot be revoked. <c>Email_Configurations.Password</c> is the existing example of a column
    /// that would qualify, and it is not master data.</para>
    ///
    /// <para>The heuristic tail is what keeps the classification honest as columns are added: a
    /// credit limit or a discount column added next quarter is labelled without anyone remembering
    /// to come back here.</para>
    /// </summary>
    public static string? ClassifyField(string propertyName)
    {
        if (CommercialFields.Contains(propertyName)) return MasterDataSensitivity.Commercial;
        if (PersonalFields.Contains(propertyName)) return MasterDataSensitivity.Personal;

        if (propertyName.EndsWith("Cost", StringComparison.Ordinal)
            || propertyName.EndsWith("Price", StringComparison.Ordinal)
            || propertyName.EndsWith("Margin", StringComparison.Ordinal)
            || propertyName.EndsWith("Discount", StringComparison.Ordinal)
            || propertyName.Contains("CreditLimit", StringComparison.Ordinal)
            || propertyName.Contains("PaymentTerm", StringComparison.Ordinal))
            return MasterDataSensitivity.Commercial;

        if (propertyName.Contains("Email", StringComparison.Ordinal)
            || propertyName.Contains("Phone", StringComparison.Ordinal)
            || propertyName.Contains("Mobile", StringComparison.Ordinal)
            || propertyName.Contains("AddressLine", StringComparison.Ordinal)
            || propertyName.Contains("PostalCode", StringComparison.Ordinal))
            return MasterDataSensitivity.Personal;

        return null;
    }
}
