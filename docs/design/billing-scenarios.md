# Billing: the scenarios we build to

Working document. Scenarios come from the people who would own or use the module — a CFO,
a Controller and a Revenue Accountant on one side, an AR/Collections specialist and a
Billing Operations Analyst on the other, a Saudi indirect-tax pair on the third — and are
then reviewed by Internal Audit for leaks before anything is built.

**The rule this document exists to enforce:** a feature is not done because it was
implemented. It is done when the named scenario below passes as a test. Anything not
traceable to a scenario here is speculation and does not get built.

Each scenario carries: the situation with real figures, what must happen (arithmetic
spelled out), what happens today with a `file:line`, who touches it and how often, and one
acceptance sentence that becomes the test name.

---

## Verified operator defects (AR/Collections panel, confirmed against the code)

These came from walking the actual job rather than reading the schema, and each was
re-checked in the source before being written down. They are listed first because several
are small, unambiguous, and cost money every month they survive.

### AR-01 · The payment date is whatever day I keyed it

**Situation.** A wire from Aramco Trading lands in the bank on Monday 1 September. I am
chasing other accounts and post it on Thursday 4 September.

**What must happen.** The receipt records the value date the money arrived — 1 September.
Ageing, DSO and the bank reconciliation all key off that date.

**What happens today.** `SubscriptionInvoicesSection.tsx:159` passes
`new Date().toISOString()` as `receivedAtUtc`. The dialog never asks. The API accepts a
date (`client.ts:1713`, `SubscriptionInvoiceService.cs`) — the console simply does not
offer one. Every payment in the system is dated the day somebody typed it, so ageing and
DSO are wrong by construction and no amount of operator care fixes it.

**Who touches it.** AR, every receipt, daily.

**Acceptance.** `A_receipt_records_the_value_date_the_money_arrived_not_the_day_it_was_keyed`

### AR-02 · One wire settling three invoices

**Situation.** Aramco pays one wire, reference `FT240903`, for $18,400 covering invoices
`NX-202608-00000141` ($6,000), `-142` ($7,400) and `-143` ($5,000).

**What must happen.** One receipt for $18,400 against reference `FT240903`, allocated
across the three invoices, with any remainder parked as unapplied cash.

**What happens today.** `SubscriptionPayment.ExternalReference` is **globally unique**
(`ErpRfqAutomationContext.Billing.cs:191`), so one bank reference can exist against exactly
one invoice for all time. The operator invents `FT240903-A/-B/-C` — putting a false
reference into the field the bank reconciliation depends on — and works through three
dialogs. An overpayment is refused outright (`SubscriptionInvoiceService.cs:279-280`)
rather than parked; unapplied cash has no representation at all.

**What it needs.** Receipt-first cash application: enter the wire once (reference, value
date, amount, currency), then allocate. Uniqueness relaxed from global to per-tenant, or a
receipt/allocation split.

**Acceptance.** `One_receipt_settles_many_invoices_and_parks_the_remainder_as_unapplied`

### AR-03 · A credit note takes one signature and two clicks

**Situation.** A customer is given a $500 goodwill credit. The operator double-clicks.

**What must happen.** One credit note. A second identical request inside the replay window
returns the first answer rather than creating another.

**What happens today.** The server has a real replay guard — `SubscriptionCreditNotes`
carries a unique index on `IdempotencyKey`
(`ErpRfqAutomationContext.Billing.cs:179`) and the service honours it
(`SubscriptionInvoiceService.cs:215-223`). The console defeats it by minting a fresh
`crypto.randomUUID()` on **every** call (`client.ts:1710`), so the guard can never fire and
a double-click issues two credit notes. The key must be stable per edit, not per request.

Separately: a credit note is Owner + MFA with **no checker**
(`PlatformSubscriptionInvoicesController.cs:121-123`), while a write-off of any size needs
two Owners (`:52-55`). Money out on one signature, adjustments on two — the asymmetry is
the wrong way round.

**Acceptance.** `A_double_click_issues_one_credit_note_not_two` and
`A_credit_note_requires_an_independent_approver`

### AR-04 · An invoice refused by the customer can never be reissued

**Situation.** An invoice goes out under the wrong legal entity. The customer's AP
department refuses it and asks for a corrected one.

**What must happen.** Void or credit the original, correct the buyer details, and issue a
replacement against the same billing period.

**What happens today.** Buyer name, address and PO are frozen into the invoice when the
draft is created (`SubscriptionInvoiceService.cs:136-141`), so correcting the tenant
profile afterwards changes nothing. `SubscriptionInvoices.BillingStatementId` is unique
(`ErpRfqAutomationContext.Billing.cs:156`) and Create returns the existing invoice or 409s
(`SubscriptionInvoiceService.cs:47-60`) — **the statement is spent**. Void requires an
entirely unsettled invoice at exactly its total
(`SubscriptionRevenueControlService.cs:244-246`), and even a clean void leaves no way to
raise the replacement. The receivable becomes uncollectable inside the product.

**Acceptance.** `A_voided_invoice_releases_its_statement_so_a_corrected_one_can_be_issued`

### AR-05 · A disputed invoice cannot be marked as disputed

**Situation.** A customer queries one line of a $7,400 invoice and withholds that portion
pending an answer. They intend to pay the rest.

**What must happen.** The disputed amount is flagged with a reason, an owner and a review
date; chasing stops on that amount only; the rest continues to be chased and aged.

**What happens today.** No dispute, hold or query concept exists anywhere in `Billing/` or
the platform console. The invoice ages as ordinary delinquency and looks identical to a
customer who simply is not paying. The only lever is a credit against the whole invoice
with no line reference.

**Acceptance.** `A_disputed_amount_stops_its_own_chase_without_stopping_the_rest`

### AR-06 · There is no collections worklist

**Situation.** Monday morning. Which accounts do I ring today?

**What must happen.** One list, all customers, ordered by next-action date then amount, with
enough on each row to pick up the phone without opening anything: who owes it, how much,
how late, disputed or promised, when we last spoke and what was said, and the name and
number to ring.

**What happens today.** The console only ever requests one tenant's invoices at a time
(`client.ts:1692-1694`) although the API already accepts no tenant filter
(`PlatformSubscriptionInvoicesController.cs:23-28`). The table has no sort, no filter and
no totals (`SubscriptionInvoicesSection.tsx:252-341`); the ageing bucket is computed
per-row in the browser (`:45-55`); the billing contact lives in a different section of a
different tab (`CommercialTab.tsx:394-397`). Reaching one customer's invoices costs about
forty seconds of navigation. The portfolio tiles are four counts with no money in them
(`BillingPage.tsx:340-368`).

**Acceptance.** `The_collections_worklist_answers_who_to_ring_without_opening_a_customer`

### AR-07 · Nothing chases, and nothing invoices

**Situation.** A Final statement is ready on 3 September. Nobody opens the console until
the 20th.

**What must happen.** The statement becomes a draft invoice without a human; an unbilled
Final statement past the settle lag appears on somebody's list with their name against it;
an overdue invoice is chased on a schedule with delivery evidence.

**What happens today.** Invoices are created only when a person POSTs
(`PlatformSubscriptionInvoicesController.cs:31-50` is the sole caller of `CreateDraftAsync`);
`BillingRunWorker.cs:228,238` computes drafts and never invoices. Dunning defaults off
(`SubscriptionDunningWorker.cs:11`) and, when on, writes a row and sends nothing to anybody
(`SubscriptionRevenueControlService.cs:216-227`). No customer-facing invoice document exists
at all, so even a willing operator has nothing to attach to an email.

**Acceptance.** `A_ready_final_statement_becomes_a_draft_invoice_without_a_human` and
`An_unbilled_final_statement_past_the_settle_lag_reaches_a_named_owner`

### AR-08 · Terms nobody agreed, and a number from the wrong month

**Situation.** A draft invoice is raised on 30 September and the second approver finalises
it on 3 October.

**What must happen.** The document carries a number in the period it was issued, and a due
date computed from the terms in force at issue.

**What happens today.** The due date is fixed at draft time from `PaymentTermsDays ?? 30`
(`SubscriptionInvoiceService.cs:134`) — so a customer who agreed nothing silently receives
thirty days of credit — and the number is stamped from the draft date
(`SubscriptionInvoiceService.cs:192`), so the invoice goes out as `NX-202609-…` already
three days into its terms.

**Acceptance.** `An_invoice_is_numbered_and_dated_from_when_it_was_issued_not_drafted`

---

## What the operators asked the system to REFUSE

Stated in their words, because a control that prevents a mess is worth more than a report
that describes one:

1. Refuse a receipt dated "now" when the money landed on another day — make the value date
   an input.
2. Refuse a credit note on one signature, and refuse the duplicate a second click creates.
3. Refuse an invoice with terms nobody agreed, or a due date computed from a stale draft.
4. Refuse to let a Final statement go unbilled past the settle lag quietly.
5. Refuse to chase a disputed amount — and refuse to mark something disputed without a
   reason, an owner and a review date.

## What is genuinely well built, and is not to be disturbed

Recorded so that a rebuild does not throw it away: statement lines carry real provenance
and honest coverage caveats; Final statements are immutable; `UNIQUE(TenantId, PeriodStart)`
makes double-charging a month impossible; maker ≠ checker holds on finalise and on
void/refund/reversal/write-off; and the server-side ceilings in
`SubscriptionRevenueControlService.cs:229-253` mean a refund cannot exceed cash actually
received. The AR panel's verdict on that control set was that it is better than most
systems they have worked in.

---

## The four-formula defect (CFO panel BIL-11, verified and extended)

The single worst thing the panels found, because it is small, invisible, and gates two
business decisions.

**"What does this customer owe" is computed four times, in four ways.** Two are right and
two — both of them *gates* — are wrong, differently, and disagree with each other:

| Formula | Where | Write-off | Reversals | `Corrected` |
|---|---|---|---|---|
| Revenue control | `Billing/SubscriptionRevenueControlService.cs:232` | included | included | counted |
| Payment settle | `Billing/SubscriptionInvoiceService.cs:293` | included | — | counted |
| **Offboarding gate** | `Platform/Lifecycle/TenantOffboardingReadinessService.cs:164` | **omitted** | omitted | **excluded wholesale** |
| **Past-due gate** | `Platform/Activation/TenantActivationPolicyService.cs:323` | **omitted** | omitted | counted |

Any credit note sets `Status = Corrected` (`SubscriptionInvoiceService.cs:242`), whatever
its size. So:

- **A $1 goodwill credit on a $10,000 invoice hides $9,999** from the offboarding gate,
  which excludes `Corrected` rows entirely. The past-due gate still sees it. The two gates
  now disagree about the same customer.
- **A fully written-off tenant is permanently PastDue** (past-due gate ignores
  `WrittenOffAmount`) **and can never be archived** (offboarding gate ignores it too) — the
  bad-debt process cannot complete inside the product.

### BIL-11 · Write off a bad debt and close the account

**Situation.** Dammam Logistics enters liquidation owing $2,592.10 on
`NX-202605-00000042`, 214 days overdue. It is written off in full on 30 November and the
account is closed.

**What must happen.** AR moves to bad-debt expense; the tenant reports zero open AR;
offboarding proceeds.

**What happens today.** The write-off itself completes correctly
(`SubscriptionRevenueControlService.cs:223`) and the invoice DTO shows outstanding 0.00.
Both gates still see a balance. The tenant is stuck PastDue and un-archivable, for ever.

**Acceptance.**
`A_fully_written_off_tenant_reports_zero_open_AR_and_can_be_archived`
`A_one_dollar_credit_on_a_ten_thousand_dollar_invoice_still_reports_the_remaining_balance`

**The fix is not four patches.** It is one shared, tested balance function that every
caller uses — the arithmetic in `SubscriptionRevenueControlService.cs:232` is already the
right one. Four copies of a money formula is the defect; the wrong answers are symptoms.

---

## Constraints the CFO panel REJECTED or AMENDED

Recorded because a control the finance team routes around is worse than no control — it
looks like assurance and provides none. Verdicts are theirs, from a real month-end.

| Proposed | Verdict | Their reason |
|---|---|---|
| Maker-checker on **record payment** | **REJECT** | 15–25 receipts a month, each already matched to a bank line. Two-person approval on cash *in* is the control that gets bypassed by month three. Use a bank-feed match, with a threshold for manual entries only. |
| Blocked meters allowed onto a rate card, refused at finalisation | **REJECT as built** | Refuse the card line at create. Discovering it as a 409 on working day 3 makes a customer silently unbillable for a month. |
| 48h settle lag before finalise | **AMEND** | Right rule, wrong shape — it makes a working-day-1 close impossible every month. Allow early finalise with a recorded acknowledgement, or shorten to 24h and alert on late arrivals. |
| Maker ≠ checker on statement finalise | **AMEND** | Keep it for *human* makers. The sweep sets `ComputedBy = "system:billing-run"`, so any Owner qualifies — until the Controller clicks Compute to refresh a figure and **locks themselves out of their own close**. |
| Rate-card edit banned once **any** statement references it | **AMEND** | Ban on *Final* only, as already built. "Any" freezes a card the moment the six-hourly sweep touches one Draft, so a typo in a new card could never be corrected. |
| Maker-checker on pin/clear rate card | **SPLIT** | Accept for *clear* (it silently reprices). Reject for *pin* — pinning the card the customer actually signed is remediation and must not need a second Owner at 18:00 on WD3. |
| Maker-checker on credit notes | **ACCEPT** | "The one genuine hole. A credit is a giveaway with no cash trail." |
| Effective-dated plan prices + maker-checker | **ACCEPT** | One keystroke retro-bills 40 tenants $16,000 (BIL-12). |
| `(TenantId, ExternalReference)` uniqueness | **ACCEPT** | AR-02 / BIL-07. |
| Server-derived tax and computed evidence hashes | **ACCEPT** | Removes the highest-risk keystrokes in the module. |

### Control theatre, named by the people subject to it

1. The hand-typed 64-character SHA-256 "evidence" hash, shape-checked only.
2. The hand-typed seller legal name and tax number, checked against nothing, while the
   server already holds the values.
3. Three separate MFA'd Owner approvals to move one invoice from Final statement to
   posted — where **the checker is the same person every time, because the company is four
   people**. None of it makes a number safer; all of it costs a working day.

## What is already right, and must be generalised rather than rebuilt

**BIL-04 — proration for `BillingStartsOn` is correct today.** Exact-ratio arithmetic
(`BillingStatementService.cs:1210`), a marker line that states "13/31 × 2000.00" on the
statement (`:1163-1170`), flow meters counted from the start date while period-end stock
meters carry a proration note (`:359-363`). The proration engine the other scenarios need
is a generalisation of this, not a second implementation beside it.
