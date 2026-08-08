namespace ERP_RFQ_Automation.Platform.Lifecycle;

/// <summary>
/// Whose record a platform-schema table holds. Every table an offboarding can reach must answer
/// this, because the two answers have opposite destinies under a purge.
/// </summary>
public enum TenantDataClass
{
    /// <summary>
    /// The CUSTOMER's record. Destroyed by a purge. If the customer would recognise the contents
    /// as theirs — what they submitted, who they invited, what the platform holds because they
    /// asked for it — it belongs here.
    /// </summary>
    CustomerRecord,

    /// <summary>
    /// The OPERATOR's record OF the customer. Survives a purge. What we did, when, why, and what
    /// we charged for it: the audit trail, the lifecycle history, the export receipts, the support
    /// threads, the impersonation sessions, the revenue books. Destroying these does not protect a
    /// customer's privacy; it destroys the evidence of how they were treated.
    /// </summary>
    OperatorRecord
}

/// <summary>How a table's rows are traced back to a tenant.</summary>
/// <param name="ForeignKeyColumn">The column on THIS table pointing at the parent.</param>
/// <param name="ParentTable">The platform table it points at.</param>
/// <param name="ParentKeyColumn">The parent's key column.</param>
public sealed record PlatformTenantParent(
    string ForeignKeyColumn, string ParentTable, string ParentKeyColumn);

/// <summary>One classified platform-schema table.</summary>
/// <param name="Table">Table name within the <c>platform</c> schema.</param>
/// <param name="Classification">Destroy on purge, or preserve.</param>
/// <param name="Reason">Why. Read by whoever adds the next table and has to make the same call.</param>
/// <param name="TenantColumn">The tenant id column, when the table carries one directly.</param>
/// <param name="ReachedThrough">The parent chain, when it does not.</param>
public sealed record PlatformTenantTable(
    string Table,
    TenantDataClass Classification,
    string Reason,
    string? TenantColumn = null,
    PlatformTenantParent? ReachedThrough = null)
{
    /// <summary>Rows traced through a parent rather than a tenant column of their own — the shape
    /// a catalogue sweep for "columns named TenantId" structurally cannot find.</summary>
    public bool IsIndirect => ReachedThrough is not null;
}

/// <summary>
/// The declared destiny of every platform-schema table an offboarding can reach.
///
/// <para><b>Why this file exists, stated plainly.</b> The purge used to discover its platform-plane
/// work by sweeping the catalogue for columns named <c>TenantId</c>. That rule has two failure
/// modes and both were live defects. A table with a tenant column that should have been KEPT was
/// destroyed — <c>ImpersonationSessions</c>, the record of operators signing into a customer's
/// account, deleted while the support ticket linking to it survived and left a dangling reference
/// inside the evidence the purge went out of its way to protect. And a table WITHOUT a tenant
/// column that should have been destroyed survived — <c>ProvisioningSteps</c> and
/// <c>ProvisioningDrafts</c>, which reach a tenant only through <c>ProvisioningExecutions</c>.
/// <c>ProvisioningDrafts</c> in particular kept the founding administrator's email address in its
/// <c>Payload</c> through both a purge and an Article 17 erasure.</para>
///
/// <para><b>Why a foreign key did not save the orphans.</b> <c>ProvisioningSteps</c> cascades from
/// <c>ProvisioningExecutions</c>, so deleting the execution should have taken the steps with it.
/// It does not, because the purge runs under <c>session_replication_role = 'replica'</c> to
/// suspend the ~30 append-only guards — and <c>ON DELETE CASCADE</c> is itself a foreign-key
/// trigger, so it is suspended along with them. The purge's own comment relies on that suppression
/// to make deletion order irrelevant; the cost is that referential cleanup must be written out by
/// hand. That is what <see cref="PlatformTenantParent"/> is.</para>
///
/// <para><b>The invariant.</b> Every table reachable from a tenant must appear here.
/// <c>TenantLifecyclePlatformTableClassificationTests</c> computes reachability from the live
/// catalogue — a <c>TenantId</c> column, or a foreign key to something reachable, transitively —
/// and fails on anything unclassified. A new platform table cannot join the schema without
/// somebody deciding whose record it is.</para>
/// </summary>
public static class PlatformTenantDataMap
{
    public const string Schema = "platform";

    public static readonly IReadOnlyList<PlatformTenantTable> Tables =
    [
        // ==== the operator's record of the customer — survives ===============================

        new("Tenants", TenantDataClass.OperatorRecord,
            "The tombstone. Kept so the audit trail, the lifecycle history and the export "
            + "receipts point at something. Its personal-contact columns are the erasure "
            + "operation's business, not the purge's.",
            TenantColumn: "Id"),

        new("PlatformAuditLogs", TenantDataClass.OperatorRecord,
            "Every privileged action, including the purge currently running. An audit trail a "
            + "purge can erase cannot evidence a purge.",
            TenantColumn: "ActAsTenantId"),

        new("ImpersonationSessions", TenantDataClass.OperatorRecord,
            "The record of a platform operator signing INTO this customer's account: who, when, "
            + "for how long, under what stated reason, and whether it was revoked. That is "
            + "evidence about the OPERATOR, of exactly the same class as PlatformAuditLogs and "
            + "SupportTickets. Destroying it deletes the customer's protection, not their data — "
            + "and it strands the surviving SupportTicketLinks rows of kind ImpersonationSession, "
            + "whose TargetKey names a jti that would no longer exist.",
            TenantColumn: "TenantId"),

        new("SupportTickets", TenantDataClass.OperatorRecord,
            "What was promised and escalated. The CUSTOMER's words inside the thread are erased "
            + "separately by ISupportTicketRedactionService, which the purge calls; the ticket "
            + "itself is the operator's record and stays.",
            TenantColumn: "TenantId"),

        new("SupportTicketNotes", TenantDataClass.OperatorRecord,
            "Thread entries. Erased in place by the redaction hook rather than deleted, so the "
            + "ticket keeps a countable history of what was removed.",
            ReachedThrough: new PlatformTenantParent("SupportTicketId", "SupportTickets", "Id")),

        new("SupportTicketLinks", TenantDataClass.OperatorRecord,
            "What a ticket pointed at — including the impersonation sessions above, which is why "
            + "those must survive too.",
            ReachedThrough: new PlatformTenantParent("SupportTicketId", "SupportTickets", "Id")),

        new("BillingStatements", TenantDataClass.OperatorRecord,
            "The operator's revenue books. Statutory retention outlives the customer, they are "
            + "already immutable at database level, and they evidence the operator's income "
            + "rather than the customer's business.",
            TenantColumn: "TenantId"),

        new("BillingStatementLines", TenantDataClass.OperatorRecord,
            "The detail behind a statement. Kept for the same reason and by the same obligation.",
            ReachedThrough: new PlatformTenantParent("BillingStatementId", "BillingStatements", "Id")),

        new("SubscriptionInvoices", TenantDataClass.OperatorRecord,
            "Nexora subscription invoices are immutable operator revenue and tax evidence retained "
            + "for the applicable statutory period.", TenantColumn: "TenantId"),

        new("SubscriptionCreditNotes", TenantDataClass.OperatorRecord,
            "Governed corrections to preserved subscription invoices.",
            ReachedThrough: new PlatformTenantParent("SubscriptionInvoiceId", "SubscriptionInvoices", "Id")),

        new("SubscriptionPayments", TenantDataClass.OperatorRecord,
            "External payment and collection evidence for preserved subscription invoices.",
            ReachedThrough: new PlatformTenantParent("SubscriptionInvoiceId", "SubscriptionInvoices", "Id")),

        new("TenantOffboardings", TenantDataClass.OperatorRecord,
            "The record that the purge completed, written after it runs. It cannot be destroyed "
            + "by the operation it records.",
            TenantColumn: "TenantId"),

        new("TenantLifecycleEvents", TenantDataClass.OperatorRecord,
            "Who decided this customer would be destroyed, when, and why. The single record that "
            + "outlives them.",
            TenantColumn: "TenantId"),

        new("TenantExportReceipts", TenantDataClass.OperatorRecord,
            "Proof the data was handed back before it was destroyed. Deleting it turns a "
            + "compliant offboarding into an unevidenced one.",
            TenantColumn: "TenantId"),

        new("TenantLegalHolds", TenantDataClass.OperatorRecord,
            "The legal authority and evidence that prevented deletion. A purge must preserve the "
            + "record proving why it was refused and who later released it.",
            TenantColumn: "TenantId"),

        new("TenantDataAssets", TenantDataClass.OperatorRecord,
            "The non-secret inventory and verification evidence for the customer's data boundary. "
            + "It must survive to prove where data lived, which backup policy governed it and why "
            + "the activation data gate passed; live data and credentials are never stored here.",
            TenantColumn: "TenantId"),

        // ==== the customer's record — destroyed ==============================================

        new("TenantAdminInvitations", TenantDataClass.CustomerRecord,
            "Carries the founding administrator's email address and the IP they redeemed from. "
            + "Both are the customer's personal data.",
            TenantColumn: "TenantId"),

        new("ProvisioningExecutions", TenantDataClass.CustomerRecord,
            "Holds the whole submitted provisioning request as jsonb — company identity, the "
            + "founding administrator's address — plus that administrator's password hash. The "
            + "operator's account of the attempt survives in PlatformAuditLogs.",
            TenantColumn: "TenantId"),

        new("ProvisioningSteps", TenantDataClass.CustomerRecord,
            "Per-step detail of that request, including the jsonb the steps recorded. Reached "
            + "only through its execution: it carries no tenant column, and its ON DELETE CASCADE "
            + "does not fire under the purge's replica mode.",
            ReachedThrough: new PlatformTenantParent("ExecutionId", "ProvisioningExecutions", "Id")),

        new("ProvisioningDrafts", TenantDataClass.CustomerRecord,
            "A submitted draft holds the full request payload — legal name, address, the founding "
            + "administrator's email — and reaches a tenant only through SubmittedExecutionId. A "
            + "draft that was never submitted has no tenant and is the drafting operator's own "
            + "work in progress, so it is deliberately out of reach of any tenant's purge.",
            ReachedThrough: new PlatformTenantParent(
                "SubmittedExecutionId", "ProvisioningExecutions", "Id"))
    ];

    public static IEnumerable<PlatformTenantTable> Destroyed =>
        Tables.Where(t => t.Classification == TenantDataClass.CustomerRecord);

    public static IEnumerable<PlatformTenantTable> Preserved =>
        Tables.Where(t => t.Classification == TenantDataClass.OperatorRecord);

    public static PlatformTenantTable? Find(string table) =>
        Tables.FirstOrDefault(t => string.Equals(t.Table, table, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How many parents sit between a table and the tenant. Deletion runs deepest-first: a child
    /// is selected through a subquery on its parent, so deleting the parent first would make the
    /// subquery match nothing and silently leave every child behind — the exact shape of the
    /// orphaned-steps defect, re-created by ordering instead of by omission.
    /// </summary>
    public static int Depth(PlatformTenantTable table)
    {
        var depth = 0;
        var current = table;
        while (current.ReachedThrough is PlatformTenantParent parent)
        {
            depth++;
            var next = Find(parent.ParentTable);
            if (next is null || ReferenceEquals(next, current)) break;
            current = next;
        }
        return depth;
    }
}
