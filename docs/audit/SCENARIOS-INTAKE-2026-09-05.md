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
| F13 | 1 | A 500-line bid list takes **1–2.5 minutes** to persist (one `SaveChanges` per evidence line; ~10 minutes under a load average of 300) while the batch reads "Processing" | P2 | No | S4i timings |
| F14 | 7 | `GET /api/LeadIngestion/leads/{id}/revisions` answers **200 `[]`** for a foreign lead id where every sibling verb answers 404 (tenant-scoped query; leaks nothing; inconsistent) | P2 | No | S7b (soft) |
| F15 | 1 | A 5,000-character description **dead-lettered the whole document** (`CanonicalLineItem` 4,000-character guard reached raw) | P1 | **Yes** — `StructuredEvidenceLedgerPersister` | `AuthoritativeEvidencePostgreSqlTests.OverlongDescription_IsKeptWithinTheLedgerContract…` (fails on old code) |

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

Three runs, three freshly seeded stacks (`run-intake-scenarios.sh`, `E2E_INTAKE_RUNS=1` each).
`P` pass, `F` fail, `S` the scenario walked to its end and recorded a soft (product) finding;
seconds in brackets. The one `F` (S2, run 3) is a strict-mode locator in the spec ("Revision 2"
appears twice on the lead page), fixed in `c4dc958`; the product behaviour — revision 2 of the
same Lead with the quantity diff — was asserted through the API and passed in all three runs.

| Scenario | Run 1 | Run 2 | Run 3 | Verdict |
|---|---|---|---|---|
| S1 clean CSV becomes one Lead with resolved lines and the same bytes are a Duplicate | P (14s) | P (29s) | P (20s) | pass |
| S1b the uploader can open the new Lead from the batch page without a detour | S (11s) | S (16s) | S (15s) | finding (soft) |
| S1c the documented sample shape (customer_rfq_reference,customer_name,part_number,quantity) keeps the reference | S (7s) | S (8s) | S (8s) | finding (soft) |
| S1d a CSV naming an existing customer by name only is UNRESOLVED with a reason, and linking resolves it | P (8s) | P (14s) | P (7s) | pass |
| S2 an amendment with changed quantities becomes revision 2 of the same Lead with a visible diff | P (12s) | P (15s) | F (15s) | FLAKY |
| S3 unknown part numbers produce a Lead whose lines are UnknownProduct and the workbench says what to do | P (12s) | P (15s) | P (12s) | pass |
| S4a zero quantity is held for a person, never stored as 0 or 1 | P (6s) | P (11s) | P (7s) | pass |
| S4b negative quantity is held for a person | P (6s) | P (7s) | P (6s) | pass |
| S4c absurd quantity beyond the persisted contract is held for a person | P (6s) | P (6s) | P (6s) | pass |
| S4d missing quantity is held for a person | P (6s) | P (7s) | P (7s) | pass |
| S4e missing unit of measure is flagged before conversion | P (6s) | P (7s) | P (6s) | pass |
| S4f blank customer name yields an unresolved Lead, never a fabricated customer | P (7s) | P (6s) | P (6s) | pass |
| S4g a 0-byte file is refused with a plain reason | P (3s) | P (4s) | P (4s) | pass |
| S4h CSV bytes under a spreadsheet name are refused with a reason that names the fix | P (5s) | P (5s) | P (6s) | pass |
| S4h-legacy the older /api/ManualUpload/upload door answers an inspection refusal without a 500 | P (4s) | P (4s) | P (4s) | pass |
| S4i a 500-line CSV becomes one Lead with 500 lines | P (59s) | P (118s) | P (96s) | pass |
| S4j UTF-8 Arabic descriptions survive intake verbatim | P (8s) | P (9s) | P (8s) | pass |
| S4k a 5,000-character description does not lose the document | P (6s) | P (9s) | P (6s) | pass |
| S5a a password-protected PDF ends as a visible password_protected outcome | P (8s) | P (8s) | P (7s) | pass |
| S5b a corrupt XLSX (valid package, broken sheet) ends as a visible parse failure | P (7s) | P (8s) | P (8s) | pass |
| S5c random bytes named .xlsx are refused at the door with a reason, and the refusal is recorded | P (5s) | P (6s) | P (5s) | pass |
| S5d a PDF with no readable content ends as a visible terminal outcome within the retry budget | P (7s) | P (8s) | P (8s) | pass |
| S6 two concurrent uploads of the same bytes mint exactly one Lead | P (6s) | P (6s) | P (6s) | pass |
| S7a the denied role gets a 403 with a plain-English reason | P (3s) | P (3s) | P (3s) | pass |
| S7b another tenant's Lead is invisible to tenant 80101 as a 404, not a 403 | S (10s) | S (12s) | S (10s) | finding (soft) |
| S8 the stopped-mail queue counts honestly and every stopped upload is findable somewhere an operator looks | P (5s) | P (5s) | P (5s) | pass |
| S9 approve needs a reason, hops need expectedVersion, and a stale version is a 409 that says so | P (8s) | P (13s) | P (8s) | pass |
| S10 a bid list with no unit or currency is held for review, refused by field name, and becomes an RFQ once a person supplies both | P (12s) | P (20s) | P (11s) | pass |
| S10b without the correction the Bid is refused naming unit then currency, and the approval cannot be redone | S (13s) | S (16s) | S (11s) | finding (soft) |
| S11 a newsletter is rejected as noise with its reason, and an invoice with a PDF is stopped where an operator looks | P (8s) | P (10s) | P (9s) | pass |
| S12 the same bid list arriving by upload and by e-mail attachment is one Lead, in either order | P (11s) | P (14s) | P (10s) | pass |
| S13 an editor outside the lead scope gets 404s on the lead, 403 with a sentence on manager verbs, and the screen says where the lead is | P (14s) | P (14s) | P (14s) | pass |
| S14 with a fallback owner set, an unmatched upload is routed to them and the uploader can open it; cleared, it waits on the queue | P (8s) | P (10s) | P (13s) | pass |

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

Runs 1–3 are three separate `run-intake-scenarios.sh` invocations, each on a fresh PostgreSQL
container, migrated database, `AcceptanceFixture` seed, GreenMail sink, real API and Vite (backend
5205, frontend 5183, PostgreSQL 55443, SMTP/IMAP 33025/33143, container names `nexora-e2e-intake-r<n>`),
run one after another with no `dotnet build` in flight. Run 1 is a redo on the final spec revision:
its first pass (and run 3) hit two strict-mode locator bugs in the spec itself ("Revision history"
matched the heading and its loading sentence; "Revision 2" appears twice) that were fixed in
`a479c98` / `c4dc958` — no product behaviour differed. A parallel attempt at the three runs was
discarded (harness notes). Backend suite on the finished code: **5,989 passed, 0 failed, 1
skipped** (`dotnet build -c Release --no-incremental` then `dotnet test --no-build`, 45 min under
load). Frontend: `npx tsc --noEmit` clean, `npm run lint:a11y` clean; vitest on `src/pages/Leads`
270/271 with one pre-existing flake (`QueueAssignment.test.tsx`, untouched by this branch, passes
alone).

Verbatim first lines of every failure and soft finding across the three runs:

- run 1 · S1b the uploader can open the new Lead from the batch page w · S: Error: F4: the manager who uploaded Lead 21 cannot open it until an owner is assigned (http://127.0.0.1:5183/procurement/leads/view/21)
- run 1 · S1c the documented sample shape (customer_rfq_reference,cust · S: Error: F7: with its reference dropped the inquiry cannot be told apart from earlier ones: ["Same buyer, overlapping closing dates and matching line items."]
- run 3 · S2 an amendment with changed quantities becomes revision 2 o · F: Error: expect(locator).toBeVisible() failed
- run 1 · S4i a 500-line CSV becomes one Lead with 500 lines · note: intake-seconds: scn-MTO0IC4A1-big.csv: 54 s to settle
- run 2 · S4i a 500-line CSV becomes one Lead with 500 lines · note: intake-seconds: scn-MTNZTHQ01-big.csv: 111 s to settle
- run 3 · S4i a 500-line CSV becomes one Lead with 500 lines · note: intake-seconds: scn-MTO07RKD1-big.csv: 89 s to settle
- run 1 · S7b another tenant's Lead is invisible to tenant 80101 as a  · S: Error: F14: a foreign lead id should be a 404 on every verb
- run 1 · S8 the stopped-mail queue counts honestly and every stopped  · note: email-triage-stopped: totalCount=0
- run 1 · S8 the stopped-mail queue counts honestly and every stopped  · note: intake-queue-truth: stopped uploads this run: 0; on an operator list: 0; only via batch URL: none
- run 1 · S10b without the correction the Bid is refused naming unit t · S: Error: F5: one-way gate — the line can never be bid and the approval cannot be redone: 409 {"error":"This lead is no longer awaiting extraction review."}
- run 1 · S10b without the correction the Bid is refused naming unit t · note: F5-redo-approval: 409 This lead is no longer awaiting extraction review.

Per-run artifacts: `.intake-scenarios-run/run<n>/intake/` (`run-1.json`, `run-1.log`,
`run-backend.log`, `fixture.log`; ignored by git).
