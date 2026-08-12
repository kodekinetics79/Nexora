# Gate 2 — supplier tiers and one governed weighted comparison

**FR-QTM-01, FR-QTM-03, FR-MDM-03 (first clause).** Same bar as every gate: persistence, domain
behaviour, UI wiring, tenant isolation, audit evidence, automated tests and a real rendered-browser
path. Anything short is reported short, with the missing layer named.

| Req | Entering | Now | Remaining |
|---|---|---|---|
| FR-QTM-01 · dispatch to Tier 1/2/3 suppliers | MISSING | **CLOSED** | — |
| FR-QTM-03 · configurable weighted scoring, four criteria | CONFLICTING | **CLOSED** | Warranty scores only on lines where a number has been captured; existing lines have none. Stated on screen, not hidden |
| FR-MDM-03 · classify vendors Tier 1/2/3 | MISSING | **CLOSED** | Second clause (manufacturer authorisations) OUT under ratified R9 |

---

## 0. The correction the gate turned on

The decision register says at **E12** that supplier weighted scoring *does not exist*. **It did.**

`Agent/Sourcing/SupplierScoring.cs` shipped a min-max normalised weighted scorer with hardcoded
weights — price 0.50, lead time 0.25, success rate 0.25 — driving the AI award tools. Meanwhile the
recommendation a human actually awarded from was a **separate hardcoded sort** in
`ProcurementApplicationService` (coverage → landed cost → lead time).

**Two recommenders on one RFQ line, able to name different winners.** So this gate was unification,
not invention. One scorer in `SupplierEvaluation/`, weights read from a tenant row the customer edits,
both callers moved onto it, the old one deleted.

The register is wrong on this point and should be corrected rather than left to mislead the next
reader.

---

## 1. What closed

**Supplier tier.** `Supplier.Tier` — `TIER_1_PARTNER` / `TIER_2_EXTENDED` / `TIER_3_OUT_OF_NETWORK`,
CHECK-constrained, null meaning *not yet classified*. Customer-set on the existing supplier form,
audited before/after through the interceptor that derives its field list from EF metadata, carried
through bulk import and export, and shown on the supplier grid, the detail page and the comparison.

**It gates nothing.** Dispatch eligibility stays with the existing governance/verification/compliance/
risk/readiness check. Tier orders the dispatch picker, labels each candidate and pre-selects Tiers 1
and 2 with Tier 3 one visible click away. The domain reason is recorded because it will be argued
again: a tier that blocks dispatch becomes a tier everyone is promoted past within a month, and Tier 3
spot suppliers are exactly who a Saudi trader calls for obsolete spares and single-source lines.

**Tier is not governance.** Supplier already carried five compliance-shaped statuses. A compliant
brand-new supplier is legitimately Tier 3; a Tier 1 partner whose CR lapsed is legitimately blocked.
Nothing syncs the two axes in either direction, and nothing derives a tier from spend.

**Weighted scoring.** `SupplierComparisonWeights` — four weights totalling 100, one row per business
unit, copied file-for-file from the `CommercialMatchingPolicy` pattern: create-on-demand, **mandatory
reason**, Idempotency-Key replay checked against `TenantGovernanceAuditEvents`, before/after snapshot,
`Version++` under Serializable. Edited as a section on the **existing** Setup → Commercial Policy page.
No new route, no new screen.

**The score annotates; the human awards.** It never changes which offers are awardable — eligibility
stays entirely with `Eligible`/`Blockers` — and it sits *beside* the lowest-landed-cost fact rather
than replacing it.

**Reliability was removed from scoring.** It is an operator-typed spreadsheet column, never a measured
outcome, and it was carrying 25% of an AI award recommendation while presenting as performance
evidence. It remains displayed.

---

## 2. The defaults, and the defect that shaped them

The Integration Owner's contract originally set defaults of **70 / 20 / 10 / 0**. That was wrong in a
way that would have shipped the feature visibly broken.

`CreditDays` is introduced *by this gate*, so it is null on every supplier that already exists. Under
the rule that a weighted criterion with no value is **never scored as zero**, a payment-terms weight of
10 would have produced *"Cannot score — payment terms missing"* on **every row of every comparison**
until somebody hand-filled credit days across the whole supplier master.

The build agent stopped and reported it rather than building around it.

Defaults are now **Price 80 / Lead time 20 / Warranty 0 / Payment terms 0**, and the rule is written
down: **a criterion may only carry default weight if its source data exists on day one.** Landed cost
and lead time are on every quote already. The Setup screen warns, naming the consequence, when either
zero-weighted criterion is given weight before its data is captured.

---

## 3. Warranty — the fourth criterion, and why it is a number

FR-QTM-03 names four criteria. Warranty was free text while the scorer consumed a number nothing
supplied, so the fourth criterion was **permanently inert**. Shipping three of four and calling the
fourth a "stated gap" was leaving the requirement unmet and describing it politely.

`SupplierQuoteLine.WarrantyMonths` is **typed, never parsed**. No regex, no classifier, no model reads
the wording. An operator types the months or there is no number. The free text is untouched and
neither field derives from the other — a warranty clause carries conditions no integer can hold, and
rewriting it to fit a score would trade a true statement for a partial one.

Existing rows take `NULL`, never a backfilled `0`: `NULL` means nobody captured it, `0` asserts the
supplier offered no warranty. Bounded `0..600` months in the database, the domain and the UI — the
ceiling is what stops a mistyped year becoming the longest warranty in the candidate set.

**Recorded divergence.** `RecommendAwardTool` still does not score warranty at any weight. It ranks
bids recorded on the *RFQ line*, and an RFQ line carries no warranty period. Rather than guess, it
declines to rank when warranty carries weight, and its tool description says so. Closing it properly
means moving that tool off RFQ-line bids onto canonical supplier quotes — a different change, not
smuggled into this gate. `CompareSupplierQuotesTool` **does** score the real value.

---

## 4. Defects found and fixed on the way

| Defect | Why it mattered |
|---|---|
| `RecommendAwardTool` turned a missing lead time into `0` days | Min-max reads 0 as the *fastest* offer, so a supplier who never stated a delivery date won the full lead-time weight |
| The comparison table rendered offers in raw API order and never applied the ranking | Changing a weight would visibly do nothing — the whole gate, invisible |
| A blocked offer displayed *"This offer can still be awarded"* | In the same row that refused it |
| A lone offer scored a perfect **100/100** | With one candidate, min == max on every criterion — full marks for being compared against nothing |
| Unbounded `stackalloc` on the tier field | Reachable from model binding with a 4 MB form limit: an uncatchable `StackOverflowException` that kills the worker process |
| `TaxRegistrationNumber` was null on **every** list response | Set in the detail mapper, never in the separate list projection. Pre-existing; fixed with a regression guard |
| A tie comparator subtracted two absent scores, producing `NaN` | A JS sort reads `NaN` as "already ordered", so the fallback ordering was unreachable and silent |
| Migration index named `"Buid"`; the column is `"BUID"` | Quoted identifiers are case-sensitive: `42703` at deploy, invisible to every portable test |
| New tenant table had no `nexora_purge_app` grant or purge policy | `TenantPurgeExecutor` asserts reach across every tenant table before deleting, so **one unreachable table refuses offboarding for every tenant** |
| The golden seeder created no governed platform tenant | ~35 `[RequiresEntitlement]` routes answered 403; `phase1-base-journey.spec.ts` was **red on `main`** |
| Seeder resolved the tenant unordered; enforcement uses `OrderBy(Id)` | With two rows it attaches the plan to one and logs success while enforcement reads the other — diagnosis starting from a false log line |
| `run-phase1-base-journey.sh` set no `Observability__Prometheus__ScrapeKey` | The backend refuses to boot; the harness was unusable from a clean checkout |

**The cross-tenant test that passed for the wrong reason.** Tenant B in the golden scenario had no
entitlements, so the cross-tenant refusal test went green *because the tenant was unentitled*, not
because isolation held. It would have kept passing if isolation broke entirely. Governing tenant B
converts it from decoration into a control.

---

## 5. Evidence

| Check | Result |
|---|---|
| Portable lane | **4127 passed / 0 failed** (baseline entering the gate: 4033) |
| PostgreSQL lane | **570 passed / 0 failed** |
| Model drift | clean |
| Build | 0 errors, **358 warnings — exactly baseline, none new** |
| Frontend | **685 tests**, lint and build clean, bundle within budget |
| `phase1-base-journey.spec.ts` | **2/2 green** (was 1 failed on `main`) |

**Browser, against a running stack** (PostgreSQL 16, migrations applied from empty, real HTTP):

- Setup → Commercial Policy renders the weights section on the existing page: defaults 80/20/0/0,
  `Total 100 of 100`, three presets plus Custom, mandatory reason, and an honest empty state.
- Weighting payment terms before Credit days exists produces both guards: `Total 110 of 100` and a
  warning naming the consequence.
- **The rank changes, and a person can see it.** Same three offers, weights changed *in the browser*:
  order went `Cheap Slow (80) → Mid (48.57) → Fast Premium (20)` to
  `Fast Premium (60) → Mid (45.71) → Cheap Slow (40)`. The spec reads supplier names and scores out of
  the rendered DOM, so a table painting offers in API order could not pass.
- The winner's chip is honest about the trade: *"Best weighted score 60 — $30.00 more than the
  cheapest, 35 days faster"*, and the demoted offer keeps its *"Lowest landed cost"* chip.
- Each row visibly sums: `price $130.00 · 0 of 40` + `lead time 10 days · 60 of 60` + … =
  **Total 60 of 100**.
- A missing weighted value reads *"Cannot score — warranty missing"* with **Approve still enabled**.
- A **Tier 3 out-of-network** supplier has **Approve enabled** — tier annotates and gates nothing.

Cross-tenant, at runtime: tenant A sees its saved 40/60; tenant B sees **its own** 80/20 defaults and
zero of A's suppliers.

---

## 6. What is NOT closed, stated plainly

1. **`RecommendAwardTool` does not score warranty** at any weight — see §3. Documented in the code,
   not hidden. Closing it means re-basing that tool on canonical supplier quotes.
2. **A warranty number exists only where someone types it.** Every pre-existing supplier quote line
   has `NULL`, so a tenant that weights warranty today will see *"Cannot score"* on historical lines.
   That is the correct behaviour, not a defect — but it is not "warranty scoring works everywhere".
3. **No device verification** of the new UI. Reflow follows the existing MUI conventions; nothing was
   measured on a real device.

---

## 7. Owed to other gates, found while here

- **An offline or phone quote cannot be recorded on the buyer workbench.**
  `POST /api/procurement/supplier-quotes` refuses with *"A supplier response requires a sent
  solicitation with delivery evidence"*, and solicitations only reach `Sent` when outbound email is
  configured **and delivers**. A buyer who takes a price over the phone is blocked by an email gate on
  a fact they keyed in by hand. The Supplier Quote Inbox route writes the same records with no email —
  so the two capture paths disagree about what a supplier response requires, and the workbench is the
  one a buyer is shown.
- **`POST /api/Product` still requires a client-supplied `CreatedBy`.** The supplier and currency
  endpoints deliberately removed that in favour of deriving the actor from the token. A client-supplied
  actor field is forgeable attribution.
- **EF writes the model snapshot to the superseded `Migrations/` folder**, not the live
  `MigrationsBaseline/` one. It happened on both migrations in this gate. The result is a green build
  against a model the compiled code does not know about — the exact shape of *"the migration I shipped
  four hours ago never ran, and my test said it was fine"*. Until the folder rename described in the
  `.csproj` happens, **every migration author must move the snapshot by hand**. This belongs in the
  migration runbook.

---

## 8. Definition of done

> Gate 2 is closed for these two capabilities when a master-data administrator can set a supplier's
> Tier 1/2/3 value in the browser and that change is persisted, tenant-isolated and written to a
> visible before/after audit record; when a sales engineer can filter supplier-RFQ dispatch by that
> tier in the browser; and when the supplier-quote comparison for an RFQ line displays a per-criterion
> weighted score computed from a tenant-configurable weight set that a customer edited through the UI
> with a recorded reason.

**Met**, with §6 stated against it rather than around it.
