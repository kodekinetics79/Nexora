# Release 01: Dashboard and Lead Intelligence

## Objective

Deliver a tenant-safe commercial spine from Lead through Order, a truthful role-aware Dashboard 1.0, local-first Lead Intelligence, and explainable measured routing. Inventory automation, broad supplier discovery, pricing optimization, and full Order status automation remain outside this release.

## Integration Decision Record

The existing Commercial Case and lifecycle event/outbox implementation is the foundation. Release 01 extends it; it does not create a competing workflow framework.

- Integration Owner exclusively owns migrations and shared EF model changes.
- `CommercialCaseReference` is exposed as `NexoraSerial` and is immutable.
- Lead resolves customer/contact identity; downstream documents inherit it server-side.
- Quote joins the governed lifecycle contract before its status contributes to certified KPIs.
- Commercial milestones use one append-only normalized event spine with tenant, case, aggregate, actor, source, correlation, idempotency, occurrence time, and bounded dimensions.
- Dashboard 1.0 uses one versioned query/read contract and one freshness boundary.
- Lead Intelligence is local-first and fail-closed to review. External processing requires tenant policy consent.
- Routing reports only measured workload factors; request idempotency is content-bound.

## Implementation Slices

1. Repair the pending evidence migration for populated databases; enforce completed-run immutability, upload authorization, tenant-scoped worker processing, typed integrity failures, and distinct source occurrences.
2. Add the shared Commercial Document Identity to Lead, RFQ, Quote, and Order with conservative backfill and tenant-qualified constraints where the existing schema permits.
3. Extend lifecycle/event governance to quote milestones and make Lead-to-RFQ conversion atomic.
4. Add Dashboard `release-01` DTOs, query service, permission checks, KPI definitions, freshness, and drill-down identifiers.
5. Expose Nexora Serial and inherited customer/contact identity in Lead, RFQ, Quote APIs and core list/detail screens; remove misleading walk-in fallbacks.
6. Record extraction processing path and review state; distinguish deterministic, local, and external provider classes.
7. Replace synthetic routing capacity with measured workload inputs and request hashes.
8. Run focused tests continuously, then portable and PostgreSQL lanes, frontend lint/build, migration drift, consultant review, and all P0/P1 fixes.

## Acceptance Gates

- Existing populated PostgreSQL databases migrate without guessed identity or broken foreign keys.
- Cross-tenant reads/writes fail under the runtime database role and HTTP authorization.
- Nexora Serial, customer, and contact remain stable from Lead through RFQ, Quote, Order, and invoice output.
- Conversion, status transition, event, and outbox writes are atomic and idempotent.
- Dashboard values reconcile to returned drill-down records under the same cohort and timestamp.
- Provider outage, storage integrity failure, malware scanner outage, or incomplete extraction never produces a false success.
- Routing explanations cite measured inputs and remain deterministic under retry.
- Portable tests, PostgreSQL tests, frontend lint/build, and EF pending-model checks pass.

## Evidence Log

Initial bounded reviews completed for commercial workflow, architecture/data, AI/document intelligence, UX/frontend, security/SRE/QA, and independent consultation. The verified P0 release blockers are customer/contact continuity, downstream serial lineage, ungoverned Quote status, non-atomic conversion, unreconcilable dashboard calculations, unsafe evidence migration backfill, tenant scope in workers, upload authorization, and declared malware-scanner readiness.

## Certification Evidence

- **Release status: NO-GO (2026-07-24).** No production deployment, merge, push, or live-data mutation was performed.
- Backend build passed on .NET 8. Existing `NU1701` warnings remain for `OpenXmlPowerTools` and `System.Management.Automation.dll` compatibility.
- Portable lane: **506 passed, 0 failed, 0 skipped**.
- PostgreSQL 16 lane: **55 passed, 0 failed, 0 skipped**. Coverage includes populated upgrades, unsafe legacy-parent/customer rejection, runtime-role customer isolation, tenant-qualified extraction-job foreign keys, evidence immutability, unstructured source/corpus/run reconciliation, queue fencing, and immutable lineage through Order.
- Frontend `npm run lint` and production `npm run build` passed with Vite 8.1.5.
- EF Core `migrations has-pending-model-changes` reported no model drift.
- NuGet vulnerability check reports **zero known vulnerable packages** after pinning `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12. `npm audit --omit=dev` reports the React Router RSC advisory on two package nodes; the reviewed Vite SPA has no RSC/server-action handler. SheetJS was upgraded to the fixed official 0.20.3 distribution.
- Populated migration/rollback rehearsal passed on disposable PostgreSQL 16: migration `20260724003000` plus representative customer/contact/Lead/RFQ/Quote data was dumped, upgraded through all three Release 01 migrations, restored to a separate database, and upgraded again. Customer/contact IDs and `NXR-2026-000001` matched across Lead, RFQ, and Quote after both upgrades.
- Local deterministic extraction benchmark: 10,000 structured rows, five runs, **191.49 ms p50**, **265.20 ms p95**, **375,117,824 aggregate allocated bytes**, and **zero external provider calls**. This does not certify PDF, scanned-document, local-model, or external-model latency.
- Focused acceptance repairs include quote lifecycle concurrency, first-decision KPI semantics, invalid dashboard-window suppression, explicit detail-page errors, PDF export wiring, governed RFQ intake navigation, generic correlated 5xx responses, customer/contact RLS, evidence-graph deletion controls, migration preflights, and unstructured extraction lifecycle completion.

## Residuals

- **P0 deployment blocker:** browser SIT is not executed. The required in-app browser connection rejects initialization because its sandbox metadata is incomplete. Lead -> RFQ -> Quote, role variants, error states, and 375 px layout are therefore not accepted in a real browser.
- **P1 acceptance blocker:** only leads received, leads requiring review, qualification rate, and median qualification time have calculated dashboard paths. The other 14 contract KPIs truthfully return `insufficient_data`, but Dashboard 1.0 is not feature-complete or fully reconciled.
- **P1 acceptance blocker:** no TestServer plus PostgreSQL test proves 401/403, wrong permission, forged tenant input, and runtime-role RLS together for the changed endpoints.
- **P1 acceptance blocker:** deterministic versus local-model versus external-model provider class is not persisted as an authoritative extraction dimension. The configured Ollama service defaults to a cloud endpoint, so local-model processing is not certified.
- **P1 acceptance blocker:** customer/contact validity is protected by RLS and identity triggers, but tenant-qualified customer/contact foreign keys and delete-restriction coverage are not installed for every lineage field.
- **P1 deployment blocker:** the S3 readiness credential performs delete under its probe prefix. A reviewed bucket policy must prove delete/overwrite is denied under `Evidence/tenants/*` while `_readiness/*` cleanup remains allowed.
- **P2:** response stamping exists, but specialized follow-up due/completed events, scheduling/history UI, and a versioned Order status lifecycle remain outside accepted Release 01 behavior.
- The React Router advisory is conditionally accepted as unreachable only while the application remains a client-rendered Vite SPA with no RSC/server-action runtime. Reassess on any routing/SSR architecture change.

## Rollback

The disposable backup/restore rehearsal passed. Production rollback remains restore-to-new-database or a forward corrective migration; do not downgrade a shared database. Application rollback must retain columns written by migrations `20260724004000`, `20260724223932`, and `20260724230121` until a reviewed compatibility release is available.

No production deployment or live-data mutation was performed.
