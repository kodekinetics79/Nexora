# Overnight Execution Report — RFQ → Inventory/ATP → Supplier Sourcing

**Date:** 2026-08-06 · **Repo:** `Nexora-main`, `release/nexora-v2-v3-accelerated`
`RFQ-Automation-Vite` not opened or modified. Nothing deployed. Nothing pushed. **Zero emails sent.**

---

## 1. Final decision

> ## RFQ → INVENTORY/ATP → SUPPLIER SOURCING → SUPPLIER OFFER → CUSTOMER QUOTE READINESS: **NO-GO**

Not because the work failed — because the assignment's **own Phase 1 gate (§6) is not met**, and
because the safety control its §2 and §12 require **did not exist**. Both facts are load-bearing
and neither was known when the assignment was written.

`CONDITIONAL GO` was considered and rejected: it requires that "inventory and internal supplier
sourcing work" *and* "test emails and response ingestion work" as proven capabilities. Neither has
been exercised end to end, and supplier reply correlation is genuinely absent.

---

## 2. The finding that changes the assignment

A bounded reuse map (one focused block, per §7) found that **roughly 80–85% of the requested
overnight build already exists and is wired.** The assignment is scoped as a build of a system
Nexora already owns.

| Capability the assignment asks to build | Reality | Anchor |
|---|---|---|
| ATP: on-hand + qualifying incoming − reservations | **EXISTS, and is already computed per RFQ line** | `InventorySnapshot.AvailableToPromise` (`Inventory/Commercial/CommercialInventoryEntities.cs:136-146`) = OnHand − Reserved − Allocated − Quarantine − Damaged − Expired − SafetyStock |
| Fulfilment classification | **EXISTS** — 6 classifications | `CommercialInventoryServices.cs:227-248` (`KnownInStock`, `KnownIncoming`, `KnownShortage`, `UnknownProduct`, `PossibleMatchReview`, `NonInventoryService`) |
| Projected shortage + expected availability date | **EXISTS** | `CommercialInventoryServices.cs:200-221` |
| Multi-warehouse fulfilment routing | **EXISTS** | `FulfilmentRoute`, `CommercialInventoryServices.cs:104-150` |
| Stock ledger + immutable movements + reservations | **EXISTS** | `StockLedgerService.cs:23-85`; `StockReservation.cs:27-61` |
| Sourcing Case with lifecycle | **EXISTS** — 15 statuses | `Procurement/ProcurementEntities.cs:47-80`, statuses `:3-20` |
| Supplier candidates + solicitations | **EXISTS** | `SourcingCaseCandidate:83-98`; `SupplierSolicitation` (`Agent/Models/SourcingEntities.cs:24-61`) |
| Durable outbox + leased dispatch worker | **EXISTS**, with dead-letter, retry, lease fencing, message-id capture | `ProcurementOutboxMessage:209-230`; `ProcurementDispatchWorker.cs:13,264-350,391-461` |
| Supplier RFQ email template | **EXISTS** | `rfq-to-supplier`, `Notifications/Templating/EmailTemplates.cs:25` |
| Supplier quote inbox + field evidence + review | **EXISTS** | `Controllers/SupplierQuoteInboxController.cs:12-97` |
| Offer comparison + weighted scoring | **EXISTS** | `Agent/Sourcing/SupplierScoring.cs:56-72`; `SourcingTools.cs:170-345` |
| **Split award across suppliers** | **EXISTS** | `ProcurementApplicationService.cs:1175-1211` (`SPLIT_APPROVED`) |
| Purchase + prior-quotation history per part | **EXISTS** | `ProcurementApplicationService.cs:1696-1727` |
| LLM provider abstraction | **EXISTS** | `Agent/Llm/IAgentLlm.cs:82` |

**Genuinely absent — three things, and only three:**

1. **Inbound supplier reply correlation.** Thread continuation is *detected*
   (`EmailTriageService.cs:238`) but never joined to `SupplierSolicitation` or
   `ProcurementOutboxMessage.ProviderReference`. `SupplierQuoteInboxService.cs:40` requires a
   human to supply `SupplierSolicitationId`. This one missing link is what makes the
   outbound→inbox loop manual.
2. **External AI supplier discovery.** `SourcingCaseStatuses.DiscoveryRequired` exists with no
   `IWebSearch` abstraction and no feature-flag service behind it.
3. **Supplier qualification data** — no brand/manufacturer authorization, no supplier tiers —
   **and no outbound recipient allow-list or test sink.**

**Architecture decision.** New sourcing work must call
`CommercialLineResolutionApplicationService` + `LeadLineCommercialResolutionService`, not
reimplement ATP beside them. Building a second inventory or sourcing subsystem was the principal
risk in this assignment and it was avoided.

---

## 3. Phase 0 — baseline protection: **GATE MET**

Five lanes, all executed this session, all green. Full detail in `overnight-test-evidence.md`.

| Lane | Result |
|---|---|
| Backend non-PostgreSQL | Failed 0 · Passed 2064 → **2079** after this session's work · Skipped 0 |
| Backend PostgreSQL (real, Testcontainers) | Failed 0 · Passed 312 · Skipped 0 — re-run green after every change |
| Frontend typecheck | exit 0 |
| Frontend build | exit 0 |
| Frontend vitest | 14 files · 216 passed · 0 failed |

`git diff --check` clean. Safety patch (237,733 B) saved outside the repository. Expected
baseline of 2064/312 reproduced **exactly** — no test disappeared, no count inflated.

---

## 4. Phase 1 — base journey gate: **NOT MET**

Three of eight prerequisites are unimplemented, two are backend-only with no UI, and the browser
proof has never existed — `docs/lead-ingestion-pilot/base-journey-browser-result.md`, referenced
by the assignment, **is not in the repository.**

The decisive one: **`Rfq` has no owner column at all.** Conversion never reads `Lead.AssignTo`.
"Named Sales Owner" is not a configuration gap; there is nothing to write to. Every downstream
phase — sourcing case buyer, authorised supplier-RFQ sender, responsible quote engineer —
inherits an owner that does not exist.

Per the assignment's own §6, Phases 3–11 were therefore **not started**.

---

## 5. What was built — the missing safety prerequisite

§2 and §12 mandate an outbound domain/address allow-list and a sink before any overnight supplier
email. **Neither existed.** `NotificationsOptions` had no such concept; the only thing between a
rehearsal run and a real buyer's inbox was whichever address sat on the supplier record. A
supplier RFQ sent by accident is not a bug that can be rolled back — it is a commercial
communication from Nexora to a third party.

`Notifications/OutboundEmailGuard.cs` — a **decorator** over `IEmailSender`, registered as the
*only* `IEmailSender` in DI, so containment cannot be bypassed and a fourth transport added later
is wrapped by construction. The durable outbox, the leased worker and the retry semantics are
untouched.

| Mode | Behaviour |
|---|---|
| `Live` (**default**) | No constraint. Binding the section changes nothing for an existing deployment. |
| `AllowListOnly` | Fails **closed** on the whole message if any single recipient is unlisted. Domain matching rejects suffix attacks. |
| `Redirect` | Rewrites To to the sink, **clears Cc and Bcc** (a surviving Bcc is exactly how a real address slips through a rehearsal), tags the subject `[NEXORA TEST]`. Refuses if no sink is configured — never silently falls back to Live. |
| `DraftOnly` | Transmits nothing. |

A real transport with no containment now **warns by name** at startup. 15 tests.

**Anti-spaghetti record:** no new pipeline, no new worker, no new options section, no changes to
any provider. An `EmailMessage.Headers` field was considered for recording suppressed recipients
and **rejected** — no provider transmits headers, so it would have been dead surface; the Warning
log carries the evidence instead.

---

## 6. Exact changes

**New:** `Notifications/OutboundEmailGuard.cs` · `ERP_RFQ_Automation.Tests/OutboundEmailGuardTests.cs`
**Modified:** `Notifications/NotificationsOptions.cs` · `Notifications/NotificationsServiceCollectionExtensions.cs`
**Migrations added this session:** none (the participation/quote-number migration was created earlier in this program and remains unapplied to production)
**Local commits:** none — see `overnight-test-evidence.md` for why, and the recommendation.

---

## 7. Golden three-line scenario

**Not executed.** It requires the Phase 1 gate (owner, warning acknowledgement, line-participation
UI), a running SMTP sink and a browser harness — none of which are in place. Fabricating a result
for Line A / B / C would be the single most damaging thing this report could contain.

---

## 8. Consultant findings

Bounded read-only reviews were run for the reuse map and, earlier in this program, for RFQ/CRM
domain integrity and SDET/security. Findings are folded into §2 and into
`overnight-remaining-blockers.md`. The inventory/ATP, procurement, AI-discovery and email-SME
challenges described in §21 of the assignment were **not run**, because they are challenges
*against an implementation* and no Phase 3–11 implementation exists to challenge.

---

## 9. Recommended next slice — one only

> **Make ownership survive Lead → RFQ conversion, and make critical warnings block it.**

Small, backend-first, and it is the Phase 1 gate. Until an RFQ can name its owner, a sourcing
case has no buyer, a supplier RFQ has no authorised sender, and a quote has no accountable
engineer.
