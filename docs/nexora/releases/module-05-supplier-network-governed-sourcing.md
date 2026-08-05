# Module 05: Supplier Network and Governed Sourcing

Date: 2026-07-31
Branch: `release/nexora-v2-v3-accelerated`
Baseline: `1f6a359`

## Decisions

- Module development: GO at M3 for the certified capabilities below.
- Production pilot: NO-GO until deployed staging proves the existing infrastructure, provider and authorized-data gates.
- Commercial boundary: Nexora ranks only known tenant Suppliers from persisted evidence. It does not fabricate Suppliers, prices or external discovery results.

## Feature Results

| Feature | Result | Evidence |
| --- | --- | --- |
| Supplier network CRUD | Complete | Supplier create/read/update is tenant-scoped, permission checked, concurrency protected and validated. Commercially referenced Suppliers cannot be deleted or casually reactivated; governance owns activation. Public image upload and manually authored performance KPIs were removed. |
| Supplier governance | Complete | Approval, verification, compliance, risk, readiness and active state move through one idempotent, append-only governance command with stable replay snapshots and monotonic event versions. |
| Known-Supplier candidate search | Complete | Out-of-stock RFQ lines open one persisted Sourcing Case and expose deterministic 10/20/50 candidate limits using tenant-local quote, purchase, preferred-Supplier and metadata evidence. Empty results explicitly state that no external search was started. |
| Numbered Supplier RFQ preparation | Complete | A selected eligible candidate produces a tenant-qualified `SRFQ-...` record linked to the Sourcing Case, RFQ line, demand line and Nexora Serial. Current shortage and Supplier readiness are revalidated before preparation. |
| Explicit delivery approval | Complete | Preparation does not create an outbox message. An authorized second command approves and queues the exact audited payload. Replays are stable, orphaned preparations are resumable, and duplicate active outreach is blocked. |
| Delivery safety and lifecycle | Complete | The worker revalidates Supplier identity, contact and governance before provider invocation. Sent, retry, uncertain and failed outcomes update the Sourcing Case without regressing already-sent outreach. |
| Legacy bypass closure | Complete | Direct authenticated solicitation creation returns `410 Gone`; direct agent dispatch fails closed. Runtime outreach must use the Sourcing Case candidate and explicit approval path. |
| Tenant and authorization boundary | Complete | Tenantless Suppliers are invisible in authenticated tenant contexts. Supplier and sourcing routes challenge unauthenticated users, forbid insufficient roles, reject cross-tenant reads and relationships, and retain PostgreSQL RLS coverage. |

## Normal Application

| Workflow | Click path | Route |
| --- | --- | --- |
| Review shortage lines | Procurement -> RFQs -> Open -> Sourcing required | `/procurement/rfqs/view/:id` |
| Open governed sourcing | RFQ shortage line -> Create/Open Sourcing Case | `/procurement/sourcing-cases/:caseId` |
| Compare outreach and responses | Sourcing Case -> Approved queue -> Sourcing workspace | `/procurement/rfqs/:rfqId/sourcing` |
| Maintain Supplier network | Procurement -> Suppliers | `/suppliers` |
| Review Supplier governance and evidence | Suppliers -> Open | `/suppliers/:supplierId` |
| Review incoming Supplier Quotes | Procurement -> Supplier Quote Inbox | `/procurement/supplier-quotes` |

## Migration

No schema migration is required. Module 5 reuses the existing tenant-qualified Sourcing Case, candidate, Supplier Solicitation, procurement event and outbox schema. EF model-drift verification reports no pending model changes.

## Verification

| Gate | Exact result |
| --- | --- |
| Focused sourcing, Supplier governance, dispatch, contracts and agent tests | 69 passed, 0 failed, 0 skipped |
| Focused authenticated HTTP/RLS PostgreSQL tests | 50 passed, 0 failed, 0 skipped |
| Portable backend suite | 1,069 passed, 0 failed, 0 skipped in 1 minute 1 second |
| PostgreSQL 16 suite | 235 passed, 0 failed, 0 skipped in 2 minutes 30 seconds |
| Backend solution build | Passed with 0 errors; 4 existing NU1701 compatibility warnings |
| EF model drift | No pending model changes |
| Frontend lint | Passed with 0 warnings |
| Frontend production build | Passed; 14,740 modules; 1,288,255 initial JS bytes within the 1,446,856-byte budget |
| Browser contract acceptance | 9 passed, 0 failed, 0 skipped in 22.3 seconds across desktop and mobile |
| NuGet vulnerability scan | No known vulnerable packages |
| Git whitespace validation | Passed |

The Module 5 browser scenarios use the normal Nexora shell and authenticated role states with controlled API responses for deterministic desktop/mobile UX. Actual ASP.NET Core authorization, PostgreSQL RLS, tenant keys, transactions, queue recovery and concurrency are independently exercised by backend and PostgreSQL tests. A deployed real-backend browser walkthrough remains a pilot gate.

## Security Disposition

The Supplier runtime query boundary no longer exposes tenantless legacy Suppliers to authenticated tenants. New and updated Suppliers derive tenancy from authentication, foreign keys are tenant-qualified where applicable, physical deletion is blocked, and delivery fails closed when governance or contact evidence changes.

`npm audit --omit=dev` reports the existing React Router RSC action CSRF advisory. Nexora is a client-only Vite `BrowserRouter` application with no React Server Components, SSR action endpoint or server-action execution path, so the affected behavior is not reachable. The audit-proposed forced downgrade is breaking and is not accepted in this module.

## Consultant Closure

Accepted P0/P1 findings were fixed and retested: legacy HTTP and agent dispatch bypasses, orphaned preparation recovery, duplicate outreach risk, governance replay casing, tenantless Supplier visibility, unstable preparation replay, retry lifecycle regression, Supplier identity race exposure, missing sourcing-table RLS assertions, weak HTTP coverage, and reversed workbench columns.

No accepted P0/P1 remains in the certified Module 5 slice.

## Remaining Gates

1. Run the normal authenticated sourcing journey against authorized Vercel, Render and Neon staging data.
2. Verify the configured delivery provider returns authoritative acceptance references and that retries/uncertain outcomes reconcile in staging.
3. Inventory tenantless legacy Supplier rows with an authorized migration-role report and map or quarantine them; never guess ownership.
4. Retain strict `/ready`, reachable malware scanning and verified durable evidence storage before pilot approval.
5. Continue monitoring the React Router advisory and block any affected RSC/server-action runtime introduction.

No push, merge, deployment, production infrastructure change, credential use or live-data access was performed.
