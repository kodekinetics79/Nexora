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
| 12 | Collapse fan-out: enqueuer → thin manifest adapter; delete `message.Attachments` walk | IN PROGRESS | 132 affected green | — | — | groundwork `<this commit>` | Adapter drafted and reverted twice for context safety; walk still present |
| 13 | `CaptureComplete` vs `SafeToAcknowledge` split | TODO | — | — | — | — | Acknowledging on capture alone can lose a message whose scheduling failed |
| 14 | Coordinator returns persisted state, never an unpersisted calculation | TODO | — | — | — | — | A caller could create a Lead from a `ReadyForAssembly` that was never committed |
| 15 | Durable idempotent scheduling (DB-enforced job uniqueness, attempt identity) | TODO | — | — | — | — | `ExtractionJobId != null` alone is not idempotency |
| 16 | Ownership tuple on every email job + validation before accepting a result | TODO | — | — | — | — | Cross-tenant result acceptance is currently possible in principle |
| 17 | DB-enforced tenant integrity: assembly→ingest, assembly→config, component→assembly | TODO | — | — | — | — | **`EmailIngest` has no `BusinessUnitId`** — needs column + safe backfill first |
| 18 | Deterministic bounded message identity + DB uniqueness | TODO | — | — | — | — | Fallback digest inherited from `ResolveIngestKey`, untested at the assembly boundary |
| 19 | Behavioural Test Connection proof (injected resolver, observed auth args) | TODO | — | — | — | — | Replaces the brittle source-text test added in `244f4a9` |
| 20 | PR #26 reconciliation vs its final head + reuse/exclude matrix | TODO | — | — | — | — | Cherry-pick may be superseded |
| 12a | Planner `ContractVersion` + manifest carries it | DONE | 132 | — | — | groundwork | Not yet persisted — see #12b |
| 12b | Persist `ManifestVersion` on the assembly | TODO | — | — | — | — | Needs migration regeneration + RLS/grant block re-paste |
| 12c | `EmailComponentManifestVerifier` (key/ordinal/kind/hash/size/version) | DONE | 0 direct | — | — | groundwork | **No direct tests yet** — must be covered before #12 closes |
| 12d | Typed ownership fields on `ExtractionJobMetadata` | DONE | — | — | — | groundwork | Sidecar is best-effort by contract, so these are hints only — #16 must use the DB row |
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

**#12, continued.** Groundwork is committed and green. What remains is one bounded increment
that must land together, because deleting the walk breaks both callers at compile time:

1. **#12b — persist `ManifestVersion`.** Add `public int ManifestVersion { get; set; }` to
   `EmailInquiryAssembly`, `e.Property(x => x.ManifestVersion).HasDefaultValue(1);` to the model
   builder, and `ManifestVersion = manifest.ContractVersion,` in `EmailInquiryCaptureService`.
   Then regenerate the migration — the branch keeps ONE migration and it has never been applied:

   ```
   git checkout ae6a376 -- Backend/ERP_RFQ_Automation/MigrationsBaseline/ErpRfqAutomationContextModelSnapshot.cs
   rm Backend/ERP_RFQ_Automation/MigrationsBaseline/20260813134002_EmailInquiryAssembly*.cs
   cd Backend/ERP_RFQ_Automation && mv Migrations Migrations.tmp-hidden
   ../../scratchpad/ef.sh migrations add EmailInquiryAssembly --no-build
   mv Migrations.tmp-hidden Migrations
   ```

   **Then re-paste the RLS + grant + purge-policy SQL block** into the regenerated migration's
   `Up`/`Down` — it is NOT regenerated and its loss silently removes tenant isolation. Copy it
   verbatim from commit `5afe445` (`git show 5afe445 -- '*EmailInquiryAssembly.cs'`). It
   contains: `ENABLE`/`FORCE ROW LEVEL SECURITY` + `nexora_tenant_isolation` on both tables;
   `GRANT SELECT, INSERT, UPDATE, DELETE ... TO nexora_tenant_app`; `GRANT USAGE ON SEQUENCE`
   (**never** `SELECT`/`UPDATE` — `PostgreSqlProductionDialectTests` enforces this globally);
   the `nexora_purge_app` grant + `nexora_tenant_purge` policy guarded on the role existing.

2. **#12 — replace the walk.** Rewrite `EmailIngestEnqueuer` as the thin adapter. The full
   drafted implementation is in this session's history; its shape:
   `ScheduleAsync(assembly, persistedComponents, plan, ingest, clientEmail, ingestion, triage,
   coordinator, logger, ct)` → verify via `EmailComponentManifestVerifier` FIRST and hold every
   non-terminal component on any mismatch; then iterate persisted rows by `Ordinal`, skipping
   `IsTerminal` and any row already carrying a job; schedule under the persisted `ComponentKey`;
   `DeriveBatchId` = SHA-256 over `"nexora:email-assembly-batch:v1:{assemblyId}:{messageKey}"`.
   Delete `EnqueueAsync` and the `foreach (var att in message.Attachments)` loop outright.

3. **Update both callers** — `Services/EmailService.cs:913` and
   `Ingestion/Triage/EmailTriageService.cs:240`. Both must obtain the assembly + persisted
   components + plan from `IEmailInquiryCaptureService`, then call `ScheduleAsync`. The manual
   reprocess path must use the SAME coordinator and must not reopen an absorbing terminal
   assembly.

4. **Rewrite `EmailIngestEnqueuerTests`** — it asserts the walk being deleted.

5. **Add direct `EmailComponentManifestVerifier` tests** (#12c has none): each mismatch kind,
   and the case where skipped/ignored components carry no hash or size and must NOT be reported
   as mismatched.

Acknowledgement semantics stay as they are until #13; do not claim `SafeToAcknowledge` yet.

Verify with:

```
cd Backend && dotnet build ERP_RFQ_Automation.sln -v q --nologo
dotnet test ERP_RFQ_Automation.Tests/ERP_RFQ_Automation.Tests.csproj \
  --filter "FullyQualifiedName~EmailInquiry|FullyQualifiedName~EmailIngestEnqueuer|FullyQualifiedName~EmailTriage" --nologo
dotnet test ERP_RFQ_Automation.Tests/ERP_RFQ_Automation.Tests.csproj --nologo -v q
```

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

---

## Independent SME review — exact SHA `c61f73cb7ba0e5dd0d3d75cc7f4c7f1c74733e90`

Five bounded reviews commissioned against this SHA. Reviewers ran as separate read-only agents
with **no edit capability** — the "reviewers may not edit production files" rule is enforced by
tooling, not by instruction. Findings are recorded verbatim in substance; none is backfilled onto
earlier commits, and no approval is recorded for work that was not reviewed.

Critical and High block requirement #12.

### SME5 — Principal SDET (test architecture). Status: RETURNED, 8 findings

| ID | Sev | Summary | Disposition |
| --- | --- | --- | --- |
| F1 | **Critical** | `EmailInquiryCaptureService` has ZERO tests. `SafeToMarkSeen` — the single flag deciding whether a mailbox message is flagged `\Seen` — is unproven on all four of its return paths. | ACCEPTED |
| F2 | **Critical** | `EmailComponentManifestVerifier` has zero tests **and zero production callers**. The empty-hash exemption and the version short-circuit are unasserted. | ACCEPTED |
| F3 | **High** | `EmailInquiryAssemblyCoordinator.RecordComponentQueuedAsync` assigns `ExtractionJobId` **before** the terminal guard, so a replayed enqueue re-points an already-`Completed` component at a newer job id. | ACCEPTED — real defect |
| F4 | **High** | The source-regex test in `MailboxLoginIdentityTests` does not pin the invariant it claims: its whitelist admits bare `config.EmailAddress`, so rewriting **both** IMAP sites to bypass the resolver keeps it green. | ACCEPTED — my test was weaker than claimed |
| F5 | **High** | Poller and manual reprocess compute body text with **two different extractors** (`EmailBodyNormalizer` vs `EmailTriageService.GetBodyText`), so the shared enqueuer does not prevent drift — the *input* diverges. `ReprocessAsync` also reads `RawEmailPath` directly, ignoring `IRawEmailEvidenceReader`. | ACCEPTED |
| F6 | Medium | The "reason names no infrastructure detail" tests assert against hard-coded literals with no interpolation — they can never fail. The real leak surface (caller-supplied `reasonDetail` persisted by the coordinator) is untested. | ACCEPTED — security theatre in my own tests |
| F7 | Medium | `Attachment_only_inquiry_is_ready…` is byte-identical to the body-only test; `Evaluate` has no notion of component kind, so the distinction cannot exist at that layer. False coverage credit. | ACCEPTED |
| F8 | Medium | No test exercises capture → schedule → assemble together. Nothing asserts the planner's `ComponentKey` is the same string the consumer looks up, so the two can drift into total silent loss. | ACCEPTED |

**Consequence for #12:** F1, F2 and F3 must close inside #12b/#12c. F3 is a code fix, not a test
gap. F4/F5 move to #19 and #12d respectively. F6/F7 are test-quality corrections to work already
landed and are scheduled with #12b.

### SME1 — Principal .NET/domain architect. Status: RUNNING
### SME2 — Email/MIME specialist. Status: RETURNED, 8 findings (1 Critical, 6 High, 1 Medium)

| ID | Sev | Summary | Disposition |
| --- | --- | --- | --- |
| M1 | **Critical** | The two walks do not merely disagree — they **alias**. Planner numbers `:attachment:{n}` over its candidate set; enqueuer numbers the *same string namespace* over `message.Attachments`, a strict subset. Inline logo + `BoQ.pdf` ⇒ planner calls the logo `attachment:1` and the BoQ `attachment:2`; enqueuer queues the BoQ as `attachment:1`. The BoQ's result binds to the **logo's** component row; the BoQ's row never terminates so the barrier holds forever; provenance names the wrong part. `SourceOccurrenceIdentity.BuildKey` hashes that string into occurrence identity, so two physical parts collide in one batch and the second ingest is discarded as a duplicate — the real attachment is never queued. | ACCEPTED |
| M2 | **High** | Planner decodes the whole part (`DecodeToAsync` + `ToArray`) **before** the size checks, and retains every accepted `byte[]` on the plan. An 800 MB part costs ≈1.6 GB transient; fifty 20 MB parts retain ≈1 GB on the LOH. The class comment claims it "terminates at a stated limit rather than by exhausting memory" — it does not. One large message OOMs the poller and loses every in-flight message on that worker. | ACCEPTED |
| M3 | **High** | Verifier compares only ordinal/kind/hash/size, and both content comparisons are conditional on the persisted side carrying a value — which Skip/Ignore rows never do. Two substitutions pass clean: a `Skip/unsupported_file_type` row re-planning as a processable PDF, and renaming `quote.pdf`→`quote.htm` inside the stored `.eml` (hash and size unchanged, different inspection path, different extractor, provenance filename now wrong). | ACCEPTED |
| M4 | **High** | `ContractVersion` is never persisted, so the stated guarantee does not exist and the version branch can never fire. Sharpest point: the likely wiring accident is passing `assembly.Version` — the **optimistic-concurrency counter** — as `persistedManifestVersion`, which would report a version mismatch on essentially every recovery and swallow all real component differences via the early return. | ACCEPTED — also rename `Version`→`ConcurrencyVersion` |
| M5 | **High** | The inline-asset classifier requires `Content-Disposition: size`, which is **optional and omitted by Gmail, Apple Mail, most webmail and Exchange/OWA**. It therefore almost never fires in production: every signature logo becomes a `Process` image consuming an OCR job that yields nothing, and every unnamed inline part becomes `attachment_unnamed` ⇒ forced review. The review queue fills with decorations, which is what makes reviewers stop reading holds. | ACCEPTED |
| M6 | **High** | Container formats fall through the extension gate: TNEF (`winmail.dat` — an Exchange RTF sender ships *all* real attachments inside it), S/MIME opaque-signed `.p7m` (entire real message inside the blob ⇒ contentless review row), clear-signed `.p7s` (every S/MIME customer forces review), appledouble resource fork (same filename as data fork ⇒ extracts binary garbage as a document), `.ics` invites and DSN bounces. `TnefPart.ExtractAttachments()` and pkcs7 signed-data need no keys. | ACCEPTED |
| M7 | **High** | `WalkAsync` never recurses; `PlanEmbeddedAsync` does not call it. `path` is always empty, the `:embedded:{path}:{index}` key form is **unreachable**, and the depth guard can only fire when `MaxNestingDepth == 0` — the sole case the test exercises. Nesting is actually bounded at 2 by `EmailContainerReader.MaxContainerDepth`, in another file, and that reader enumerates `message.Attachments` — the very walk the planner's own comment calls broken. Inside a forwarded enquiry a PDF with only a `name=` parameter is dropped with no row and no note. | ACCEPTED |
| M8 | Medium | Hash stability: body hash is over `EmailBodyNormalizer` output, which is outside `ContractVersion`; embedded hash is over a MimeKit **re-serialization**. A MimeKit upgrade or a quote-stripper tweak changes every hash and surfaces as "the stored original no longer matches" — an accusation of evidence tampering aimed at ordinary maintenance. Positional keys over a filtered list also shift wholesale if one part's candidacy flips. | ACCEPTED |

**Consequence for #12:** M1 upgrades the blocker from "keys differ" to "keys alias to the wrong
component" — a silent mis-binding, not just a stall. M2, M5, M6, M7 are defects in the canonical
planner itself and must close before the planner can be called the sole authority. M3/M4 land in
#12b with the verifier and version work.

### SME3 — PostgreSQL/reliability specialist. Status: RUNNING
### SME4 — Security/evidence specialist. Status: RUNNING

---

## #12 CONSOLIDATED DECISION RECORD — all five reviews returned

**40 findings: 8 Critical, 18 High, 13 Medium, 1 Low.** Every Critical and High blocks #12.
Two findings are DISPUTED with repository evidence (below); all others accepted.

### Disputed — challenged, not ignored

| ID | Claim | Repository evidence | Verdict |
| --- | --- | --- | --- |
| SME4 EA-8 | "migration creates both tables with **no** `ENABLE ROW LEVEL SECURITY` and no policy" | `grep -c 'ENABLE ROW LEVEL SECURITY\|FORCE ROW LEVEL SECURITY\|CREATE POLICY nexora_tenant_isolation' MigrationsBaseline/20260813134002_EmailInquiryAssembly.cs` = **6**. SME3 EIA-07 independently read the same SQL and confirmed it correct and complete. | **REJECTED** — reviewer missed the `migrationBuilder.Sql` blocks |
| SME3 EIA-07(a) | "there is **no** `HasQueryFilter` anywhere in `EmailInquiryAssemblyModelBuilderExtensions.cs`" | True of that file (0 hits), but the filters exist at `Models/ErpRfqAutomationContext.Tenancy.cs:96-99`. | **PARTIALLY ACCEPTED** — filter exists; the comment claiming it is "added alongside" the policies is misleading about location and will be corrected. The reviewer's *separate* point — that these tables may be absent from the `filteredTables` sweep in `PostgreSqlProductionDialectTests` — remains open and must be verified. |

### The three findings that change the plan

**1. Migration route — DECIDED by SME3 EIA-06, overriding the earlier plan.**
Do **NOT** run `ef migrations remove`/`add` on `20260813134002`. It carries four hand-written
`migrationBuilder.Sql` blocks (RLS, tenant grants, purge grants+policy, sequence+pipeline grants)
plus a hand-written `Down`, none derivable from the model diff — regeneration silently discards
all of them, compiles cleanly, and is caught only by a tenant offboarding. Instead add a focused
follow-up migration `EmailInquiryManifestContractVersion` containing only the `AddColumn`,
generated by the tool so Designer and snapshot stay consistent.
**Acceptance gate: `git diff --stat` must show `20260813134002_EmailInquiryAssembly.cs` unchanged (0 lines).**
This retires the "re-paste the SQL block by hand" step previously written into this ledger.

**2. The #12 blocker is mis-binding, not a stall (SME2 M1, corroborated by SME3 EIA-05 and SME4 EA-2).**
Both walks number into the *same* `:attachment:{n}` namespace over *different* sets. Inline logo
+ `BoQ.pdf` ⇒ the BoQ's result binds to the **logo's** row; `SourceOccurrenceIdentity.BuildKey`
hashes the string, so two physical parts collide and the second ingest is dropped as a duplicate.

**3. Scheduling must reuse the existing durable pattern (SME3 EIA-01/EIA-02), not a new one.**
Named: `SourceDocumentOccurrence` + `ExtractionQueue.EnqueueAsync` inside
`DocumentIngestionService.IngestAsync`, under `pg_advisory_xact_lock`. The occurrence idempotency
key must be derived from `ComponentKey`, **never** from a random batch `Guid` — otherwise two
pollers mint two jobs for one attachment and produce two Leads for one email part.

### Cross-cutting agreements (independent reviewers converging)

* **Contract version unpersisted** — SME1 F1, SME2 M4, SME4 EA-6. Plus SME2's warning that the
  obvious wiring (`assembly.Version`, the concurrency counter) would report a mismatch on every
  recovery and swallow all real differences. Rename to `ConcurrencyVersion`.
* **Component lookup by `(BusinessUnitId, ComponentKey)` is not unique** — SME4 EA-2, SME3 EIA-05.
  The unique index is 3-column. Two mailboxes in one tenant receiving the same `Message-Id`
  (To+Cc, distribution list, forwarding rule) ⇒ outcomes bind to an arbitrary assembly.
* **Optimistic concurrency is inert** — SME4 EA-4, SME3 EIA-03. `Version` is declared a
  concurrency token and **never incremented**; Npgsql does not auto-generate `int` tokens. The
  documented guarantee does not exist.
* **Sidecar cannot carry ownership** — SME1 F2, SME4 EA-5. Worse than "best-effort": the sidecar
  path is content-addressed, so the *same attachment bytes in one tenant* overwrite each other's
  sidecar and attribute one message's result to another's component.
* **Embedded/nested is unreachable** — SME2 M7, SME1 F3. `WalkAsync` never recurses; the
  `embedded:` key form is dead; the depth guard only fires at `MaxNestingDepth == 0`, the one case
  the test exercises. Real nesting is bounded at 2 elsewhere by a reader using the broken walk.
* **Inline classifier never fires** — SME2 M5, SME1 F5. Depends on the optional
  `Content-Disposition: size`, omitted by Gmail/Apple Mail/Outlook/Exchange.
* **Coordinator returns unpersisted state** — SME1 F6, SME4 EA-1. And `RejectedSecurity` is absent
  from the `ReadyForAssembly` and `Assembled` transition rows, so a late malware verdict is
  discarded with one `LogError` while the Lead stays live.
* **`IsTerminal` omits `Ignored`** — SME3 EIA-08. Disagrees with `CompletedCount`, which counts it.
  A replayed report can flip an `Ignored` logo to `Skipped` and drag a clean message into review.
* **Poller/reprocess diverge on input** — SME5 F5, SME1 F8. Two body extractors; reprocess reads
  `RawEmailPath` directly and will break the moment capture becomes authoritative.

### Revised increment plan

| Increment | Closes |
| --- | --- |
| #12b-1 | SME3 EIA-06 follow-up migration for `ManifestContractVersion`; rename `Version`→`ConcurrencyVersion`; make the token actually increment (EA-4/EIA-03); `IsTerminal` += `Ignored` (EIA-08) |
| #12b-2 | Verifier completeness (M3): FileName, MimeType, disposition, ReasonCode; hash skipped parts. Direct tests for verifier + capture + coordinator (F1, F2, F3, F6, F7) |
| #12b-3 | Planner defects: decode-before-limit (M2), inline classifier without `size` (M5), containers TNEF/S-MIME/appledouble/ics/DSN (M6), nesting recursion or honest removal (M7) |
| #12c | Thin scheduler over `SourceDocumentOccurrence` + `ExtractionQueue` with `ComponentKey`-derived idempotency key (EIA-01, EIA-02); coordinator scoped by `assemblyId` (EA-2, EIA-05); persisted job↔component link (F2, EA-5); coordinator returns persisted state + `RejectedSecurity` reachable (F6, EA-1) |
| #12d | Delete the `message.Attachments` walk; scheduler as injected `IEmailInquiryScheduler`; DI registration of all four assembly services — currently **unregistered dead code** (SME1 F7); single body extractor; reprocess via `IRawEmailEvidenceReader` (F5, F8) |
| Deferred to #17 | `EmailIngests.BusinessUnitId` + composite FK, with the purge-order and grant hazards SME3 EIA-04 identified |

**SME1 F7 note:** `IEmailInquiryCaptureService`, `IEmailInquiryAssemblyCoordinator` and
`IRawEmailEvidenceReader` are **not registered in `Program.cs`** — the entire assembly package is
unreachable at this SHA. That is consistent with "PARTIAL/not wired" in the ledger, but it means
no runtime behaviour has ever executed this code.

---

## Section A — data and migration integrity. COMPLETE

| Gate | Evidence |
| --- | --- |
| `ManifestContractVersion` added by focused follow-up migration | `20260813200929_EmailInquiryManifestContractVersion` — tool-generated, Designer + snapshot consistent |
| `20260813134002` unchanged | `git diff --stat 700312b -- …20260813134002_EmailInquiryAssembly.cs` = **empty (0 lines)** |
| Migrations apply to an empty PostgreSQL database | `PostgreSqlProductionDialectTests.AllMigrationsApplyToAnEmptyPostgreSqlDatabase` green |
| RLS ENABLE **and** FORCE on both tables | asserted on `pg_class.relrowsecurity` / `relforcerowsecurity` — applied, not grepped |
| `nexora_tenant_isolation` + `nexora_tenant_purge` on both tables | asserted on `pg_policy` |
| Tenant role table access | `has_table_privilege` SELECT/INSERT/UPDATE/DELETE = true |
| Sequences USAGE-only | `has_sequence_privilege` USAGE = true, SELECT = **false**, UPDATE = **false** |
| Purge role reach | `has_table_privilege('nexora_purge_app', …, 'SELECT, DELETE')` = true on both |
| Cross-tenant negative | raw SQL as `nexora_tenant_app` with the tenant GUC set to another business unit, EF bypassed entirely → **0 rows** |
| Model drift | none |
| Purge / lifecycle / forced-RLS / dialect suites | Failed: 0, Passed: 98 |
| New isolation suite | Failed: 0, Passed: 6 |
| Affected email suites | Failed: 0, Passed: 147 |

### Findings closed in section A

| Finding | Closure | Evidence |
| --- | --- | --- |
| **SME4 EA-8** (Critical, disputed) | **CLOSED — REJECTED, conclusively** | Applied assertion on a migrated container: RLS is ENABLED and FORCED and both policies exist. Closed by automated evidence, not reviewer confirmation — marked honestly. |
| **SME3 EIA-06** (Medium) | CLOSED | Follow-up migration used; original untouched, gate proven |
| **SME3 EIA-07(a)** (Medium, partial) | CLOSED | The migration's comment is stale but the file is frozen by the EIA-06 gate, so the correction is recorded in `EmailInquiryAssemblyModelBuilderExtensions` — where a reader looking for the filter actually goes — and states plainly that RLS is the boundary and the pipeline role is `BYPASSRLS`. |
| **SME2 M4** / **SME4 EA-4** / **SME3 EIA-03** (High ×3) | **PARTIALLY closed** | `Version` → `ConcurrencyVersion` (removing the trap of passing the concurrency counter into manifest verification), and `EmailInquiryConcurrencyStamp` now increments it in both `SaveChanges` overrides so the token is no longer inert. **The concurrency BEHAVIOUR test is still owed** — see section C. Not closed until two concurrent completions are proven to converge. |

### Open from EIA-07(b), carried forward

`EmailInquiryComponents.ExtractionJobId` and `.SourceDocumentOccurrenceId` still have no FK and no
tenant-composite FK. Owner: section D. Consequence if unfixed: a component can cite another
tenant's occurrence or job id with nothing refusing it.

**Next: section B — canonical MIME correctness (M2 decode-before-limit, M3 verifier completeness,
M5 inline classifier, M6 containers, M7 recursion).**

---

## Section B — canonical MIME correctness

### B1 — bounded decoding. COMPLETE

`BoundedComponentDecoder` enforces the per-component ceiling and the message's remaining shared
budget **during** the copy, via a write-only sink that refuses past its ceiling. Both planner
paths (attachment decode, embedded-message serialize) now go through it.

Refusing rather than truncating is deliberate: a silently truncated attachment would be extracted
as though it were the whole document, and half a bill of quantities priced as a complete one is
worse than a refusal an operator can see.

Declared `Content-Disposition: size` may only ever REJECT early — it can never authorise a
decode — because the sender controls it and understating it would otherwise be the way past the
limit.

| Test | Proves |
| --- | --- |
| oversized part refused | observed bytes ≤ ceiling, not the part's real size — the OOM vector |
| dishonestly small declared size | cannot authorise an oversized decode; still bounded |
| honestly oversized declared size | refused without touching the body |
| shared-budget exhaustion | distinct outcome from per-component (different operator action) |
| exhausted budget | refuses before reading anything |
| exactly at ceiling | accepted (inclusive limit) |
| embedded message | same ceilings, no separate allowance |
| cancellation | propagates, not reported as "could not be read" |

Focused: **Failed: 0, Passed: 34** (decoder + planner). **Closes SME2 M2 (High).**

### B1 exact-boundary proof. COMPLETE

Three-point boundary confirmed with **no off-by-one false rejection**: `limit − 1` accepted,
**exactly `limit` accepted**, `limit + 1` refused. A very large input observes ≤ `limit + 1`.

Declared-size hostility covered as a `Theory` over `1, 0, −1, −999_999, long.MinValue` — none can
authorise an oversized decode; an honest oversized declaration refuses without reading the body;
a declaration of exactly the limit does not pre-reject a legal file (the early check is strictly
greater-than). Refusals return no bytes at all, so a fragment can never be hashed or persisted as
whole content. Cancellation propagates and disposes.

**One test premise was wrong and is recorded rather than papered over.** The requested
"non-seekable body" case is *unreachable in this pipeline*: `MimeKit.MimeContent`'s constructor
throws `ArgumentException("The stream does not support seeking")`, so a `MimePart`'s content is
seekable by construction. That constraint is now asserted directly — if a future MimeKit relaxes
it, the test fails and tells the next reader to add the streaming case for real. The genuinely
non-seekable stream in this design is the **destination**: the decoder writes into a forward-only,
write-only sink that cannot be rewound or measured, which is what every boundary number above
actually exercises.

Focused: **Failed: 0, Passed: 24** (decoder + boundary).

### B2–B5 — OUTSTANDING

| Item | Finding | Status |
| --- | --- | --- |
| B2 real recursion + one shared budget across the tree | M7, SME1 F3 | **IMPLEMENTED** — dedicated test matrix outstanding |
| B3 verifier completeness + typed outcomes | M3 | TODO |
| B4 inline classifier without declared size | M5, SME1 F5 | **IMPLEMENTED** — dedicated test matrix outstanding |
| B5 container formats classified truthfully | M6 | **IMPLEMENTED** — dedicated test matrix outstanding |

### V1 CONTRACT DECISION (required before recursion could land)

Recursion changes key formation, so the contract-version consequence had to be decided rather
than discovered. **The manifest schema has never been applied outside disposable Testcontainers
databases** — migration `20260813134002` is unapplied on this branch and production is at base
`1601db6`, which predates the aggregate entirely. There are therefore **no historical rows in any
persistent environment** that could be reinterpreted under a new scheme.

**Decision: the completed recursive scheme defines final V1.** `ContractVersion` stays `1`. The
fail-closed unknown-version path is retained regardless, because B3 needs it and because the next
scheme change will not have this luxury.

### B2/B4/B5 implementation notes

**Key formation is now a hierarchical PATH**, not a flat counter: `part:1`, `part:3`, `part:3.1`,
`part:3.2`. A counter alone collides once traversal recurses — the third top-level attachment and
the third attachment inside a forward would both be `attachment:3`.

**`EmailInquiryBudget`** is one mutable instance passed down every branch. Before recursion the
limits were locals and the embedded branch was handed `MaxTotalBytes` afresh; under recursion that
shape gives each nested level a new allowance, so three forwards each carrying 90 MB would each
pass a "100 MB total" check and cost 270 MB. Byte charging is deliberately double-counted for a
container and its contents, because the octets really are materialised twice.

**An embedded message is now BOTH a component and a walked subtree.** Without the component the
forward is invisible; without the walk, a refused spreadsheet inside it is a prose note nobody
counts, the container reports Completed, and a clean Lead is priced against a document nobody
opened.

**`InlineAssetClassifier` measures instead of believing.** The old rule required
`Content-Disposition: size`, which RFC 2183 makes optional and Gmail/Apple Mail/Outlook/Exchange
omit — so it almost never fired. Size now comes from the encoded stream length (base64 inflates,
so the bound is conservative), and `Decorative` additionally requires image type, a Content-Id,
an **actual `cid:` reference in the HTML body**, non-attachment intent, and no commercial filename
signal. Anything short of that is processed as content, never ignored.

**`ContainerFormatClassifier` refuses truthfully rather than pretending.** TNEF is **not** wired:
`TnefPart.ExtractAttachments()` materialises the expanded container before any budget can see it,
which would reintroduce the unbounded-expansion hazard B1 just removed. Per the guardrail,
`winmail.dat` is classified unsupported **commercial** evidence → `NeedsReview`. Encrypted S/MIME
is security-gated; opaque signed-data is not unwrapped; detached signatures are marked
non-commercial so a signed customer is not sent to review forever; AppleDouble refuses only the
**resource fork**, never the primary file; calendar invites are refused conservatively rather than
assumed non-commercial; DSN detection requires `multipart/report; report-type=delivery-status`
with a real `MessageDeliveryStatus` part — a subject line reading "Undeliverable" is not evidence,
because customers forward bounces asking what went wrong and that forward is a real enquiry.

Affected suites: **Failed: 0, Passed: 162**. Model drift: none.

### B2 duplication gate — TWO DEFECTS FOUND BY THE MATRIX, BOTH IN MY CODE

The reviewer-mandated ownership contract exposed a real double-extraction bug and a second
defect the fix uncovered. Neither was visible in the 162 inherited tests.

**Defect 1 — double extraction.** The embedded `message/rfc822` was planned as a `Process`
component carrying its serialized bytes AND its children were planned as `Process` components.
`EmailContainerReader` unwraps an `.eml` internally, so the same body and the same attachments
went through extraction twice: duplicated line items on one inquiry, and provenance naming two
sources for one physical document.

**Fix:** a new `StructuralContainer` disposition / `StructuralOnly` component status. The
container is recorded (presence, raw SHA-256, size, subtree relationship) and counted by the
barrier, but carries no content and produces no extraction job. Its children are the commercial
components. Byte budget is charged **once**, by the children — the container's bytes are hashed
and released, so charging for them would deduct an allowance nothing is holding.

**Defect 2 — the fix hid entire forwards.** With the container structural, a *body-only* forward
produced **no commercial component at all**: the container carries nothing by contract and the
walk only plans attachments. A forwarded body-only RFQ — the commonest way an enquiry reaches a
distributor — lost its content completely. Fixed by planning the nested message's own body,
routed through `EmailBodyNormalizer`, the same quote-stripper the outer body uses, so the two
cannot drift.

**Defect 3 — the per-component ceiling refused whole subtrees.** Applying `MaxComponentBytes` to
the container made a forward whose envelope exceeded the ceiling refuse its entire subtree,
hiding the attachments the recursion exists to find. That ceiling bounds bytes we RETAIN and a
structural container retains none, so it is now bounded by the message-wide budget only.

Also closed here: **SME3 EIA-08** — `IsTerminal` omitted `Ignored` while `CompletedCount` counted
it, so a replayed report could overwrite an `Ignored` logo with `Skipped` and drag a clean message
into review. `IsTerminal` now includes `Ignored` and `StructuralOnly`.

B2 matrix: **Failed: 0, Passed: 15** — duplication gate, one-body-per-forward, one-evidence-item-
once, duplicate filenames across forwards, no cross-level collision, key/ordinal stability across
re-plan, dense ordinals, depth 0/1/max+1, shared component budget across siblings, shared byte
budget, per-component ceiling inside a forward, malformed embedded message, and 50-level nesting
terminating at the declared depth without exhausting the stack.

Cumulative affected: **Failed: 0, Passed: 177**.

Two further planner tests encoded the superseded contract and were corrected, not deleted:
`An_embedded_message_becomes_a_processable_eml_component` asserted the double-extraction
behaviour directly.

**B5 inventory (done):** the repository has **no** existing reader for TNEF, S/MIME, calendar or
DSN. MimeKit 4.16 supplies `TnefPart.ExtractAttachments()` (safe wiring, no new parser) and
`ApplicationPkcs7Mime`. Decrypting S/MIME needs a `SecureMimeContext` with keys, which this
deployment does not have — so encrypted S/MIME must be classified security-gated and surfaced as
`NeedsReview`, never represented as extracted. No new parsers are to be written.


### B4 — inline assets. MATRIX COMPLETE

Classification now uses a **bounded decoded-size probe** through `BoundedComponentDecoder`,
reading at most `InlineAssetMaxBytes + 1` bytes. Encoded length was a safe upper bound but
rejected legitimate logos for arithmetic reasons — base64 inflates by a third, so a 50 KB logo
measures 67 KB encoded and loses its exemption without any commercial justification.

**The decoration ceiling moved from 64 KB to 16 KB, and the matrix is why.** Two cases failed on
first run — a 30 KB `image001.png` requirements screenshot and a QR code were both being
exempted. Outlook names BOTH a signature logo and a pasted screenshot `image001.png`, both inline,
both cid-referenced, both image parts, so filename and headers cannot separate them and **size is
the only honest discriminator**. Signature logos are typically 2–15 KB; a screenshot of a
requirements table is rarely under 20 KB. The ceiling now sits below the ambiguous band, so
everything uncertain is processed. `qr` was added to the commercial filename signals: a QR code in
commercial mail encodes a portal link or reference number, and a sender who names it that is
telling us it carries information.

`Decorative` requires **all** of: image media type · non-attachment intent · a Content-Id · an
actual `cid:` reference in the HTML body · no commercial filename signal · measured decoded size
within the ceiling. Anything short of that is `Process`.

| Case | Outcome |
| --- | --- |
| ordinary cid signature logo | Ignored |
| logo with no declared size (Gmail/Apple/Outlook/Exchange) | Ignored |
| logo just under the ceiling | Ignored |
| logo + genuine BOQ | logo Ignored, BOQ Process, **no Skip anywhere** |
| repeated inline asset | both Ignored, distinct keys |
| **small generic `image001.png` screenshot** | **Process — not discarded** |
| commercially named inline image, perfect logo headers | Process (filename wins) |
| image explicitly marked attachment | Process |
| cid image the HTML never references | Process |
| oversized inline image | Process |
| QR code | Process |
| dishonest declared size on a large image | Process |
| unnamed inline cid image within ceiling | Ignored — **not** an `attachment_unnamed` Skip |
| unnamed inline image above ceiling | Process — not refused for lacking a name |

The last two matter commercially: before the classifier fired, an unnamed inline part became
`attachment_unnamed`, which is a Skip, which sends the **whole message** to review — so every
message carrying a signature would have been reviewed.

B4 matrix: **Failed: 0, Passed: 14**. Cumulative affected: **Failed: 0, Passed: 200**.

### Still outstanding for Section B exit

B5 matrix · B3 typed verifier · B2 carried safeguards (StructuralOnly lifecycle, raw-message
bounds, nested-body normalization) · full backend regression · PG suites · drift · exact-diff
MIME/security/SDET review.

---

# SCOPE FROZEN — DRIVE TO THE SCREEN

Product owner, final directive. All architecture exploration, broad review waves, optional MIME
work and edge-case optimisation are **stopped**. Current design decisions are frozen. Any unusual
or unfinished email format **fails safely to NeedsReview** — no further TNEF, S/MIME, ICS, DSN,
AppleDouble or decorative-image work before the screen test.

**Authorized outcome, and nothing else:** real GoDaddy email → Poll Now → durable capture →
body/attachment processing → message-level assembly → Lead or NeedsReview → visible Email Intake
screen → open the resulting Lead.

## Ordered work — do not reorder, commit green at each step

| # | Step | State |
| --- | --- | --- |
| 1 | Minimum verifier + safety tests to prevent document loss, duplicate extraction, unbounded memory | B1/B2/B4 done; **B3 typed outcomes = the minimum remaining** |
| 2 | Durable canonical scheduler over `SourceDocumentOccurrence` + `ExtractionQueue`, idempotency key from `ComponentKey` | TODO |
| 3 | Split `CaptureComplete` from `SafeToAcknowledge` | TODO |
| 4 | Authoritative persisted job/component ownership + tenant validation | TODO |
| 5 | Coordinator concurrency + persisted-state return | TODO |
| 6 | Register the complete runtime dependency graph | TODO |
| 7 | Wire the real `EmailService` poller | TODO |
| 8 | Wire `ExtractionWorker` to the message-level barrier | TODO |
| 9 | Remove the legacy direct-email/component-to-Lead path | TODO |
| 10 | Minimum live API/UI: mailbox config, behavioural Test Connection, Poll Now, Email Intake list/detail, component statuses, assembly outcome/reason, Lead link, Lead provenance | TODO |
| 11 | Automated A (body-only → 1 Lead), B (body+PDF/XLSX → 1 combined Lead), C (unsupported attachment → NeedsReview, no partial Lead) | TODO |
| 12 | Start the real app, open visible Chrome for acceptance | TODO |

**No mocks, fixtures, hardcoded statuses, manual DB insertion or upload substitution anywhere in
the acceptance path.**

## Deferred until after A–C acceptance

Medium/Low findings · advanced container handling · retention purge · scenarios D–H · production
deployment · unrelated modules · B5 matrix · B2 carried safeguards · full five-reviewer re-review.

Dropped from Section B by this directive: the B5 container matrix and the B2 carried safeguards
(StructuralOnly lifecycle, raw-message bounds, nested-body normalization tests). The *behaviour*
they would have tested is already implemented and frozen; only their dedicated proofs are
deferred. Recorded here so the gap is visible rather than forgotten.

---

## Takeover verified at `e22b119`

Branch `fix/enterprise-email-lead-participation` · clean tree · 24 commits ahead of base
`1601db6` · repo `kodekinetics79/Nexora`. Handoff matches the repository. No Phase Zero repeat.

### Correction to the record

`EmailComponentManifestVerifier` has **no production caller**. An edit intended to wire it into
the enqueuer during the typed-verdict step silently matched nothing, because `ScheduleAsync` had
been reverted earlier and the replace target no longer existed. The build stayed green because
nothing referenced the old signature. The verifier and its 20 tests are correct; they are simply
not yet load-bearing. `EmailIngestEnqueuer.EnqueueAsync` still walks `message.Attachments` at
line 96 and still mints `Guid.NewGuid()` at line 53.

Nothing in this ledger claimed otherwise — steps 2 and 9 were already TODO — but the distinction
matters and is written down rather than left to be rediscovered.

## STEP 2 — the decisive contract detail

`SourceOccurrenceIdentity.BuildKey` (`Extraction/DocumentIngestionService.cs:638`) is:

```
$"{batchId:D}:{sourceType}:{sha256(metadata.SourceOccurrenceId)}"
```

**The batch id is part of the occurrence idempotency key.** That single fact explains SME3's
EIA-01 and dictates the whole of Step 2:

* With `Guid.NewGuid()` per scheduling pass (today, `EmailIngestEnqueuer.cs:53`), two pollers —
  or a poller and the recovery sweep — produce **different** occurrence keys for the same
  component, so `ux_source_document_occurrences_tenant_idempotency` never fires, a second
  occurrence is created, `UX_ExtractionJobs_BU_SourceOccurrence` never fires, and the component
  gets **two extraction jobs → two Leads for one email part**.
* With a **derived** batch id, the occurrence key is deterministic and the two unique indexes
  the repository ALREADY has collapse concurrent schedulers onto one occurrence and one job.
  No new queue, no new constraint, no advisory lock of our own — the existing durable pattern
  does the work once it is fed a stable identity.

So Step 2 needs no new idempotency mechanism. It needs exactly two things fed correctly:

1. `batchId` = SHA-256 over `"nexora:email-assembly-batch:v1:{assemblyId}:{messageKey}"`
   (never `Guid.NewGuid`, never `GetHashCode`).
2. `metadata.SourceOccurrenceId` = the **persisted** `ComponentKey`, read from the row, never
   recomputed.

### Exact next action

Rewrite `Ingestion/Triage/EmailIngestEnqueuer.cs`:

* delete `EnqueueAsync` and its `foreach (var att in message.Attachments)` loop outright;
* add `ScheduleAsync(assembly, persistedComponents, plan, ingest, clientEmail, ingestion,
  triage, coordinator, logger, ct)`;
* call `EmailComponentManifestVerifier.Verify(assembly.ManifestContractVersion,
  assembly.ExpectedComponentCount, components, plan)` **first** and hold every non-terminal
  component unless the verdict is `Compatible` — branch on the typed verdict, never on text;
* iterate persisted rows by `Ordinal`; skip `IsTerminal` (Completed/Skipped/RefusedSecurity/
  Ignored/StructuralOnly) and any row whose referenced job is verified to exist;
* schedule under the persisted `ComponentKey` with the derived `batchId`.

Then update `Services/EmailService.cs:913` and `Ingestion/Triage/EmailTriageService.cs:240`
together — deleting the walk breaks both at compile time, so they land in one increment — and
rewrite `EmailIngestEnqueuerTests`, which asserts the walk being deleted.

Verify: `dotnet build ERP_RFQ_Automation.sln` then the email/assembly filter, then full backend.


## STEP 2a — durable canonical scheduler added. Build green, callers not yet switched.

`EmailIngestEnqueuer.ScheduleAsync` exists and satisfies the Step 2 requirements:

* schedules only from **persisted** component rows, ordered by `Ordinal`;
* no MIME traversal, no `ComponentKey` recomputation — the key is read from the row;
* branches on the **typed** `EmailManifestVerdict`; `Compatible` is the only schedulable
  verdict, and the human-readable detail is never parsed for control flow;
* on any other verdict, holds **every** non-terminal component rather than scheduling the subset
  that happens to match, and returns the verdict in `EmailScheduleResult`;
* `DeriveBatchId` = SHA-256 over `"nexora:email-assembly-batch:v1:{assemblyId}:{messageKey}"` —
  never `Guid.NewGuid`, never `GetHashCode`;
* a non-null `ExtractionJobId` is verified via `IEmailInquiryAssemblyCoordinator.DurableJobExistsAsync`,
  which filters on the tenant, so a purged job is rescheduled and a foreign job can never satisfy
  the check;
* `IsTerminal` components — Skipped, Ignored, RefusedSecurity, StructuralOnly — create no job;
* full ownership tuple on every scheduled job: BusinessUnitId, EmailIngestId, AssemblyId,
  ComponentId, ComponentKey, plus the derived batch and the occurrence key the queue computes.
  The sidecar carrying them stays a diagnostic hint and authorizes nothing.

**Idempotency is the database's.** No new queue, constraint or advisory lock was added: feeding
`SourceOccurrenceIdentity.BuildKey` a derived batch id and the persisted `ComponentKey` makes
`ux_source_document_occurrences_tenant_idempotency` and `UX_ExtractionJobs_BU_SourceOccurrence`
collapse concurrent schedulers onto one occurrence and one job.

**Migration state, stated plainly:** `EnqueueAsync` and its `message.Attachments` walk are still
present and still the only thing the two call sites use. Nothing new calls them. They are deleted
in Step 2b together with both call sites, because removing them breaks `EmailService.cs:913` and
`EmailTriageService.cs:240` at compile time and the three must land atomically. This is a
migration, not a competing implementation.

Build: 0 errors. Affected: **Failed: 0, Passed: 220**. Model drift: none.

### Exact next action — Step 2b

1. `EmailInquiryCaptureResult` must carry the persisted `Components` and the planned `Manifest`
   so a caller can reach `ScheduleAsync` without re-planning on the first pass.
2. `EmailService.ProcessSingleEmailAsync`: capture → `ScheduleAsync` → acknowledge only on
   `FullyScheduled` (Step 3 then splits `CaptureComplete` from `SafeToAcknowledge` properly).
3. `EmailTriageService.ReprocessAsync`: same coordinator and scheduler; load the message through
   `IRawEmailEvidenceReader` rather than `RawEmailPath`.
4. Delete `EnqueueAsync` and the `message.Attachments` loop.
5. Rewrite `EmailIngestEnqueuerTests` — it asserts the deleted walk.
6. Register `IEmailInquiryCaptureService`, `IEmailInquiryAssemblyCoordinator`,
   `IRawEmailEvidenceReader` and the scheduler in `Program.cs` (Step 6 dependency).


## STEP 2b (partial) — ownership, worker fence, DI registration

Three of the six 2b control groups are complete and green. The atomic caller switch is not.

### 2b-1 — full ownership tuple. DONE

`DurableJobExistsAsync` → **`DurableJobBelongsToComponentAsync`**. Tenant + id was not enough:
within one tenant it would accept a job belonging to a different component or a different
message, and the component would count as scheduled while its own work was never queued — a
message waiting at the barrier forever for something nobody is running.

Now checks the whole tuple: the job's **tenant**, its **batch** (derived from the assembly, so a
job from another message cannot match), and its **`SourceDocumentOccurrenceId`** resolved from
the occurrence whose `IdempotencyKey` is rebuilt from *this* component's persisted key. Anything
short of all three is treated as unscheduled work and rescheduled.

### 2b-4 — worker safety fence. DONE

`ExtractionWorker.PersistInternalAsync` now refuses to reconcile a Lead for any job that belongs
to an email inquiry component. The signal is the **persisted component row joined on the job id**,
never the metadata sidecar — the sidecar is best-effort by its own contract and cannot be trusted
to withhold a commercial action.

The component's outcome is recorded against the message and the assembly is left visibly at its
state. Work is not lost, the operator can see where it stopped, and no Lead is invented from a
fragment. Non-email and manual-upload jobs never match the query and keep their existing
behaviour — proven by the full suite, not asserted.

This is a temporary fence, replaced by the barrier handler in Step 8. **It is not behind a
feature flag** and there is no path that restores the old email fan-out.

### 2b-5 (partial) — DI registration. DONE

`IEmailInquiryCaptureService`, `IEmailInquiryAssemblyCoordinator`, `IRawEmailEvidenceReader` and
`EmailInquiryLimits` are registered in `Program.cs`. Every one of them compiled and passed unit
tests for weeks while registered nowhere — the package was unreachable dead code at runtime and
no test noticed, because unit tests construct their subjects directly.

`EmailInquiryAssemblyRegistrationTests` (7 passed) proves each contract resolves, that the
capability resolves **as one graph** (catching a scoped dependency captured by a singleton, which
per-service resolution would miss), that **registration alone starts no background work**, and
that the frozen message limits are the committed values.

**Full backend regression: Failed: 0, Passed: 4902.** Model drift: none.

### Exact next action — remaining 2b

* **2b-3/5 atomic switch:** extend `EmailInquiryCaptureResult` with immutable component
  identities and the transient manifest; switch `EmailService.ProcessSingleEmailAsync` and
  `EmailTriageService.ReprocessAsync` (the latter via `IRawEmailEvidenceReader`, not
  `RawEmailPath`); delete `EnqueueAsync` and its `message.Attachments` walk; rewrite
  `EmailIngestEnqueuerTests`. The scheduler must re-read persisted rows rather than trusting a
  returned tracked graph, so recovery still works once the DbContext and manifest are gone.
* **2b-1 tests:** six ownership cases — correct job accepted; purged job rescheduled;
  foreign-tenant job rejected; same-tenant job of another component rejected; same-tenant job of
  another assembly rejected; stale `ExtractionJobId` repaired.
* **2b-2:** PostgreSQL concurrency — two contexts, same assembly, one occurrence, one job, no
  aborted transaction, replay returns the existing identity.
* **2b-6:** closure matrix.


## STEP 2b SAFETY CORRECTION — result loss and fail-closed

### Correction 1 (partial) — the fence was losing extraction results

`8b91785`'s fence called `RecordComponentOutcomeAsync(... Completed ...)` and returned **before**
`outcome.Result` was persisted anywhere. The extraction genuinely ran, genuinely cost money, and
its output went nowhere — while the barrier would later see a `Completed` component carrying
nothing and assemble a Lead from whatever parts happened to survive. That is silent result loss
dressed as success, and it was worse than having no fence.

**There is no durable extraction-result store in this repository.** The result has only ever
flowed straight into Lead creation; `ExtractionJob` has no result payload column and no
proposal entity exists. So the result genuinely *cannot* be persisted at this point without
building one — and the standing rule for that case is explicit: mark `FailedRecoverable`, never
`Completed`-and-discard.

The fence now records `FailedRecoverable` with reason `assembly_result_store_pending` and an
operator sentence saying the part was read but the message-level assembly is not available yet
and it will be processed again. The work is re-runnable, the stopping point is visible, and
nothing claims to have finished that did not.

**This is a hold, not a destination.** It is replaced when the durable component-result store and
the barrier handler land.

### Correction 4 — the worker now fails closed

An `ExtractionSourceType.Email` job with no unambiguous component mapping no longer falls through
to per-document Lead reconciliation — the exact path that mints one Lead per attachment. A
missing mapping means the scheduler and the worker disagree about ownership: something to see and
repair, never a licence to create commercial records. It is logged as a recoverable ownership
failure and creates no Lead.

Non-email ingestion is untouched — manual upload and watched folders are not
`ExtractionSourceType.Email` and never reach the branch. Proven by the full suite.

**Full backend regression: Failed: 0, Passed: 4902.**

### Still outstanding from the correction increment

| # | Item | Note |
| --- | --- | --- |
| 1 | Durable component-result store + migration, so the fence **completes** instead of holding | needs a new entity; no existing contract to reuse |
| 2 | Atomic enqueue→bind transaction + DB uniqueness on the component/job relationship | occurrence/job unique indexes dedupe but do not route |
| 3 | Coordinator identity by `(BusinessUnitId, AssemblyId, ComponentId)`; remove every `BusinessUnitId + ComponentKey` control-flow lookup | worker already has `AssemblyId` |
| 5 | Real raw `.eml` SHA-256 verification before any recovery/reprocess re-plan; `RawEvidenceHashMismatch` typed outcome | reader exists; verification not yet called on the recovery path |
| — | Shared `AddEmailInquiryAssembly` extension used by **both** `Program` and the test | current test copies registrations, which the directive rules insufficient |
| — | Correction test matrix + PG concurrency + SME reviews | |


## HOLD LIFECYCLE PROVEN — and the operator message was false

Branch pushed for continuity: local **`c3470218e2539c5823f4552e13fe6fb186ea9bcb`** = remote,
confirmed by local ref, `origin/` ref and the GitHub API. No `.env`, key, credential or `.eml`
fixture in the 65 changed files; the design-time `ef.sh` helper is untracked. Not merged, not
deployed.

### The claim that did not survive checking

The fence's operator sentence said the part "will be processed again automatically". **Nothing
sweeps `FailedRecoverable` components — there is no recovery service in this build.** The
sentence was untrue, and it is the same class of defect as advising a retry that cannot succeed:
the 2026-08-12 incident in miniature, written by the person who had just finished fixing it.

Corrected to: *"This part of the message was read successfully, but the step that combines it
with the rest of the email is not available yet, so the inquiry is not complete. It is being
held; no information has been lost."*

Both the reason code and the sentence are now **named constants** in
`EmailInquiryHoldReasons`, so the promise itself is assertable. The first version of that test
read the worker's source and matched substrings — a source-text assertion, which is exactly what
SME5 F4 flagged and what the directive prohibits. It asserts the constant now.

### What "held" actually means, proven

| Property | Evidence |
| --- | --- |
| not terminal — the message is never declared finished | `IsTerminal` false, `IsRecoverableHold` true |
| holds the whole message | `Evaluate` → `FailedRecoverable`, never `ReadyForAssembly` |
| never counted as captured content | `CapturedComponentCount` 0 **and** `CompletedComponentCount` 0 |
| cannot reach ready by transition either | `CanTransition(FailedRecoverable, ReadyForAssembly)` false |
| **no hot-loop / no repeated AI or OCR spend** | the component keeps its job reference, and `ScheduleAsync` skips a component whose referenced job is verified to belong to it — so re-running the scheduler does not re-extract |
| diagnosable | job reference and reason code retained |
| per-assembly isolation | same `ComponentKey` under two assemblies stays two rows |
| no Lead per component | fence returns before reconciliation; Email jobs without ownership fail closed |

**The honest reading: nothing advances until the result store lands.** That is what "held"
means, and it is why the word "retryable" is not used for it.

Hold lifecycle: **9 passed**. Full backend regression: **Failed: 0, Passed: 4911**.

### Next — the durable component-result store

No reusable contract exists, so the smallest tenant-scoped surface gets added on the frozen-
migration-safe pattern (focused follow-up migration; `20260813134002` stays byte-identical).
Then the fence completes instead of holding, and correction 3 (identity by
`BusinessUnitId + AssemblyId + ComponentId`) lands with it since both touch the coordinator.


## REGRESSION FOUND FROM A LIVE SYMPTOM — "emails picked up but not processed as RFQ"

The product owner reported GoDaddy connected, mail being polled, and nothing becoming an RFQ.
That is **exactly** what the fail-closed branch I added at `c347021` does on this branch.

`ExtractionWorker` refused a Lead for **every** `ExtractionSourceType.Email` job with no owning
component row. But capture is not wired into `EmailService` yet, so **no email job has a
component row** — the branch matched every inbound message and silently stopped all email-to-Lead
processing. Mail polled, jobs ran, nothing became an RFQ.

It also offered no protection whatsoever: a fence can only guard components that exist, and none
do until the caller switch. It was pure regression.

**Removed.** The protection is re-added in the same increment that switches the callers to
capture — when every email job genuinely has an owning component, and a missing one really does
mean the scheduler and worker disagree — with a test that a component-less email job is refused.
Not before.

Lesson worth keeping: a fail-closed guard added *ahead* of the thing it guards is not
conservative, it is an outage. The full suite stayed green through it because no test polls a
real mailbox — the same blind spot that let the `Username`/`EmailAddress` mismatch survive.

### Evidence correction — the nine hold tests are domain evidence, not behavioural proof

Recorded as the product owner required, because the distinction is real:

| Test | What it actually proves |
| --- | --- |
| no-hot-loop | **does not call `ScheduleAsync`** — it asserts the component's shape, not the scheduler's behaviour |
| per-assembly isolation | **does not touch the coordinator or a database** — two constructed entities, not two rows |
| operator message | asserts the **constant**, not what the worker persists |

They are still worth keeping — they pin the domain rules — but they are not the behavioural proof
the hold lifecycle needs. Those gaps close inside the durable-result-store increment, against the
real worker, coordinator, scheduler and database, rather than as a separate testing exercise.

### Operator message narrowed

"no information has been lost" was too broad. The **extraction output of that pass really is
discarded** — there is nowhere durable to put it yet. The sentence now claims only what survives:
*"the original captured email evidence is preserved."*

Full backend regression after removal: **Failed: 0, Passed: 4911**.

---

## DESIGN DECISION — the persisted job discriminator (answers the cutover addendum)

The addendum forbids branching on `ExtractionSourceType.Email` and requires an authoritative
persisted discriminator that is not the optional sidecar. One column answers that **and**
corrections 2 and 7 at the same time:

### `ExtractionJobs.EmailInquiryComponentId` — nullable FK, tenant-composite

```
ALTER TABLE public."ExtractionJobs" ADD COLUMN "EmailInquiryComponentId" bigint NULL;

-- one job can never belong to two components
CREATE UNIQUE INDEX "UX_ExtractionJobs_BU_EmailInquiryComponent"
  ON public."ExtractionJobs" ("BusinessUnitId", "EmailInquiryComponentId")
  WHERE "EmailInquiryComponentId" IS NOT NULL;

-- and the component it names must be the same tenant's
ALTER TABLE public."ExtractionJobs"
  ADD CONSTRAINT "FK_ExtractionJobs_EmailInquiryComponents_BU_Component"
  FOREIGN KEY ("BusinessUnitId", "EmailInquiryComponentId")
  REFERENCES public."EmailInquiryComponents" ("BusinessUnitId", "Id");
```

Why this column rather than a boolean flag or a new source type:

* **It IS the discriminator.** Non-null ⇒ canonical EmailInquiry component job. Null +
  `SourceType = Email` ⇒ pre-cutover legacy job, drained under compatibility behaviour. Neither
  ⇒ manual upload / watched folder, untouched. The worker's three cases fall out of one column
  instead of a heuristic.
* **It IS the ownership**, so the sidecar stops being load-bearing entirely — the addendum's
  requirement — and the worker resolves the owning component by FK rather than by lookup.
* **The partial unique index makes "one job, two components" impossible**, which application
  checks could never guarantee (correction 7).
* **The tenant-composite FK makes cross-tenant binding impossible even with application
  validation bypassed**, which is the PostgreSQL negative test the directive asks for.
* **A runnable-but-unowned canonical job becomes structurally impossible** once the column is
  written inside the same transaction that creates the job (correction 2) — the worker check
  then really is defence in depth rather than the routing mechanism.

`EmailInquiryComponents` already exposes the alternate key `(BusinessUnitId, Id)` that this FK
needs, so no change is required on that side.

**Migration:** one focused follow-up carrying the column, index, FK and the new result entity.
`20260813134002` stays byte-identical, as SME3 EIA-06 requires.

### Ambiguity is now a database impossibility, not a worker judgement

"Ambiguous ownership" cannot arise from two components claiming one job — the unique index
refuses it. The remaining ambiguity is a canonical job whose component row was deleted, which the
FK also refuses. So the worker's fail-closed case narrows to exactly one situation: a job marked
canonical whose component cannot be loaded, which is a genuine integrity fault worth surfacing.

### PRODUCT-OWNER DECISION REQUIRED BEFORE THE CALLER SWITCH

The addendum says: *"Do not deploy the caller switch without an explicit legacy-job
drain/compatibility decision."* That decision is **not made** and is not mine to make. Options:

| Option | Consequence |
| --- | --- |
| **Drain first** — stop the switch until the queue holds no `SourceType = Email` job with a null discriminator | cleanest; needs a quiet window |
| **Dual-run** — legacy jobs keep the existing per-document path until they age out | no window needed; two behaviours coexist briefly, and a legacy job can still mint a per-document Lead during that period |
| **Abandon** — fail legacy in-flight email jobs and re-poll their messages | simplest code; re-reads mail already marked seen, so it depends on the acknowledgement work landing first |

Recorded as an open decision, not assumed.

### Next action

One focused migration + entity, in this order: result store entity → `EmailInquiryComponentId`
column, partial unique index, tenant-composite FK → coordinator identity by
`(BusinessUnitId, AssemblyId, ComponentId)` → atomic enqueue→bind → behavioural tests that close
the three evidence gaps against the real worker, coordinator, scheduler and database.

---

## CTO DECISION ACCEPTED — drain-first, with five corrections to my design record

**Cutover: DRAIN-FIRST.** Dual-run rejected (a legacy job could still mint a per-document Lead);
abandon/re-poll rejected (messages may already be acknowledged). Compatibility exists only to
drain jobs that already exist. **There must be no period in which both producers create new email
jobs** — that is the binding constraint on the caller switch.

### Correction 1 — my design record was factually wrong. Verified.

I wrote that "`EmailInquiryComponents` already exposes the `(BusinessUnitId, Id)` alternate key
this FK needs, so nothing changes on that side."

**It does not.** Only `EmailInquiryAssemblies` declares it
(`EmailInquiryAssemblyModelBuilderExtensions.cs:32`, migration line 44). The component entity has
indexes and a composite FK to its assembly, but **no alternate key of its own** — so the
composite FK from `ExtractionJobs` would have failed at migration time. Confirmed by inspection,
not assumed. The alternate key is added and migration-tested in the focused increment.

### Corrections 2–5, accepted and recorded

| # | Correction |
| --- | --- |
| 2 | My index description was backwards. The **scalar** `ExtractionJob.EmailInquiryComponentId` is what makes each **job** single-owned; the **partial unique index** is what makes each **component** have at most one job. Two different guarantees, and I conflated them. |
| 3 | `EmailInquiryComponents.ExtractionJobId` must be **removed** from the final model — leaving it makes two ownership authorities that can disagree, which is the class of defect this whole increment exists to remove. Scheduler and coordinator queries move to `ExtractionJobs.EmailInquiryComponentId`. |
| 4 | Delete behaviour **Restrict/NoAction**. Never `SET NULL`: nulling the column silently converts a canonical job into an *apparent legacy* job, which under drain-mode rules is then treated as compatibility work — a data-shaped route back into per-document Lead creation. Never `CASCADE`: processing history is evidence. |
| 5 | `CHECK (EmailInquiryComponentId IS NULL OR SourceType = 'Email')` — a non-email job can never carry an email component. |

### Two further consequences of correction 3, recorded so they are not missed

* `RecordComponentQueuedAsync` currently writes `component.ExtractionJobId`. That write disappears
  with the column; the binding becomes part of the ingestion transaction (item C).
* `DurableJobBelongsToComponentAsync` currently reads that column to verify ownership. It becomes
  a read of `ExtractionJobs.EmailInquiryComponentId`, which is simpler and authoritative — the
  occurrence-key reconstruction it does today stops being necessary.

### Item D — content-level reuse is a real trap, and it is in the existing code

`DocumentIngestionService` reuses `SourceDocument.ExtractionJobId` for identical content hashes.
Two components carrying **identical bytes** — the same price list attached twice, the same T&Cs
across two forwards — would therefore share one job, and one component would be left unowned or
double-bound. Canonical component jobs must bypass that shortcut: one durable job per component,
always. Reuse can return later only by transactionally materialising the reused structured result
for the second component.

### Item G — the completion contract

`Completed` must never mean "output discarded". A processable component marked `Completed`
without a durable result row **holds the barrier** with a typed `result_missing` reason. That is
the permanent form of the temporary hold now in the worker.

### Drain runbook — recorded as the deployment sequence

1. Deploy expand migration + worker that reads both shapes. 2. Canonical production stays off.
3. Pause **inbound polling only** — outbound quote delivery keeps running. 4. Drain all runnable,
leased, retryable and recoverable `SourceType = Email` jobs with a null component. 5. Reconcile or
dead-letter exceptions; prove zero active legacy jobs **and zero live leases**. 6. Enable the
caller switch. 7. Null-component email jobs become fail-closed — compatibility ends. 8. Resume
polling; monitor job, component, assembly and Lead counts.

### Implementation order for the next context

A: `EmailInquiryComponentResult` entity (+ RLS, purge policy, grants, purge ordering) →
component composite alternate key → `ExtractionJobs.EmailInquiryComponentId` + partial unique
index + composite FK (Restrict) + CHECK → drop `EmailInquiryComponents.ExtractionJobId` →
typed ingestion contract carrying the component id → queue projections (`ReturningColumns`,
`MapJob`) → coordinator identity `(BusinessUnitId, AssemblyId, ComponentId)` → transactional
persist/complete/re-evaluate. One focused migration; `20260813134002` stays byte-identical.

---

## THREE SME REVIEWS — 21 findings (5 Critical, 8 High). Criticals and structural Highs resolved.

Reviewers ran read-only on `c61f73c..ab2137a`. **All three independently found the same Critical**,
which is the strongest signal in this program so far.

### Resolved in this increment

| Finding | Reviewers | Fix |
| --- | --- | --- |
| **Component identity was a PREFIX of its own unique index** | .NET EIA-1 · PG-1 · SDET F5 | `FindComponentAsync`/`RecordComponentQueuedAsync`/`RecordComponentOutcomeAsync` now take `(BusinessUnitId, AssemblyId, ComponentKey)` — the exact unique tuple — and use `SingleOrDefaultAsync`. The worker already read `AssemblyId` at the join and **threw it away**. One tenant receiving the same message in two mailboxes (CC, distribution list — routine) produced byte-identical component keys, and the write bound one message's outcome onto the other's row: one advanced on evidence it did not own, the other stalled forever. |
| **The hold re-armed the outage on a delay** | .NET EIA-2 | `ScheduleAsync` counted a held component as `alreadyScheduled` because its job genuinely existed. Once capture is wired, every component would park in `FailedRecoverable` with nothing sweeping and nothing rescheduling — zero email throughput, the `3a672dd` outage returning by another route. `IsRecoverableHold` is now re-schedulable. |
| **Missing component was a silent no-op** | .NET EIA-1 | Both coordinator writes threw away outcomes with a log line. They now throw: an unowned job is an ownership failure, not a no-op. |
| **Body paths bypassed the byte budget entirely** | .NET EIA-7 | `ChargeBytes` clamps and cannot refuse; both body paths materialised then charged unconditionally, so nested forwards carrying multi-megabyte bodies were bounded only by component count. Added `TryChargeBytes`, which refuses without deducting. This is M2 reappearing on the paths recursion added. |
| **Outcome and re-evaluation were two transactions** | .NET EIA-3 · PG-6 | Now one transaction with a bounded reload-and-retry, catching **both** `DbUpdateConcurrencyException` and SQLSTATE `40001` — the latter is what PostgreSQL raises under tighter isolation, and catching only the first would leave the stranded-message failure intact. The change tracker is cleared before retry: re-saving a failed entry would stamp it twice and race the same stale value. |
| **Contract version blind through its own rewrite** | PG-5 | Bumped to **v2**. v1 walked one level with flat keys; v2 recurses, uses hierarchical paths, treats forwarded containers as structural, plans nested bodies, and classifies inline assets by measured size. Leaving it at 1 meant a v1-captured message re-planned by v2 would pass the version check and surface as a pile of misleading per-component mismatches instead of one true "the contract changed". |
| **`HasDefaultValue(1)` was an EF sentinel** | PG-7 | Removed. It marked the property `ValueGenerated.OnAdd`, so a forgotten assignment stored `0` as `1` — defeating the mismatch detector a second way. Focused migration `20260814045818`. |

### A bug the suite caught inside this increment

Refusing an oversized body originally `return`ed the manifest, **abandoning every attachment on
the message**. An oversized covering note would have discarded the bill of quantities. The body is
now recorded as skipped and the walk continues.

Frozen-migration gate: `20260813134002` **byte-identical**. Model drift: none.
Full backend regression: **Failed: 0, Passed: 4925**.

### Open, with owners — not closed by this increment

| Finding | Sev | Why still open |
| --- | --- | --- |
| SDET F1 — the fence is **dead code under test** | Critical | **CLOSED** — `LeadPersisterEmailFenceTests`, 5 tests on `TestDb` with a recording coordinator |
| SDET F2 — **no end-to-end test** of capture → schedule → extract → assemble | Critical | The scheduler ships wholly unexercised |
| SDET F4 — the PG isolation negative test **passes with RLS dropped** (FK swallow means the row is never inserted) | High | Must seed the parent and assert the row genuinely exists first |
| .NET EIA-4 — **`ReprocessAsync` is a second legacy producer**; pausing polling does not stop it | High | Drain runbook needs step 3b gating it |
| .NET EIA-5 — `SourceDocumentOccurrenceId` is a **third ownership authority**, unaddressed by the plan | High | Demote to evidence-only; it is many-to-one under content reuse |
| PG-3 — `ExtractionJobs` DDL would take `ACCESS EXCLUSIVE` on the hot queue | High | Split into `NOT VALID` + `VALIDATE`, index `CONCURRENTLY` |
| PG-4 — `RESTRICT` from jobs conflicts with the child `CASCADE`; also the purge-ordering item is a **misdiagnosis** (`session_replication_role = replica` already makes order irrelevant) | High | Reopen the delete-behaviour decision |
| SDET F3 — registration test **copies** `Program` registrations | High | Shared `AddEmailInquiryAssembly` extension |
| .NET EIA-6 — legacy + canonical processing of the **same message** across the cutover yields two Lead sets | Medium | Drain gate must assert on occurrences, not just the queue |


## SDET F1 CLOSED — the fence is exercised for the first time

`LeadPersisterEmailFenceTests` runs on `TestDb` (real model, SQLite in memory, FKs and unique
indexes enforced) with a **recording** `IEmailInquiryAssemblyCoordinator`, so assertions are on
the arguments the worker actually passes rather than on constants read back in isolation.

| Test | Property |
| --- | --- |
| email job with **no** component is not swallowed | persistence proceeds PAST the fence into ordinary email handling; under the regression it returned 0 silently and nothing threw |
| email job **with** a component | zero Leads; the recorded outcome carries the right tenant, the **AssemblyId the worker read at its join**, the component key, `FailedRecoverable`, the reason code, and the operator detail |
| held component | never reported `Completed` — that would tell the barrier a part was done while its output was discarded |
| manual upload | unaffected, even with an unrelated component row present for another job |
| coordinator absent | fence is inert and does not stop processing |

The fixture seeds the real parent chain — business unit → mailbox → ingest → assembly → component
— because a fixture that skips parents proves nothing about behaviour under real constraints.

**This is the class of test whose absence let the fail-closed outage ship with 4911 green.**

Full backend regression: **Failed: 0, Passed: 4930**.


## SDET F3 / F4 CLOSED — two tests that could not fail (`3a4522e`)

Both asserted properties of themselves rather than of production.

**F3.** `EmailInquiryAssemblyRegistrationTests` re-declared the registrations it was meant to
guard, so deleting every `AddScoped` from `Program` left it green. The composition now lives in
`AddEmailInquiryAssembly()` and the test calls that same extension, with `ValidateOnBuild` so a
scoped dependency captured by a singleton is caught at build time.

**F4.** The tenant-isolation negative test swallowed the FK violation from an unseeded parent, so
tenant A's row was never inserted and `count(*) = 0` held for trivial reasons — it passed with
row-level security dropped entirely. It now seeds the real parent chain, proves the owner can read
the row, then proves a second tenant can neither read, update nor delete it, and that the row is
unchanged afterwards.

**Mutation-proved:** with `ROW LEVEL SECURITY` disabled on `EmailInquiryAssemblies` the test fails
(expected 0, actual 1). Restored, it passes. That is the evidence the old version could not offer.


## SDET F2 CLOSED — the vertical slice runs end to end (`1311617`, `ca86def`)

`EmailToLeadVerticalSlicePostgreSqlTests`. One message — a covering note and two priced CSV
attachments — through a migrated PostgreSQL container and the production composition: capture,
manifest, scheduling, `DocumentIngestionService`, the real advisory-lock queue claim, the real
`ExtractionWorker` loop, the real `ProductionDocumentReader` reading bytes back out of evidence
storage, the real `LeadPersister`. Out comes **exactly one Lead carrying all five lines in the
order the buyer wrote them**.

No recording queue, no recording persister, no seeded result, no SQLite, no source-text assertions.
The single substituted boundary is the language model, and `RefusingLlm` throws on any call — so
the CSVs taking the deterministic path is an assertion, not an assumption.

### Four defects the slice found that every existing test missed

Each was invisible because the seam in question was proven against a double.

| # | Defect | Why it was invisible |
| --- | --- | --- |
| 1 | Capture wrote the raw `.eml` to evidence zone `"inbound-email"`. Both providers whitelist `quarantine\|cleared` and throw `ArgumentException` otherwise — outside the storage-unavailable contract capture catches. **Capture failed on every message, on every provider.** | every capture test substituted `IEvidenceObjectStorage` with a double that accepted any string |
| 2 | The claimed job was materialized by raw SQL that did not select the new ownership column, so it came back null and read exactly like a legacy per-document job — sending an email component past the barrier into its own Lead | no test claimed a job through the real queue and then inspected ownership |
| 3 | **The assembly never left `Captured`.** Scheduling moved components to `Extracting` but never re-evaluated the message, so when the barrier finally said `ReadyForAssembly` the transition was illegal, was logged, and was discarded. Every component completed with a durable result and no message was ever assembled | the state machine was tested as a pure function; nothing drove it through the real scheduling path |
| 4 | `VALIDATE CONSTRAINT` against a `FORCE ROW LEVEL SECURITY` table is refused for the table owner, and migrations run as the owner on the managed target. **The deployment would have stopped at this migration** while passing everywhere migrations run as a superuser | the ordinary test fixture migrates as the container superuser |

### What was built

- **`EmailInquiryComponentResults`** — the durable, versioned store the worker had nowhere to write
  to. Its absence is why the fence could only hold: the honest options were to discard a paid-for
  extraction or stall the message, and it stalled. A payload contract version this build cannot
  read sends the message to review rather than being coerced. RLS enabled and forced,
  tenant/pipeline/purge grants, purge policy, `USAGE`-only on the sequence.
- **`ExtractionJobs.EmailInquiryComponentId`** — ONE ownership authority, written with the INSERT.
  Binding it in a second statement leaves a window in which a worker can claim a job whose owner is
  not yet visible. Composite FK carries the tenant; a `CHECK` confines it to email jobs using the
  string the provider actually stores, not a guessed enum ordinal.
- **`RecordComponentResultAsync`** — result, completion and re-evaluation in ONE transaction.
- **`EmailInquiryLeadAssembler`** — merges every component's durable result into one Lead in ordinal
  order, so a buyer's schedule is not silently reordered.

### Judgement calls worth recording

- **`CREATE INDEX CONCURRENTLY` was considered and rejected.** It cannot run inside a transaction,
  and every migration here is applied inside one. Splitting that contract risks a half-applied
  schema and an INVALID index for an index that builds over zero existing rows. `lock_timeout`,
  `NOT VALID` + `VALIDATE`, and a partial index carry the load instead.
- **Assembly is orchestration and belongs to the worker.** The first attempt injected the assembler
  into `LeadPersister`, which is a dependency cycle (the assembler must persist the Lead it builds).
  The container rejected it and three jobs dead-lettered on a circular-dependency message.
- **The assembler is registered beside `ILeadPersister`, not in `AddEmailInquiryAssembly`** — it
  depends on extraction, and putting it in the capability made the capability unresolvable alone.

Full backend regression: **Failed: 0, Passed: 4935**. Frozen migration byte-identical.

### Known gap, deliberately not closed in this increment

The worker completes the queue job and assembles the message **after**. If the process dies in
between, the job reads `Succeeded` and the message sits at `ReadyForAssembly` with no Lead — and
nothing in this build sweeps that state. The ordering is still correct (assembling first would
duplicate the Lead on retry), so the fix is a recovery sweep, not a reordering. Recorded here
rather than built, and it is the next item.


## SME REVIEW OF THE BARRIER — Critical closed (`22ebd5e`)

Three bounded read-only reviews of `ca86def`. The headline finding was one the
end-to-end test **could not see**, and it failed for the same reason the defects it did
find were invisible: it differed from production in the dimension under test.

**CRITICAL — every non-ambient transaction would have thrown in production.** `Program.cs`
configures `EnableRetryOnFailure`, installing `NpgsqlRetryingExecutionStrategy`, under which a
user-initiated `BeginTransactionAsync` outside `CreateExecutionStrategy().ExecuteAsync` throws
outright. The slice test built its context WITHOUT it, so `MarkAssembledAsync`,
`HoldForReviewAsync`, `RecordComponentQueuedAsync` and `RecordComponentOutcomeAsync` all passed
locally and would each have thrown on the first real message — Lead created, job Succeeded,
message never marked Assembled. The repository had paid for this before: `GeneralLedgerService`
carries the same note after it made every ledger write throw against PostgreSQL.

The test now configures the production retry policy, and **reproduced the failure before the
fix**.

| Also closed | Why it mattered |
| --- | --- |
| retrying inside an AMBIENT transaction | 40001 has already aborted it, so attempt two fails 25P02 with the real conflict masked; and `ChangeTracker.Clear()` emptied the caller's tracked-Lead snapshot |
| the retry dropped the component's completion | the entity was loaded OUTSIDE the retried delegate — detached on retry AND still carrying attempt one's `Completed`, so the guard skipped the write |
| check-then-act on `ReadyForAssembly` | two workers could both build a Lead; the only thing preventing it lived three layers away in the identity service |
| a Lead no message pointed at | `AssembledLeadId` added; the old lookup could only ever return null, so the idempotency contract could not be honoured |
| raw `.eml` in the `quarantine` zone | the retention purge deletes the sibling key derived by swapping `/quarantine/` ↔ `/cleared/`, and `.eml` is intake-allowed — purging a document would have destroyed an assembly's authoritative raw message, silently. Now its own `raw-mail` zone |

Plus: the ownership index is UNIQUE; 23505 is classified as a concurrency conflict; the result
token is stamped with the rest of the aggregate rather than at the call site; provenance is
MERGED so a message of deterministic parses stops being reported as external-AI-derived; two
silent dead ends now hold for review; every `Completed` component must have contributed a
result; the migration asserts FORCE RLS is back on; and the comment claiming a concurrency
benefit from `NOT VALID` + `VALIDATE` now records that this migration does not get one.


## RECOVERY SWEEP — the stranded message is now impossible to lose (`b4a08db`)

The worker commits the queue job and assembles **after**. That order is correct — assembling
first means a crash in between is retried and builds the Lead twice — but it left a window with
nothing watching it. A process dying in between left every job `Succeeded`, every result durable,
and the message at `ReadyForAssembly` with no Lead, no error, no dead letter, and nothing that
would ever look again. The window is entered on every deploy, not only on crashes.

**`ReadyForAssembly` with a null `AssembledLeadId` IS the work item.** No outbox, no retry table,
no requeue, no re-extraction, no re-reading of evidence. A second record of the same fact is how
two sources of truth start disagreeing.

### Two real defects the failure-injection proof found

| # | Defect |
| --- | --- |
| 1 | **`SELECT ... FOR UPDATE` through EF did not serialize two concurrent recovery instances — both built a Lead.** Materializing the row via `FromSql` looks equivalent to a lock and is not: EF wraps raw SQL as a subquery to apply the global query filter, and the entity returned goes through identity resolution, so the value the code reasons about need not be the row just read. Replaced with a compare-and-swap — a conditional `UPDATE` guarded on `Status` and `ConcurrencyVersion` — which takes the row's write lock, blocks the loser until the winner commits, then fails its `WHERE`. |
| 2 | **The claim's loser reported a recovery it had not performed.** Returning the winner's lead id was true and useless: a caller counting outcomes recorded two recoveries for one Lead, and "a Lead exists" became indistinguishable from "I built it" in every metric downstream. Null now means "not by me". |

Both were found by an applied test, not by reasoning — the first was diagnosed only after an
instrumented run showed one claim, one persist, and still two Leads.

### What the proof asserts

Real PostgreSQL under the production retry policy. The crash is injected by replacing ONE
registration — the assembler — so the worker stops where a dying process would; **nothing that
writes is substituted**, so the stranded state is produced by the real queue, worker, persister
and coordinator. Process loss is simulated by disposing the entire `ServiceProvider`, and
recovery runs on a graph sharing nothing but the database.

- the stranded state in full: jobs `Succeeded`, results durable, components `Completed`,
  assembly `ReadyForAssembly`, `AssembledLeadId` null, zero Leads;
- recovery produces exactly one Lead with all five lines in the buyer's order;
- sweeping again recovers nothing, creates no second Lead, and re-runs no extraction;
- two concurrent instances produce one Lead and agree on its id;
- cancellation inside the assembler transaction leaves no partial Lead, still recoverable;
- a replay after success returns the same Lead, twice;
- a poisoned message is held for review while another tenant's is recovered in the same sweep;
- a tenant can neither discover nor recover a neighbour's assembly;
- `Captured`, `Extracting`, `NeedsReview`, `Assembled`, legacy email jobs and manual uploads are
  all left alone.

The graph now lives in `EmailToLeadHarness` so every test on this path shares ONE definition of
the production composition — a second copy is how the retry-policy blindness happened once.

Full backend regression: **Failed: 0, Passed: 4944**. No model drift. Frozen migration
byte-identical.

### Still open, and deliberately not in this increment

**Caller cutover.** `ScheduleAsync` has no production caller: live mail still takes the legacy
per-attachment route through `EmailService` and `EmailTriageService.ReprocessAsync`. Everything
above is 0% of production traffic today and 100% of multi-part inquiry mail the day capture is
wired. The cutover must gate BOTH callers and drain occurrences, not just queue rows.


## CALLER CUTOVER — both producers, then the guardrails that make it a cutover (`391c306` + this increment)

`391c306` moved both production callers onto `EmailInquiryIntakeService` and deleted the
`message.Attachments` fan-out with them. That closed the "Still open" item above: `ScheduleAsync`
has production callers, and it is the only scheduler.

Deleting the second door is not the same as closing the first one. Two things were still missing,
and they are what turn a switch into a cutover.

### 1. The worker now FAILS CLOSED on a component-less email job

An `ExtractionJob` with `SourceType = Email` and a NULL `EmailInquiryComponentId` used to fall
through to per-job Lead reconciliation. That was step 7 of the drain runbook recorded above, and
it was deliberately not taken while capture was unwired — a blanket refusal then returned 0 for
**every** email job, because no email job had a component. Mail was polled, jobs ran, nothing
became an RFQ, and 4911 tests stayed green. It was found by a product owner looking at a screen.

**That reason is gone.** Both producers are canonical, and `ScheduleAsync` writes the component id
WITH the job row. So a component-less email job means the scheduler and the worker disagree about
the same message, and the per-document Lead it would mint — a covering note priced without the
schedule attached to it — is the exact defect the barrier exists to remove.

The fence has two outcomes, and the split is not cosmetic:

| The job's message | What happens | Why |
| --- | --- | --- |
| has an assembly | `HoldForReviewAsync` with `assembly_ownership_unresolved`, no Lead, job completes | The message exists as an aggregate, so the operator gets a message-level hold on the screen they already read. Retrying the job cannot help — a job never gains a component id — so failing it would only burn attempts before dead-lettering. |
| has no assembly | the job fails with a reason naming only the job and the tenant | This is pre-cutover work. There is nothing to hold; the honest answer is "this cannot produce a lead, reprocess the message". |

Neither outcome loses anything: raw evidence, sibling components and the ingest row are untouched,
and `POST /api/email-triage/{id}/reprocess` re-enters the canonical door.

The refusal is gated on the assembly coordinator being registered, exactly as the component fence
above it is. A container without the capability (the lease/heartbeat harnesses) has no assembly to
be inconsistent with, and an unregistered capability must degrade to pre-fence behaviour rather
than silently stop ingestion — that is the same lesson, spelled the same way.

### 2. THE DRAIN BOUNDARY, stated and countable

**The boundary:** an email `ExtractionJob` created before the caller cutover carries no
`EmailInquiryComponentId`. From this increment those jobs **cannot produce a Lead**. There is no
dual routing and there will not be one: a compatibility path that stays becomes the path, and the
migration is then never finished by anyone.

**Draining one:** reprocess the message from the inbound mail triage surface
(`POST /api/email-triage/{id}/reprocess`). It captures the message, plans the real MIME tree and
schedules component jobs — the same operation the poller performs, with a different trigger. The
legacy job is left as history.

**Counting what remains:** `GET /api/operations/readiness` reports `legacyEmailIntake` —
`inFlight` (email jobs with no component still Pending/Leased/Extracting/Persisting) and
`oldestCreatedOn`. A non-zero `inFlight` is also a **blocking reason** on that response, because
these jobs cannot finish and the mail behind them looks ingested while producing nothing. Terminal
and dead-lettered legacy jobs are deliberately excluded: history does not drain, and a permanently
red flag is a flag nobody reads.

No new controller, no new table, no new queue. The readiness surface is where an operator already
goes to ask what is stuck.

### 3. What the tests now hold

`EmailCallerCutoverTests` pins the two properties the cutover is made of, neither of which had any
coverage — a future edit could have reintroduced a second door to the queue with the whole suite
green, which is how the two callers drifted apart the first time.

- the poller hands the message to `IEmailInquiryIntakeService`, with the durable ingest row;
- the poller does **not** acknowledge when capture fails (the message stays unread and the ingest
  row survives, so the next cycle re-fetches rather than starting from nothing);
- a message the gate stops is recorded and never reaches capture, and is still acknowledged;
- reprocess reads the original through `IRawEmailEvidenceReader`, proven by **disagreement**: the
  local `RawEmailPath` file is a different message from the one the reader returns, and what
  reaches the intake is the reader's copy;
- reprocess refuses when capture did not complete;
- `EmailIngestEnqueuer.EnqueueAsync` does not come back.

`LeadPersisterEmailFenceTests` gains the two fence outcomes above, including that the failure
reason names no file, no sender and no storage path.

### 4. Two truthfulness fixes found while doing it

**`EmailTriageService.CountRawEmailAttachments` was the deleted walk, still running.** The list
screen opened the stored `.eml` and counted `message.Attachments` for any message that produced no
jobs — wrong twice over. It is not the MIME tree (MimeKit yields only entities whose
Content-Disposition says "attachment", so a forwarded enquiry counted zero), and the path it read
is container-local storage that does not survive a deploy on the managed target, so the number
silently degraded to "unknown" for historic mail. It now counts the **persisted components**, which
are what the pipeline actually planned, cost no file I/O, and are tenant-scoped. Nothing on the
read path touches the filesystem any more.

**`POST /api/Email/fetch` reported the HTTP call, not the mail.** "Email data fetched and inserted
into the database successfully" was returned identically by a cycle that turned four emails into
four inquiries and by one that rejected three as noise and could not queue the fourth's attachment.
The poll outcome now carries messages found / downloaded / already ingested / captured, components
scheduled, held for review, rejected, and not acknowledged — per mailbox and as totals — and the
`mailboxes` field keeps its existing numeric contract, because the web client treats its absence as
"nothing polled".

### 5. The Email Intake surface now shows the message, not just the decision

List and detail return assembly state and reason, subject/sender/received time, component totals
(expected/completed), the assembled Lead id, and ingestion/last-change timestamps; detail adds every
component with its filename, kind, state and failure reason. Detail is the SAME row the list
renders with `components` filled in — not a second shape, which is how a list and a detail screen
come to disagree about one message — and `components` is **null on the list**, because "not asked
for" and "asked, and there are none" are different answers.

The fields are FLAT on the row rather than nested under an `assembly` object, and that is a
decision about the consumer: the Email Intake screen reads these names off the row and degrades an
unrecognised name to "not reported", so a nested shape would have rendered the whole panel blank
while every value was present and correct on the wire.

**One thing is deliberately not reported: a recovery timestamp.** Nothing durable records that a
message was recovered rather than assembled inline — the sweep and the worker's barrier write the
same `UpdatedAtUtc` — so the field is named `lastUpdatedAtUtc` and says what it is. Distinguishing
the two needs a column, and a schema change was out of scope for this increment.

Evidence metadata is limited to size, content type and **whether** a digest and an evidence object
exist. No URI, no bucket, no key, no
local path leaves the server — a storage location is useless to the reader and a map of the
evidence layout to everyone else, and a test serializes both surfaces and asserts neither carries
one. Tenant, module permission and the `EmailIntake` entitlement on the detail endpoint are the
list's own, unchanged.


## DEMO CLOSURE — what the first live run found

The stack: PostgreSQL, a GreenMail IMAP/SMTP server, the API with its extraction and recovery
workers, and the web client — all local, all throwaway. Outbound email is contained through the
product's own `POST /api/Mailbox/outbound/pause`, which reports `canSendToCustomers: false`.
Demo messages carry a `DEMO-<run>` subject marker so a run can never be confused with real mail.

### Three defects the live run found that no test had

| # | Defect | Why no test caught it |
| --- | --- | --- |
| 1 | **`GoldenCommercialJourneySeeder` seeded nothing.** `GoldenJourneySeed:Enabled=true` without `GoldenJourneySeed:Password` refuses — correctly, it must never invent a credential — but it did so with ONE `LogWarning` whose message never reached the aggregated output. The observable symptom was 0 tenants, 0 users, 0 mailboxes and a run that looked like a silent no-op. | nothing asserted the refusal was *observable*, only that it refused |
| 2 | **`MailEndpointPolicy` made local end-to-end mail testing impossible.** Loopback was refused in every environment, and BOTH the Test Connection probe and the poller enforce it, so no mail sink a developer can run was reachable. The one path that loses a customer's mail could only ever be tested against doubles. | by design; the class comment argued the trade-off explicitly |
| 3 | **Usage metering reads the platform `Tenants` table as `nexora_tenant_app`.** `ExtractionWorker` pushes the tenant scope, so the interceptor switches roles; the metering lookup then hits a table only `nexora_pipeline_app` is granted on. Live symptom: `42501: permission denied for table Tenants`, jobs stuck Pending, retrying forever. **Pre-existing, and it would affect every extraction under production role topology.** | the RLS lane did not register `UsageMeteringService`, so the path was never exercised under a real role |

Defect 3 is the one that justifies the whole exercise. It is not an email defect at all — it sits
in the shared extraction persist path — and it was invisible to 4,956 passing tests because every
one of them ran as a superuser, where role grants do not apply.

### The loopback allowance, and why it is not a bypass

The class it modifies argued against exactly this: "an environment-conditional bypass in an SSRF
control is exactly the kind of flag that reaches production set the wrong way." That objection is
answered STRUCTURALLY rather than by discipline. The environment is a **parameter to the enabling
call**, not a read inside it, so there is no key, variable or appsettings file that can grant the
allowance on a non-Development host — a production deployment carrying the flag set true is a
no-op with a loud log line, not a hole. It is scoped to **loopback only**: private, link-local and
carrier-grade-NAT ranges stay refused everywhere, because the risk this control exists for is a
mail server dialling internal infrastructure, and 127.0.0.0/8 reaches only the machine already
running the code. Five tests pin it, including the one that matters — a Production host cannot
grant it even when the key is true.

### Local model, no egress

Unstructured (prose body) extraction requires a model, and the AI gate refuses EXTERNAL
processing of unstructured documents outright — it did so on the first live poll. Pointing the
app at a loopback Ollama resolves the provider class to `Local`, which the gate permits:
`reason=loopback_endpoint ... no third-party egress`. No message content leaves the machine.

### Defect 3, resolved: the platform plane executes as the platform role

`ExtractionWorker` pushes the tenant scope, so the persist transaction runs as
`nexora_tenant_app`. The usage-metering block inside it is entirely PLATFORM plane: it reads
`Tenants` and `RateCards` and writes `UsageEvents`, `UsageEventRatings` and
`UsageMinuteAggregates`. The tenant role holds column-level `SELECT` on six `Tenants` columns and
nothing else on that list — not even `Tenants."RateCardId"`, which the projection reads. Live
symptom: `42501`, the job failed, and the queue re-leased it forever.

Two fixes were possible and the choice matters. Granting the tenant role what it lacks is
defensible for `Tenants` alone — a tenant reading its own tenant row is not an escalation, which
is exactly why the six column grants already exist — but it does NOT extend to the rest of the
block. Making metering work under the tenant role means granting `INSERT` on the billing ledger:
`UsageEvents`, `UsageEventRatings`, `UsageMinuteAggregates`. That is the one plane a tenant must
never be able to write, because it is what the platform charges them from.

So the block switches ROLE instead, through a scoped `PlatformPlaneExecution` the interceptor
resolves before the tenant. The statements run as `nexora_pipeline_app` and switch back on
dispose, inside the same transaction as the tenant-plane write, because the two must commit
together. The tenant GUC is deliberately left alone: the block cannot be used to blank a tenant
scope, and the next tenant-plane command re-issues both.

### Two harness bugs, recorded because both cost a full cycle

Neither was a product defect, and both would recur:

- **The demo sender omitted `Date` headers.** The poller searches IMAP with `SENTSINCE`, which
  matches the Date HEADER, not the server's `INTERNALDATE`. Messages without one are invisible to
  the poll window — two runs found zero mail and the pipeline looked broken.
- **`Security:SecretProtectionKey` was regenerated per launch.** Every stored mailbox credential
  became undecryptable on the next restart (`AuthenticationTagMismatchException`), which surfaces
  as a mailbox that simply stops polling. Pinned for the demo stack.

A third was a cleanup error worth stating plainly: clearing `ExtractionJobs` without clearing
`source_document_occurrences` left the content-addressed reuse path bound to deleted jobs, and
the next ingest failed on the foreign key. The evidence ledger is part of the pipeline's state,
not a side table.
