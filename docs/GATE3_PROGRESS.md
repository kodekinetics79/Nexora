# Gate 3 — Customer PO matching and the authoritative Sales Order

**FR-COM-01 … FR-COM-07.** Same bar as every gate: persistence, domain behaviour, UI wiring,
tenant isolation, audit evidence, automated tests and a real rendered-browser path. Anything short
is reported short, with the missing layer named.

| Req | Entering | Now | Remaining |
|---|---|---|---|
| FR-COM-01 · PO upload and extraction | MISSING | **CLOSED** | — |
| FR-COM-02 · three-key line matching | MISSING | **CLOSED** | — |
| FR-COM-03 · award classification, win value and ratio | PARTIAL | **PARTIAL** | Per-line classification is a projection; win value and win ratio are not computable — see below |
| FR-COM-04 · configurable price tolerance | CONFLICTING | **PARTIAL** | Pre-fill defect fixed and the policy table exists; the comparison does not read it yet |
| FR-COM-05 · multiple POs, cumulative call-off | PARTIAL | **PARTIAL** | Cumulative tracking is quote-scoped, not RFQ-scoped; no blanket header |
| FR-COM-06 · auto-generate supplier demand | MISSING | **MISSING** | Still a manual button |
| FR-COM-07 · Sales Order as single source of truth | CONFLICTING | **PARTIAL** | Spine repaired; the case-timeline reader still ignores it — see below |

## What closed

**FR-COM-01.** There was no upload at all — a customer PO was hand-keyed JSON, and the OCR and
document parsing built for RFQ intake were never wired to the PO path. A PO is a tabular
commercial document exactly like an RFQ, so it reuses that stack rather than growing a second one.

It deliberately does **not** go through the unified ingestion door, and that judgement is worth
recording: that door's contract is one document to one job to one *Lead*, so a customer PO through
it would manufacture a phantom inbound RFQ for every award. The governance primitives — bounded
input, signature typing, malware scan, immutable content-addressed write — are reused without the
lead-producing side effect.

**FR-COM-02.** The pairing of PO line to quote line used to be an *input*: a human picked from a
dropdown. It is now proposed deterministically on the buyer's own keys, strongest tier first, and
ambiguity proposes nothing and lists the tied candidates. Nothing commits automatically — the BRD
is explicit that touchless matching is out of scope.

**The pre-fill defect, which was the worst thing in this gate.** The capture screen pre-filled the
PO line's price and quantity from *our own quote*, so the discrepancy engine compared the system
against itself and the default state was always "no discrepancy". A clean demo proved nothing, and
the part-mismatch check was structurally unreachable because the product had been copied across.
Buyer fields now start empty; the quoted figure is shown beside the input rather than inside it.

## The spine

`SupplierPurchaseOrder` carried only an RFQ reference, so every downstream trace re-joined through
the RFQ and the Sales Order was connected to procurement by nothing. It now states **why** it
exists — `STOCK` or `CUSTOMER_DEMAND` — which is what makes the customer keys meaningful rather
than merely nullable: absent keys on a stock order are correct, absent keys on a customer-demand
order are a defect a test can catch.

The commercial case reference now also reaches the two documents that never carried it,
`SupplierPurchaseOrder` and `Shipment`, populated inside the same transaction as the document's
creation and backfilled from the joinable chain. A supplier PO or delivery note can now state its
own master reference without a four-table join.

## Two findings from the review panel that are NOT yet fixed

**1. The case-timeline reader does not use the case id.** `CommercialCaseQueryService` reconstructs
the document chain by walking foreign keys — lead to RFQ to quote to order to shipment — and only
the customer PO filters on `CommercialCaseId`. So the column is decorative for traceability: a
record with the *wrong* case id still appears in the timeline, and a record with the right one but
a broken join is invisible.

**Until the reader depends on the column, adding it to more tables does not make the chain real.**
Making it the read key would turn every unpopulated column and every bypass-created record from a
silent defect into an immediately visible one.

**2. A correct win ratio is not computable today**, which makes FR-COM-03 and FR-CST-05 unmeetable
as written:

- The denominator counts *quotes*, not RFQs, so every inquiry that never became a quote is missing.
- Undecided quotes are excluded, so the ratio drifts upward until outcomes land.
- Automatic expiry stamps an outcome and therefore counts as a **loss**, conflating "the buyer
  chose someone else" with "we never heard back".
- Supplier and product-category dimensions do not exist on that path, and sales-engineer win rate
  uses a different denominator entirely.
- No win/loss query references the commercial case, so nothing deduplicates to one decision per
  inquiry.

## Other live findings, recorded not fixed

- **`POST /api/Order` mints a priced customer document with no commercial case.** `Rfq` and `Quote`
  guard identity with private setters and an inheritance invariant; `Order` has three public
  setters and no guard, and the manual-creation path sets none of them.
- **Manual "Create RFQ" allocates a second case for an inquiry already ingested**, and there is no
  merge path — a case is immutable in code and in the database. Once two exist for one inquiry,
  downstream documents stay permanently split.
- **One supplier PO cannot serve two customer demands** — consolidated buying across cases, the
  normal way a trader gets a price, is structurally impossible.
