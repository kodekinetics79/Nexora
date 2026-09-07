# Billing: target architecture

Design first; implementation follows this note. Written out of the CFO/Finance-Director
panel review of the tenant module (2026-09-07) and sequenced as its own stream, separate
from the tenant-console redesign.

## What is already right, and must not be rebuilt

The metering and statement engine is the strongest thing in the codebase and this
programme builds **around** it, not over it:

- Usage → invoice line is real, from source ledgers, with provenance and honest coverage
  caveats (`BillingStatementService.cs:436-560`, `:1103-1195`).
- Statements: Draft recomputable, Final immutable, `UNIQUE(TenantId, PeriodStart)`, a 48h
  settle lag, and maker ≠ checker on finalise (`:753-762`).
- `usage_events_insert_guard` reconciles overage, allowance and rate-card lineage in the
  database (`Migrations/20260808191301:60-100`); coverage segments and event ratings are
  immutable by trigger.
- Rate cards are versioned and become pin-immutable once a Final statement references them
  (`PlatformBillingController.cs:301-334`).
- Void, refund, payment reversal and write-off all carry genuine maker-checker
  (`SubscriptionRevenueControlService.cs:206`).
- `SubscriptionInvoices.BillingStatementId` is unique — one statement cannot be billed twice.

Everything below is either in front of that engine (price governance) or behind it
(collection, recognition, reporting).

## The five that block a CFO sign-off

| # | Gap | Evidence | Consequence |
|---|-----|----------|-------------|
| 1 | **Nothing bills or chases unless a human remembers** | `SubscriptionDunningWorker.cs:11` (off by default) and `:331-338` (writes a row, sends nothing); `AccountingOutboxDispatcher.cs:12` (off); `BillingRunWorker.cs:228,238` computes drafts and never invoices; the only caller of `CreateDraftAsync` is a human POST (`PlatformSubscriptionInvoicesController.cs:31-50`) | Revenue leaks silently. No GL entry ever leaves the outbox. |
| 2 | **A mid-cycle plan change reprices the whole month** | `TenantsController.ChangePlan:1412-1444` writes `PlanId` with no effective date; `BaseSubscriptionLine` reads `plan.MonthlyPriceUsd` at compute time (`BillingStatementService.cs:1205`); the run recomputes prior Drafts every 6h. Proration exists **only** for `BillingStartsOn` (`:238-288`). Same defect at the catalogue: `UpdatePlan` (`PlatformOperationsController.cs:249-280`) edits price in place for every tenant on that plan. | Upgrade on the 25th and the customer pays the new price for all 31 days. |
| 3 | **No revenue reporting exists at all** | Zero occurrences across `Backend/` and `Frontend/src` of MRR, ARR, churn, net revenue retention, deferred revenue, AR ageing, DSO. `BillingPage.tsx:342-366` shows four tiles, all counts, no currency figure at portfolio level. | The company cannot answer a board question from its own product. |
| 4 | **Multi-currency does not exist, and the KSA position is non-compliant** | `PlatformBillingCurrency.Code = "USD"` enforced at card create, at pin (`PlatformBillingController.cs:609`) and at compute with a 409 (`BillingStatementService.cs:604`). Zero FX code in `Billing/`. Zero occurrences of `zatca` in `Billing/`. Numbering is `NX-{yyyyMM}-{Id:D8}` (`SubscriptionInvoiceService.cs:192`) — identity-derived, so the sequence **gaps** whenever a draft is abandoned. | A company selling ZATCA compliance issues its own Saudi customers plain USD PDFs with a gapping invoice sequence. |
| 5 | **Tax and evidence are hand-typed per invoice** | `SubscriptionInvoicesSection.tsx:360-386` asks the operator to type jurisdiction code, treatment, rate %, seller legal name and seller tax number; the typed jurisdiction code *selects the rule* (`SubscriptionRevenueControlService.cs:80-91`); seller identity is checked against nothing (`:302-305`); the evidence SHA-256 is validated for shape only (`:146`). | A typo silently picks a different tax rule on a document that is then legally immutable. |

Plus one structural break: `SubscriptionPayment.ExternalReference` is **globally unique**
(`ErpRfqAutomationContext.Billing.cs:191`), so one wire settling three invoices cannot be
recorded without fabricating references.

## Target capabilities

Stated as capabilities, not screens. Each is a sign-off condition.

1. **An effective-dated, versioned price book.** Plan and rate-card prices become immutable
   versions with validity windows. Editing a live price is impossible; superseding it is one
   approved act. This is the fix for gap 2 at the catalogue end.
2. **A subscription/contract object** carrying term, committed value, currency, renewal date
   and ramp. Nothing today can answer "what is this customer worth" — `ContractStartOn`/
   `EndOn` are stored and read by exactly one activation gate. MRR, backlog and revenue
   recognition are all computed from this object, so it comes before item 8.
3. **Proration as a first-class engine.** Plan change, cancellation, seat change and
   mid-term upgrade each produce a dated, explained delta line — the `BillingStartsOn`
   logic generalised rather than a second implementation of it.
4. **Effective-dated commercial change with a preview.** No money-affecting save commits
   until the operator sees *"this changes the September invoice from $X to $Y"*. Pairs with
   the two-person approval below.
5. **Automatic invoicing.** A Final, Ready statement becomes a draft invoice with no human
   in the loop, and an unbilled Final statement past the settle lag is an alert with an owner.
6. **Real collections.** A dunning ladder that sends on a schedule, escalates, records
   delivery evidence, and is **on by default in production**.
7. **Cash application from a bank feed or gateway**, allowing one receipt to settle many
   invoices and part-payments. Requires relaxing the global uniqueness on
   `ExternalReference` to `(TenantId, ExternalReference)` or a receipt/allocation split.
8. **A revenue ledger**: recognised, deferred and unbilled balances by period, with an MRR
   bridge (new / expansion / contraction / churn), NRR, AR ageing, DSO and cash collected.
9. **Server-derived tax.** Jurisdiction and treatment resolve from the tenant's country and
   the approved rule set with no operator typing; the evidence hash is computed, never entered.
10. **Multi-currency billing**: per-tenant billing currency, dated FX, SAR VAT equivalence on
    foreign-currency invoices, and settlement-versus-invoice FX variance recorded. Depends on
    `billing-vs-functional-currency.md`, which already separates the two meanings of
    `BaseCurrencyCode`.
11. **ZATCA-compliant self-invoicing** for KSA tenants: gapless sequential numbering, XML, QR,
    clearance/reporting — the product's own promise applied to its own receivables.
12. **Money lineage**: any figure clicks through to the price version, the commercial
    decision, the approver and the source meter reading, in one trail.

## Controls to add

Maker-checker exists at finalisation and on the irreversible revenue actions. It does not
exist at **the point the price is set**, which is the point that decides the money.

| Action | Today | Required |
|---|---|---|
| Create/edit plan price (`PlatformOperationsController.cs:250`) | Owner, single | Maker-checker + effective-dated version |
| Create/edit rate card (`PlatformBillingController.cs:216,267`) | Billing, single | Maker-checker; edit banned once **any** statement references it, not only a Final one |
| Change tenant plan (`TenantsController.cs:1413`) | Billing, single | Maker-checker + effective date + proration preview |
| Pin/clear rate card (`:588`) | Billing, single | Maker-checker — clearing especially, it silently reprices onto "whichever card is active" |
| Billing mode → non-Billable (`:651`) | Billing, single + reason | Maker-checker above a waived-value threshold |
| **Credit note** (`SubscriptionInvoiceService.cs:203`) | Owner + MFA, **no checker** | Maker-checker. This is the one real hole in an otherwise correct set |
| **Record payment** (`:149`) | Billing policy only, no MFA, no checker | Maker-checker or bank-feed match above a threshold |
| Payment terms, contact, address, PO (`:739`) | Billing, single + reason | Correct as built |
| Compute statement (`:361`) | Billing, single | Correct as built — idempotent |
| Finalize statement / invoice, tax rule approve | maker ≠ checker | Correct as built |

## Sequence

Ordered by dependency, not by appetite. Each row is a stream that can ship on its own.

| # | Stream | Delivers | Depends on |
|---|--------|----------|------------|
| 1 | **Turn on what already exists** | Dunning worker and accounting dispatcher enabled in production config with real delivery; alert on any Final statement unbilled past the settle lag | nothing — this is configuration and a sender |
| 2 | **Credit-note and payment controls** | Maker-checker on credit notes; idempotency key honoured (the console regenerates `crypto.randomUUID()` per call, so a double-click issues two credit notes — `client.ts:1660,1668,1705,1719`); `(TenantId, ExternalReference)` uniqueness | nothing |
| 3 | **Price-book versioning** | Effective-dated plan and rate-card versions; maker-checker on every price write | 2 |
| 4 | **Proration engine + preview** | Dated delta lines for plan change, cancellation and seat change; "what this changes" before commit | 3 |
| 5 | **Contract object** | Term, committed value, renewal, ramp — and the renewal date the customer list has no source for today | 3 |
| 6 | **Automatic invoicing** | Ready Final statement → draft invoice with no human | 1, 4 |
| 7 | **Server-derived tax + seller identity** | Deployment-level seller identity; jurisdiction resolved, not typed; hashes computed | nothing (can run parallel) |
| 8 | **Revenue ledger and CFO screen** | MRR bridge, NRR, ageing, DSO, cash collected, margin per tenant | 5, 6 |
| 9 | **Multi-currency** | Per-tenant billing currency, dated FX, SAR VAT equivalence | 3, `billing-vs-functional-currency.md` |
| 10 | **ZATCA self-invoicing** | Gapless numbering, XML, QR, clearance | 9, 7 |

Streams 1 and 2 are worth shipping whatever else happens: one is revenue that currently
leaks because nobody remembers, and the other is a duplicate-credit-note defect that a
double-click can trigger today.

## Two decisions needed from the owner

1. **Invoice numbering is already gapping.** Fixing it changes historical numbering
   behaviour. Confirm whether existing `NX-` numbers are preserved and the new sequence
   starts alongside them, or whether a renumbering is acceptable before first sale.
2. **Does Nexora invoice its own Saudi customers under ZATCA?** If yes, stream 10 is a
   compliance obligation and moves ahead of stream 8. If the vendor entity is not
   KSA-resident, it is a credibility question rather than a legal one and can stay last.
