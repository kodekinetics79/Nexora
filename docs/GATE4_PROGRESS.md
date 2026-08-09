# Gate 4 — Supplier PO drafting, approval, dispatch and acknowledgement

**FR-SPO-01 … FR-SPO-07.** Same bar as every gate: persistence, domain behaviour, UI wiring,
tenant isolation, audit evidence, automated tests and a real rendered-browser path. Anything short
is reported short, with the missing layer named.

| Req | Entering | Now | Remaining |
|---|---|---|---|
| FR-SPO-01 · auto-draft from awards, approval before release | MISSING | **PARTIAL** | Approval gate closed with segregation of duties; the draft is still raised by a human pressing a button |
| FR-SPO-02 · one-supplier and split-supplier POs | PARTIAL | **PARTIAL** | Structurally supported but not proven — see below |
| FR-SPO-03 · acknowledgement accept/reject/counter | MISSING | **CLOSED** | — |
| FR-SPO-04 · full status ladder | PARTIAL | **PARTIAL** | Four of the eight BRD statuses are unreachable — see below |
| FR-SPO-05 · link to customer PO and Sales Order | PARTIAL | **PARTIAL** | Keys are populated in-transaction; the case-timeline reader is being moved onto them |
| FR-SPO-06 · Incoterm, ports, HS code, country of origin | MISSING | **CLOSED** | — |
| FR-SPO-07 · ship-date reminders and acknowledgement escalation | MISSING | **CLOSED** | — |

## What closed

**FR-SPO-03 was the requirement this gate is named for, and it had no write path at all.**

The column, the `CHECK` constraint restricting it to `ACCEPTED`/`REJECTED`/`COUNTERED`, the revised
lead time, the acknowledgement note and an entire SLA escalation sweep that reads
`AcknowledgementStatus` had all shipped. Nothing in the codebase could set it. Every issued order
was therefore permanently unacknowledged, and the escalation sweep was chasing a state the system
had no way to leave.

This is the second time in this build that schema plus a comment has been mistaken for a feature.
It is worth naming the smell: **a column with no writer and a reader that depends on it is worse
than no column**, because the reader now behaves confidently and wrongly.

`AcknowledgePurchaseOrderAsync` now records what the supplier actually said. The semantics are
deliberate and are pinned by tests rather than left to a reader's assumption:

- Only **ACCEPTED** advances `Status` to `ACKNOWLEDGED`. A counter is the supplier asking for
  different terms and a rejection is a refusal — neither is agreement, and neither is allowed to
  display as an acknowledged order.
- All three stamp `AcknowledgementStatus` and `AcknowledgedOn`, which is what stops the escalation
  sweep. The buyer still owes a decision on a counter or a rejection, and that decision is a
  separate governed action.
- A counter that names neither a revised lead time nor a committed ship date is refused. It would
  otherwise be an empty acknowledgement whose only effect is to silence the alarm.
- A rejection without a reason is refused, for the same reason.

The supplier contact who answered is recorded separately from the internal user who keyed it in.
Nexora has no supplier portal, so these are always two different people, and collapsing them would
attribute the supplier's commitment to our own staff.

**FR-SPO-07 shipped with three defects, all found and fixed in this gate.**

1. **The escalation clock started at internal approval, not at dispatch.** An order approved on
   Monday and sent to the supplier on Thursday charged the supplier three days it never had. The
   sweep now starts from `SentToSupplierOn`, falling back only when it is absent.
2. **A zero policy value meant "escalate everything immediately"** rather than "not configured".
   `TimeSpan.FromHours(0)` makes every dispatched order overdue the instant it is sent. Both
   supplier sweeps now treat a non-positive value as disabled.
3. **The migration backfilled existing tenants with `0`, not with the intended defaults.**
   `SlaPolicy` declares 48 working hours and 3 working days in code, but every pre-existing row
   would have been created with zero — so the first sweep after deployment would have escalated
   the entire order book into managers' inboxes. That is how an alerting channel gets muted
   permanently. The migration now backfills 48 and 3.

Defect 3 is the dangerous one: it is invisible in every test, because tests build their policy rows
from the C# defaults and never see the backfilled value.

## The write path was only half the requirement

A review pass on the finished acknowledgement work found that `SupplierPurchaseOrderView` — the
workbench read model — projected `ApprovedBy` and `ApprovedOn` and **nothing about the
acknowledgement at all**. The practical consequence was worse than a missing field: a buyer records
a counter, the status deliberately stays `ISSUED`, so nothing on screen changes, the button is
still there, and the only feedback they ever get is a 409 the next time they press it. The revised
lead time and the rejection reason — the entire reason for capturing an answer — were invisible
everywhere in the product.

The view now carries the acknowledgement, the revised terms, the note and the trade terms, and the
lines carry HS code and country of origin. **This is the same lesson as the missing writer, from
the other end: a writer with no reader is as useless as a column with no writer**, and both pass
every test that only exercises one side.

Two validation rules were tightened at the same time. A revised lead time is now refused unless the
answer is COUNTERED — a changed lead time *is* the counter, and accepting one under the ACCEPTED
label records agreement to terms nobody agreed to. A committed ship date is refused on a rejection,
which has no schedule.

## A regression caught before it shipped

Adding `ACKNOWLEDGED` broke goods receipt. `PostGoodsReceiptAsync` admitted only `ISSUED` or
`PARTIALLY_RECEIVED`, so a supplier **accepting** an order would have become the act that made it
impossible to receive the goods. Cancellation had the same gap.

Both now read shared sets — `SupplierPurchaseOrderStatuses.OpenForReceipt` and `.Cancellable` —
declared next to the status constants themselves, so the next status added to the ladder is a
compile-time-visible decision rather than a silently missed `or` clause in two separate methods.

## FR-SPO-06 was the same defect as FR-SPO-03, found in the same sweep

`Incoterm`, `PortOfLoading` and `PortOfDischarge` on the header and `HsCode`, `CountryOfOrigin` on
the lines existed in the schema, the model and the migration, and **no code path anywhere wrote any
of them**. Five nullable columns that would have been null forever, on the requirement that exists
specifically to support international suppliers.

They now have three writers:

- **At creation.** The create command carries the Incoterm and both ports.
- **From master data.** A line's country of origin is seeded from `Product.CountryOfOrigin`, so the
  customs fields are populated by default rather than left for someone to discover at the border.
  Product carries no HS code today, so that stays a per-order entry rather than being invented on
  the catalogue.
- **By amendment before dispatch.** `AmendPurchaseOrderTradeTermsAsync` corrects the header terms
  and per-line customs data while the order is DRAFT or APPROVED.

Two judgements worth recording. **Amendment stops at dispatch**: once the PDF is with the supplier,
the Incoterm they hold and the one we hold must not silently diverge, so changing terms after that
is a re-issue rather than an edit. And **an omitted field means "leave unchanged", not "clear"** —
the common amendment is a one-line HS-code correction, and treating omission as deletion would let
that narrow edit wipe the Incoterm the whole order depends on.

The Incoterm is validated against the closed Incoterms 2020 set rather than accepted as free text.
An Incoterm decides who pays freight, where risk passes and who clears customs at each end; a typo
is not a cosmetic defect, it is a changed commercial position that nobody notices until a container
is sitting at a port with no party obliged to clear it.

**Accepted limitation:** `HsCode` and `CountryOfOrigin` are `text` in PostgreSQL while the rest of
the table is length-bounded, and the bound (20 and 100 characters) is enforced in the application
rather than by the column. The migration was left alone deliberately — regenerating it while other
work was mid-flight would have swept unrelated half-finished model changes into it. The next
migration should tighten both columns.

## FR-SPO-04 — four of the eight BRD statuses are unreachable

The BRD names Draft, Approved, Sent, Acknowledged, In Production, Shipped, Received and Closed.

Reachable today: `DRAFT` on creation, `APPROVED` on approval, `ACKNOWLEDGED` on supplier
acceptance, `ISSUED` on dispatch, `PARTIALLY_RECEIVED` and `RECEIVED` on goods receipt, and
`CANCELLED`.

Never assigned by any code path: **`SENT`, `IN_PRODUCTION`, `SHIPPED`, `CLOSED`.**

`SENT` matters most. It is the status the BRD intends for dispatch, but dispatch writes the legacy
`ISSUED` — a value the constant's own documentation describes as having "conflated Approved and
Sent". So the ladder has a documented legacy rung still carrying live traffic while the correct
rung is unreachable. `IN_PRODUCTION` and `SHIPPED` belong to supplier progress tracking, and
`CLOSED` to settlement; both are downstream gate work, but they should not be presented as
implemented.

## FR-SPO-02 is structurally supported but unproven

A purchase order is created against exactly one supplier, and nothing stops an RFQ raising several,
so splitting one RFQ across suppliers works by construction. But the only split test in the suite,
`Award_enforces_moq_and_supports_bounded_split_sourcing`, splits **awards on a single supplier's
quote** — it never involves a second supplier. So the requirement is plausible rather than
demonstrated, and it is recorded as PARTIAL on that basis alone. A test that awards two lines of one
RFQ to two different suppliers and asserts two independent purchase orders would close it.

## What is verified, and what is not

Closed here means persistence, domain behaviour, UI wiring and automated tests. FR-SPO-03 carries 28
tests and FR-SPO-06 carries 24, both including replay, stale-version, cross-tenant and audit-payload
cases. Two named layers are still missing, and neither should be read as covered:

- **No HTTP-layer test on either new route.** `POST …/acknowledge` and `POST …/trade-terms` are
  exercised through the application service only. The `Orders:Edit` attribute, the required
  `Idempotency-Key` and `X-Correlation-ID` headers, and the ProblemDetails shape the frontend error
  handling now depends on are all unexercised. This is the largest missing layer in the gate.
- **No frontend test.** Two dialogs, the acknowledgement panel and the amendment's dirty-field
  diffing are verified by typecheck and lint alone.

A third limit is structural rather than missing work: the PostgreSQL `CHECK` constraints are
certified only by the PostgreSQL lane, because the portable lane runs SQLite with
`PRAGMA ignore_check_constraints = ON`. A constraint defect passes green there and fails in
production — which is exactly how the five-value status constraint nearly shipped.

## Open findings carried forward, not fixed here

- **One supplier PO cannot serve two customer demands.** Consolidated buying across cases — the
  normal way a trader gets a better price — is structurally impossible. This is a commercial
  capability gap, not a bug, and it needs a product decision before it is built.
- **Auto-draft (FR-SPO-01, and FR-COM-06 behind it) is still a manual button.** The award data
  needed to raise the draft automatically is all present; nothing consumes it on a trigger.
