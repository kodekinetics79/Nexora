# Nexora V1 Development Completion Certificate

Certification date: 2026-07-27

Branch: `release/v1-completion-intelligence`
Candidate base: `6395ce4e47512b6467df974d795d93febb3bf6a8`

## Decision

- **Development phase: GO.** Every required V1 core domain is verified at M3 or higher. No accepted P0 or P1 development defect remains open.
- **Production deployment: NO-GO.** The code candidate is releasable, but the external Render, Neon, Vercel, object-storage and malware-scanner gates have not been executed in this local, non-production review.

No production system, live customer data, production credential, remote branch or deployment was accessed or changed during certification.

## Frozen V1 Scope

V1 is the connected, tenant-safe commercial journey from governed Lead Intelligence through Customer/Contact and ownership continuity, RFQ resolution, Product/Inventory evidence, Supplier sourcing and quote review, offer selection, Customer Quote, follow-up/outcome, Client PO, Customer Order, procurement handoff, Commercial Memory, Platform Administration, AI governance and operational readiness.

The immutable Nexora Serial, canonical Lead identity, customer/contact ownership, evidence, revisions and append-only commercial events remain the cross-stage lineage authorities. Commercial, inventory, tenant, money and lifecycle state remains server-authoritative.

## Module Maturity

| Required domain | V1 maturity | Certification basis |
|---|---:|---|
| Lead Intelligence | M3 | Deterministic local intake, canonical identity, exact duplicate/revision handling, evidence and governed review |
| CRM / Customer 360 | M3 | Tenant-qualified Customer/Contact continuity, ownership, active work and commercial drill-down |
| Sales Rep / Ownership / Routing | M3 | Explainable workload-aware routing, effective ownership, contribution and fair coaching evidence |
| RFQ Commercial Workspace | M3 | Persisted line resolution, readiness, ATP/sourcing evidence and no-quote recovery |
| Product / Part Intelligence | M3 | Alias resolution, commercial memory, outcome evidence and currency-safe aggregation |
| Inventory / ATP | M3 | Server-authoritative ATP, reservations, multi-warehouse and incoming-demand evidence |
| Supplier Management | M3 | Governed tenant CRUD, contacts, history, bid-quality evidence and performance memory |
| Supplier RFQ / Supplier Quote Inbox | M3 | Persisted solicitations, local extraction, revisions, evidence review and canonical projection |
| Offer Comparison | M3 | Evidence-backed landed cost, price, lead time, reliability, validity and governed award |
| Customer Quote | M3 | Selected-cost lineage, revision safety, readiness blocking and server-owned lifecycle |
| Follow-Up / Outcome | M3 | Persisted actions, due/completed history, Won/Lost evidence and learning linkage |
| Client PO / Customer Order | M3 | Exact/partial/discrepancy review, accepted award and lineage-complete order creation |
| Procurement Handoff | M3 | Controlled external reference, signed callback, authority/freshness and tenant-safe status |
| Commercial Memory | M3 | Evidence snapshots, product/supplier/customer/rep learning and governed decision history |
| Platform Administration | M3 | Separate platform auth, persisted tenant/plan/pipeline/audit views and audited tenant lifecycle |
| AI Governance / Cost Ledger | M3 | Local-first policy gateway, provider classification, lineage, token/cost status and external opt-in |
| Security / RLS / Audit | M3 | HTTP authorization, PostgreSQL RLS negatives, append-only audit/evidence and secret controls |
| Operations Readiness | M3 | Real health checks, queue status, AI dependency accounting and blocking-reason reconciliation |

M4 behavior exists only where verified evidence supports explainable optimization, including RFQ scenarios, stocking recommendations and selected commercial insights. No M5 claim is made.

## P0/P1 Closure

No P0 was found. The final review found and fixed these P1 defects:

1. The Platform Overview presented static service health, obsolete queue statuses and zero-valued AI cost/trend as authoritative. It now uses registered readiness checks, persisted extraction states and the USD AI cost ledger.
2. Pipeline, Plan and Audit navigation called missing live endpoints, while the UI exposed unsupported job deletion/requeue and per-tenant feature override controls. Persisted read-only endpoints were added and unsupported mutation controls were removed.
3. A production build could opt into the in-memory platform adapter. The adapter has been removed from the live client path; Platform Administration now always uses authenticated HTTP.
4. Tenant provisioning sent a UI plan tier that the backend did not persist. It now resolves and submits the real persisted Plan ID.
5. Platform Administration lacked authenticated browser acceptance. A dedicated synthetic platform operator and scenario 39 now verify Overview, Tenants, Tenant Registry, Pipeline, Plans and Audit through real HTTP/PostgreSQL.
6. The benchmark did not publish all requested rates. It now records lead, page/sheet and field locality, critical-field accuracy, review dependency, cost status and p50/p95.
7. Tenant lifecycle and read-only impersonation actions lacked a mandatory operator reason, and the audit search vocabulary did not cover those actions. The API client and UI now require a reason and the persisted audit view exposes the resulting events.

The first diagnostic browser attempt exposed a missing local HMAC environment variable. A later diagnostic polluted its own synthetic handoff state by running the callback scenario before the ordered suite. Neither was an application defect; the final certificate run followed a fresh zero-to-latest migration and fixture restore with the complete authorized environment.

## Browser Acceptance

Final result: **39/39 passed**, zero skipped, zero retries, one Chromium worker, **3.2 minutes**. All scenarios used normal authentication, actual HTTP APIs and the local PostgreSQL database. No platform mock adapter is wired into the live client path, and no mocked business response was accepted. The expanded scenario 39 also passed independently in **6.9 seconds** before the final ordered run, then passed in-sequence in **6.5 seconds**.

Certified click paths:

| Journey | Normal click path / route |
|---|---|
| Sales | Sign in -> Today -> Sales Rep Today (`/sales/today`) -> Customers (`/customers/:id`) -> RFQ workspace (`/commercial-cases/:id`) -> Quotes (`/sales/quotes`) |
| Sourcing | Sourcing Today (`/sourcing/today`) -> RFQs (`/procurement/rfqs/view/:id`) -> Sourcing (`/procurement/rfqs/:rfqId/sourcing`) -> Supplier Quote Inbox (`/procurement/supplier-quotes/:supplierQuoteId`) |
| Client PO / Order | Client PO Inbox (`/sales/client-pos`) -> PO Review (`/sales/client-pos/:clientPoId`) -> Orders -> Procurement Handoffs (`/procurement/handoffs`) |
| Commercial Intelligence | Customer 360 (`/customers/:id`) -> Commercial Workspace (`/commercial-cases/:id`) -> Commercial Memory (`/intelligence/commercial-memory`) |
| Platform / Tenant Admin | Platform sign-in (`/platform/overview`) -> Tenants (`/platform/tenants/:id`) -> Pipeline (`/platform/pipeline`) -> Plans (`/platform/plans`) -> Audit (`/platform/audit`); tenant operations at `/admin/operations` |

Primary V1 navigation and actions in these journeys were exercised. Empty, error, fail-closed, retry/review and authorization states are covered across portable, PostgreSQL and browser tests. RFQ and Client PO mobile layouts passed. Tested critical controls have accessible names and keyboard-reachable native controls; this is not a claim of a full independent WCAG conformance audit.

## Verification Evidence

| Gate | Exact result |
|---|---|
| Portable backend | `825/825` passed, 0 failed, 0 skipped, 47 seconds |
| PostgreSQL 16 | `140/140` passed, 0 failed, 0 skipped, 1 minute 33 seconds |
| Authenticated browser | `39/39` passed, 0 skipped, 0 retries, 3.2 minutes |
| Final Platform workflow | Scenario `39` passed, 0 skipped, 0 retries, 6.9 seconds |
| Backend solution build | Passed, 0 errors; 4 pre-existing `NU1701` compatibility warnings |
| Frontend lint | Passed with zero warnings |
| Frontend production build | Passed; initial JavaScript `1,314,599` bytes, 21.89% below the `1,683,028` baseline and below the `1,446,856` budget |
| EF model drift | No pending model changes |
| Migration rehearsal | Disposable loopback `nexora_sales_tests` reset and all 70 migrations applied; two authorized platform tenants and one platform operator seeded |
| NuGet security | No known vulnerable direct or transitive packages |
| npm security | 2 high entries representing inherited `GHSA-qwww-vcr4-c8h2`; RSC-only path is unreachable in the BrowserRouter SPA |
| Linux OCR image | Image built; Tesseract 5.3.0, Leptonica and `/app/tessdata/eng.traineddata` verified |
| Diff hygiene | `git diff --check` passed |

Commands:

```bash
dotnet build Backend/ERP_RFQ_Automation.sln --no-restore --nologo
dotnet test Backend/ERP_RFQ_Automation.Tests/ERP_RFQ_Automation.Tests.csproj --no-build --filter 'Category!=PostgreSQL' --logger 'console;verbosity=minimal'
dotnet test Backend/ERP_RFQ_Automation.Tests/ERP_RFQ_Automation.Tests.csproj --no-build --filter 'Category=PostgreSQL' --logger 'console;verbosity=minimal'
dotnet ef migrations has-pending-model-changes --project Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj --startup-project Backend/ERP_RFQ_Automation/ERP_RFQ_Automation.csproj --no-build
cd Frontend && npm run lint
cd Frontend && npm run build
cd Frontend && E2E_FULL_ACCEPTANCE=true npx playwright test --config playwright.commercial-journey-v2.config.ts
cd Frontend && npx playwright test --config playwright.commercial-journey-v2.config.ts --workers=1 --retries=0 --grep '39 platform'
dotnet list Backend/ERP_RFQ_Automation.sln package --vulnerable --include-transitive
cd Frontend && npm audit --json
docker build -f Backend/Dockerfile -t nexora-v1-rc1-check Backend
docker run --rm --entrypoint sh nexora-v1-rc1-check -c 'tesseract --version | head -1 && ldconfig -p | grep -q liblept && test -s /app/tessdata/eng.traineddata'
git diff --check
```

The PostgreSQL lane includes migration history/restore/re-upgrade, RLS, direct runtime-role isolation, queue and concurrency coverage. Historical migrations were not rewritten. No new migration was required by the final fixes.

## Local-First Benchmark

Authorized deterministic corpus: 12 synthetic artifacts, 10 lead-producing artifacts, 13 logical pages/sheets and 12 critical-field opportunities. Formats and cases: email text, CSV, a two-sheet XLSX, native PDF, scanned PDF, DOCX, PNG OCR, Supplier Quote text, Client PO CSV, duplicate original, forwarded duplicate and revision.

| Measure | Result |
|---|---:|
| Local lead rate | 100% |
| Local page/sheet rate | 100% |
| Local field-processing rate | 100% |
| External dependency | 0% |
| Human review | 16.7% (2/12) |
| Critical-field accuracy | 83.3% (10/12) |
| External processing cost | USD 0.00 |
| Local compute cost | Unpriced, not falsely reported as zero |
| Per-artifact p50 | 2.1 ms |
| Per-artifact p95 | 157.2 ms |

Limitations: this is a small synthetic, one-run algorithm corpus with one asserted critical token per artifact, not production accuracy or end-to-end throughput. The two local macOS ARM OCR cases failed closed into human review; the Linux delivery image has its OCR runtime verified. Larger authorized multilingual, noisy-scan and customer-layout corpora remain post-V1 measurement work.

## Deployment Prerequisites

1. Configure separate Neon schema-owner migration and least-privilege runtime connections; perform authorized backup/restore and guarded migration rehearsal, then prove runtime RLS.
2. Configure reachable, integrity-checked S3-compatible evidence storage and a reachable production malware scanner from Render. Free-instance ephemeral disk is not acceptable evidence storage.
3. Set strong production JWT/platform JWT and commercial-finance HMAC secrets outside source control; provision platform operators through an authorized secure process.
4. Verify Render `/health` and `/ready`, extraction/quote/procurement workers, queue/dead-letter reconciliation and AI cost/dependency status after deployment.
5. Build Vercel with `VITE_API_BASE_URL=https://nexora-fyjw.onrender.com`, no platform mock flag, and approved origin `https://nexora1-ai.vercel.app`; run authenticated production smoke tests.
6. Keep external AI disabled by default and policy bounded. Confirm no silent external provider call in production telemetry.
7. Preserve the React Router RSC compensating control, do not enable RSC actions, and adopt an upstream fixed package when available.

## P2 / Backlog

- Persist optional platform billing price, region and primary-contact metadata before presenting them as managed attributes.
- Add a governed, audited dead-letter requeue command; destructive extraction-job deletion remains prohibited.
- Add persisted per-tenant feature overrides only with an explicit entitlement model, authorization and audit design.
- Expand local document benchmarks to authorized multilingual/noisy real-world corpora and measure end-to-end queue throughput and cost.
- Complete a formal independent WCAG conformance audit and broaden responsive coverage beyond the certified critical paths.
- Upgrade React Router when a compatible upstream fix for `GHSA-qwww-vcr4-c8h2` is available.

## Excluded Scope

The following is not part of the V1 closure and is not certified by this record:

- Logistics execution: carrier selection, shipment orchestration, delivery proof, returns and transport optimization.
- Finance execution: invoicing, payment collection, credit, tax, treasury, general ledger and statutory accounting.
- FMCG specialization: lot/batch expiry, cold chain, route delivery, promotion/rebate and high-velocity replenishment semantics.
- Contracting: contract authoring, clause/obligation management, amendments, e-signature and legal compliance workflow.

Existing legacy screens or entities in these areas do not change the exclusion. They require their own scope, contracts, tests and release certification.

## Rollback

This closure adds no migration. Revert the V1 freeze commit and rebuild the prior application image. Preserve all tenant data, evidence, audit, queue and migration history. Do not downgrade or reset a shared database.
