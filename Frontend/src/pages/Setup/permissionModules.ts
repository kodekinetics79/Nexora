/**
 * The permission modules the server ACTUALLY enforces — mirrored from
 * `Backend/ERP_RFQ_Automation/Authorization/ModuleCatalog.cs`.
 *
 * <b>Why this list exists.</b> The `Module` table is insert-only: `ModuleCatalogReconciler` adds
 * rows and never removes them, so every module ever inserted by any feature migration is still
 * there and still arrives from `GET /api/Module`. Nine of them are enforced by nothing at all —
 * Bulk Uploaders, Contacts, Currency, File Management, Locations, Teams, UOM, User Groups and
 * Warehouse carry no `[RequireModulePermission]` anywhere in the backend. Rendering a checkbox for
 * those is worse than rendering nothing: ticking it grants no access and un-ticking it revokes
 * none, while the screen states in four columns that it did both. ModuleCatalog.cs says so itself
 * — "worse than having no checkbox at all".
 *
 * The rows are deliberately NOT deleted. 629 gated endpoints resolve through them and
 * `ModuleCatalogTests` asserts the catalogue's integrity; deleting rows would break the join that
 * makes a real grant work. This list changes only what is PRESENTED.
 *
 * <b>Drift.</b> `permissionModules.test.ts` parses ModuleCatalog.cs and fails if this file and it
 * disagree, so the .cs stays the single authority and a module added or removed there cannot
 * silently leave this list stale.
 */

export interface EnforcedModule {
  name: string;
  description: string;
}

/** Every module with real server-side enforcement, in the backend catalogue's own order. */
export const ENFORCED_MODULES: readonly EnforcedModule[] = [
  // Sales pipeline
  { name: 'Dashboard', description: 'Home dashboard and summary figures' },
  { name: 'Leads', description: 'Incoming customer enquiries and their triage' },
  { name: 'RFQ Management', description: 'Requests for quotation raised from leads' },
  { name: 'Quotations', description: 'Customer quotes, pricing and approval' },
  { name: 'Quote Configuration', description: 'Quote templates, numbering and document layout' },
  { name: 'Orders', description: 'Confirmed customer orders' },
  { name: 'Shipments', description: 'Despatch and delivery of orders' },
  // Customers and suppliers
  { name: 'Customers', description: 'Customer accounts and contacts' },
  { name: 'Customer Awards', description: 'Awards and contract wins recorded against customers' },
  { name: 'Suppliers', description: 'Supplier accounts and contacts' },
  { name: 'Supplier History', description: 'Past supplier quotes, performance and correspondence' },
  { name: 'Supplier Negotiation', description: 'Negotiation rounds and counter-offers with suppliers' },
  // Products and inventory
  { name: 'Products', description: 'Product catalogue, stock and availability' },
  { name: 'Product Categories', description: 'Product classification and grouping' },
  // Administration
  { name: 'Users', description: 'User accounts for this business unit' },
  { name: 'Roles & Permissions', description: 'Roles and what each role may do' },
  { name: 'Business Units', description: 'Business unit setup and details' },
  { name: 'Email & SMTP', description: 'Mailboxes leads are read from and quotes are sent through' },
  // Currency and exchange rates
  { name: 'Currencies', description: 'Currencies this business unit trades in, and their symbols' },
  { name: 'Exchange Rates', description: 'Effective-dated conversion rates, and the rate frozen onto each document' },
  { name: 'Exchange Rate Approval', description: 'Approving a rate so that quotes and orders convert at it' },
  // Receivables
  { name: 'Accounts Receivable', description: 'Governed invoices and accounts receivable' },
  { name: 'Customer Payments', description: 'Governed customer receipts and reversals' },
  { name: 'Customer Refunds', description: 'Refunds issued back to customers' },
  { name: 'Customer Statements', description: 'Statements of account issued to customers' },
  { name: 'Receivable Adjustments', description: 'Adjustments raised against receivable balances' },
  { name: 'Receivable Write-offs', description: 'Write-off of uncollectable receivable balances' },
  { name: 'Collection Controls', description: 'Credit holds and collection policy controls' },
  // Collections
  { name: 'Dunning Cases', description: 'Individual collection cases being pursued' },
  { name: 'Dunning Notices', description: 'Reminder notices issued on overdue balances' },
  { name: 'Dunning Policies', description: 'Rules governing when and how reminders are issued' },
  // Banking
  { name: 'Bank Accounts', description: 'Bank accounts held by the business unit' },
  { name: 'Bank Statement Import', description: 'Import of bank statements for reconciliation' },
  { name: 'Bank Reconciliation', description: 'Matching bank lines to ledger entries' },
  { name: 'Bank Reconciliation Approval', description: 'Approval of completed bank reconciliations' },
  { name: 'Bank Adjustments', description: 'Adjustments raised during bank reconciliation' },
  { name: 'Bank Adjustment Approval', description: 'Approval of bank reconciliation adjustments' },
  { name: 'Bank Matching Rule Administration', description: 'Creation and editing of automatic matching rules' },
  { name: 'Bank Matching Rule Approval', description: 'Approval of automatic matching rules before use' },
  // General ledger
  { name: 'General Ledger', description: 'Chart of accounts and ledger enquiry' },
  { name: 'General Ledger Posting', description: 'Posting journals to the general ledger' },
  { name: 'Ledger Control', description: 'Control accounts and ledger integrity settings' },
  { name: 'Accounting Periods', description: 'Accounting period definition' },
  { name: 'Period Close', description: 'Closing an accounting period' },
];

/** Fast membership test for the matrix filter and the setup-catalogue gate check. */
export const ENFORCED_MODULE_NAMES: ReadonlySet<string> =
  new Set(ENFORCED_MODULES.map((module) => module.name));

/**
 * Does this module name grant anything?
 *
 * Compared case-insensitively and trimmed, matching `AuthContext.hasPermission` and the server's
 * own tolerance for the inconsistently-cased rows in live data.
 */
const NORMALISED: ReadonlySet<string> =
  new Set(ENFORCED_MODULES.map((module) => module.name.trim().toLowerCase()));

export const isEnforcedModule = (moduleName: string | null | undefined): boolean =>
  NORMALISED.has((moduleName ?? '').trim().toLowerCase());
