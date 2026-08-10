# Gate 8 — follow-ups, dashboards and scheduled reporting

Scope: **FR-DSH-01..07**, the **FR-CST** customer items, and the FR-SBF-01 reminder triggers that
belong to the follow-up engine. Owner: dashboards / reporting / follow-ups.

---

## 1. The defect that started this gate

`Repositories/DashboardRepository.cs` computed a board-facing average margin that was wrong three
ways at once, and looked exactly like a figure that was right.

| # | Defect | Why it mattered |
|---|---|---|
| 1 | **Wrong cost basis.** It read `Product.FinalLandedCost ?? Product.UnitCost`. `FinalLandedCost` is not a landed cost: `Repositories/SupplierPurchaseHistoryRepository.cs:131-137` sets it to the *last purchase row's bare `UnitPrice`*, with no freight, duty or currency; it is also free-typed in the product form (`Controllers/ProductController.cs:203,323`) and imported from spreadsheet column 28 (`Services/ProductUploaderService.cs:245`). | The cost the *price* was actually built on lives on `CustomerQuoteSourcingDecision.SupplierLandedUnitCost` and was read by nothing. |
| 2 | **Unweighted mean of per-line percentages.** | One 1-unit line at 60% and one 10,000-unit line at 5% reported **32.5%**. The value-weighted answer is **5.0%**. That is not a rounding difference; it is the gross margin of nothing. |
| 3 | **No date filter and no outcome filter.** | The sample was every quote line ever written in the tenant, drafts and lost bids included. |

**Replaced by** `Reporting/GrossMarginService.cs`, exposed at `GET /api/dashboard/gross-margin`.
The four `AvgMarginPct` / `MarginSampleLines` / `MarginLinesExcludedForFx` / `TotalQuoteLines`
fields were **removed** from `PipelineAnalyticsDTO`, not deprecated: leaving a wrong number on a
live API contract behind a warning comment is how it survives.

### The corrected computation

```
revenue = Σ FX( CustomerUnitPrice      × Quantity , decision.CurrencyId → base )
cost    = Σ FX( SupplierLandedUnitCost × Quantity , decision.CurrencyId → base )
margin% = (revenue − cost) / revenue × 100
```

over `CustomerQuoteSourcingDecision` rows joined to quotes that are **ACCEPTED or ORDERED** with
`OutcomeOn` inside a stated half-open window `[from, to)`, taking the **latest decision per quote
line** (the table's only uniqueness is on `IdempotencyKey`; a re-priced line inserts a second row).

Cost and revenue come off **the same record in the same currency**, so they cannot drift apart.
Both totals go through `IFxConversionService`, whose contract nulls a total rather than blending
unconvertible currencies — so an unconvertible sample yields `status: "unavailable"` with the
reason, never a partial number. The precedent is
`CommercialIntelligence/Growth/GrowthIntelligenceService.cs:279`.

### The two cost bases

Recoverable input VAT was being wrongly capitalised into landed cost (register R15/R18), so margin
history spans two bases. **Decision rows carry no basis stamp** — R20 ratified a full recompute
before pilot *instead of* a calculation-version column — so the only evidence available is the
tenant governance ledger, where `CommercialMatchingPolicyService` records a
`COMMERCIAL_POLICY_UPDATED` event with a before/after snapshot.

The service reads that ledger and takes the **most recent entry where
`SupplierInputTaxRecoverablePercent` actually moved**. Using the policy row's `ModifiedOn` instead
would flag a basis change every time somebody adjusted a price tolerance, and a disclosure that
fires on everything is one people learn to skip.

Behaviour across the boundary, stated rather than blended:

- Sample entirely after the change → single basis, and the note says so.
- Sample entirely before → the note says the figure is on the **superseded** basis and is not
  comparable with later periods.
- Sample straddling → `costBasisNote` says it **blends two cost bases**, gives the split counts
  either side, states that the split is inferred from when each decision was recorded because no
  basis stamp exists, and `marginPercentCurrentBasisOnly` carries the comparable subset.
- No ledger entry → the note says the ledger records no change to it.

---

## 2. Worked example — old versus new

Two accepted lines on one accepted quote, in one currency:

| Line | Quantity | Landed unit cost (sourcing decision) | Customer unit price | `Product.FinalLandedCost` |
|---|---|---|---|---|
| A | 1 | 40.00 | 100.00 | 40.00 |
| B | 10,000 | 95.00 | 100.00 | 95.00 |

**Old figure.** Per-line margins are `(100−40)/100 = 60%` and `(100−95)/100 = 5%`. Mean = **32.5%**.
Reported as the tenant's margin, over a sample that also included every draft and lost bid.

**New figure.** revenue `= 1×100 + 10,000×100 = 1,000,100`; cost `= 1×40 + 10,000×95 = 950,040`;
margin `= 50,060 / 1,000,100 = ` **5.0%**, with `sampleLines: 2`, `sampleQuotes: 1` and the
currency stated.

**The cost basis alone moves it again.** Take one line: 10 units, decision landed cost 80.00,
price 100.00, and a product card whose `FinalLandedCost` is 40.00 because the last purchase row was
a bare unit price with no freight or duty. Old: **60%**. New: **20%**. Both are precise; only one
is a margin.

Both examples are pinned as tests
(`Margin_is_weighted_by_value_not_averaged_across_lines`,
`Cost_comes_from_the_sourcing_decision_not_the_product_card`).

---

## 3. Per-requirement status

### Module 13 — Dashboard and Reporting

| ID | Status | Evidence / what is missing |
|---|---|---|
| **FR-DSH-01** | **Partial** | `GET /api/dashboard/deadline-board` covers open enquiries by time-to-close with its two gap disclosures, and `release-01` covers received / requiring review / qualification. **Missing:** quotations awaiting response and orders being fulfilled exist only on `pipeline-analytics` (all-time, not on the landing page); shipments in transit and overdue follow-ups have no landing-page tile at all. Not claimed closed. |
| **FR-DSH-02** | **Partial** | Win/loss ratio and revenue-from-wins are on `pipeline-analytics`; **gross margin is now correct and traceable** (new). **Missing:** RFQ response time has no agreed baseline (register D11) and on-time delivery and supplier performance have no computation anywhere. |
| **FR-DSH-03** | **Partial** | Drill-down to source transactions exists on `release-01` KPIs only (`DashboardRelease01DrillDownIdentifierDTO` → lead/rfq/quote routes) and on the deadline board. The gross-margin panel exposes its full evidence breakdown but **not yet per-record drill-down**; pipeline, workload and brand demand have none. Not claimed closed. |
| **FR-DSH-04** | **Built — see §8 for what is short** | `GET /api/search` (`Search/GlobalSearchService.cs`) searches customers, suppliers, products, leads, RFQs, quotes, orders and shipments in one call, with the date-range and status filters the requirement names, per-family module permissions, and account-team scoping on the customer family. The Navbar keyword router is **deleted**: a term that matches nothing now says so and navigates nowhere. **Short of the requirement:** matching is unranked case-insensitive `CONTAINS`, not a ranked full-text search; there is no saved-filter/"filter across screens" surface; and the status filter spans only families with a `SetupMaster` status row — customers, products, suppliers and RFQs are **excluded and named in `notes`** rather than silently ignored. |
| **FR-DSH-05** | **Built for `release-01`; the other dashboard endpoints are still unscoped** | The binary is gone. `Authorization/AccountTeamScopeResolver.cs` resolves three tiers — `assigned_accounts`, `managed_scope`, `tenant` — and `GET /api/dashboard/release-01` reads through it: a supervisor's figures now include their team members' work **and the accounts their teams hold**, and the payload states the tier and the team ids. **Short:** `deadline-board`, `document-yield`, `pipeline-analytics` and `team-workload` still read tenant-wide; scoping them is the same one-line change per endpoint but each needs its own denominator review, so they are named rather than silently converted. |
| **FR-DSH-06** | **Built — closure blocked on the awaited migration** | `Reporting/` — governed report catalog, per-tenant subscriptions with daily/weekly/monthly cadence, PDF (QuestPDF) and Excel (EPPlus) rendering, tenant-scoped delivery worker, on-demand download of the identical document, and 20 tests. **It is NOT closed**, for two stated reasons: `ReportSubscriptions` exists only in the EF model, so it is green on the portable lane and absent on PostgreSQL until §4.1 is migrated; and no rendered-browser pass has been performed. **Approved deviation:** reports are English-only under register R6, which defers Arabic/RTL beyond this release, so "bilingual where required" is unmet by decision rather than by omission. |
| **FR-DSH-07** | **Partial** | Dashboard, deadline board and brand demand use MUI breakpoint grids and reflow; the new margin panel and reports screen follow the same convention with their tables in `overflow-x` containers. **`TeamWorkloadPage`'s table does not reflow.** No device verification has been performed, so this is not claimed closed. |

### Module 10 — Customer (FR-CST)

| ID | Status | Evidence / what is missing |
|---|---|---|
| **FR-CST-01** | **Built — closure blocked on the awaited migration** | `Models/Customer.AccountMaster.cs` adds `CommercialRegistrationNumber`, `TaxRegistrationNumber`, `Sector`, `RegionStateId` and `AccountTeamId`, configured in `Models/ErpRfqAutomationContext.CustomerMaster.cs` (§4.4). VAT reuses `Tax/TaxRegistrationNumbers` — **the same type Supplier and BusinessUnit use**, not a second KSA rule. CR uses the matching new `MasterData/CommercialRegistrationNumbers`. Sector is a closed list; NULL means *not classified* and is never defaulted to PRIVATE. Region is an FK into the tenant's own `SetState` master — the list `RoutingScopeKeys.Territory` already resolves sales territory against (§8 on the Gate 7 seam). All five are settable on the customer form, rendered as stated gaps on the list and detail screens, and audited by the existing interceptor without any change to it. **Not closed:** the columns exist only in the EF model until §4.4 is migrated. |
| **FR-CST-02** | **Built — closure blocked on the awaited migration** | `Customer.AccountTeamId` supplies the customers-to-teams edge that did not exist; membership comes from the existing effective-dated `SalesTeamMembership`, so *"is this customer mine"* is now one join and answerable inside a SQL predicate. `Authorization/AccountTeamReadFilter.cs` is the one definition of the read rule, and the customer list, the customer detail read, the quick search and the release-01 dashboard all call it. `RoutingScopeKeys.KeyAccountTeam` is **no longer structurally underivable** — it derives from this column, which is what unblocked FR-DSH-05's account tier. **Stated deliberately:** a customer with NO account team stays readable tenant-wide (there is no team to restrict it to) and is rendered as a visible gap; assigning a team is the act that narrows. **Not closed:** same migration dependency, and *tasks* (`FollowUpTask`) are not yet account-scoped — see §8. |
| **FR-CST-03** | **Partial** | `CustomerDetailPage` shows open RFQs, quotes in progress and order status. **Delivery status is absent** — `CustomerContextController` reads no shipment at all. |
| **FR-CST-04** | **Not met** | No customer price agreement or frame contract entity. Incoterms and payment terms are modelled on the **supplier** side only. |
| **FR-CST-05** | **Partial** | Win ratio is computed two ways (`WinRatePct`, `InquiryWinRatePct`) and its denominator problems are already documented in `GATE3_PROGRESS.md`. Honored response time, on-time delivery rate and payment behaviour are **not computed anywhere**. |

### Module 8 — System-Based Follow-Up (the reminder triggers)

FR-SBF-01 names five triggers. The register's item **E35** says "only 1 of the 5 exists" and that
"supplier-response-overdue and invoice-due have no upstream data plumbing at all". **That second
half is now out of date** — `SupplierSolicitation.DueOn` has been a validated, dispatched, indexed
column all along, and `ReceivableDocument.DueDate` exists too. The gap was readers, not columns.

| Trigger | Before this gate | Now |
|---|---|---|
| (a) pending Quote/No-Quote decisions | **Absent.** `Rfqitem.ParticipationDecision` had a writer and a read-model, and no sweep. | **Built** — `SweepUndecidedRfqLinesAsync`. |
| (b) RFQs approaching closure without a decision | **Partial.** The lead-deadline sweep chases every lead near close and cannot tell worked work from untouched work. | **Built** — the same sweep is decision-aware, so an RFQ whose lines are all decided is deliberately *not* chased. The original lead sweep is unchanged. |
| (c) supplier responses overdue against SLA | **Absent.** `SupplierSolicitation.DueOn` was written on dispatch, rendered into the outbound email, and read back by nothing. | **Built** — `SweepSupplierResponseOverdueAsync`. |
| (d) shipments approaching/missing ETA | **Present** for inbound (`inbound-shipment-late`, `inbound-shipment-risk`). | Unchanged. **Outbound customer shipments are still not swept** — `Shipment.EstimatedDeliveryDate` has no reader. Gate 7's surface. |
| (e) invoices approaching payment due date | **Absent.** | **Not built — deliberately blocked on register D7**, which is open, owned by Finance and explicitly listed as blocking Gate 8. `ReceivableDocument.DueDate` exists but the AR subsystem is inside the E36 out-of-BRD freeze, ZATCA invoicing is sequenced last under R1, and the semantics a reminder needs — which document states count, what a partial allocation does, whether a disputed invoice is chased — are business decisions. Engineering must not invent them. |

**True state: 3 of 5 met (a, b, c), 1 met for the inbound half only (d), 1 blocked on a decision (e).**

Also reported, not built (each is a decision or another gate's surface):

- **FR-SBF-02** — thresholds are configurable; there is no configurable escalation *chain* (who
  after how many reminders). Recipients are resolved by `RoleRank` in code.
- **FR-SBF-03** — no single activity log records every automated *and* manual follow-up with its
  channel and outcome. `SlaEvent` is a send-once claim ledger with no channel, no recipient and no
  outcome, and a released claim leaves no trace at all. Four partial ledgers exist.
- **FR-SBF-04** — follow-up rules remain tenant-global. Register **E34** is the open question.
- **FR-SBF-05** — `FollowUpTask` has exactly one producer in the codebase (quote send), so
  *My Follow-ups* is a quote-chase list rather than a consolidation. Register **E33**.

---

## 4. Schema delta owed to the migration owner

**No migration was generated and nothing under `Migrations/` was touched.**

### 4.1 New tenant table `ReportSubscriptions`

Entity `Reporting/ReportingEntities.cs`; EF configuration
`Models/ErpRfqAutomationContext.Reporting.cs`, spliced from `OnModelCreatingPartial` via
`modelBuilder.ApplyReportingModel(...)`.

| Column | Type | Null | Default | Notes |
|---|---|---|---|---|
| `Id` | bigint identity | no | — | PK |
| `BusinessUnitId` | bigint | no | — | tenant column; global query filter applied |
| `ReportKey` | varchar(60) | no | — | `deadline-board` \| `pipeline` \| `gross-margin` |
| `Cadence` | varchar(20) | no | — | `DAILY` \| `WEEKLY` \| `MONTHLY` |
| `Format` | varchar(10) | no | — | `PDF` \| `XLSX` |
| `HourUtc` | integer | no | — | 0–23, validated in the service |
| `DayOfWeek` | integer | no | — | 0 = Sunday … 6 = Saturday; weekly only |
| `DayOfMonth` | integer | no | — | 1–28; later days do not exist in every month |
| `WindowDays` | integer | no | — | 1–732 |
| `Recipients` | varchar(2000) | no | — | semicolon-separated; the service refuses an empty list |
| `IsActive` | boolean | no | — | |
| `NextRunOn` | timestamp | **YES** | **none** | **NULL means NOT SCHEDULED.** See the backfill note below. |
| `LastRunOn` | timestamp | yes | — | |
| `LastRunOutcome` | varchar(30) | yes | — | `DELIVERED` \| `FAILED` \| `NOTHING_TO_REPORT` |
| `LastRunDetail` | varchar(1000) | yes | — | shown on the screen, not only in a log |
| `CreatedOn` | timestamp | no | `now()` | |
| `CreatedBy` | varchar(255) | no | — | |
| `ModifiedOn` | timestamp | yes | — | |
| `ModifiedBy` | varchar(255) | yes | — | |

Indexes:

```sql
CREATE INDEX "IX_ReportSubscriptions_BU_Active_NextRun"
    ON public."ReportSubscriptions" ("BusinessUnitId", "IsActive", "NextRunOn");

CREATE UNIQUE INDEX "UX_ReportSubscriptions_BU_Report_Cadence_Format"
    ON public."ReportSubscriptions" ("BusinessUnitId", "ReportKey", "Cadence", "Format");
```

**Backfill semantics (wiring-contract failure #10).** This is a new table, so there are no existing
rows — but `NextRunOn` is deliberately **nullable with no server default**, because a `NOT NULL`
column defaulting to `default(DateTime)` would read as "due since year 1" and the first sweep after
deployment would mail the entire estate at once. The code default is `null`; the column default is
none; both mean *not scheduled*. Every other column is written by the service on save, and the
service refuses to save a row that would schedule something undeliverable.

**RLS and GRANT — mandatory, and both halves are required.** The schema is deny-by-default since
`20260723120000_CompleteTenantRlsCoverage` revoked the public default privileges, so a policy
without a grant is not a narrower boundary — it is a table that raises `42501` before any row
predicate runs. Three tables shipped that way in Gate 4.

```sql
ALTER TABLE public."ReportSubscriptions" ENABLE ROW LEVEL SECURITY;
ALTER TABLE public."ReportSubscriptions" FORCE  ROW LEVEL SECURITY;

CREATE POLICY nexora_tenant_isolation ON public."ReportSubscriptions" TO nexora_tenant_app
    USING      ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint)
    WITH CHECK ("BusinessUnitId" = NULLIF(current_setting('nexora.business_unit_id', true), '')::bigint);

GRANT SELECT, INSERT, UPDATE, DELETE ON TABLE public."ReportSubscriptions" TO nexora_tenant_app;
```

The column is spelled `BusinessUnitId`, not the legacy `BusinessUnitID`.

### 4.2 New column on `SlaPolicies`

```sql
ALTER TABLE "SlaPolicies" ADD COLUMN "QuoteDecisionReminderDays" integer NOT NULL DEFAULT 3;
```

Code default `3`, backfill default `3` — stated side by side because they must agree. A backfill of
`0` would also be safe (the sweep reads non-positive as *not configured*, register R12) but would
ship the feature dark. `SlaPolicies` already has its RLS policy and grant; no change there.

### 4.3 No schema change for the new `SlaEvents` vocabulary

`SlaEvents.EntityType` is `varchar(40)` with **no CHECK constraint** — `ConfigureSlaModel` applies
only `HasMaxLength(40)`. The four new values (`rfq-undecided-lines`, `supplier-response-overdue`,
`scheduled-report`, plus the Gate 5 inbound pair) therefore need no DDL. Worth stating explicitly
because the equivalent addition on `source_document_occurrences.outcome_state` *does* carry a CHECK
constraint, passes silently on the SQLite lane and raises `23514` on PostgreSQL. `Level` gains
`sent`, same column, same reasoning.

---

### 4.4 Five new columns on the existing `Customers` table (FR-CST-01 / FR-CST-02)

Entity `Models/Customer.AccountMaster.cs`; EF configuration
`Models/ErpRfqAutomationContext.CustomerMaster.cs`, spliced from `OnModelCreatingPartial` via
`modelBuilder.ApplyCustomerMasterModel(Database.IsNpgsql())`.

**No new table, therefore no new RLS policy and no new GRANT.** Every column lands inside
`public."Customers"`, which already carries both. Two things the migration owner must not lose:
the tenant column on this table is spelled **`"BUID"`** (the legacy spelling, not `BusinessUnitId`)
and the existing policy names it; and the primary key column is spelled **`"ID"`**.

| Column | Type | Null | Default | Notes |
|---|---|---|---|---|
| `CommercialRegistrationNumber` | varchar(30) | **YES** | **none** | KSA CR. NULL = *not captured* |
| `TaxRegistrationNumber` | varchar(50) | **YES** | **none** | Same length and rule as `Suppliers.TaxRegistrationNumber` |
| `Sector` | varchar(20) | **YES** | **none** | `GOVERNMENT` \| `SEMI_GOVERNMENT` \| `PRIVATE`. NULL = *not classified* |
| `RegionStateId` | integer | **YES** | **none** | FK → `"SetState"."StateId"`, `ON DELETE RESTRICT` |
| `AccountTeamId` | bigint | **YES** | **none** | FK → `"Teams"."ID"`, `ON DELETE RESTRICT` |

```sql
ALTER TABLE public."Customers"
    ADD COLUMN "CommercialRegistrationNumber" varchar(30) NULL,
    ADD COLUMN "TaxRegistrationNumber"        varchar(50) NULL,
    ADD COLUMN "Sector"                       varchar(20) NULL,
    ADD COLUMN "RegionStateId"                integer     NULL,
    ADD COLUMN "AccountTeamId"                bigint      NULL;

ALTER TABLE public."Customers"
    ADD CONSTRAINT "FK_Customers_AccountTeam"
        FOREIGN KEY ("AccountTeamId") REFERENCES public."Teams" ("ID") ON DELETE RESTRICT,
    ADD CONSTRAINT "FK_Customers_RegionState"
        FOREIGN KEY ("RegionStateId") REFERENCES public."SetState" ("StateId") ON DELETE RESTRICT;

-- THE read predicate of FR-CST-02. Filtered: an unassigned customer is never selected by it.
CREATE INDEX "IX_Customers_BU_AccountTeam"
    ON public."Customers" ("BUID", "AccountTeamId") WHERE "AccountTeamId" IS NOT NULL;
CREATE INDEX "IX_Customers_BU_RegionState"
    ON public."Customers" ("BUID", "RegionStateId") WHERE "RegionStateId" IS NOT NULL;
CREATE INDEX "IX_Customers_BU_CommercialRegistrationNumber"
    ON public."Customers" ("BUID", "CommercialRegistrationNumber")
    WHERE "CommercialRegistrationNumber" IS NOT NULL;
CREATE INDEX "IX_Customers_BU_TaxRegistrationNumber"
    ON public."Customers" ("BUID", "TaxRegistrationNumber")
    WHERE "TaxRegistrationNumber" IS NOT NULL;

-- PostgreSQL only: POSIX regex, so these are not emitted on the portable (SQLite) lane, which
-- additionally runs with PRAGMA ignore_check_constraints = ON. The application validators
-- (Tax/TaxRegistrationNumbers, MasterData/CommercialRegistrationNumbers, CustomerSectors) run on
-- both lanes, so the portable suite still certifies the rule.
ALTER TABLE public."Customers"
    ADD CONSTRAINT "CK_Customers_TaxRegistrationNumber" CHECK (
        "TaxRegistrationNumber" IS NULL OR (
        "TaxRegistrationNumber" ~ '^[A-Z0-9./]{5,50}$' AND (
        "TaxRegistrationNumber" !~ '^3[0-9]*$' OR "TaxRegistrationNumber" ~ '^3[0-9]{13}3$'))),
    ADD CONSTRAINT "CK_Customers_CommercialRegistrationNumber" CHECK (
        "CommercialRegistrationNumber" IS NULL OR (
        "CommercialRegistrationNumber" ~ '^[A-Z0-9]{5,30}$' AND (
        "CommercialRegistrationNumber" !~ '^[0-9]+$' OR
        "CommercialRegistrationNumber" ~ '^[0-9]{10}$'))),
    ADD CONSTRAINT "CK_Customers_Sector" CHECK (
        "Sector" IS NULL OR "Sector" IN ('GOVERNMENT', 'SEMI_GOVERNMENT', 'PRIVATE'));
```

**Backfill semantics (wiring-contract failure #10).** All five columns are **NULL with no server
default, and the code default is also `null`** — the two agree, and both mean *not recorded*. Each
is stated separately because they do not all mean the same kind of nothing:

- `CommercialRegistrationNumber` / `TaxRegistrationNumber` — **not captured.** Backfilling `''`
  would be worse than NULL: an empty string satisfies "has a value", passes a NOT NULL check, and
  makes a customer look verified. There is no value to derive one from; the existing
  `Customers` rows have never held either identifier.
- `Sector` — **not classified.** Deliberately **not** backfilled to `PRIVATE`, which is the
  tempting default because most customers are private: the difference between "somebody said
  private" and "nobody has said" decides whether government procurement rules apply, and a
  backfill would erase that distinction permanently and invisibly.
- `RegionStateId` — **not stated.** It is not derived from the existing free-typed
  `BillingState` / `ShippingState` text. Those columns are exactly why routing has to resolve
  wording against aliases at read time, and a fuzzy backfill would write a guess into a governed
  key. Routing continues to fall back to the address wording, so nothing regresses.
- `AccountTeamId` — **no account team assigned**, which is **not** the same as "restricted to
  nobody". A customer with no team stays readable by everyone holding the Customers permission —
  exactly as before the column existed — and is rendered as a visible gap. The alternative,
  treating NULL as "invisible", would make every pre-existing customer disappear on deploy: the
  same failure as a `0` backfill on an SLA column, in a different costume.

**Neither foreign key can express the tenant boundary** — both are single-column keys onto tables
whose own tenant column the constraint cannot see. `CustomerRepository.ValidateTenantReferencesAsync`
is the only thing enforcing it, on every write path, and `Gate8CustomerMasterTests` pins both halves.

### 4.5 No schema change for the global search

`GET /api/search` reads existing columns only. It adds **no table, no column, no index and no
constraint**, and it is stated here so its absence from the migration is deliberate rather than
forgotten. It is also where the honest limit lives: matching is `LOWER(col) LIKE '%term%'`, which
**cannot use a b-tree index**. On the pilot's data volumes that is a sequential scan of at most a
few thousand rows per family and is fine; at production scale it needs either `pg_trgm` GIN indexes
or a `tsvector` column, and that is a schema change nobody should make speculatively before there
is a query plan to look at.

---

## 5. Tests that fail if the wiring is removed

**No requirement in this gate is claimed closed** — including the four in the second wave. FR-CST-01,
FR-CST-02, FR-DSH-04 and FR-DSH-05 are all built and tested, and all four are held open: the first
two by the awaited migration (§4.4), and all four by the absence of a rendered-browser pass. FR-DSH-06
is built and tested but cannot be
certified until `ReportSubscriptions` is migrated (§4.1) and a rendered-browser pass is done — the
gate definition requires persistence and a real browser path, and a table that exists only in the EF
model is precisely the "green on the portable lane, broken on PostgreSQL" debt the wiring contract
warns about. Everything below is therefore evidence of *wiring*, not a completion claim. Each test
asserts a dependence rather than a round-trip: delete the wiring and the test fails.

| Wiring | Test |
|---|---|
| Margin is value-weighted, not a mean of ratios | `Gate8GrossMarginTests.Margin_is_weighted_by_value_not_averaged_across_lines` |
| Cost is read from the sourcing decision, not the product card | `Gate8GrossMarginTests.Cost_comes_from_the_sourcing_decision_not_the_product_card` |
| Drafts and lost bids are excluded | `Gate8GrossMarginTests.Only_accepted_quotes_are_sampled` |
| The period filter is real | `Gate8GrossMarginTests.The_period_filter_excludes_acceptances_outside_the_window` |
| A missing acceptance date is disclosed, never coalesced into the window | `Gate8GrossMarginTests.An_accepted_quote_with_no_acceptance_date_is_disclosed_not_absorbed` |
| A re-priced line is counted once | `Gate8GrossMarginTests.A_repriced_line_contributes_once_at_its_latest_decision` |
| Untraceable lines are counted as a gap, not costed from elsewhere | `Gate8GrossMarginTests.Accepted_lines_with_no_sourcing_decision_are_counted_as_a_gap` |
| "Unavailable" instead of a number, with a reason | `Gate8GrossMarginTests.No_accepted_line_carrying_a_sourcing_decision_returns_unavailable_with_a_reason`, `...An_unconvertible_currency_makes_the_figure_unavailable_rather_than_partial` |
| Two cost bases are disclosed, and the comparable subset is offered | `Gate8GrossMarginTests.A_sample_straddling_the_input_tax_correction_says_so_and_offers_the_current_basis` |
| The disclosure does not fire on unrelated policy edits | `Gate8GrossMarginTests.A_policy_edit_that_did_not_move_the_recoverable_percent_is_not_a_basis_change` |
| **No aggregate crosses a tenant boundary** | `Gate8GrossMarginTests.No_aggregate_crosses_a_tenant_boundary`, `...The_tenant_scoped_context_sees_only_its_own_accepted_lines` |
| The report worker runs each tenant inside a pushed scope | `Gate8ScheduledReportingTests.Each_tenant_is_swept_inside_its_own_pushed_scope` |
| It **fails closed** when the scope does not apply | `Gate8ScheduledReportingTests.A_scope_that_does_not_apply_stops_the_sweep_instead_of_running_unscoped` |
| A delivered report carries only its own tenant's rows | `Gate8ScheduledReportingTests.A_delivered_report_carries_only_its_own_tenants_rows` |
| Send-once across sweeps | `Gate8ScheduledReportingTests.A_second_sweep_in_the_same_period_does_not_send_the_report_twice` |
| A send the provider never took releases its claim (RELEASED is the only reclaimable status) | `Gate8ScheduledReportingTests.A_delivery_the_provider_never_took_releases_its_claim_and_records_the_failure` |
| An UNCERTAIN send keeps its claim and is never retried | `Gate8ScheduledReportingTests.An_uncertain_delivery_keeps_its_claim_and_is_never_retried` |
| A dormant schedule resumes in the future instead of replaying its backlog | `Gate8ScheduledReportingTests.A_long_dormant_schedule_resumes_in_the_future_rather_than_replaying_its_backlog` |
| An empty report is recorded, not emailed | `Gate8ScheduledReportingTests.A_report_with_no_rows_is_recorded_and_not_emailed` |
| `NextRunOn = NULL` means not scheduled, never "due now" | `Gate8ScheduledReportingTests.A_subscription_with_no_next_run_is_not_due` |
| The schedule advances from the occurrence, so it cannot drift | `Gate8ScheduledReportingTests.The_schedule_advances_from_the_occurrence_not_from_the_run_time` |
| Validation rejects the wrong values, not merely the impossible | `Gate8ScheduledReportingTests.A_subscription_with_no_recipient_is_refused`, `...Values_that_would_schedule_something_undeliverable_are_refused` |
| Undecided lines near close are chased | `Gate8FollowUpTriggerTests.An_rfq_closing_soon_with_undecided_lines_reminds_its_owner` |
| A fully-decided RFQ is **not** chased (the signal carries information) | `Gate8FollowUpTriggerTests.An_rfq_whose_lines_are_all_decided_is_not_chased` |
| A non-positive policy value means "not configured" | `Gate8FollowUpTriggerTests.A_non_positive_reminder_window_means_not_configured_and_sends_nothing` |
| `SupplierSolicitation.DueOn` now has a reader | `Gate8FollowUpTriggerTests.A_supplier_who_missed_the_response_deadline_alerts_the_owner` |
| No deadline means no alert | `Gate8FollowUpTriggerTests.A_solicitation_with_no_deadline_is_silent` |

### Second wave — FR-CST-01/02 and FR-DSH-04/05 (37 tests, all passing)

| Wiring | Test |
|---|---|
| The customer VAT number is validated by the **same** definition as the supplier one, not a second KSA rule | `Gate8CustomerMasterTests.The_customer_vat_number_is_validated_by_the_same_definition_as_the_supplier_one` |
| A claimed Saudi CR number must be exactly 10 digits, and the message says which rule broke | `Gate8CustomerMasterTests.A_claimed_saudi_cr_number_must_be_ten_digits` |
| A foreign registration with a country prefix is still accepted | `Gate8CustomerMasterTests.A_foreign_registration_carrying_its_country_prefix_is_accepted` |
| Blank canonicalises to NULL, never `''` | `Gate8CustomerMasterTests.A_cr_number_is_canonicalised_and_blank_becomes_null_not_empty` |
| The sector list is closed and "not stated" is NULL, not PRIVATE | `Gate8CustomerMasterTests.The_sector_list_is_closed_and_an_unstated_sector_is_null_not_private` |
| Region is the tenant's **own** governed master — another tenant's region id is refused | `Gate8CustomerMasterTests.A_region_from_another_tenants_master_is_refused` |
| So is another tenant's account team (the single-column FK cannot say so) | `Gate8CustomerMasterTests.An_account_team_from_another_tenant_is_refused` |
| Zero is refused rather than stored as a key that would match nothing forever | `Gate8CustomerMasterTests.A_zero_account_team_is_refused_rather_than_stored` |
| The new fields persist **and** appear in the master-data change trail by name | `Gate8CustomerMasterTests.The_new_master_fields_are_persisted_and_audited` |
| **A rep reads their own teams' accounts and not another team's** — delete the `InAccountScope` call and this fails | `Gate8AccountTeamScopeTests.A_rep_reads_their_own_teams_accounts_and_not_another_teams` |
| A customer with no account team stays readable, and the row says so | `Gate8AccountTeamScopeTests.A_customer_with_no_account_team_stays_readable_and_says_so` |
| An out-of-scope read by id is indistinguishable from not-found (no enumeration oracle) | `Gate8AccountTeamScopeTests.Reading_an_out_of_scope_customer_by_id_is_indistinguishable_from_not_found` |
| A named owner keeps an account in another team's book | `Gate8AccountTeamScopeTests.A_named_owner_keeps_an_account_that_belongs_to_another_team` |
| An **expired** ownership row grants nothing | `Gate8AccountTeamScopeTests.An_expired_ownership_row_does_not_keep_the_account_readable` |
| An **expired** team membership grants nothing | `Gate8AccountTeamScopeTests.An_expired_membership_grants_no_account_scope` |
| **The middle tier exists**: a supervisor reaches managed teams and their sub-teams, and is not tenant-wide | `Gate8AccountTeamScopeTests.A_supervisor_reaches_their_managed_teams_and_their_sub_teams_but_not_the_tenant` |
| …and reads strictly more than a rep and strictly less than the tenant | `Gate8AccountTeamScopeTests.A_supervisor_reads_more_than_a_rep_and_less_than_the_tenant` |
| A member is scoped to the teams they are effectively on | `Gate8AccountTeamScopeTests.A_member_is_scoped_to_the_teams_they_are_effectively_on` |
| An administrator is tenant-wide at the same rank threshold `PermissionHandler` already uses | `Gate8AccountTeamScopeTests.An_administrator_is_tenant_wide` |
| The account filter never substitutes for the tenant filter | `Gate8AccountTeamScopeTests.The_account_filter_never_reaches_across_a_tenant_boundary` |
| **The dashboard counts a team account's lead that is assigned to nobody** — the clause that makes `managed_scope` a real tier rather than "my people's work" | `Gate8AccountTeamScopeTests.The_dashboard_counts_a_team_account_lead_that_is_assigned_to_nobody` |
| The tenant tier states it is not scoped by team, rather than reporting an empty team list as a scope | `Gate8AccountTeamScopeTests.The_tenant_tier_states_that_it_is_not_scoped_by_team` |
| `KeyAccountTeam` derives from `Customer.AccountTeamId` — the scope that was structurally underivable | `CommercialRouting.RoutingScopeKeyDerivationTests.KeyAccountTeam_derives_from_the_customers_account_team` |
| …and reports UNAVAILABLE per RFQ rather than an empty key that would match every rule | `…KeyAccountTeam_reports_unavailable_when_the_customer_is_in_no_account_team` |
| **One term finds records across several families** — the old keyword router issued no request at all | `Gate8GlobalSearchTests.One_term_finds_records_across_several_families` |
| **A term that matches nothing returns nothing and goes nowhere** — the defect, pinned | `Gate8GlobalSearchTests.A_term_that_matches_nothing_returns_no_hits_rather_than_a_destination` |
| Every hit states which field matched | `Gate8GlobalSearchTests.A_hit_states_which_field_the_term_matched` |
| A customer hit names its account team, or says it has none | `Gate8GlobalSearchTests.A_customer_hit_names_its_account_team_or_states_that_it_has_none` |
| The date filter is half-open and actually excludes | `Gate8GlobalSearchTests.The_date_range_filter_is_half_open_and_actually_excludes` |
| An unconfigured status matches **nothing** and says so — a typo must not silently disable a filter | `Gate8GlobalSearchTests.An_unconfigured_status_matches_nothing_and_says_so` |
| The status filter selects rather than decorating | `Gate8GlobalSearchTests.The_status_filter_selects_rather_than_decorating` |
| A family with no status concept is excluded **and named** | `Gate8GlobalSearchTests.A_family_with_no_status_is_excluded_and_the_gap_is_stated` |
| Search never crosses a tenant boundary | `Gate8GlobalSearchTests.Search_never_crosses_a_tenant_boundary` |
| **Quick search applies the same account scope as the customer list** — it is not a side door | `Gate8GlobalSearchTests.Quick_search_applies_the_same_account_scope_as_the_customer_list` |
| A family the caller may not read is reported, not dropped | `Gate8GlobalSearchTests.A_family_the_caller_may_not_read_is_reported_rather_than_dropped` |
| A truncated family is named so a count is not read as a total | `Gate8GlobalSearchTests.A_truncated_family_is_named_so_a_count_is_not_mistaken_for_a_total` |
| A term below the minimum length is refused | `Gate8GlobalSearchTests.A_term_shorter_than_the_minimum_is_refused` |
| The dashboard controller passes the resolved tier through unchanged, for all three tiers | `DashboardRelease01Tests.Release01Endpoint_DerivesRoleScopeAndTenantOnlyFromClaims` |


---

### Alignment with the upgraded claim ledger

Mid-gate the SLA owner replaced `ReleaseEventClaimAsync`'s delete with a settle model — `CLAIMED`,
`SENT`, `UNCERTAIN`, `RELEASED`, where only `RELEASED` frees the dedup key — and changed
`ISlaNotifications` to answer with a three-state `SlaSendResult` rather than a bool. Report delivery
was changed to match rather than kept on the bool, because the reasoning applies with more force
here: a bool can only say "nothing threw", so a connection dropped after the body was accepted reads
as a failure and the report is sent again. A duplicate escalation is bad; a duplicate board report
in a director's inbox is worse, and neither is recoverable. `ReportDelivery` therefore returns
`Uncertain` when the transport was entered and produced no acceptance evidence, and the worker keeps
the claim, records the run as failed and says so on the screen — it does not retry.

---

## 5a. Real test numbers

Whole solution, `dotnet test ERP_RFQ_Automation.sln`, measured before starting and again at the end.
The tree was being edited by several agents throughout, so the delta is attributed rather than
claimed.

| | Total | Passed | Failed |
|---|---|---|---|
| Baseline at the start of this gate | 4047 | 3534 | 513 |
| After this gate | 4180 | 3649 | 531 |

Grouped so the awaited-migration failures are separable:

- **Awaited-migration lane** (`*PostgreSql*` and `HttpIntegration` classes): **470 failed, before and
  after — unchanged.** The whole lane fails at fixture init on
  `PendingModelChangesWarning: the model has pending changes`, plus
  `42703: column "TaxRegistrationNumber" of relation "BusinessUnits" does not exist`. Both belong to
  the migration owner. This gate adds to that debt (§4) and did not touch it.
- **`TenantIsolationTests.Every_tenant_owned_table_is_covered_by_row_level_security`** — 1 failure,
  red before and after, and it is the integration owner's. **No opt-out entry was added**; this gate
  adds an eleventh table to what it is reporting.
- **This gate's own tests: 46 of 46 pass** (`Gate8GrossMarginTests` 14,
  `Gate8ScheduledReportingTests` 20, `Gate8FollowUpTriggerTests` 12).
- **+133 tests, +115 passing, +18 failing.** All 18 new failures are in five classes whose
  production files are being edited by other agents right now, and none is in a dashboard,
  reporting, follow-up, margin, FX or tenancy path:
  `CommercialFinanceTests` (13 — a new "confirm the delivery before invoicing it" gate added in
  `CommercialFinanceApplicationService.cs`), `ReceivablesOperationsTests` (3 — a `DunningCase`
  change-tracker conflict in `ReceivablesOperationsService.cs`),
  `CommercialExceptionApplicationServiceTests` (1 — `complete` vs `Partial`),
  `SubscriptionRevenueControlTests` (1 — `Void` vs `Finalized` in
  `SubscriptionRevenueControlService.cs`), and `ExtractionWorkerLeaseTests` (1 — a
  `TimeoutException` with five test runs executing concurrently on this machine).

### Second wave — measured numbers

Whole solution, `dotnet test ERP_RFQ_Automation.sln`, measured against the first wave's own final
run as the baseline. Several other agents were editing and testing the tree throughout, so the
comparison is stated per lane rather than as one delta.

| | Total | Passed | Failed |
|---|---|---|---|
| First wave, end of gate | 4180 | 3649 | 531 |
| **Second wave, end of gate** | **4242** | **3727** | **515** |

Grouped so the awaited-migration failures are separable:

- **Awaited-migration lane** (`*PostgreSql*` and `HttpIntegration` classes): **462 failed.** The
  whole lane fails at fixture init on `PendingModelChangesWarning: the model has pending changes`.
  **This gate adds to that debt** (§4.4 puts five columns, two foreign keys, four indexes and three
  CHECK constraints into the EF model with no migration) and did not touch it. Three of the 462 are
  new since the first wave and are **Gate 6's** — `Gate6ReorderAlertPostgreSqlTests`.
- **Everything else: 53 failures, and NONE of them is new.** Every one is present in the first
  wave's own baseline run: `EvidenceRetentionPurgeTests` (24),
  `OpportunityPrioritiesAuthenticatedHttpTests` (11),
  `TenantLifecyclePlatformTableClassificationTests` (4), `FinanceOutboxDispatcherTests` (2),
  `LandedCostInputTaxTests` (2), `AgentSpendCapCurrencyTests` (2), and nine singletons including
  the `TenantIsolationTests` RLS assertion that belongs to the integration owner. **No opt-out
  entry was added to it**; §4.4 adds no new table, so it adds nothing to what that test reports.
- **This wave's own tests: 38 of 38 pass** (`Gate8CustomerMasterTests` 11,
  `Gate8AccountTeamScopeTests` 13, `Gate8GlobalSearchTests` 14). **All 84 `Gate8*` tests across
  both waves pass, 0 fail.**
- **Diff versus the first wave's baseline, non-awaited lane: zero new failures.** The 18 failures
  the first wave attributed to other agents' in-flight edits have since been resolved by those
  agents.

Two adjacent test files were updated rather than left red, and both changes are substantive rather
than cosmetic: `DashboardRelease01Tests` now asserts **three** tiers instead of two (its old theory
could not express the middle one), and `CustomerContactAuthorizationTests` now issues a **numeric**
user id and a role id in its fixture, because FR-CST-02 refuses a request it cannot scope and the
old literal `"test-user"` is not a claim any real token carries.

Builds: backend solution clean, 0 errors. Frontend `tsc --noEmit` reports 8 errors, **all of them in
`Frontend/src/pages/Procurement/RFQs/ProcessRFQPage.tsx`**, which is unmodified from `HEAD` — as is
`rfqService.ts`, the file defining the types it fails against. They are pre-existing and are not from
this gate. Every file this gate touched or added typechecks clean.

---

## 6. Also fixed in this gate

**The brand-demand analytics page has been permanently empty.** `dashboardService.getBrandDemand`
called `/api/analytics/brand-demand`; the controller route is `/api/brand-demand` and no proxy
rewrites it. Because the service maps 404 to `null` by design, every request produced the page's
honest-looking "not available yet" empty state instead of an error — a silent fallback hiding
exactly the gap it was written to surface. Route corrected.

**Three SLA policy knobs had no way to be set.** `SupplierShipDateReminderDays`,
`SupplierAckEscalationHours` and the new `QuoteDecisionReminderDays` were on the API contract and
absent from the Setup screen — wiring-contract failure #5. All three now render under
*Deadlines & Alerts*.

---

## 7. Deliberately not done, and why

- **FR-SBF-01(e), invoice payment-due reminders** — blocked on register **D7**, open and owned by
  Finance. See §3.
- **FR-DSH-04 global quick search** — ~~no cross-entity search endpoint exists~~ **built in the
  second wave; see §8 for exactly what is short.**
- **FR-CST-01/02** — ~~customer master schema and account-team authorization~~ **built in the second
  wave; see §8.** Closing FR-CST-02 is what unblocked FR-DSH-05's account tier, as predicted here.
- **FR-CST-04** — customer price agreements / frame contracts, and customer-side Incoterms and
  payment terms. Still not built; it is a separate entity, not a column on `Customer`.
- **Outbound shipment ETA sweeps** — Gate 7's surface.
- **The `TenantIsolationTests` RLS failure** — belongs to the integration owner. This gate adds an
  eleventh tenant table to that list (§4.1) and **no opt-out entry was added**.

---

## 8. Second wave — what was built, and what is short

### FR-CST-01 — customer master

**Built:** all five fields, every layer. Entity + EF configuration (§4.4), canonicalising validators
on every write path (not only the controller), model-binding attributes at the API edge, DTOs on
request and response, the customer form, the customer list grid and the customer detail screen — and
the master-data audit interceptor covers them with **no change to the interceptor**, because
`MasterDataEntityDescriptor` derives its audited column list from EF metadata rather than a
hand-written include-list.

**Deliberate reuse:** the VAT number is validated by `Tax/TaxRegistrationNumbers`, the same type
`Supplier` and `BusinessUnit` use. There is no second KSA rule in the codebase, and
`Gate8CustomerMasterTests.The_customer_vat_number_is_validated_by_the_same_definition_as_the_supplier_one`
drives the customer write path and asserts the **shared** validator's own message, so a divergent
copy would fail it.

**Short:**

- **The columns are model-only until §4.4 is migrated.** Green on the portable lane, absent on
  PostgreSQL. This is the same debt the wiring contract already tracks and it is why FR-CST-01 is
  not claimed closed.
- **The CR number's "claimed Saudi" test is length-based, and that is weaker than the VAT one.**
  A Saudi VAT number is marked by a leading `3`, so a foreign value is never swept into the Saudi
  rule by accident. A CR number has no such marker, so the only mechanical test available is
  "all digits ⇒ must be 10 digits", and a foreign registration that happens to be all digits and
  not ten long is refused until it is entered with a country prefix. The error message says exactly
  that. A cleaner rule would need a country discriminator on the field, which is a product decision.
- **No downstream consumer yet requires the VAT number.** ZATCA invoicing is sequenced last under
  R1, so `Customer.TaxRegistrationNumber` is currently read by the search index and the screens but
  not by an invoice. That is recorded as owed rather than claimed.
- **No bulk import path writes these fields** — there is no customer spreadsheet importer to extend.
  If one is added it must call `NormalizeAndValidate`, which is why the canonicalisation lives in
  the repository rather than in the controller.

### FR-CST-02 — account team

**The model.** `Customer.AccountTeamId` → `Teams.ID`. It is a **relationship, not a string**, because
the question the requirement asks — *"is this customer mine"* — has to be answerable inside a SQL
predicate. The other half of the answer already existed: `SalesTeamMembership` maps users to teams
and is effective-dated. This column supplies the missing customers-to-teams edge, so the join
**customer → team → membership → user** closes and the whole predicate pushes into the database.

`RoutingScopeKeys.KeyAccountTeam` was documented as *structurally underivable* precisely because that
edge was missing — the only path ran customer → ownership → user → team, which is circular for
routing. It now derives from this column, and that is what unblocked FR-DSH-05's account tier.

**How the read path depends on it.** `Authorization/AccountTeamReadFilter.InAccountScope` is the one
definition of the rule. Four production call sites depend on it — the customer list, the customer
detail read, the quick-search customer family and the release-01 dashboard — so **deleting the
column does not degrade the read path, it breaks the compile**. The dashboard in particular does not
re-derive ownership from `CustomerOwnership` or from `Lead.AssignTo` alone: a supervisor's figures
include leads on their teams' accounts *that are assigned to nobody*, which is only expressible
through `AccountTeamId`, and `The_dashboard_counts_a_team_account_lead_that_is_assigned_to_nobody`
is the test that pins it.

**Stated design decisions, not oversights:**

- **NULL account team = readable tenant-wide.** There is no team to restrict such a record to, and
  treating NULL as "invisible" would have made every existing customer vanish on deploy. Assigning a
  team is the act that narrows. Every read surface renders the absence as a stated gap
  ("No account team"), and the grid marks it in the warning colour so the estate's unassigned
  accounts are visible and actionable rather than invisible. **The consequence must be understood:
  until accounts are actually assigned, this control restricts nothing.**
- **An out-of-scope read raises `KeyNotFoundException`, the same as a non-existent record.** A
  distinct 403 would confirm the record's existence to a caller who may not open it.
- **A named owner (`CustomerOwnership` primary/backup, active and effective) keeps their account**
  even when it sits in another team's book, because that assignment is a deliberate act by a manager.

**Short:**

- **Migration, as above.**
- **"…and tasks" is not done.** FR-CST-02 says customer records, dashboards *and tasks*.
  `FollowUpTask` is still filtered by `AssignedToUserId` only. It carries a `CustomerId`, so the
  same filter applies directly; it is not done here because *My Follow-ups* has exactly one producer
  (register E33) and re-scoping a list that is already a single-source quote-chase list would change
  its meaning without improving it. Named, not silently skipped.
- **Manager rank still grants tenant-wide READS elsewhere.** `IRoleGate.IsManagerOrAdminAsync` is
  used by other repositories to widen reads; those call sites are untouched. The account tier is
  enforced on the customer surfaces, the dashboard and the search, and nowhere else yet.
- **`Team.SubTeamId` has no cycle constraint in the database.** The expansion is bounded at ten
  levels for that reason; a cycle would otherwise loop inside an authorization decision.
- **Setting the account team is per-customer only.** There is no bulk-assign screen, so putting a
  book of two hundred accounts into a team is two hundred edits. Worth a follow-up.

### FR-DSH-04 — global search

**Built.** `GET /api/search` over eight families with `q`, `entities`, `from`, `to`, `status` and
`limit`; per-family module permissions resolved by the same rule `PermissionHandler` uses;
tenant-scoped; account-scoped on customers. The response reports `searchedEntities`,
`deniedEntities`, `truncated` and `notes`, so a short answer is never mistaken for an empty estate.

**What was deleted:** the ten-keyword router in `Navbar.tsx`, including its
`navigate(match ? match.path : '/dashboard')` fallback. The box now issues a real request, shows
what matched and on which field, and **on no match says "Nothing matches …" and navigates nowhere.**
A failed request renders the server's message — a failure and a genuine "no results" are never
shown as the same thing.

**Short, precisely:**

1. **Matching is unranked substring, not full-text.** `LOWER(col) LIKE '%term%'`, first-match-wins on
   which field is reported, ordered by date within each family. There is no relevance score, no
   stemming and no typo tolerance. `pg_trgm` or a `tsvector` column would fix it and both are schema
   changes (§4.5) that should follow a real query plan, not precede one.
2. **It does not use an index.** Acceptable at pilot volumes; it is a sequential scan.
3. **The status filter spans only families with a `SetupMaster` status row** — leads, quotes, orders
   and shipments. Customers, products, suppliers and RFQs are **excluded when a status is supplied**
   and each says so in `notes`. Suppliers carry governance statuses and RFQs a bidding decision;
   folding those into one "status" vocabulary would be an invented taxonomy.
4. **"Filter across screens" is not built.** The requirement's wording covers a persistent filter
   applied to the screen you are on. What exists is a quick-search panel that finds records and
   opens them. This is the largest remaining gap in FR-DSH-04 and it is named rather than glossed.
5. **The date filter matches each family's own date column** (`Customers.CreatedOn`,
   `Quotes.QuoteDate`, `Orders.OrderDate`, …) because there is no shared one. Every hit states which
   column it was filtered on, so the result is explainable rather than merely plausible.
6. **Undated rows are excluded when a date filter is present** — not kept, and not coalesced to
   today.

### The region master — the seam with Gate 7

**`Customer.RegionStateId` is an FK into `SetState`**, the tenant's existing region master. That is
not an arbitrary choice: `CommercialRouting/RoutingScopeKeys.Territory` **already** resolves sales
territory by matching stated wording against `set_states` codes and names and `set_cities` names, so
`SetState` is the governed regional vocabulary this platform has today, and pointing the customer's
region at anything else would create a second regional master on day one.

**Gate 7's delivery-address region list had not landed when this was written** — there is no Gate 7
progress document in `docs/` and no new region entity in the tree. So the seam is stated rather than
guessed at:

- If Gate 7's governed list **is** `SetState` (extended), nothing changes: this column already points
  at it and `ValidateTenantReferencesAsync` already refuses an inactive or cross-tenant row.
- If Gate 7 introduces a **new** region entity, the migration is one column swap on `Customers`
  plus a data move from `RegionStateId` to the new key, and the only two readers to update are
  `CustomerRepository` (write validation and the `RegionName` projection) and
  `RoutingScopeKeys.Territory`. **The new entity must be the one routing reads too** — if Gate 7's
  list and `set_states` both survive, routing and the customer master will disagree about what a
  region is, and the disagreement will only show up as ownership rules that quietly stop matching.

Coordination is owed on that second case before Gate 7 closes.

### FR-DSH-05 — role-scoped dashboards

**Built.** Three tiers, resolved from real data: `assigned_accounts` (a rep's own teams),
`managed_scope` (teams managed, plus their sub-teams, plus their members) and `tenant`
(rank ≥ `RoleRanks.Admin`, the same threshold at which `PermissionHandler` already satisfies module
permissions — deliberately not a second answer to "what may this caller read"). `GET
/api/dashboard/release-01` reads through it and the payload carries the tier, the team ids and the
user ids, so a reader can see whose numbers they are looking at. The dashboard chip renders the tier
in words instead of the raw token.

**Short:**

- **Only `release-01` is scoped.** `deadline-board`, `document-yield`, `pipeline-analytics`,
  `team-workload` and the gross-margin panel still read tenant-wide. Each is a one-line change to
  apply the same filter, but each also needs its denominator reviewed — a win rate over a scoped
  numerator and a tenant-wide denominator is worse than an unscoped figure — so they are named here
  rather than converted quietly.
- **A rep on no team, with no ownership rows, sees only unassigned customers.** That is the
  fail-closed direction and it is correct, but it will look like an empty dashboard until team
  membership is populated. `SalesTeamMembership` has no maintenance screen in this build.
- **The tier is derived from rank plus membership, not from a named "executive" role.** The BRD says
  "company-wide KPIs for executives"; this maps executives onto rank ≥ Admin, which is the only
  authority signal the platform has.

