# Scenario testing: intake variations (e-mail / document → Lead → qualified → RFQ) — 2026-09-05

Branch `scenarios/intake-variations`, cut from `origin/main` `339e398`. Everything below was walked
by hand with `curl` against a disposable real stack first (PostgreSQL 16 container, migrated
database, deterministic `AcceptanceFixture` seed, the real API in Development with
`Notifications__OutboundGuard__Mode=DraftOnly`, Vite, and a loopback GreenMail sink for the mail
door), then encoded as `Frontend/e2e/scenarios-intake.spec.ts` and run three times on three freshly
seeded stacks by `scripts/e2e/run-intake-scenarios.sh`. Nothing on the live system was touched.

**A refusal is not a defect.** The product refuses a great deal on purpose and most of it is right.
The defects are the refusals that were **unexplained** (a 500 where a sentence belonged, a part
with no reason code), **unreachable** (a stopped message the Stopped tab did not count), or
**one-way** (an approval that closes a door the person still needs).

Severity: **P0** = work is lost or invisible to the person who must act; **P1** = a refusal that is
unexplained, unreachable or one-way, or a server error where a sentence belongs; **P2** = wording,
a missing convenience, or a gate that is correct but surprising.

## Findings by severity

| # | Scenario | Finding | Sev | Fixed here | Proof |
|---|----------|---------|-----|------------|-------|
| F1 | 5 | Mail held because a part's job dead-lettered is **absent from the Stopped tab** (0 stopped while three messages were dead) | **P0** | **Yes** — `EmailTriageService` stopped filter | `EmailTriageStoppedMessagesAreVisibleTests.A_message_held_because_its_job_gave_up_is_stopped` (fails on old code) |
| F2 | 5 | A part the worker closes (dead-lettered job) carries **no reason code or sentence**; the detail shows `FailedRecoverable` and `reasonCode: null` | P1 | **Yes** — `ExtractionWorker.CloseAssemblyComponentAsync` | `EmailInquiryStrandingHasAWayOutPostgreSqlTests.An_unexpected_extraction_failure_closes_its_part…` (extended; fails on old code) |
| F3 | 2/7 | Routing-queue **assign without `Idempotency-Key` → HTTP 500** "Internal server error" (the sentence existed and was thrown away); same exposure on account-ownership assign and follow-up complete | P1 | **Yes** — `CommercialIntelligenceController` | `CommercialIntelligenceControllerFocusedTests.AssignLead_without_an_idempotency_key…` (fails on old code) |
| F4 | 2 | The manager who **uploaded** a document cannot open the Lead it produced (404 → "We couldn't load this lead"); the batch page links straight into that dead end | P1 | **Screen: yes** (`LeadDetailPage` says where the lead is and offers the Routing queue). **API: no** — owner-less leads are outside a manager's record scope by design (`CommercialAccessFilters.InCommercialScope`, pinned by tests); the customer-set fix is the tenant fallback owner (scenario 8) | S1b (soft), S13, S14 |
| F5 | 3 | **One-way gate**: approve the extraction without correcting a blank unit → the review closes ("no longer awaiting extraction review"); every Bid on that line is refused ("lacks exact evidence for unit of measure") and nothing reopens the review | P1 | No — proposal below | S10b (soft), walk log |
| F6 | 3 | A quantity cell of `0`, `-5` or `1e20` **dead-lettered the whole document** (structured path handed the raw value to `CanonicalLineItem`'s guard on every retry) | P1 | **Yes** — `StructuredEvidenceLedgerPersister` | `AuthoritativeEvidencePostgreSqlTests.UnusableQuantity_HoldsTheLineForAPerson…` (3 cases fail on old code) |
| F7 | 1 | `POST /api/ManualUpload/upload` answered **500** on a document-inspection refusal (CSV bytes named `.xlsx`); the sibling door answered 422 with the reason | P1 | **Yes** — `ManualUploadController` | `ManualUploadControllerTrustTests.InspectionRefusalIsTheCallersOutcomeNotAServerError` (fails on old code) |
| F8 | 5 | On a tenant with **no model authorization**, an RFQ e-mail whose CSV attachment extracted cleanly still produces nothing: the prose body's job dead-letters (`ai_not_authorized`), the closure rule (correctly) holds the whole message, and the hold sentence says it "will resume" though nothing retries a job-bound hold | P2 (design) | Wording only via F2's part detail; the hold itself is deliberate (`EmailInquiryComponentClosure`: an unauthorised model is an infrastructure fault, not a content fault) | walk log, S11 |
| F9 | 8 | Fallback-owner routing **logs nothing** (the brief expected a warning). The record is durable — `lead_routing_decisions.Explanation` carries `DEFAULT_OWNER_ASSIGNED` and the code it fell back from — but there is no log line | P2 | No | walk log, S14 |
| F10 | 3 | Decision refusals name the **internal revision-line id** ("Bid revision line 78 requires an active tenant currency"), not the document's line number | P2 | No | S10b |
| F11 | 1 | `customer_rfq_reference` is not a recognised header, so the reference is dropped and a fresh inquiry lands as `PossibleMatchReviewRequired` against the earlier same-buyer upload instead of `New` | P2 | No | S1c (soft) |
| F12 | 3 | Promote with no committed decision says "The participation decision does not belong to the current lead revision" — there is no decision at all | P2 | No | walk log |

### Gates that looked wrong and are right

| Looks broken | Actually correct |
|---|---|
| A buyer **name** in a spreadsheet cell does not resolve the customer (`UNRESOLVED`, "Client evidence was found but matched no customer in this tenant") | Identity comes from customer-set identifiers (e-mail address, domain), never from a string that any document can contain. One `PUT /api/Lead/{id}/client` resolves it (S1d). |
| `SAR` refused: "requires an active tenant currency" | The tenant's currency table is the authority; the fixture tenant has only `USD`. |
| Approve needs `items` ("At least one line item is required for approval") | The approval IS the governed correction step; the items carry the person's unit/currency (S10). |
| Lifecycle hop without `idempotencyKey` → 400 "IdempotencyKey is required." | Explained and immediate. |
| Editor gets **404**, not 403, on a lead outside their scope | "Out-of-scope records are intentionally indistinguishable from missing records" (`CommercialAccessScope`). The screen now says where the lead went (F4). |
| Another tenant's lead → 404 on every verb | Same rule; verified for GET/review/transition/decisions/workbench/batches/revisions. |
| Second solicitation of the same bytes → `Duplicate` at the door, `ExactDuplicate` in the batch, no Lead | Content-addressed: renamed file and e-mail attachment both collide (S1, S12). |

## Scenario × run matrix

Three runs, three freshly seeded stacks (`run-intake-scenarios.sh`, `E2E_INTAKE_RUNS=1` each,
container/ports rotated). `P` pass, `F` fail, `S` pass with a soft (product) finding recorded.

RUN_MATRIX_PLACEHOLDER

## The walks (what was done, why it matters, what to do)

### 1. Same document twice, renamed, and a different one
`POST /api/Extraction/upload` (the Manual Upload screen's door, `Idempotency-Key` per click).
Same bytes with the same key → same batch, `Duplicate`. Same bytes, new key → new batch,
`ExactDuplicate`, `newLeads: 0`, listed on the Duplicate uploads screen. Renamed file, same bytes →
`Duplicate` (content-addressed). Different bytes → `Enqueued`, a second Lead. Two clicks racing on
one key → one batch, one job; two people racing on different keys → two batches, one Lead.
**Verdict: correct.**

### 2. Upload for an unknown customer, and who can open it
The Lead lands `UNRESOLVED` with a sentence. The manager who uploaded it gets **404** on
`GET /api/Lead/{id}` (F4): `InCommercialScope` admits a Lead to a non-tenant-wide scope only when
`AssignTo` is one of the scope's users; a fresh unowned Lead has none. The queue item is there
(`NO_MATCH_EVIDENCE`, "Assign an eligible owner"); assigning it to an **eligible** rep (the
fixture's account owner has exhausted capacity and is refused with a sentence) opens the lead.
Assigning without an `Idempotency-Key` was a 500 (F3, fixed). The screen used to say "We couldn't
load this lead"; it now says the lead is waiting on the Routing queue and offers the button.
**Recommendation (needs approval):** set the tenant fallback owner (scenario 8) during onboarding —
with it set, the uploaded lead is owned on arrival and the uploader opens it directly.

### 3. Bid list with no unit and no currency
The line persists with `unitOfMeasure: null, currency: null`, `RequiresCommercialReview`, workbench
`needsAttention` "Unit of measure missing". The governed path that works: link the customer,
**approve the extraction with the person's unit and currency in `items` and a reason**, hop to
`QUALIFIED`, save a fit assessment (all five governed criteria), decide `Bid` (with an
acknowledgement note), promote → `NXR-RFQ-80101-2026-00000001`. Without the correction the
refusals are good sentences in order: acknowledgement note → "requires an active tenant unit of
measure" → "requires an active tenant currency" → and then, with both supplied at decision time,
**409 "lacks exact evidence for unit of measure … complete a governed extraction approval"** while
`PUT /api/Lead/{id}/review` answers **409 "This lead is no longer awaiting extraction review"**
(F5). `ConvertRequest.Currency` no longer exists on main: direct conversion is retired
(`POST /api/Lead/{id}/convert-to-rfq` → 409 `PARTICIPATION_REQUIRED`); the promotion path is the
only one.
**Proposed fix for F5:** `LeadRepository.SubmitLeadReviewAsync`'s `awaitingReview` should also be
true while any current-revision line's verification is `NEEDS_CHECK` (the workbench already
computes it), so the second look the refusal asks for is possible; alternatively a
`source-fields` door on the workbench line. Not done here — it changes a governed gate.

### 4. Reason, `expectedVersion`, stale version
Approve without `reason` → 400 "An approval reason is required." With reason → 200,
`commercialFactsVerified`. Replay with the old `expectedVersion` → 409 "Review version 1 is stale;
current version is 2." Transition without `expectedVersion` → 400 "Expected version must be
positive."; with a wrong one → 409 "The lifecycle state changed. Reload it and retry with the
current version." and nothing moved. Four hops with re-read versions → `QUALIFIED`.
**Verdict: correct and explained.**

### 5. Inbound mail that is not an RFQ
Newsletter (`List-Unsubscribe`, `Precedence: bulk`) → `Noise` / `Rejected` / `bulk_list_header` /
assembly `NoInquiry` — a stated verdict, nothing stranded. Invoice with a PDF from an unknown
sender → `Uncertain` (`no_signal`); both parts need a model, the tenant has none, the jobs
dead-letter with `ai_not_authorized`, the message is held `FailedRecoverable` — and the Stopped
tab said **0** (F1). The parts carried no reason (F2). Both fixed: the message is now on the stopped
list and each stopped part names its code and what unblocks it. The hold itself is by design (F8):
the closure rule treats an unauthorised model as infrastructure, so the message waits for the
platform dead-letter recovery rather than being quoted against a body nobody read.

### 6. Duplicate detection across e-mail and upload
Upload then e-mail the same CSV → the attachment occurrence is `ExactDuplicate` (the hash is over
the attachment bytes, not the message), `newLeads: 0`. E-mail then upload → the door answers
`Duplicate`. **Verdict: correct.**

### 7. Role boundaries
`denied` → 403 `{"error":"You don't have permission for this action."}` on every verb including
upload and the triage list. `editor` → 404 on another person's lead for GET / workbench / review /
transition / decisions, 403 with the sentence on routing-queue assign and default-owner, 202 on
upload (may bring documents in). Other tenant → 404 on every verb, 202 on its own upload. The Lead
screen explains the 404 (F4). **Verdict: consistent**; the three 403 body shapes (`{error}`, empty
`Forbid()`, `{error}` from routing) are noted for the frontend lane.

### 8. Tenant fallback owner
`GET /api/commercial-routing/default-owner` → null with "No fallback owner is set…". `PUT` with the
account owner → saved but `isEligible: false` "Configured or measured capacity is exhausted" (the
setting and its usefulness are two facts, correctly). With an eligible rep → an unmatched upload
is routed: batch `assignedOpportunityOwner`, lead `assignmentReason: DEFAULT_OWNER_ASSIGNED`,
`lead_routing_decisions.Explanation` records the fallback, the uploader opens it directly, no
queue item. Cleared → the next upload waits on the queue. Nothing is logged (F9).

## Harness notes (not product findings)

- Port 5203 was held by the `scn-cash` lane's backend; this lane ran on backend **5205**,
  frontend 5183, PostgreSQL 55443, GreenMail 33025/33143.
- `Cors__AllowedOrigins__0=http://127.0.0.1:5183` is mandatory on a non-default Vite port.
- The runner now starts a loopback GreenMail, sets `Mail__AllowLoopbackForLocalDevelopment=true`
  (Development-only allowance) and repoints the fixture's seeded `localhost:993` mailbox row at it,
  so `POST /api/Email/fetch` reads real mail. The fixture customer carries e-mail/domain
  identifiers only.
- Editing `run-intake-scenarios.sh` while it was running broke that run (bash reads scripts
  incrementally); the stack was rebuilt.
- **Any `dotnet build` in the worktree stops a backend started with `dotnet run --no-build`**
  (seen three times: a Debug build, and the Release build that precedes the full test suite).
  A first attempt at the three official runs, launched in parallel while the suite was
  building, lost all three backends after S4k (`ECONNREFUSED`) and is discarded; the three runs
  reported below were run sequentially with no build in flight.
- Three Playwright runs in parallel share `Frontend/test-results/intake-scenarios` and clobber
  each other's artifacts (`ENOENT … .playwright-artifacts`); run them one at a time.
- Under a machine load average of ~300 (four lanes at once) the 15 s API timeout and the 15 s
  login wait flake; the numbers below are from sequential runs.

## Run log

RUN_LOG_PLACEHOLDER
