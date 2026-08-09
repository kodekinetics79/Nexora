# Nexora — BRD v3.0 Requirement Traceability Result

**Audit date:** 2026-08-08 · **Branch:** `wip/phase1-base-journey-20260806` · **Method:** seven parallel SME domain audits against [NEXORA_BRD_V3_CONTROLLED_EXTRACT.md](NEXORA_BRD_V3_CONTROLLED_EXTRACT.md), plus first-hand verification of the spine, live environment probes and a clean build of both applications. No implementation changes were made.

**Evidence standard applied:** a requirement is VERIFIED only with persistence + domain behavior + UI wiring + test coverage. A schema name, a screen, or a green unit test alone is not completion.

## Headline

| Verdict | Count | Share |
|---|---:|---:|
| **VERIFIED** — provably complete | **4** | 5% |
| **PARTIAL** — real work exists, a named layer is missing | **36** | 44% |
| **CONFLICTING** — code contradicts the BRD | **6** | 7% |
| **MISSING** — no real implementation | **35** | 43% |
| **Total** | **81** | 100% |

The four VERIFIED requirements are FR-RFQ-02 (intake formats), FR-QTM-08 (quote revisions), FR-SPO-02 (split-supplier POs) and FR-PQH-02 (quotation history with win/loss and loss reason).

## Result by module

| Module | Verified | Partial | Conflicting | Missing | Gate |
|---|---:|---:|---:|---:|---|
| 1 · RFQ Ingestion (FR-RFQ, 8) | 1 | 5 | 2 | 0 | 1 |
| 2 · Quote Management (FR-QTM, 8) | 1 | 4 | 1 | 2 | 2 |
| 3 · Customer Order Matching (FR-COM, 7) | 0 | 3 | 1 | 3 | 3 |
| 4 · Supplier PO (FR-SPO, 7) | 1 | 1 | 1 | 4 | 4 |
| 5 · Material Traceability (FR-MTR, 5) | 0 | 2 | 0 | 3 | 5 |
| 6 · Material Availability (FR-MAS, 5) | 0 | 1 | 0 | 4 | 5 |
| 7 · Delivery Management (FR-DLM, 7) | 0 | 2 | 0 | 5 | 7 |
| 8 · Follow-Up (FR-SBF, 5) | 0 | 4 | 0 | 1 | 8 |
| 9 · Inventory (FR-INV, 6) | 0 | 4 | 1 | 1 | 6 |
| 10 · Customer (FR-CST, 5) | 0 | 2 | 0 | 3 | 3 |
| 11 · Master Data (FR-MDM, 6) | 0 | 3 | 0 | 3 | 1–3 |
| 12 · History (FR-PQH, 5) | 1 | 2 | 0 | 2 | 10 |
| 13 · Dashboards (FR-DSH, 7) | 0 | 3 | 0 | 4 | 8 |

**The shape of the result matters more than the total.** Completion decays monotonically along the transaction spine. The front (RFQ intake, quoting) is substantially built. The middle (customer PO, supplier PO) is half-built with a broken join. The back (traceability, delivery, POD, ZATCA) is close to absent.

## The spine, as it exists today

```
Rfq ──────────────────────────────────────────────── INTACT
 └→ Quote                  Quote.Rfqid (nullable)     PARTIAL
     └→ QuoteItem          .RfqitemId                 INTACT
CustomerPurchaseOrder      .CommercialCaseId          PARTIAL  (no QuoteId, no RfqId,
                                                                no source-document link)
 └→ CustomerAward          .CustomerPurchaseOrderId   INTACT   ← the real PO↔Quote bridge
                           .QuoteId                   INTACT
Order (= Sales Order)      .CustomerAwardId nullable  PARTIAL  (MANUAL source may have
                                                                all lineage keys null)
──────────────────────── THE BREAK ────────────────────────
Order → SupplierPurchaseOrder                         ★ BROKEN ★
   SupplierPurchaseOrder carries RfqId only — no OrderId,
   no CustomerPurchaseOrderId, no CustomerAwardId.
   A second, unjoined model (ProcurementHandoff) does reach the
   Sales Order but stores the supplier PO number as a bare string.
───────────────────────────────────────────────────────────
SupplierPurchaseOrder → GoodsReceipt                  INTACT
 └→ GoodsReceiptLine                                  INTACT  (quantity only — no lot,
                                                                no condition)
GoodsReceipt → Material Lot                           ✖ ENTITY DOES NOT EXIST
Lot → Delivery Note / POD                             ✖ ENTITY DOES NOT EXIST
Delivery → ZATCA Invoice                              ✖ NOT IMPLEMENTED
```

The chain reconnects downstream only by re-joining through `RfqId`. **In the implementation the RFQ, not the Sales Order, is the de-facto spine — the inverse of FR-COM-07.**

## Requirements where the code contradicts the BRD

These are more urgent than the missing ones, because work is accumulating on top of an unratified assumption.

| Req | What the BRD says | What the code does |
|---|---|---|
| FR-RFQ-03 | IDs are `RFQ-KSA-{YYYY}-{sequence}` | IDs are `NXR-{YYYY}-{NNNNNN}`; zero occurrences of `RFQ-KSA` |
| FR-RFQ-06 | Flag duplicates **before** a record is created | Two engines run; one creates the record then flags it |
| FR-QTM-07 | Expire on validity date, or 90 days with no response | Expires 14 days *after* validity; no 90-day rule |
| FR-COM-04 | Configurable tolerance, e.g. ±2% | Exact decimal equality — and the PO screen pre-fills from the quote, so the check compares the system to itself |
| FR-SPO-05 | Link every supplier PO to its customer PO and Sales Order | Neither FK exists |
| FR-INV-02 | ATP = on-hand + incoming − reservations | ATP excludes incoming (defensible, but undocumented as a deviation) |

## The four hard stops

1. **ZATCA is 0% built.** `fatoora`, `xades`, `csid`, `InvoiceHash` return zero matches. The only user-visible invoice hardcodes a UK tax ID, 10% VAT and `$` currency. Pilot acceptance requires at least one compliant e-invoice; ZATCA onboarding has external lead time that cannot be compressed by adding engineers.
2. **Material lot does not exist.** `Inventory` carries `BatchTracking`/`SerialTracking` booleans with no entity behind them. FR-MTR-01/02/03, FR-MAS-05, FR-INV-01 and FR-DLM-01 all depend on it.
3. **Arabic and RTL are absent by deliberate lock.** ~6% of the UI string surface is externalized (157 keys against ~2,290 hardcoded strings), no RTL infrastructure exists, only `eng.traineddata` ships, and Hijri appears nowhere. Realistic remediation is 6–16 weeks, not a config flag.
4. **PDPL data residency is unmet and unmeetable on the current stack.** Hosting defaults to US-Oregon; none of the three providers offers a Saudi region.

## What is genuinely strong

This is not a weak codebase, and the report should not be read that way.

- **Tenant isolation is excellent** — 104 RLS policies, 109 tables with row-level security enabled, ~200 EF query filters, `SET LOCAL ROLE` per transaction, and ~53 direct cross-tenant negative tests.
- **Auth-by-default** — a fallback authorization policy means unauthenticated probes return 401, not 404; no anonymous endpoint enumeration.
- **The stock ledger is well built** — append-only movements, PostgreSQL advisory locks with deterministic ordering, optimistic concurrency on reservations, idempotency keys, and a drift-reconciliation endpoint with a test proving it fires.
- **Async work is disciplined** — persisted queues, leases, dead-lettering, idempotent workers, scale-out tests.
- **Both applications build clean**, and the team documents its own risks honestly in configuration comments rather than hiding them.

The problem is **allocation, not craft**. Roughly 63k LOC of hand-written subsystems — a SaaS control plane, subscription billing, general ledger, bank reconciliation, treasury, AR/collections, AI copilot and several intelligence engines — carry no BRD v3.0 requirement. That is about 64% of hand-written backend subsystem code sitting outside the Phase 1 ceiling, and the finance engine alone is substantially larger than the shipment/delivery/traceability spine the BRD actually commits to.

## Test and browser evidence

The BRD requires a real rendered-browser path for gate completion. Current end-to-end coverage mirrors the code exactly: 22 specs touch RFQ and 17 touch quoting, but **zero specs cover shipment and only one touches invoicing.** Gates 5 and 7 are unprovable today regardless of what is built next.

## Corrections to earlier findings

- The **`.xls` unreadable** defect is **fixed** — `ExcelDataReader` now backs the parser, with an HTML-masquerading-as-`.xls` fallback.
- The **per-tenant Setup Master starvation** is **substantially fixed on the platform provisioning path**, which now seeds lifecycle statuses, roles, modules, permissions, currency and UoM idempotently. Two gaps remain: business units created via `POST /api/BusinessUnit` are still born without roles or permissions (the likely origin of the original finding), and there is no repair path for already-broken tenants.
- The suspected **stock corruption** was not reproducible in the ledger itself. The real vector is narrower and still open: a legacy `Product.QtyOnHand` column that some paths still write, bypassing both the advisory lock and the movement ledger, and which the reconciliation endpoint does not check.

## Newly discovered, not previously reported

- **Account-team scoping (FR-CST-02) does not exist at any layer.** A Sales Engineer authenticated to a business unit can read any customer in that unit, including other teams' accounts, and receives a 200. There is no team predicate anywhere and no test asserts denial, because there is nothing to assert.
- **Master-data changes are not audited.** The audit infrastructure supports before/after values, but the Customer, Supplier, Product, Contact and Category controllers write no audit events at all.
- **Purchase history has no writer.** The table is orphaned, its controller returns `410 Gone` for every mutation, and it lacks BP number and manufacturer columns — yet Gate 10 requires migrating two years of history into it.
- **Real malware scanning is disabled.** ClamAV is not deployed; the active inspector detects no actual malware and logs a reduced-security-posture warning on every boot, in a product whose primary input is customer documents.
- **The document estate has no backup.** The disk holding every source document has no backup configured; the configuration file itself notes that losing it is unrecoverable.
- **The global "Search anything…" box is cosmetic** — it matches typed text against a hardcoded keyword-to-route list and otherwise navigates to the dashboard. There is no search backend.
- **`Product` has no manufacturer column.** Manufacturer lookups resolve against the preferred *supplier's* name. Supplier is not manufacturer; this is a data-model conflict that FR-MDM-01, FR-COM-02 and FR-PQH-03 all inherit.

## Disposition

**Phase 1 pilot: NO-GO today**, and the gap is not closable by polish. See [NEXORA_PHASE1_DECISION_REGISTER.md](NEXORA_PHASE1_DECISION_REGISTER.md) for the 46 open decisions that must be answered before the corresponding code is written, and the gate plan for sequencing.
