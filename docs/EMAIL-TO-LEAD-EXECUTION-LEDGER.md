# Email → Lead — Cumulative Execution Ledger

Single source of truth for this bounded program. Update it in the same commit as the work it
records. Do not add a second planning document.

**Branch:** `fix/enterprise-email-lead-participation`
**Audited base:** `1601db6ad9e19655118ae5a051e1cba6b1649d34` (= `origin/main` at takeover)
**Repository:** `kodekinetics79/Nexora`

---

## Phase Zero — baseline verified at `1e69acd0978911edf091754bd2eb6142b7ca68b8`

Run at that exact SHA, clean worktree, 11 commits ahead of base:

| Check | Result |
| --- | --- |
| Solution build | **0 errors** |
| Complete backend suite (incl. PostgreSQL via Testcontainers) | **Failed: 0, Passed: 4816, Total: 4816** (12m27s) |
| PostgreSQL-tagged subset | **Failed: 0, Passed: 447** (1m37s) |
| Frontend Vitest | **698 passed / 65 files** |
| `tsc --noEmit` | **exit 0** |
| Model drift | `No changes have been made to the model since the last migration.` |

The previously reported 4816 was measured at `244f4a9`. It has now been **re-measured at
`1e69acd`** and is current. Nothing was red; no repair was required before proceeding.

---

## Requirement ledger

Status values: `DONE` (wired, tested, committed) · `PARTIAL` (built, not wired) ·
`TODO` · `BLOCKED`.

SME review column records the *independent* reviewer. The implementer's own statement is not
approval — an entry of `—` means no independent review has happened yet.

| # | Requirement / gate | Status | Focused tests | Integration tests | SME | Commit | Remaining risk |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 0 | Evidence matrix of the current pipeline | DONE | n/a | n/a | — | `ae6a376` | Cited at base SHA; re-verify claims before relying on them |
| 1 | `EmailInquiryAssembly` + `EmailInquiryComponent` aggregate, migration, RLS, grants | DONE | 42 | 447 PG suite green | — | `a7cfd8e` `64ceb07` `5afe445` | — |
| 2 | Assembly barrier state machine | DONE | 73 (with #6) | n/a | — | `a7cfd8e` `ee7e03a` | Not yet consumed by any production path |
| 3 | Empty message ≠ completed inquiry (`NoInquiry`) | DONE | incl. in 73 | n/a | — | `3a36d22` | — |
| 4 | Sequence grants `USAGE` only; purge reach; policy census | DONE | n/a | 447 PG green | — | `5afe445` | — |
| 5 | Partial evidence: unread commercial attachment ⇒ `NeedsReview` | DONE | incl. in 73 | n/a | — | `ee7e03a` | Inline-asset classifier is conservative by design; may over-review |
| 6 | Unsafe transitions removed; amendment ⇒ new ingest/assembly | DONE | incl. in 73 | n/a | — | `ee7e03a` | — |
| 7 | Canonical MIME planner (`EmailInquiryManifestPlanner`) | DONE | 24 | n/a | — | `5ea2cd3` `ee7e03a` | Sole authority only once #12 lands |
| 8 | Capture service (raw `.eml` → object storage, manifest persistence) | PARTIAL | 0 direct | n/a | — | `e7a0760` | **Not wired.** `SafeToMarkSeen` conflates capture with acknowledgement — see #13 |
| 9 | Test Connection uses the poller login identity | DONE | 77 | n/a | — | `244f4a9` | Pinned by **source-text** assertion — must become behavioural, see #19 |
| 10 | PR #26 storage-failure contract cherry-picked whole | PARTIAL | 79 | n/a | — | `151f6ca` | Not reconciled against PR #26's *current* head — see #20 |
| 11 | Assembly coordinator (single writer after capture) | PARTIAL | 0 | n/a | — | `1e69acd` | **Not wired.** Illegal transition is logged and swallowed — see #14 |
| 12 | Collapse fan-out: enqueuer → thin manifest adapter; delete `message.Attachments` walk | TODO | — | — | — | — | **Blocking.** Two MIME walks disagree; wiring as-is stalls every message at the barrier |
| 13 | `CaptureComplete` vs `SafeToAcknowledge` split | TODO | — | — | — | — | Acknowledging on capture alone can lose a message whose scheduling failed |
| 14 | Coordinator returns persisted state, never an unpersisted calculation | TODO | — | — | — | — | A caller could create a Lead from a `ReadyForAssembly` that was never committed |
| 15 | Durable idempotent scheduling (DB-enforced job uniqueness, attempt identity) | TODO | — | — | — | — | `ExtractionJobId != null` alone is not idempotency |
| 16 | Ownership tuple on every email job + validation before accepting a result | TODO | — | — | — | — | Cross-tenant result acceptance is currently possible in principle |
| 17 | DB-enforced tenant integrity: assembly→ingest, assembly→config, component→assembly | TODO | — | — | — | — | **`EmailIngest` has no `BusinessUnitId`** — needs column + safe backfill first |
| 18 | Deterministic bounded message identity + DB uniqueness | TODO | — | — | — | — | Fallback digest inherited from `ResolveIngestKey`, untested at the assembly boundary |
| 19 | Behavioural Test Connection proof (injected resolver, observed auth args) | TODO | — | — | — | — | Replaces the brittle source-text test added in `244f4a9` |
| 20 | PR #26 reconciliation vs its final head + reuse/exclude matrix | TODO | — | — | — | — | Cherry-pick may be superseded |
| 21 | End-to-end nested MIME limits (one shared budget across the tree) | TODO | — | — | — | — | Current test only proves `MaxNestingDepth = 0` |
| 22 | Direct capture-service tests (outage, crash, replay, race, hash corruption) | TODO | — | — | — | — | Capture has **zero** direct tests today |
| 23 | Poller wiring (Milestone 2) | TODO | — | — | — | — | Depends on #12–#16 |
| 24 | Retire legacy direct-email-to-Lead path | TODO | — | — | — | — | `EmailIngestCrossTenantDuplicatePostgreSqlTests` drives the legacy method directly |
| 25 | Worker barrier + one-message-one-Lead (Milestone 3) | TODO | — | — | — | — | The headline defect is still live in production code |
| 26 | Recovery + retention (Milestone 4) | TODO | — | — | — | — | Scope fixed: 1/7/30/90/180/365, default 7, terminal assemblies only |
| 27 | UI: Poll Now, Email Intake, Lead provenance (Milestone 5) | TODO | — | — | — | — | Nothing truthful to display until #23/#25 |
| 28 | Automated browser + live GoDaddy acceptance (Milestone 6) | TODO | — | — | — | — | Needs user credentials via the app's encrypted secret mechanism |

---

## The blocking defect (#12) — read this first

There are currently **two independent MIME traversals** that disagree:

* `EmailInquiryManifestPlanner` walks `message.BodyParts` with explicit candidate selection
  ([`EmailInquiryManifestPlanner.cs`](../Backend/ERP_RFQ_Automation/Ingestion/Assembly/EmailInquiryManifestPlanner.cs), the `candidates` line).
* `EmailIngestEnqueuer.EnqueueAsync` walks `message.Attachments`
  ([`EmailIngestEnqueuer.cs`](../Backend/ERP_RFQ_Automation/Ingestion/Triage/EmailIngestEnqueuer.cs)).

`Attachments` yields only entities whose `Content-Disposition` is `attachment`. An embedded
`message/rfc822` from Outlook or Gmail commonly has no disposition header, so it is **invisible
to the enqueuer and visible to the planner**. The two therefore produce different parts, in
different order, and would mint different `ComponentKey`s.

**Wiring them as they stand would make every message wait at the barrier forever for a
component that was never scheduled** — a total ingestion stall that renders as "processing" on
screen. That is worse than the defect being fixed, which is why the poller wiring was reverted
rather than completed.

Approved resolution (product owner, this program): collapse onto the planner as the only walk.

---

## Exact next task

**Task #12.** In `Backend/ERP_RFQ_Automation/Ingestion/Triage/EmailIngestEnqueuer.cs`:

1. Delete `EnqueueAsync` and its `foreach (var att in message.Attachments)` loop entirely — not
   behind a flag, not as a fallback.
2. Replace with a thin `ScheduleAsync` that performs **no MIME traversal and generates no
   identities**. It iterates the **persisted** `EmailInquiryComponent` rows in `Ordinal` order:
   * skip `Skipped` / `Ignored` / `RefusedSecurity` — already terminal, no job by design;
   * skip any component already carrying a verified durable job (idempotent replay);
   * schedule the rest under the **persisted** `ComponentKey`.
3. Bytes come from the in-memory manifest on first capture, and on recovery from re-planning the
   raw `.eml` read back out of `IEvidenceObjectStorage` and hash-verified. The planner is
   deterministic, so a re-plan yields identical keys; a persisted component with no counterpart
   in the re-plan is a `manifest_mismatch` hold — never a silent drop, replace or reorder.
4. `BatchId` must be **derived** from `(AssemblyId, MessageKey)` via a documented stable hash
   (never `Guid.NewGuid()`, never `GetHashCode()`), and is grouping only — not the uniqueness
   boundary.
5. Do **not** overload `SourceOccurrenceId` unless its existing semantic contract genuinely is
   "stable source occurrence"; otherwise add typed assembly/component metadata fields.

Callers to update in the same increment:
`Services/EmailService.cs` (the `EnqueueEmailForExtractionAsync` call) and
`Ingestion/Triage/EmailTriageService.cs` (manual reprocess — must route through the same
coordinator and barrier, per the architecture rule).

`EmailIngestEnqueuerTests` asserts the walk being deleted and must be rewritten against the
manifest contract.

Command to re-verify after the increment:

```
cd Backend && dotnet build ERP_RFQ_Automation.sln -v q --nologo
dotnet test ERP_RFQ_Automation.Tests/ERP_RFQ_Automation.Tests.csproj --nologo -v q
```

Design-time EF helper (throwaway values, never secrets):
`scratchpad/ef.sh migrations has-pending-model-changes --no-build`
(run with `Migrations/` temporarily renamed — `MigrationsBaseline/` is the live lineage).

---

## Standing constraints

* One canonical planner, one persisted manifest, one durable scheduler, one barrier, one Lead
  identity path (`ILeadIdentityApplicationService`). No second implementation of any of these.
* No `dependency = null!` production constructor defaults, no service-locator, no swallowed
  exceptions, no dead legacy path, no unused flags, no source-text tests posing as behavioural.
* Retention (agreed): 1/7/30/90/180/365 days, default 7; clock starts only after a terminal
  outcome with structured data committed; never purge `NeedsReview` or `FailedRecoverable`.
* Credentials are entered only through the application's encrypted secret mechanism. Never in
  chat, code, tests, commits, screenshots or logs.
* Do not merge, deploy, amend published commits, or start Phase 3 / RFQ participation.
