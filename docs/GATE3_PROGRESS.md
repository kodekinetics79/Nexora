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
| FR-COM-07 · Sales Order as single source of truth | CONFLICTING | **PARTIAL** | Reader and writer now both depend on the case id; the index/FK schema delta is outstanding |

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

## The case id is now the read key

**Fixed.** `CommercialCaseQueryService.GetAsync` no longer rebuilds the chain by walking foreign
keys. Every document in the timeline is there because it **declares** the case — its
`CommercialCaseId`, or its Nexora Serial for the four procurement documents that do not carry the
surrogate key yet. Search does the same, so the counts on a result card are the population the
timeline shows.

The foreign-key walk survives, labelled as **reconciliation evidence**. It never adds a document
and never substitutes for a missing key; it exists so the two views can be compared and the
difference reported in a new `traceabilityGaps` collection on the case detail:

| Gap | Meaning | In the timeline? |
|---|---|---|
| `UnlinkedDocument` | The chain reaches it; it states no case at all | No — and it is named, not silently dropped |
| `ConflictingCase` | The chain reaches it; it states a *different* case | No — the wrong id is the defect, not the exclusion |
| `ChainBroken` | It states this case; the chain no longer reaches it | Yes, flagged `ChainBroken` |

A `STOCK` supplier PO is excluded from the unlinked report on purpose: replenishment has no
customer, so an absent case there is correct, and reporting it would train reviewers to ignore the
panel. The workspace shows the gaps on the Overview and in a dedicated Traceability tab.

`CommercialCaseReadKeyTests` is the proof the column is load-bearing: a document with the wrong
case id is absent and reported; a document with the right case id and no joinable chain at all is
present; a joinable document with a null case is reported rather than hidden. Each of those fails
against the old reader.

**Residual risk.** `Shipments."CommercialCaseId"` and `supplier_purchase_orders."CommercialCaseId"`
have no index and no foreign key to `CommercialCases`, so the reader now filters them on an
unindexed column and nothing at the database level stops a wrong value being written. Both are
schema deltas for the integration owner — see the handover note below.

**A correct win ratio is still not computable**, which makes FR-COM-03 and FR-CST-05 unmeetable
as written:

- The denominator counts *quotes*, not RFQs, so every inquiry that never became a quote is missing.
- Undecided quotes are excluded, so the ratio drifts upward until outcomes land.
- Automatic expiry stamps an outcome and therefore counts as a **loss**, conflating "the buyer
  chose someone else" with "we never heard back".
- Supplier and product-category dimensions do not exist on that path, and sales-engineer win rate
  uses a different denominator entirely.
- No win/loss query references the commercial case, so nothing deduplicates to one decision per
  inquiry.

## An order can no longer exist outside the spine

**Fixed.** `Order.CommercialCaseId`, `NexoraSerial` and `ContactId` now have private setters and
three `InheritCommercialIdentity` overloads (lead, RFQ, quote) — the same shape `Rfq` and `Quote`
already used. `CreateManualOrderAsync` resolves the originating document inside the caller's
tenant first and refuses the request outright when none is named or the named one carries no case.
Both service paths and the customer-award conversion inherit instead of copying fields.

There is deliberately **no** "allocate a case for a walk-in" branch. A commercial case is the
one-to-one principal of a `Lead` (`UX_Leads_CommercialCaseID`), so minting one for a counter sale
would have to manufacture a phantom inquiry — the same reasoning that kept customer POs out of the
unified ingestion door — and BRD v3.0 §2 starts the Phase 1 spine at an inquiry with no
counter-sale requirement to serve.

## Handover to the integration owner — schema delta

No migration was authored (migration authorship is reserved). Two deltas are outstanding; the exact
DDL and the backfill proof are in the lane report.

1. `IX_Shipments_BU_CommercialCase` and `IX_supplier_purchase_orders_BU_CommercialCase`, plus
   composite foreign keys to `CommercialCases (BusinessUnitID, ID)` mirroring `Orders`.
2. A `NOT NULL` tightening on `Orders."CommercialCaseID"` once the backfill proves zero nulls.

Following the convention already recorded in `ErpRfqAutomationContext.Procurement.cs`, these are
**not** declared in the EF model ahead of the migration: model-only drift fails the entire
PostgreSQL lane.

## The read fallbacks are gone, and a missing case is stated

**Fixed.** `QuoteRepository.MapToDTO` and both `RfqRepository` projections used to substitute the
parent's case when the document carried none — `q.CommercialCaseId ?? q.Rfq?.CommercialCaseId ??
q.Rfq?.Lead?.CommercialCaseId`. That is the same silent foreign-key substitution removed from the
timeline reader, still running on the list and detail screens, so a document the case workspace
reports as an unlinked gap displayed a perfectly good Nexora Serial everywhere else. The chains are
removed; each document reports its own case or none.

The UI states the null rather than leaving a blank: the RFQ and quote grids read **Not linked** in
warning colour, both detail headers carry a **Not linked to a case** chip instead of hiding the
serial chip, the quote's Lineage field says so in words, "Create Quote" distinguishes *no RFQ
selected* from *the selected RFQ has no case*, and RFQ creation no longer claims a case on the
strength of a lead id alone.

`CommercialCaseReadFallbackTests` seeds the fallback's ingredients deliberately — parent has a
case, child does not — so restoring any `??` chain fails all four.

## Quotation upload is inside the spine

**Fixed.** The quotation template gains a mandatory **Customer RFQ No** column (14). The uploader
resolves that RFQ inside the caller's tenant and refuses when it is absent, unknown, ambiguous,
owned by a different customer, or itself case-less; one quote number naming two RFQs is refused
too. The quotation then inherits through `Quote.InheritCommercialIdentity`. Refuse, not allocate —
same reasoning as the sales order.

**Known consequence:** an upload from a template downloaded before column 14 existed is refused
with a message telling the operator to download the current template. That is deliberate; the
alternative is importing priced quotations outside the spine.

## Other live findings, recorded not fixed

- **Manual "Create RFQ" allocates a second case for an inquiry already ingested**, and there is no
  merge path — a case is immutable in code and in the database. Once two exist for one inquiry,
  downstream documents stay permanently split.
- **One supplier PO cannot serve two customer demands** — consolidated buying across cases, the
  normal way a trader gets a price, is structurally impossible.
