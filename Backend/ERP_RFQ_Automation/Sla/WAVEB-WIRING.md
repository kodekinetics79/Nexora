# Wave B wiring — WP-B3 (below-floor approval + escalation clock) & WP-B4 (passive metrics + quote revisions)

Everything in this wave compiles and runs **without** any Program.cs change; the
two splice lines below only *activate* the approve path for below-floor holds and
the passive metric writes. No `dotnet ef migrations add` was run — the lead
generates the migration from the model configuration listed below.

---

## 1. Registration lines for the lead to splice into Program.cs

```csharp
// WP-B3: executor for below-floor holds — the approvals inbox approve endpoint
// re-invokes it via the tool registry. Place next to the other Intelligence tool
// registrations (after AddPricingIntelligence()).
builder.Services.AddScoped<ERP_RFQ_Automation.Agent.IAgentTool,
                           ERP_RFQ_Automation.Intelligence.Pricing.ApproveBelowFloorQuoteTool>();

// WP-B4: append-only passive metric writer (never throws; writes on its own DI
// scope). Singleton — it only holds IServiceScopeFactory + a logger.
builder.Services.AddSingleton<ERP_RFQ_Automation.Metrics.IMetricRecorder,
                              ERP_RFQ_Automation.Metrics.MetricRecorder>();
```

Already wired without a splice (registered inside `AddPricingIntelligence()`,
which Program.cs already calls):

```csharp
services.AddScoped<IBelowFloorGuard, BelowFloorGuard>(); // detection + hold creation
```

Behaviour **before** the splice lands (all consumers take the new services as
optional constructor parameters defaulting to `null`):

* Without `IMetricRecorder`: all three metric hooks are silent no-ops.
* Without the tool registration: holds are still created and visible in the
  Approvals inbox, but approving one returns the existing
  "Tool 'approve_below_floor_quote' is no longer registered" failure — so splice
  the tool line first.

## 2. Columns for the lead's migration

### New table `MetricEvents` (config: `Models/ErpRfqAutomationContext.Metrics.cs`, entity: `Metrics/MetricEvent.cs`)

| Column         | Type                        | Notes                                            |
|----------------|-----------------------------|--------------------------------------------------|
| Id             | bigint identity, PK         |                                                  |
| BusinessUnitId | bigint, not null            | tenant scope (fail-closed global query filter)   |
| Type           | varchar(60), not null       | "pricing_applied" \| "extraction_corrected" \| "outcome_recorded" |
| EntityId       | bigint, null                | rfqId / leadId / quoteId                         |
| PayloadJson    | jsonb, not null (default "{}" from code) | event payload, shapes below            |
| CreatedOn      | timestamp, default `now()`  |                                                  |

Index: `IX_MetricEvents_BU_Type_CreatedOn` on (BusinessUnitId, Type, CreatedOn). Append-only — no uniques.

### `Quotes` — two new columns (config: same partial, entity: `Models/Quote.Revision.cs`)

| Column            | Type                     | Notes                                                        |
|-------------------|--------------------------|--------------------------------------------------------------|
| RevisionOfQuoteId | bigint, null             | loose self-reference to Quotes.Id — **no FK/nav by design** (same convention as OutcomeReasonId) |
| RevisionNo        | int, not null, default 1 | 1 = original, 2 = first revision, …                          |

Index: `IX_Quotes_RevisionOfQuoteId` on RevisionOfQuoteId.

No other schema changes. (AgentApprovals/AgentAuditLogs/SlaEvents are reused as-is — WP-B3 builds **no** new approval entity.)

## 3. WP-B3 — how the below-floor flow hangs together

* **Detection + hold** (`Intelligence/Pricing/BelowFloorGuard.cs`):
  * `PricingIntelligenceController.ApplyPricingAsync` — before applying, floors are
    recomputed via `IPricingEngine.PriceRfqAsync`; any requested line price `<` its
    `FloorUnitPrice` (floor ≤ 0 ⇒ "no floor", never blocks) parks the request as an
    `AgentApproval` (ToolName `approve_below_floor_quote`, Status Pending, BU +
    requester stamped, audit row "Held") and returns **409**
    `{ queuedForApproval, approvalId, summary, message, lines }`.
  * `QuoteService.SendQuoteEmailAsync` — same check against the quote's *current*
    line prices (mapped to the linked RFQ's floors via `QuoteItem.RfqitemId`);
    when below floor **nothing is emailed / no SENT stamp**, and the new
    `QuoteSendResult` says `Held` (QuoteController turns that into the same 409;
    RfqController.Approve surfaces it via `emailWarning`).
  * `InputJson` contract: `{ holdType: "apply_pricing"|"send_quote", rfqId?, quoteId?,
    recipientEmail?, customSubject?, customBody?, lines: [{ rfqItemId, unitPrice,
    floorUnitPrice, delta }] }`. Summary example: `Quote #QT-0725-0003: 3 line(s) below floor by up to 12%`.
* **Execution on approve** (`Intelligence/Pricing/ApproveBelowFloorQuoteTool.cs`):
  the existing `[RequireManagerRole]` approve endpoint re-invokes the tool with the
  stored input. `apply_pricing` → applies the stored line prices through the engine
  (BU-checked with the approver's tenant); `send_quote` → calls
  `SendQuoteEmailAsync(..., QuoteSendOptions { BypassFloorHold = true })` so the
  approved send cannot re-hold itself. `IsMutation = true`, but the tool is only
  reachable via the approvals path; if the model ever called it, the guardrail's
  unknown-mutation fail-safe would demand approval anyway.
* **Escalation clock** (`Sla/SlaSweepWorker.SweepPendingApprovalsAsync`):
  a pending approval escalates at
  `min(CreatedOn + policy.ApprovalEscalationHours, BidClosingDate − policy.DeadlineBufferHours)`
  — the deadline term applies when the hold resolves to an RFQ with a real
  (year ≥ 2000) bid-closing date (via rfqId, or quoteId → Quote.Rfqid). Recipient
  is the **requester's manager** (`Users.ManagerId`, requester resolved by id then
  email), falling back to the tenant's managers/admins. Dedup: one SlaEvent
  `("approval", guid-derived key, "escalated")` per approval, ever.
* **Deadline already inside the buffer at creation**: `BelowFloorGuard`
  immediately notifies the requester **and** their manager and stamps the same
  SlaEvent, so the sweep never double-fires.

## 4. WP-B4 — metric payload shapes (PayloadJson)

* `pricing_applied` (EntityId = rfqId; hook: `PricingIntelligenceController`, reuses
  the floor-check preview — no extra pricing pass):
  `{ rfqId, lines: [{ rfqItemId, recommended, applied, delta }], totals: { recommendedTotal, appliedTotal, appliedLines } }`
* `extraction_corrected` (EntityId = leadId; hook: `LeadRepository.SubmitLeadReviewAsync`,
  diff computed against stored values *before* the upsert):
  `{ leadId, action, headerChanged: ["rfqno", …], itemsAdded, itemsRemoved, itemsChanged, itemFieldChanges: { field: count } }`
  (only written when something actually changed)
* `outcome_recorded` (EntityId = quoteId; hooks: `QuoteOutcomeService.SetOutcomeAsync`
  + system `ExpireAsync`):
  `{ quoteId, quoteNo, revisionNo, outcome, reasonCode, cycle: { recDate, sentOn, outcomeOn, recToSentDays, sentToOutcomeDays, recToOutcomeDays }, decisionBriefRecommendation? }`
  — `decisionBriefRecommendation` comes from `ILeadDecisionService.GetSummariesAsync`
  (its cheap batch path) and is omitted on any failure / missing lead.

## 5. WP-B4 — revisions-lite state machine

* `POST /api/Quote/{id}/revise` (`[RequireModulePermission("Quotations", Create)]`) →
  clones a **non-DRAFT** quote + items as a new DRAFT (`RevisionNo + 1`,
  `RevisionOfQuoteId` link back, QuoteNo `…-R2`, customer/RFQ refs copied, totals
  recalculated). 409 (`{ message }`) when: still a draft / already superseded /
  chain locked. DRAFT status resolved via SetupMaster `QuoteStatus` + code (legacy
  fallback), like everything else.
* **Chain lock**: an outcome on *any* chain member closes the chain —
  `SetOutcomeAsync` on a superseded member → `InvalidOperationException` → 409
  ("superseded by Rev N — record the outcome on the latest revision");
  further `revise` on a closed chain → 409; the sweep's `ExpireAsync` silently
  skips superseded quotes (returns false).
* `GET /api/Quote/{id}/revisions` → `{ quoteId, quoteNo, revisionNo,
  revisionOfQuoteId/No, supersededByQuoteId/No, chainLocked, canRevise }` — a
  separate endpoint on purpose, so the concurrently-owned `QuoteRepository`
  list/detail mappings did not need touching.

## 6. Frontend touch-points

* `src/pages/Copilot/humanize.ts`: `approve_below_floor_quote` → 💰 "Approve
  below-floor pricing" / "approve this below-floor quote" (Approvals inbox card).
* `src/api/services/quoteService.ts`: `sendEmail` now resolves
  `{ held, message, approvalId }` on the 409 queued response; new `revise` /
  `getRevisionInfo`.
* `src/pages/Sales/Quotes/QuoteViewPage.tsx`: Email button wired through
  `EmailPromptDialog`; info alert "Sent for approval — pricing is below your
  floor. Track it in Approvals." (with an Open Approvals action) on a held send;
  "Rev N · replaces Q-…" and "Superseded by Q-…" chips; Revise button when
  `canRevise`.
* `src/pages/Sales/Quotes/QuotesPage.tsx`: per-row Revise action on non-draft,
  non-ordered quotes (navigates into the new draft).
* `src/pages/Intelligence/RfqPricingPage.tsx`: apply-pricing 409 → info snackbar
  instead of an error.

## 7. Verification

* `dotnet build` — 0 errors (remaining warnings are pre-existing, none in the new files).
* `dotnet test` — 79 passed / 0 failed.
* `npm run build` (tsc + vite) — clean.
