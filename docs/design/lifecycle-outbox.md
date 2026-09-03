# Lifecycle outbox: retire the producer

Stream 4, item C. Status: designed 2026-09-02, implemented in the same PR.

## Problem

`lifecycle_outbox_messages` holds rows nobody reads. Production on 2026-09-02:

| EventType | rows | pending | AttemptCount max | first | last |
|---|---|---|---|---|---|
| commercial-case.lead.statustransitioned | 65 | 65 | 0 | 2026-08-18 | 2026-08-29 |
| commercial-case.lead.promoted-to-rfq | 4 | 4 | 0 | 2026-08-28 | 2026-08-29 |
| commercial-case.quote.statustransitioned | 2 | 2 | 0 | 2026-07-28 | 2026-07-28 |

`AttemptCount = 0` on every row means `ILifecycleOutboxStore.ClaimAsync` has never run. A
queue that is never claimed is indistinguishable from an idle one — failure shape 5 in the
design-review checklist — and every audit of the estate will keep flagging it.

## Current mechanism (file:line)

- Writers: `CommercialCases/Lifecycle/LifecycleApplicationService.cs:255-268`
  (promotion) and `:420-431` (status transition / reopen). Each appends one
  `LifecycleOutboxMessage` 1:1 with the `CommercialLifecycleEvent` it just wrote.
- Store: `CommercialCases/Lifecycle/LifecycleOutboxStore.cs` — `ClaimAsync` / `CompleteAsync`
  / `FailAsync` with lease fencing and dead-lettering. Registered at `Program.cs:426`. Its
  only callers are two unit tests (`LifecycleApplicationServiceTests.cs:466-500`).
- Consumers: **none.** Grep for the event-type literals, `ILifecycleOutboxStore`,
  `LifecycleOutboxMessages` and "outbox" across `Sla/`, `Metrics/`, `Notifications/`,
  `CommercialCases/`, `CommercialLearning/` finds no reader. `Sla/SlaSweepWorker.cs` computes
  from lead/quote rows directly. The comment at `LifecycleApplicationService.cs:165-170`
  describes a consumer that "routes on this literal string" in the conditional tense; it was
  never written.
- Related: `FinanceOutboxMessages` has 2 rows pending since 2026-08-05 with the same shape.
  That one HAS a dispatcher (`CommercialFinance/FinanceOutboxDispatcherService.cs`) that
  publishes to an external HTTP endpoint and is opt-in by configuration; it is not enabled in
  production. Out of scope here; reported.

## Decision

**Delete the producer, not build a consumer.** A dispatcher modelled on
`Procurement/ProcurementDispatchWorker.cs` would lease each row, hand it to a consumer list
that is empty, and mark it processed. That converts "nobody reads this" into "processed
successfully" — a silent wrong answer, the one failure the review checklist calls
unsurvivable. There is no event subscriber in Phase 1 (BRD v3.0 ceiling) to hand the
events to, and inventing one is a feature, not hardening.

The durable record survives untouched: `commercial_lifecycle_events` is the append-only
audit of every transition and promotion, and `LifecycleTransitionResult` still returns the
event. Only the second copy in the outbox stops being written.

Concretely:

1. Remove the two `LifecycleOutboxMessages.Add(...)` blocks and the payload serialisation that
   only feeds them.
2. Delete `ILifecycleOutboxStore`/`LifecycleOutboxStore` and the DI line at `Program.cs:426`.
3. Keep the `LifecycleOutboxMessage` entity, its mapping
   (`LifecycleModelBuilderExtensions.cs:44-60`) and the table. Removing the entity would be a
   model change with no migration and this stream may author only one; dropping the table is
   one line in the next squash. The entity is marked retired in code.
4. Tests: update the assertions that counted outbox rows; delete the two store tests; add a
   regression test that a transition and a promotion write zero outbox rows.

## Failure modes considered

- **Silent queue** (the defect): removed by removing the queue.
- **Two sources of truth**: the outbox payload duplicated the event row; one remains.
- **Grants**: the tenant role's INSERT on `lifecycle_outbox_messages`
  (`RfqTenantRoleCreatePostgreSqlTests.cs:229` proved it) is no longer exercised; the grant
  stays, harmlessly, until the table is dropped.
- **Production rows**: the 71 pending rows stay in the table; nothing polls them. The next
  squash drops the table and them with it. No migration needed now.

## Tests

- `LifecycleApplicationServiceTests`: transition/promotion produce exactly one
  `CommercialLifecycleEvent` and zero `LifecycleOutboxMessages` (fails on revert — the old code
  writes one).
- `RfqTenantRoleCreatePostgreSqlTests`: the assertion on the outbox row is replaced by one on
  the lifecycle event, which is what the tenant role must be able to write.

## Rollout / rollback

No config, no migration. Rollback is a code revert.
