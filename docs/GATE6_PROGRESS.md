# Gate 6 — Inventory, receipt, reservation, ATP and goods issue

**FR-INV-01 … FR-INV-06.** Most of this module already existed and several parts of it were well
built. The audit came first and is reported first, because the honest finding is that this gate is
mostly one missing idea rather than a missing module.

## What was already there

| Piece | State on entry |
|---|---|
| `InventoryQuantityMath.AvailableToPromise` | Complete, and the right abstraction — one definition, documented, clamped |
| `InventoryAvailabilityService` reservation locking | Complete. Advisory-lock identities derived from shared helpers, idempotency fenced inside the lock, split/consume/release all audited |
| `StockLedgerService` | Complete as the single sanctioned writer of the stock buckets, with movement-and-balance in one transaction and a reconciliation read |
| `OrderStockReservationService` | Complete for inventory-scoped allocation, including multi-warehouse spill and partial issue |
| Commercial inventory subsystem | Complete for snapshots, movements, incoming supply and line resolution |
| Lot/batch/serial (`MaterialLot`) | Complete from Gate 5, created only by goods receipt |

Nothing in `Inventory/` turned out to be a table with no writer or a control that reports success
while doing nothing. Two things elsewhere did, and both are fixed below.

## Requirement position

| Req | Entering | Now | Missing layer |
|---|---|---|---|
| FR-INV-01 · quantities by item/warehouse/**lot**, on-hand vs reserved vs in-transit | PARTIAL — no lot dimension on reservations | **CLOSED** | — |
| FR-INV-02 · ATP incl. incoming, exposed at quotation | PARTIAL — four hand-rolled copies | **CLOSED** | — |
| FR-INV-03 · receipts with lot and condition; **goods issues** against delivery notes | PARTIAL — issue named no lot, discarded its result | **CLOSED** | — |
| FR-INV-04 · min/max, reorder points, reorder **alerts** | PARTIAL | **CLOSED** | — (see "Minimum, maximum and the reorder alert" below) |
| FR-INV-05 · cycle counts with **variance reporting** | PARTIAL — count existed, variance was discarded | **SHORT** | API + UI complete; **no count sheet or counting session** — a stock take is still not a governed document |
| FR-INV-06 · stock ageing for slow-moving and obsolete | MISSING at every layer | **CLOSED** | — |

## Lot-level reservation — the headline

Gate 5 shut the system-side door and left the physical one open. Quarantining one of two lots
correctly removed its quantity from available-to-promise, so no *new* order could be promised it —
but a reservation named only an inventory row, and any unit on that row satisfied it. A picker
could still walk to the recalled rack.

`StockReservation.MaterialLotId` closes it. Four behaviours now depend on the hold naming what it
holds, and each is what breaks if the column stops being load-bearing:

1. **Allocation picks lot by lot**, first-expired-first-out within the chosen warehouse row. FEFO
   rather than FIFO because a trading business holds sealant and batteries next to steel; where
   nothing expires the ordering degenerates exactly to FIFO.
2. **Goods issue refuses a quarantined lot** (`QuarantinedLotIssueException`), in `ConsumeAsync` —
   the single point every issue path funnels through, so it is not a guard three callers have to
   remember.
3. **Quarantine releases exactly the affected orders.** The quantity-based sweep freed the newest
   holds on the row, which displaced whoever happened to be newest rather than whoever was holding
   the recalled material. Now every other customer's promise survives the recall.
4. **A recall can be scoped.** `GetLotCommitmentsAsync` answers "which orders hold or have consumed
   THIS lot", not "which orders hold this product" — which over-reaches on to customers whose
   material was sound and under-reaches the moment an order spans two warehouses.

### Why the column is nullable, and what that costs

Lots are created by goods receipt and only by goods receipt. Opening balances, cycle-count
increases, adjustments and inter-warehouse transfers all raise on-hand with no lot behind them, so
an inventory row's physical stock legitimately exceeds the sum of its lots. Requiring a lot would
mean either fabricating lots with no supplier purchase order behind them — the exact untraceable
stock the traceability module exists to prevent — or refusing to reserve stock the business really
holds.

So the un-lotted balance is reservable, and **every read reports it as a named gap**:
`OrderLineAllocation.ReservedWithoutLot`, `OrderLineIssue.IssuedWithoutLot`, `unlottedQuantity` on
the lot-availability route, and a counted warning above the reservations grid. Zero is the healthy
state. It is never a blank column that reads like completeness.

## The declaration is wired into the issue

Gate 5's other named gap: nothing declared consumption, so every despatch produced an
`UndeclaredFulfilment` gap until a person remembered to close it. The issue now declares the lots it
actually moved, in the same transaction, through `ILotFulfilmentDeclarer` →
`MaterialLotFulfilmentDeclarer` → the existing `DeclareConsumptionAsync`. The port exists because
traceability already depends on inventory, and because the declaration carries certificate policy,
override attribution and an append-only audit write that the reservation engine has no business
duplicating.

**Certificate override.** `DeclareConsumptionAsync` refuses an override on a lot that is in date —
correctly, so it cannot become a reflex. A despatch ships several lots at once and the supervisor
signs once, so the adapter routes the single reason only to the lots that are actually lapsed, and
refuses the despatch outright if none of them are.

## Three defects verified and fixed

- **The shipped-quantity ceiling existed only in the browser.** `CreateShipmentPage` set `max` on a
  number input; `ShipmentController` rejected only `Quantity <= 0`. 150 against an order for 100 was
  accepted, written and issued, and could then never be invoiced because the invoice ceiling *is*
  enforced. Now enforced server-side, **cumulative across shipments**, mirroring
  `CommercialFinanceApplicationService`, and rendering the arithmetic in the refusal.
- **The goods issue discarded its own result.** Both `ReserveOrderAsync` and `ConsumeOrderLinesAsync`
  were awaited and thrown away. Partial allocation does not throw, so a quarantined-stock order
  shipped on paper with nothing issued — and was then marked SHIPPED because completion counted
  shipment *lines* rather than issued *quantity*. The result is captured; any line short of its
  declared quantity fails the whole shipment, and the transaction rolls the despatch note back.
- **ATP re-derived inline at four sites.** All four now call `InventoryQuantityMath`. They agreed
  arithmetically on the day they were found, which is why the proof is a source-level test — see
  below.

## Two further defects found while auditing

- **`Inventory.ReorderPoint` was copied from the product once and never re-synced.** Every exception
  surface reads `Inventory.ReorderPoint`; the product screen writes `Product.ReorderPoint`. Editing a
  reorder point on a product that already held stock therefore changed nothing anybody could see —
  the setting existed, the field saved, the alert kept using the old number. `ProductController.Update`
  now pushes it down to the tenant's stock rows.
- **`DeliveryStatuses.Despatched.Contains(...)` inside a LINQ-to-Entities predicate.** Introduced by
  Gate 7 into the over-shipment query while this gate was open. EF Core cannot translate
  `IReadOnlySet<string>.Contains` and throws at query-compile time, so *every* shipment creation
  failed. Fixed by `DespatchedForQuery`, projected from the set rather than restated. **Flagged to
  the Gate 7 owner** — the rule is theirs, only the translation was changed.

## Minimum, maximum and the reorder alert (FR-INV-04)

Nothing of this existed. There were no min/max columns anywhere, no alert record, no delivery
channel, no acknowledgement, no dedupe and no sweep — only a live-computed exception list that
vanished the moment nobody was looking at the screen.

### Where the levels live, and why not on the product

`Inventory.MinimumLevel` and `Inventory.MaximumLevel`, on the (item, warehouse) stock row and
**nowhere else**. `ReorderPoint` already lives in two places — the product master and the stock row —
and copying it down once at creation and never re-syncing is the exact defect this gate found and
fixed. Minimum and maximum get one home, which is also the grain the alert is evaluated at, so there
is no second copy that can drift out of agreement with the first. The reorder point is left where it
is and the levels screen shows it read-only, naming the product record as its source.

### Both columns are NULL, and NULL means "not configured"

This is the trap the requirement sets, and it points both ways. A minimum defaulting to `0` means
"never reorder", which is at least safe. A **maximum** defaulting to `0` means "any stock at all is
too much", and the first sweep after deployment would raise an overstock alert against every row in
every warehouse — after which the channel is filtered and every real alert is lost with it. Null
cannot be misread in either direction: the sweep skips the row and the screen renders "Not set".

**Code default `null`. Backfill `NULL`. They agree, and they mean the same thing.** A non-positive
`ReorderPoint` is read the same way, mirroring the rule `SlaSweepWorker` applies to a non-positive
policy window.

### Why the alert will not fire on everything

A signal that fires on everything carries no information. Four rules keep the population small, and
each is a rule about the business rather than a threshold on the noise:

1. **A level must have been configured.** A tenant that deploys this and configures nothing gets
   zero alerts, which is the only safe first-sweep behaviour. The stock-levels screen states how
   many rows are *unmonitored*, because an empty alerts screen and a healthy warehouse are otherwise
   indistinguishable.
2. **Measured on available-to-promise, not on-hand.** On-hand counts units that are reserved,
   quarantined, damaged or expired. A warehouse holding 500 units all of which are on somebody's
   order can supply nobody, and an on-hand reading would call it healthy. ATP comes from
   `InventoryQuantityMath.AvailableToPromise` — the one definition — and is not re-derived.
3. **Committed incoming supply suppresses the alert.** A row below its minimum with a purchase order
   already covering the gap is not actionable; the buyer has acted. The shortage test is
   `available + incoming`, and the suppressed count is reported rather than left as an absence.
4. **One alert per row, at the worst rung.** Out-of-stock, below-minimum and reorder-point are the
   same fact three times over. The ladder stops at the first match; deterioration resolves the old
   alert and raises the new one, because "this got worse" is new information and "this is still bad"
   is not.

Overstock is deliberately measured on **on-hand**, not on ATP: a maximum level is a statement about
capital and shelf space, and stock already promised to a customer still occupies both.

### The alert is a row, not an email

`inventory_reorder_alerts` carries the arithmetic that raised it — on-hand, available, incoming,
projected, the threshold and the gap — plus an acknowledgement (who, when, **and why**; a reason is
required, because an acknowledgement without one is a mute button with nobody's name on it) and a
resolution. **The resolve transition is what lets the same row alert again**: without it an alert
fires once in the lifetime of a stock row and every shortage after the first is silent, which is
worse than no alert because the screen looks calm. A `UNIQUE (BusinessUnitId, InventoryId, Kind)
WHERE Status IN ('OPEN','ACKNOWLEDGED')` partial index is the dedupe, so two sweep instances produce
a `23505` for the loser rather than two copies.

An email is one delivery of the alert. When no recipient can be resolved, or the provider accepts
nothing, the alert stays OPEN on the screen and the row says "Not emailed" rather than leaving a
blank that reads like "sent".

### The sweep follows `SlaSweepWorker` and does not edit it

`ReorderAlertSweepWorker`, 30-minute period (reorder is a decision measured in days of lead time, and
a long period also stops a row hovering on its threshold from thrashing resolve/raise). Exactly one
query runs with no tenant pushed, under the BYPASSRLS pipeline role, and it reads nothing but tenant
ids; suspended tenants are dropped there through `ITenantWorkGate` for the reason that worker states.
Every other query runs inside a pushed scope and `SweepTenantAsync` **refuses to proceed** if
`db.ScopedTenantId != bu` — the fail-closed check, because a background worker with a null tenant has
both isolation layers off at once and the symptom would be one tenant's part numbers in another
tenant's alert email.

Send-once reuses `SlaSweepWorker.TryClaimEventAsync` / `SettleClaimAsync` and the
`CLAIMED/SENT/UNCERTAIN/RELEASED` settle model — **called, not copied**, and those files are not
edited, so the two engines cannot drift apart on whether a message can be sent twice. The send
returns `SlaSendResult`, not a bool: a bool can only say "nothing threw", so a connection dropped
after acceptance re-sends. `UNCERTAIN` keeps the claim and is never re-sent; only `NotSent` releases.

## Interface — four screens that did not exist

| Screen | Route | Requirement |
|---|---|---|
| Stock levels | `/inventory/levels` | FR-INV-04 — read *and* write min/max, plus the unmonitored count |
| Reorder alerts | `/inventory/reorder-alerts` | FR-INV-04 — the ledger, with acknowledgement |
| Count variance | `/inventory/count-variance` | FR-INV-05 |
| Stock ageing | `/inventory/ageing` | FR-INV-06 |

**The stock-mutation half of the controller now has a caller.** `StockActionsDialog`, reachable from
every row on Availability and Stock levels, calls `stock/count`, `stock/adjust`, `stock/reclassify`,
`stock/transfer`, `stock/safety-stock` and `stock/levels`. Before this, **not one** of them had a
caller anywhere in `Frontend/src`: a client's opening stock could not be entered, a miscount could
not be corrected, damaged goods could not be written down, and stock could not be moved between the
warehouses the product lets you create. Only `stock/reconciliation` and `reservations/sweep` remain
without a screen — both are operator diagnostics rather than daily work, and both are named below.

Every refusal from the ledger is rendered **verbatim**; the client invents no copy. A count reports
its variance in the dialog and links it to the variance report.

## Proof — the test that fails if the wiring is removed

| Requirement | Test | Fails when |
|---|---|---|
| FR-INV-01 | `An_allocated_hold_names_the_material_lot_it_is_holding` | Allocation stops resolving lots |
| FR-INV-01 | `Allocation_takes_the_earliest_expiring_lot_first` | FEFO ordering is dropped |
| FR-INV-01 | `Two_orders_cannot_both_name_the_same_units_of_one_lot` | The lot-level availability check in `ReserveAsync` is removed |
| FR-INV-01 | `Quarantining_one_lot_releases_only_the_orders_that_were_holding_that_lot` | `ReleaseHoldsOnLotAsync` is removed from quarantine |
| FR-INV-01 | `A_hold_that_names_a_quarantined_lot_cannot_be_issued` | The `ConsumeAsync` lot guard is removed |
| FR-INV-01 | `A_recall_names_the_orders_holding_the_lot_not_everyone_holding_the_product` | `GetLotCommitmentsAsync` stops filtering on lot |
| FR-INV-01 | `Stock_with_no_lot_behind_it_is_reported_as_a_gap_rather_than_as_coverage` | The un-lotted split is folded into the total |
| FR-INV-02 | `No_module_re_derives_available_to_promise_by_hand` | Any of the five files reintroduces the subtraction chain |
| FR-INV-03 | `A_goods_issue_declares_the_lots_it_moved_without_anyone_being_asked_to` | The declarer call is removed from the issue |
| FR-INV-03 | `The_where_used_trace_has_no_undeclared_gap_once_the_issue_declares_for_itself` | The declaration stops naming the despatch note |
| FR-INV-03 | `A_lapsed_certificate_stops_the_despatch_until_somebody_signs_for_it` | The compliance state is not computed per lot |
| FR-INV-03 | `An_override_offered_for_lots_that_are_all_in_date_is_refused` | The adapter stops checking whether the override is needed |
| Defect | `Over_shipping_a_line_in_one_despatch_is_refused_by_the_server` | The server-side ceiling is removed |
| Defect | `The_ceiling_is_cumulative_across_despatches_exactly_as_the_invoice_ceiling_is` | The prior-shipment sum is dropped |
| Defect | `A_despatch_that_cannot_issue_the_goods_is_refused_rather_than_recorded` | `IsShort` stops being acted on |
| FR-INV-05 | `A_stock_count_returns_the_variance_it_used_to_throw_away` | Book capture is removed from `RecordCountAsync` |
| FR-INV-05 | `The_variance_report_is_rebuilt_from_the_append_only_ledger` | Counts stop being distinguishable from adjustments |
| FR-INV-06 | `Stock_ageing_dates_a_row_from_its_last_issue_not_its_last_receipt` | Ageing reverts to receipt-dating |
| FR-INV-04 | `A_tenant_that_has_configured_no_levels_raises_no_alerts_at_all` | The "must be configured" gate is dropped and the first sweep mails the whole catalogue |
| FR-INV-04 | `A_maximum_of_zero_does_not_declare_every_item_overstocked` | Zero stops being read as "not configured" |
| FR-INV-04 | `A_row_with_no_level_set_is_reported_as_unmonitored_rather_than_as_healthy` | `IsConfigured` is folded into "no breach" |
| FR-INV-04 | `The_shortage_is_measured_on_what_can_be_promised_not_on_what_is_in_the_building` | The measure reverts to on-hand |
| FR-INV-04 | `A_shortage_already_covered_by_an_order_in_flight_is_not_alerted_on` | Incoming supply stops suppressing |
| FR-INV-04 | `One_short_row_produces_one_alert_and_not_one_for_every_level_it_is_under` | The ladder stops stopping at the worst rung |
| FR-INV-04 | `An_overstock_is_measured_on_physical_units_because_promised_stock_still_fills_the_shelf` | Overstock is measured on ATP |
| FR-INV-04 | `An_alert_that_is_already_open_is_not_raised_again_by_the_next_sweep` | The live-alert dedupe is removed |
| FR-INV-04 | `An_alert_resolves_when_the_stock_recovers_and_the_same_row_can_alert_again_later` | The RESOLVED transition is dropped and every shortage after the first is silent |
| FR-INV-04 | `A_row_that_gets_worse_supersedes_its_alert_rather_than_adding_a_second_one` | Deterioration adds an alert instead of replacing one |
| FR-INV-04 | `Setting_a_minimum_level_is_what_makes_the_alert_engine_see_the_row` | The level saves on a screen and never reaches the row the sweep reads |
| FR-INV-04 | `A_maximum_below_the_minimum_is_refused_by_the_server` | The service-level copy of `CK_Inventory_StockLevels` is removed (SQLite ignores the constraint) |
| FR-INV-04 | `Clearing_a_level_leaves_the_item_unmonitored_rather_than_at_zero` | Null stops being a settable value |
| FR-INV-04 | `An_acknowledgement_with_no_reason_is_refused` | Acknowledgement becomes a mute button |
| FR-INV-04 | `An_acknowledged_alert_still_resolves_itself_when_the_stock_recovers` | Acknowledging ends the alert's life instead of owning it |
| FR-INV-04 | `The_sweep_refuses_to_run_for_a_tenant_the_DbContext_did_not_scope_to` | The worker's fail-closed tenant check is removed |
| FR-INV-04 (UI) | `renders a level that was never configured as "Not set" rather than as an empty cell` | A null level renders as a blank that reads like zero |
| FR-INV-04 (UI) | `states how many rows nobody is watching` | The unmonitored count is dropped and an empty alerts screen reads as a healthy warehouse |
| FR-INV-04 (UI) | `says so when no email copy was accepted` | `notifiedCount = 0` renders as a blank |
| FR-INV-04 (UI) | `refuses to submit an acknowledgement with no reason` | The client stops requiring the reason the server requires |
| FR-INV-04 (PG) | `A_maximum_stock_level_below_the_minimum_is_refused_by_the_database` | `CK_Inventory_StockLevels` is not in the migration |
| FR-INV-04 (PG) | `A_reorder_alert_acknowledged_with_no_name_or_reason_is_refused_by_the_database` | `CK_inventory_reorder_alerts_Acknowledgement` is not in the migration |
| FR-INV-04 (PG) | `The_tenant_role_can_read_and_write_the_reorder_alert_ledger` | The table ships a policy with no `GRANT` and raises `42501` |

`No_module_re_derives_available_to_promise_by_hand` reads source rather than behaviour, and the
reasoning is on the test: all four sites produced the *same number* as the canonical function, so a
behavioural test would pass identically before and after the fix and prove nothing. The failure being
guarded against is a seventh bucket added next year and missed by a hand-written copy.

## Schema delta owed to the migration owner

**No migration was authored and nothing under `Migrations/` was touched.**

### `stock_reservations` — one new column, one new index

| Item | Definition |
|---|---|
| Column | `"MaterialLotId" bigint NULL` |
| Backfill | `NULL` for every existing row |
| Index | `IX_stock_reservations_lot` on `("BusinessUnitId", "MaterialLotId", "Status")`, partial: `WHERE "MaterialLotId" IS NOT NULL` |
| Foreign key | **None, deliberately** — see below |
| RLS | `stock_reservations` already carries its policy and grant. **No change**: the new column is inside an existing tenant table, so no new policy or `GRANT` is required. |

**Backfill semantics, stated explicitly (wiring-contract failure #10).** The code default is `null`
and the backfill is `null`, and they mean the same thing: *this hold names no lot*. That is the
truthful description of every hold taken before this gate, and it is a **visible** state — those
rows render as "No lot — not traceable" on the reservations grid and count towards the warning
above it. There is no default that would be safer: stamping an arbitrary lot on a historical hold
would fabricate traceability, which is worse than reporting its absence.

**Why no foreign key to `material_lots`.** A lot is quarantined and released, never deleted, so
referential integrity buys nothing here — while a hard FK would make the entire reservation ledger
un-writable in any deployment where the traceability tables have not been migrated yet, which is the
state PostgreSQL is in today (Gate 5's three tables are still model-only). The lot id is validated
against the tenant's own lots on the write path instead, which also enforces the things an FK could
not: same inventory row, AVAILABLE status, sufficient unheld quantity.

### `Inventory` — two new columns and one CHECK (FR-INV-04)

| Item | Definition |
|---|---|
| Column | `"MinimumLevel" numeric(18,4) NULL` |
| Column | `"MaximumLevel" numeric(18,4) NULL` |
| Constraint | `CK_Inventory_StockLevels`: `("MinimumLevel" IS NULL OR "MinimumLevel" >= 0) AND ("MaximumLevel" IS NULL OR "MaximumLevel" >= 0) AND ("MinimumLevel" IS NULL OR "MaximumLevel" IS NULL OR "MaximumLevel" >= "MinimumLevel")` |
| Index | None. Both columns are read by a full sweep of the tenant's stock rows, which is already a table scan bounded by the existing `UX_Inventory_BU_Product_Warehouse` |
| RLS | `Inventory` already carries its policy and grant. **No change** — the columns land inside an existing tenant table |

**Backfill semantics, side by side.** Code default `NULL`. Backfill `NULL`. **They agree**, and both
mean *not configured*. There is no `DEFAULT 0` on either column and there must not be one:

- a minimum of `0` means "never reorder" — safe, but a lie about a decision nobody took;
- a maximum of `0` means "any stock at all is too much", and the first sweep after deploy would
  raise an overstock alert against **every row in every warehouse**. That is wiring-contract failure
  #10 in its most damaging direction: the SLA column that backfilled `0` against a 48-hour code
  default would have escalated the order book once; a zero maximum would do it every 30 minutes.

NULL is rendered as the word "Not set" on both screens that show it, never as a blank numeric cell.

### `inventory_reorder_alerts` — new tenant table (FR-INV-04)

| Item | Definition |
|---|---|
| Columns | `Id bigint PK`, `BusinessUnitId bigint NOT NULL`, `InventoryId bigint NOT NULL`, `ProductId bigint NOT NULL`, `WarehouseId bigint NOT NULL`, `Kind varchar(24) NOT NULL`, `Status varchar(16) NOT NULL`, `Severity varchar(16) NOT NULL`, `OnHandQuantity/AvailableQuantity/IncomingQuantity/ProjectedQuantity/ThresholdQuantity/ShortfallQuantity numeric(18,4) NOT NULL`, `RaisedOn timestamp NOT NULL`, `NotifiedCount int NOT NULL`, `AcknowledgedOn timestamp NULL`, `AcknowledgedBy varchar(160) NULL`, `AcknowledgementReason varchar(500) NULL`, `ResolvedOn timestamp NULL`, `ResolutionReason varchar(120) NULL`, `CreatedOn timestamp NOT NULL`, `Version bigint NOT NULL` |
| Backfill | **None — the table is new**, so every column is created rather than backfilled and the code defaults are the only defaults: `Status` = `'OPEN'` (a freshly raised alert has not been taken), `NotifiedCount` = `0` (**"nobody has been emailed", which is a real and visible state rendered as "Not emailed"**, not a placeholder), `Version` = 1. Every nullable column is `NULL` = *not recorded*, **never `''`** |
| Constraint | `CK_inventory_reorder_alerts_Kind`: `"Kind" IN ('OUT_OF_STOCK','BELOW_MINIMUM','REORDER_POINT','OVERSTOCK')` |
| Constraint | `CK_inventory_reorder_alerts_Status`: `"Status" IN ('OPEN','ACKNOWLEDGED','RESOLVED')` |
| Constraint | `CK_inventory_reorder_alerts_Acknowledgement`: all three of `AcknowledgedOn`/`AcknowledgedBy`/`AcknowledgementReason` NULL, or all three NOT NULL |
| Constraint | `CK_inventory_reorder_alerts_Quantities`: `"OnHandQuantity" >= 0 AND "IncomingQuantity" >= 0 AND "AvailableQuantity" >= 0 AND "ThresholdQuantity" > 0 AND "ShortfallQuantity" >= 0` — threshold strictly positive because zero is "not configured" and must never reach this table; shortfall may be zero because a row sitting exactly ON its reorder point has an honest gap of zero |
| Index | `UX_inventory_reorder_alerts_live` — **`UNIQUE (BusinessUnitId, InventoryId, Kind) WHERE "Status" IN ('OPEN','ACKNOWLEDGED')`**. This is the dedupe. Partial rather than total, so a RESOLVED alert frees the key and the same row can legitimately alert again when it drops back |
| Index | `IX_inventory_reorder_alerts_status` on `("BusinessUnitId", "Status", "RaisedOn")` |
| Foreign key | `(BusinessUnitId, InventoryId)` → `Inventory (BUID, Id)`, `ON DELETE RESTRICT` |
| **RLS** | **REQUIRED — new tenant table.** `ALTER TABLE public.inventory_reorder_alerts ENABLE ROW LEVEL SECURITY;` plus `CREATE POLICY nexora_tenant_isolation ON public.inventory_reorder_alerts TO nexora_tenant_app USING ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint) WITH CHECK (same)` — note the column is spelled `BusinessUnitId`, **not** the legacy `BusinessUnitID` |
| **GRANT** | **REQUIRED, and separately.** `GRANT SELECT, INSERT, UPDATE ON public.inventory_reorder_alerts TO nexora_tenant_app;` plus the sequence grant. The schema is deny-by-default: a policy with no grant raises `42501` **before** any row predicate is evaluated, which is the defect that shipped three times in one gate. `DELETE` is deliberately **not** granted — an alert is resolved by a status transition, never removed, so the ledger keeps the evidence that the shortage was real and how it ended |

### `SlaEvents` — no schema change

The reorder sweep claims under `EntityType = 'inventory-reorder'`, `EntityId` = the alert id, and the
existing `Level` vocabulary (`warn`/`critical`). No column, constraint or index changes; the value is
recorded here only because `SlaEvent`'s class documentation enumerates the entity types.

### Nothing else

No other column, table, constraint, default or index changed. `SourceType` on
`inventory_movements` now also carries the value `'StockCount'`; it is a plain `varchar` with **no
CHECK constraint**, so this needs no schema change — but it is recorded here because the variance
report depends on it.

### CHECK constraints and the portable lane

The lot-reservation half of this gate added no CHECK constraint. **FR-INV-04 adds five**, and every
one of them is **unenforced on the portable lane**, which runs SQLite with
`PRAGMA ignore_check_constraints = ON` — a row violating any of them is written silently there and
the suite goes green. So:

- every invariant they express is **also enforced in application code** —
  `StockLedgerService.SetStockLevelsAsync` refuses a negative level and a maximum below the minimum
  and renders the arithmetic in the refusal; `ReorderAlertService.AcknowledgeAsync` refuses an
  acknowledgement with no actor or no reason; `Classify` never emits a non-positive threshold. Those
  refusals are what `Gate6ReorderAlertTests` exercises;
- **the certifying tests for the constraint text are in the PostgreSQL lane**,
  `Gate6ReorderAlertPostgreSqlTests`, and they insert through a raw connection so no application
  guard stands between the test and the database. The third asserts the `GRANT` rather than the
  policy, because a test that only asserts a policy exists passes on an unreadable table.

Those three tests **fail today**, along with the rest of that lane, on
`PendingModelChangesWarning` — the model carries this table and nothing under `Migrations/` does.
That is the intended signal and must not be silenced by weakening the assertions.

## Named gaps — not done, and not implied to be done

- **FR-INV-05 still has no count sheet, and FR-INV-05 is therefore still SHORT.** The variance
  report and the screen that renders it are complete; **the counting process is not**. A count is
  one product in one warehouse per posting. There is no counting session, no count sheet issued
  against a warehouse or a location, no blind count, no second count and no approval step before the
  adjustment posts — so a stock take is a series of independent postings rather than a governed
  document that can be planned, frozen, recounted and signed off. The screen says so above the
  table rather than implying the requirement is met. This is a genuine module of work (a
  `stock_count_sessions` / `stock_count_lines` pair, a freeze of book quantity at issue time, and a
  posting step that is separate from the counting step) and it was not in reach of this gate.
- **`stock/reconciliation` and `reservations/sweep` still have no screen.** Everything else on the
  stock-mutation surface now does. Both of these are operator diagnostics rather than daily work —
  one reports rows where the balance and the movement ledger disagree, the other recovers abandoned
  holds — and both are destructive or alarming enough to want a considered interface rather than a
  button added in passing. Named as owed, not quietly skipped.
- **No HTTP-layer test** on the new routes (`stock/levels` both ways, `reorder-alerts`,
  `reorder-alerts/{id}/acknowledge`). The services behind them are tested and the screens are tested;
  the wire contract between them is not. Same gap Gates 4 and 5 carried, named again rather than
  quietly repeated.
- **The reorder alert has one audience: managers and admins by `RoleRank`.** There is no per-buyer
  routing, no per-category ownership and no digest — a tenant with forty managers mails forty
  copies of each alert. That is the same audience `SlaSweepWorker` escalates to and it is
  deliberately not widened here, but a real buying desk will want the alert to reach the buyer who
  owns the category, and there is nothing in the model that records who that is.
- **Min/max are per (item, warehouse) only.** There is no item-master default that seeds a new
  warehouse's levels, so adding a fourth warehouse means configuring it from scratch. That is a
  conscious trade against re-creating the copy-once-never-resync defect the reorder point carried;
  if a default is wanted later it must be a *derivation* read at evaluation time, not a copy.
- **The variance report cannot recover the book value for counts posted before this gate.** Those
  carry the bare reason `"Physical count"`. The parser falls back to reporting the *variance*
  correctly with a book value of zero, so the number the report exists for is right and the two
  absolute figures are visibly implausible rather than quietly wrong. Pre-launch, there are none.
- **`ConsumeOrderAsync` (order-scoped, all-or-nothing) still exists** and does not declare lots. It
  is not reached by the despatch flow — `ConsumeOrderLinesAsync` is — but it is still exposed on the
  interface and by `POST /api/Order/{id}/consume-stock`. Any issue through that door produces an
  undeclared fulfilment. It should be retired rather than patched; retiring it is an order-module
  change and was not in this gate's mandate.
