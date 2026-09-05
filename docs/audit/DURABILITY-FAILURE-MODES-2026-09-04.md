# Durability and failure modes of the commercial spine — 2026-09-04

Branch `audit/durability-failure-modes` from `origin/main` 4b76d9c (deployed). Backend paths are
relative to `Backend/ERP_RFQ_Automation/`.

## Scope and method

Production is one Render instance with a disk attached: every deploy is a stop-then-start
(~40 s), and a host inotify exhaustion took it down for 90 minutes on 2026-09-03. The first
customer quote was delivered live on 2026-09-04. This audit asks, for every stage from email
ingest to payment, what happens when the process dies **between two writes**, what is retried,
what is fenced, what is dead-lettered, what is silently lost, whether a duplicate request is
safe, whether two operators are serialised, and what the tenant sees versus what only the
platform operator sees.

Rather than reading code top-down, each stage was traced from the write sites outward, the
production tables were queried for rows already stuck in the states the code allows, and the
three worst torn writes were reproduced with tests that kill the operation between the writes
(a `SaveChangesInterceptor` that throws exactly when `Quote.SentOn` is being written, and rows
seeded in the exact state a mid-write crash leaves). Every fix is revert-proofed: the test was
run against the old code and failed before it was run against the new code and passed.

## Production evidence (read-only, 2026-09-04)

| Table | State | Count | Meaning |
|---|---|---|---|
| `procurement_outbox` | `FAILED` / `DELIVERY_UNCERTAIN`, attempt 1, round 0, since 2026-08-20 | 1 | The 2026-08-19 wedge, fenced uncertain. Tenant-visible since PR #149; operator recovery cannot touch it (`PlatformDeadLetterRecoveryService.cs:110-111` recovers `DEAD_LETTERED` only). Its sourcing case sits at `OUTREACH_READY` with `NextAction = "Review failed Supplier RFQ delivery"`. |
| `quote_delivery_requests` | completed, attempt 1, `CompletedOn` 22:26:13 | 1 | The live delivery. `Quotes.ID 66` has `SentOn` 22:26:12 and status 315. Note the order: `SentOn` was written **one second before** the ledger row was sealed — the window this audit's top finding lives in. |
| `lifecycle_outbox_messages` | unprocessed | 71 | 65 lead status transitions, 4 promotions, 2 quote transitions. Producer removed in PR #142; there was never a consumer. Orphans; no queue reports them. |
| `FinanceOutboxMessages` | unprocessed | 2 | `finance.receivable.draft-created` and `finance.receivable.issued` for the only invoice ever issued (2026-08-05). Written by DB triggers, never dispatched (`appsettings.json:48-50` `OutboxDispatcher.Enabled=false`). No queue reports them. |
| `ExtractionJobs` | `DeadLetter` | 65 | 36 evidence-integrity failures, 11 "LLM returned no result", 7 "NpgsqlRetryingExecutionStrategy does not support user-initiated transactions" (July), 5 AI-not-authorised, 4 save failures, 1 password PDF, 1 intake-link mismatch. Visible at `/api/operations/readiness/extraction-dead-letters` and to the operator. |
| `EmailIngests` | `NeedsReview/Uncertain` 64, `NeedsReview/null` 21, `Failed/null` 7 | 92 | Mail that produced neither a lead nor a rejection (the 37 % from the 2026-08-24 intake repair). |
| `SupplierSolicitations` | `DeliveryFailed` 1, `Responded` 1 | 2 | Consistent with the outbox. |

No leased row anywhere had an expired lease at the time of the query; the reclaim paths are
doing their job for what they cover.

## The single most dangerous torn write

**Customer quote delivery, after the SMTP provider has accepted the message**
(`QuoteDelivery/QuoteDeliveryDispatcher.cs`, old lines 236-250).

The old order was: send → `FinalizeQuoteDeliveryAsync` (its own Serializable transaction:
`Quote.SentOn`, lifecycle event, follow-up task, sales activity — `Services/QuoteService.cs:1931-1972`)
→ `store.CompleteAsync` (a separate `SaveChanges`, `QuoteDeliveryStore.cs:91-116`). Any exception
after the send — including a failure in the *bookkeeping* that has nothing to do with delivery,
such as a lifecycle refusal or a follow-up-task write — was recorded as
`DeliveryOutcomeUncertain:<code>` and dead-lettered on the spot (`maxAttempts: 1`,
`QuoteDeliveryStore.cs:87-89`). A process death between the two writes was swept into the same
state by the expired-lease sweep (`QuoteDeliveryStore.cs:41-52`).

Consequences, all confirmed by test before the fix:

1. The rep is told *"the customer may or may not have received it… check with the customer"*
   (`QuoteService.cs:1742-1746`) about a quote the provider has just confirmed accepting.
2. The quote stays DRAFT with `SentOn` null. The in-flight edit guard (`QuoteService.cs:544-551`)
   only looked at rows with `CompletedOn == null && DeadLetteredOn == null`, so the dead-lettered
   row released the lock: **the quote was editable while the customer held the PDF.**
3. The fixed idempotency key `quote:{id}:delivery:v1` (`QuoteService.cs:1855`) means the quote
   can never be sent again under its number; the rep is told to issue a revision — of a quote
   the customer already has.
4. If `Finalize` succeeded and only `CompleteAsync` failed, the quote said SENT while readiness
   said UNCERTAIN — two sources of truth in open disagreement.
5. The platform operator's dead-letter recovery (`PlatformDeadLetterRecoveryService.cs:126-137`)
   did not check for the uncertain code at all: "recover" reset the row and **re-sent the quote**
   to the customer. The supplier-RFQ branch has the confirm-before-retry gate; the quote branch
   did not.

Fixed in this PR (see "Changes"): the ledger row is sealed first (provider acceptance is the
fact; a sealed row can never be claimed or recovered), the quote status is derived from the
ledger and reconciled on the next cycle without resending, readiness says DELIVERED (never
UNCERTAIN) while the status catches up, edits are refused for delivered *and* uncertain quotes,
and operator recovery of an uncertain quote delivery requires `confirmedNotDelivered=true`.

## Stage-by-stage

Legend — **T**: transactions per operation; **Dies between**: state after a crash between writes;
**R/F/D/L**: retried automatically / fenced terminal or uncertain / dead-lettered / silently lost;
**Dup**: idempotency and whether the key can trap a legitimate retry; **Conc**: serialisation of
concurrent operators; **Sees**: tenant / operator-only.

| # | Stage | Writes, T, dies-between | R / F / D / L | Dup and key trap | Conc | Sees |
|---|---|---|---|---|---|---|
| 1 | Email ingest | S3 raw copy (`Services/EmailService.cs:1282`), then 3-4 un-wrapped `SaveChanges` (`:1300`, `:1384`, `:1448`) then IMAP `\Seen` (`:845`). Dies before `:1300` → orphan object, re-fetched. Dies before `\Seen` → re-fetched, resumes on the existing row (`:1227-1249`). Row stuck `Pending` → stranded sweep after 15 min (`:1501`). | R: every cycle forever, no backoff (`EmailBackgroundService.cs:566-578`). F: `Failed - raw message lost` (`:1534`). L: per-UID catch (`:866-872`), folder-sweep catch (`EmailBackgroundService.cs:531-534`), stranded-sweep per-row catch leaves `Pending` forever with no attempt counter (`:1645-1652`); mail older than `MaxLookbackDays`=30 (`:149`) is never seen. | Unique `(EmailConfigurationID, MessageID)` (`ErpRfqAutomationContext.cs:395`). **Trap**: a terminal-failed row occupies the key for ever; only `POST /api/email-triage/{id}/reprocess` reopens it. | Advisory lease `nexora:email-poller` around the whole cycle; single-instance by construction (`EmailBackgroundService.cs:14-20`, `:96`). `EmailIngest` has no version — last-writer-wins between poller, persister and sweep. | Tenant: mailbox health (`MailboxController.cs:655-712`), triage "stopped" tab only once `ParseStatus` starts with `Failed` (`EmailTriageService.cs:261-271`) — a `Pending` row is in no tab. Operator: `email-poll-channel` readiness. Per-UID and folder-sweep failures: **nobody**. |
| 2 | Extraction | Claim is one CTE statement with `FOR UPDATE SKIP LOCKED`, 5-min lease, `Attempts+1` (`ExtractionQueue.cs:211-295`); status hops are separate fenced UPDATEs (`ExtractionWorker.cs:325-454`); LLM call un-fenced (`:387-409`); persist+complete is one transaction (`:2263-2380`); `AssembleAsync` runs **after** commit (`:504`). Dies after commit, before assemble → job `Succeeded`, assembly stuck `ReadyForAssembly`, recovered by the 2-min sweep (`EmailInquiryAssemblyRecoveryService.cs:318-324`). Dies during LLM → attempt burned, spend not recovered. | R: backoff `2^Attempts` capped 1 h, `MaxAttempts` 5 (`ExtractionQueue.cs:607-616`). F/D: `exhausted`/`quarantined` CTEs dead-letter at claim (`:213-269`). L: best-effort annotations (`ExtractionWorker.cs:966-972`, `:1089-1095`, `:1164-1170`); jobs dead-lettered by the claim statement get none of them (`:1134-1137`). | Unique `(BU, SourceDocumentOccurrenceId)` (`Tenancy.cs:608-610`). **Trap**: reconciliation key `extraction:{bu}:{job}:inquiry:{n}` (`ExtractionWorker.cs:1792`) omits `Attempts`; a dead-letter recovery replay that yields a different fingerprint throws for ever (`LeadIdentityApplicationService.cs:1970-1974`). | `Attempts` is the fencing token on every transition (`ExtractionQueue.cs:561-634`); stale worker = 0-row no-op. Per-tenant in-flight cap; suspended tenants' jobs are invisible to the queue but look runnable to the sweep (`:131-139`). | Tenant: dead-letters + recover (`OperationsReadinessController.cs:144-162`), `ParseStatus = "Failed - extraction dead-lettered"` only when a worker owned the failure (`:1145-1155`). Operator: `/api/platform/pipeline/queue`. **False alarm**: `extraction-worker` readiness goes red after 30 s silence (`ExtractionWorkerHealthCheck.cs:28`) while one document over 30 s is being processed — `Beat()` is per loop iteration (`:233`). |
| 3 | Assembly | S3 write before the DB row (`EmailInquiryCaptureService.cs:160`), one `SaveChanges` for assembly+components (`:253-256`). Per component: ingestion (2 tx + 2 S3 + scan, `DocumentIngestionService.cs:166-292`) then `RecordComponentQueuedAsync` (own tx). Dies between → job without component binding, detected by `DurableJobBelongsToComponentAsync` (`EmailInquiryAssemblyCoordinator.cs:524-554`) and rescheduled (`EmailIngestEnqueuer.cs:212-228`). | R: recovery worker every 2 min, 30-min stranded window, 240-min resume window measured from capture (`EmailInquiryAssemblyRecoveryService.cs:54,67,871-874`). F: `NoInquiry`/`RejectedSecurity` absorbing (`StateMachine.cs:125-126`). L: a job-bound `FailedRecoverable` component is never auto-rescheduled (`EmailIngestEnqueuer.cs:199-210`) — held in `NeedsReview` until an audited recovery; sweep failures reach only a metric (`Worker:146-157`); illegal transitions are logged, not thrown (`:506-517`). | Unique `EmailIngestId`, `(BU, mailbox, MessageKey)`, `(BU, assembly, ComponentKey)` (`ModelBuilderExtensions.cs:37,41,91`). Loser reloads the winner (`CaptureService.cs:258-272`). | `FOR UPDATE` on the assembly before every re-evaluation (`Coordinator.cs:484-489`); `ConcurrencyVersion` token (`ConcurrencyStamp.cs:32-50`); assembler claim is CAS with `lock_timeout 5s` (`LeadAssembler.cs:185-236`). Strong. | Tenant: `/api/email-triage` state + reason; an assembly at `Extracting`/`Captured`/`FailedRecoverable` with a `Queued` ledger row is in **neither** the open nor the stopped bucket until reconciliation (60 min). Operator: metrics only. |
| 4 | Lead | Many `SaveChanges` inside one ambient transaction (`LeadIdentityApplicationService.cs:390-408`); commits with the extraction job. **Post-commit best-effort**: customer resolution and routing (`ExtractionWorker.cs:2237-2249`, `:2516-2548`; `LeadAssembler.cs:112-118`). Dies/fails after commit → Lead exists, unresolved customer, no owner. | R: routing has `RoutingReconciliationWorker`; **customer resolution has no re-run worker**. L: both failures are log lines only. | Unique `(BU, IdempotencyKey)` on occurrences (`LeadIdentityModelBuilderExtensions.cs:36`). Trap: as stage 2. | Two advisory locks (idempotency key, reconciliation scope) (`:95-102`); `Lead.IdentityVersion` token (`:124`); Serializable for human revisions (`:1302`). Strong. | Tenant: duplicate/possible-match queues; an unresolved customer looks like a normal lead. Operator: tenant operations summary. |
| 5 | Participation | Fit: 1 tx, 1 save (`LeadParticipationService.cs:96-132`). Decision: 1 Serializable tx, 2 saves incl. nested lifecycle transition (`:177-421`); guarded by "ambient tx required" (`LifecycleApplicationService.cs:144-145`). No torn write. | Synchronous; nothing retried, nothing lost; failures are 400/404/409. | Unique `(BU, IdempotencyKey)` + `(BU, RevisionId, Sequence)` (`ParticipationModelBuilderExtensions.cs:23-24,47-48`). **Trap**: same key with a corrected payload → 409 for ever (`:486-498`); a draft (`Commit=false`) consumes the key and the sequence slot (`:348`). | `Lead.LifecycleVersion` token (`LifecycleModelBuilderExtensions.cs:64`) + expected-version guards (`:507-519`) + Serializable. Loser gets 409. | Tenant: full message. Operator: nothing (no surface exists, none needed). |
| 6 | RFQ promotion | 1 Serializable tx, 4 saves: receipt (`RfqPromotionService.cs:229`), RFQ+items (`:347`), lead transition (`Lifecycle:369`), promotion event (`Lifecycle:232`). Atomic. **Object-store read inside the Serializable tx** per bid line (`:212-216`). | No worker. **Permanent trap**: receipt without its RFQ → every future promote 409s (`:384-386`, `:423-425`) and `RfqDeletionGovernance.cs:22-29` forbids the way back. `lifecycle_outbox_messages`: producer removed, consumer never existed — 71 orphans. | Unique keys on promotion, decision, RFQ (`RfqPromotionModelBuilderExtensions.cs:21-22,46-47`); partial unique `RFQ.LeadID` (`LeadConversionGate.cs:16-19`). Trap: `lead-promotion:{bu}:{lead}` is one key per lead for all time (`Lifecycle:190-192`). | Lifecycle version tokens on Lead/Rfq/Quote (`:64-68`); Serializable; `nextval` sequence on Postgres (`:432-434`). | Tenant: 400/409 with the service message; lifecycle endpoints. Operator: nothing; orphans and stuck receipts invisible to both. |
| 7 | Sourcing case | 1 Serializable tx, 3 saves (`ProcurementApplicationService.cs:122,243,261`); status set before children but same tx. | No worker: a case at `DISCOVERY_REQUIRED`/`OUTREACH_READY` sits for ever with only `NextAction` (4 + 1 in production). L: `ToCandidateViewsAsync:2501` silently drops candidates whose supplier is no longer visible; JSON parse failures swallowed (`:2540,2586,2599`). | Unique `(BU, IdempotencyKey)` + natural key `(BU, DemandLine, ShortageDecisionKey)` (`Procurement.cs:243-244`) — the one stage where a fresh key does not duplicate. Trap: discovery-event key reuse → 409 (`:297-298`). | `SourcingCase.Version` token (`Procurement.cs:242`) + expected-version guards; candidates have no token and are wholesale replaced. | Tenant only; unmapped exceptions are an opaque 500 (`ProcurementController.cs:292-298`). |
| 8 | Supplier RFQ dispatch | Enqueue: 1 tx (`:514-612`). Dispatch: claim tx (`ProcurementDispatchWorker.cs:283-337`: outbox `PROCESSING`, lease 10 min, solicitation `Dispatching`) → **SMTP** (`:128-140`) → finish tx (`:417-504`). Dies between → `PROCESSING` until lease expiry, then fenced `FAILED`/`DELIVERY_UNCERTAIN` (`:362-403`). Deliberate at-most-once. | R: `RetriableFailure` only when the provider was never invoked (`:162-169`) — and `NotificationService.cs:253-259` **swallows every SMTP exception into `Accepted=false`**, so a connection-refused becomes `DELIVERY_ACCEPTANCE_EVIDENCE_MISSING` → uncertain; the backoff path is effectively unreachable. F: uncertain, never auto-retried. D: payload/governance/sender (`:89,99,112`). L: pending rows of a non-Active tenant never scanned (`:237-242`); second fence of the same claim writes no event (`:614-621`). | Unique `(BU, SolicitationId)` on outbox (`Procurement.cs:463`); events keyed message+round+attempt+type with a shared read-then-skip gate (`:605-621`). **Trap** (fixed 2026-08-19): fence recomputes an existing key; now guarded; `DispatchRound` frees the namespace on retry (`:1071`, recovery `:118`). | No token on the outbox; `FOR UPDATE SKIP LOCKED` + lease tuple + ownership re-check (`:429-434`). Claim tx is ReadCommitted so the event gate is a non-repeatable read — two instances could double-insert; single instance today. Solicitation/case versions bumped without expected-version checks. | Tenant: workbench `DeliveryOutcome`, confirm-before-retry (PR #149, `:1059-1064`). Operator: readiness folds `FAILED` into *Pending* (`OperationsReadinessController.cs:104-107`) and recovery refuses non-dead-lettered rows — the operator has **no** view of an uncertain send. |
| 9 | Supplier quote capture | API capture: 1 tx (ReadCommitted), 1 save (`SupplierQuoteInboxService.cs:85-89`). **Document upload: 4 un-wrapped commits + 2 S3 writes** (`SupplierQuoteDocumentIntakeService.cs:96,114,130,139`). Dies after 1 → occurrence with no job/classification; after 2 → classified document, no quote; after 3 → quote never linked. Nothing reconciles. | No worker; failure is a 500 (`SupplierQuoteInboxController.cs:156-163`). L: a torn upload leaves no dead-letter and appears nowhere. | Capture: header key + unique `(BU, IdempotencyKey)` (`SupplierQuoteModelConfiguration.cs:75`). **`ReviewAsync` has no key** (`Controller:82-91`); a double-click writes two review decisions and re-opens the "newer correction" refusal (`SupplierQuoteCommercialService.cs:352`). | `supplier_quotes.Version` token (`:24`); projection fenced by expected version (`:35-36`); **review is not** (`:242` blind `Version++`) — the losing reviewer's decision is rolled back as a generic 409. | Tenant: inbox status. A torn upload is visible **nowhere**. |
| 10 | Award | Supplier award: 1 Serializable tx, 2 saves (`ProcurementApplicationService.cs:1348-1434`). Customer award: tenant advisory lock `73001` + row locks, 2 saves per command (`CustomerAwardApplicationService.cs:1325-1349,1494`). No torn write. | Synchronous. | Client-supplied key; supplier award has no unique on `(BU, RfqItemId)` (`Agent.cs:165-166` not unique) — partial awards to two suppliers are legal by design. Customer award: audit row written in the same tx, so a failed command frees its key. | Serializable + versions; customer award additionally advisory-locked. Strong. | Tenant: full 409s. |
| 11 | Customer quote | Create from RFQ: 1 tx (`QuoteService.cs:313-473`). **`UpdateQuoteAsync`: no tx, no lock, no version, one save** (`:530-657`). Revise: advisory lock + `FOR UPDATE` (`:1999-2006`). RFQ approve→quote→send: **three commits** (`RfqController.cs:559,568,637`); enqueue failure becomes a string in the 200 body (`:624-628`). | No worker. L: the below-floor manager escalation notification (`BelowFloorGuard.cs:545-550`) — hold survives, alert lost, dedup key may already be stamped. | Quote number unique (`ErpRfqAutomationContext.cs:971`), allocated by read-max-plus-one (`:493-516`). `UpdateQuoteAsync` has no key. Finalize passes a fresh GUID as the lifecycle key (`:1957`) — replay guard inert, saved only by the `SentOn` short-circuit. | **`Quote` has no concurrency token** (`ErpRfqAutomationContext.cs:956-980`): two reps editing one DRAFT is last-writer-wins, and an item another rep deleted is silently resurrected/overwritten (`:600-627`). | Tenant: send-readiness blockers (`:1668-1761`). |
| 12 | Delivery | Enqueue: advisory lock + Serializable, 1 save (`:1856-1928`). Dispatch: claim (`QuoteDeliveryStore.cs:36-74`) → PDF+SMTP (`Dispatcher:39-85`) → **now**: seal row → mark quote SENT (reconciled next cycle if it fails). Dies after send, before seal → swept uncertain after 2 min (`Store:41-52`) — genuinely uncertain, correct. Dies after seal → reconciled on restart, nothing resent. | R: pre-send failures, 8 attempts, backoff to 300 s (`Store:113`); permanent pre-send refusals dead-letter at once (`Dispatcher:215`). F: uncertain, never retried. L (still open): a tenant suspended while a row is leased keeps it leased for ever (`Dispatcher:173` drops the tenant before `ClaimAsync`, where the sweep lives); `MarkOutcomeUncertainAsync` after a 2-min-plus SMTP throws "lease expired" and abandons the rest of the batch (`Store:98`). | Fixed key `quote:{id}:delivery:v1` (`:1855`). **Trap by design**: once dead-lettered the quote number is unsendable; a revision is the way out (`:1713-1716`). Operator recovery of an uncertain row now requires confirmation (this PR). | `Version` token + lease tuple (`Store:96-99`); cross-tenant fence (`Dispatcher:197-200,261-268`). | Tenant: `DeliveryOutcome` UNCERTAIN / NOT_DELIVERED / **DELIVERED** (this PR), `DELIVERY_IN_FLIGHT`. Operator: recovery only; **no endpoint lists a quote-delivery dead-letter** — the item id must come from the DB. |
| 13 | Client PO | 1 tx, 2 saves, counter consumed in-tx (`CustomerAwardApplicationService.cs:787-819`). **Document path**: PO committed, then evidence link in a second unit of work (`CustomerPurchaseOrderDocumentService.cs:230-246`); blob written before the `Attachment` row (`:140,184`). Dies between → PO with `SourceAttachmentId` null; orphan blob. | Synchronous; a same-key retry repairs the link; nothing else does. | Header key + unique audit `(BU, CommandType, Key)`; external PO number unique. Safe. | Tenant advisory lock + `FOR UPDATE` + `EnsureVersion`. Counter row `FOR UPDATE` locks nothing on the first document of a year — the advisory lock carries it (`:1468-1494`). | Tenant: 409/400. Outbox rows (stage 19) invisible to all. |
| 14 | Order | 1 tx, 2 saves, number in-tx (`:1248-1321`). **No stock reservation is written at conversion** — reserved only at `POST /{id}/allocate` or at despatch (`OrderController.cs:54`, `ShipmentController.cs:368`); two orders can be raised against one unit and find out at the loading bay. | Synchronous. | Unique `(BU, CustomerAwardID)` and `(BU, OrderNo)` — one order per award. **Manual orders**: `GetNextOrderNumberAsync` is an unlocked max-scan and `CreateManualOrder` takes no key (`OrderRepository.cs:106-129`, `OrderController.cs:137`); a collision is an opaque 400. | Award conversion strong. `UpdateOrderAsync` last-writer-wins (`OrderService.cs:517`, `OrderRepository.cs:73`). | Tenant: 409/400; the un-reserved stock condition is invisible until despatch fails. |
| 15 | Supplier PO | 1 Serializable tx, saves at `:1528,1530,1567,1597`; number derived from identity `PO-{yyyy}-{Id:D10}` (`:1529`) — no counter, no gaps. | `procurement_handoffs` with `ExternalSystemTarget = "MANUAL"` (`ProcurementHandoffService.cs:201`) sit at `Created` until a human synchronises; no worker, no queue. | Unique `(BU, IdempotencyKey)` and number (`05_indexes.sql:4500,4507`); awards fenced from double conversion (`:1503-1505`). | Serializable everywhere; `ExpectedPurchaseOrderVersion` (`:2252`); serialisation failures translated (`:2375-2378`). | Tenant: 409. A stalled handoff appears on no queue. |
| 16 | Receipt | 1 Serializable tx, saves at `:2276,2298,2370` incl. lots and shipment settlement. Atomic. | Synchronous. | Replay by key and by receipt number; per-line movement keys (`:2229-2294`). | Serializable + PO version. | Tenant: 409/400. |
| 17 | Shipment | 1 tx covering shipment, items, history, goods issue, order flip (`ShipmentController.cs:179-282`). | Short issues throw (`:382-383`). L: `ReserveOrderAsync` swallows per-candidate shortage (`OrderStockReservationService.cs:383-386`) by design. | **Key is optional**: no header and no `ExternalId` → fresh GUID per request (`:596-608`); a double-click on an order with slack creates two shipments. **`ShipmentNo` is an unlocked max-scan with no unique index** (`ShipmentRepository.cs:157-179`; `05_indexes.sql` has only non-unique `IX_Shipments_*`); the `FOR UPDATE` is on the Order, so two orders in one tenant/month can mint the same `SHP-` number silently. | Order row lock; no token on `Shipment`; `UpdateShipment` last-writer-wins. | Tenant: typed 409s; a duplicate number would surface as an opaque 400 if at all. |
| 18 | Delivery / POD | 1 tx, 2 saves + order flip (`DeliveryConfirmationService.cs:102-274`). | Synchronous; POD captured once (`:136-138`). | Unique `(BU, IdempotencyKey)` and `(BU, Shipment)` on proofs with race recovery (`:285-297`). | Unique indexes arbitrate; no row lock on the shipment. | Tenant: 409. |
| 19 | Invoice | Draft: 1 Serializable tx (`CommercialFinanceApplicationService.cs:72-207`). Issue: 1 tx, 1 save; **number allocated by a `BEFORE UPDATE` trigger in the same tx** (`02_functions.sql:3605-3612`) — no gap, no duplicate. **No `JournalEntry` is ever written for an invoice** (`InternalSourceJournalPostingService.cs` has no receivable method): AR and GL are out of step by design. | In-request retry ×4 on 40001/40P01/unique races (`:1413-1447`). Issued is irreversible (`:400-401`, `CK_ReceivableDocuments_Issue`). `FinanceOutboxMessages` rows are written by DB triggers (`06_triggers.sql:5413`, deferred constraint trigger) and **never consumed** (`FinanceOutboxDispatcherService.cs:73`, `appsettings.json:50`); the store's dead-letter (`FinanceOutboxStore.cs:198-200`) has no recovery endpoint; the table is append-only (`06_triggers.sql:4053`). | Header key required (`Controller:229-232`); issue is state-idempotent (`:329-330`); over-invoicing blocked arithmetically at issue (`:1180-1218`). | Serializable + `FOR UPDATE` on document and order + `ExpectedVersion` (`:331-332`). Strong. | Tenant: 409/404/400. Outbox backlog and missing GL posting: invisible to both. |
| 20 | Payment | 1 Serializable tx: receipt, allocations, cash journal (`:443-535`; journal on the same context inside the ambient tx, `InternalSourceJournalPostingService.cs:181-185`); receipt number via `nexora_allocate_legal_document_number` `ON CONFLICT … RETURNING` (`20260829220000:50-56`). Atomic. | In-request retry ×4. Reversal is dual-control (`:567-568`). Outbox as stage 19. | Unique key, unique `(BU, Payment, Document)` allocation, outstanding check under lock (`:488-490`), unique journal per receipt. Safe. | Serializable + payment and document `FOR UPDATE` in id order (`:453,480-492`); allocation function fails closed on tenant scope (`:37-41`). Strong. | Tenant: 409. No finance outbox view at any level. |

## Ranked findings

**P0 — silently lost or silently wrong, tenant told the wrong thing**

| # | Finding | Evidence | Status |
|---|---|---|---|
| P0-1 | Post-acceptance bookkeeping failure recorded as "uncertain"; quote left DRAFT and editable while the customer holds the PDF; quote number burned. | `QuoteDeliveryDispatcher.cs` old 236-250; `QuoteService.cs:544-551`, `:1742-1746`, `:1855` | **Fixed** (seal first, reconcile, readiness DELIVERED, edit refused). Tests: `QuoteDeliveryDurabilityTests` 1-3, 5. |
| P0-2 | Operator dead-letter recovery of an UNCERTAIN quote delivery re-sends the quote without confirmation — breaks at-most-once from the operator console. | `PlatformDeadLetterRecoveryService.cs:126-137` (no `DeliveryOutcomeUncertain` check) | **Fixed** (`confirmedNotDelivered` required; audited). Test: `Operator_recovery_of_an_uncertain_quote_delivery_requires_confirmation_of_non_delivery`. |
| P0-3 | Six loop workers with no heartbeat: dead ≡ idle under `BackgroundServiceExceptionBehavior.Ignore`. `ProvisioningRunWorker` additionally awaited its wake-up signal outside every `try`. | `Program.cs:128`; `ProvisioningRunWorker.cs` old `:94`; matrix below | **Fixed** for the five loop workers (register + beat; wait moved inside the guard). `LegacyEvidenceMigrationHostedService` is a run-once job, off by default, logs on failure — a liveness beat would be meaningless and go red after it finishes; deliberately excluded. Tests: `BackgroundWorkerHeartbeatCoverageTests`. |
| P0-4 | Torn supplier-quote document upload: four un-wrapped commits and two object-store writes; a crash after classification leaves a classified document with no supplier quote, visible nowhere. | `SupplierQuoteDocumentIntakeService.cs:96-139`; `SupplierQuoteInboxController.cs:156-163` | Open — M. Wrap steps 2-4 in one transaction owned by the intake service (`DocumentIngestionService` already commits its own; the classification/capture/link trio share the request context). No migration needed. |

**P1 — the failure exists and only the operator (or nobody) can see it, or a definite outcome is misclassified**

| # | Finding | Evidence | Status |
|---|---|---|---|
| P1-1 | `NotificationService` swallows every SMTP exception into `Accepted=false`; the dispatch worker records that as `DELIVERY_ACCEPTANCE_EVIDENCE_MISSING` → terminal uncertain requiring human confirmation. A connection refusal — definitely not delivered — can never reach the retry path (`RetriableFailure` needs `!providerInvoked`). | `Notifications/NotificationService.cs:253-259`; `ProcurementDispatchWorker.cs:124,162-190` | Open — M. Classify pre-DATA transport failures (connect/auth/`SmtpCommandException` before `DATA`) as definite non-delivery and surface them as `RetriableFailure`; keep post-`DATA` ambiguity as uncertain. Must not be an automatic resend of a fenced-uncertain row. |
| P1-2 | Operator readiness folds `FAILED` (uncertain) supplier-RFQ rows into *Pending*; recovery refuses them. The operator cannot see an uncertain send. The tenant can (PR #149). | `OperationsReadinessController.cs:104-107`; `PlatformDeadLetterRecoveryService.cs:110-111` | Open — S but a DTO change (`QueueStatus` gains `Uncertain`); frontend consumes it. |
| P1-3 | No platform endpoint lists quote-delivery dead-letters; recovery requires an item id from the database. | `PlatformOperationsController.cs:25-91` (extraction only), `:167` | Open — S read endpoint. |
| P1-4 | `Quote` has no concurrency token; `UpdateQuoteAsync` is last-writer-wins with silent item resurrection. | `ErpRfqAutomationContext.cs:956-980`; `QuoteService.cs:530-657` | Open — needs a `RowVersion`/`xmin` column → **migration; stop**. Interim without migration: take the existing `quote-delivery:{bu}:{id}` advisory lock on update and compare `ModifiedDate` as an expected-version. |
| P1-5 | `ShipmentNo` minted by an unlocked max-scan with no unique index — silent duplicate document numbers across orders. `CreateShipment` without header or `ExternalId` gets a fresh key per request. | `ShipmentRepository.cs:157-179`; `ShipmentController.cs:596-608`; `05_indexes.sql:2519-2547` | Open — unique index is a **migration; stop**. Interim: `pg_advisory_xact_lock(hashtext("shipment-number:{bu}"))` before the scan, inside the existing transaction; make the key mandatory at the controller. |
| P1-6 | `FinanceOutboxMessages` written by triggers on every finance and O2C event, never consumed, not on any queue, no recovery endpoint, append-only. 2 rows today; grows with every invoice, payment, PO and award. The dispatcher's no-context fallback claims cross-tenant. | `06_triggers.sql:4873-5413`; `FinanceOutboxDispatcherService.cs:73,141-151`; `appsettings.json:48-50` | Open — decision needed: either enable the dispatcher with an endpoint, or add the table to readiness as a backlog gauge so the count is at least visible. Latent until an integration is configured; then the backlog goes out late and out of order. |
| P1-7 | Invoices never post to the General Ledger; only cash does. Trial balance cannot reconcile to AR ageing. | `GeneralLedger/InternalSourceJournalPostingService.cs` (no receivable method); `CommercialFinanceApplicationService.cs:318-367` | Open — design gap, not a crash. Out of Phase 1 scope unless BRD requires GL revenue posting. |
| P1-8 | Lead created; customer resolution fails post-commit; no worker re-runs it; log line only. Routing has a reconciler, resolution does not. | `ExtractionWorker.cs:2516-2522`; `EmailInquiryLeadAssembler.cs:112-118` | Open — S: add unresolved-customer leads to `RoutingReconciliationWorker`'s sweep or a sibling. |
| P1-9 | Dead-letter recovery replay of an extraction job reuses `extraction:{bu}:{job}:inquiry:{n}` (no attempt) and `EnsureReconciliationReplay` throws on divergence — a recovered job that extracts differently is poisoned for ever. | `ExtractionWorker.cs:1792`; `LeadIdentityApplicationService.cs:1970-1974`; `ExtractionDeadLetterService.cs:276-277` | Open — S: include `Attempts` (as `ExtractionRun` already does, `:1949`). |
| P1-10 | Quote delivery: a tenant suspended while a row is leased keeps the lease for ever (the sweep lives inside `ClaimAsync`, which the gate prevents); readiness reports `DELIVERY_IN_FLIGHT` indefinitely. `MarkOutcomeUncertainAsync` after a >2-min SMTP throws and abandons the rest of the batch. | `QuoteDeliveryDispatcher.cs:173`; `QuoteDeliveryStore.cs:41,98` | Open — S: run the abandoned-lease sweep before the gate filter; catch the fence failure per row (partially done in this PR for the post-acceptance path). |
| P1-11 | `extraction-worker` readiness goes red after 30 s silence while a single long document is processed. | `ExtractionWorkerHealthCheck.cs:28`; `ExtractionWorker.cs:233` | Open — S: beat from the lease-maintenance loop (`MaintainLeaseAsync`) as well. |
| P1-12 | Stranded `Pending` email ingests whose re-route keeps throwing stay `Pending` for ever — no attempt counter, and a `Pending` row is in neither triage bucket. | `EmailService.cs:1645-1652`; `EmailTriageService.cs:261-271` | Open — S: count attempts on the ingest and fence to `Failed - …` after N, which puts it in the stopped tab. |

**P2 — real, bounded, documented**

- `RfqPromotionService.cs:212-216` reads the object store inside a Serializable transaction, per line; a slow store holds the Lead row locked.
- `RfqController.cs:624-628` reports a failed delivery enqueue as a string in a 200 body; the quote exists and can be sent from its page, but nothing persists the warning.
- `BelowFloorGuard.cs:545-550` loses the manager escalation notification silently; the hold survives.
- `ReviewAsync` on supplier quotes has no idempotency key and no expected version (`SupplierQuoteInboxController.cs:82-91`).
- `SubscriptionDunningWorker` keys occurrences per invoice per UTC day; a day the worker was dead is never made up (`:40`).
- `lifecycle_outbox_messages`: 71 orphans, entity and RLS policy still live; drop the table in a later migration.
- Seven extraction jobs dead-lettered in July 2026 with "NpgsqlRetryingExecutionStrategy does not support user-initiated transactions". `SendQuoteEmailAsync`'s `ownsTransaction` branch (`QuoteService.cs:1860-1864`) has the same shape and would throw in production if ever called under an ambient transaction; today no caller does.
- `ProcurementDispatchWorker` cancelled between claim and provider call (deploy landing in that window) leaves the claim `PROCESSING` and it is fenced uncertain ten minutes later although the provider was never invoked. Rare; a release-on-cancel before `providerInvoked` would close it.

## Heartbeat matrix

Registered hosted services (`Program.cs` and the `Add*` extensions), what stalls when each dies, and liveness coverage after this PR.

| Service | Kind | Loop | Heartbeat before | After | What stalls invisibly if it dies |
|---|---|---|---|---|---|
| `EmailBackgroundService` | loop | per-mailbox interval | shared `email-poller` | same | All inbound mail. |
| `ExtractionWorker` | loop ×N | continuous | own `extraction-worker` (30 s) | same | Every extraction; leads stop appearing. |
| `EmailInquiryAssemblyRecoveryWorker` | loop | 2 min | shared (when enabled) | same | Assemblies stuck `ReadyForAssembly` after every deploy are never recovered. |
| `ProcurementDispatchWorker` | loop | 5 s | own `procurement-dispatch-worker` | same | Supplier RFQs never leave. |
| `QuoteDeliveryWorker` | loop | 5 s | own `quote-delivery-worker` | same | Customer quotes never leave; delivered quotes never reconciled. |
| `RoutingReconciliationWorker` | loop | 1 min | shared | same | Unrouted leads stay unowned. |
| `SlaSweepWorker` | loop | 5 min | shared | same | SLA breaches never raised. |
| `ReorderAlertSweepWorker` | loop | — | shared | same | Reorder alerts never raised. |
| `ScheduledReportWorker` | loop | — | shared | same | Scheduled reports never sent. |
| `AiReservationReconciliationWorker` | loop | — | shared | same | AI budget reservations never reconciled. |
| `BillingRunWorker` | loop | 6 h | shared (when enabled) | same | Nothing is invoiced. |
| **`ProvisioningRunWorker`** | loop | 5 s / signal | **none** | `tenant-provisioning` (when enabled); wait moved inside the guard | Submitted tenant executions stay `Queued` for ever. |
| **`ExtractionQueueMetricsPoller`** | loop | 15 s | **none** | `extraction-queue-metrics` (when enabled) | Queue gauges freeze at their last value, including oldest-pending age. |
| **`SubscriptionDunningWorker`** | loop | 60 min | **none** | `subscription-dunning` | Overdue notices never proposed; missed days never made up. |
| **`AccountingOutboxDispatcher`** | loop | 10 s | **none** | `accounting-outbox` | Accounting export rows sit leased; nothing reaches the accounting system. |
| **`FinanceOutboxDispatcherService`** | loop | 5 s | **none** | `finance-outbox` (when enabled; disabled in production) | Finance events written and never published. |
| `LegacyEvidenceMigrationHostedService` | run-once | — | none | none (deliberate) | Legacy blobs stay on the old store; job is off by default, idempotent, logs on failure. |
| `MalwareScannerStartupProbe`, `OcrEngineStartupProbe`, `PlatformMfaPolicyStartupGuard`, `OutboundEmailSettingsWarmup`, `ModuleCatalogStartupService`, `TenantReferenceListStartupReconciler` | startup `IHostedService` | — | n/a | n/a | An exception in `StartAsync` aborts host start (not covered by `Ignore`); fail-loud by construction. |

## Changes in this PR

1. **`QuoteDelivery/QuoteDeliveryDispatcher.cs`** — after provider acceptance: seal the ledger row
   (`CompleteAsync`) first, then mark the quote SENT; a failure of the second step is logged and
   retried by `ReconcileDeliveredQuotesAsync` on the next visit, never recorded as uncertain; a
   failure to seal is fenced uncertain with its error code, and a failure to fence no longer
   abandons the batch. Tenant discovery now also visits tenants with a sealed-but-unfinalized
   delivery. The catch-up is skipped only in compositions with no `IQuoteService`.
2. **`Services/QuoteService.cs`** — `ReconcileDeliveredQuotesAsync` (derives `SentOn` from the
   ledger; defers a persistently failing quote on the row with `SentNotFinalized:<code>` and a
   5-minute `AvailableOn`); send-readiness reports `DeliveryOutcome = "DELIVERED"` with blocker
   `DELIVERY_STATUS_PENDING` for a sealed-but-unfinalized delivery; `UpdateQuoteAsync` refuses
   edits when any delivery row is sealed (delivered) or fenced uncertain, in addition to in-flight.
3. **`Platform/Operations/PlatformDeadLetterRecoveryService.cs`** — `RecoverPlatformDeadLetterCommand`
   gains `ConfirmedNotDelivered` (default `false`); recovery of a quote delivery whose
   `LastErrorCode` starts with `DeliveryOutcomeUncertain` is refused without it; the flag is
   written to the audit metadata.
4. **Heartbeats** — `BackgroundWorkerNames` gains five names; `ProvisioningRunWorker`,
   `ExtractionQueueMetricsPoller`, `SubscriptionDunningWorker`, `AccountingOutboxDispatcher` and
   `FinanceOutboxDispatcherService` register in their constructors (disabled ones register
   nothing, per the `BillingRunWorker` rule) and beat once per loop turn whatever the sweep's
   outcome. `ProvisioningRunWorker`'s `_signal.WaitAsync` is inside the guarded region.

No EF migration. No new columns. No change to at-most-once for supplier RFQs or quote
deliveries — nothing is resent automatically anywhere.

## Verification

- `QuoteDeliveryDurabilityTests` (6): with the dispatcher, `QuoteService` and the recovery gate
  reverted to `main`, all six fail (row dead-lettered `DeliveryOutcomeUncertain`; quote never
  SENT; recovery proceeds blind; edits accepted). With the fix, all six pass.
- `BackgroundWorkerHeartbeatCoverageTests` (9): with every `Register`/`Beat` replaced by a no-op,
  7 fail; the two that pass either way are the "disabled registers nothing" controls — which is
  what proves the class is not simply always-red.
- Neighbouring suites (`QuoteDeliveryTests`, `QuotePriceAttestationTests`, `QuoteSendReadinessTests`,
  `PlatformDeadLetterRecoveryTests`, `TenantWorkGateSuspensionTests`, `FinanceOutboxDispatcherTests`,
  `PlatformObservabilityMetricsTests`, `BillingRevenueIntegrityTests`, `ProcurementDispatchWorkerTests`,
  `QuoteReadProjectionFieldCarriageTests`, `BackgroundWorkerHeartbeatTests`): 205/205.
- Full backend suite: see the PR.

## Designs that need a migration — stopped, not built

- `Quote` optimistic concurrency (P1-4): a `RowVersion` or mapped `xmin` on `Quotes`.
- `Shipments.ShipmentNo` uniqueness (P1-5): `UX_Shipments_BU_ShipmentNo`.
- `lifecycle_outbox_messages` removal (P2).

## Residual risk after this PR

- A process death between SMTP acceptance and the seal is still reported as uncertain. That is
  the honest state: the ledger cannot prove the send. The rep is told to check with the customer
  and the operator must confirm non-delivery before any resend. The window is one `SaveChanges`
  wide; on the single Render instance it is the deploy stop.
- The supplier-RFQ path keeps its deliberate at-most-once and its P1-1 misclassification of
  definite transport failures as uncertain.
- The finance outbox keeps accumulating until a decision is made (P1-6).
- Two operators editing the same DRAFT quote remain last-writer-wins until P1-4 ships.
