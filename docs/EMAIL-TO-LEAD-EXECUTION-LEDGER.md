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
