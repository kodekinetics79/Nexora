# Module 04: Product and Part Intelligence, Inventory and ATP

Date: 2026-07-30
Branch: `release/nexora-v2-v3-accelerated`
Baseline: `fca499e`

## Decisions

- Module development: GO at M3 for the certified capabilities below.
- Production pilot: NO-GO until the existing staging infrastructure gates are closed and this migration is rehearsed through the Neon migration role against authorized data.
- Commercial boundary: RFQ availability is an evidence snapshot, not a reservation. Stock is reserved only for a confirmed Customer Order.

## Feature Results

| Feature | Result | Evidence |
| --- | --- | --- |
| Canonical part resolution | Complete | Lead resolution reuses the tenant-local Product resolver and preserves possible-match review. Supplier Quote history is evidence only and can no longer fabricate an exact Product identity. |
| Authoritative ATP | Complete | ATP subtracts active reservations, allocations, quarantine, damaged, expired and safety-stock quantities from physical on-hand and floors the result at zero. Missing tenant inventory fails closed. |
| Incoming and shortage classification | Complete | Known incoming supply is recognized only when it covers the immediate shortage. Partial supply remains `KnownShortage`; projected shortage, expected availability and Product lead time are persisted. |
| Commercial cost evidence | Complete | Unit cost is emitted only when exactly one active tenant base currency proves its currency. Ambiguous or absent currency suppresses the cost instead of assuming USD. |
| RFQ-line lineage | Complete | Each commercial resolution links to the exact persisted RFQ item through deterministic line, Product and normalized-part matching. Lead, RFQ, RFQ item and Product relationships are protected by restrictive keys. |
| Product and Warehouse operations | Complete | Normal Product and Warehouse routes enforce module permissions, claims-derived tenancy, cross-tenant invisibility, safe errors, retry states and permission-aware actions. Product forms submit only fields the server persists. |
| Inventory release and consumption | Complete | Manual release is versioned, idempotent and audited; only the original idempotency key can replay. Consumption locks both reservation and inventory identities before decrementing stock. |
| Truthful RFQ experience | Complete | Processing blocks RFQ creation and sourcing while availability is unavailable, offers retry, shows ATP/shortage/lead-time/cost evidence, and removes fabricated approval history. |

## Normal Application

| Workflow | Click path | Route |
| --- | --- | --- |
| Resolve an RFQ | Procurement -> RFQs -> Process | `/procurement/rfqs/process/:id` |
| Inspect persisted RFQ-line evidence | Procurement -> RFQs -> Open | `/procurement/rfqs/view/:id` |
| Browse and maintain Products | Inventory -> Products | `/inventory/products` |
| Open Product detail | Products -> Open | `/inventory/products/:id` |
| Review availability exceptions | Inventory -> Availability | `/inventory/availability` |
| Review active reservations | Inventory -> Reservations | `/inventory/reservations` |
| Review incoming supply | Inventory -> Incoming | `/inventory/incoming` |
| Review demand intelligence | Inventory -> Demand | `/inventory/demand` |

## Migration

`20260731014905_Module04ProductInventoryAuthority`:

- adds immutable RFQ-item lineage, projected shortage, lead time, expected date, unit cost and cost-currency evidence;
- backfills shortage and only unambiguous Product/line matches on data-bearing upgrades;
- temporarily suspends only the update guard inside the transactional backfill and proves the guard is re-enabled;
- versions the append-only guard so new evidence fields cannot change during the one permitted Lead-to-RFQ linkage;
- adds quantity, cost, lead-time and RFQ-item checks plus tenant-qualified Lead, RFQ and Product keys and the RFQ-item parent key;
- restores the historical trigger definition on downgrade, retains existing rows, and supports re-upgrade.

## Verification

| Gate | Exact result |
| --- | --- |
| Focused ATP, resolution, reservation, Warehouse and Product regression | 41 passed, 0 failed, 0 skipped |
| Focused populated PostgreSQL migration rehearsal | 1 passed, 0 failed, 0 skipped |
| Portable backend suite | 1,058 passed, 0 failed, 0 skipped; final Integration Owner run 1 minute 8 seconds |
| PostgreSQL 16 suite | 211 passed, 0 failed, 0 skipped; final Integration Owner run 2 minutes 44 seconds |
| Backend solution build | Passed with 0 errors; 4 existing NU1701 compatibility warnings |
| EF model drift | No pending model changes |
| Frontend lint | Passed with 0 warnings |
| Frontend production build | Passed; 14,740 modules; 1,288,258 initial JS bytes within the 1,446,856-byte budget |
| Browser contract acceptance | 11 passed, 0 failed, 0 skipped in 30.5 seconds across desktop and mobile |
| NuGet vulnerability scan | No known vulnerable packages |
| Git whitespace validation | Passed |

The Module 4 browser scenarios use the normal Nexora shell and authenticated role states with controlled API responses for deterministic desktop/mobile UX. Actual ASP.NET Core authorization, PostgreSQL migration, RLS, tenant keys and concurrency behavior are independently exercised in backend and PostgreSQL tests. A deployed real-backend browser walkthrough remains a pilot gate.

## Security Disposition

Warehouse endpoints no longer trust request tenant IDs and require Product permissions for view, create, edit and delete. Product purchase history is tenant-qualified. Cross-tenant commercial Product references are rejected by the tenant-integrity trigger and reinforced by a composite foreign key.

`npm audit --omit=dev` continues to report the React Router RSC action CSRF advisory. Nexora is a client-only Vite `BrowserRouter` application with no React Server Components, SSR action endpoint or server-action execution path, so the affected behavior is not reachable. The audit-proposed forced downgrade is breaking and is not accepted in this module.

## Consultant Closure

Accepted P0/P1 findings were fixed and retested: cross-tenant Product history, request-controlled Warehouse tenancy, missing permissions, incomplete ATP deductions, concurrent consumption locking, non-versioned manual release, false exact Product matches from Supplier history, partial incoming misclassification, absent RFQ-item lineage, silent inventory dependency failure, fabricated RFQ approval history, unsupported Product save fields, ambiguous currency display, and incomplete append-only protection for the new evidence columns.

No accepted P0/P1 remains in the certified Module 4 slice.

## Remaining Gates

1. Apply and rehearse the migration in authorized staging with separate Neon migration and runtime roles.
2. Run normal authenticated workflows against deployed Vercel, Render and Neon staging with representative authorized Products, warehouses, stock, incoming supply and RFQs.
3. Retain strict `/ready`, reachable malware scanning and verified durable evidence storage before pilot approval.
4. Add governed Product alias maintenance UI and authoritative Product price-currency storage as a later Product master-data increment; do not infer either field meanwhile.
5. Continue monitoring the React Router advisory and block any affected RSC/server-action runtime introduction.

No push, merge, deployment, production infrastructure change, credential use or live-data access was performed.
