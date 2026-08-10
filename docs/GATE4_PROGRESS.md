# Gate 4 — Supplier PO drafting, approval, dispatch and acknowledgement

**FR-SPO-01 … FR-SPO-07.** Same bar as every gate: persistence, domain behaviour, UI wiring,
tenant isolation, audit evidence, automated tests and a real rendered-browser path. Anything short
is reported short, with the missing layer named.

| Req | Entering | Now | Remaining |
|---|---|---|---|
| FR-SPO-01 · auto-draft from awards, approval before release | MISSING | **PARTIAL** | Approval gate closed with segregation of duties; the draft is still raised by a human pressing a button |
| FR-SPO-02 · one-supplier and split-supplier POs | PARTIAL | **PARTIAL** | Structurally supported but not proven — see below |
| FR-SPO-03 · acknowledgement accept/reject/counter | MISSING | **CLOSED** | — |
| FR-SPO-04 · full status ladder | PARTIAL | **PARTIAL** | `SENT` is now written at dispatch; `IN_PRODUCTION`, `SHIPPED` and `CLOSED` remain unreachable and are owed by later gates — see below |
| FR-SPO-05 · link to customer PO and Sales Order | PARTIAL | **PARTIAL** | Keys are now written at construction from the governed award→customer-quote bridge; the sales-order key is null until the award is converted, and that gap is reported rather than filled — see below |
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

## FR-SPO-04 — `SENT` is now written; three statuses remain owed by later gates

The BRD names Draft, Approved, Sent, Acknowledged, In Production, Shipped, Received and Closed.

Reachable today: `DRAFT` on creation, `APPROVED` on approval, **`SENT` on dispatch**,
`ACKNOWLEDGED` on supplier acceptance, `PARTIALLY_RECEIVED` and `RECEIVED` on goods receipt, and
`CANCELLED`.

**`SENT` and `ISSUED` coexist deliberately.** `IssuePurchaseOrderAsync` now writes `SENT`, so the
split of the conflated `ISSUED` into `APPROVED` + `SENT` exists in data and not only in the constant
list. `ISSUED` is not retired: rows raised before the split carry it, and rewriting history to fit a
new vocabulary is worse than keeping the old word and mapping it. The equivalence is stated once, in
`SupplierPurchaseOrderStatuses.WithSupplier`, and every guard admits both:

| Guard | Both admitted? |
|---|---|
| `OpenForReceipt` (goods receipt) | Yes — `SENT` added with the writer, in the same change |
| `Cancellable` (buyer withdrawal) | Yes — already |
| Acknowledgement precondition | Yes — already |
| Committed-supply predicate behind `GetNetSourcingRequirementAsync` | Yes — `SENT` added with the writer |
| `InboundShipmentApplicationService.ShippableOrderStatuses` | Yes — already |
| `SlaSweepWorker.WithSupplierStatuses` (acknowledgement escalation) | Yes — already |
| `SlaSweepWorker.ShipmentSettledStatuses` | Correctly excludes both — a dispatched order is still worth chasing |
| Frontend receipt-button gate (`SourcingWorkbenchPage`) | Yes — `SENT` added; it mirrored `OpenForReceipt` and had the same hole |

The `OpenForReceipt` hole was the trap: it did not contain `SENT`, so adding the writer alone would
have broken goods receipt for every dispatched-but-unacknowledged order — the same regression
`ACKNOWLEDGED` caused once already. `Gate4SupplierPurchaseOrderDispatchTests` fails if either half
is reverted.

Still never assigned by any code path: **`IN_PRODUCTION`, `SHIPPED`, `CLOSED`.** These are owed,
not forgotten, and no writer was invented for them here:

- **`IN_PRODUCTION` and `SHIPPED` are owed by Gate 5** (shipment and material traceability).
  `InboundShipmentApplicationService` already records the milestones they correspond to —
  `READY_AT_FACTORY` and `DEPARTED_ORIGIN` — and already reads the purchase order's status, but it
  never writes it back. The projection from milestone to order status is the missing link.
- **`CLOSED` — "received and settled" — is owed by the invoice/settlement boundary, Gate 7.** It
  cannot be written by anyone today: there is no supplier-invoice or three-way-match entity in the
  codebase, so nothing knows when an order is settled.

Until those writers exist, the four guard sets that already list `IN_PRODUCTION`, `SHIPPED` and
`CLOSED` are reading states no row can hold.

## Committed supply — the screen and the command path now ask one question

Two defects in the same concept, pointing in opposite directions, both found in this sweep.

**A supplier acknowledging an order made Nexora buy it twice.**
`GetNetSourcingRequirementAsync` filtered committed supply with a hand-written
`ISSUED or PARTIALLY_RECEIVED` list. `OpenForReceipt` was correctly widened when `ACKNOWLEDGED`
arrived; this list was not. So: order for 100 issued, shortfall 0; supplier accepts, status becomes
`ACKNOWLEDGED`; the order matches neither branch and 100 units vanish from `incomingByItem`; the
shortfall reads 100 again, the "fully covered" refusal does not fire, the sourcing-case dedupe key
differs because the quantity changed, and a second sourcing case, a second award and a second
purchase order are raised for material already on order. One user, no concurrency, ordinary happy
path. `PrepareSupplierRfqAsync` and `QueuePreparedSupplierRfqAsync` read the same function, so
supplier outreach was re-prepared too.

**The workbench counted DRAFT and CANCELLED purchase orders as incoming.**
`GetWorkbenchAsync` loaded every purchase order on the RFQ with no status predicate and summed
`OrderedQuantity - ReceivedQuantity` across all of them. `CancelPurchaseOrderAsync` reverts the
awards precisely so the line goes back to sourcing — but the screen still showed shortfall 0, so
nobody re-sourced it. The screen over-counted while the command path under-counted, and a buyer
could see "covered" on a line the API said still needed buying.

Both now read one shared set, `SupplierPurchaseOrderStatuses.CommittedSupply`, declared beside
`OpenForReceipt` and `Cancellable`:

`SENT`, `ISSUED`, `ACKNOWLEDGED`, `IN_PRODUCTION`, `SHIPPED`, `PARTIALLY_RECEIVED`.

`DRAFT` and `APPROVED` are out — an order the supplier has never seen is an intention, not supply,
and counting a draft is what left one with lapsed quotes suppressing its RFQ line forever.
`RECEIVED`, `CLOSED` and `CANCELLED` are out — nothing is still expected to arrive against them.

**`IN_PRODUCTION` and `SHIPPED` are in the set deliberately, ahead of their writers.** Nothing
assigns them today. They are the strongest commitments on the ladder, and if they were left out
the day Gate 5 starts writing them, an order in production would silently stop covering its demand
and the double-buy would return one status further along — which is exactly how `ACKNOWLEDGED`
caused it the first time. Adding a state to the ladder without adding it here is the defect.

The set is the single source; `ProcurementApplicationService` derives its SQL `IN` list from it
(`CommittedSupplyStatuses`) rather than restating it, because EF cannot translate
`IReadOnlySet.Contains` and a second hand-written list is precisely what went wrong.

### Every hand-written supplier-PO status comparison in the backend

| Site | Kind | Disposition |
|---|---|---|
| `ProcurementApplicationService` create `:1349` | writes `DRAFT` | unchanged |
| approve `:1490`, `:1492`, writes `:1519` | `== APPROVED`, `!= DRAFT` | unchanged — only a draft is approvable |
| trade-terms amend `:1639` | `is not (DRAFT or APPROVED)` | unchanged — terms stop at dispatch, by design |
| acknowledge `:1777`, writes `:1793` | `is not (SENT or ISSUED)` | unchanged — already admits both spellings |
| release `:1858`, `:1861`, writes `:1884` | `== DRAFT`, `!= APPROVED` | writer changed `ISSUED` → `SENT`; guards unchanged |
| cancel `:1984`, `:1995`, writes `:2002` | `== CANCELLED`, `Cancellable` set | unchanged |
| goods receipt `:2084` | `OpenForReceipt` set | set gained `SENT`; the call site is another owner's region and was not touched |
| goods receipt writes `:2170` | `RECEIVED` / `PARTIALLY_RECEIVED` | unchanged, another owner's region |
| supplier-history evidence `:2236` | `!= CANCELLED` | unchanged — "have we bought from this supplier before?" is a history question, and a draft or cancelled order is still evidence of a relationship |
| net sourcing requirement `:2641` | hand-written list | **changed** — now `CommittedSupply` |
| workbench `:634` | no predicate at all | **changed** — now `CommittedSupply` |
| `SlaSweepWorker.ShipmentSettledStatuses` `:682` | array | unchanged — correctly excludes `SENT`/`ISSUED`; a dispatched order is still worth chasing |
| `SlaSweepWorker.WithSupplierStatuses` `:695` | array | unchanged — already both spellings. Left as a `string[]` because it is used inside an EF query |
| `InboundShipmentApplicationService.ShippableOrderStatuses` `:63` | set | unchanged — already both spellings and both unwritten states |
| Frontend `SourcingWorkbenchPage` receipt gate | inline list mirroring `OpenForReceipt` | **changed** — was missing `SENT` |
| Frontend `PurchaseOrdersPage` | `["SENT","ISSUED"]`, twice | unchanged — already correct |

## FR-SPO-05 / FR-COM-07 — the customer keys now have a writer

`CustomerPurchaseOrderId`, `CustomerOrderId` and `QuoteId` were declared on `SupplierPurchaseOrder`,
added to the schema, and read by `ResolveCustomerChainCaseAsync` — and **nothing ever wrote them**.
The single construction site set none of the three, so the reader was unreachable and the RFQ
remained the de-facto spine, the inverse of what FR-COM-07 requires. Deleting all three columns
would have broken nothing.

They are now resolved and written inside the same serializable transaction that inserts the order,
from the governed bridge only: `CustomerQuoteSourcingDecision` links the approved sourcing award to
one customer quote line; the customer award allocation names the client PO; the sales order names
the award. Nothing is derived from the RFQ — an RFQ can carry lines for several quotations, and
"the customer PO that shares this RFQ" is a guess dressed as a key.

**The `DemandSource` asymmetry is enforced, not described.** On a `STOCK` order any customer key is
refused outright by `SupplierPurchaseOrder.AttachCustomerOrigin`, so replenishment cannot acquire a
customer through a mis-wired caller. On a `CUSTOMER_DEMAND` order every key the chain can prove is
written, each key only when the chain resolves to exactly one document; ambiguity, an unbuilt link
and an order raised outside the customer flow all leave the key null **with a stated reason** —
recorded in the `SUPPLIER_PO_CREATED` payload and surfaced on the case timeline as a
`CustomerOriginMissing` traceability gap, following the same "report it, never hide it" rule as the
rest of `CommercialCaseQueryService`.

**Known and deliberate:** `CustomerOrderId` is frequently null at creation, because the sales order
is often raised *after* the supplier order. Back-stamping the supplier order when the customer award
is converted belongs to the Order-to-Cash write path and is not done here. Until it is, the gap is
visible rather than silent.

**Schema delta owed to the migration owner** (reported, not authored — no migration was created or
edited): the three columns have no foreign key and no index, unlike `CommercialCaseId` which has
both; `DemandSource` is unbounded `text` with a database default of `''` that contradicts the C#
default of `CUSTOMER_DEMAND` and has no `CHECK`; and the `STOCK` half of the asymmetry deserves a
`CHECK` in PostgreSQL. The SQLite lane runs `PRAGMA ignore_check_constraints = ON`, so any such
constraint is unenforced there and its certifying test belongs in the PostgreSQL lane. The
`Orders` table has no `(BusinessUnitId, Id)` alternate key, so a tenant-scoped FK on
`CustomerOrderId` needs that added first.

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
