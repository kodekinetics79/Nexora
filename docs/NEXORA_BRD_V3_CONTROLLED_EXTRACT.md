# NEXORA BRD v3.0 — CONTROLLED PHASE 1 REQUIREMENTS EXTRACT

> **CONTROL STATUS: THIS IS THE PHASE 1 FUNCTIONAL CEILING.**
> No feature outside this extract may be built, and no gate may be skipped or combined,
> without written product-owner approval. The original BRD DOCX remains the legal source
> and controls if wording differs. Where this extract records an ambiguity, engineering
> MUST NOT invent an answer — it goes to the Decision Register.

## 1. Document identity and control status

| Field | Value |
|---|---|
| Document | Business Requirement Document — CRM & RFQ-to-Delivery Management System (Nexora) |
| Client | Tech Connect |
| Region | GCC; primary market Kingdom of Saudi Arabia (KSA) |
| Version | 3.0 (Revised & Expanded) |
| Date | 28 July 2026 |
| Status in source | Draft for Review |
| Classification | Confidential — Internal & Selected Partner Use Only |
| Source length | 43 pages |
| Source DOCX SHA-256 | `736f6acf5b527f8872d5d46f357ea823c7ef4de1da16642cbda8a70a12bbe2b4` |
| Functional requirements | 81 across 13 modules |
| Approval state | All five required approvers are `[TBD]` — no signatures, no dates |

## 2. Authoritative transaction spine

Nexora is a KSA-first, GCC-extensible CRM and RFQ-to-delivery platform replacing fragmented
email, portal, scanned-document and spreadsheet activity with a single governed lifecycle:

```
RFQ → Quote Revision → Customer PO → Sales Order → Supplier PO → Shipment
    → Goods Receipt/Lot → Delivery/POD → ZATCA Invoice
```

One traceable chain must connect the originating RFQ to every downstream commercial and
operational record. Phase 1 must complete this chain before any post-BRD innovation.

### The core transaction — what must work end to end

1. Customer PO upload and governed quotation matching.
2. Full/partial/not-awarded classification at line level.
3. Price and quantity discrepancy review.
4. Confirmed Sales Order as the authoritative commercial bridge.
5. Supplier PO drafting, approval, dispatch and acknowledgement.
6. Goods receipt with lot/batch/serial traceability.
7. Shipment milestones and updated material availability.
8. Warehouse stock ledger, reservations and ATP calculation.
9. Partial delivery, delivery note and POD.
10. ZATCA-compliant invoice generation and evidence.
11. End-to-end audit trail across the entire reference chain.

## 3. Phase 1 scope boundary

### In scope

- Email, manual and watched-folder RFQ intake; Arabic/English OCR and extraction; revisions and duplicate review.
- Supplier RFQ dispatch, supplier quote capture/comparison, LOA approval and customer quotation generation.
- Customer PO upload, extraction, line matching, award classification and discrepancy review.
- Supplier PO drafting, approval, acknowledgement and status tracking.
- Lot/batch/serial traceability, certificate storage and where-used/where-from trace.
- Shipment milestones, ETA/material-availability calculation and delay alerts.
- Delivery notes, partial delivery, POD, delivery exceptions and ZATCA invoice generation.
- Rule-based follow-ups and escalation across the lifecycle.
- Inventory balances, ATP, goods receipt/issue and reorder alerts.
- Customer profile/360-degree view and account-based access.
- Governed item, customer, vendor and price-list master data with bulk import/export.
- At least two years of purchase and quotation history, supplier recommendation and trends.
- Role-based dashboards, drill-down and scheduled reports.
- Defined ERP integration points and an API-ready architecture; Nexora may operate standalone in Phase 1.

### Explicitly OUT of scope for Phase 1

- Direct automatic download from every customer portal API.
- Automated supplier e-auction or reverse bidding.
- Completely touchless customer-PO matching; exceptions retain human review.
- Supplier EDI/cXML integration.
- Blockchain provenance.
- Live carrier API integration for all carriers; only an approved subset is expected.
- Own-fleet route optimization and telematics.
- AI-predictive next-best-action recommendations.
- A complete WMS with bin slotting and pick-path optimization.
- Customer self-service portal.
- Live real-time ERP master-data synchronization.
- Predictive pricing or ML-based quote optimization.
- Embedded self-service BI authoring for all users.
- Live production ERP integration, pending ERP selection and a Phase 2 contract.

## 4. Functional requirements — 81 authoritative parent requirements

### Module 1 — RFQ Ingestion (FR-RFQ-01..08)

*Purpose: capture RFQs/RFPs from approved inbound channels, normalize them into structured records and start the governed workflow.*

- **FR-RFQ-01** — Provide three ingestion methods: email through a dedicated SMTP/IMAP mailbox monitored continuously; manual single or batch drag-and-drop upload; and a watched network/SFTP/SharePoint folder synchronized with customer portals such as Etimad, SAP Ariba or Oracle iSupplier.
- **FR-RFQ-02** — Support native and scanned PDF, DOCX, XLSX, HTML, JPEG, PNG, Outlook MSG and EML.
- **FR-RFQ-03** — Generate a unique ID in the form `RFQ-KSA-{YYYY}-{sequence}` and maintain a separate agreement/contract reference for standing RFQ agreements.
- **FR-RFQ-04** — Apply bilingual Arabic/English OCR and NLP extraction for buyer name, RFQ/bid number, item description, quantity, UoM, required delivery date, Saudi region/city delivery location, Gregorian/Hijri closing date and time, manufacturer, part number and special notes.
- **FR-RFQ-05** — When a closing-date amendment is received, version the existing RFQ (v1, v2, etc.) rather than create a duplicate.
- **FR-RFQ-06** — Flag possible duplicates based on the same buyer, same item and overlapping dates for human review before a new record is created.
- **FR-RFQ-07** — Route an ingested RFQ to the correct Business Unit or Sales Engineer queue using customer, product-category or region rules configured in Master Data.
- **FR-RFQ-08** — Retain the original email/document/image as an immutable attachment linked to the RFQ for audit purposes.

### Module 2 — Quote Management (FR-QTM-01..08)

*Purpose: manage supplier solicitation through internal approval and customer quotation submission.*

- **FR-QTM-01** — Generate a supplier RFQ from selected customer-RFQ lines using a configurable Arabic/English template and dispatch it to Tier 1, Tier 2 or Tier 3 suppliers.
- **FR-QTM-02** — Capture supplier quotations through portal submission or document upload and extract unit price, currency, lead time, validity and Incoterm.
- **FR-QTM-03** — Compare multiple supplier quotations per RFQ line side by side using configurable weighted scoring for price, lead time, quality/warranty and payment terms.
- **FR-QTM-04** — Require an LOA approval step that records approver, date and approved margin/pricing before releasing a sales quotation.
- **FR-QTM-05** — Calculate customer quotation pricing with Saudi landed-cost components: customs duty, freight, applicable SASO/SABER cost and 15% VAT, itemized clearly.
- **FR-QTM-06** — Generate a uniquely numbered bilingual PDF sales quotation linked to the RFQ ID and Response ID.
- **FR-QTM-07** — Track validity and automatically mark a quotation Expired after its validity date, or after 90 days from submission when no customer response is recorded.
- **FR-QTM-08** — Support multiple quotation revisions against one RFQ and retain the complete revision history.

### Module 3 — Customer Order Matching (FR-COM-01..07)

*Purpose: match customer awards to the submitted quotation and establish the authoritative Sales Order bridge.*

- **FR-COM-01** — Upload a customer PO in native/scanned PDF and extract PO number, PO date, line items, awarded quantity and unit price.
- **FR-COM-02** — Match PO lines to the originating RFQ and quotation using item code, manufacturer and part number.
- **FR-COM-03** — Classify every RFQ line as Fully Awarded, Partially Awarded or Not Awarded, and calculate win value and win ratio.
- **FR-COM-04** — Flag quoted-price versus PO-price differences beyond a configurable tolerance, such as ±2%, for manual review.
- **FR-COM-05** — Support multiple customer POs against one RFQ/quotation for call-off or blanket orders, tracking cumulative released quantity against awarded quantity.
- **FR-COM-06** — When a matched order is confirmed, automatically generate the supplier-PO demand required for procurement.
- **FR-COM-07** — Maintain the Sales Order as the single source of truth linking RFQ → quotation → customer PO → Sales Order → supplier PO → delivery.

### Module 4 — Supplier PO Management (FR-SPO-01..07)

*Purpose: create and govern procurement against a confirmed customer award.*

- **FR-SPO-01** — Auto-draft a supplier PO from awarded Sales Order lines and the winning supplier quotation, requiring buyer approval before release.
- **FR-SPO-02** — Support both one-supplier and split-supplier POs for an RFQ whose lines are awarded to different suppliers.
- **FR-SPO-03** — Capture supplier acknowledgement as accept, reject or counter, including a revised lead time when applicable.
- **FR-SPO-04** — Track Draft, Approved, Sent, Acknowledged, In Production, Shipped, Received and Closed statuses.
- **FR-SPO-05** — Link every supplier PO to its customer PO and Sales Order for end-to-end traceability.
- **FR-SPO-06** — Support Saudi/GCC and international suppliers, with Incoterm, port of loading/discharge and customs/HS code.
- **FR-SPO-07** — Remind buyers/suppliers ahead of committed ship dates and escalate overdue acknowledgements.

### Module 5 — Material Traceability (FR-MTR-01..05)

*Purpose: trace material origin, compliance evidence and downstream use.*

- **FR-MTR-01** — Assign and track lot, batch or serial numbers for received material against the supplier PO and link them forward to the Sales Order and delivery note.
- **FR-MTR-02** — Store manufacturer certificates, Certificate of Origin, Certificate of Conformity and SASO/SABER certificates by lot, including expiry where applicable.
- **FR-MTR-03** — Provide both where-from and where-used trace: lot to originating supplier PO, and Sales Order to all fulfilment lots.
- **FR-MTR-04** — Record country of origin and manufacturer per line for customs and customer compliance reporting.
- **FR-MTR-05** — Quarantine lots under supplier recall or quality hold and block allocation until authorized release.

### Module 6 — Material Availability from Shipment (FR-MAS-01..05)

*Purpose: provide visibility of inbound supply before warehouse receipt.*

- **FR-MAS-01** — Capture Ready at Factory, Departed Origin, In Transit, Arrived Saudi Port, Customs Clearance and Received at Warehouse milestones, including named Saudi port/location options.
- **FR-MAS-02** — Support carrier/freight-forwarder tracking through either API or manual update to maintain status and ETA.
- **FR-MAS-03** — Calculate Material Available Date from shipment ETA plus customs-clearance and putaway lead time, then propagate it to Delivery Management.
- **FR-MAS-04** — Alert buyer and sales staff when a shipment exceeds the committed ship date or puts the customer's required delivery date at risk.
- **FR-MAS-05** — Support multiple partial shipments against one supplier PO, with availability tracked per shipment and lot.

### Module 7 — Delivery Management (FR-DLM-01..07)

*Purpose: schedule, execute and evidence final delivery, then invoke the approved invoicing boundary.*

- **FR-DLM-01** — Generate an Arabic/English Delivery Note or Waybill referencing the Sales Order, material lots and customer PO, with the address mapped to the Saudi region/city master.
- **FR-DLM-02** — Support partial deliveries per Sales Order line and track cumulative delivered versus awarded quantity.
- **FR-DLM-03** — Capture POD signature, stamp, photo, GPS timestamp and receiving contact.
- **FR-DLM-04** — Integrate with ZATCA Fatoora Phase 2 to generate a cleared/reported compliant tax invoice with QR code upon delivery confirmation.
- **FR-DLM-05** — Schedule delivery routes/vehicles and show Scheduled, Dispatched, In Transit, Delivered and Delivery Exception statuses.
- **FR-DLM-06** — Notify the customer by email, SMS or WhatsApp at dispatch and delivery confirmation.
- **FR-DLM-07** — Record rejected goods, short shipment and damage exceptions and link each to a corrective-action workflow.

### Module 8 — System-Based Follow-Up (FR-SBF-01..05)

*Purpose: replace spreadsheet-based chasing with rule-driven work across the RFQ-to-cash lifecycle.*

- **FR-SBF-01** — Create automated reminders for pending Quote/No-Quote decisions, RFQs approaching closure without a decision, supplier responses overdue against SLA, shipments approaching/missing ETA and invoices approaching payment due date.
- **FR-SBF-02** — Support configurable escalations, including supervisor escalation when a task is overdue by a configured number of business days.
- **FR-SBF-03** — Maintain an RFQ/order activity log of every automated and manual follow-up, its channel and outcome.
- **FR-SBF-04** — Configure follow-up rules globally, by customer account or by product category.
- **FR-SBF-05** — Give each user one *My Follow-ups* worklist consolidating pending actions across modules.

### Module 9 — Inventory (FR-INV-01..06)

*Purpose: maintain stock, inbound supply and reservations for quoting and fulfilment.*

- **FR-INV-01** — Maintain quantities by item, warehouse/location and lot, separating on-hand, Sales-Order-reserved and shipment-based in-transit stock.
- **FR-INV-02** — Calculate ATP as on-hand plus incoming shipments minus existing reservations and expose it during quotation preparation.
- **FR-INV-03** — Record goods receipts against supplier POs with quantity, lot and condition, and goods issues against delivery notes.
- **FR-INV-04** — Maintain item min/max and reorder points and generate reorder alerts.
- **FR-INV-05** — Support cycle counts/stock takes with variance reporting.
- **FR-INV-06** — Report stock ageing for slow-moving and obsolete items.

### Module 10 — Customer (FR-CST-01..05)

*Purpose: provide a governed account record and operational 360-degree view.*

- **FR-CST-01** — Maintain CR number, VAT registration number, Government/Semi-Government/Private sector, Saudi region and assigned account team.
- **FR-CST-02** — Restrict customer records, dashboards and tasks to the assigned account team; supervisors/managers may access multiple accounts within their scope.
- **FR-CST-03** — Show open RFQs, quotations in progress, order status and delivery status in one customer view.
- **FR-CST-04** — Maintain customer-specific price agreements/frame contracts and preferred Incoterms/payment terms.
- **FR-CST-05** — Track RFQ win ratio, honored response time, on-time delivery rate and payment behavior.

### Module 11 — Master Data (FR-MDM-01..06)

*Purpose: supply governed item, customer, vendor and pricing truth to every transaction module.*

- **FR-MDM-01** — Map customer material codes to Tech Connect Sage codes, manufacturer part number, UoM and preferred suppliers, with Excel upload and API sync to purchase-history sources.
- **FR-MDM-02** — Maintain Customer Master records linked to unique BP numbers for RFQ/RFP association and reporting.
- **FR-MDM-03** — Classify vendors as Tier 1 Partner, Tier 2 Extended Network or Tier 3 Out-of-Network and record brand/manufacturer authorizations.
- **FR-MDM-04** — Maintain vendor and customer price lists with effective dates, currency and approval status, including LOA-approved pricing.
- **FR-MDM-05** — Limit create/edit rights to authorized master-data administrators and audit every change with before/after values.
- **FR-MDM-06** — Provide Excel-template bulk upload/download for all master-data types and an API for future ERP synchronization.

### Module 12 — Purchase and Quotation History (FR-PQH-01..05)

*Purpose: preserve evidence and reuse commercial history in current decisions.*

- **FR-PQH-01** — Retain at least two years, configurable, of purchase orders with PO number/date, supplier, BP number, part number, manufacturer, unit price and quantity.
- **FR-PQH-02** — Retain issued quotations, all revisions, win/loss outcome and loss reason where captured.
- **FR-PQH-03** — On creation of a new RFQ line, search retained purchase history by part number and manufacturer and surface suppliers with prior success.
- **FR-PQH-04** — Show item/manufacturer price trends over time to inform quotation decisions.
- **FR-PQH-05** — Report win/loss trends by customer, supplier, product category and sales engineer.

### Module 13 — Dashboard and Reporting (FR-DSH-01..07)

*Purpose: give each role timely visibility and traceable operational reporting.*

- **FR-DSH-01** — Landing dashboard summarizing open RFQs by status, pending decisions, quotations awaiting response, orders being fulfilled, shipments in transit and overdue follow-ups.
- **FR-DSH-02** — KPI widgets for RFQ response time, win/loss ratio, revenue from wins, on-time delivery and supplier performance.
- **FR-DSH-03** — Drill down from every KPI/chart to its source transaction.
- **FR-DSH-04** — Provide top-bar quick search/filter across screens for customer, supplier, product, date range and status.
- **FR-DSH-05** — Scope dashboards by role: assigned accounts for account teams, managed scope for supervisors/managers and company-wide KPIs for executives.
- **FR-DSH-06** — Schedule daily, weekly or monthly email delivery of PDF/Excel reports, bilingual where required.
- **FR-DSH-07** — Make dashboards responsive on phones and tablets.

## 5. Required logical records

RFQ header and RFQ line item · Supplier RFQ and quotation · Customer PO and Sales Order line ·
Supplier PO · Material lot · Shipment · Delivery note/POD · Follow-up task/activity ·
Stock balance and stock movements · Customer master · Item master, vendor master and price list ·
Purchase history and quotation history · Dashboard KPI snapshot/reporting model

Engineering may preserve an equivalent existing model if it proves every required behavior and
lineage. **A schema name alone is not evidence of completion.**

## 6. ERP, finance and ZATCA boundary

- Nexora may operate as the standalone system of record in Phase 1.
- In Phase 2, the ERP is intended to be the system of record for finance, inventory valuation and statutory reporting.
- Supplier POs, Sales Orders, deliveries and invoices are intended to be posted asynchronously through an integration layer with retry/error handling.
- Item, customer and vendor data are governed in Nexora in Phase 1 and synchronized with the selected ERP in Phase 2.
- ZATCA invoice generation/clearance operates independently of the selected ERP and then posts its reference to the ERP AR ledger.
- Phase 1 includes invoice generation and payment-due follow-up. **The BRD does not define a complete accounts-receivable, cash-receipt, allocation, reconciliation, refund, collections or general-ledger engine.**
- The exact authority split between Nexora, ZATCA and the future ERP requires written approval from Tech Connect Finance, Compliance and IT before implementation is certified.

## 7. Non-functional requirements

### Performance and scalability
- Average response time below three seconds for standard navigation.
- Horizontal scale for increased documents, users and data without architectural redesign.
- Standard multi-page OCR/NLP extraction target of 60 seconds under normal load.

### Security and privacy
- MFA through Azure AD or an equivalent identity provider for every login.
- Role/account-scoped access: account teams see assigned accounts; managers have approved broader scopes.
- TLS 1.2 or later in transit and AES-256 at rest.
- Saudi PDPL alignment, including approved data residency and defined retention/deletion policies.
- Full audit trail for master-data changes, LOA/pricing approvals and financial documents.

### Reliability and availability
- At least 99.5% availability during Saudi business hours, Sunday–Thursday, excluding communicated maintenance windows.
- Automated backup and disaster recovery with RPO ≤ 24 hours and RTO ≤ 8 hours.
- Informative bilingual error handling plus failure logging.
- Version control for RFQs, quotations and master-data changes.

### Localization and KSA/GCC compliance
- Full Arabic/English interface and genuine RTL layout.
- Hijri and Gregorian calendars for relevant government RFQ dates.
- Saudi 15% VAT plus configurable GCC VAT rates.
- ZATCA Fatoora Phase 2 generation, clearance/reporting and QR codes.
- SASO/SABER applicability and certificate-expiry tracking.
- Mandatory customer CR and VAT registration numbers.

## 8. Phase 1 acceptance criteria

**Design sign-off** — the implementation partner must demonstrate the solution and complete a POC; the resulting design must satisfy the BRD before first-stage acceptance.

**Cutover readiness** — migrate at least two years of purchase and quotation history; validate master data; train users; complete parallel-run verification.

**Pilot success** — for one in-scope pilot customer account, Go-Live is followed by a three-month incubation period. Success requires:
- 100% of the pilot account's RFQs captured in Nexora.
- Measured improvement in average RFQ response time against the agreed current baseline.
- No critical defect left open beyond its agreed SLA.
- At least one successfully generated ZATCA-compliant e-invoice through Nexora.

## 9. Decision Register — unresolved business decisions

These are business decisions, **not permission for engineering to guess**:

| # | Open decision |
|---|---|
| D1 | Document says *Draft for Review*; all approval names/signatures/dates are blank. |
| D2 | Tech Connect must supply the in-scope customer/BP list, vendor master and product/purchase-history data. |
| D3 | The future ERP platform and live integration contract are not approved. |
| D4 | The carrier/freight-forwarder subset for Phase 1 is not identified. |
| D5 | The ZATCA-versus-ERP statutory and accounting authority boundary requires approval. |
| D6 | Immutable RFQ source retention vs. retention/deletion policy: authorized disposal behavior and legal-hold rules are unspecified. |
| D7 | The payment-collection narrative is broader than the actual requirements, which define reminders but not a finance/AR engine. |
| D8 | WhatsApp is mentioned as a channel in narrative text, but the atomic RFQ ingestion requirement defines only email, manual and watched-folder ingestion. |
| D9 | The architecture section is explicitly a reference/recommendation. It does **not** require replacing an existing .NET modular application with Node/Java microservices, Kafka, Elasticsearch, Redis or a data warehouse. |
| D10 | Capacity, concurrent users, RFQ/page volume, maximum file size, OCR accuracy, Arabic accuracy, report SLA and defect severity/SLA definitions are not measurable in the BRD. |
| D11 | *Average RFQ response time improvement* requires an approved baseline and target. |
| D12 | TSDM is explicitly noted as requiring final definition with Tech Connect. |

## 10. Phase 1 product lock and development order

No feature outside this extract may interrupt the core sequence without written approval.
Existing extra functionality should be **preserved but frozen or hidden** when it distracts
from pilot delivery.

| Gate | Scope |
|---|---|
| Gate 0 | Repository-to-BRD evidence map and business decision register |
| Gate 1 | RFQ ingestion closure |
| Gate 2 | RFQ to approved customer quotation |
| Gate 3 | Customer PO matching and authoritative Sales Order |
| Gate 4 | Supplier PO drafting, approval and acknowledgement |
| Gate 5 | Shipment and material traceability |
| Gate 6 | Inventory, receipt, reservation, ATP and goods issue |
| Gate 7 | Delivery, POD and approved ZATCA/ERP invoice boundary |
| Gate 8 | Follow-ups, dashboards and scheduled reporting |
| Gate 9 | Arabic/RTL, security, performance, availability, backup and recovery certification |
| Gate 10 | History migration, training, parallel run and pilot acceptance |

**Each gate is complete only when its applicable UI, API/domain behavior, persistence,
authorization/tenant isolation, audit evidence, automated tests and real rendered-browser path
pass. CRUD scaffolding, filenames, mocked routes or unit tests alone are not completion.**

## 11. Instruction for the coding agent

Use this document as the Phase 1 requirements ceiling. First audit the repository against all 81
parent requirements and their independently testable sub-behaviors. Report what is verified,
partial, missing, conflicting and outside Phase 1. Make no implementation changes during the
audit. After the product owner accepts the audit and resolves the decision register, implement
only Gate 1. Do not combine later gates, redesign the architecture from the BRD reference stack,
or expand functionality outside the approved gate.

## 12. Traceability counts

| Requirement family | Count |
|---|---|
| FR-RFQ | 8 |
| FR-QTM | 8 |
| FR-COM | 7 |
| FR-SPO | 7 |
| FR-MTR | 5 |
| FR-MAS | 5 |
| FR-DLM | 7 |
| FR-SBF | 5 |
| FR-INV | 6 |
| FR-CST | 5 |
| FR-MDM | 6 |
| FR-PQH | 5 |
| FR-DSH | 7 |
| **Total** | **81** |
