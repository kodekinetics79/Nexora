namespace ERP_RFQ_Automation.Models;

// AA-01 · tenant-defined custom fields.
//
// Storage decision (CTO, not open for redesign): ONE jsonb value bag per owning row plus a
// tenant-scoped definition table. No per-tenant DDL and no real column per custom field —
// per-tenant DDL would fork the migration story and make the row-level-security model
// unmaintainable, because every tenant's table would need its own policy re-derived.
//
// The bag is a flat JSON object keyed by the definition's stable key:
//   {"vendor_code": "TC-9910", "framework_expiry": "2027-03-31", "is_strategic": true}
// Values are validated against the definition's declared data type on every write by
// CustomFields/CustomFieldBagValidator. Nothing writes to this column directly.
//
// These are partial extensions of database-scaffolded classes, so a re-scaffold cannot wipe
// them — the same convention as LeadItem.Extra.cs and ErpRfqAutomationContext.Tenancy.cs.

public partial class Customer
{
    /// <summary>
    /// Tenant-defined custom field values for this customer, as a jsonb object keyed by
    /// custom-field stable key. Null when the tenant has defined none or set none.
    /// Column type is configured in ErpRfqAutomationContext.Tenancy.OnModelCreatingPartial.
    /// </summary>
    public string? CustomFieldsJson { get; set; }
}

public partial class Supplier
{
    /// <inheritdoc cref="Customer.CustomFieldsJson"/>
    public string? CustomFieldsJson { get; set; }
}

public partial class LeadItem
{
    /// <summary>
    /// Tenant-defined custom field values for this lead/RFQ line, as a jsonb object keyed by
    /// custom-field stable key.
    ///
    /// Deliberately distinct from <see cref="ExtraFields"/>: ExtraFields is a verbatim,
    /// UNGOVERNED capture of whatever column headings a customer's document happened to
    /// contain, keyed by the raw header text. This bag holds values against fields the tenant
    /// has explicitly defined, typed and named. One is evidence, the other is a schema.
    /// </summary>
    public string? CustomFieldsJson { get; set; }
}
