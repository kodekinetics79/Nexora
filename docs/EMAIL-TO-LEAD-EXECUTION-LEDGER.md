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
| B2 real recursion + one shared budget across the tree | M7, SME1 F3 | TODO |
| B3 verifier completeness (filename/MIME/disposition/reason/depth, dense ordinals, set equality, typed version hold) | M3 | TODO |
| B4 inline classifier without declared size; screenshot never silently ignored | M5, SME1 F5 | TODO |
| B5 container formats — TNEF wiring, S/MIME security-gated, appledouble/ics/DSN | M6 | TODO |

**B5 inventory (done):** the repository has **no** existing reader for TNEF, S/MIME, calendar or
DSN. MimeKit 4.16 supplies `TnefPart.ExtractAttachments()` (safe wiring, no new parser) and
`ApplicationPkcs7Mime`. Decrypting S/MIME needs a `SecureMimeContext` with keys, which this
deployment does not have — so encrypted S/MIME must be classified security-gated and surfaced as
`NeedsReview`, never represented as extracted. No new parsers are to be written.
