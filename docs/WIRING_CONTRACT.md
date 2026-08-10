# The wiring contract

**A field is not done when it is saved. It is done when something depends on it.**

Every defect in this list was found in Nexora, in this build, by a reviewer rather than by a test.
Each one passed its own test suite. That is the point: they are not bugs of carelessness, they are
bugs of *incompleteness*, and incompleteness is invisible to a test that only exercises the layer
that exists.

This page is the definition of done for any new field, column, entity, status, policy or control.
It is a checklist to run **before** the work is called complete, and it is required reading in
every implementation brief.

---

## The twelve failures, each observed here

| # | Failure | What it looked like in Nexora |
|---|---|---|
| 1 | **Column with no writer** | FR-SPO-03: the acknowledgement column, its CHECK constraint, its revised lead time, and an entire SLA escalation sweep that read it — and nothing in the codebase could set it. Every order was permanently unacknowledged and the sweep chased a state the system could not leave. FR-SPO-06 was identical: five trade-term columns, no writer. |
| 2 | **Writer with no reader** | Acknowledgement shipped complete, then the workbench read model projected none of it. A buyer recorded a counter, nothing changed on screen, and the only feedback was a 409 the next time they pressed the button. The revised lead time — the whole point — was invisible. |
| 3 | **Reader with a silent fallback that hides the gap** | Quote and RFQ list screens read `case ?? parent.case ?? lead.case`. A document with a NULL column displayed a case anyway, masking exactly the rows the gap report existed to surface. |
| 4 | **Policy with no grant** | Three tables shipped row-level-security policies with no `GRANT`. PostgreSQL checks the grant first and raises `42501` before evaluating any row predicate — so the table was not "more isolated", it was unreadable. `TenantIsolationTests` passed, because it asserts a policy exists. |
| 5 | **Setting with no way to set it** | `SupplierInputTaxRecoverable` re-bases every landed cost in the system. No controller, no service, no UI — changeable only by direct SQL. The ratified decision existed solely as a database default. |
| 6 | **History table never written** | `CustomFieldValueHistory` — declared, mapped, tenant-filtered, protected from update and delete by an interceptor — and its `Create` is called from nowhere. An auditor testing that control finds no exceptions *because there are no rows*. False assurance is worse than an absent table. |
| 7 | **Control that reports success while doing nothing** | A reviewer corrects a supplier quote's freight; the correction is validated, recorded, accepted — and never applied to the projection. The reviewer sees success and has changed nothing. |
| 8 | **Validation that only rejects the impossible** | Output VAT rejected negative amounts. Null and zero sailed through, so every quote carried no tax and nobody noticed. |
| 9 | **New state, old guards** | Adding `ACKNOWLEDGED` broke goods receipt and cancellation, because each guard carried its own hand-written status list. A supplier accepting an order became the act that made it impossible to receive. |
| 10 | **Migration default that inverts the meaning** | A new SLA column backfilled `0` while the code default was 48 hours. Zero meant "instantly overdue", so the first sweep after deploy would have escalated the entire order book. Invisible to every test, because tests build rows from the C# defaults and never see the backfill. |
| 11 | **Display convention load-bearing in a write path** | `'$ '` stripped from a price input. The formatted value has no space, so the strip was a no-op, `Number()` returned `NaN`, and `NaN` serialises to `null` — the line posted with no price at all. |
| 12 | **A number compared without its unit** | The AI auto-approve cap is a bare decimal compared against amounts in the supplier's own currency with no conversion. The same ceiling authorises ~3.75x more spend depending only on which currency the supplier quoted in. |

---

## The checklist

Run this for **every** new field, column, entity, status value, policy or control.

### Persistence
- [ ] Entity property and EF configuration exist, with length, precision and nullability stated
- [ ] The schema delta is reported to the migration owner — column, type, nullability, default, index, constraint, foreign key
- [ ] **The backfill default means the same thing as the code default.** State them side by side and confirm
- [ ] Tenant-scoped table has **both** an RLS policy **and** an explicit `GRANT` — the schema is deny-by-default
- [ ] The policy names the column as actually spelled (`BusinessUnitId` vs the legacy `BusinessUnitID`)

### Write path
- [ ] Something can set it, through the application, by a user or a governed process
- [ ] Validation rejects the *wrong* values, not merely the impossible ones — null and zero are values
- [ ] The write is attributable: who, when, and why where a reason is warranted
- [ ] Bulk and import paths write it too, not only the single-record controller

### Read path
- [ ] Something reads it, and the read **depends** on it rather than re-deriving the answer another way
- [ ] No `??` fallback silently substitutes a value from a parent or a sibling
- [ ] A missing value surfaces as a **visible gap**, never as a blank that reads like a loading state
- [ ] It reaches the DTO, the API contract and the client type

### Interface
- [ ] A user can see it, and where appropriate set it, without a developer
- [ ] The unit is stated wherever a number could be money, weight, time or quantity
- [ ] Server validation messages surface verbatim; the client invents no copy that contradicts them

### Coherence across modules
- [ ] Every guard, state machine and allowed-value set that should now consider it, does. **Search for all of them** — a status added in one method and not another is failure #9
- [ ] Shared sets live next to the constants they belong to, not inline in each method
- [ ] Downstream consumers — reporting, exports, documents, ZATCA, analytics — either consume it or are recorded as owing it
- [ ] Any control it feeds actually blocks something. A flag a busy user can bypass is not a control

### Proof
- [ ] A test fails if the wiring is removed. Not a test that the value round-trips — a test that the **dependence** exists
- [ ] Behaviour only PostgreSQL can certify is in the PostgreSQL lane. The portable lane runs SQLite with `PRAGMA ignore_check_constraints = ON`, so every CHECK constraint is unenforced there
- [ ] Anything deliberately left undone is **named** in the report and the gate document

---

## The one question

Before calling any field complete, answer it out loud:

> **If I deleted this field right now, what would break?**

If the honest answer is "nothing", it is not wired. If the answer is "a test that only asserts it
was saved", it is still not wired. Something real must depend on it — a decision, a document, a
guard, a screen a person looks at.

---

## Accumulated schema debt owed to the migration owner

Kept here rather than in a gate document because it crosses gates, and because every entry is a
model change that is **green on the portable lane and broken on PostgreSQL** — the portable lane
builds its schema from the model with `EnsureCreated()`, so an unmigrated table exists there and
every test passes.

Two assertions in `PostgreSqlProductionDialectTests.AllMigrationsApplyToAnEmptyPostgreSqlDatabase`
now make both halves of this fail loudly instead:

- `HasPendingModelChanges()` — catches a model change with no migration, which
  `GetPendingMigrationsAsync()` structurally cannot see: it compares migrations *authored* against
  migrations *applied*, and a table that exists only in `OnModelCreating` is invisible to it.
- **row-level security without a grant** — catches the defect that shipped three times in one gate.

Outstanding at the time of writing, from the agents' own reported deltas:

| Area | Delta |
|---|---|
| Gate 5 inbound logistics | `ports_of_entry`, `supplier_shipments`, `supplier_shipment_lines`, `inbound_logistics_policies` — **RLS + GRANT**. All four are new, so every column is created rather than backfilled and the code defaults are the only defaults: `supplier_shipment_lines."ReceivedQuantity" numeric(18,4) NOT NULL DEFAULT 0` (0 = nothing booked in, which is correct for a new line and for every row the table will ever hold), `supplier_shipments."TrackingSource" NOT NULL DEFAULT 'MANUAL'`, `"Version" bigint NOT NULL DEFAULT 1` on all four, and `inbound_logistics_policies."CustomsClearanceLeadDays"/"PutawayLeadDays"` **NULL — "not configured", never 0**. Plus one column on an existing table: `supplier_purchase_order_lines."ShippedQuantity" numeric(18,4) NOT NULL DEFAULT 0` with `CK_supplier_purchase_order_lines_ShippedQuantity` |
| Gate 5 traceability | `material_lots`, `material_lot_certificates`, `material_lot_consumptions` — **RLS + GRANT** |
| Quote withdrawal | `QuoteRemovalRecords` + `Quote.RemovedOn/RemovedBy/RemovalReason` — **RLS + GRANT** |
| Output VAT | `CommercialMatchingPolicies.OutputTaxRatePercent`; `SupplierInputTaxRecoverablePercent` replacing the boolean, which must be **dropped** rather than left orphaned |
| Tax treatment | `QuoteItem.TaxCategory`, `TaxRatePercentApplied` |
| Tax evidence | `Supplier.TaxRegistrationNumber`, `BusinessUnit.TaxRegistrationNumber` |
| Spend cap | `AgentPolicies.CurrencyId` |
| Supplier charges | `supplier_quote_revisions.DutyAmount/OtherAmount/DiscountAmount`, and the widened `CK_supplier_quote_revisions_Values` |
| SLA claim release policy | Six columns on `SlaEvents` plus a reshaped unique index. `"Status" varchar(16) NOT NULL DEFAULT 'SENT'` — **then `DROP DEFAULT`**, because the backfill default and the code default deliberately differ and both are correct: every existing row exists *only* because a send returned true (the old release path DELETED the row otherwise), so `SENT` is the honest history; a NEW row is a claim that has not been settled yet, so the code default is `CLAIMED`. Also `"Recipient" varchar(320) NULL`, `"Provider" varchar(64) NULL`, `"AcceptanceReference" varchar(200) NULL`, `"SettledOn" timestamp NULL`, `"OutcomeReason" varchar(120) NULL` — **NULL means "not recorded", never `''`**; historical rows never recorded who they were mailed to and no backfill can reconstruct it. `UX_SlaEvents_BU_DedupKey` is dropped and recreated as **`UNIQUE (BusinessUnitId, DedupKey) WHERE "Status" <> 'RELEASED'`**: releasing is now a status transition rather than a delete, so a released row would otherwise hold its key forever and silence the alert permanently. **No duplicate collapse is needed**, unlike `20260804181147`: that migration computed a key from existing data and so could create collisions, whereas this change only *refines* the key (a recipient discriminator) for rows written after the deploy, rewrites nothing, and makes the index strictly less restrictive. The re-send hazard the key change creates is handled in code instead — `SlaSweepWorker.TryClaimEventAsync` also suppresses on the legacy recipient-less key, which can cost a copy but can never produce a duplicate |
| Gate 6 lot reservation | `stock_reservations."MaterialLotId" bigint NULL`, plus partial index `IX_stock_reservations_lot` on `("BusinessUnitId","MaterialLotId","Status") WHERE "MaterialLotId" IS NOT NULL`. **No new table, so no new RLS policy and no new GRANT** — the column lands inside an existing tenant table that already carries both. **No foreign key** to `material_lots`, deliberately: a lot is quarantined and released but never deleted, so an FK buys nothing, while a hard FK would make the reservation ledger un-writable in any deployment where Gate 5's traceability tables are still model-only — which is today. The lot is validated on the write path instead (same tenant, same inventory row, AVAILABLE, sufficient unheld quantity). Backfill is `NULL`, which is also the code default, and the two mean the same thing: *this hold names no lot*. That is true of every pre-existing hold and it is **rendered as a visible gap**, never as a blank. |
| Gate 7 outbound delivery + POD | Three new tenant tables — `delivery_proofs`, `delivery_proof_lines`, `delivery_shortfall_decisions` — **RLS + GRANT**, policy column spelled `"BusinessUnitId"`. All three are new, so every column is created rather than backfilled and the code defaults are the only defaults: `delivery_proofs."Version" bigint NOT NULL DEFAULT 1`; `delivery_proof_lines."DespatchedQuantity"/"AcceptedQuantity" numeric(18,6) NOT NULL` with **no default** (both are always written, and a defaulted accepted quantity is the value nobody looked at becoming the value an invoice is raised against); `delivery_proofs."GpsLatitude"/"GpsLongitude" numeric(10,7) NULL` and `"GpsAccuracyMeters" numeric(9,2) NULL` — **NULL means "no fix", never `0`**, which would be a coordinate at the equator. Constraints: `CK_delivery_proofs_Gps`, `CK_delivery_proofs_GpsCapturedOn`, `CK_delivery_proofs_GpsAccuracy`, `CK_delivery_proofs_Version`, `CK_delivery_proof_lines_Quantities`, `CK_delivery_proof_lines_Reason`, `CK_delivery_proof_lines_ShortfallHasReason`, `CK_delivery_shortfall_decisions_Decision`. Unique indexes `UX_delivery_proofs_BU_Shipment` (one POD per consignment — this is the immutability guarantee, not a service check), `UX_delivery_proofs_BU_IdempotencyKey`, `UX_delivery_proof_lines_BU_Proof_ShipmentItem`, `UX_delivery_shortfall_decisions_BU_ProofLine` (append-only decision). |
| Gate 7 shipment lifecycle | Four columns on the existing `Shipments` table. **`"DeliveryStatus" varchar(24) NOT NULL DEFAULT 'SCHEDULED'` — and the backfill is `'DISPATCHED'`, NOT the column default.** These deliberately differ and this is failure #10 with the sign flipped: a *new* shipment is SCHEDULED because nothing has left, but every row already in the table was written by `ShipmentController.CreateShipment`, which issues stock in the same transaction — so every existing row has already despatched, and backfilling `'SCHEDULED'` would un-despatch the entire open order book and free every shipped quantity back onto its order line. `"DeliveryStatusChangedOn" timestamptz NULL` and `"DeliveryStatusChangedBy" varchar(255) NULL` — **left NULL on backfill**, meaning "predates the ladder and is unattributed"; inventing an actor for a transition nobody made is worse than an honest null, and `CK_Shipments_DeliveryStatusAttribution` permits the both-null case for exactly this reason. `"DeliveryCityID" integer NULL` — **NULL = not mapped to a governed region, never `0`.** Plus `CK_Shipments_DeliveryStatus`, `IX_Shipments_BU_DeliveryStatus`, and a tenant-qualified FK to `SetCity` which needs `ALTER TABLE public."SetCity" ADD CONSTRAINT "AK_SetCity_BUID_CityID" UNIQUE ("BUID","CityID")` first. Also `ALTER TABLE public."ShipmentItems" ADD CONSTRAINT "AK_ShipmentItems_ID_ShipmentID" UNIQUE ("ID","ShipmentID")` — `ShipmentItems` carries no tenant column, so the pair (line, shipment) is the only key a proof line can point at without being able to name another shipment's line. **No RLS change**: both are existing tables and `Shipments` already carries its policy on `"BusinessUnitID"` — note the legacy capitalisation. |
| Gate 7 delivery exceptions | `commercial_exception_cases."DeliveryProofLineId" bigint NULL` + tenant-qualified FK to `delivery_proof_lines`, and **both source CHECK constraints must be DROPPED and RECREATED**, not added alongside: `CK_commercial_exception_cases_Source` and `CK_commercial_exception_cases_SourceIdentity` each gain a `DeliveryShortfall` branch, and the two pre-existing branches gain `AND "DeliveryProofLineId" IS NULL`. Widening only by adding a branch would let the new column be set alongside `FollowUpTaskId`, and "which source is this case from" would stop having one answer. **No column default** — NULL is correct for every existing row, since none of them is a delivery shortfall. `ExceptionType` is already `varchar(40)` written through `HasConversion<string>()` and `DeliveryShortfall` is 17 characters, so no width change. **Deploy the constraint before the code**, for the same reason `IngestionOutcomeState.AI_NOT_AUTHORIZED` below does: the portable lane runs SQLite with `PRAGMA ignore_check_constraints = ON`, so the first delivery shortfall raises `23514` on PostgreSQL and nowhere else. |
| Gate 6 reorder alerts | **New tenant table `inventory_reorder_alerts` — RLS + GRANT.** Policy on `"BusinessUnitId"` (the modern spelling, not `BusinessUnitID`); `GRANT SELECT, INSERT, UPDATE` to `nexora_tenant_app` **and the sequence** — **`DELETE` deliberately NOT granted**, because an alert is resolved by a status transition and never removed, so the ledger keeps the evidence that the shortage was real and how it ended. The table is new, so nothing is backfilled and the code defaults are the only defaults: `"Status" NOT NULL DEFAULT 'OPEN'`, `"NotifiedCount" int NOT NULL DEFAULT 0` (**0 = "nobody has been emailed", a real state rendered as "Not emailed" on the screen, never a placeholder**), `"Version" bigint NOT NULL DEFAULT 1`; every nullable column is **`NULL` = "not recorded", never `''`**. Dedupe is **`UNIQUE (BusinessUnitId, InventoryId, Kind) WHERE "Status" IN ('OPEN','ACKNOWLEDGED')`** — partial, so a RESOLVED alert frees the key and the same row can alert again the next time it runs short; a total unique index would make every shortage after the first one silent. Four CHECKs (`_Kind`, `_Status`, `_Acknowledgement` — all-or-nothing on who/when/why — and `_Quantities`, threshold strictly positive because zero means "not configured"). Plus two columns on the existing `Inventory` table: **`"MinimumLevel" numeric(18,4) NULL` and `"MaximumLevel" numeric(18,4) NULL` with `CK_Inventory_StockLevels`** and **no new policy or grant** (existing tenant table). **Backfill `NULL`, code default `NULL`, and they agree: "not configured".** There must be no `DEFAULT 0` on either — a minimum of 0 means "never reorder" (a lie about a decision nobody took), and a **maximum of 0 means "any stock at all is too much", which would raise an overstock alert against every row in every warehouse on the first sweep and every 30 minutes after it**. That is failure #10 in its most damaging direction |
| Refused AI processing | `IngestionOutcomeState.AI_NOT_AUTHORIZED` must be added to `ck_source_document_occurrences_outcome_state` on `source_document_occurrences`. **No column, no index, no default changes** — `outcome_state` is already `varchar(48)` written through `HasConversion<string>()`, and the new name is 18 characters. This one is worse than the others in the table: the portable lane runs SQLite with `PRAGMA ignore_check_constraints = ON`, so the write is green there and raises `23514` on PostgreSQL the first time a tenant uploads a PDF. **Deploy the constraint before the code.** |

**Backfill semantics must be stated for each**, per failure #10. Two already have a known answer:
the supplier-charge columns default to `0`, which is correct for duty and other but **not** for
historical `FreightAmount` on revisions the workbench wrote before the split — those collapsed
freight, duty and other into one figure and never stored the discount, so the original split is not
recoverable by any backfill. Harmless pre-launch; it must not be discovered after go-live.
