# Gate 5 — Shipment and material traceability

**FR-MTR-01 … FR-MTR-05 and FR-MAS-01 … FR-MAS-05.** Same bar as every gate: persistence, domain
behaviour, UI wiring, tenant isolation, audit evidence, automated tests and a real rendered-browser
path. Anything short is reported short, with the missing layer named.

Both modules were built from nothing. There was no lot, batch or serial concept anywhere in the
codebase, and `Shipment` turned out to be purely the **outbound** customer despatch — so decision
R3's separation of inbound from outbound was not a refactor, it was the absence of one of the two.

| Req | Entering | Now | Remaining |
|---|---|---|---|
| FR-MTR-01 · lot/batch/serial against the supplier PO, linked forward | MISSING | **CLOSED** | — |
| FR-MTR-02 · certificates by lot, with expiry | MISSING | **CLOSED** | Upload wiring has no automated test — see below |
| FR-MTR-03 · where-from and where-used trace | MISSING | **CLOSED** | Completeness depends on operator declaration — see below |
| FR-MTR-04 · country of origin and manufacturer per line | PARTIAL | **CLOSED** | — |
| FR-MTR-05 · quarantine blocks allocation until release | MISSING | **CLOSED** | Physical picking is not lot-controlled — see below |
| FR-MAS-01 · six milestones, named Saudi entry points | MISSING | **CLOSED** | — |
| FR-MAS-02 · carrier tracking, status and ETA | MISSING | **PARTIAL — manual only** | The API half is not built. See below |
| FR-MAS-03 · Material Available Date, propagated | MISSING | **CLOSED** | Delivery Management (Module 7) does not exist to receive it — see below |
| FR-MAS-04 · ship-date breach and delivery-risk alerts | MISSING | **CLOSED** | — |
| FR-MAS-05 · partial shipments reconciled | MISSING | **CLOSED** | — |

## Three tracking modes, one table

A serial is a lot of quantity one. Five serialised instruments are five lots, so a recall on one does
not quarantine the other four, and there is no second table and no second trace path to keep in
step. Bulk cable is `UNTRACKED` — the operator is asked for nothing and the lot number is derived
from the receipt — **but the lot still exists**, so no receipt can produce stock that trace cannot
reach. The mode is read from the `Product.SerialTracking`/`BatchTracking` flags, which until now had
never had a reader, and is stamped immutably at receipt.

## Quarantine was proved, not asserted

The requirement is worthless if the flag is advisory, so the agent mutation-tested it: each
enforcement point was removed in turn and the suite re-run.

| Enforcement removed | Result |
|---|---|
| Reclassify into the quarantine bucket | **3 tests failed** |
| The quarantined-lot check on fulfilment declaration | **1 test failed** |
| Release of pre-existing holds | **1 test failed** |

That third one is the interesting find. Quarantine correctly removes the quantity from
available-to-promise, and every allocation path already routes through the canonical ATP function —
but a reservation taken *before* the recall never re-reads availability, so that stock would still
have shipped. Quarantine now releases exactly the shortfall, newest hold first, audited as its own
event, and returns the displaced orders so somebody can re-source them.

Release is explicit, versioned and reasoned: a blank reason is refused, the actor is recorded, and
the event lands in the existing append-only procurement ledger rather than a fourth audit table.

## Certificate expiry is computed, never stored

`ExpiryState` is evaluated at every read, and "does not expire" is kept distinct from the dated
states so that absent never quietly reads as fine. The verdict is per certificate type — a valid
certificate of origin does not cover a lapsed SABER — and the governing expiry for a type is its
latest, so uploading a renewal restores compliance with no supersession workflow to forget.

**An expired certificate blocks the outbound declaration** unless an attributable, reasoned override
is recorded, which then shows on both traces permanently. A *missing* certificate is reported as a
gap but does not block. The reasoning is worth keeping: a recall is a quality decision only a quality
release can reverse, whereas lapsed paperwork is a documented risk a despatch supervisor may
legitimately accept in writing — and nothing in this product knows which certificate types a given
material requires, so a required-certificate matrix would either block legitimate despatches or,
configured leniently, block nothing. Supplying an override for a *compliant* lot is refused, so the
field cannot become a habit.

## Where-used is driven by declaration, not by a join

Membership comes from the declared consumption row. Shipped quantity is reconciliation evidence only
and never adds a lot: the difference surfaces as `UntracedQuantity` and an `UndeclaredFulfilment`
gap, so an order showing "4 shipped, 1 traced" says exactly that rather than presenting a tidy list
of one. This is the same pattern the commercial-case reader was rebuilt on, for the same reason.

## What origin actually means now

The supplier PO line keeps what was **ordered**. The lot carries what **arrived**, plus a snapshot of
what was ordered taken at receipt — so a later trade-terms amendment cannot retro-fit agreement — and
a divergence is reported as an `OriginMismatch` gap. Manufacturer was the half of FR-MTR-04 that had
no home anywhere and now has one.

## Named gaps — not done, and not implied to be done

- **No migration yet.** Three tenant tables exist only in the model, so they have no row-level
  security, no grant, and do not exist in PostgreSQL at all. `TenantIsolationTests` is correctly red
  and names all eight offending tables across both Gate 5 modules; **no opt-out entry was added to
  silence it**, which is the right call.
- **The PostgreSQL lane has not run**, so every `CHECK` constraint above is uncertified. The portable
  lane runs SQLite with `PRAGMA ignore_check_constraints = ON`. Each invariant is also enforced in
  the service and tested there, but the constraint text itself is unproven — and that is exactly how
  Gate 4's five-value status constraint nearly shipped.
- **No HTTP-layer test** on any of the seven new routes, and **no frontend test** on three pages and
  three dialogs. The same gap Gate 4 carried, named again rather than quietly repeated.
- **The certificate upload path has no automated test.** Expiry semantics are tested by inserting
  rows directly; the inspection → storage → attachment → digest wiring is not covered.
- **Permission granularity is wrong for the domain.** Quarantine and release are gated on
  `Products/Edit` — the same permission as editing the product catalogue. Quality release should be
  separately grantable, but a new module needs both a catalog entry and a seeded row before any
  non-super-admin can hold it, so shipping the screen ahead of the seed would 403 every real user.
  Must land with a migration.
- **Reservations are inventory-scoped, not lot-scoped.** Quarantining one of two lots correctly
  removes its quantity from ATP, but the surviving reservation does not *name* the other lot, so a
  picker could still physically pull from the quarantined one. The system-side door is shut at
  declaration; the physical control is not. Closing it properly is lot-level reservation — Gate 6.
- **Nothing declares consumption automatically.** Despatch issues stock without naming a lot, so
  every shipment currently produces an `UndeclaredFulfilment` gap until a user declares it. That is
  honest — the gap is visible rather than fabricated — but where-used is only as complete as operator
  discipline until the declaration is wired into the despatch flow.

## Module 6 — the Gate 4 / Gate 5 seam, and the decision behind it

An earlier review found that `RECEIVED_AT_WAREHOUSE` stamped a milestone and derived an availability
date while **creating no goods receipt, moving no received quantity and never touching the supplier
PO status**. The milestone implied a receipt had happened and nothing had.

**The decision: the goods receipt stays a deliberate human act. The milestone never fakes one. What
crosses the boundary is the other direction — posting a receipt settles the inbound shipment that
carried the material.**

Why not have arrival raise the receipt:

- A receipt needs four facts the milestone does not carry: the **counted** quantity (which is
  precisely where a shortage or damage is discovered), the receiving warehouse, a receipt number,
  and a **lot declaration**. `MaterialLotRecorder.ResolveLotNumbers` refuses a batch-tracked line
  with no supplier batch number and a serial-tracked line without one serial per unit. A milestone
  that tried to raise a receipt would either invent those identifiers or crash the milestone write,
  and an invented batch number poisons FR-MTR-02 certificates and FR-MTR-05 recall for the life of
  the tenant.
- The model already keeps the two events apart on purpose. `PutawayLeadDays` is the working days
  between goods **reaching** the warehouse and being available to promise — which is exactly the
  count and put-away the receipt performs. If arrival were the receipt, that number would be zero by
  definition.
- A receipt moves `QtyOnHand`. A logistics status update that silently changed stock on hand would
  turn a mis-keyed milestone into a financial misstatement.

What is now real:

- `SupplierShipmentLine.ReceivedQuantity` is written **only** by `InboundShipmentReceiptSettlement`,
  called from inside `ProcurementApplicationService.PostGoodsReceiptAsync`'s own serializable
  transaction. Stock, the lot that explains it and the shipment that carried it commit or fail
  together.
- A receipt that fully takes in a shipment stamps `RECEIVED_AT_WAREHOUSE` on it, re-derives the
  Material Available Date and appends an `INBOUND_SHIPMENT_RECEIPTED` event naming the goods
  receipt. A **part** receipt leaves the shipment where it is — saying it had arrived would be the
  same lie in the opposite direction.
- Allocation across a line's shipments is **oldest first**, stated in the event payload so the
  attribution is auditable rather than implicit. A cancelled shipment is never allocated to.
- Receipted units in excess of any shipment's manifest are deliberately **not** forced onto a
  shipment: goods can legitimately be received against a line nobody recorded a shipment for.
- A shipment a receipt has booked in **cannot be cancelled**, because cancellation releases the
  shipped quantity back to the purchase order line and doing that for units already on the shelf
  would let the same material be shipped twice.
- The panel carries a **Goods receipt** column. A shipment reading "received at warehouse · not
  booked in · 8 outstanding" is the honest state, and the milestone dialog says plainly that the
  milestone does not book stock in.

Proved by mutation: disabling the `SettleAsync` call fails four tests
(`Posting_the_goods_receipt_settles_the_shipment_that_carried_the_material`,
`A_part_receipt_leaves_the_shipment_in_flight_with_the_shortfall_visible`,
`A_receipt_fills_the_oldest_open_shipment_first`,
`A_shipment_a_receipt_has_booked_in_cannot_be_cancelled`).

## Module 6 — named gaps

- **FR-MAS-02's API half is NOT built.** There is no carrier or freight-forwarder integration, no
  polling worker and no provider adapter. Decision D4 — the Phase 1 carrier subset — is still open,
  and a fabricated integration against an unnamed provider would be a mock in a production path.
  `TrackingSource` exists as the named seam and is written `MANUAL` on every row, so an API-sourced
  update is distinguishable from a keyed one the day one is built, with no schema change. The manual
  path is complete: carrier, forwarder reference, ETA, explicit ETA withdrawal, and a re-derivation
  on every change.
- **FR-MAS-03 propagates as far as there is anywhere to propagate to.** The derived date is pushed
  onto `IncomingInventory.ExpectedOn`, which ATP and the sourcing net-requirement calculation
  already read. **Delivery Management (Module 7, FR-DLM-01..07) does not exist**, so the last leg of
  the BRD sentence has no destination yet. It is recorded here as owed rather than claimed.
- **Four tables still exist only in the EF model.** `ports_of_entry`, `supplier_shipments`,
  `supplier_shipment_lines`, `inbound_logistics_policies` — no RLS, no grant, absent from
  PostgreSQL. `TenantIsolationTests` is correctly red naming all four plus the three traceability
  tables; **no opt-out entry was added**.
- **No HTTP-layer test on any of the ten routes, and no frontend test on the panel or its four
  dialogs.** The same gap the rest of the build carries, named again rather than quietly repeated.
- **The PostgreSQL lane has not certified the CHECK constraints.** The portable lane runs SQLite
  with `PRAGMA ignore_check_constraints = ON`, so `CK_supplier_shipments_Milestone`,
  `CK_supplier_shipment_lines_ReceivedQuantity` and the rest are unproven text. Every one is also
  enforced in the service and tested there.
- **Lot is still not modelled on the shipment line.** FR-MAS-05 asks for availability per shipment
  **and per lot**. The lot now exists (Module 5) and is created by the receipt, so a shipment's lots
  are reachable through the goods receipt — but `SupplierShipmentLine` carries no `MaterialLotId`
  and the settlement does not attribute individual lots to individual shipments. For a line split
  across two shipments and receipted in one go, which lot came off which vessel is not recorded.

## Module 6 — the exact schema delta owed to the migration owner

Table names are snake_case; **column names are quoted PascalCase**, matching every other procurement
table. Timestamps are `timestamp without time zone`; calendar dates are `date`.

### `ports_of_entry` (new)

| Column | Type | Null | Default |
|---|---|---|---|
| `Id` | `bigint` identity | NOT NULL | — |
| `BusinessUnitId` | `bigint` | NOT NULL | — |
| `Code` | `character varying(40)` | NOT NULL | — |
| `Name` | `character varying(200)` | NOT NULL | — |
| `Kind` | `character varying(24)` | NOT NULL | — |
| `CountryCode` | `character varying(2)` | NOT NULL | `'SA'` |
| `City` | `character varying(120)` | NULL | — |
| `IsActive` | `boolean` | NOT NULL | `true` |
| `Version` | `bigint` | NOT NULL | `1` |
| `CreatedOn` | `timestamp without time zone` | NOT NULL | — |
| `CreatedBy` | `character varying(255)` | NOT NULL | — |
| `ModifiedOn` | `timestamp without time zone` | NULL | — |
| `ModifiedBy` | `character varying(255)` | NULL | — |

`PK_ports_of_entry (Id)`; `AK_ports_of_entry_BusinessUnitId_Id (BusinessUnitId, Id)`;
`IX_ports_of_entry_BusinessUnitId_Code` UNIQUE; `IX_ports_of_entry_BusinessUnitId_IsActive_Kind`;
`FK_ports_of_entry_BusinessUnits_BusinessUnitId` ON DELETE RESTRICT;
`CK_ports_of_entry_Kind CHECK ("Kind" IN ('SEAPORT','AIRPORT','DRY_PORT','LAND_BORDER'))`.

### `supplier_shipments` (new)

`Id bigint` identity; `BusinessUnitId bigint NOT NULL`; `SupplierPurchaseOrderId bigint NOT NULL`;
`ShipmentNumber varchar(100) NOT NULL`; `Milestone varchar(32) NOT NULL`;
`MilestoneOccurredOn date NOT NULL`; `ReadyAtFactoryOn`, `DepartedOriginOn`, `InTransitOn`,
`ArrivedSaudiPortOn`, `CustomsClearanceOn`, `ReceivedAtWarehouseOn`, `CancelledOn` all `date NULL`;
`CancellationReason varchar(1000) NULL`; `PortOfEntryId bigint NULL`;
`CarrierName varchar(255) NULL`; `TrackingReference varchar(160) NULL`;
`TrackingSource varchar(16) NOT NULL DEFAULT 'MANUAL'`; `EtaDate date NULL`;
`EtaUpdatedOn timestamp NULL`; `EtaUpdatedBy varchar(255) NULL`;
`MaterialAvailableDate date NULL`; `MaterialAvailableBasisKind varchar(32) NULL`;
`MaterialAvailableBasisDate date NULL`; `AppliedCustomsClearanceDays integer NULL`;
`AppliedPutawayDays integer NULL`; `MaterialAvailableComputedOn timestamp NULL`;
`MaterialAvailableUnavailableReason varchar(500) NULL`; `IdempotencyKey varchar(160) NOT NULL`;
`RequestHash varchar(64) NOT NULL`; `Version bigint NOT NULL DEFAULT 1`;
`CreatedOn timestamp NOT NULL`; `CreatedBy varchar(255) NOT NULL`; `ModifiedOn timestamp NULL`;
`ModifiedBy varchar(255) NULL`.

`PK`/`AK (BusinessUnitId, Id)`; UNIQUE `(BusinessUnitId, ShipmentNumber)`; UNIQUE
`(BusinessUnitId, IdempotencyKey)`; `(BusinessUnitId, SupplierPurchaseOrderId)`;
`(BusinessUnitId, Milestone, EtaDate)`; `(BusinessUnitId, MaterialAvailableDate)`. Composite FKs to
`supplier_purchase_orders (BusinessUnitId, Id)` and `ports_of_entry (BusinessUnitId, Id)`, both
RESTRICT. Constraints: `CK_supplier_shipments_Milestone` (the seven values),
`CK_supplier_shipments_TrackingSource` (`'MANUAL','CARRIER_API'`),
`CK_supplier_shipments_ArrivalLocation`, `CK_supplier_shipments_Cancellation`,
`CK_supplier_shipments_AppliedLeadDays`.

### `supplier_shipment_lines` (new)

`Id`, `BusinessUnitId`, `SupplierShipmentId`, `SupplierPurchaseOrderLineId`, `ProductId` all
`bigint NOT NULL`; `ShippedQuantity numeric(18,4) NOT NULL`;
**`ReceivedQuantity numeric(18,4) NOT NULL DEFAULT 0`**; `Version bigint NOT NULL DEFAULT 1`.
UNIQUE `(BusinessUnitId, SupplierShipmentId, SupplierPurchaseOrderLineId)`;
index `(BusinessUnitId, SupplierPurchaseOrderLineId)`. Composite FK to
`supplier_shipments (BusinessUnitId, Id)` ON DELETE CASCADE; to
`supplier_purchase_order_lines (BusinessUnitId, Id)` RESTRICT; to `Products (Buid, Id)` RESTRICT.
`CK_supplier_shipment_lines_Quantity CHECK ("ShippedQuantity" > 0)` and
`CK_supplier_shipment_lines_ReceivedQuantity CHECK ("ReceivedQuantity" >= 0 AND "ReceivedQuantity" <= "ShippedQuantity")`.

### `inbound_logistics_policies` (new)

`Id bigint` identity; `BusinessUnitId bigint NOT NULL` UNIQUE;
**`CustomsClearanceLeadDays integer NULL`** and **`PutawayLeadDays integer NULL`** — NULL means NOT
CONFIGURED and must stay reachable; `Version bigint NOT NULL DEFAULT 1`;
`CreatedOn timestamp NOT NULL`; `ModifiedOn timestamp NULL`; `ModifiedBy varchar(255) NULL`.
`CK_inbound_logistics_policies_LeadDays` bounds each to 0–365 **when not null**.
FK to `BusinessUnits` RESTRICT.

### One column on an existing table

`supplier_purchase_order_lines."ShippedQuantity" numeric(18,4) NOT NULL DEFAULT 0`, plus
`CK_supplier_purchase_order_lines_ShippedQuantity CHECK ("ShippedQuantity" >= 0 AND "ShippedQuantity" <= "OrderedQuantity")`.

### Backfill semantics — stated, per failure #10

All four tables are **new**, so nothing is backfilled: every row they will ever hold is written by
this build with the C# defaults. The one existing table takes `ShippedQuantity DEFAULT 0`, and 0 is
correct for every pre-existing row because no shipment record exists to have shipped against it —
the code default and the backfill default are the same value and mean the same thing.
`CustomsClearanceLeadDays`/`PutawayLeadDays` must be backfilled **NULL, never 0**: 0 is the positive
assertion "clearance really is same-day" and would silently produce a Material Available Date equal
to the ETA on every shipment in the tenant.

### RLS and GRANT — required for all four

Follow the `GovernSupplierSourcingAndProcurement` pattern exactly:

```sql
DO $block$
DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY[
        'ports_of_entry', 'supplier_shipments', 'supplier_shipment_lines', 'inbound_logistics_policies'
    ]
    LOOP
        EXECUTE format('ALTER TABLE public.%I ENABLE ROW LEVEL SECURITY', table_name);
        EXECUTE format('DROP POLICY IF EXISTS nexora_tenant_isolation ON public.%I', table_name);
        EXECUTE format(
            'CREATE POLICY nexora_tenant_isolation ON public.%I TO nexora_tenant_app '
            'USING ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint) '
            'WITH CHECK ("BusinessUnitId" = NULLIF(current_setting(''nexora.business_unit_id'', true), '''')::bigint)',
            table_name);
        EXECUTE format('GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public.%I TO nexora_tenant_app', table_name);
        EXECUTE format('GRANT USAGE ON SEQUENCE %s TO nexora_tenant_app',
            pg_get_serial_sequence(format('public.%I', table_name), 'Id'));
    END LOOP;
END
$block$;

REVOKE DELETE ON TABLE public.supplier_shipments FROM nexora_tenant_app;
REVOKE DELETE ON TABLE public.ports_of_entry FROM nexora_tenant_app;
```

The policy names the column as spelled — `"BusinessUnitId"`, not `BusinessUnitID`. DELETE is revoked
on the two tables shipments and milestones reference: a shipment is cancelled, never deleted, and a
port is deactivated because rows point at it. `supplier_shipment_lines` keeps DELETE because it
cascades from its shipment.

## Found outside this gate, recorded as E54

`FileController.DownloadAttachment` falls through to a legacy storage provider that dependency
injection leaves null, so **customer PO attachments 404 in production today** — the parent type Gate
3 added cannot actually be downloaded. The route is also gated on `Leads/View`, the wrong permission
for a commercial document. The agent declined to build on it and added a separate, correctly gated
route for lot certificates instead.
