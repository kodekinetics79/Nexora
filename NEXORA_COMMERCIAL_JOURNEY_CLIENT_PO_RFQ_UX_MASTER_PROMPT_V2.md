# NEXORA COMMERCIAL JOURNEY COMPLETION
## RFQ Command Workspace, Supplier Quote Loop, Customer Quote, Client PO / Customer Order, and Commercial Learning

Read and obey `./AGENTS.md`.

Read the latest Nexora capability register, architecture record, and release certification records only for implementation context. Do not repeat repository-wide reverse engineering.

---

## 1. EXECUTIVE DECISION

Complete one coherent, visible, persisted commercial journey in the ordinary Nexora application:

```text
End Customer RFQ
→ Lead Intelligence
→ Customer and Sales Ownership
→ RFQ Commercial Resolution
→ Inventory Route or Supplier Sourcing
→ Supplier RFQs
→ Supplier Quotes
→ Offer Selection
→ Customer Quote
→ Follow-Up
→ Client PO / Customer Order
→ Procurement Handoff
→ Commercial Outcome Learning
```

Existing completed foundations are frozen unless a reproducible integration defect requires a surgical fix:

- canonical Lead identity;
- duplicate/revision reconciliation;
- Nexora Serial;
- customer/contact resolution;
- Sales Rep profiles and weighted routing;
- Product/part matching;
- Inventory and ATP;
- RFQ and Quote lineage;
- tenant isolation, authorization, RLS, and event ledger.

Do not rebuild these foundations, create competing models, or restart architecture discovery.

This release is not complete when only backend tests pass. It is complete when a normal authenticated business user can discover and complete the complete journey from Dashboard.

---

## 2. PRODUCT BOUNDARY

Nexora is the commercial system of intelligence.

Nexora owns:

- customer RFQ intake;
- Lead and RFQ intelligence;
- item and inventory resolution;
- supplier sourcing;
- Supplier RFQs;
- Supplier Quote Inbox and extraction;
- supplier offer comparison;
- Customer Quote generation;
- follow-up and outcome;
- Client PO / Customer Order ingestion and matching;
- procurement handoff;
- commercial visibility and learning.

External ERP, WMS, TMS, and Finance systems remain systems of record for:

- actual Supplier Purchase Orders;
- goods receipts and warehouse movements;
- pick, pack, dispatch, carrier execution;
- supplier/customer invoices;
- payments and general ledger.

Do not build a full ERP, WMS, logistics, or finance suite in this release.

Nexora may store external operational references and statuses through APIs, webhooks, scheduled synchronization, document ingestion, or controlled manual fallback.

---


## 2A. NON-REGRESSION AND FROZEN FOUNDATIONS

Treat the following as frozen, reusable foundations unless a real-backend browser scenario exposes a reproducible integration defect:

- durable intake occurrences;
- canonical Lead identity, duplicate and revision handling;
- Nexora Serial;
- Customer and Contact resolution;
- Account Owner, Opportunity Owner, Sales Rep profiles, routing, activity and performance foundations;
- Product, Part, Inventory, ATP, Warehouse and reservation foundations;
- existing Supplier, Supplier RFQ, Supplier Quote, offer-comparison and Customer Quote foundations;
- tenant context, HTTP authorization, PostgreSQL RLS, audit/event ledger, evidence storage, idempotency and concurrency protections.

Do not rebuild, rename, fork, or create competing versions of these domains. Make only surgical fixes required to complete the visible commercial journey.

## 2B. LOCAL-FIRST INTELLIGENCE AND COST CONTROL

All document and intelligence processing in this release must preserve Nexora's local-first operating model.

Required processing order:

```text
Known sender/customer/supplier/template
→ native local parser
→ deterministic rules and validation
→ tenant-scoped commercial memory and retrieval
→ local OCR only for required pages/regions
→ local specialist model for unresolved fields
→ human review
→ authorized external fallback only when policy permits
```

Non-negotiable controls:

- external AI is disabled by default;
- no direct provider calls from business modules;
- every external call passes through the centralized AI Gateway;
- external dependency must remain within the configured ceiling, with a target of no more than 10%;
- no silent cloud fallback;
- send only the minimum necessary, redacted content externally;
- record tenant, source occurrence, document, page/field, provider, model, reason, tokens, cost, result, confidence and correlation ID;
- preserve local compute and OCR cost where supported;
- critical uncertain commercial fields must enter review rather than being guessed;
- Supplier prices, Customer prices, margins, ownership and commercial memory remain tenant-scoped;
- approved corrections may update governed operational memory, but production models must not silently retrain or promote after each ingestion.

Apply this to:

- Customer RFQs and revisions;
- Supplier Quotes and revisions;
- Customer Quotes and responses;
- Client POs / Customer Orders;
- operational-status documents;
- attachments and supporting evidence.

## 2C. REAL-BACKEND ACCEPTANCE — NO FIXTURE-BACKED PRODUCT CLAIMS

All product acceptance must run through:

```text
normal Nexora frontend
→ normal authentication
→ tenant middleware/context
→ actual backend HTTP APIs
→ application services
→ PostgreSQL 16
→ RLS and persisted records
```

Synthetic documents and disposable Development/Test seed data are allowed.

The following do not qualify as product acceptance:

- mocked business API responses;
- `page.route()` or equivalent interception that fabricates Nexora responses;
- fixture-only application shells;
- hidden acceptance routes unavailable to normal users;
- hardcoded Suppliers, Quotes, Client POs, Orders, prices or statuses;
- test-only authorization bypass;
- screenshots without executable real-backend behavior.

Every named Playwright scenario must execute against the real backend with zero skips. Browser evidence must show the normal application navigation and persisted state before and after each governed action.


## 3. LOCKED TERMINOLOGY

Use these terms consistently:

- **Nexora Client / Tenant**: the company using Nexora.
- **End Customer**: the tenant’s customer sending an RFQ or PO.
- **Supplier**: the vendor from whom the tenant obtains availability and cost.
- **Customer RFQ**: End Customer → Nexora Client.
- **Supplier RFQ**: Nexora Client → Supplier.
- **Supplier Quote**: Supplier → Nexora Client.
- **Customer Quote**: Nexora Client → End Customer.
- **Client PO / Customer PO**: End Customer’s purchase order to the Nexora Client.
- **Customer Order**: Nexora’s governed record of the accepted Client PO.
- **Procurement Handoff**: Nexora’s instruction/reference package to the client’s ERP/procurement system.
- **External Supplier PO**: the actual supplier order created in the ERP/procurement system.

Never label all RFQs or Quotes generically.

---

## 4. PERMANENT IDENTITY BACKBONE

Preserve:

- `NexoraSerial`: complete commercial journey identity.
- `DemandLineId`: permanent identity of each customer-requested line.

Line-level lineage must support:

```text
DemandLineId
→ CustomerRfqLineId
→ SupplierRfqLineId(s)
→ SupplierQuoteLineId(s)
→ SelectedSourcingDecisionLineId
→ CustomerQuoteLineId
→ CustomerOrderLineId
→ ProcurementHandoffLineId
→ ExternalPurchaseOrderNumber + ExternalPurchaseOrderLineNumber
```

Every downstream record must remain tenant-qualified and retain source evidence.

This is mandatory commercial traceability, not a full logistics engine.

---

## 5. BOUNDED SME AND CONSULTANT TEAM

Use no more than seven non-overlapping principal-level lanes:

1. **RFQ-to-Revenue Product and Commercial SME**
   - validates the complete commercial journey, terminology, and business states.

2. **Strategic Sourcing and Supplier Quote SME**
   - owns Supplier RFQ, Supplier Quote Inbox, comparison, award, and sourcing decisions.

3. **CRM, Sales Operations, and Customer Order SME**
   - owns customer ownership, follow-up, Client PO matching, partial awards, and sales accountability.

4. **CPQ, Pricing, and Commercial Finance SME**
   - owns cost-source lineage, landed cost, margin policy, validity, and pricing evidence without building accounting.

5. **Enterprise UX / UI and Customer Experience SME**
   - redesigns the RFQ workspace and ensures simple, attractive, responsive, accessible journeys.

6. **Backend, Data, Integration, and Security Implementation SME**
   - owns domain services, APIs, schema, migrations, idempotency, RLS, outbox, and operational-system handoff.

7. **Independent Consultant / QA Reviewer**
   - challenges the implemented behavior for commercial accuracy, usability, authorization, data integrity, and misleading claims.

One Integration Owner controls shared contracts, schema, migrations, and merge order.

Agents must inspect only affected areas. No duplicated repository-wide scans. Consultants review implemented code and browser behavior; accepted P0/P1 findings are fixed in the same run.

---

## 6. NORMAL APPLICATION INFORMATION ARCHITECTURE

The ordinary authenticated sidebar must expose:

```text
Dashboard

Lead Intelligence
  → Today
  → All Inquiries
  → Needs Review
  → Bulk Uploads
  → Duplicates
  → Revisions

RFQ Management
  → All Customer RFQs
  → Needs Commercial Review
  → Sourcing Required
  → Ready for Customer Quote

Supplier Management
  → Suppliers
  → Supplier Contacts
  → Supplier Items
  → Sourcing Cases
  → Supplier RFQs
  → Supplier Quote Inbox
  → Offer Comparison
  → Supplier Performance

Quote Management
  → Customer Quote Drafts
  → Sent Customer Quotes
  → Follow-Up Due
  → Won / Lost / Partial

Customer Orders
  → Client PO Inbox
  → Matching Review
  → Confirmed Orders
  → Partial Awards
  → Discrepancies
  → Procurement Handoffs
  → Operational Status

Sales Management
Inventory
Analytics
```

No hidden routes, fixture-only shells, test-only authentication, or dead navigation.

---

## 7. RFQ COMMAND WORKSPACE — REQUIRED REDESIGN

The current RFQ experience is too shallow and visually ordinary. Transform RFQ Detail into a modern commercial command workspace.

### 7.1 Header

Show prominently:

- Nexora Serial;
- Customer RFQ number;
- customer and contact;
- Account Owner and Opportunity Owner;
- received date;
- customer deadline and SLA risk;
- RFQ value where reliable;
- readiness percentage;
- current stage;
- primary blocker;
- recommended next action.

### 7.2 Visual structure

Use a restrained modern enterprise design:

- minimal base layout;
- bento-style summary cards;
- subtle glass treatment only for header/overlay surfaces;
- generous whitespace;
- semantic icons with text;
- clear typography hierarchy;
- sticky primary action bar;
- responsive table-to-card behavior;
- accessible focus, contrast, and keyboard operation.

Avoid excessive decoration, tiny dense text, icon-only actions, and flat data dumps.

### 7.3 Summary cards

Show:

- total lines;
- ready from stock;
- partially available;
- sourcing required;
- Supplier Quotes received;
- unresolved item matches;
- pricing pending;
- ready for Customer Quote.

Every card must be clickable and filter the lines below.

### 7.4 Line-level commercial matrix

Each RFQ line must show:

- customer-requested item;
- manufacturer;
- quantity/UOM;
- item match and evidence;
- inventory state;
- ATP;
- incoming stock;
- known suppliers;
- Supplier RFQ state;
- Supplier Quote count;
- selected cost source;
- lead-time source;
- pricing state;
- fulfilment route;
- next action.

Supported routes:

- `FULFIL_FROM_STOCK`
- `SPLIT_STOCK_AND_SOURCE`
- `FULFIL_FROM_INCOMING`
- `SOURCE_FROM_KNOWN_SUPPLIER`
- `DISCOVER_NEW_SUPPLIER`
- `PROPOSE_APPROVED_ALTERNATE`
- `CUSTOMER_CLARIFICATION_REQUIRED`
- `SERVICE_LINE`
- `DECLINE_LINE`

### 7.5 Progressive disclosure

Level 1: status, recommendation, next action.  
Level 2: stock, suppliers, pricing, lead time.  
Level 3: source evidence, calculations, confidence, audit history.

### 7.6 Source evidence

Provide an evidence drawer or split view showing:

- original email/document;
- page/sheet/row/cell or bounding box;
- original and normalized values;
- revisions;
- reviewer corrections;
- extraction path and confidence.

---

## 8. SUPPLIER SOURCING AND SUPPLIER QUOTE LOOP

For each partial, out-of-stock, known non-stock, or unknown line:

```text
Create Sourcing Case
→ Search internal supplier intelligence
→ Optionally discover approved suppliers
→ Select suppliers
→ Send Supplier RFQs
→ Receive Supplier Quotes
→ Validate and compare offers
→ Select one or split award
```

### 8.1 Supplier discovery

Search internal sources first:

- Supplier-Item history;
- previous Supplier Quotes;
- previous sourcing decisions;
- approved suppliers;
- manufacturer/category relationships;
- approved alternates.

Allow 10, 20, 50, or policy-approved custom result limits.

External discovery is:

- disabled by default;
- gateway-controlled;
- tenant-aware;
- metered;
- auditable;
- never silent;
- prohibited from inventing suppliers or availability.

### 8.2 Supplier RFQ

Generate editable outbound Supplier RFQs containing:

- Nexora Serial;
- Sourcing Case;
- Supplier-specific RFQ reference;
- requested part/manufacturer;
- quantity/UOM;
- required date;
- delivery location;
- requested currency/Incoterms;
- price validity;
- availability;
- lead time;
- MOQ;
- warranty/origin/certificates;
- response deadline;
- approved attachments.

Track draft, approved, queued, sent, delivered where available, bounced, responded, declined, follow-up due, expired, and cancelled.

Use idempotent outbox behavior. Retries must not send duplicate messages.

### 8.3 Supplier Quote Inbox

Accept Supplier Quotes from:

- reply email;
- email body;
- PDF;
- Excel/CSV;
- Word;
- scanned image;
- secure response link;
- supplier portal;
- API;
- manual/offline entry.

Match using Supplier RFQ reference, secure token, email thread, verified supplier identity, Nexora Serial, part/quantity overlap, and governed review when ambiguous.

Extract and preserve:

- Supplier Quote number and revision;
- supplier;
- part/manufacturer;
- quantity/available quantity;
- unit price/currency;
- MOQ/price breaks;
- availability;
- lead time type;
- validity;
- Incoterms/freight;
- payment terms;
- warranty/origin;
- alternates;
- exceptions;
- source evidence;
- field confidence.

Critical uncertain fields must enter review.

### 8.4 Offer comparison

Compare line by line:

- unit cost;
- landed cost where verified;
- available quantity;
- partial availability;
- lead time;
- MOQ;
- validity;
- currency/exchange date;
- freight/Incoterms;
- payment terms;
- authorization;
- response time;
- quote completeness;
- historical competitiveness;
- risk.

Show:

- cheapest;
- fastest;
- lowest landed cost;
- lowest risk;
- best overall;
- recommended split award.

Every recommendation must explain itself. User retains final selection authority.

---

## 9. CUSTOMER QUOTE

The selected cost/fulfilment source for every Customer Quote line must be explicit:

- `INTERNAL_INVENTORY`
- `SELECTED_SUPPLIER_QUOTE`
- `MIXED_INVENTORY_AND_SUPPLIER`
- `INCOMING_INVENTORY`
- `APPROVED_ALTERNATE`
- `SERVICE`

Persist:

```text
CustomerQuoteLine
→ DemandLineId
→ CustomerRfqLineId
→ SourcingCaseId where applicable
→ SelectedSupplierQuoteLineId where applicable
→ InventorySnapshotId where applicable
→ LandedCostDecisionId
→ PricingDecisionId
```

The user must be able to click the pricing source and see exact evidence.

Never invent cost, price, lead time, stock, freight, tax, or margin.

Warn or block when:

- supporting Supplier Quote expired;
- Supplier Quote validity is shorter than Customer Quote validity;
- inventory changed;
- required cost is unverified;
- required lead time is unconfirmed;
- margin policy is violated.

After sending a Customer Quote, create accountable follow-up only after delivery is confirmed.

---

## 10. CLIENT PO / CUSTOMER ORDER MODULE

This is a mandatory visible module. Do not treat a Customer PO as a new RFQ.

### 10.1 Client PO Inbox

Ingest Customer POs from:

- email body/attachment;
- PDF;
- Excel/CSV;
- portal;
- API/EDI;
- manual upload.

Classify and match using:

- Nexora Serial;
- Customer Quote number/revision;
- Customer RFQ reference;
- Customer PO number;
- email thread;
- customer/contact;
- part, quantity, price, and currency overlap.

### 10.2 Matching review

Compare each Customer PO line against the selected Customer Quote revision:

- part;
- manufacturer;
- quantity;
- UOM;
- price;
- currency;
- delivery date;
- ship-to;
- payment terms;
- quote validity.

Outcomes:

- exact acceptance;
- partial award;
- quantity change;
- price discrepancy;
- delivery discrepancy;
- unquoted line;
- substitution;
- expired Quote;
- ambiguous match.

Show a clear side-by-side discrepancy matrix.

Actions:

- Accept;
- Accept with Approval;
- Create Partial Customer Order;
- Request Customer Correction;
- Reopen Customer Quote;
- Reject Line;
- Leave in Review.

All actions are authorized, idempotent, and audited.

### 10.3 Customer Order

On acceptance, create a governed Customer Order retaining:

- Nexora Serial;
- Customer PO number;
- Customer Quote and revision;
- customer/contact;
- Account and Opportunity Owners;
- DemandLineId;
- ordered quantity and accepted price;
- promised delivery;
- fulfilment source;
- selected Supplier Quote where applicable;
- commercial status;
- next action.

This record is evidence of a won or partial outcome and updates commercial memory.

---

## 11. PROCUREMENT HANDOFF — NOT FULL PROCUREMENT

For sourced Customer Order lines, create a Procurement Handoff:

- Nexora Serial;
- Customer Order and line;
- DemandLineId;
- Sourcing Decision;
- selected Supplier Quote line;
- Supplier;
- required quantity;
- selected cost/currency;
- required delivery;
- warehouse or drop-ship destination;
- external-system target;
- handoff status;
- idempotency key.

The external ERP/procurement system creates the actual Supplier PO.

Send line-level references:

- Nexora Serial;
- DemandLineId;
- SourcingDecisionId;
- SelectedSupplierQuoteLineId.

Receive and store:

- External Supplier PO number;
- External Supplier PO line number;
- ordered quantity;
- approved cost;
- expected date;
- status;
- last synchronization;
- source of truth.

Support split sourcing:

```text
DemandLine
├── internal inventory quantity
├── Supplier A external PO line quantity
└── Supplier B external PO line quantity
```

Do not build warehouse receiving, pick/pack, carrier execution, AP, GL, or payment processing.

When no integration is configured, show honest status and allow controlled manual reference entry.

---

## 12. OPERATIONAL STATUS VISIBILITY

Nexora may display read-only or synchronized statuses from ERP/WMS/TMS:

- procurement handoff created;
- external PO created;
- supplier confirmed;
- expected date changed;
- partially received;
- received/accepted;
- available for fulfilment;
- dispatched;
- delivered.

Every status must show:

- source system;
- last synchronized date;
- authoritative/not-authoritative state.

Never infer:

- Supplier Quote = inventory;
- Supplier PO = received stock;
- in transit = ATP;
- unverified status = confirmed delivery.

---

## 13. COMMERCIAL MEMORY AND LEARNING

Every verified event updates tenant-scoped intelligence.

### 13.1 Part/Product memory

Show:

- times requested;
- Customer Quotes issued;
- decided;
- won/lost/pending;
- line win rate;
- last won selling price;
- last won quantity/customer/date context;
- winning price range;
- last/median Supplier cost;
- winning lead-time range;
- suppliers supporting successful Orders;
- stock status at quotation;
- loss reasons;
- stockout-blocked opportunities.

Do not say “never won” without time period, decided sample, pending count, and evidence.

### 13.2 Supplier intelligence

Separate:

- responsiveness;
- quote completeness;
- price competitiveness;
- landed-cost competitiveness;
- selected-offer contribution;
- Customer Quote wins supported;
- later promise-versus-actual metrics only when operational-system data exists.

Use “contributed to” rather than claiming causation.

### 13.3 Sales Rep intelligence

Measure from real events:

- weighted opportunity coverage;
- time to first action;
- Quote turnaround;
- customer insight capture;
- follow-up completion;
- conversion;
- value conversion;
- margin where verified;
- quality and incorrect commitments.

Separate:

- Account Owner credit;
- Opportunity Owner execution;
- sourcing/technical/pricing contribution;
- management intervention.

### 13.4 Customer intelligence

Show:

- RFQ volume;
- Quote coverage;
- conversion;
- accepted price behavior;
- revision burden;
- response behavior;
- profitability where reliable;
- payment/delivery metrics only when integrated.

### 13.5 Learning controls

Use three layers:

1. approved immediate operational memory;
2. deterministic analytics from verified events;
3. predictive recommendations only after minimum sample, offline evaluation, shadow mode, approval, and rollback.

No silent retraining after every ingestion. No cross-tenant learning of prices, margins, ownership, or supplier terms.

---

## 14. ROLE-BASED UX

Primary users:

- Sales Representative;
- Sales Manager;
- Sourcing/Procurement User;
- Inventory User;
- Commercial/Pricing Approver;
- Tenant Administrator;
- Platform Administrator.

Logistics and Finance departments do not need to operate Nexora in this release.

Provide controlled read-only status views or integration administration when appropriate.

---

## 15. VISIBLE ACCEPTANCE SCENARIO

Use authorized synthetic Development/Test data.

Create one End Customer RFQ containing:

1. In-stock line.
2. Partially available line.
3. Known out-of-stock line with known suppliers.
4. Unknown/new part.

Prove in the ordinary browser:

- RFQ Command Workspace is visually polished and usable.
- In-stock line proceeds without sourcing.
- Partial line splits stock and sourced quantity.
- Out-of-stock line sends Supplier RFQs.
- Unknown line requests 10 supplier candidates.
- Supplier Quotes arrive through email/PDF/Excel fixtures.
- Supplier Quote Inbox classifies and extracts them.
- Offer comparison selects Supplier A.
- Customer Quote uses exact Inventory/Supplier Quote sources.
- Follow-up is created after confirmed send.
- A Client PO is ingested and matched to the Customer Quote.
- Exact and partial award paths work.
- Customer Order is created.
- Procurement Handoff is created for sourced quantities.
- An external PO number/line is returned by a disposable integration fixture.
- Product, Supplier, Sales Rep, and Customer commercial memory update.
- No false physical inventory is created.

---

## 16. PLAYWRIGHT ACCEPTANCE

Run separate scenarios with zero skips:

1. RFQ Command Workspace normal navigation.
2. RFQ header/readiness/owner/deadline.
3. Line filters and progressive disclosure.
4. Source evidence drawer.
5. In-stock line bypasses sourcing.
6. Partial line creates sourcing for balance.
7. Known out-of-stock line shows suppliers.
8. Unknown line uses 10/20/50 selector.
9. Supplier RFQ preview/edit/send.
10. Multi-supplier send idempotency.
11. Supplier Quote Inbox.
12. Supplier Quote PDF/Excel extraction.
13. Critical low-confidence field review.
14. Offer comparison.
15. Supplier selection.
16. Customer Quote cost-source evidence.
17. Supplier validity warning.
18. Customer Quote send/follow-up.
19. Client PO Inbox.
20. Exact Customer PO match.
21. Partial award.
22. Price/quantity discrepancy review.
23. Customer Order creation.
24. Procurement Handoff.
25. External Supplier PO reference callback.
26. Commercial memory update.
27. Denied-role behavior.
28. Cross-tenant isolation.
29. Responsive/mobile RFQ workspace.
30. Responsive/mobile Client PO review.

---

## 17. ENGINEERING GATES

Run:

- focused RFQ workspace/API tests;
- supplier sourcing tests;
- Supplier RFQ/outbox tests;
- Supplier Quote extraction and matching tests;
- offer comparison and pricing-source tests;
- Customer Quote tests;
- Customer PO matching tests;
- Customer Order and Procurement Handoff tests;
- event/intelligence tests;
- idempotency and concurrency tests;
- authenticated HTTP + tenant middleware + authorization + PostgreSQL RLS tests;
- real-backend Playwright scenarios with zero mocked business responses and zero skips;
- backend build;
- frontend lint/build;
- EF drift and migration checks;
- security scans;
- `git diff --check`.

Use populated PostgreSQL upgrade, restore, and re-upgrade when schema changes.

---

## 18. DELIVERY DISCIPLINE

Within 30 minutes, deliver a visible checkpoint in the normal app:

- redesigned RFQ header;
- line-level readiness cards;
- Client PO / Customer Orders navigation visible.

Then continue to the full journey.

Use local checkpoint commits only:

1. `feat: redesign RFQ commercial command workspace`
2. `feat: complete Supplier Quote to Customer Quote journey`
3. `feat: add Client PO matching and Customer Orders`
4. `feat: wire procurement handoff and commercial learning`

Do not push, merge, deploy, access live data, or begin another release automatically.

---

## 19. REJECTION RULES

Reject and fix any implementation that:

- omits Client PO / Customer Orders from normal navigation;
- calls Client PO another RFQ;
- uses shallow record-detail RFQ presentation;
- uses hidden/test-only routes;
- creates fake suppliers or prices;
- creates company inventory from Supplier Quotes;
- silently accepts PO discrepancies;
- lacks line-level identity;
- loses Nexora Serial;
- invents operational statuses;
- builds full logistics/finance execution;
- reports completion without real-backend browser behavior;
- uses fixture-backed or mocked business responses as acceptance evidence;
- bypasses the AI Gateway or silently invokes an external provider;
- exceeds the configured external-dependency ceiling without an approved exception;
- returns only tests or documentation.

---

## 20. FINAL RESPONSE

Return only:

1. Executive verdict.
2. Normal local URL and startup commands.
3. Exact click path from Dashboard.
4. RFQ Command Workspace completed.
5. Supplier RFQ/Supplier Quote journey completed.
6. Customer Quote completed.
7. Client PO Inbox and matching completed.
8. Customer Order completed.
9. Procurement Handoff and external PO linkage completed.
10. Commercial memory updates completed.
11. Screenshot/evidence paths.
12. Playwright results.
13. Backend/PostgreSQL/frontend results.
14. Migrations and backfill.
15. Files changed.
16. Four local commit hashes.
17. Remaining blockers.
18. Honest GO/NO-GO recommendation.

Do not return another architecture essay. Do not begin logistics, finance, or another release automatically.
