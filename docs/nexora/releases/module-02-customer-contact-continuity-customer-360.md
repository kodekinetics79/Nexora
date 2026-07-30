# Module 02: Customer, Contact Continuity and Customer 360

Date: 2026-07-30
Branch: `release/nexora-v2-v3-accelerated`
Baseline: `dd1ae44`

## Decisions

- Module development: GO at M3 for the certified capabilities below.
- Production pilot: NO-GO until the existing external infrastructure gates are closed and this migration is rehearsed against authorized staging data through the Neon migration role.
- Scope boundary: customer and contact lifecycle remains deactivate-only in this module. Reactivation, governed merge, and account hierarchy are not silently inferred by ordinary edit operations.

## Feature Results

| Feature | Result | Evidence |
| --- | --- | --- |
| Tenant-owned Customer CRUD | Complete | Claims-derived tenant and actor, deterministic `CU{Id:D8}` number, allowlisted writes, validation, stable ordering, optimistic concurrency, and auditable deactivation. |
| Customer contact CRUD | Complete | Exactly one immutable Customer or Supplier parent, tenant-wide email and active-primary uniqueness, active-parent validation, optimistic concurrency, and deactivation without deletion. |
| Supplier contact continuity | Complete | Shared contact endpoints resolve authorization from the persisted parent. Supplier-only roles cannot read Customer contacts and Customer permission is not required for Supplier contacts. |
| Lifecycle authority | Complete | Ordinary update DTOs and frontend edit forms cannot change active state or re-parent contacts. Explicit permission-protected deactivate commands are the only lifecycle mutation in this scope; deactivating a Customer atomically deactivates all active contacts. |
| Identity continuity | Complete | Customer number, name, email/domain, and active contact email/phone identities synchronize transactionally. Tenant-wide authoritative email/phone claims are serialized and conflicts return a safe `409`; inactive customers have every active routing identifier expired while history is retained. |
| Routing safety | Complete | Matching requires both an active identifier and active tenant-owned customer. Per-customer PostgreSQL advisory transaction locks serialize identity synchronization. |
| Retry and transaction safety | Complete | Customer, contact, and spreadsheet-import transactions execute through the configured EF execution strategy. Each retry reconstructs tracked state inside the attempt; PostgreSQL injects a transient failure after the first save and proves rollback, retry, and persisted completion. |
| Customer 360 | Complete | Normal application view presents identity, contacts, exact ownership, recent RFQs, quotes, orders, demand, commercial memory, health, follow-up, and evidence completeness from persisted server calculations. |
| Historical contact lineage | Complete | RFQ, Quote, and Order summaries retain contact identifiers and names even when the current Customer contact collection changes. |
| KPI truth | Complete | Win rate uses decided outcomes only; currency values are grouped and scalar totals are withheld when currencies are mixed; demand and sold evidence use bounded, disclosed cohorts. |
| Error and permission UX | Complete | Loading, empty, partial, error, retry, view-only, edit, and deactivate states are visible on desktop and mobile without claiming failed data as empty. |
| Tenant and role separation | Complete | Authenticated HTTP, EF tenant filters, PostgreSQL RLS, cross-tenant negatives, parent-aware module authorization, and permission-aware UI controls pass. |

## Normal Application

| Workflow | Click path | Route |
| --- | --- | --- |
| Browse and maintain accounts | Customers | `/customers` |
| Open Customer 360 | Customers -> View Details | `/customers/:customerId` |
| Edit the exact account | Customer 360 -> Edit Customer | `/customers?edit=:customerId` |
| Maintain Customer contacts | Customers -> Edit -> Contacts | `/customers?edit=:customerId` |
| Maintain Supplier contacts | Suppliers -> Edit -> Contacts | `/suppliers` |
| Inspect account ownership | Sales -> Account Ownership | `/sales/account-ownership` |

## Migration

`20260730222700_Module02CustomerContinuity`:

- adds non-null optimistic-concurrency tokens to Customer and Contact records;
- backfills a distinct UUID for every populated row before enforcing non-nullability;
- preflights duplicate tenant/customer document numbers and fails without discarding data;
- creates a tenant-qualified filtered unique Customer document-number index;
- updates the existing tenant-qualified Customer and Supplier primary-contact indexes so inactive former primaries do not block an active replacement;
- preserves historical migrations and supports downgrade/re-upgrade rehearsal.

The populated isolated PostgreSQL rehearsal passed upgrade, distinct token backfill, uniqueness enforcement, downgrade, migration-history verification, data retention, and re-upgrade.

## Verification

| Gate | Exact result |
| --- | --- |
| Focused Customer/Contact/Customer 360 backend after consultant fixes | 32 passed, 0 failed, 0 skipped |
| Focused PostgreSQL migration, retry and identity | 3 passed, 0 failed, 0 skipped |
| Portable backend suite | 1,031 passed, 0 failed, 0 skipped; 1 minute 20 seconds |
| PostgreSQL 16 suite | 203 passed, 0 failed, 0 skipped; 2 minutes 22 seconds |
| Backend solution build | Passed, 0 errors; 223 existing compatibility/nullability warnings remain |
| EF model drift | No pending model changes |
| Frontend lint | Passed with 0 warnings |
| Frontend production build | Passed; 14,740 modules; 1,287,639 initial JS bytes within 1,446,856-byte budget |
| Browser UX and permission acceptance | 13 passed, 0 failed, 0 skipped in 51.5 seconds across desktop and mobile |
| Authenticated HTTP and RLS | Customer/Contact create, update, context, deactivate, cross-tenant denial, denied-role rejection, and Supplier-only separation passed against ASP.NET Core and PostgreSQL |
| NuGet vulnerability scan | No known vulnerable packages |
| Git whitespace validation | Passed after final diff |

The browser scenarios use the normal application shell with authenticated roles and controlled API fixtures for deterministic desktop/mobile UX. Real HTTP, PostgreSQL, authentication, permissions, concurrency, migration, and RLS behavior is independently exercised in the PostgreSQL lane.

## Security Disposition

`npm audit --omit=dev` reports the React Router RSC action CSRF advisory for `react-router` and `react-router-dom` 7.18.2. Nexora is a client-only Vite `BrowserRouter` application and contains no React Server Components, SSR action endpoint, or server-action execution path, so the vulnerable behavior is not reachable in the current architecture. A forced downgrade is not accepted inside this module because it is a breaking dependency change; the advisory remains a monitored dependency item and blocks introducing an affected RSC runtime.

## Independent Review Closure

The consultant review identified one P0 retry-state defect and six P1 product/data defects. All accepted application findings were fixed and retested: retry attempts rebuild tracked state, Customer deactivation retires contacts, primary-contact indexes now model active records, Supplier contacts use Supplier permissions, Customer deactivation is visible and confirmed, cross-customer authoritative identity collisions are serialized and return `409`, and frontend Customer 360 contracts expose currency and completeness semantics. The remaining real-backend browser exercise is an authorized staging deployment gate, not claimed as completed by fixture-backed browser acceptance.

## Remaining Gates

1. Apply the migration in authorized staging using the Neon owner/direct migration role and retain the least-privilege runtime role.
2. Rehearse backup, upgrade, smoke test, and rollback against representative authorized staging data.
3. Run the normal authenticated Customer and Supplier contact workflows against the deployed Vercel/Render/Neon staging topology.
4. Retain the existing pilot requirements for durable S3-compatible evidence storage, reachable ClamAV, and strict `/ready` status from Module 01.
5. Promote only when no unresolved P0/P1 issue remains and both `/health` and `/ready` return 200.

No push, merge, deployment, production infrastructure change, or live-data access was performed.
