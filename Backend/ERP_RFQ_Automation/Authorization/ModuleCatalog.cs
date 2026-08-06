namespace ERP_RFQ_Automation.Authorization;

/// <summary>
/// The complete set of permission modules this system enforces, with the plain-language
/// descriptions shown on the Roles &amp; Permissions matrix.
///
/// <para><b>The problem this solves.</b> <c>[RequireModulePermission("X", …)]</c> resolves by
/// joining <c>RolePermissions</c> to a <c>Module</c> row whose <c>ModuleName</c> matches exactly,
/// and <c>RolePermissionRepository</c> DENIES when no row matches. So a gated endpoint whose
/// module has never been inserted is not "ungoverned" — it is permanently forbidden to every
/// non-super-admin, with no error that names the cause. Modules were only ever inserted ad hoc by
/// whichever feature migration happened to remember, which is why most of the product could only
/// be used by a super admin.</para>
///
/// <para><b>Why a code catalogue rather than a one-off seed script.</b> A seed fixes the rows that
/// exist on the day it runs. This list is asserted against the assembly by
/// <c>ModuleCatalogTests</c>: every name passed to <c>RequireModulePermission</c> anywhere in the
/// codebase must appear here, AND every entry here must be enforced somewhere. A new gated
/// endpoint with a typo'd or unseeded module name fails the build rather than shipping a silently
/// unreachable screen, and a module that stops being enforced cannot linger as a checkbox that
/// grants nothing.</para>
///
/// <para><b>Deliberately absent:</b> Teams, User Groups, File Management, Bulk Uploaders and
/// Contacts. Nothing gates them, so a checkbox for them would revoke nothing while implying it
/// did — worse than having no checkbox at all. They belong here the moment an endpoint enforces
/// them, and the test above is what will require it.</para>
/// </summary>
public static class ModuleCatalog
{
    public sealed record ModuleDefinition(string Name, string Description);

    /// <summary>
    /// Grouping is presentation only — <c>Module</c> has no category column. It exists so the
    /// permissions matrix can be read by a human rather than as 41 alphabetical checkboxes.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<ModuleDefinition>> ByArea =
        new Dictionary<string, IReadOnlyList<ModuleDefinition>>
        {
            ["Sales pipeline"] =
            [
                new("Dashboard", "Home dashboard and summary figures"),
                new("Leads", "Incoming customer enquiries and their triage"),
                new("RFQ Management", "Requests for quotation raised from leads"),
                new("Quotations", "Customer quotes, pricing and approval"),
                new("Quote Configuration", "Quote templates, numbering and document layout"),
                new("Orders", "Confirmed customer orders"),
                new("Shipments", "Despatch and delivery of orders"),
            ],

            ["Customers and suppliers"] =
            [
                new("Customers", "Customer accounts and contacts"),
                new("Customer Awards", "Awards and contract wins recorded against customers"),
                new("Suppliers", "Supplier accounts and contacts"),
                new("Supplier History", "Past supplier quotes, performance and correspondence"),
                new("Supplier Negotiation", "Negotiation rounds and counter-offers with suppliers"),
            ],

            ["Products and inventory"] =
            [
                new("Products", "Product catalogue, stock and availability"),
                new("Product Categories", "Product classification and grouping"),
            ],

            ["Administration"] =
            [
                new("Users", "User accounts for this business unit"),
                new("Roles & Permissions", "Roles and what each role may do"),
                new("Business Units", "Business unit setup and details"),
                new("Email & SMTP", "Mailboxes leads are read from and quotes are sent through"),
            ],

            ["Receivables"] =
            [
                new("Accounts Receivable", "Governed invoices and accounts receivable"),
                new("Customer Payments", "Governed customer receipts and reversals"),
                new("Customer Refunds", "Refunds issued back to customers"),
                new("Customer Statements", "Statements of account issued to customers"),
                new("Receivable Adjustments", "Adjustments raised against receivable balances"),
                new("Receivable Write-offs", "Write-off of uncollectable receivable balances"),
                new("Collection Controls", "Credit holds and collection policy controls"),
            ],

            ["Collections"] =
            [
                new("Dunning Cases", "Individual collection cases being pursued"),
                new("Dunning Notices", "Reminder notices issued on overdue balances"),
                new("Dunning Policies", "Rules governing when and how reminders are issued"),
            ],

            ["Banking"] =
            [
                new("Bank Accounts", "Bank accounts held by the business unit"),
                new("Bank Statement Import", "Import of bank statements for reconciliation"),
                new("Bank Reconciliation", "Matching bank lines to ledger entries"),
                new("Bank Reconciliation Approval", "Approval of completed bank reconciliations"),
                new("Bank Adjustments", "Adjustments raised during bank reconciliation"),
                new("Bank Adjustment Approval", "Approval of bank reconciliation adjustments"),
                new("Bank Matching Rule Administration", "Creation and editing of automatic matching rules"),
                new("Bank Matching Rule Approval", "Approval of automatic matching rules before use"),
            ],

            ["General ledger"] =
            [
                new("General Ledger", "Chart of accounts and ledger enquiry"),
                new("General Ledger Posting", "Posting journals to the general ledger"),
                new("Ledger Control", "Control accounts and ledger integrity settings"),
                new("Accounting Periods", "Accounting period definition"),
                new("Period Close", "Closing an accounting period"),
            ],
        };

    /// <summary>Every module, flattened. Ordering is stable so the seed migration and the matrix
    /// agree.</summary>
    public static readonly IReadOnlyList<ModuleDefinition> All =
        ByArea.SelectMany(area => area.Value).ToList();

    public static readonly IReadOnlySet<string> Names =
        All.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

    /// <summary>Attribution stamp written to <c>Module.CreatedBy</c> by the seeding migration, so
    /// catalogue rows are distinguishable from anything hand-inserted.</summary>
    public const string SeedActor = "migration:module-catalogue:v1";
}
