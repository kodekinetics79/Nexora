# NEXORA V1 COMPLETION & INTELLIGENCE UPGRADE
## Close uncovered capabilities, elevate basic modules, preserve proven foundations, and certify a superior Trading/Distribution RFQ-to-Revenue product

Read and obey `./AGENTS.md`.

Read the latest capability register, architecture record, KPI dictionary, and release certification records. Inspect the current branch, commits, migrations, tests, and working tree before editing.

Do not repeat repository-wide discovery. Do not return another product strategy essay. Implement, integrate, verify, and leave visible working software.

---

## 1. EXECUTIVE MANDATE

Complete Nexora V1 as a coherent, high-performance commercial intelligence platform:

```text
Customer RFQ
→ Lead Intelligence
→ Customer & Sales Ownership
→ Item / Inventory Resolution
→ Supplier Sourcing
→ Supplier RFQs
→ Supplier Quotes
→ Offer Comparison
→ Customer Quote
→ Follow-Up
→ Client PO / Customer Order
→ Procurement Handoff
→ External Operational Visibility
→ Commercial Learning
```

This release has two responsibilities:

1. **Close capabilities that remain absent or incomplete.**
2. **Upgrade any basic CRUD/UI-only/workflow-only capability into evidence-based, intelligent, explainable, high-performance behavior.**

Do not rewrite proven foundations merely to make them look new.

---

## 2. PRODUCT BOUNDARY

Nexora owns:

- CRM and account intelligence;
- high-volume Lead and RFQ automation;
- Product, Part, Inventory and availability intelligence;
- Supplier sourcing and Supplier Quote management;
- Customer Quote, follow-up, Client PO and Customer Order capture;
- procurement handoff and read-only operational visibility;
- Sales Rep, Supplier, Customer, Product, Inventory and pricing intelligence;
- local-first AI governance, evidence, audit, cost and learning.

External ERP/WMS/TMS/Finance systems remain systems of record for:

- actual Supplier POs;
- goods receipt and warehouse execution;
- pick/pack/dispatch and carrier execution;
- invoices, payments, AP/AR, tax and general ledger.

Do not turn Nexora into a full ERP, WMS, TMS or accounting suite.

Target V1 market: **Trading and Distribution businesses with high RFQ volume**.

FMCG and Contracting remain separate industry editions. Do not claim them complete in this release; preserve extension points and document their gaps.

---

## 3. FROZEN FOUNDATIONS

Where present and passing, freeze:

- durable intake occurrences;
- canonical Lead identity;
- duplicates and revisions;
- Nexora Serial and `DemandLineId`;
- Customer/Contact resolution;
- Account Owner, Opportunity Owner, Sales Rep profiles and weighted routing;
- Product/Part resolution;
- Inventory, ATP, Warehouses and reservations;
- Supplier master, sourcing, Supplier RFQs and Supplier Quote Inbox;
- offer comparison and Customer Quote cost lineage;
- Client PO matching, Customer Orders and procurement handoff;
- commercial events, evidence and memory;
- authentication, tenant context, authorization and PostgreSQL RLS.

Preserve these recent checkpoints where present:

- `ed98590` — RFQ commercial command workspace
- `8fe156c` — Supplier Quote to Customer Quote journey
- `e7036e4` — Client PO matching and Customer Orders
- `78e4ddf` — Procurement handoff and commercial learning

Change a frozen foundation only for a reproducible defect or a proven maturity gap. Apply the smallest surgical fix and add regression coverage.

---

## 4. MATURITY MODEL

Classify every relevant capability using actual code and browser evidence:

- **M0 — Absent**
- **M1 — UI/CRUD only**
- **M2 — Workflow-wired**
- **M3 — Evidence-based and automated**
- **M4 — Intelligent, explainable and optimized**
- **M5 — Autonomous within policy**

Targets:

- Core commercial modules: **M3 minimum, M4 where sufficient verified data exists**
- External operational visibility: **M2–M3**
- Predictive decisions: **shadow-mode M4 only**
- No uncontrolled M5 automation

Create/update:

`docs/nexora/v1-completion-matrix.md`

For each capability record:

- current level;
- target level;
- exact evidence;
- gap;
- implementation decision;
- acceptance result.

Do not spend more than 45 minutes on this matrix. Continue immediately into implementation.

---

## 5. BOUNDED PRINCIPAL-LEVEL TEAM

Use no more than eight non-overlapping lanes:

1. **Chief Product / RFQ-to-Revenue SME**
   - commercial journey, customer value, adoption and market differentiation.

2. **CRM / Sales Operations / Customer Success SME**
   - Customer 360, ownership, routing, follow-up, coaching, account health and Sales Rep fairness.

3. **Strategic Sourcing / Supplier Management SME**
   - Supplier Quote quality, competitiveness, verification, sourcing and offer outcomes.

4. **Inventory / Distribution / Commercial Supply SME**
   - ATP, demand layers, stockout impact, replenishment and what-to-stock intelligence.

5. **AI / Document Intelligence / Applied ML SME**
   - local-first parsing, OCR, retrieval, confidence, learning, shadow prediction and cost control.

6. **Enterprise UX / Accessibility / Design Systems SME**
   - simple journeys, RFQ workspace, role-based Today views, mobile, accessibility and visual quality.

7. **Backend / Data / Integration / Security / SRE SME**
   - APIs, PostgreSQL, RLS, migrations, outbox, integrations, observability and performance.

8. **Independent Consultant / Red-Team QA**
   - challenges implemented behavior, market usefulness, business logic, tenant safety and evidence.

One Integration Owner controls shared contracts, schema, migrations, state machines and merge order.

No agent may rescan the entire repository. Each returns at most five P0/P1 findings with exact files. Consultants review the implemented diff and normal browser behavior; implementation agents fix accepted P0/P1 issues in the same gate.

---

## 6. GATE 1 — PRODUCT EXPERIENCE COMPLETION

Deliver a calm, discoverable, attractive normal application.

### 6.1 Role-based Today

Create/complete role-based homes:

- Sales Rep Today
- Sales Manager Control Tower
- Sourcing Today
- Inventory Today
- Executive RFQ-to-Revenue
- Platform/Tenant Admin Operations

Each shows only actionable, permission-appropriate work.

### 6.2 Opportunity / RFQ Command Workspace

Ensure one continuous workspace exposes:

- customer and contact;
- Account and Opportunity Owners;
- deadline/SLA;
- readiness;
- requested lines;
- Product match;
- Inventory/ATP;
- sourcing;
- Supplier Quotes;
- Customer Quote;
- follow-up;
- Client PO / Customer Order;
- procurement handoff;
- commercial memory;
- evidence and timeline.

No manual jumping among disconnected modules to understand one opportunity.

### 6.3 Customer 360

Upgrade CRM from basic master data to:

- contacts and ownership;
- current opportunities;
- RFQs, Customer Quotes and Orders;
- product/category demand;
- conversion;
- accepted pricing;
- follow-up history;
- no-quote/loss reasons;
- account health;
- profitability only where reliable;
- next-best action with evidence.

### 6.4 Universal Search and navigation

Search by:

- Nexora Serial;
- Customer RFQ;
- Customer;
- Contact;
- Part/Manufacturer;
- Supplier;
- Supplier RFQ/Quote;
- Customer Quote;
- Client PO;
- Customer Order;
- external PO reference;
- attachment/email subject.

Results must explain relationships.

### 6.5 UX quality

Require:

- progressive disclosure;
- clear primary actions;
- no icon-only critical actions;
- breadcrumbs;
- loading/skeleton, empty, stale, error and retry states;
- desktop/mobile;
- WCAG-aligned keyboard, labels, focus and contrast;
- route-level lazy loading;
- no dead controls or mock data.

**Visible Gate 1 checkpoint:** normal sidebar, Customer 360, role-based Today and complete RFQ workspace.

Local commit:

`feat: complete Nexora V1 commercial experience`

---

## 7. GATE 2 — INTELLIGENCE UPGRADE

Upgrade basic analytics into explainable commercial intelligence.

### 7.1 RFQ Intelligence

Implement/complete:

- RFQ readiness score;
- viable/actionable/no-quote decision;
- line-level blocker analysis;
- no-quote recovery;
- SLA risk;
- customer clarification detection;
- next-best commercial action;
- duplicate/revision-safe coverage;
- evidence drill-down.

No generic “Likely Skip.” Every recommendation needs reason, evidence, confidence and override.

### 7.2 Product / Part Commercial Memory

For each part show:

- requested;
- quoted;
- decided;
- won/lost/pending;
- time period and sample size;
- last won price and context;
- typical winning range;
- Supplier cost context;
- winning lead time;
- stock status at quotation;
- loss reasons;
- stockout-blocked opportunities.

Never say “never won” without decided sample, period and pending count.

### 7.3 Supplier Intelligence and Bid Quality

Upgrade Supplier evaluation to separate:

- response rate/time;
- Quote completeness;
- price and landed-cost competitiveness;
- availability and lead-time quality;
- selections;
- Customer Orders supported;
- revision volatility;
- missing-term burden;
- risk/compliance.

Add a **Bid Quality Detector** that flags:

- missing validity;
- unconfirmed stock;
- unrealistic lead time;
- price outlier;
- repeated post-selection price changes;
- incomplete terms;
- suspicious alternate;
- stale evidence.

Do not claim fulfilment quality without authoritative ERP/WMS data.

### 7.4 Sales Rep Intelligence

Upgrade performance to:

- weighted coverage;
- first meaningful action;
- Quote turnaround;
- follow-up completion;
- customer insight capture;
- conversion/value conversion;
- margin where verified;
- quality/incorrect commitments;
- context-adjusted comparison;
- coaching opportunities.

Separate Account Owner, Opportunity Owner and contributor credit. Do not use clicks or login time.

### 7.5 Inventory Demand and Stocking Intelligence

Use:

- observed;
- qualified;
- quoted;
- weighted;
- committed;
- fulfilled demand where authoritative.

Recommend:

- stock-review candidates;
- stockout-blocked opportunities;
- high-demand/low-conversion items;
- high-margin recurring items;
- reorder candidates;
- slow/dead/overstock risk.

Consider conversion, margin, Supplier lead time/reliability, MOQ, carrying cost, shelf life, obsolescence and demand consistency. Do not recommend stocking from RFQ frequency alone.

### 7.6 Opportunity Digital Twin

For eligible opportunities compare:

- stock-only;
- Supplier-only;
- split stock/source;
- fastest delivery;
- lowest landed cost;
- best margin;
- lowest risk;
- approved alternate.

Show assumptions, evidence, validity and user override. Keep as decision support, not autonomous commitment.

### 7.7 Customer Target Bridge

Where verified:

```text
Maximum Landed Cost =
Customer Target Selling Price × (1 − Required Margin)

Maximum Supplier Cost =
Maximum Landed Cost − Freight − Duties − Taxes − Handling − Risk
```

Never reveal confidential target/margin automatically and never calculate from missing inputs.

**Visible Gate 2 checkpoint:** Commercial Memory, Bid Quality, Sales coaching, Inventory stocking evidence and Digital Twin on real records.

Local commit:

`feat: elevate commercial modules with explainable intelligence`

---

## 8. GATE 3 — INTEGRATION AND OPERATIONAL VISIBILITY

Complete the system-of-intelligence boundary.

### 8.1 Integration framework

Provide governed connectors/contracts for:

- Microsoft 365/Gmail intake;
- CRM/customer master;
- ERP inventory and customer/supplier references;
- WMS availability/status;
- Supplier discovery sources;
- procurement handoff;
- operational status callbacks/webhooks;
- SMTP;
- S3-compatible evidence storage;
- malware scanning.

Use outbox/inbox, idempotency, retries, dead-letter handling, correlation IDs, sync checkpoints and audit.

### 8.2 Procurement and operational status

Persist and display:

- external Sales Order reference where available;
- external Supplier PO and line;
- ordered quantity/cost;
- expected date;
- supplier confirmation;
- receipt/accepted status;
- fulfilment/dispatched/delivered status where integrated;
- source system;
- last sync;
- authoritative/unverified state.

Do not infer operational facts.

### 8.3 Sync health

Admin visibility:

- connector status;
- last successful sync;
- backlog;
- failed events;
- retries;
- stale records;
- reconciliation differences;
- replay action with authorization.

### 8.4 Graceful absence

Where integration is not configured:

- show truthful “Not integrated / Awaiting synchronization”;
- allow controlled reference capture;
- never fabricate status.

**Visible Gate 3 checkpoint:** one real disposable integration contract completes procurement handoff → external PO line → status update in the normal opportunity view.

Local commit:

`feat: complete enterprise integration and operational visibility`

---

## 9. GATE 4 — LOCAL-FIRST AI, LEARNING AND COST CONTROL

### 9.1 Authoritative processing paths

Persist per document/page/field:

- native parser;
- deterministic rules;
- local OCR;
- local retrieval;
- local model;
- human review;
- external fallback.

### 9.2 Policy

- external AI disabled by default;
- all provider calls through AI Gateway;
- no direct business-module provider clients;
- target ≤10% external dependency;
- no silent fallback;
- tenant/document budgets;
- minimum necessary redacted content;
- input/output tokens and cost;
- local compute/OCR cost;
- provider/model/version;
- reason, confidence, result and correlation ID.

### 9.3 Learning

Three governed layers:

1. approved tenant-scoped operational memory;
2. deterministic analytics from verified events;
3. predictive models only after sample threshold, offline evaluation, shadow mode, fairness review, approval and rollback.

Add/complete an **Outcome Learning Studio** showing:

- learned templates and aliases;
- customer/supplier mappings;
- corrections reused;
- conflicts;
- sample sizes;
- drift;
- shadow recommendations;
- approval/disable/rollback.

### 9.4 Real document benchmark

Use representative authorized fixtures:

- email body;
- CSV/XLSX;
- multi-sheet workbook;
- native PDF;
- scanned PDF;
- DOCX;
- image;
- Supplier Quote;
- Client PO;
- duplicate/revision.

Measure:

- critical-field accuracy;
- local processing rate;
- external lead/page/field rates;
- human-review rate;
- cost;
- p50/p95;
- error/retry rate;
- correction reuse.

Do not confuse algorithm benchmarks with end-to-end throughput.

**Visible Gate 4 checkpoint:** processing path/evidence and learning improvements visible on RFQ, Supplier Quote and Client PO records.

Local commit:

`feat: complete local-first commercial learning and AI governance`

---

## 10. GATE 5 — SUPERIOR PERFORMANCE AND PRODUCTION CERTIFICATION

### 10.1 Performance

Preserve or improve established baselines.

Requirements:

- no >10% regression on existing deterministic classification, exact-match, offer-comparison, memory-aggregation and reservation benchmarks without documented reason;
- optimize proven hotspots by at least 20% where safely achievable;
- eliminate N+1 queries;
- index tenant and identity lookups;
- batch and cache safely;
- route-level lazy loading and bundle analysis;
- query-count and allocation evidence;
- realistic concurrent reservation and idempotency tests;
- dashboard/read-model p50/p95 with realistic datasets;
- separate full-document end-to-end throughput benchmark.

Do not invent thresholds unsupported by the acceptance environment.

### 10.2 Reliability

Test:

- provider outage;
- database retry;
- duplicate webhook;
- worker replay;
- stale inventory;
- expired Supplier Quote;
- SMTP failure;
- storage failure;
- malware scanner unavailable;
- partial integration outage;
- optimistic concurrency;
- dead-letter recovery;
- backup/restore/re-upgrade.

### 10.3 Security and tenancy

Run real:

```text
frontend
→ authentication
→ tenant context
→ HTTP authorization
→ service
→ PostgreSQL RLS
→ persisted result
```

Cover critical routes, roles, cross-tenant denial, evidence, pricing, Supplier costs, ownership, Customer Orders, handoffs and admin controls.

### 10.4 Production prerequisites

Validate in disposable/staging environment:

- SMTP;
- persistent evidence storage and deny-delete/overwrite policy;
- ClamAV or approved scanner;
- Render/Vercel/Neon configuration or repository-equivalent deployment;
- backups and restore;
- readiness/health endpoints;
- logging, metrics and traces;
- alerting and SLOs;
- secrets and egress;
- dependency advisory disposition.

### 10.5 Browser acceptance

Run normal authenticated application with actual backend/PostgreSQL.

No mocked business API responses, fixture-only shells, hidden routes or authorization bypass.

Run all existing journeys plus new intelligence, integration and failure-state scenarios with zero skips.

**Visible Gate 5 checkpoint:** production-like acceptance dashboard with pass/fail evidence and no hidden blocker.

Local commit:

`test: certify Nexora V1 intelligence performance and production readiness`

---

## 11. KPI NORTH STAR

Implement and reconcile:

```text
Profitable RFQ Coverage =
Viable RFQ lines quoted within customer SLA and commercial policy
÷ Total viable RFQ lines received
```

Supporting KPIs:

- inquiry capture;
- action rate;
- local processing;
- critical-field accuracy;
- time to structured Lead;
- time to Quote;
- no-quote reasons;
- follow-up compliance;
- conversion/value conversion;
- gross margin where verified;
- stockout-blocked opportunity;
- Supplier response/competitiveness;
- Sales Rep context-adjusted execution;
- cost per commercially actionable line.

Every KPI must have:

- formula;
- source events;
- freshness;
- sample threshold;
- tenant/role scope;
- drill-down;
- duplicate/revision-safe semantics.

Show “Awaiting sufficient evidence” rather than fabricated values.

---

## 12. NON-NEGOTIABLE REJECTION RULES

Reject and fix any implementation that:

- rewrites a proven foundation without evidence;
- adds UI without persisted logic;
- creates disconnected modules;
- fabricates Suppliers, prices, stock, status, delivery or margin;
- uses raw On Hand as ATP;
- calls Supplier stock company inventory;
- treats Client PO as RFQ;
- silently accepts PO discrepancies;
- ranks Sales Reps from clicks or insufficient samples;
- recommends stock from RFQ frequency alone;
- claims Supplier causation or delivery performance without data;
- silently invokes external AI;
- bypasses the AI Gateway;
- leaks commercial memory across tenants;
- uses mocked business responses as product acceptance;
- has hidden/test-only routes or dead actions;
- regresses benchmarks without explanation;
- declares completion from tests without visible normal-app behavior.

---

## 13. DELIVERY DISCIPLINE

Before edits:

- report branch, HEAD, working tree and recent checkpoints;
- preserve unrelated work and historical migrations;
- create a new local completion branch when appropriate.

At each gate:

- visible working outcome;
- real persisted data;
- focused backend/PostgreSQL/HTTP/browser tests;
- independent consultant review;
- immediate P0/P1 fixes;
- one local commit;
- update completion matrix and certification.

Do not push, merge, deploy or access live data without explicit authorization.

Do not start another release automatically.

---

## 14. FINAL ACCEPTANCE

Nexora V1 is complete only when the normal application proves:

1. Every viable inquiry receives an accountable commercial decision.
2. Customer and Sales ownership are correct and explainable.
3. Every requested line has an evidence-backed fulfilment route.
4. Supplier Quotes become traceable Customer Quote cost sources.
5. Client POs match governed Customer Quote revisions.
6. Customer Orders and procurement handoffs preserve line identity.
7. External operational status is truthful and source-attributed.
8. CRM, RFQ, Supplier, Inventory and Sales modules operate at M3+.
9. M4 recommendations show evidence, samples, confidence and override.
10. Local-first AI and external-cost controls are authoritative.
11. Core UX is simple, attractive, accessible and responsive.
12. Real-backend browser journeys pass with zero skips.
13. Security, RLS, migrations, backup/restore and failure tests pass.
14. Performance meets or improves established baselines.
15. No full logistics, finance or unsupported vertical-edition claim is made.

---

## 15. FINAL RESPONSE

Return only:

1. Executive verdict.
2. Current branch and local commits.
3. Before/after maturity matrix.
4. Normal local URL and exact click paths.
5. Uncovered capabilities completed.
6. Basic capabilities upgraded to M3/M4.
7. Visible UX improvements.
8. Commercial intelligence delivered.
9. Integration/status visibility delivered.
10. Local-first AI and learning results.
11. Performance before/after benchmarks.
12. Browser, backend, PostgreSQL, RLS and security results.
13. Migrations and rollback evidence.
14. Screenshot/evidence paths.
15. Remaining P0/P1/P2 items.
16. Honest GO/NO-GO recommendation.

Do not return another architecture essay. Do not begin another release.
