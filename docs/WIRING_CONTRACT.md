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
| Refused AI processing | `IngestionOutcomeState.AI_NOT_AUTHORIZED` must be added to `ck_source_document_occurrences_outcome_state` on `source_document_occurrences`. **No column, no index, no default changes** — `outcome_state` is already `varchar(48)` written through `HasConversion<string>()`, and the new name is 18 characters. This one is worse than the others in the table: the portable lane runs SQLite with `PRAGMA ignore_check_constraints = ON`, so the write is green there and raises `23514` on PostgreSQL the first time a tenant uploads a PDF. **Deploy the constraint before the code.** |

**Backfill semantics must be stated for each**, per failure #10. Two already have a known answer:
the supplier-charge columns default to `0`, which is correct for duty and other but **not** for
historical `FreightAmount` on revisions the workbench wrote before the split — those collapsed
freight, duty and other into one figure and never stored the discount, so the original split is not
recoverable by any backfill. Harmless pre-launch; it must not be discovered after go-live.
