# Logic defects: two sources of truth — 2026-09-04

Branch `audit/logic-two-sources-of-truth` from `origin/main` `4b76d9c` (deployed). Read-only
production evidence was taken on 2026-09-04 through `.claude/prod-read.sh`; nothing on the live
system was written to or called.

## The defect class

A rule lives in two places — screen and server, precheck and executor, writer and reader — and
one of them drifts; or a code path ends without producing either the outcome or a recorded
reason. The six instances found on the live system this week are listed at the end with their
status at this commit. Everything else here is new.

Severity: **P0** = the user is told it worked and it silently did not, or the row is permanently
stuck; **P1** = a late or unexplained refusal, or a fact recorded wrongly; **P2** = duplicated rule
with no observed drift, or dead code. Effort: S < half a day, M ≤ two days, L longer.

## Findings by severity

| # | Lane | Finding | Sev | Effort | Fixed here |
|---|------|---------|-----|--------|------------|
| 1 | C | PDF prints `USD` on a currency-less non-draft quote; readiness says `canSend` | P0 | S | **Yes** |
| 2 | C/A | Invoice issued with `CurrencyId NULL`; can never be paid | P0 | S | **Yes** |
| 3 | A | "Quote emailed to the customer" for a send that was only queued | P0 | S | **Yes** |
| 4 | B | Won/Lost/Responded activity dropped silently when the lead is unowned | P1 | S | **Yes** |
| 5 | C | `Rfq.RecDate` update path writes `0001-01-01`; turnaround metric reads ~17.7M hours | P1 | S | **Yes** |
| 6 | A | Supplier RFQ dispatch has no outbound-mail preflight (quote path has one) | P1 | M | No |
| 7 | A | Client PO → sales order hides the tax gate the quote screen surfaces | P1 | M | No |
| 8 | A | Goods-receipt warehouse dropdown offers N options, one can succeed | P1 | S | No (frontend lane) |
| 9 | A | Supplier PO issue: expected date already past, button live | P1 | S | No (frontend lane) |
| 10 | A | Order "Create shipment" client set missing `COMPLETED`/`CANCELED` | P1 | S | No (frontend lane) |
| 11 | A | Invoice dialog opens for a DRAFT order; server refuses on submit | P1 | S | No (frontend lane) |
| 12 | A | Supplier quote review discards the server's refusal sentence | P1 | S | No (frontend lane) |
| 13 | A | Lead qualify: readiness is a pure state machine, transition adds two rules | P1 | M | No |
| 14 | A | Lead promotion: 8 server refusals absent from both client blocker lists | P1 | M | No |
| 15 | A | Award dialog states an upper bound, never the supplier MOQ | P1 | S | No (frontend lane) |
| 16 | A | RFQ create blocked by a client-only inventory rule; outage = permanent block | P1 | S | No (frontend lane) |
| 17 | A | Credit/debit note: maker-checker rule invisible; creator sees a live Issue button | P1 | S | No (frontend lane) |
| 18 | C | Shipment carries two statuses that disagree (`StatusID` Pending vs `DeliveryStatus` DISPATCHED) | P1 | M | No |
| 19 | D | `FinanceOutboxMessages` produced by Postgres triggers; dispatcher registered but disabled by default, no heartbeat | P1 | M | No |
| 20 | B | `SlaSweepWorker.SweepLeadDeadlinesAsync` skips an assignee with no email, silently, forever | P1 | S | No |
| 21 | B | `SlaSweepWorker` approvals / supplier-ack sweeps: "nobody to tell — retry next sweep" for a structural gap | P1 | S | No |
| 22 | E | `SlaEvent` claim burned by a crash between claim and settle; no reaper, no UI, log says "resend by hand" | P1 | M | No |
| 23 | C | `SupplierSolicitation.SentOn` sentinel still read raw by the agent tool ordering and the overdue-chase email | P1 | S (migration for the real fix) | No — design below |
| 24 | C | Procurement views ship `"N/A"` as a currency code; grid prints "N/A 1,000.00" | P1 | S | No |
| 25 | B | `DeliveryConfirmationService.MarkOrderDeliveredIfCompleteAsync` returns silently on inactive order / no lines | P2 | S | No |
| 26 | B | `EmailInquiryAssemblyCoordinator.MarkNoInquiryAsync` drops a refused transition silently; sibling logs | P2 | S | No |
| 27 | B | `RoutingReconciliationWorker` swallows every `RoutingConflictException` with a comment naming one cause | P2 | S | No |
| 28 | B | `ProcurementDispatchWorker.UpdateSourcingCaseLifecycleAsync` returns silently on a dangling `SourcingCaseId` | P2 | S | No |
| 29 | D | `commercial_opportunity_outbox` and `commercial_exception_outbox`: producers, no consumer (the lifecycle-outbox bug, twice) | P2 | M | No |
| 30 | D | `AccountingOutboxDispatcher`, `SubscriptionDunningWorker`: disabled by default, no log, no heartbeat | P2 | S | No |
| 31 | E | `quote:{id}:delivery:v1` has no tenant-side escape; only a platform-owner recover | P2 | M | No |
| 32 | A | `DraftRFQsPage` approval dialog is unreachable; `rfqService.approve` has no live caller; `emailWarning` never rendered | P2 | S | No |
| 33 | A | `SupplierRfqBlockingReasons` duplicated in Procurement and SupplierController with different wording | P2 | S | No |
| 34 | A | `OPERATOR_TRANSITIONS`, `OpenForReceipt`, lot validation: hand-copied server state machines in TS | P2 | S | No |
| 35 | C | `QuoteService.GenerateQuotePdfAsync` bare `catch { }` around the logo | P2 | S | No |
| 36 | C | `GrowthIntelligenceService` injects `DateTime.MinValue` into a `Max()` | P2 | S | No |
| 37 | C | `Order.TotalAmount = 0` for unpriced RFQ-built orders, summed as real value | P2 | M | No |
| 38 | C | `CustomerPurchaseOrder.PoDate`, `Shipment.ShipmentDate`: non-nullable command fields manufacture `0001-01-01` (API-only) | P2 | S | No |

## Production evidence (2026-09-04, tenant BU 7 unless stated)

```
Quotes:        62 EXPIRED  CurrencyID NULL  TotalAmount 59,200,000.00
               63 EXPIRED  CurrencyID NULL  TotalAmount 740.00
               65 DRAFT    CurrencyID NULL  7 lines all 0.00
               66 SENT     CurrencyID 10    460,460.00  sent 22:26 today, lead 491 AssignTo NULL
Leads:         238 total, 8 with AssignTo, 0 with ContactID
commercial_activities: 0 rows       follow_up_tasks: 0 rows
Orders:        3  SHIPPED  MANUAL  TotalAmount 1,000.00  CurrencyID NULL
ReceivableDocuments: INV-2026-000001 Issued 1,000.00 CurrencyId NULL (order 3)
Shipments:     2  StatusID 71 (Pending)  DeliveryStatus DISPATCHED  order 3 SHIPPED
SupplierSolicitations: 1 DeliveryFailed SentOn -infinity; 2 Responded
procurement_outbox: 1 FAILED (DELIVERY_UNCERTAIN, attempt 1, not dead-lettered), 1 SENT
FinanceOutboxMessages: 2 rows, 0 processed (draft-created, issued for invoice 1, since 2026-08-05)
lifecycle_outbox_messages: 71 pending since 2026-07-28 (producer retired at this commit; rows orphaned)
unassigned_work_items: 230 Open, all past SlaDueOn
commercial_opportunity_outbox / commercial_exception_outbox: 0 rows
```

---

## 1. PDF prints `USD` on a currency-less non-draft quote — P0, S, FIXED

**Source A (precheck)** — `Backend/ERP_RFQ_Automation/Services/QuoteService.cs:947-960` (before):

```csharp
internal static string? DraftCompletenessBlocker(bool isDraft, long? currencyId, ...)
{
    if (!isDraft) return null;
    ...
    if (!currencyId.HasValue)
        return prefix + "this quote has no currency. Set the currency on the quote before sending it.";
```

**Source B (executor/renderer)** — `QuoteService.cs:1576` (before):

```csharp
var currency = quote.Currency?.Code ?? "USD";
...
r.RelativeItem().AlignRight().Text($"{currency} {(quote.TotalAmount ?? 0):N2}")
```

The gate was draft-scoped on the assumption that anything past DRAFT had passed it on the way
out. Backfilled quotes never took that path: production quotes 62 and 63 are EXPIRED with
`CurrencyID NULL`. A PDF download of quote 62 printed **"USD 59,200,000.00"** as the grand total
on the tenant's letterhead beside its VAT number, and `GET /api/Quote/62/send-readiness` — which
reads the same draft-scoped rule — answered `canSend: true`. The `?? "USD"` hazard is even named
in a comment at `QuoteService.cs:563-566` ("a 3.75x misstatement of price on a SAR quote"); the
comment closed one route to it and left the fallback in place.

**Fix.** The currency clause of `DraftCompletenessBlocker` now runs for every status, with a
non-draft wording ("issue it as a new revision with the currency set"); the renderer resolves
`currencyCode` once, after the gate, and the `"USD"` fallback is gone. Readiness and renderer
still read one rule, so they cannot disagree.

**Test** — `Backend/ERP_RFQ_Automation.Tests/IssuedQuoteCurrencyTests.cs`. Fixture is
production quote 62's shape: SENT, one priced line, tax derived, attested, `CurrencyId` null.
Revert-proof: with `QuoteService.cs` checked out from `origin/main`, `A_sent_quote_with_no_currency_is_refused_a_document_instead_of_printing_USD` and `The_currency_rule_does_not_depend_on_the_quote_being_a_draft` fail; the control `A_sent_quote_with_a_currency_prints_that_currency_and_nothing_else` passes both ways.

## 2. Invoice issued with `CurrencyId NULL`; unpayable forever — P0, S, FIXED

**Writer** — `Backend/ERP_RFQ_Automation/CommercialFinance/CommercialFinanceApplicationService.cs:112-118`:

```csharp
var document = new ReceivableDocument { ..., CurrencyId = order.CurrencyId, ... };
```

`Order.CurrencyId` is nullable; `CreateOrderPage.tsx:171-176` documents that "in CREATE mode there
is none"; `OrderService.UpdateOrderAsync` never touches it. Neither `CreateInvoiceAsync` nor
`IssueCoreAsync` (`:319-353`) checked it.

**Reader** — `CommercialFinanceApplicationService.cs:486`:

```csharp
if (document.CustomerId != request.CustomerId || document.CurrencyId != request.CurrencyId)
    throw new FinanceConflictException("Payment and invoice customer/currency must match.");
```

Production: order 3 (MANUAL, `CurrencyID NULL`) → `INV-2026-000001`, Issued, 1,000.00,
`CurrencyId NULL`. A numbered legal document that states no currency, and one no payment can
ever be allocated to, because `NULL != any long`. Statements, dunning and the aging report all key
on `CurrencyId` too. The rule "a payment must match the invoice's currency" lived at the payment;
the rule "an invoice must have a currency" lived nowhere.

**Fix.** `CreateInvoiceAsync` refuses an order with no currency, naming the order; `IssueCoreAsync`
refuses a draft with no currency at the point of no return.

**Test** — `Backend/ERP_RFQ_Automation.Tests/InvoiceCurrencyGateTests.cs`. Revert-proof: with
`CommercialFinanceApplicationService.cs` from `origin/main`, `An_order_with_no_currency_cannot_be_drafted_into_an_invoice` and `A_draft_that_reached_the_table_without_a_currency_is_refused_at_issue` fail; the control `An_order_with_a_currency_is_invoiced_and_issued_in_that_currency` passes both ways.

**Still open.** The manual order screen cannot set a currency at all (`CreateOrderPage.tsx:171`),
so a manual order is now honestly un-invoiceable instead of dishonestly invoiced. The screen needs
a currency field and `OrderService.CreateOrderAsync` should refuse without one (frontend lane, S).
`INV-2026-000001` itself needs voiding and re-issuing once order 3 carries a currency.

## 3. "Quote emailed to the customer" for a queued send — P0, S, FIXED

**Server** — `Backend/ERP_RFQ_Automation/Controllers/QuoteController.cs:463-468`:

```csharp
return Accepted(new { queuedForDelivery = result.QueuedForDelivery, delivered = result.Delivered, ... });
```

**Client** — `Frontend/src/api/services/quoteService.ts:330-333` (before):

```ts
await axiosInstance.post(`/api/Quote/${id}/email`, null, { params: { recipientEmail } });
return { held: false };
```

then `QuoteViewPage.tsx:154` and `QuotesPage.tsx:235`: `'Quote emailed to the customer'`.

`SendQuoteEmailAsync` almost always returns `Queued(false, false)` (`QuoteService.cs:1925`): a
`quote_delivery_requests` row is written and `QuoteDeliveryWorker` sends it later — or refuses it
later, with nobody watching, and the fixed key `quote:{id}:delivery:v1` then makes the quote
number permanently unsendable. The rep who read "emailed" closed the tab. Production quote 66
today: the row completed 8 seconds after it was queued — but only because the tenant's mailbox
happened to be configured; readiness blocks the non-transmitting case, not the render failure or
the SMTP outage.

**Fix.** `sendEmail` reads the 202 body; a shared `describeQuoteSendOutcome` gives both screens one
wording: "queued for delivery … the status changes to Sent once the email is confirmed" versus
"emailed to the customer" only when `delivered` is true.

**Tests** — `Frontend/src/api/services/quoteService.sendEmail.test.ts` (service + wording) and
`Frontend/src/pages/Sales/Quotes/QuoteViewPage.sendOutcome.test.tsx` (drives the real chain:
recipient → price confirmation → send). Revert-proof: with the three frontend files from
`origin/main`, 5 of 6 fail; the control "says emailed only when the server confirmed delivery"
passes both ways.

## 4. Won/Lost/Responded activity dropped silently when the lead is unowned — P1, S, FIXED

`Backend/ERP_RFQ_Automation/Sla/QuoteOutcomeService.cs:437` (before):

```csharp
if (attribution?.OwnerUserId is not > 0) return;
```

The outcome-side twin of `QuoteService.RecordQuoteSentWorkAsync` (known instance #3). With 230
of 238 leads unowned, every Won, Lost and CustomerResponded activity was dropped;
`commercial_activities` is empty on production. `fix/quote-email-body` (unmerged) fixes the
sent-side twin by falling back to `BusinessUnit.DefaultLeadOwnerUserId` and warning when that is
unset; this branch applies the identical rule to the outcome side, in a different file, so the two
branches do not conflict. Direct-entry quotes with no RFQ are now also credited.

**Test** — `Backend/ERP_RFQ_Automation.Tests/QuoteOutcomeAttributionTests.cs`, through the public
entry point `SetOutcomeAsync` with a real `SalesApplicationService`. Revert-proof: with
`QuoteOutcomeService.cs` from `origin/main`, both tests fail (no activity; no warning).

## 5. `Rfq.RecDate` update writes `0001-01-01` — P1, S, FIXED

`Repositories/RfqRepository.cs:570` copied `rfq.RecDate` unconditionally; the create path at
`:533` guards `== default`. `RfqUpdateRequestDTO.RecDate` is a non-nullable `DateTime`, so an
update payload without `recDate` binds to the sentinel and passes validation
(`DTOs/RFQ DTOs/RfqResponseDTO.cs:127-134` documents that `[Required]` cannot fire on a value
type). Reader: `CommercialLearning/CommercialLearningService.cs:337-338` computes
`(CreatedDate - RecDate).TotalHours` and keeps it because ~17.7 million is `>= 0`; the same file
guards the same column with `Year >= 2000` at `:70`.

**Fix.** The update path mirrors the create path. **Test** —
`RfqReceivedDateUpdateTests.cs`; revert-proof: `An_update_without_a_received_date_keeps_the_one_on_record` fails against `origin/main`, the control passes both ways. The reader-side floor at
`CommercialLearningService.cs:338` is a one-line follow-up not done here.

---

## Not fixed, and why

### 6. Supplier RFQ dispatch has no outbound-mail preflight — P1, M

`ProcurementDispatchWorker.cs:106-112` dead-letters `DELIVERY_PROVIDER_NOT_CONFIGURED` on the
first attempt; `ProcurementApplicationService.PrepareSupplierRfqAsync` (`:349`) never asks
`IProcurementDeliveryConfiguration`, and `SourcingCasePage.tsx:162` toasts "prepared and queued".
The quote path has exactly this preflight (`QuoteService.cs:1716-1725`, `OUTBOUND_MAIL_NOT_CONFIGURED`).
Not a trap — `RetrySolicitationAsync` exists and the workbench explains DEAD_LETTERED vs UNCERTAIN —
so P1. Fix: inject the configuration into `PrepareSupplierRfqAsync` and expose it on the
sourcing-case GET; needs a DI change and a worker-shaped test.

### 7. Client PO → sales order hides the tax gate — P1, M

`CustomerAwardApplicationService.cs:1215-1219` runs `QuoteService.TaxDerivationBlocker`;
`ClientPurchaseOrderReviewPage.tsx:114-115` computes `canConvert` from the differences read model
only. The Quote screen surfaces the identical gate as `OUTPUT_TAX_NOT_DERIVED`. The award workspace
(`CustomerAwardWorkspace.tsx:518-583`) runs four POSTs in a chain and the gate throws on the fourth,
after the PO and award are committed. Fix: a `convert-readiness` endpoint that calls the same
blockers, consumed by both screens.

### 8–12, 15–17. Frontend precheck gaps — P1, S each

Each is a client control that is live where the server will refuse, or a client rule the server
does not have. Listed with file:line in the Lane A appendix below. They belong to the frontend
lane; none is a silent success, all surface the server's refusal after the click (except 12, which
replaces it with a generic string — `SupplierQuoteReviewPage.tsx:124,130`).

### 13–14. Lead readiness endpoints restate a subset — P1, M

`LifecycleApplicationService.BuildStateAsync:408` is a pure state machine; the transition at
`:295-302` adds "commercial facts must be approved" and "every line needs a positive quantity".
`LeadDecisionWorkbenchService.cs:311-358` authors its own blocker list; `RfqPromotionService`
throws on eight conditions it never mentions, and `workbenchRules.ts:161-197` keeps a third copy
("`promotionBlockers` remains the authoritative client-side gate"). Fix: have both GETs call the
mutating path's own guard, as `EvaluateSendReadinessAsync` and `CreateRefundAsync` do.

### 18. Shipment carries two statuses — P1, M

`ShipmentController.cs:214` writes the tenant picklist `StatusId` from the DTO and `:238` writes
`DeliveryStatus = Dispatched` in the same row; `UpdateShipment` (`:435-448`) moves only
`StatusId`; the delivery module moves only `DeliveryStatus`. `ShipmentListPage.tsx:212-214` shows
both ("Tenant status: Pending" under a DISPATCHED chip). Production shipment 2: `StatusID` 71
Pending, `DeliveryStatus` DISPATCHED, order SHIPPED. Design: `StatusId` becomes a derived label of
`DeliveryStatus` (or is dropped from the update DTO); the picklist is a display mapping, not a
second state.

### 19. Finance outbox: producer on, consumer off — P1, M

`FinanceOutboxMessages` is written by Postgres triggers (`Migrations/20260723180000_AddFinanceOutbox.cs:99`
and four more). `FinanceOutboxDispatcherService` is registered (`Program.cs:448`) but
`FinanceOutboxDispatcherOptions.Enabled` defaults to `false` (`:9`) and `CommercialFinance:OutboxDispatcher`
is absent from `appsettings.json`; the service logs one Information line and returns
(`FinanceOutboxDispatcherService.cs:73-78`). No heartbeat, not in `/api/operations/readiness`.
Production: 2 rows since 2026-08-05, 0 processed. Exactly the lifecycle-outbox failure with one
extra step. `ProvisioningDiagnosticsService.cs:177-181` already shows the fix ("accepted work is
never acted on"). Decide: enable it, or retire the triggers the way the lifecycle producer was
retired.

### 20–21. SLA sweeps that skip silently — P1, S

`Sla/SlaSweepWorker.cs:272` `if (assignee is null || string.IsNullOrWhiteSpace(assignee.Email)) continue;`
— the five sibling sweeps do `unresolved++` and `LogWarning`. `:589` and `:861` `// nobody to tell —
retry next sweep` for a structural gap (no manager/admin) that never changes. One-line fixes; not
done here because the only harness (`Gate8FollowUpTriggerTests.SweepHost`) is private to its
file and a revert-proofed test needs a capturing logger threaded through it (M for the test).

### 22. `SlaEvent` claims have no reaper — P1, M

`InsertClaimAsync` (`SlaSweepWorker.cs:1444-1469`) commits `CLAIMED` in its own transaction before
the send. A restart between claim and settle leaves `CLAIMED`, which is `!= RELEASED`, so the
dedup key is burned forever; the log at `:1564` says "resend by hand" through a surface that does
not exist (no controller, no frontend reference). Backs SLA alerts, reorder alerts and below-floor
approvals. Design: a stale-CLAIMED sweep (older than N minutes → RELEASED) at the top of
`SweepOnceAsync`.

### 23. `SupplierSolicitation.SentOn` sentinel — P1, migration

Column is NOT NULL (`MigrationsBaseline/ErpRfqAutomationContextModelSnapshot.cs:913`); both
constructors at `ProcurementApplicationService.cs:444-461` and `:887-903` omit it, so Npgsql stores
`-infinity`. `SourcingEntities.cs:55-56` says "the real fix needs an EF migration and is deliberately
NOT done on this branch." Two readers still touch the raw column: `Agent/Tools/SourcingTools.cs:86`
`.OrderBy(s => s.SentOn)` (never-sent rows sort first) and `Sla/SlaSweepWorker.cs:1290`
`$"The request went out on {solicitation.SentOn:dd MMM yyyy}"` in an outbound email.

**Design (not executed, no migration on this branch):**
1. Migration: `ALTER TABLE "SupplierSolicitations" ALTER COLUMN "SentOn" DROP NOT NULL;`
   `UPDATE "SupplierSolicitations" SET "SentOn" = NULL WHERE "SentOn" = '-infinity';`
2. Model: `public DateTime? SentOn { get; set; }`; delete the `[NotMapped] SentOnUtc` shim and
   rename its readers.
3. `ProcurementDispatchWorker.cs:449` is the only writer of a real value; keep it.
4. Add a check constraint: `("Status" IN ('Sent','Responded','Declined','Expired')) = ("SentOn" IS NOT NULL)`
   so status and timestamp cannot disagree again.
5. Until then: both readers above should use `SentOnUtc` and the overdue-chase email should omit
   the sentence when it is null.

### 24. `"N/A"` as a currency code — P1, S

`ProcurementApplicationService.cs:792,802,806` `currencyCodes.GetValueOrDefault(row.CurrencyId ?? 0) ?? "N/A"`.
`Frontend/src/utils/currency.ts:32` handles `null` correctly; `"N/A"` is truthy and renders
"N/A 1,000.00" in the comparison grid. Fix: emit `null` and `long?`. Not done: the DTO change
ripples through `SupplierOfferView`/`SourcingAwardView`/`SupplierPurchaseOrderView` consumers and
needs the workbench view harness.

### 25–28. Silent early returns — P2, S each

Each is an inconsistency with a correct sibling in the same file:
- `Delivery/DeliveryConfirmationService.cs:355,361` return silently; `:377-383` throws with a
  sentence for the next gap in the same method.
- `Ingestion/Assembly/EmailInquiryAssemblyCoordinator.cs:648` returns; `MarkAssembledAsync:626-634`
  logs an error for the same refused transition.
- `CommercialRouting/RoutingReconciliationWorker.cs:169-172` swallows `RoutingConflictException`
  with a comment naming one of ten producers. Low risk today: candidates exclude leads with any
  routing decision, and the key is deterministic.
- `Procurement/ProcurementDispatchWorker.cs:519` returns on a dangling FK; `:518` is the
  legitimate "not part of a case" path.

### 29–30. Outboxes with no reader; idle-by-default workers — P2

`OpportunityPriorityApplicationService.cs:1045` and `CommercialExceptionApplicationService.cs:865`
write outbox rows nothing reads; both have `(ProcessedAtUtc, AvailableAtUtc)` indexes built for a
dispatcher that was never written. 0 rows on production today. `AccountingOutboxDispatcher.cs:69-73`
spins silently when disabled; `SubscriptionDunningWorker.cs:23` likewise. Decide per the lifecycle
precedent: build one dispatcher or retire the producers.

### 31. `quote:{id}:delivery:v1` — P2, M

Confirmed as described. Readiness now blocks before the row is written and names the escape
("issue as a new revision") once trapped. The only recover is `POST /api/platform/tenants/{id}/dead-letters/recover`
(MFA'd platform Owner). `RetrySolicitationAsync` (`ProcurementApplicationService.cs:1047-1090`) is
the reference shape — DEAD_LETTERED retries in one click, UNCERTAIN needs `ConfirmedNotDelivered` —
and porting it to quote delivery turns "new revision" back into "retry".

### 32. `DraftRFQsPage` approval is dead code — P2, S

`setApprovalDialogOpen(true)` is never called (`DraftRFQsPage.tsx:42,74,283,290` are the only
references; the actions column at `:198-215` offers View, Open lifecycle, Delete). `rfqService.approve`
therefore has no live caller, and the `emailWarning` the server computes on six paths
(`RfqController.cs:588-631`) is rendered nowhere. Delete the dialog and the mutation, or wire a
button and render `emailWarning`; a test on the current code would exercise an unreachable path,
so nothing was changed here.

### 33–38. Duplicated rules and remaining sentinels — P2

See the lane appendices. `Order.TotalAmount = 0` (`OrderService.cs:306-334`) is the one worth a
design note: an order built from an unpriced RFQ is stored as a real zero-value order and summed
into `LeadDecisionService.cs:652` / `GrowthIntelligenceService.cs:571`; the honest shape is
`TotalAmount` nullable-until-priced or an explicit `IsPriced` flag (migration).

---

## Status of the six known instances at `4b76d9c`

| # | Instance | Status here |
|---|----------|-------------|
| 1 | `QuoteViewPage` precheck vs `GenerateQuotePdfAsync` | Fixed on main via `send-readiness`; the draft-scoping of that same rule is finding 1 above |
| 2 | `send-readiness` omits price attestation | Deliberate and documented (`QuoteService.cs:1664-1666`); `fix/quote-email-body` adds it anyway, unmerged |
| 3 | `RecordQuoteSentWorkAsync` silent on unowned lead | Still present on main (`QuoteService.cs:1977`); fixed on `fix/quote-email-body`, unmerged. Production: quote 66 sent today, 0 follow-ups, 0 activities. Its outcome-side twin is finding 4, fixed here |
| 4 | `SupplierSolicitation.SentOn` sentinel | Shim `SentOnUtc` on main; two raw readers remain (finding 23) |
| 5 | `lifecycle_outbox_messages` unread | Producer retired on main (`LifecycleApplicationService.cs:235-238`); 71 orphan rows remain; the same bug is live in finding 19 and latent in finding 29 |
| 6 | `LeadsPage` filtered-to-zero copy | Fixed on main (`LeadsPage.tsx:427-444`, pinned by `LeadsPage.assign.test.tsx:456`) |

---

## Lane A appendix — every precheck/executor pair examined

Reference implementations (client asks the server's own guard): `GET /api/Quote/{id}/send-readiness`,
`GET /api/Quote/{id}/price-attestation`, `GET /api/procurement/rfq-items/{id}/quote-comparison`,
`GET /api/customer-awards/purchase-orders/{id}/quote-line-matches` (differences half),
`GET /api/commercial-finance/payments/{id}/refund-eligibility`, `GET /api/delivery/orders/{id}/delivered-quantities`.

Restating a subset: `decision-workbench`, `commercial-lifecycle/.../lifecycle`, `write-off-eligibility`
(`CreateWriteOffAsync:657-662` restates instead of calling `GetWriteOffEligibilityAsync` the way the
refund path does — one line), `rfq/{id}/commercial-intelligence` (one of ~10 prepare-draft refusals).

| Screen | Server | Difference |
|---|---|---|
| `SourcingWorkbenchPage.tsx:3176-3190` warehouse `<Select>` lists every active warehouse | `ProcurementApplicationService.cs:2265` every receipt line must match the PO's warehouse | one option can succeed |
| `SourcingWorkbenchPage.tsx:1416-1424` Issue PO live when APPROVED | `:2037-2038` `ExpectedOn` must be after today; `:2043` every line's supplier quote unexpired | button live for a PO that can never issue |
| `SourcingWorkbenchPage.tsx:2478-2484` create PO | `:1508-1509` `ExpectedOn <= today` refused | date field has no `min` |
| `SourcingWorkbenchPage.tsx:2207-2212` award quantity | `:1383` below supplier MOQ refused | upper bound only |
| `OrderListPage.tsx:191-193` `['SHIPPED','DELIVERED','CANCELLED']` | `ShipmentController.cs:594-595` adds `COMPLETED`, `CANCELED` | hand-copied set already differs |
| `OrderViewPage.tsx:237` invoice gated by permission only | `CommercialFinanceApplicationService.cs:92-93` order must be CONFIRMED/COMPLETED/SHIPPED/DELIVERED or quote-backed | dialog opens on a DRAFT order |
| `AccountsReceivablePage.tsx:633` Issue for any Draft | `:342-343` creator cannot issue own credit/debit note | maker-checker invisible |
| `ProcessRFQPage.tsx:1496` blocks on inventory match `'unavailable'` | `RfqController.cs:311-334` no such rule | a client-only rule; an outage is a permanent block |
| `SupplierQuoteReviewPage.tsx:223-232` | `SupplierQuoteCommercialService.cs:49,93-100` four refusals | server sentence discarded at `:124,130` |
| `ClientPurchaseOrderReviewPage.tsx:114-115` | `CustomerAwardApplicationService.cs:1202-1219,1244` product, tax, DRAFT status | none surfaced |
| `deliveryService.ts:46-53` `OPERATOR_TRANSITIONS` | `Delivery/DeliveryEntities.cs:85-86` ladder permits skips | hand-copied state machine |
| `SourcingWorkbenchPage.tsx:1442-1457` receipt statuses | `ProcurementEntities.cs:160-163` `OpenForReceipt` | identical today, copied |
| `BoqEditorPage.tsx:328` `totals.tbd > 0` | `BoqBuilderService.cs:476-478` recomputes | duplicated, mitigated by `dirty` |

## Lane B appendix — classification of silent exits

Deliberate and correctly recorded (no action): the empty-set / disabled-policy guards throughout
`SlaSweepWorker`, `InsertClaimAsync`'s unique-violation catch, `LifecycleApplicationService`
replay returns, `EmailInquiryAssemblyCoordinator.RecordComponentOutcomeAsync` terminal guard,
`EmailIngestEnqueuer.ScheduleAsync` (writes a reason code on every drop), `ManualUploadService`
and `FolderService` skip paths (warn + counter), `NotificationService.DispatchAsync`,
`QuoteDeliveryDispatcher` (best in tree), `CommercialCaseQueryService.Reconcile` (surfaces gaps
verbatim), `CustomerAwardApplicationService.DerivePurchaseOrderStatusAsync` cancelled guard.

Defects: findings 4, 20, 21, 25–28, plus `QuoteService.RecordQuoteSentWorkAsync` (known #3) and the
bare `catch { }` at `QuoteService.cs:1331` (logo; an unbranded PDF goes to the customer with no log).

## Lane D appendix — hosted services

Every long-running worker has a top-level catch, so `BackgroundServiceExceptionBehavior.Ignore`
(`Program.cs:128`) is neutralised by discipline. Heartbeats cover extraction, quote delivery,
procurement dispatch, SLA sweep, reorder sweep, routing reconciliation, email poller, AI
reservation reconciliation, email-inquiry recovery, scheduled reports and billing run. Without a
heartbeat and disabled by default: `FinanceOutboxDispatcherService`, `AccountingOutboxDispatcher`,
`SubscriptionDunningWorker`. `ConsoleEmailSender` is a no-op transport but `TransmitsMail` is
consulted before the fact everywhere it matters — for quotes. Not for supplier RFQs (finding 6).

## Lane E appendix — idempotency keys

Trapping: `quote:{id}:delivery:v1` (finding 31), `SlaEvent.DedupKey` (finding 22). Not trapping:
supplier solicitation keys (client-generated per click, with `RetrySolicitationAsync`), extraction
dedup (tenant-reachable requeue), email ingest dedup (resumes incomplete work), lead identity
fingerprint (classifies, does not gate), usage metering (at-most-once by intent), invoice and
journal numbering (row-locked counters). Same shape as the quote key but benign because the
operation has no external side effect: `lead-promotion:{bu}:{leadId}`, `ar-payment:{id}:v1`,
`reservation-consume:{id}`, `attachment-integrity:{id}:{digest}`. Any of these that later acquires
a send becomes finding 31.
