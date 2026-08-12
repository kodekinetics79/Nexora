# Gate 2 — shared contract: supplier tiers and weighted scoring

**Integration Owner authored. Binding on every implementation agent.** Do not deviate. If you believe
a clause is wrong, stop and report it — do not build around it.

Scope: **FR-QTM-01** (tier-targeted dispatch), **FR-QTM-03** (configurable weighted scoring),
**FR-MDM-03 first clause only** (tier classification). Register items **E12** and **E13**.

---

## 0. The correction that reshaped this contract

The register says at E12 "supplier weighted scoring does not exist". **That is false.**

`Agent/Sourcing/SupplierScoring.cs:33-98` already implements a min-max normalised multi-criteria
weighted scorer with fixed weights (`WeightPrice = 0.5`, `WeightLeadTime = 0.25`,
`WeightSuccessRate = 0.25`) and a fail-closed single-currency gate (`EnsureOneCurrency`, lines 82-98)
that throws rather than rank across currencies. Consumers: `Agent/Tools/SourcingTools.cs:279,320` and
`Agent/Tools/RecommendAwardTool.cs:147,169`.

Meanwhile the **authoritative** recommendation — the one a human sees and awards from — is a
hardcoded sort at `Procurement/ProcurementApplicationService.cs:1218-1222`
(coverage → landed cost → lead time).

**Two recommenders exist and can name different winners on the same line.** AGENTS.md forbids
divergent state models. So this gate is *unification*, not invention: one scorer, governed weights,
both callers on it. That makes the work smaller and the outcome simpler for the operator.

---

## 1. Non-negotiable rulings

These resolve E12 and E13. They are the answers; do not re-litigate them in code.

| # | Ruling |
|---|---|
| R-A | **Tier annotates, orders and pre-selects. It NEVER gates.** Dispatch eligibility stays governed solely by the existing check at `SupplierQuotes/SupplierNegotiationService.cs:568-578`. Tier enters no blocker list, no eligibility predicate, no award guard. |
| R-B | **Tier is customer-set. Nothing derives it.** No spend bands, no auto-promotion, no sync with `GovernanceStatus`. A compliant brand-new supplier is legitimately Tier 3; a Tier 1 partner with a lapsed CR is legitimately blocked. These are different axes and must stay so. |
| R-C | **Tier never enters the weighted score.** The two capabilities are adjacent, not composable. |
| R-D | **Exactly four weighted criteria**, per FR-QTM-03: price, lead time, quality/warranty, payment terms. Coverage is NOT a weight — it stays the existing full-cover-first tiebreak. No configurable criteria registry. |
| R-E | **`SuccessRate`/`Reliability` is dropped from the score.** FR-QTM-03 does not name it, and it is an operator-typed spreadsheet column (`Services/SupplierUploaderService.cs:161`), never a measured outcome. Presenting it as measured performance breaks the AGENTS.md ban on unexplained scores. It stays a display-only column. **This is a deliberate behaviour change to the Agent tool path.** |
| R-F | **A missing criterion is NEVER scored as zero.** If any weighted criterion has no value for an offer, compute no score for that offer: render `Cannot score — <criterion> missing`, sort it below scored offers, and leave it **fully awardable**. Do not impute, default or interpolate. |
| R-G | **The score annotates; the human awards.** No auto-award, no threshold selection, no background re-scoring. Eligibility stays with `Eligible`/`Blockers`. |
| R-H | **One weight set per business unit.** Four numbers totalling 100. No per-customer, per-category, per-supplier or per-RFQ overrides. No inheritance chain. |
| R-I | **The score's price input is the single existing landed-cost figure, consumed as one number.** Do not decompose it into duty/freight/conformity sub-weights — R8 closed that. |
| R-J | **FR-MDM-03's second clause (brand/manufacturer authorizations) is OUT**, superseded by ratified R9. Build the tier enum only. Record the deviation. |

### Explicitly forbidden builds

No tier-management screen. No tier auto-assignment or recalculation job. No weight-history page.
No what-if simulator. No AI-suggested weights. No supplier scorecard, rolling rating or performance
trend. No payment-terms parser, warranty-text classifier or terms normalisation table. No new
Setup route. No second approval step.

---

## 2. Schema — Integration Owner owns the migration. Do NOT author one.

Add to `Models/Supplier.cs`, beside the existing governance columns at lines 41-49:

```csharp
public string? Tier { get; set; }          // null = not yet classified
public int? CreditDays { get; set; }       // null = NOT CONFIGURED, not zero
```

- New constant class `SupplierTiers` beside `SupplierGovernanceStatuses` (`Models/Supplier.cs:91-140`):
  `TIER_1_PARTNER`, `TIER_2_EXTENDED`, `TIER_3_OUT_OF_NETWORK`.
- CHECK constraint on `Tier` in the entity configuration — pattern at
  `Models/ErpRfqAutomationContext.InboundLogistics.cs:45-46`.
- `CreditDays` is the numeric companion to the existing free-text `Supplier.PaymentTerms` (line 19).
  The customer types `30`; the free text keeps the human wording. **This is the one-setting doctrine:
  a number a person types beats a number a model guessed.**
- No RLS policy, no GRANT, no new negative test — these are columns on an existing tenant table that
  already carries the query filter and composite key.

New weights row — **copy the `CommercialMatchingPolicy` file set one-for-one**, it is the only
end-to-end template in the repo that already satisfies "a setting is only real if it has a UI and an
audit trail":

| Copy from | To |
|---|---|
| `OrderToCash/CommercialMatchingPolicy.cs:27-123` (`DefaultFor`, nullable = not configured, one row per `BusinessUnitId`) | the weights entity |
| `OrderToCash/CommercialMatchingPolicyService.cs:93-175` (create-on-demand, **mandatory `Reason`**, Idempotency-Key replay against `TenantGovernanceAuditEvents`, before/after snapshot, `Version++` under Serializable) | the weights service |

Fields: `PriceWeight`, `LeadTimeWeight`, `WarrantyWeight`, `PaymentTermsWeight` (int, must total 100),
plus `Version`. **Defaults: Price 80 / Lead time 20 / Warranty 0 / Payment terms 0.**

> **Correction, recorded rather than quietly fixed.** This clause originally read
> *Price 70 / Lead time 20 / Payment terms 10 / Warranty 0*, and it was wrong in a way that would have
> shipped the feature broken. The comparison agent caught it and stopped rather than building around it.
>
> `CreditDays` is introduced *by this gate*, so it is null on every supplier that already exists. Under
> ruling R-F a weighted criterion with no value is never scored as zero — so a payment-terms weight of
> 10 would have produced **"Cannot score — payment terms missing" on every row of every comparison**
> until somebody hand-filled credit days across the entire supplier master. The customer's first
> encounter with the feature would have been a screen that refuses to rank anything.
>
> The rule the original clause missed, now explicit: **a criterion may only carry default weight if its
> source data exists on day one.** Landed cost and lead time are on every quote already; warranty is
> free text; credit days is brand new. So the two that work carry the whole score, and the two that
> need capture start at zero and are raised by the customer when the data is there. The Setup screen
> warns on both when they are given weight, naming the consequence.

This is what makes the default set *equivalent in coverage to today's behaviour* — every offer that
ranks today still ranks — while making the ordering configurable, which is the actual requirement.

---

## 3. The scorer — one implementation

1. Lift `ScoreInPlace` and `EnsureOneCurrency` out of `Agent/Sourcing/SupplierScoring.cs` into a
   governed namespace `SupplierEvaluation/`. The governed comparison path must not depend on
   `Agent/Tools/`, which E37 marks BRD-excluded and freezable.
2. **Keep `EnsureOneCurrency` verbatim.** Ranking `900 EUR` above `1,000 USD` as bare decimals is a
   wrong award wearing the authority of a recommendation. It fails closed today; it must keep failing closed.
3. Parameterise the weights — they arrive from the governed weights row, not from constants.
4. Both callers move onto it: `Agent/Tools/SourcingTools.cs` + `RecommendAwardTool.cs`, and
   `Procurement/ProcurementApplicationService.CompareQuotesAsync`.
5. Normalisation stays min-max within the candidate set. No z-scores, log scaling or utility curves.

---

## 4. Comparison contract — restore what it already drops

`Procurement/ProcurementContracts.cs:469-489` `QuoteComparisonLine` carries no supplier name,
manufacturer, part number, `IsAlternate`, origin, warranty or payment terms — **yet `ToComparisonLine`
(`ProcurementApplicationService.cs:2516-2549`) already receives the `Supplier` and the
`SupplierQuoteRevision` that hold them.** They are dropped at the moment they are in scope.

Add as optional trailing parameters (precedent: `CostWarnings = null` at line 489) and populate from
arguments already bound. **No new query, no new join.** Add at minimum: `WeightedScore` (nullable),
`ScoreBreakdown`, `ScoreUnavailableReason`, `SupplierTier`, `Warranty`, `PaymentTerms`, `CreditDays`.

Then change **only** these two statements: the display sort at line 1209 and the recommendation at
lines 1218-1222. Coverage stays the first tiebreak.

---

## 5. UI — zero new screens

| Surface | Change |
|---|---|
| `Frontend/src/pages/Setup/` Commercial Policy page | Add one section, "Supplier comparison", below "Customer PO tolerances". Widen the subtitle to name supplier recommendation. Three preset radios — **Cheapest wins** (100/0/0, reproduces today's behaviour exactly), **Balanced** (70/20/10/0, default), **Speed matters** (40/50/10/0) — plus manual entry, with a running total that must reach 100. No new route, no new page, no new audit table. |
| `Frontend/src/pages/Suppliers/SupplierFormDialog.tsx` | Tier select + Credit days number field in the Basic Information block. **Tier is master data, not governance** — do not put it in the Governance Review dialog, where an operator would read it as a compliance axis. |
| `Frontend/src/pages/Suppliers/SupplierDetailPage.tsx` | Mount the existing `components/common/ChangeHistoryPanel.tsx` with `entityType="Supplier"`. The endpoint (`Controllers/SupplierController.cs:33-52`) and the service route already exist; only Customers and Products mount it. **Without this the tier field has no visible audit trail and does not meet the owner's test.** |
| `SourcingWorkbenchPage.tsx` comparison table | Score column and per-criterion contribution columns. |

**Explainability is not optional** (AGENTS.md: no unexplained scores). In the row, never behind a
hover or drawer: the score labelled with its ceiling (`Weighted 82/100`, never a bare `82`); rank
within the line (`1 of 4`); each scored criterion's raw value *and* the points it earned, so the row
visibly sums to the score.

The chip sits **beside** the existing landed-cost fact, never replacing it:
- winner is also cheapest → `Best weighted score 82 — also the lowest landed cost`
- winner is not cheapest → `Best weighted score 82 — SAR 1,240 more than the cheapest, 12 days faster`,
  and the plain `Lowest landed cost` chip stays on the cheap offer.

A verifiable claim must not be replaced by an unverifiable one.

---

## 6. The five-site checklist for a new Supplier field

`SupplierResponseDTO` has two independent mapping sites and one has **already silently drifted**:
`TaxRegistrationNumber` is set in `Controllers/SupplierController.cs:301` but never assigned in the
separate projection at `Repositories/SupplierRepository.cs:88-127`, so it is **null on every list
response today**. Fix that omission in the same edit — it is one line, and it removes the trap.

Every new field must land in all five: DTOs (`DTOs/SupplierDTOs/`), controller Create
(`SupplierController.cs:139-162`), controller Update (`199-214`), `MapToResponse` (`278-310`),
**and** the repository projection (`SupplierRepository.cs:88-127`).

Plus, or the field is silently dropped: uploader headers (`Services/SupplierUploaderService.cs:35-48`),
the parse switch (line 157), export (`212-237`), and the grid column list
(`SuppliersPage.tsx:327-353` — ordering and visibility come free from the existing preferences hook).

---

## 7. Audit — free, if you do not touch it

`MasterData/MasterDataEntityDescriptor.cs:13-17` derives the audited field list from EF metadata
rather than a declared include-list, and `SupplierDescriptor` already exists (lines 73-80). **Add the
columns and change nothing in `MasterData/`.** Tier is neither COMMERCIAL nor PERSONAL; null
classification is the correct outcome.

Do **not** retrofit before/after auditing across the wider controller surface. That is E44, it is
open, and it is the owner's decision.

---

## 8. Out of bounds — touch none of these

E9/R5 (no LOA approver or `ApprovedMargin` under a scoring label) · E10 price tolerance, Gate 3 ·
E27/R8 (no duty/freight/conformity on the customer PDF) · FR-COM win/loss classification ·
FR-PQH-03 prior-purchase success as a fifth criterion · FR-DSH-02 supplier-level aggregate ratings ·
E44 blanket master-data auditing · the duplicated `SupplierRfqBlockingReasons`
(`ProcurementApplicationService.cs:2399` and `SupplierController.cs:463`) — under R-A tier gates
nothing, so touch neither copy.

---

## 9. Definition of done

> Gate 2 is closed for these two capabilities when a master-data administrator can set a supplier's
> Tier 1/2/3 value in the browser and that change is persisted, tenant-isolated and written to a
> visible before/after audit record; when a sales engineer can filter supplier-RFQ dispatch by that
> tier in the browser; and when the supplier-quote comparison for an RFQ line displays a
> per-criterion weighted score computed from a tenant-configurable weight set that a customer edited
> through the UI with a recorded reason.

Tests must prove, not assert:
- changing a weight **changes the rank** — the control fires
- a missing criterion yields **no score and no zero**, and the offer stays awardable
- a mixed-currency candidate set is **refused, not ranked**
- tier changes **do not** change dispatch eligibility
- a cross-tenant read of the weights row returns nothing

A demo against seeded data is not closure.
