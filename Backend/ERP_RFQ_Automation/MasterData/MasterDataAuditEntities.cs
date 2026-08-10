using System;
using System.Collections.Generic;

namespace ERP_RFQ_Automation.MasterData;

/// <summary>
/// FR-MDM-05 · "audit every change with before/after values" for the BRD's master data —
/// <c>Customer</c>, <c>Supplier</c> and <c>Product</c>. Register item E44.
///
/// <para><b>Why another table, and why this is not a fifteenth silo.</b> Fourteen per-subsystem
/// append-only event tables already exist (<c>ProcurementEvents</c>,
/// <c>CommercialFinanceAudits</c>, <c>OrderToCashAuditEvents</c>, <c>IamAuditEvents</c>,
/// <c>CommercialLifecycleEvents</c>, …). Every one of them is a COMMAND log: it records that an
/// aggregate was told to do something, with a payload blob. Exactly ONE existing table records a
/// FIELD-LEVEL before/after — <see cref="ERP_RFQ_Automation.LeadIdentity.LeadRevisionDifference"/>,
/// which hangs <c>Path / PreviousValueJson / CurrentValueJson</c> rows off a
/// <c>LeadRevision</c> header. That header-plus-difference shape is what this pair copies, because
/// it is the only shape in the codebase that can answer "which field moved, from what, to what".
/// The names, the tenant column, the append-only trigger and the RLS/GRANT pairing are taken from
/// <c>IamAuditEvents</c>, which is the closest table by PURPOSE (a governed record of who changed
/// a governed thing) and already carries before/after.</para>
///
/// <para>It is deliberately NOT written into <c>IamAuditEvents</c>. That table is
/// identity-and-access evidence: it is exported for SOC 2 CC6.2/CC6.3 and it is read under IAM
/// authority. A product's landed cost is commercial master data read under the Products module
/// permission. Merging them would either widen who can read privilege changes or narrow who can
/// read cost changes, and it would pollute an access-control export with catalogue edits.</para>
///
/// <para><b>Append-only.</b> Enforced at the database by a trigger
/// (<c>trg_master_data_audit_append_only</c>, same function shape as
/// <c>trg_commercial_finance_audit_append_only</c>) and in the application by
/// <see cref="MasterDataAuditInterceptor.ValidateAppendOnly"/>. The trigger is the control; the
/// interceptor exists so the portable SQLite suite fails on the same mistake.</para>
///
/// <para><b>No foreign key to the audited row.</b> Same reasoning as
/// <see cref="ERP_RFQ_Automation.Models.IamAuditEvent"/>: the record of "who deleted this product"
/// must outlive the product it names, so <see cref="EntityId"/> carries no FK. Only
/// <see cref="BusinessUnitId"/> does, because a tenant-less audit row is unattributable and would
/// sit outside the RLS policy's reach.</para>
/// </summary>
public sealed class MasterDataChangeEvent
{
    public long Id { get; set; }

    /// <summary>Tenant that owns the audited record. Stamped from the row itself, never a request body.</summary>
    public long BusinessUnitId { get; set; }

    /// <summary>One of <see cref="MasterDataEntityTypes"/>.</summary>
    public string EntityType { get; set; } = null!;

    /// <summary>Primary key of the audited row. No FK — see the type remarks.</summary>
    public long EntityId { get; set; }

    /// <summary>
    /// Human-readable identity captured AT WRITE TIME (customer/supplier name, product part
    /// number). A later rename must not rewrite what the trail says was changed, and a deleted
    /// record still has to be nameable in the trail that records its deletion.
    /// </summary>
    public string? EntityLabel { get; set; }

    /// <summary>One of <see cref="MasterDataChangeTypes"/>.</summary>
    public string ChangeType { get; set; } = null!;

    /// <summary>Authenticated user id from the validated token; null for system/import-without-identity writes.</summary>
    public long? ActorUserId { get; set; }

    /// <summary>The role the actor held AT THE TIME, so a later role rename cannot rewrite history.</summary>
    public long? ActorRoleId { get; set; }

    /// <summary>Display stamp for the actor. Server-derived; falls back to the row's own
    /// CreatedBy/ModifiedBy stamp and finally to "system".</summary>
    public string Actor { get; set; } = null!;

    /// <summary>One of <see cref="MasterDataChangeSources"/> — an API edit and a spreadsheet
    /// import are different review problems and must be separable in one query.</summary>
    public string ChangeSource { get; set; } = null!;

    public string? CorrelationId { get; set; }

    /// <summary>Free-text justification when a write path supplies one. Nothing invents one.</summary>
    public string? Reason { get; set; }

    /// <summary>Number of <see cref="MasterDataFieldChange"/> rows, denormalised so a history
    /// list can be rendered without joining.</summary>
    public int FieldCount { get; set; }

    public DateTime OccurredOn { get; set; }

    public ICollection<MasterDataFieldChange> Fields { get; } = new List<MasterDataFieldChange>();
}

/// <summary>
/// One field that moved, with its before and after value. This is the row FR-MDM-05 is actually
/// about: a reviewer asking "who changed this product's landed cost, and what was it before?"
/// answers it with <c>WHERE "FieldName" = 'FinalLandedCost'</c> and no JSON parsing.
/// </summary>
public sealed class MasterDataFieldChange
{
    public long Id { get; set; }

    public long BusinessUnitId { get; set; }

    public long ChangeEventId { get; set; }

    /// <summary>CLR property name as mapped by EF — stable, and the name the UI shows.</summary>
    public string FieldName { get; set; } = null!;

    /// <summary>Invariant-culture rendering of the original value; null when the field had none.</summary>
    public string? BeforeValue { get; set; }

    /// <summary>Invariant-culture rendering of the new value; null when the field was cleared.</summary>
    public string? AfterValue { get; set; }

    /// <summary>
    /// <see cref="MasterDataSensitivity"/> classification, or null for an ordinary field.
    ///
    /// <para>Values are recorded IN FULL for both classes, including the before value. Redacting
    /// the old landed cost would leave a trail that cannot answer the one question it exists for.
    /// The column exists so that (a) a reviewer can filter straight to the commercially loaded
    /// fields, and (b) a future PDPL erasure can find the personal-data rows to null without
    /// deleting the audit row that proves the change happened.</para>
    /// </summary>
    public string? Sensitivity { get; set; }

    public MasterDataChangeEvent ChangeEvent { get; set; } = null!;
}

/// <summary>Master-data types under FR-MDM-05. String constants, not an enum, because the column
/// is queried directly and must stay readable in SQL.</summary>
public static class MasterDataEntityTypes
{
    public const string Customer = "Customer";
    public const string Supplier = "Supplier";
    public const string Product = "Product";

    public static readonly string[] All = [Customer, Supplier, Product];

    /// <summary>Case-insensitive resolution for route values; null when the caller named
    /// something that is not governed master data.</summary>
    public static string? Resolve(string? value)
    {
        foreach (var candidate in All)
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase))
                return candidate;
        return null;
    }
}

public static class MasterDataChangeTypes
{
    public const string Created = "CREATED";
    public const string Updated = "UPDATED";
    public const string Deleted = "DELETED";
}

/// <summary>
/// How the change arrived. A bulk spreadsheet edit and a single screen edit carry very different
/// review weight, and the audit is worth much less if they cannot be told apart.
/// </summary>
public static class MasterDataChangeSources
{
    /// <summary>An authenticated HTTP request against a master-data endpoint.</summary>
    public const string Api = "API";

    /// <summary>A spreadsheet upload — <c>ProductUploaderService</c> and its siblings.</summary>
    public const string Import = "IMPORT";

    /// <summary>A background worker, migration harness or any save with no ambient actor.</summary>
    public const string System = "SYSTEM";
}

public static class MasterDataSensitivity
{
    /// <summary>Cost, price and payment-terms fields — the numbers reported margin is computed from.</summary>
    public const string Commercial = "COMMERCIAL";

    /// <summary>Personal data under PDPL: contact email, postal addresses.</summary>
    public const string Personal = "PERSONAL";
}
