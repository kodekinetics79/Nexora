# Module 03: Sales Rep Ownership, Workload Routing and Performance Intelligence

Date: 2026-07-30
Branch: `release/nexora-v2-v3-accelerated`
Baseline: `1ac5581`

## Decisions

- Module development: GO at M3 for the certified capabilities below.
- Production pilot: NO-GO until the existing infrastructure gates are closed and this migration is rehearsed against authorized staging data through the Neon migration role.
- Scope boundary: routing remains explainable and manager-controlled. Recommendations do not silently assign commercial ownership.

## Feature Results

| Feature | Result | Evidence |
| --- | --- | --- |
| Sales Rep directory and drill-down | Complete | Canonical rep routes, tenant-owned workload, pipeline, action, Quote timing, follow-up, and outcome records are available from persisted data. |
| Account ownership | Complete | Managers assign or reassign the General Customer scope with a recorded reason, optimistic concurrency, idempotency, and preserved ownership history. Other ownership scopes are not closed implicitly. |
| Workload-aware routing | Complete | Governed eligibility, capacity, active workload, policy version, confidence, and recommendation evidence are returned together. Ineligible or out-of-window profiles cannot receive automated recommendations. |
| Routing execution | Complete | Managers can accept or override a recommendation through the normal queue. The command is tenant-scoped, versioned, idempotent, audited, and returns `404` for cross-tenant work and `409` for stale state. |
| Queue recovery | Complete | Open and claimed work remains visible, failures expose a real retry action, and retries preserve the transport correlation and idempotency contract. |
| Quote outcome attribution | Complete | Customer response and Won/Lost facts append to the commercial activity ledger in the same lifecycle transaction. Corrections use lifecycle-versioned identities and analytics use the latest outcome per Quote. |
| Performance truth | Complete | Win rate uses decided Quotes only and is suppressed below five decisions. Response and follow-up denominators are visible, date windows are validated, and rep records are directly drillable. |
| Tenant and role separation | Complete | Authenticated HTTP, EF filters, PostgreSQL RLS, tenant-qualified foreign keys, denied-role checks, cross-tenant negatives, and manager-only mutations pass. |

## Normal Application

| Workflow | Click path | Route |
| --- | --- | --- |
| Inspect team workload | Sales -> Team Overview | `/sales/team` |
| Browse Sales Reps | Sales -> Sales Reps | `/sales/reps` |
| Open a rep record | Sales Reps -> Open | `/sales/reps/:userId` |
| Assign account ownership | Sales -> Account Ownership | `/sales/accounts` |
| Review and assign routed Leads | Sales -> Routing Queue | `/sales/routing` |
| Reconcile performance | Sales -> Performance | `/sales/performance` |
| Retry dashboard workload | Dashboard -> Team Workload | `/dashboard/team` |

## Migration

`20260730234426_Module03TenantSafeSalesRouting`:

- preflights cross-tenant and orphaned routing records and fails without deleting data;
- adds tenant-qualified alternate keys and 20 composite foreign keys across users, Customers, Leads, identifiers, ownership, decisions, assignments, and queue work;
- makes `lead_routing_decisions` append-only in PostgreSQL;
- preserves historical migrations and supports populated upgrade, downgrade, function-removal verification, data retention, and re-upgrade rehearsal.

## Verification

| Gate | Exact result |
| --- | --- |
| Focused routing, controller, performance and Quote-outcome regression | 44 passed, 0 failed, 0 skipped |
| Focused PostgreSQL migration, authenticated HTTP and assignment-concurrency regression | 7 passed, 0 failed, 0 skipped |
| Portable backend suite | 1,044 passed, 0 failed, 0 skipped; final Integration Owner run 1 minute 10 seconds |
| PostgreSQL 16 suite | 210 passed, 0 failed, 0 skipped; final Integration Owner run 3 minutes 11 seconds |
| Backend solution build | Passed with 0 errors; 4 existing NU1701 compatibility warnings |
| EF model drift | No pending model changes; verified against disposable local PostgreSQL 16 configuration |
| Frontend lint | Passed with 0 warnings |
| Frontend production build | Passed; 14,740 modules; 1,288,250 initial JS bytes within the 1,446,856-byte budget |
| Authenticated browser acceptance | 13 passed, 0 failed, 0 skipped in 28.4 seconds across desktop and mobile |
| Authenticated HTTP and RLS | Manager assignment, recommendation options, stale conflict, idempotent replay, denied-role rejection, cross-tenant invisibility/`404`, and assignment history passed against ASP.NET Core and PostgreSQL |
| Populated migration rehearsal | Upgrade, 20 tenant-qualified foreign keys, cross-tenant rejection, decision immutability, downgrade, retained data, and re-upgrade passed |
| NuGet vulnerability scan | No known vulnerable packages |
| Git whitespace validation | Passed |

The browser scenarios use the normal Nexora shell with authenticated roles and controlled API responses for deterministic desktop/mobile UX. Real HTTP, PostgreSQL, authentication, authorization, migration, concurrency, and RLS behavior is independently exercised in the PostgreSQL lane.

## Security Disposition

`npm audit --omit=dev` reports the React Router RSC action CSRF advisory for `react-router` and `react-router-dom` 7.18.2. Nexora is a client-only Vite `BrowserRouter` application and has no React Server Components, SSR action endpoint, or server-action execution path, so the affected behavior is not reachable in this architecture. The audit-proposed forced downgrade is a breaking dependency change and is not accepted inside Module 3; the advisory remains monitored and blocks introduction of an affected RSC runtime.

The Vercel Blob snippet supplied during this module is not treated as evidence of a configured backend store. Nexora's current evidence contract requires backend-authenticated immutable writes, private tenant-authorized reads, hashes, recovery, and readiness probing. No Blob adapter, token, database credential, or production setting was added or changed.

## Consultant Closure

No P0 was found. Accepted P1 findings were fixed and retested: governed candidate filtering and server validation, customer-authorized owner options, monotonic account-ownership history versions, PostgreSQL concurrent-assignment conflict translation, idempotent account replay before mutable eligibility checks, truthful no-response KPI semantics, bounded performance periods, stable client idempotency, stale account conflict handling, permission-safe KPI drill-down, resolved customer/Nexora Serial evidence, explicit unattributed-outcome reconciliation, and rollback verification. The claimed rollback function leak was disproved in code and is now protected by an automated downgrade assertion.

## Remaining Gates

1. Apply and rehearse the migration in authorized staging with separate Neon migration and runtime roles.
2. Run normal authenticated workflows against deployed Vercel, Render, and Neon staging with representative authorized data.
3. Retain strict `/ready`, reachable malware scanning, and a verified durable evidence store before pilot approval.
4. Resolve the monitored React Router advisory before adopting any RSC or server-action runtime.
5. Rotate the Neon credential pasted into chat; it was ignored and was not used, stored, or tested.

No push, merge, deployment, production infrastructure change, Blob integration, credential use, or live-data access was performed.
