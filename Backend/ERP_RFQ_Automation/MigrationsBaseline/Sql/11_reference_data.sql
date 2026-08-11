-- ==========================================================================
-- Reference data seeded by the pre-baseline migrations.
--
-- `pg_dump --schema-only` cannot see this, and losing it is not cosmetic: the
-- HTTP integration harness resolves the "Supplier Negotiation" module by name
-- and RolePermissions carries a foreign key to Module."ID", so both the row and
-- its id have to survive the squash.
--
-- This is the ONLY table the 134 migrations leave rows in. Verified by counting
-- every table in public and platform on a database built from those migrations:
-- public."Module" (25 rows) and public."__EFMigrationsHistory" (134 rows, which
-- the baseline replaces with its own single row). Everything else is empty.
--
-- Ids are explicit so they match the database the migrations produce; the rows
-- are ordered as the migrations inserted them, and the identity sequence is
-- advanced past them afterwards. ON CONFLICT DO NOTHING mirrors every original
-- INSERT, so re-running against a database that already has the catalogue is a
-- no-op. CreatedOn stays now() - the original migrations used now() too, and
-- freezing a build timestamp into the baseline would be a lie.
--
-- The migrations also carried INSERT ... SELECT backfills into RolePermissions
-- and AiProcessingPolicies. Those select from pre-existing tenant rows, so on a
-- fresh database they insert nothing - which is exactly what they did on the
-- database this baseline was verified against.
-- ==========================================================================
INSERT INTO public."Module" ("ID", "ModuleName", "Description", "IsActive", "CreatedBy", "CreatedOn")
VALUES
    (1, 'Accounts Receivable', 'Governed invoices and accounts receivable', true, 'migration:commercial-finance:v1', now()),
    (2, 'Customer Payments', 'Governed customer receipts and reversals', true, 'migration:commercial-finance:v1', now()),
    (3, 'Customer Awards', 'Governed customer purchase orders, awards, allocations, and conversion', true, 'migration:customer-awards:v1', now()),
    (4, 'Receivable Adjustments', 'Governed credit and debit note creation and approval', true, 'migration:receivable-adjustments:v1', now()),
    (5, 'Receivable Write-offs', 'Governed receivable write-off preparation, posting and reversal', true, 'migration:finance-exceptions:v1', now()),
    (6, 'Customer Refunds', 'Governed receipt refund approval, release and reversal', true, 'migration:finance-exceptions:v1', now()),
    (7, 'Customer Statements', 'Immutable governed customer statement snapshots and corrections', true, 'migration:statements-dunning:v1', now()),
    (8, 'Dunning Policies', 'Approved collections policy versions and customer profiles', true, 'migration:statements-dunning:v1', now()),
    (9, 'Collection Controls', 'Disputes, communication restrictions and legal holds', true, 'migration:statements-dunning:v1', now()),
    (10, 'Dunning Cases', 'Governed collection cases and promises to pay', true, 'migration:statements-dunning:v1', now()),
    (11, 'Dunning Notices', 'Maker-checker collection notices and delivery evidence', true, 'migration:statements-dunning:v1', now()),
    (12, 'General Ledger', 'Governed chart of accounts, journals, reversals and trial balance', true, 'migration:general-ledger:v1', now()),
    (13, 'Accounting Periods', 'Maker-checker fiscal period close controls', true, 'migration:general-ledger:v1', now()),
    (14, 'General Ledger Posting', 'Independent journal posting approval', true, 'migration:general-ledger:v2', now()),
    (15, 'Period Close', 'Independent accounting period hard-close approval', true, 'migration:general-ledger:v2', now()),
    (16, 'Ledger Control', 'Controller-only reversals and period reopening', true, 'migration:general-ledger:v2', now()),
    (17, 'Bank Accounts', 'Tenant bank account register', true, 'migration:bank-reconciliation:v1', now()),
    (18, 'Bank Statement Import', 'Immutable bank statement evidence import', true, 'migration:bank-reconciliation:v1', now()),
    (19, 'Bank Reconciliation', 'Statement-to-ledger matching and preparation', true, 'migration:bank-reconciliation:v1', now()),
    (20, 'Bank Reconciliation Approval', 'Independent reconciliation approval and reopening', true, 'migration:bank-reconciliation:v1', now()),
    (21, 'Bank Matching Rule Administration', 'Immutable tenant matching-rule versions', true, 'migration:treasury-governance:v1', now()),
    (22, 'Bank Matching Rule Approval', 'Independent matching-rule approval and activation', true, 'migration:treasury-governance:v1', now()),
    (23, 'Bank Adjustments', 'Governed bank fee, interest, and adjustment preparation', true, 'migration:treasury-governance:v1', now()),
    (24, 'Bank Adjustment Approval', 'Independent bank adjustment posting and reversal', true, 'migration:treasury-governance:v1', now()),
    (25, 'Supplier Negotiation', 'Evidence-backed Supplier negotiation decisions', true, 'V2Gate04SupplierNegotiationIntelligence', now())
ON CONFLICT ("ModuleName") DO NOTHING;

-- Advance the identity sequence past the seeded ids so the next tenant-created
-- module does not collide with them.
SELECT setval(
    pg_get_serial_sequence('public."Module"', 'ID'),
    (SELECT max("ID") FROM public."Module"),
    true);
