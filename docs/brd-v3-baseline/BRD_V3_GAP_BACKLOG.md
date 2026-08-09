# Nexora BRD v3.0 Gap Backlog — Gate 0

Ordering is dependency-first. “Missing behavior,” “missing evidence,” and “missing tests” are separated. IDs beyond `FR-RFQ` are mapped only at module level because the BRD text is unavailable.

## P0 — Gate 1 entry blockers

| Order | Type | Backlog item | BRD IDs | Exit evidence |
|---:|---|---|---|---|
| 1 | Missing evidence | Restore the exact BRD v3.0 binary, record its hash, extract all 81 summaries plus every Section 10–12 NFR/criterion, and replace unresolved rows. | All 81; Sections 10–12 | Reviewed atomic matrix with no placeholder summaries. |
| 2 | Decision | Approve Phase 1 interpretations for draft status, payment collection, Sales Order ownership, ZATCA/ERP authority, immutable documents versus retention deletion, carrier subset, and mandatory versus recommended architecture. | COM, SPO, DLM, SBF, INV; Sections 10–12 | Signed decision log with affected IDs and acceptance criteria. |
| 3 | Missing evidence | Stand up a production-like Gate 1 environment with real PostgreSQL, worker, durable S3-compatible storage, real malware scanner, OCR bindings and test mailboxes. | FR-RFQ-01, 02, 08; all integration NFRs | `/ready` green plus immutable environment manifest; no development-only provider. |
| 4 | Missing tests | Run the current Phase 1 browser journey against that real stack with zero mocks/retries, two tenants and representative roles. | FR-RFQ-01..08; QTM/COM | Screenshots/traces plus DB reconciliation and 401/403/404 negative evidence. |
| 5 | Missing tests | Build and execute a versioned golden corpus for every required intake format and every required field, including scanned/image and any Arabic/Hijri cases confirmed in scope. | FR-RFQ-02, 04 | Human-keyed ground truth, field/line conservation, explicit refusal cases. |
| 6 | Missing evidence | Demonstrate exact duplicate, amendment and possible-match decisions from real documents through the worker and human UI. | FR-RFQ-05, 06 | Same canonical RFQ, incremented revision, audited human decision, no data loss. |
| 7 | Missing behavior | Add an auditable evidence-read event on successful original-document access; verify tenant denial and hash failure. | FR-RFQ-08 | Actor/time/tenant/document audit row plus browser and PostgreSQL tests. |
| 8 | Missing evidence | Populate pilot sales profiles/ownership/customer identifiers and prove named routing, fallback queue, explanation and reasoned override. | FR-RFQ-07 | Real-browser route, assignment history, denial tests. |
| 9 | Missing evidence | Execute one immutable serial journey through RFQ → quote → accepted customer PO/order → sourcing/PO → receipt/shipment → invoice, reconciling every line and amount. | QTM, COM, SPO, MTR, DLM, INV | Real-stack trace and source-record reconciliation. |
| 10 | Missing behavior | Implement or explicitly integrate the approved ZATCA/ERP invoice authority; block unsupported compliance claims. | INV-01..06; Sections 10–12 | Approved architecture decision and compliance acceptance evidence. |

## P1 — Required after P0 decisions

| Order | Type | Backlog item | BRD IDs | Exit evidence |
|---:|---|---|---|---|
| 11 | Missing behavior | Complete confirmed RFQ extraction fields and numbering semantics, including tenant/year and agreement reference if in scope. | FR-RFQ-03, 04 | PostgreSQL rollover/concurrency tests and browser display. |
| 12 | Missing behavior | Implement only the approved carrier integration subset; remove/disable unsupported carrier claims. | FR-DLM-01..07 | Sandbox webhook/label/tracking tests and failure recovery. |
| 13 | Missing behavior | Reconcile immutable commercial records with lawful byte deletion/legal holds and document the system-of-record boundary. | FR-RFQ-08; INV/SPO/DLM; Sections 10–12 | Data-bearing upgrade/purge tests and approved retention policy. |
| 14 | Missing tests | Add current rendered-browser evidence for quote revision/send/follow-up, customer PO discrepancies, partial fulfillment, partial shipment and invoice issue/cancel. | QTM, COM, DLM, INV | Real-stack Playwright plus DB audit assertions. |
| 15 | Missing tests | Add cross-tenant and least-privilege HTTP/RLS negative tests for every BRD transaction endpoint not already covered. | All modules | Denial matrix by role/module/action/tenant. |
| 16 | Missing evidence | Define measurable thresholds for performance, availability, recovery, accessibility, localization and accuracy. | Sections 10–12 | Signed NFR table with units, loads and pass/fail thresholds. |

## P2 — After pilot core is stable

| Order | Type | Backlog item | BRD IDs | Exit evidence |
|---:|---|---|---|---|
| 17 | Missing evidence | Complete per-requirement UI/API/service/persistence/audit/test mapping for the 73 currently unknown IDs. | QTM through DSH | Zero unknown rows attributable to missing wording. |
| 18 | Missing behavior | Finish localization only for languages approved for Phase 1; current app is intentionally locked to English. | Sections 10–12; affected UI IDs | Full route inventory, RTL/a11y/browser evidence if Arabic is required. |
| 19 | Missing tests | Add dashboard KPI drill-down reconciliation for each exact `FR-DSH` definition and empty/error/role states. | FR-DSH-01..07 | Source-record totals equal rendered KPI cohorts. |
| 20 | Product control | Feature-flag or hide outside-Phase-1 intelligence, finance and platform features selected in the overbuild register. | OUTSIDE_PHASE_1 | Pilot role navigation and API entitlement proof. |
