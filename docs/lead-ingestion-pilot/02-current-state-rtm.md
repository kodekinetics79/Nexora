# Lead / RFQ Ingestion — Current-State Requirements Traceability Matrix

Requirements FR-RFQ-01 .. FR-RFQ-08 traced against executing code and live production data.

**Conventions**
- `VERIFIED` requires all five: file path + class/function, API route, DB entity/migration, UI route/component,
  and a test **or production-data** proof that the path *executed*. "The code exists" is not verification.
- All file paths are relative to `Backend/ERP_RFQ_Automation/` unless prefixed `Frontend/`.
- Production evidence gathered read-only against the live Neon database and Render service
  `srv-d9csjhe1a83c739phue0` on **2026-08-06 02:10–02:30 UTC**. Deploy in service at time of audit:
  `dep-d9puheb7uimc7384ovk0` (live 02:05:24Z).

**Section ownership** — append only; do not rewrite other agents' sections.

| Section | Requirement | Owner |
|---|---|---|
| §1 | FR-RFQ-01 Ingestion channels | Lead Architect |
| §2 | FR-RFQ-08 Source evidence | Lead Architect |
| §3 | FR-RFQ-02 Format support | Implementation Engineer |
| §4 | FR-RFQ-04 Extraction fields | Implementation Engineer |

---

## §1 — FR-RFQ-01: INGESTION CHANNELS

> **Requirement.** (a) A dedicated email mailbox monitored continuously; (b) manual drag-and-drop
> upload, single **and** batch; (c) a watched network / SFTP / SharePoint folder.

### 1.0 Production channel census (the ground truth all rows below are scored against)

Every door in the codebase funnels into one gateway, `Extraction/DocumentIngestionService.cs:109
IngestAsync`, which stamps `ExtractionJob.SourceType` from the
`ExtractionSourceType` enum (`Extraction/ExtractionJob.cs:9-16`: `Email`, `ManualUpload`,
`ExcelTemplate`, `Folder`). That column is therefore an exact per-door production counter.

```sql
select "SourceType","Status",count(*) from "ExtractionJobs" group by 1,2;
```

| SourceType | Jobs ever created | Verdict |
|---|---|---|
| `ManualUpload` | **57** (21 Succeeded, 22 DeadLetter, 10 Pending, 4 Extracting) | the only door that has ever run |
| `Email` | **0** | never produced a job |
| `Folder` | **0** | never produced a job |
| `ExcelTemplate` | **0** | never produced a job — confirms the charter's Excel-template finding |

Corroborating counters:

| Query | Result |
|---|---|
| `source_document_occurrences` by `source_metadata->>'sourceType'` | `ManualUpload` **288**, `Email` **1** (that one `Rejected`, 2026-07-28T17:46:54Z), `Folder` **0** |
| `LeadIngestionOccurrences` by `SourceChannel` | `ManualUpload` **12** (ActorType `Service`), `Legacy` **23** (ActorType `Migration` — a one-shot backfill on 2026-07-28T13:23:34Z, not an ingestion event) |
| `Leads` by `LeadSource` | `ManualUpload` 22 (2026-07-16 → 2026-08-06), `Email` 10 (**all 2026-05-12 → 2026-06-17**), `Manual` 6 (May 2026), `Aramco Leads` 2 (2026-05-13) |
| `Leads` by month | 2026-05: Email 8 / Manual 6 / Aramco 2 · 2026-06: Email 2 · **2026-07: ManualUpload 5** · **2026-08: ManualUpload 17** |

**Reading.** The 10 `Email` leads and 2 `Aramco Leads` predate the durable ingestion ledger (which
begins 2026-07-28) and were produced by the *legacy* inline-extraction paths, not by the current
pipeline. Since 2026-07-16, **100 % of production leads came through manual upload.**

### 1.1 Channel (a) — dedicated email mailbox, monitored continuously

| Field | Finding |
|---|---|
| **Requirement ID** | FR-RFQ-01(a) |
| **Requirement text** | A dedicated email mailbox is monitored continuously and every inbound message is ingested. |
| **Status** | **DEFECTIVE** — the door is fully built and the poller is running, but it has been failing IMAP authentication on every cycle for at least the last 5 hours and reports success while doing so. |
| **Existing implementation** | Poller: `Services/EmailBackgroundService.cs:22` (`class EmailBackgroundService : BackgroundService`), `:42 ExecuteAsync`, `:108 RunPollCycleAsync`, `:114 emailService.FetchAndSaveLeadsAsync()`. Registered `Program.cs:346 AddHostedService<EmailBackgroundService>()`. Single-leader gate: `EmailBackgroundService.cs:24` lock name `nexora:email-poller`, acquired `:57 PostgresAdvisoryLease.TryAcquireAsync`. IMAP client: `Services/EmailService.cs:126 new ImapClient()`, `:128-131 ConnectAsync/AuthenticateAsync`, `:133 inbox.OpenAsync(FolderAccess.ReadWrite)`, search `:136`. Per-message: `Services/EmailService.cs:200 ProcessSingleEmailAsync`. Triage: `Ingestion/Triage/DeterministicEmailTriage.cs:60 Evaluate`, `Ingestion/Triage/EmailBodyNormalizer.cs:97 Normalize`, `Ingestion/Triage/SenderPartyResolver.cs:50 ResolveAsync`. Fan-out to the queue: `Ingestion/Triage/EmailIngestEnqueuer.cs:34 EnqueueAsync` → `:143` (attachments, `ExtractionSourceType.Email`) and `:177` (body). Manual trigger: `Controllers/EmailController.cs:26 POST api/Email/fetch`. Replay: `Controllers/EmailTriageController.cs:45 POST api/email-triage/{id}/reprocess` → `Ingestion/Triage/EmailTriageService.cs:200 ReprocessAsync`. Config entity `Email_Configurations`; interval read at `EmailBackgroundService.cs:161-184 ResolvePollIntervalAsync`. |
| **Exact evidence** | **The loop runs.** Render logs: `Email Background Service started.` at 02:05:19.814Z, and `Starting email fetch...` at 21:43:26Z, 22:43:32Z, 23:06:54Z, 23:07:56Z, 00:05:32Z, 00:23:41Z, 01:23:48Z, **02:05:20Z** — a ~1 h cadence matching `Email_Configurations.PollingInterval = 3600` for the single active IMAP row (ID 5, `info@intelliflowsystem.com`, `mail.spacemail.com:993`, `UseSSL=t`, `IsActive=t`, `BusinessUnitID=1`). **The mailbox connection fails.** Every one of those cycles is immediately followed by `IMAP error for config: info@intelliflowsystem.com` and `MailKit.Security.AuthenticationException: Authentication failed.` at `MailKit.Net.Imap.ImapClient.AuthenticateAsync` — logged at 21:43:32Z, 22:43:39Z, 23:07:01Z, 23:08:03Z, 23:46:36Z, 23:47:54Z, 00:05:39Z, 00:23:48Z, 01:23:55Z, **02:05:27Z**. **And it reports success anyway.** The very same timestamps also emit `Email fetch completed successfully.` (02:05:27.206Z, 1.5 ms after the auth exception at 02:05:27.204Z). The last cycle that actually reached the mailbox: `Found 0 potential RFQ emails for info@intelliflowsystem.com` on **2026-07-30T11:47:35Z**. **Production data agrees:** `ExtractionJobs` where `SourceType='Email'` = **0 rows, ever**. `EmailIngests` = 40 rows, newest **ID 880, 2026-07-28T17:46:49Z** (a ZoomInfo marketing mail, `ParseStatus='Failed'`); every other row is dated ≤ 2026-07-22, and 9 of them carry a Windows `RawEmailPath` (`D:\Sites\site39520\wwwroot\...`) proving they are pre-migration legacy. `EmailIngests.TriageOutcome` is **NULL on all 40 rows** — the triage service shipped 2026-08-05 (`Migrations/20260805210619_EmailTriage.cs`) and has never decided a real message. |
| **Missing behaviour** | 1. Working mailbox credentials — no message has been fetched since 2026-07-30. 2. A health signal that goes red on mailbox-connectivity failure. 3. Any tenant other than BusinessUnitID 1 has no mailbox row at all, so the channel does not exist for BUs 2/4/5/6. 4. No mailbox-level "last successful poll" timestamp is persisted anywhere — the only trace is a log line that rolls off. |
| **Defects** | **D-01 (Sev 1, silent-loss class).** `Services/EmailService.cs` catches the IMAP failure inside `ProcessConfigAsync` and only logs it; control returns normally to `EmailBackgroundService.cs:114`, which then logs `Email fetch completed successfully.` at `:115`. A total mailbox outage is indistinguishable from an empty mailbox in both the logs and the data. **D-02 (Sev 1).** `EmailBackgroundService.cs:100 _heartbeats?.Beat(...)` executes unconditionally after the cycle, so the `email-poller` liveness check registered at `Program.cs:187` stays green through an indefinite authentication outage — the heartbeat proves the *loop* is alive, never that the *mailbox* is reachable. **D-03 (Sev 2, silent loss).** `Services/EmailService.cs:44 SEARCH_DAYS_BACK = 7` combined with `:136 BuildRFQSearchQuery(sinceDate).And(SearchQuery.NotSeen)` and `:194 SearchQuery.SentSince(sinceDate)` means (i) any message whose **sent** date is older than 7 days is never fetched — so this very outage, if it lasts past 2026-08-06, permanently loses everything that arrived during it; and (ii) any message a human reads in a mail client before the poller runs becomes `\Seen` and is never ingested, with no row and no log. **D-04 (Sev 2, silent loss).** `Services/EmailService.cs:210-215` skips and marks `\Seen` any message matching an existing ingest on `(FromEmail, ToEmail, EmailSubject)` — not on `MessageId`. A customer who sends two genuine RFQs under the same subject line loses the second one entirely, with only a `LogDebug`. |
| **Security impact** | Mailbox credentials are stored in `Email_Configurations.Password`; the repeated authentication failure is consistent with a rotated or revoked password and should be treated as a credential-hygiene finding, not only an availability one. `Controllers/EmailController.cs:26` and `Controllers/EmailTriageController.cs:45` are both `[Authorize]` + `[RequireModulePermission("Leads", …)]`, and `EmailController.cs:32-34` rejects a `businessUnitId` that does not match the caller's claim — no anonymous mail-ingestion surface exists. |
| **Pilot impact** | **Blocking.** Email is the channel Tech Connect will ask about first and the one the mission statement leads with. It cannot be demonstrated today: a message sent to the pilot mailbox during a demo will not appear, and the product will report the poll as successful. |
| **Priority** | **P0** |
| **Recommended action** | 1. Restore/rotate the IMAP credential for `info@intelliflowsystem.com` (or repoint the pilot at a dedicated mailbox) and re-verify with `POST api/Email/fetch`. 2. Make failure visible: persist `LastPollAttemptUtc` / `LastPollSuccessUtc` / `LastPollError` on `Email_Configurations`, stop logging `Email fetch completed successfully.` when no configuration was reached, and gate the `email-poller` heartbeat on at least one successful mailbox connection within N intervals. 3. Fix D-03 — replace `SentSince(7d)` with a persisted per-mailbox UID watermark (`UIDVALIDITY` + last-seen UID) so a poller outage of any length is fully recoverable and `\Seen` is not the system of record. 4. Fix D-04 — dedupe on `MessageId` only; treat a repeated subject as a new occurrence. |
| **Acceptance evidence required** | A real message sent to the pilot mailbox during a witnessed run, evidenced end to end by: a Render log line `Found N potential RFQ emails`; a new `EmailIngests` row with a non-null `TriageOutcome`; a `source_document_occurrences` row with `source_metadata->>'sourceType' = 'Email'`; an `ExtractionJobs` row with `SourceType='Email'` reaching `Succeeded`; the resulting `Leads` row visible in the UI; **plus** a negative test in which the credential is deliberately broken and `/ready` goes `Unhealthy` and the ops screen names the mailbox failure. |

### 1.2 Channel (b) — manual upload, single and batch

| Field | Finding |
|---|---|
| **Requirement ID** | FR-RFQ-01(b) |
| **Requirement text** | Manual drag-and-drop upload of RFQ documents, supporting a single file and a batch of files. |
| **Status** | **VERIFIED (backend + batch semantics)** — this is the one channel proven in production. Front-end drag-and-drop affordance is tracked separately in §1.4. |
| **Existing implementation** | **Batch:** `Controllers/ExtractionController.cs:52 POST api/Extraction/upload`, signature `:57 Upload([FromForm] List<IFormFile> files, …)`, caps at `:37-39` (25 MB/file, 200 MB/batch, 50 files/batch), `[RequestSizeLimit]`/`[RequestFormLimits]` `:53-54`, `[Authorize]` `:28` + `[RequireModulePermission("Leads", Create)]` `:56`, honours an `Idempotency-Key` header, returns 202 Accepted, calls `:96 _ingestion.IngestAsync(… ExtractionSourceType.ManualUpload …)`. Also batch: `Controllers/ManualUploadController.cs:57 POST api/ManualUpload/upload`, `:59 UploadFiles(List<IFormFile> files, …)` → `:86 IngestAsync`. **Single:** `Controllers/ManualUploadController.cs:108 POST api/ManualUpload/upload-rfq-excel` (`:110 IFormFile file`) → `:142 IngestAsync(… ExtractionSourceType.ExcelTemplate …)`; `Controllers/LeadUploaderController.cs:55 POST api/LeadUploader/upload-template` (`:57 IFormFile file`) → `:74 IngestAsync(… ManualUpload, priority: 10)`; `Controllers/RfqUploaderController.cs:52 POST api/RfqUploader/upload-template` (`:54 IFormFile file`, validated `:87`). **Shared downstream:** `Extraction/DocumentIngestionService.cs:109` → `Extraction/ExtractionQueue.cs:138 EnqueueAsync` (PostgreSQL `ExtractionJobs`) → `Extraction/ExtractionWorker.cs:106 ExecuteAsync` / `:149 ProcessOnceAsync`. |
| **Exact evidence** | `ExtractionJobs`: **57 rows, all `SourceType='ManualUpload'`**, spanning 2026-07-16T04:48:43Z → 2026-08-06T00:33:44Z; 21 `Succeeded` with a non-null `ResultLeadId` (e.g. Id 55 → Lead 418; Id 53 → Lead 430; Id 51 → Lead 420; Id 50 → Lead 419). `Leads`: 22 rows with `LeadSource='ManualUpload'`, most recent `IngestedAtUtc = 2026-08-06T02:14:03Z`. **Batch is real, not theoretical:** `source_document_occurrences.source_metadata->>'SourceOccurrenceId'` values of the form `manual-upload:c4f05da2-3d2c-46c7-9c16-28c2ff925337:0:RFQ_Aramco_4203208081.docx` and `manual-upload:c4f05da2-…:1:PURCHASE ORDER_54pages.docx` show two files sharing one batch GUID with distinct ordinals. `LeadIngestionBatches` = 32 rows. Render log at 02:05–02:20Z shows `ExtractionWorker` actively claiming and completing jobs. |
| **Missing behaviour** | The `ExcelTemplate` sub-door (`ManualUploadController.cs:143`) has **zero production jobs and zero production leads** — it compiles and is routed but has never executed against a real document, so it is unproven rather than working. Batch upload is capped at 50 files / 200 MB per request; there is no chunked or resumable upload for a larger drop. |
| **Defects** | **D-05 (Sev 2, throughput).** Of 57 manual-upload jobs only 21 succeeded; 22 dead-lettered and 14 are still `Pending`/`Extracting` with high attempt counts (job 52 = 14 attempts, job 54 = 13, job 48/47/46 = 11 each). The door accepts reliably; the pipeline behind it does not yet drain reliably. **D-06 (Sev 2).** `Attempts` up to 14 against `MaxAttempts` default 5 (`Extraction/ExtractionJob.cs:91`) indicates operator re-drives are inflating the ceiling (`extraction_dead_letter_events` shows 85 `RetryQueued` actions with reasons like "post-ceiling-fix clean re-drive (8f8c84d)") — attempt counts are not currently a trustworthy poison-document signal. |
| **Security impact** | None found on this door. Every route is `[Authorize]` plus an explicit `[RequireModulePermission("Leads", Create)]`; the global fallback policy is `RequireAuthenticatedUser` (`Program.cs:336-344`); the only `[AllowAnonymous]` endpoints in the tree are the two login controllers and two health probes. Uploaded bytes are hashed and written to the immutable store *before* any parsing (`DocumentIngestionService.cs:139,144`). |
| **Pilot impact** | **Demonstrable today.** This is the channel the pilot demo must be built around. |
| **Priority** | **P1** (prove the ExcelTemplate sub-door or explicitly de-scope it; fix drain reliability under FR-RFQ-04/05) |
| **Recommended action** | 1. Declare `POST api/Extraction/upload` the single canonical pilot upload door and freeze the other four for the pilot. 2. Either exercise `upload-rfq-excel` against a real customer template and record the evidence, or record it as out of pilot scope in `04-risk-and-blocker-register.md`. 3. Publish the real batch ceiling (50 files / 200 MB / 25 MB per file) in the runbook rather than leaving it implicit. |
| **Acceptance evidence required** | A witnessed batch of ≥ 10 mixed-format files uploaded in one request, producing 10 `source_document_occurrences`, 10 `ExtractionJobs` sharing one `BatchId`, and a per-file disposition visible in the UI for every one of them — including any that fail. Plus a single-file upload proving the same path. Plus a crash-injection run (below) proving no file is lost. |

### 1.3 Channel (c) — watched network / SFTP / SharePoint folder

| Field | Finding |
|---|---|
| **Requirement ID** | FR-RFQ-01(c) |
| **Requirement text** | A watched network folder / SFTP / SharePoint location is polled and its documents ingested. |
| **Status** | **PARTIAL** — a watched *local-disk* folder door is fully implemented, wired and scheduled, but has **never executed in production** because no tenant folder exists on the persistent disk. **SFTP and SharePoint do not exist at all.** |
| **Existing implementation** | Sweep entry: `Services/EmailBackgroundService.cs:129-154` — `FolderService.DiscoverTenantFolderIds()` at `:131`, then per-BU `folderService.ProcessAllFoldersAsync(businessUnitId, ct)` at `:138`, inside a pushed tenant scope (`:135`) so it runs under RLS. Sweep body: `Services/FolderService.cs:149 ProcessAllFoldersAsync` → `:182 EnqueueFolderFilesAsync` for `SEC` (`:165`), `Aramco` (`:167`, `.docx` only) and `Shared` (`:169`). Tenant discovery: `Services/FolderService.cs:1332 DiscoverTenantFolderIds()` — enumerates `{uploadRoot}/Tenants/*` and parses each directory name as a BU id. Path resolution: `:1296 GetTenantFolderPath` → `{root}/Tenants/{buId}/Watched/{Shared, SEC, Aramco}`. Extension policy: `:1315 SharedFolderExtensions`, `:1321 IsAllowedUploadExtension` (SEC = `.doc` only, Aramco = `.docx` only). Retry ledger: `Services/FolderIngestionRetryState.cs`, table `FolderIngestionRetryStates` (`Migrations/20260804181147_PilotScaleOutSlaIdempotencyAndFolderRetry.cs:79`). Crash recovery: `Services/FolderService.cs:1491 RecoverStaleClaims`. HTTP feeders: `Controllers/EmailController.cs:52 POST api/Email/upload-leads-folder` (multi-file, writes *into* the watched folder via `Services/FolderService.cs:82 SaveFilesToSharedFolderAsync`) and `Controllers/EmailController.cs:90 POST api/Email/process-all-folder-leads` (manual sweep). |
| **Exact evidence** | **Never run.** `ExtractionJobs` where `SourceType='Folder'` = **0 rows**. `source_document_occurrences` with `sourceType='Folder'` = **0 rows**. `FolderIngestionRetryStates` = **0 rows**. Render logs contain **no** `Folder sweep …` line (`EmailBackgroundService.cs:141`) and **no** `No files found in {Label} folder.` line (`FolderService.cs:198`) at any point since 2026-08-05T18:00Z, across 8 poll cycles. Since `FolderService.cs:198` fires unconditionally at `LogInformation` for every folder that is swept but empty, its total absence proves the `foreach` at `EmailBackgroundService.cs:131` iterated **zero** business units — i.e. `DiscoverTenantFolderIds()` hit the `if (!Directory.Exists(tenantsRoot)) return Array.Empty<long>();` early return at `FolderService.cs:1334-1335`. The `{uploadRoot}/Tenants` directory does not exist on the Render disk (`/var/data`, 5 GB, mounted per the service definition). **No SFTP, no SharePoint:** a tree-wide grep for `Sftp`, `SFTP`, `SharePoint`, `FileSystemWatcher`, `WatchedFolder` returns zero implementation hits — the only matches are EF migration scaffolding for `FolderIngestionRetryStates`. The "watcher" is a 1-hour polling sweep piggybacked on the email loop, not a filesystem watcher. |
| **Missing behaviour** | 1. The `{root}/Tenants/{buId}/Watched/…` directory tree is never provisioned — nothing in `Program.cs` or any migration/bootstrap creates it, so the door is inert on a fresh deploy. Production `Storage__RootPath = /var/data/nexora/uploads`, so the path the sweep looks for is `/var/data/nexora/uploads/Tenants`, and it does not exist. 2. No SFTP client and no SharePoint/Graph connector exist. 3. The sweep inherits the email poller's cadence (3600 s here), so folder latency is up to an hour and is silently coupled to the mailbox configuration. 4. There is no operator-visible surface showing which folders are being watched or when they were last swept. |
| **Defects** | **D-07 (Sev 2, coupling).** The folder sweep executes inside `RunPollCycleAsync` *after* the mail fetch (`EmailBackgroundService.cs:129`). Its interval is derived exclusively from `Email_Configurations.PollingInterval` (`:161-184`); a tenant with **no** email configuration therefore gets folder sweeps only at the 300 s default, and the whole folder channel is disabled if the email poller ever faults out — two unrelated channels sharing one failure domain. **D-08 (Sev 3, dead code — see §Repository Map).** `Services/FolderService.cs:321 ProcessLegacyAramcoFolderAsync` and `:680 ProcessLegacySecFolderAsync` have **no callers anywhere in the tree**, and neither do their exclusive downstream helpers `:832 SaveAttachmentsAsync` (called only from the two dead methods at `:444` and `:809`) and `:950 BuildExtraction` (called only from `:753`, inside the dead SEC method). This is roughly 700 lines of orphaned Aramco/SEC parser that reads as a live customer integration and has misled prior reviews. |
| **Security impact** | The live sweep is defensively written and worth preserving: symlinks rejected (`FolderService.cs:216-223`), path-traversal guarded (`:99-119`, `:232-234`), 25 MB cap (`:243`), unsupported extensions quarantined rather than deleted (`:224-230`), and the sweep runs under a pushed tenant scope so it executes as the RLS-constrained role rather than the BYPASSRLS pipeline role (`EmailBackgroundService.cs:126-135`). No new exposure. Note that `Controllers/EmailController.cs:52` lets an authenticated user with `Leads:Create` write arbitrary allowed-extension files into a tenant watched folder — acceptable, but it means the folder door's trust boundary is the same as the upload door's. |
| **Pilot impact** | **Not demonstrable today** without provisioning the directory tree. If Tech Connect's requirement is genuinely SFTP or SharePoint, this is a build, not a fix — and should be declared an out-of-pilot limitation under charter amendment A2 rather than attempted. |
| **Priority** | **P1** for the local watched folder (small, high-confidence: provision the tree + decouple the schedule). **P2 / de-scope** for SFTP and SharePoint. |
| **Recommended action** | 1. Provision `{uploadRoot}/Tenants/{buId}/Watched/{Shared,SEC,Aramco}` at startup for every active tenant (or on first sweep) so the door stops being inert. 2. Split the folder sweep out of `EmailBackgroundService` into its own hosted service with its own interval and its own heartbeat, removing D-07. 3. Delete the two orphaned legacy methods and their exclusive helpers, or move them behind an explicit `[Obsolete]`/archive marker so no future reviewer mistakes them for the live path. 4. Record SFTP/SharePoint as an accepted pilot limitation with a dated follow-up, per charter §6 CONDITIONAL GO rules. |
| **Acceptance evidence required** | A file dropped into `{root}/Tenants/1/Watched/Shared` and, within one sweep interval: a Render log line `Folder sweep {BatchId} for BU 1: 1 enqueued`; a `source_document_occurrences` row with `sourceType='Folder'`; an `ExtractionJobs` row with `SourceType='Folder'` reaching `Succeeded`; the original moved to `…/Processed/`; and the resulting lead visible in the UI. Plus a crash-injection run proving `RecoverStaleClaims` returns a claimed-but-unprocessed file to the watched folder. |

### 1.4 FR-RFQ-01 cross-cutting: durable capture ("nothing disappears silently")

The charter's central requirement is that **every** channel writes a durable ingestion occurrence
**before** asynchronous processing begins. Traced per door, naming the exact line where the durable
row commits relative to where work is queued:

| Door | Durable write | Queue write | Gap | Verdict |
|---|---|---|---|---|
| All doors (shared gateway) | `Extraction/DocumentIngestionService.cs:144` writes the raw bytes to the immutable content-addressed store; `:156` opens the intake transaction; `:170` `LeadIngestionBatch`, `:193` `SourceDocument`, `:209` `SourceDocumentOccurrence`; **`:231 intakeTransaction.CommitAsync(ct)`** | **`:376 _queue.EnqueueAsync(...)`** (a row in `ExtractionJobs`), committed at `:403` | 145 lines, one committed transaction apart | **CORRECT.** Bytes are on disk and the occurrence row is committed before any job exists. A crash anywhere after `:231` leaves a recoverable `source_document_occurrences` row, never a void. |
| HTTP upload (`ExtractionController.cs:96`, `ManualUploadController.cs:86,142`, `LeadUploaderController.cs:74`, `RfqUploaderController.cs`) | inside the gateway, as above | as above | request bytes are held in memory only until `IngestAsync` is entered | **ACCEPTABLE.** A crash before `:231` loses the bytes, but the HTTP request also fails, so the loss is *reported to the user*, not silent. |
| Email (`Ingestion/Triage/EmailIngestEnqueuer.cs:143,177`) | raw `.eml` written to disk at `Services/EmailService.cs:231 message.WriteTo(rawPath)`; `EmailIngest` row committed at `:247 SaveChangesAsync()`; the message is marked read **only after** that, at `Services/EmailService.cs:160 inbox.AddFlagsAsync(uid, MessageFlags.Seen)`, gated on the boolean returned at `:148` | gateway, as above | `\Seen` strictly follows durable persistence | **CORRECT BY DESIGN, but undermined by D-03/D-04.** The mark-seen ordering is right. The *silent* loss on this door comes from messages that are never fetched at all (7-day `SentSince` window; externally-set `\Seen`) and from the subject-based duplicate skip — neither leaves any row or log. |
| Folder (`Services/FolderService.cs:251`) | file atomically claimed by `File.Move(filePath, claimCandidate, false)` at `:212` into `{root}/Tenants/{bu}/Processing/…`; gateway commit at `DocumentIngestionService.cs:231`; original moved to `…/Processed/` at `FolderService.cs:280` **after** ingest returns | gateway, as above | claim precedes ingest; ingest precedes archive | **CORRECT.** A crash between `:212` and `:251` strands the file in `Processing/`, from which `:194 RecoverStaleClaims` returns it to the watched folder after 5 minutes (`:1491-1493`). Per-file exceptions leave the file in place for retry (`:298-317`). |

**Conclusion on durable capture.** No door in the current code queues work before persisting a
durable record. **The one genuine silent-loss surface is upstream of every door: the email door's
message-selection logic (D-03, D-04), where a message is never fetched, never rowed and never
logged.** Everything downstream of `DocumentIngestionService.cs:231` is recoverable.

### 1.5 FR-RFQ-01 summary

| Sub-requirement | Status | Priority |
|---|---|---|
| (a) Dedicated mailbox, monitored continuously | **DEFECTIVE** — built, running, failing auth every cycle since ≤ 2026-08-05T21:43Z, reporting success | P0 |
| (b) Manual upload, single **and** batch | **VERIFIED** — 57 production jobs, 22 leads, multi-file batches proven | P1 |
| (c) Watched folder | **PARTIAL** — implemented and scheduled, never executed (no tenant directory exists) | P1 |
| (c) SFTP / SharePoint | **MISSING** — no implementation of any kind | P2 / de-scope |
| Durable capture before async processing | **VERIFIED** at the gateway for all four doors | — |

---

## §2 — FR-RFQ-08: SOURCE EVIDENCE

> **Requirement.** Retain the original source email/document as immutable audit evidence, linked to
> the RFQ and its ingestion occurrence, in governed object/evidence storage with metadata, access
> control, retention, hash, scan status and audit history.

| Field | Finding |
|---|---|
| **Requirement ID** | FR-RFQ-08 |
| **Requirement text** | The original source email/document is retained as immutable audit evidence, linked to the RFQ and to its ingestion occurrence, in governed object/evidence storage carrying metadata, access control, retention, hash, scan status and audit history. |
| **Status** | **PARTIAL** — the evidence *ledger* (schema, linkage, hashing, immutability-by-construction) is genuinely well built and populated; the evidence *store* is not governed object storage, retention is modelled but never enforced, and the malware verdicts recorded against 86 production documents were produced by a stub scanner. |
| **Existing implementation** | **Ledger entities & tables:** `source_documents` (id, business_unit_id, corpus_id, extraction_job_id, `content_hash`, original_file_name, detected_mime_type, `object_bucket`, `object_key`, `object_version`, byte_size, page_count, `security_status`, processing_status, `malware_scanned_on`, `malware_scanner_engine`, `malware_signature_version`, `malware_verdict_status`, `purge_state`, `purge_policy_code`, `purge_reason`, `bytes_purged_on`, `purged_by_user_id`, `purged_byte_count`); `source_document_occurrences` (idempotency_key, `source_metadata` jsonb, received_on, intake_status, outcome_state, last_error_code/category/details, `original_occurrence_id`, storage_logical_bytes / storage_physical_bytes, cost + reuse columns). **Linkage to the RFQ:** `LeadOccurrenceDocuments` (Id, BusinessUnitId, OccurrenceId, SourceDocumentId, Role, Ordinal, LinkedAtUtc) and `LeadIngestionOccurrences.SourceDocumentId` / `.SourceDocumentOccurrenceId`. **Write path:** `Extraction/DocumentIngestionService.cs:139` (SHA-256), `:144 _storage.WriteImmutableAsync(businessUnitId, "quarantine", hash, ext, bytes, ct)`, `:266 WriteImmutableAsync(… "cleared" …)` after inspection, `:291 source.ReleaseFromQuarantine(...)`. **Read path with integrity check:** `Extraction/ProductionDocumentReader.cs:90-91 OpenVerifiedReadAsync` → throws `EvidenceIntegrityException` at `:103` / `:700`; handled at `Extraction/ExtractionWorker.cs:335-365` with `LogCritical` + `source.Fail()` + `corpus.Fail()`. **Storage abstraction:** `IEvidenceObjectStorage`, bound at `Program.cs:121-126` to `S3EvidenceObjectStorage` when `S3EvidenceStorageOptions` is configured, otherwise `LocalEvidenceObjectStorage`. **Inspection:** `Program.cs:133-150` `MalwareScannerFactory.Select` → `IMalwareScanner`; `Program.cs:151 AddHostedService<MalwareScannerStartupProbe>()`. **Recovery:** `Extraction/SecurityScanRecoveryService.cs:133 RetryAsync` re-reads the immutable object and replays it at `:235`; surfaced by `Controllers/LeadIngestionController.cs:41,54,71`. **Read-back API:** `Controllers/FileController.cs:60 GET api/File/attachment/{attachmentId:long}` (`[RequireModulePermission("Leads", View)]`); the legacy `GET api/File/DownloadFile` is retired and returns `410 Gone` at `FileController.cs:53-57`. **Evidence query surface:** `Controllers/ProcessingEvidenceController.cs:26,40,54,68` over `Extraction/ProcessingEvidenceQuery.cs:10`. |
| **Exact evidence** | **Ledger is populated and consistent.** `source_documents` = **86 rows**; `source_document_occurrences` = **289**; `LeadOccurrenceDocuments` = 10; `field_evidence` = 28. **86 rows, 86 distinct `content_hash` values** — no hash collisions and no duplicate-content rows. **0 orphaned documents** (`left join source_document_occurrences` → 0 rows with no occurrence). 24 of 86 documents are linked through to a `Leads` row via `LeadIngestionOccurrences`. **Object keys are tenant-scoped, content-addressed and non-guessable:** every key matches `Evidence/tenants/{business_unit_id}/{quarantine or cleared}/sha256/{first2}/{sha256}.{ext}` — e.g. `Evidence/tenants/1/cleared/sha256/7c/7c0e1afdd9acaa3d90f72448a86c25b187da773a20e214bb973684835ba9af3f.docx`. All 86 rows are `business_unit_id = 1`; key prefixes group cleanly as `Evidence/tenants/1/quarantine` (56) and `Evidence/tenants/1/cleared` (30). `object_version = content_hash` on **all 86 rows**, i.e. the key embeds the digest, so a different byte-stream can never occupy the same key — **immutability holds by construction, not by policy**. **Storage is NOT governed object storage — by explicit production configuration.** `object_bucket = 'local'` on **all 86 rows**; `source_metadata->'immutableObjects'->'selected'->>'StorageUri'` resolves to `/var/data/nexora/uploads/Evidence/tenants/1/…` — the 5 GB Render persistent disk, not S3. The Render service environment sets **`EvidenceStorage__Provider = Local`** (and `Storage__RootPath = /var/data/nexora/uploads`, `Storage__EnforcePersistentMount = true`, `Storage__RequiredMountPath = /var/data`), so `Program.cs:121-127` binds `LocalEvidenceObjectStorage`, whose `IsDurable` is hard-coded `false` (`Infrastructure/Storage/IEvidenceObjectStorage.cs:94`). The application says so itself: the live `/ready` probe returns **`Unhealthy`** and the Render log at 2026-08-06T02:20:51.916Z reads `Health check evidence-storage with status Unhealthy completed after 0.9022ms with message 'Evidence storage is local and ephemeral; configure an S3-compatible provider before certification.'` **Scan status is recorded but the scanner is a stub — also by explicit production configuration.** The environment sets **`DocumentInspection__Scanner__Provider = BuiltIn`**, overriding the code default (which would select ClamAV outside Development, `Security/DocumentInspection/MalwareScannerProvisioning.cs:140-149`). `malware_scanner_engine = 'Nexora.EICAR'` on every scanned row; Render logs at 02:04:58.730Z and 02:05:13.512Z: `Malware scanner provider selected: BuiltIn. ConfiguredValue=BuiltIn Source=Configuration … Endpoint=in-process (no network endpoint) Environment=Production.` and `REDUCED SECURITY POSTURE: malware scanning in Production is running on the BuiltIn provider, which performs structural/type checks and the EICAR reference signature ONLY`. `Security/DocumentInspection/MalwareScanners.cs:8-59 EicarMalwareScanner` byte-matches exactly one hard-coded EICAR test string (`:13-14`) and returns `Clean` for everything else (`:33`). Real ClamAV was attempted and unreachable — 2026-08-05T01:21:52–55Z: `Upload C001046148.doc stopped by document inspection: Quarantined ClamAV is unavailable (SocketException); the file must remain quarantined.` **Current security posture of the 86 originals:** `Quarantined`/`Received` **54**, `Cleared`/`ReviewRequired` 16, `Cleared`/`Received` 14, `Rejected`/`Received` 2 — i.e. **63 % of all stored originals are still quarantined and have never been released**. **Retrievability is materially broken.** 45 of 86 documents (**52 %**) have at least one occurrence whose `outcome_state = 'SOURCE_OBJECT_UNAVAILABLE'` (48 occurrences), and 14 documents sit in that state as their latest disposition. `ExtractionJobs.StoragePath` explains why: **19 jobs** point at `/app/Uploads/Extraction/…` — the ephemeral container filesystem, wiped on every redeploy — and **1 job** points at `/Users/zackkhan/Downloads/Nexora…`, a developer laptop path that can never resolve in production. That is the charter's "22 documents whose bytes were lost", located precisely: 20 unreachable paths, all created 2026-07-16 → 2026-07-22, before the `/var/data` disk was adopted (the 37 newer jobs all use `/var/data/…`). **Retention is modelled but inert.** `evidence_retention_policies` = **0 rows**. All 86 documents are `purge_state = 'Present'`, `bytes_purged_on` null on all 86, `purge_policy_code` null on all 86 — no document has ever been subject to a retention decision. **Audit history of human access does not exist.** `LeadReviewAudits` = **0 rows** (consistent with the charter: no human has ever completed a review), and no table records a *download* or *view* of a stored original. |
| **Missing behaviour** | 1. **Governed object storage.** S3 is implemented but not configured; production runs on a single 5 GB Render disk with no versioning, no object-lock, no server-side encryption, no cross-region durability and no lifecycle policy. 2. **Retention enforcement.** The `Retention/` module is substantial and complete — `EvidenceRetentionEntities.cs:26-69` (default 90 days, min 30, max 3650, **`IsEnabled` defaults `false`**), `EvidenceRetentionEligibility.cs:17-114` (statutory exclusions, terminal-state rules, skip reasons), `EvidenceRetentionService.cs` (`RunPurgeAsync:196-258`, `PurgeOneAsync:262`, `CompleteInterruptedPurgesAsync`), and an operator API at `Controllers/PlatformGovernanceController.cs:154-157 GET`, `:165-169 PUT …/policy`, `:186-191 POST …/purge-run` (with `dryRun` defaulting to true). **What is missing is the scheduler:** there is no retention hosted service in `Program.cs` — the eight `AddHostedService` registrations are `MalwareScannerStartupProbe:151`, `ProcurementDispatchWorker:229`, `QuoteDeliveryWorker:237`, `RoutingReconciliationWorker:292`, `EmailBackgroundService:346`, `AiReservationReconciliationWorker:370`, `ExtractionWorker:486`, `SlaSweepWorker:550`. `RunPurgeAsync` additionally refuses to act unless `policy.IsEnabled` (`:207-210`), and there are zero policy rows. Nothing expires automatically; no legal-hold concept is exercised. 3. **Access audit history.** Nothing records who retrieved a source document, when, or from which tenant context. FR-RFQ-08 names audit history explicitly; this is the largest single gap in the requirement. 4. **Real malware scanning.** The `Nexora.EICAR` BuiltIn provider is a structural/type check plus one reference signature; the 30 `Cleared` verdicts in production carry no real anti-virus assurance. 5. **Read-side hash verification everywhere.** It exists on the extraction read path (`ProductionDocumentReader.cs:90-91`) but is not demonstrated on the user-facing download path. 6. **Multi-tenant proof.** All 86 documents belong to BU 1; tenant isolation of evidence keys is correct by construction but has never been exercised with a second tenant's data. |
| **Defects** | **D-09 (Sev 1, data loss — already materialised).** 19 `ExtractionJobs` reference `/app/Uploads/Extraction/…` on the ephemeral container filesystem and 1 references a developer laptop path; those originals are gone from the running system, and 45 of 86 documents have hit `SOURCE_OBJECT_UNAVAILABLE`. The current `/var/data` layout fixes the cause going forward, but the ledger still asserts `purge_state='Present'` for rows whose bytes are unreachable — **the evidence ledger currently overstates what it holds.** **D-10 (Sev 1, certification blocker).** Evidence storage is a 5 GB single-instance disk. The app's own readiness probe fails on this. Disk exhaustion or instance replacement destroys every original at once, and `/health` (the path Render actually probes) stays `Healthy` throughout because the evidence check is tagged `ready`, not `live` (`Program.cs:178`). **D-11 (Sev 2, security).** Production malware scanning is the BuiltIn EICAR stub. Every `Cleared` verdict on the 30 released documents is, in security terms, unsubstantiated. **D-12 (Sev 2, compliance).** Retention is unenforceable: 0 policies, 0 purges, no sweeper. A pilot client asking "what is your retention period and how is it enforced?" has no true answer. **D-13 (Sev 2, compliance).** No audit row is written when a source document is read. The requirement's "audit history" clause is unmet. **D-14 (Sev 3, operational).** 54 of 86 originals are stuck `Quarantined` — a backlog created by the ClamAV outage that `SecurityScanRecoveryService` exists to drain but which no scheduled job drains, because that service is scoped and operator-triggered only (`Program.cs:491`), not hosted. **D-15 (Sev 2, evidence integrity).** Post-revision the download endpoint can serve the superseded revision's bytes — see §2.1. **D-16 (Sev 2, security).** `Controllers/FileController.cs:60-152` applies **no `SecurityStatus` gate**: a document still in `Quarantined` state is downloadable provided an `Attachment` row and an `ExtractionJob` exist. Given the 54 quarantined originals and the stub scanner, unscanned bytes are reachable by any user holding `Leads:View` in the owning tenant. There is likewise no `PurgeState` gate, so a purged document yields a raw `FileNotFoundException` → generic `404` rather than the designed `410 Gone` tombstone; `EvidenceLedgerEntities.cs:426 BytesAvailable`, which exists to express exactly that contract, has **zero callers**. **D-17 (Sev 3, storage growth).** Every document is written **twice** — quarantine at `DocumentIngestionService.cs:144` and, once cleared, again at `:266` — and the ingestion path never deletes the quarantine copy. On a 5 GB disk this doubles evidence footprint and retains malware-bearing bytes indefinitely (`Retention/EvidenceRetentionEligibility.cs:90-97` documents the behaviour). **D-18 (Sev 3, ledger integrity).** `EvidenceLedgerEntities.cs:311-324 RecordMalwareVerdict` has no state guard, so a later `Clean` verdict can overwrite a recorded `Infected` one; `:701-715 MarkTerminalSecurityFailure` sets `IntakeStatus = Rejected` directly, bypassing the `Transition` guard every sibling mutator uses. |
| **Security impact** | **Positive:** object keys are tenant-partitioned and SHA-256-addressed (`Infrastructure/Storage/IEvidenceObjectStorage.cs:176-180 BuildKey`, validated at `:182-190`), so cross-tenant key collision is impossible and the key embeds the digest — an original cannot be silently replaced. Overwrite is refused rather than performed: local writes re-hash any pre-existing file and throw `InvalidDataException` on mismatch (`Infrastructure/Storage/IFileStorage.cs:228-232`, `:248-263` using `FixedTimeEquals`); the S3 path uses a conditional `IfNoneMatch = "*"` PUT (`IEvidenceObjectStorage.cs:325`). Reads are hash-verified on both providers (`:114-128`, `:343-357`, verifier `:202-210`), and `EvidenceIntegrityException` (`ProductionDocumentReader.cs:103`) turns a tampered or truncated object into a loud `LogCritical` failure (`ExtractionWorker.cs:335-365`) rather than a wrong extraction. Database triggers enforce ledger immutability independently of the application (§2.1). There is **no unauthenticated path to a stored document**: the only `[AllowAnonymous]` endpoints in the tree are the two login controllers and the `/health` + `/ready` probes (`Program.cs:751,755`), the global fallback policy is `RequireAuthenticatedUser` (`Program.cs:336-344`) reinforced by `app.MapControllers().RequireAuthorization()` (`Program.cs:745`), the legacy `GET api/File/DownloadFile` is hard-retired to `410 Gone` (`FileController.cs:53-57`), **no presigned-URL generation exists anywhere in the codebase** (zero hits for `Presign`/`GetPreSignedUrl`), and the live download route is tenant-filtered on every join (`FileController.cs:64-66,80-84,88,97,103`). **Negative:** verdicts are stub-derived (D-11); quarantined bytes are downloadable (D-16); no encryption-at-rest, S3 object-lock, SSE/KMS or bucket policy is configured anywhere (moot today at `Provider=Local`, but required before the S3 cutover); `Attachment` carries no `BusinessUnitId` and no global query filter, so tenant isolation on download rests entirely on the Lead join at `FileController.cs:80-84` with no defence in depth; `CopyAndVerifyAsync` (`IEvidenceObjectStorage.cs:212-220`) buffers a whole object into memory before serving, an unbounded-memory surface on a 121 MB-class document; and no access is audited at all (D-13). |
| **Pilot impact** | The evidence *story* is demonstrable — an original can be shown, hash-matched, and traced to its occurrence and lead. The evidence *guarantee* is not: on today's configuration a single instance replacement destroys all 86 originals, half of them have already been unreachable at least once, and 63 % are quarantined behind a stub scanner. This is a **CONDITIONAL GO** area under charter §6 — no cross-tenant exposure and no unrecoverable corruption, but a real, dated data-durability limitation that must be owned. |
| **Priority** | **P0** for D-09/D-10/D-16 (storage durability, truthful ledger, quarantined-bytes exposure). **P1** for D-11/D-13/D-15. **P2** for D-12/D-14/D-17/D-18. |
| **Recommended action** | 1. **Configure `S3EvidenceObjectStorage`** — the implementation and the `Program.cs:121-127` branch already exist; this is flipping `EvidenceStorage__Provider` from `Local` to `S3` plus a backfill of the 86 local objects, and it clears the `/ready` failure. Enable bucket versioning **and** object-lock and SSE, none of which the code currently sets. Note `IEvidenceObjectStorage.cs:485-491`: the mandatory versioning check silently passes if the store answers `405`/`501` to `GetBucketVersioning` — tighten that before relying on it. Note also `:450-458`: the S3 overwrite-refusal path validates against a caller-supplied `x-amz-meta-sha256` header rather than re-hashing bytes, unlike the local path; make it re-hash. 2. **Reconcile the ledger with reality** — sweep all `source_documents`, attempt a byte read, and mark the unreachable ones with an explicit terminal state instead of `purge_state='Present'`; re-upload the 22 originals the founder recovered and hash-verified from OneDrive. 3. **Gate the download endpoint** on `SecurityStatus == Cleared` and on `BytesAvailable` (returning `410 Gone` for purged documents — the contract `EvidenceLedgerEntities.cs:426` already describes and nothing implements). 4. **Write an access-audit row** on every source-document read (actor, tenant, document id, occurrence id, timestamp, route). The audit infrastructure already exists and is used for evidence *mutation* (`Retention/EvidenceRetentionService.cs:50-55`, `LeadIdentityApplicationService.cs:238`); reads are simply not covered. This is the single change that converts FR-RFQ-08 from PARTIAL to close-to-complete. 5. **Fix revision retrieval (D-15)** — key the download on the occurrence being viewed rather than `FirstOrDefault()` + filename match. 6. Restore a real scanner (ClamAV sidecar — the code already defaults to `127.0.0.1:3310`, `MalwareScannerProvisioning.cs:140-149`) and re-scan the 54 quarantined originals through `SecurityScanRecoveryService`; promote that service to a scheduled hosted worker so the backlog drains without an operator. 7. Seed at least one `evidence_retention_policies` row with `IsEnabled = true` and schedule `EvidenceRetentionService.RunPurgeAsync` as a hosted worker — an unenforced policy column is worse than an honest "retention not yet enforced". |
| **Acceptance evidence required** | 1. `select count(*) from source_documents where object_bucket <> 'local'` returns 86 — every original in S3 with versioning + object-lock on. 2. `/ready` returns `Healthy` with the `evidence-storage` check passing. 3. A byte-level read of every `source_documents` row succeeds and its SHA-256 matches `content_hash` — published as a table, zero mismatches, zero unreachable. 4. A witnessed download of a source document produces a new access-audit row naming the actor and tenant. 5. A cross-tenant negative test: a BU 2 user requesting a BU 1 document receives 403/404 and the attempt is audited. 6. A revision test: an updated version of an already-ingested document produces a **new** `source_documents` row and a **new** occurrence, with the prior original still byte-retrievable and still linked to its lead. 7. A real malware scanner named in `Malware scanner provider selected: …` with a non-`Nexora.EICAR` engine, and the 54 quarantined originals resolved to a real verdict. |

### 2.1 What happens to the original when a revision arrives

Traced, because the requirement turns on it. `Extraction/DocumentIngestionService.cs:184-195` looks
up `SourceDocument` by `(BusinessUnitId, ContentHash)`. **Different bytes ⇒ different hash ⇒ a new
`source_documents` row with a new key** — the prior original is never updated, overwritten or
deleted, and its key (which embeds its own digest) is not reachable by the new content. **Identical
bytes ⇒ the existing row is reused** and only a new `source_document_occurrences` row is created
(`:197-211`), with `:217-228 MarkExactDuplicateCandidate` recording the lineage back to the first
occurrence via `original_occurrence_id`. Production confirms the design holds: 86 rows / 86 distinct
hashes, and `EXACT_DUPLICATE_CONFIRMED` appears on 62 occurrences across 19 distinct documents.

The ledger is further protected at the database level, not just in C#. Migration
`Migrations/20260730193414_SynchronizeSharedExtractionOccurrences.cs:124-131` installs
`nexora_protect_source_document_identity`, which raises `23514` on any attempt to change
`business_unit_id`, `corpus_id`, `content_hash`, `original_file_name`, `byte_size` or `created_on`;
`:133-138` freezes `object_bucket` / `object_key` / `object_version` once `security_status = 'Cleared'`;
`:147-159` freezes `source_metadata`; and `trg_source_documents_no_delete` refuses `DELETE` outright
(`DocumentIntelligence/Persistence/EvidenceLedgerEntities.cs:50-53`). The single legitimate mutation
of the object pointer is `EvidenceLedgerEntities.cs:372-385 ReleaseFromQuarantine`, permitted only
from `Pending`/`Quarantined`.

**Verdict: the stored original survives a revision — the storage layer is correct.** But the
*retrieval* layer is not:

**D-15 (Sev 2, evidence integrity).** `Controllers/FileController.cs:86-99` resolves which source
document to serve by taking `OrderBy(link.Ordinal).ThenBy(link.Id).FirstOrDefault()` across **all**
occurrences of the lead, then requiring `document.OriginalFileName == attachment.FileName` (`:99`).
After a revision, that returns the **earliest-ordinal** linked document. If the revision reuses the
same filename — the normal case for a re-issued RFQ — the endpoint can serve the *superseded*
revision's bytes while the UI presents them as the current source evidence; if the filename changed,
it returns 404 instead. Neither is a tenant-isolation break, but both defeat the requirement's
purpose. Retrieval must be keyed on the occurrence/revision being viewed, not on filename.

The remaining gap is presentational: nothing yet *presents* the prior original alongside the revision
in the UI as an audit artefact.

### 2.2 FR-RFQ-08 sub-requirement scorecard

| Clause | Status | Evidence anchor |
|---|---|---|
| Original retained | **PARTIAL** | 86 rows stored; 45 have been unreachable at least once; 20 job paths are on wiped ephemeral storage |
| Immutable | **VERIFIED** | key = `…/sha256/{digest}.{ext}`, `object_version = content_hash` on 86/86 rows; new content ⇒ new row (`DocumentIngestionService.cs:184-195`) |
| Linked to RFQ + ingestion occurrence | **VERIFIED** | `LeadOccurrenceDocuments`, `LeadIngestionOccurrences.SourceDocumentId`; 24 documents traced to leads; 0 orphans |
| Governed object storage | **MISSING** | `object_bucket='local'` on 86/86; `/ready` = `Unhealthy`, `evidence-storage` check fails by name |
| Metadata | **VERIFIED** | `source_metadata` jsonb populated on 289/289 occurrences with `fileName`, `sourceType`, `metadata`, `immutableObjects`, `inspection` |
| Access control | **PARTIAL** | authn/authz and tenant filtering are sound (every read route `[Authorize]` + `[RequireModulePermission]`, tenant-filtered joins, legacy download `410 Gone`, no presigned URLs, no anonymous document surface) — but there is no `SecurityStatus` or `PurgeState` gate, so quarantined bytes are downloadable (D-16) |
| Retention | **MISSING (enforcement)** | full `Retention/` module + operator API exist; **no hosted sweeper**, `IsEnabled` defaults false, `evidence_retention_policies` = 0 rows, 0 of 86 documents purged or policy-tagged |
| Hash | **VERIFIED** | SHA-256 at `DocumentIngestionService.cs:139`; verified on read on both providers (`IEvidenceObjectStorage.cs:114-128`, `:343-357`, `:202-210` constant-time compare); `EvidenceIntegrityException` at `ProductionDocumentReader.cs:103` |
| Scan status | **DEFECTIVE** | recorded on every row, but `DocumentInspection__Scanner__Provider = BuiltIn` in production selects the `Nexora.EICAR` stub; 54/86 still `Quarantined`; `RecordMalwareVerdict` has no state guard (D-18) |
| Audit history | **MISSING** | no access-audit table or write path — `FileController.cs:60-152` writes nothing on the success path; zero repo hits for evidence-read audit events; `LeadReviewAudits` = 0 rows |
| Revision retrieval | **DEFECTIVE** | storage retains every revision correctly, but `FileController.cs:86-99` can serve the superseded revision's bytes (D-15) |

---

## §3 — FR-RFQ-02: FORMAT SUPPORT

> **Requirement.** Ingest and parse: native PDF, scanned PDF, DOCX, XLSX, HTML, JPEG, PNG,
> Outlook MSG, EML.

Owner: Implementation Engineer (audit capacity). Production evidence gathered read-only
against the live Neon database on **2026-08-06**.

### 3.0 Production format census — the ground truth every row is scored against

Two independent counters agree, and both say the same thing.

```sql
select "FileType","Status",count(*) from "ExtractionJobs" group by 1,2;
select lower(regexp_replace(source_metadata->>'fileName','^.*\.','')) ext, count(*)
  from source_document_occurrences group by 1;
```

| Extension | Occurrences ingested | Jobs created | Jobs **Succeeded** |
|---|---|---|---|
| `.doc` (legacy Word 97-2003) | 253 | 49 | **25** |
| `.xls` | 28 | 4 | **3** |
| `.docx` | 7 | 2 | **1** |
| `.txt` | 1 | 1 | **1** |
| `.csv` | 0 (manual) | 1 | **1** |
| `.pdf` `.xlsx` `.jpg` `.jpeg` `.png` `.html` `.msg` `.eml` `.tif` `.bmp` `.gif` `.webp` | **0** | **0** | **0** |

**Seven of the nine required formats have never been ingested once.** The two that have
(DOCX, and XLSX only by way of its `.xls` sibling) carry n=1 and n=3 respectively.

**Trap — the legacy leads are not evidence.** `Leads.EmailSource` shows `PDF` (11),
`JPEG, PDF` (2), `PNG` (1), `Excel` (2), `Aramco RFP Document` (2). Every one of those rows
was created **2026-05-12 → 2026-06-17** and joins to **zero** `ExtractionJobs`:

```sql
select l."EmailSource", count(*) leads, count(distinct ej."Id") with_job,
       min(l."CreatedDate")::date first
from "Leads" l left join "ExtractionJobs" ej on ej."ResultLeadId"=l."ID" group by 1;
-- PDF | 11 | 0 | 2026-05-12      doc | 27 | 27 | 2026-07-22
```

They are artefacts of a retired pre-queue path. The pipeline the pilot demonstrates
(`DocumentIngestionService` → `ExtractionJobs` → `ProductionDocumentReader`) has processed
`doc/xls/docx/txt/csv` and **nothing else**, ever.

### 3.1 FORMAT CAPABILITY MATRIX

Four independent gates per format: **reader exists** → **wired into dispatch**
(`Extraction/ProductionDocumentReader.cs:129-139`) → **on the intake allow-list**
(`Security/DocumentInspection/DocumentIntakeAllowList.cs:30-34`) → **proven in production**.

| Format | Reader | Dispatch (`PDR.cs`) | Allow-list | Prod proof | Status |
|---|---|---|---|---|---|
| **Native PDF** | PdfPig, `PDR.cs:355-364` | ✅ `:131 "pdf"` | ✅ `.pdf` | **0 jobs** | implemented-but-unproven |
| **Scanned PDF** | Docnet raster + Tesseract, `PDR.cs:390-442` | ✅ `:375` fallback | ✅ `.pdf` | **0 OCR runs** | implemented-but-unproven |
| **DOCX** | OpenXmlReader streaming, `PDR.cs:226-324` | ✅ `:135 "docx"` | ✅ `.docx` | 2 jobs, **1 Succeeded** | **VERIFIED (n=1)** |
| **XLSX** | `NativeSpreadsheetParser.ParseXlsx`, `PDR.cs:109-114` | ✅ `:109` | ✅ `.xlsx` `.xlsm` | **0 `.xlsx` jobs** (3 `.xls` succeeded via `:115-120`) | implemented-but-unproven |
| **HTML** | **none** | ❌ falls to `:138 _ => DecodeText` | ❌ **absent** | 0 | **MISSING** |
| **JPEG** | Tesseract, `PDR.cs:446-466` | ✅ `:137` | ✅ `.jpg` `.jpeg` | **0** | implemented-but-unproven |
| **PNG** | Tesseract, `PDR.cs:446-466` | ✅ `:137` | ✅ `.png` | **0** | implemented-but-unproven |
| **Outlook MSG** | **none** | ❌ | ❌ **absent** | 0 | **MISSING** |
| **EML** | **none** (MimeKit is IMAP transport only) | ❌ | ❌ **absent** | 0 | **MISSING** |
| *(`.doc` legacy — not required, but 96% of the real corpus)* | `WordBinaryTextExtractor`, `PDR.cs:205-224` | ✅ `:134` | ✅ `.doc` | **25 Succeeded** | **VERIFIED** |

`ProductionDocumentReader` is the registered reader — `Program.cs:485`
`AddScoped<IExtractionDocumentReader, ProductionDocumentReader>()`. It is reachable, not
orphaned; every parser it calls (`NativeSpreadsheetParser`, `WordBinaryTextExtractor`,
`CanonicalRfqNormalizer`, `MultiInquirySplitter`, `ChunkedExtractionService`,
`ConversationalExtractionService`) is DI-registered and constructed. **The dead code is not
in the reader — it is in the formats that reach it.**

### 3.2 Requirement rows

| ID | Requirement | Status | Existing implementation | Exact evidence | Missing behaviour | Defects | Pilot impact | Pri | Recommended action | Acceptance evidence required |
|---|---|---|---|---|---|---|---|---|---|---|
| **02-A** | Native PDF | **BLOCKED** | `Extraction/ProductionDocumentReader.cs:355-364` (PdfPig `PdfDocument.Open` → per-page `page.Text`) | Reader + dispatch + allow-list all present; **`ExtractionJobs` has 0 rows with `FileType='pdf'`**; the 11 `EmailSource='PDF'` leads predate the queue and join to 0 jobs | Nothing in code. Missing is **execution** | PdfPig parse failure is swallowed to a warning at `:366-368` and falls through to the OCR branch — a *corrupt* PDF is therefore misreported as "scanned" and, once OCR also fails, returns `OcrStatus.Failed` (`:385-386`) rather than a truthful `corrupt_pdf` disposition | Client will send PDFs on day one. A path never executed once is not a pilot capability | **P0** | Drive one real native PDF end-to-end; add a distinct disposition for "PDF parsed but unreadable" vs "PDF is scanned" | `ExtractionJobs` row `FileType='pdf'`, `Status='Succeeded'`, non-null `ResultLeadId`; `extraction_runs.processing_path='NativeParser'`, `ocr_status='NotRequired'` |
| **02-B** | Scanned PDF (OCR) | **BLOCKED** | `PDR.cs:390-442` `TryOcrScannedPdf` — Docnet `DocLib.Instance.GetDocReader` at scale 2.0 (~144 DPI), `BgraToBmp24`, `TesseractEngine(_tessDataPath,"eng")`, capped at `MaxOcrPages = 10` (`:392`) | **OCR has never executed.** `select count(*) filter (where ocr_page_count>0) from extraction_runs` → **0 of 21**; all 21 rows `ocr_status='NotRequired'`; `LeadIngestionOccurrences.ProcessingPath` ∈ {HumanReview 23, ExternalModel 20, Deterministic 1} — **no `LocalOcr` row exists in production** | Nothing in code. Missing is proof that Tesseract + pdfium load in the deployed container | **No OCR startup probe exists.** `grep -rn "Tesseract\|tessdata\|Ocr" HealthChecks/ Program.cs` → **0 hits**. First OCR attempt in front of a client is also the first test of the native libraries. `MaxOcrPages=10` silently truncates page 11+ (`OcrTruncated` is set but never persisted to `ExtractionJobs`) | Scanned tenders are the single most likely "wow" demo and the least proven path | **P0** | Add a startup/health probe that OCRs a 1-page embedded fixture and reports `tesseract` red/green; then drive one real scanned PDF | Health endpoint reports `ocr: Healthy` with engine + language version; **and** one `extraction_runs` row with `ocr_page_count > 0`, `ocr_status='Completed'`, `processing_path='LocalOcr'` |
| **02-C** | DOCX | **VERIFIED (thin)** | `PDR.cs:226-324` — deliberately `OpenXmlReader` streaming, not DOM (`:238-248` documents the 121 MB Aramco OOM avoidance) | 2 jobs, **1 Succeeded** (`FileType='docx'`, 2026-08-06). Intake caps sized to the real document: `DocumentInspectionOptions:27-28` 256 MB entry/expanded, `:35` ratio 300 | Second and third real specimens | Production still holds **5 `UNSUPPORTED_FORMAT` occurrences for `RFQ_Aramco_4203208081.docx`** reading `"A part of this document expands to 121 MB, above the 100 MB limit"` / `"An OOXML entry exceeds the expanded-size limit"` — the fix is deployed but **the file was never re-driven**, so the only real Aramco DOCX in production is still a rejection | The flagship large-tender proof is a stale rejection record | **P0** | Re-drive `RFQ_Aramco_4203208081.docx`; confirm the 256 MB caps admit it and the streaming reader completes inside the 512 MB instance | A `Succeeded` job for that exact file + peak worker RSS recorded; the 5 stale `UNSUPPORTED_FORMAT` occurrences superseded, not deleted |
| **02-D** | XLSX | **PARTIAL** | `PDR.cs:109-114` → `NativeSpreadsheetParser.ParseXlsx`; unrecognised layouts fall back to rendered text + LLM at `:585-620` (documented `:554-566`) — a good design: an unknown column layout is *not* terminal | **0 `.xlsx` jobs ever.** Sibling `.xls` proves the surrounding machinery: 4 jobs, 3 Succeeded. Production also holds 4 occurrences of `C001046190.xls` rejected as `"The file extension '.xls' is not supported"` — from a pre-allow-list build | `.xlsx` execution | The `.xls` rejections are stale but still the newest record for that file | **P1** | Drive the one real `.xlsx` and re-drive `C001046190.xls` | `Succeeded` job with `FileType='xlsx'`; `extraction_runs.processing_path='DeterministicRules'` for a recognised layout **and** one `ExternalFallback` row for an unrecognised layout |
| **02-E** | **HTML** | **MISSING** | none | **`.html`/`.htm` are absent from `DocumentIntakeAllowList.cs:30-34`** (16 extensions: pdf, doc, docx, xls, xlsx, xlsm, csv, txt, png, jpg, jpeg, gif, bmp, tif, tiff, webp). `HtmlAgilityPack 1.12.4` is referenced at `ERP_RFQ_Automation.csproj:30` and **used nowhere** — `grep -rn "HtmlAgility\|HtmlNode\|LoadHtml" --include=*.cs` → **0 hits**. The only HTML handling in the codebase is `Regex.Replace(html,"<.*?>"," ")` at `Services/EmailService.cs:1151` and `Ingestion/Triage/EmailTriageService.cs:301` | An HTML reader; `.html`/`.htm` on the allow-list; a `"html"`/`"htm"` dispatch arm | A regex tag-strip destroys table structure — the exact structure an RFQ line-item grid lives in. An HTML RFQ body today yields one flat prose blob, not rows | **HTML-masquerading-as-`.xls` is the known real-world case.** `DocumentFileInspectionService.cs:200-264` `DetectType` finds no signature, `.xls` is not `.csv`/`.txt`, so `:259-263` throws `UnsafeArchiveException` with an actionable sentence. **Visible rejection — but the RFQ is not extracted.** Portal exports from SEC/Aramco commonly take exactly this shape | **P1** | Add `.html`/`.htm` to the allow-list + a signature (`<html`/`<!DOCTYPE`/`<table`) ; add an `HtmlAgilityPack` table-aware reader; make the `.xls`-that-is-HTML case *re-route* to that reader instead of rejecting | `Succeeded` job for an HTML RFQ with ≥2 `LeadItems`; and one `.xls`-named HTML file that extracts rather than rejects |
| **02-F** | JPEG | **BLOCKED** | `PDR.cs:446-466` `ExtractTextFromImage` — `Pix.LoadFromMemory` → `engine.Process` | Reader + dispatch (`:137`) + allow-list all present; **0 jobs**. Same unproven Tesseract runtime as 02-B | Execution | Single-shot OCR with no deskew, no denoise, no DPI normalisation and no `PageSegMode` selection. A phone photo of a tender notice will score far below a clean raster | Photographed/screenshotted RFQs are common from field sales | **P1** | Drive one real photo through; measure and publish the character-level result rather than asserting support | `Succeeded` job `FileType='jpg'`; `extraction_runs.ocr_page_count=1`, `processing_path='LocalOcr'`; extracted text diffed against ground truth |
| **02-G** | PNG | **BLOCKED** | same code path as 02-F | same — 0 jobs | Execution | as 02-F | Screenshots of portal RFQ pages are the most common PNG case | **P1** | as 02-F | as 02-F |
| **02-H** | **Outlook MSG** | **MISSING** | none | **`.msg` absent from the allow-list.** No CFB/OLE property-stream parser exists anywhere: `grep -rn "MsgReader\|CompoundFile\|__substg"` → 0 hits. `Ingestion/Triage/EmailIngestEnqueuer.cs:101-105` records `"unsupported file type '.msg'"` and skips | A `.msg` reader (`.msg` is an OLE compound file — the `OleSignature` at `DocumentFileInspectionService.cs:41` is already detected, and `InspectOleCompound` already runs for `.doc`; the gap is the *reader*, not the inspector), `.msg` on the allow-list, a dispatch arm, and recursive extraction of the MSG's own attachments | Forwarded Outlook items are how a sales engineer hands over an RFQ. Today the RFQ inside is invisible | **Residual silent-loss window (see 02-J).** A `.msg` skip reason is persisted **only** when the email also has fresh body text (`EmailIngestEnqueuer.cs:170-171`) **or** when nothing at all was queued (`EmailService.cs:357-360`) | **P1** | Add `MsgReader` (or a CFB `__substg1.0_*` reader), allow-list `.msg`, and treat contained attachments as first-class child documents sharing the batch id | `Succeeded` job `FileType='msg'` whose contained PDF/XLSX attachment produced its **own** job in the same `BatchId` |
| **02-I** | **EML** | **MISSING** | none | **`.eml` absent from the allow-list.** MimeKit 4.16.0 (`csproj:32`) is present but used only as IMAP/SMTP transport (`EmailTriageService`, `EmailIngestEnqueuer`, `OutboundSmtpTransport`, `SmtpController`, `EmailService`) — never to parse an uploaded `.eml`. **`Services/EmailService.cs:1172` is a bare `continue` that drops `.eml` attachments with no reason recorded at all** | An `.eml` reader — this is the cheapest gap on the list: `MimeMessage.Load(stream)` + the existing `EmailBodyNormalizer`/`EmailIngestEnqueuer` fan-out already do the work for live IMAP mail | The `SaveAttachmentsAsync` drop at `:1172` predates the visible-disposition work and was never brought forward | Same as 02-H; `.eml` is the standard export from non-Outlook clients | **P1** | Allow-list `.eml`; dispatch to `MimeMessage.Load` and reuse `EmailIngestEnqueuer`'s attachment fan-out; delete the bare `continue` at `EmailService.cs:1172` | `Succeeded` job `FileType='eml'` whose attachment produced a sibling job in the same `BatchId`; no code path drops an attachment without a persisted reason |
| **02-J** | Native text preferred; OCR only as fallback | **VERIFIED (by inspection)** | `PDR.cs:371-386` | The decision point, quoted verbatim: `// Fast path: the PDF already carries an embedded text layer.` / `if (!IsNearEmpty(pdfText)) return Native(pdfText);` / `_log.LogInformation("PDF has little/no embedded text; attempting OCR fallback.");` / `var ocr = TryOcrScannedPdf(bytes);`. Threshold `NearEmptyThreshold = 20` non-whitespace chars (`:51`); `IsNearEmpty` at `:684`. Ordering is correct and unconditional | — | **20 characters is too coarse.** A 40-page scanned tender carrying only a 25-character digital stamp or footer clears the threshold and returns as "native" with 25 characters of text — a *silent* content loss with `OcrStatus='NotRequired'` asserting nothing was needed. The threshold is absolute, not per-page | A scanned tender with any text-layer artefact extracts to near-nothing and looks like a low-confidence document rather than a missed OCR | **P1** | Make the test per-page and density-based (e.g. < N chars **per page**), and record the observed density on the run so a reviewer can see why OCR was skipped | A 20-page scanned PDF bearing a digital-signature text layer takes `processing_path='LocalOcr'`, not `NativeParser` |
| **02-K** | Failure handling per format — nothing disappears silently | **PARTIAL** | `Extraction/ExtractionWorker.cs:315-378` | Three typed catch sites, each ending in a persisted disposition: `:315-334` `DocumentParsingException` → `FailPermanentlyAsync` + `MarkIntakeFailureAsync(ex is UnsupportedDocumentFormatException ? "unsupported_format" : "document_parse_failed", permanent: true)`; `:335-364` `EvidenceIntegrityException` → `LogCritical` + `source.Fail()` + `corpus.Fail()` + `"evidence_integrity_failure"`; `:366-378` catch-all → `FailAsync` + `"unexpected_extraction_failure"`. Production confirms the dispositions are real and visible: `outcome_state` = `UNSUPPORTED_FORMAT` 9, `SOURCE_OBJECT_UNAVAILABLE` 48, `EXACT_DUPLICATE_CONFIRMED` 52, `DUPLICATE_RESCAN_REQUIRED` 130; `intake_status/last_error_code` = `Rejected/document_quarantined` 155, `Rejected/source_object_unavailable` 48, `Rejected/document_rejected` 9. **Oversized:** `DocumentInspectionOptions:11` 25 MB + `:27-35` archive caps — rejection carries the exact size (`"expands to 121 MB, above the 100 MB limit"`). **Malformed/masquerading:** `DocumentFileInspectionService.cs:116-122` signature-vs-extension + `:259-263` no-signature, both with actionable reasons. **`.doc` unreadable:** `PDR.cs:222-223` `UnsupportedDocumentFormatException` with a truthful message | **Password-protected files have no dedicated disposition.** An encrypted OOXML is an OLE container, so `InspectOleCompound` sees a non-Word CFB and rejects it as a signature mismatch; an encrypted PDF surfaces as PdfPig throwing → swallowed at `PDR.cs:366-368` → misreported as "scanned" → `OcrStatus='Failed'`. Neither says *"this file is password-protected"* | **Two real silent-loss windows.** (1) `EmailIngestEnqueuer.cs:160-171` — `skippedAttachments` is copied into job metadata **only inside the `else` branch where a fresh body exists**. An email whose body is pure quoted thread, carrying one supported attachment *and* one `.msg`, queues 1 job → `EmailService.cs:357-360` does not fire (`Queued != 0`) → **the `.msg` skip reason is persisted nowhere**. (2) `ExtractionWorker.cs:329-332` / `:360-363` / `:376` — if `MarkIntakeFailureAsync` throws *after* `FailAsync` succeeded, the queue row is terminal but the occurrence carries no disposition; the exception is logged and `return true` consumes the job | A pilot claim of "nothing disappears" is falsifiable on window (1) with a two-attachment forwarded email — a realistic client action | **P0** (window 1) / **P2** (window 2) | Move the `skippedAttachments` assignment above the body branch and persist it on the ingest record unconditionally; add a `password_protected` disposition detected before the OCR fallback; make `MarkIntakeFailureAsync` failure re-throw so the lease expires and retries rather than being consumed | An email with a quoted-only body + 1 PDF + 1 `.msg` yields a queued PDF job **and** a persisted, user-visible reason for the `.msg`; an encrypted PDF yields `password_protected`, never `OcrStatus='Failed'` |
| **02-L** | OCR language data ships and loads | **PARTIAL** | `csproj:46` `<Content Include="tessdata\**" CopyToOutputDirectory/CopyToPublishDirectory="PreserveNewest" />`; `PDR.cs:75` `_tessDataPath = Path.Combine(env.ContentRootPath,"tessdata")`; `Backend/Dockerfile:15-17` `apt-get install libleptonica-dev libtesseract-dev tesseract-ocr` | The Windows-style `tessdata\**` glob **does** work on Unix — MSBuild normalises the separator, confirmed empirically: `bin/Release/net8.0/tessdata/eng.traineddata` exists on this macOS build. `tessdata/` contains **`eng.traineddata` only** (23.4 MB) | Runtime proof that the Tesseract 5.2.0 managed wrapper binds the Debian `libtesseract` in the `aspnet:8.0` image | The apt `tesseract-ocr` package installs language data to `/usr/share/tesseract-ocr/*/tessdata`, which this code never reads — only the `csproj` copy matters. Native binding is **plausible but unverified**: nothing has ever constructed a `TesseractEngine` in production | If the native bind fails, every image and scanned PDF returns empty text with `OcrStatus='Failed'` and looks like a bad document rather than a broken container | **P0** | The 02-B startup probe settles this in one deploy | Probe output naming the Tesseract version and `eng` load, from the deployed container |
| **02-M** | Arabic / Hijri OCR | **NOT APPLICABLE** | — | `tessdata/` holds `eng.traineddata` only; no `ara.traineddata`; `Dockerfile:16` installs no `tesseract-ocr-ara` | — | — | **Accepted limitation A1** (charter §3, founder approved 2026-08-06) — recorded, not silently dropped | — | Record in `04-risk-and-blocker-register.md` as limitation A1 with an owner and a date | None for pilot |

### 3.3 Golden-corpus dependency (charter A7) — **BLOCKING**

The supplied corpus at `Backend/ERP_RFQ_Automation/Uploads/Evidence/tenants/80101/` is
149 files, but deduplicating the `cleared/` and `quarantine/` mirrors and inspecting bytes
rather than filenames gives a very different picture:

| Apparent | Actual |
|---|---|
| 58 `.csv` | **synthetic test fixtures** — headers `rfqno,buyername,productname,quantity,manufacturerpartnumber`; values `VALVE-A`, `ACTUATOR-ADDED`, `FIELD-VERIFY-FIVE`, `Northstar Buyer` |
| 14 `.doc` | **genuine** — `Composite Document File V2`, SEC "MATERIALS E-BIDDING SYSTEM / Bid Materials List", real bid numbers (`C001046552`), real buyers (`AMER S. AL-DOSSARI`, `57322@se.com.sa`), real plants (Shoaiba, Jeddah South, Arar, QPP) |
| 1 `.pdf` | **45 bytes** — a stub. No `/Font`, no `/XObject`, no `/Image`, 0 pages |
| 1 `.xlsx` | 15 KB, one sheet, no `sharedStrings` — synthetic |

**Zero real specimens exist for native PDF, scanned PDF, XLSX, HTML, JPEG, PNG, MSG or EML
— 8 of the 9 required formats.** No amount of code review closes this. Escalate to the
founder as the A7 blocking dependency it was declared to be: request ≥2 real specimens per
required format, including at least one genuinely scanned tender.

---

## §4 — FR-RFQ-04: EXTRACTION FIELDS

> **Requirement.** Extract buyer/customer name, RFQ/bid/tender number, item description,
> quantity, unit of measure, required delivery date, delivery location, Saudi region/city,
> closing date **and time**, Gregorian date, manufacturer, part number, special notes — at
> **header** and **line** level. *(Arabic/Hijri descoped — charter A1.)*

Owner: Implementation Engineer (audit capacity).

Three artefacts define the achievable ceiling, and they are checked independently:
the prompt (`Services/OllamaLlmService.cs:614-715` `BuildExtractionInstructions`), the DTO
(`Services/Interfaces/ILLMService.cs`), the mapper (`Services/LeadItemMapper.cs`) and the
schema (`Leads` 60 cols / `LeadItems` 28 cols). **A field absent from the prompt cannot be
rescued downstream**, so the prompt is the binding constraint.

### 4.0 Field coverage matrix — production fill rates, n=46 leads / 3,121 line items

| Required field | Prompt asks | DTO | DB column | Production fill | Verdict |
|---|---|---|---|---|---|
| Buyer / customer name | ✅ `BuyersName` `:644`, `CustomerCompanyName` `:667` | ✅ | `Leads.BuyersName`, `Leads.CustomerCompanyNameExtracted` | 39/46 (85%) · **1/46 (2%)** | **PARTIAL** |
| RFQ / bid / tender number | ✅ `Rfqno` `:642`, `CustomerRfqno` `:684` | ✅ | `Leads.RFQNo`, `LeadItems.CustomerRFQNo` | 35/46 (76%) · 2948/3121 (94%) | **VERIFIED** |
| Item description | ✅ `ProductShortName` `:689`, `ProductShortDescription` `:691` | ✅ | both columns | 3070 (98%) · 3083 (99%) | **VERIFIED** |
| Quantity | ✅ `:695` + rule 5 `:623` | ✅ | `LeadItems.Quantity` | 3121/3121 (100%) | **VERIFIED** |
| Unit of measure | ✅ `:693` + rule 13 `:631` (verbatim transcription) | ✅ | `LeadItems.UnitOfMeasure` | 3064 (98%) | **VERIFIED** |
| **Required delivery date** | ❌ **not asked** | `LeadTime` *string* | `LeadItems.LeadTime` **integer** | **0 / 3,121 (0.0%)** | **DEFECTIVE** |
| **Delivery location** | ❌ **not asked** | ❌ | ❌ **no column** | **0** (leaks to `ExtraFields`) | **MISSING** |
| **Saudi region / city** | ❌ **not asked** | ❌ | ❌ **no column** | **0** | **MISSING** |
| Closing **date** | ✅ `BidClosingDate` `:648` | ✅ | `Leads.BidClosingDate` | 14/46 (30%) | **PARTIAL** |
| Closing **time** | ❌ format pinned to `"YYYY-MM-DD"` (rule 4 `:622`) | ❌ | column *is* `timestamp` | **0/46 carry a non-midnight time** | **MISSING** |
| Gregorian date | ✅ rule 4 forces `YYYY-MM-DD` | ✅ | timestamps | `RecDate` 46/46 (100%) | **VERIFIED** |
| Manufacturer | ✅ `ManufacturerName` `:697` | ✅ | `LeadItems.ManufacturerName` | 2806 (90%) | **VERIFIED** |
| Part number | ✅ `ManufacturerPartNumber` `:698`, `ItemMaterialCode` `:685`, `AlternatePartNumber` `:700` | ✅ | all three | 2901 (93%) · 2963 (95%) · 12 | **VERIFIED** |
| Special notes | ⚠️ `HeaderRemarks` `:656` *"1-2 sentences"*; `ItemText` `:701`, `MaterialPotext` `:702` | ✅ | `Leads.HeaderRemarks`, `LeadItems.ItemText`, `.MaterialPOText` | 46/46 but **avg 180 / max 522 chars** · 3014 (97%) · **5 (0.2%)** | **DEFECTIVE** |
| Header **and** line level | ✅ header keys + `Items[]` `:680-711` | ✅ | `Leads` + `LeadItems` FK | 46 leads, **46/46 with ≥1 item**, 3,121 items | **VERIFIED** |

### 4.1 What the *real* documents prove about the four gaps

Not inference — measured across all 14 genuine SEC bid documents (`textutil`-converted):

- **`Ship To` is a per-line column in 14 of 14 documents**, sitting beside `Req Unit` / `Req Qty`.
  It maps to no schema field. It is already leaking into the overflow bucket: `ExtraFields`
  is populated on only 16 of 3,121 items, and **`Ship To` is one of its five keys (6 rows)**,
  alongside `Req Qty` (11), `Req Unit` (11). The extractor is finding delivery location and
  the schema has nowhere to put it.
- **`Address` header block in 14 of 14** — e.g. `Address / Buyer / Buyer Tel` → `Saudi Arabia`.
- **Explicit Saudi delivery locations in free text**, verbatim from the corpus:
  `"Delivery Location to Arar Power Plant Warehouse in the north area."`;
  `"THE DELIVERY OF MATERIALS MUST BE IN THE SAME PLACE MENTIONED IN THE BID \"SEC WAREHOUSES\""`.
  Named sites across the corpus: Arar, Jeddah South, Shoaiba, QPP — `CITY` ×8, `PLANT` ×69,
  `WAREHOUSE` ×6. **Region and city are present in the source and captured nowhere.**
- **Closing time**: `Bid Close` appears in 14/14 as a bare date (`2/28/2021`) in this corpus —
  but note the US `M/D/YYYY` format, and note the header timestamp `2/16/2021 7:16:53 AM`,
  which proves SEC's exporter emits times. The schema can hold one; the prompt forbids it.
- **Special notes are enormous.** Real blocks run to 2,000+ characters — ten numbered
  contractual terms (`IMPORTANT NOTE TO BIDDERS`: bid-bond waivers, LME metal factors,
  catalogue-attachment requirements, 7-day PO objection windows, 5-Star safety compliance).
  `HeaderRemarks` averages **180 characters** in production because the prompt asks for
  *"a very brief (1-2 sentences) summary"*. Terms that decide whether a bid is disqualified
  are being summarised away.

### 4.2 Requirement rows

| ID | Requirement | Status | Existing implementation | Exact evidence | Missing behaviour | Defects | Pilot impact | Pri | Recommended action | Acceptance evidence required |
|---|---|---|---|---|---|---|---|---|---|---|
| **04-A** | Buyer / customer name | **PARTIAL** | `OllamaLlmService.cs:644` + rules 10-11 `:628-629` (direction-of-trade + verbatim evidence snippet) | `Leads.BuyersName` 39/46 (85%). **`CustomerCompanyNameExtracted` 1/46 (2%)**, `CustomerCompanyEvidence` 1/46 | The buying organisation on 45 of 46 leads | Rule 11 requires a ≤120-char verbatim snippet or **both** fields return null. In the SEC corpus the buyer block is `Vendor Code / Vendname` — which rule 10 correctly routes to `SupplierNameOnDocument` (19/46) — and SEC's own name appears only in letterhead, which rule 10 explicitly forbids inferring from. **The rules are individually right and jointly starve the field** | Customer resolution, routing and dedupe all key off customer identity. At 2% the pilot cannot demonstrate customer matching | **P0** | Permit a document-scoped fallback: an issuer identified by e-mail domain (`@se.com.sa`) or portal branding is legitimate evidence, distinct from letterhead inference — with its own lower confidence and reason code | `CustomerCompanyNameExtracted` ≥ 80% on the 14-document SEC corpus, each with a non-null evidence snippet |
| **04-B** | RFQ / bid / tender number | **VERIFIED** | `:642` header, `:684` per line | `Leads.RFQNo` 35/46 (76%); `LeadItems.CustomerRFQNo` 2948/3121 (94%); real values match the corpus (`C001046552`) | — | 11 leads carry no RFQ number; identity/dedupe then leans on the fingerprint alone | Duplicate detection degrades on the 24% | **P2** | Measure whether the 11 are genuinely numberless documents before changing anything | Per-document diff against the 14-doc corpus ground truth |
| **04-C** | Item description | **VERIFIED** | `:689`, `:691` | 3070/3121 (98%), 3083/3121 (99%) | — | — | — | — | — | Sustained ≥95% on the golden corpus |
| **04-D** | Quantity | **VERIFIED** | `:695`, rule 5 `:623`, `LenientQuantityConverter:783-827`, `Extraction/Quantities/QuantityParser.cs` | 3121/3121 (100%). Charter records the `"1,000"→1` fabrication as fixed with DB CHECK constraints | — | — | — | — | — | No `Quantity=1` cluster on re-drive |
| **04-E** | Unit of measure | **VERIFIED** | `:693`, rule 13 `:631` — verbatim transcription, canonicalisation deferred to a shared mapper, explicit ban on defaulting to `EA`, explicit package-vs-count rule | 3064/3121 (98%); real corpus units `EA` present | — | — | — | — | — | UOM distribution shows no synthetic default |
| **04-F** | **Required delivery date** | **DEFECTIVE** | nearest analogue only: `LeadTime` | **The prompt never asks for a required delivery date.** It asks for `"LeadTime": string \| null` (`:703`) with no format guidance, so the model returns prose (`"4 weeks"`, `"ARO 30 days"`). `LeadItemMapper.cs:60-62` then does `int.TryParse(source.LeadTime, NumberStyles.Integer, InvariantCulture, out var leadTime) ? leadTime : null` into an **`integer`** column. **Result: 0 of 3,121 rows populated — a 100% loss.** `SubDate` 3/46, `AcknowledgmentDate` 0/46 | A `RequiredDeliveryDate` (header) and `RequiredDeliveryDateLine` (line) — distinct from lead time | A string→int contract mismatch that discards every value silently. Nothing logs it; the field simply reads as "the document didn't say" | The BRD names required delivery date explicitly. A quote priced without it is commercially wrong, and the reviewer has no signal that the date was ever seen | **P0** | Add `RequiredDeliveryDate` to prompt/DTO/schema as a `YYYY-MM-DD` date; **separately** keep `LeadTime` but change the prompt to demand a bare integer day count, or widen the column to text and parse with the tolerant converter family | `RequiredDeliveryDate` populated on every corpus document that states one; `LeadTime` no longer 0% where the document states a lead time |
| **04-G** | **Delivery location** | **MISSING** | none | No `Delivery*` column exists on `Leads` **or** `LeadItems` (`information_schema` → 0 and 0). `LeadItems.StorageLocation` (2772/3121) is the SAP *storage location* code, a different concept. **`Ship To` — the actual delivery column, present in 14/14 real documents — has no field and is surfacing in `ExtraFields` on 6 rows** | `DeliveryLocation` at header **and** line level (SEC ships different lines to different plants, so line level is required, not optional) | The data is being extracted and then discarded into an untyped overflow bucket capped at 20 keys per item (rule 7 `:625`) | Sales engineers cannot route or price freight. `"Delivery Location to Arar Power Plant Warehouse in the north area"` is in the corpus today and invisible in the product | **P0** | Add `DeliveryLocation` to the prompt (header + item), DTO, mapper and both tables; backfill from the `Ship To` values already sitting in `ExtraFields` | `DeliveryLocation` populated on ≥90% of the 14-doc corpus; zero `Ship To` keys remaining in `ExtraFields` |
| **04-H** | **Saudi region / city** | **MISSING** | none | No region/city column on `Leads` or `LeadItems`. The prompt never mentions region, city, province or a Saudi place name. Real corpus contains `CITY` ×8, `JEDDAH` ×2, plus Arar/Shoaiba/QPP plant names and `Address: Saudi Arabia` in 14/14 | `Region` + `City` fields, ideally normalised against the 13 Saudi administrative regions | Region is derivable from delivery location once 04-G lands, but nothing derives it today | The BRD calls for Saudi region/city specifically — it drives BU routing and logistics. Absent end to end | **P1** (unblocks after 04-G) | Add `Region`/`City` to the header schema; populate by normalising the 04-G delivery location against a Saudi region/city gazetteer, with the raw text retained as evidence | Region/city populated on every corpus document naming a Saudi site; `"north area"` → `Northern Borders`, `Arar` → city |
| **04-I** | Closing date **and time** | **PARTIAL / DEFECTIVE** | `:648` `BidClosingDate`, `:705` `BidClosingDateLine` | `Leads.BidClosingDate` is `timestamp without time zone` — **the column can hold a time**. Rule 4 (`:622`) pins every date to `"YYYY-MM-DD"`, so `select count(*) filter (where "BidClosingDate"::time <> '00:00:00')` → **0 of 14**. Line-level `BidClosingDateLine` 2937/3121 (94%) | Time-of-day on the closing timestamp, and an explicit time zone (Arabia Standard Time, UTC+3) | **Tender closing time is decisive.** A bid at 14:05 against a 14:00 close is rejected. The product currently implies "any time that day" | Header fill is also only 14/46 (30%) despite `Bid Close` being present in 14/14 real documents | **P0** | Extend the prompt to `"YYYY-MM-DDTHH:MM"` for `BidClosingDate` only (leave pure dates elsewhere), record the source time zone, and store UTC + offset | A corpus document stating a closing time produces a non-midnight `BidClosingDate` with a recorded offset |
| **04-J** | Gregorian date output | **VERIFIED** | rule 4 `:622` `Dates must be in YYYY-MM-DD format or null`; `LeadItemMapper.cs:64-66` `SanitizeDate` rejects the `0001-01-01` sentinel | `RecDate` 46/46; all stored values are Gregorian timestamps | — | Source dates in the corpus are US `M/D/YYYY`; nothing pins the reading order, so `3/4/2021` is ambiguous between 4 March and 3 April | A one-month closing-date error would be silent and material | **P1** | State the source convention explicitly in the prompt and require the verbatim source string alongside the parsed date | An unambiguous corpus date (`2/28/2021`) and an ambiguous one (`3/4/2021`) both resolve correctly with the raw string retained |
| **04-K** | Manufacturer | **VERIFIED** | `:697` | 2806/3121 (90%). Real corpus carries `CAMPINI`, `HONEYWELL`, `GOULDS PUMPS`, `ALSTOM`, `KLAUKE GMBH` in the `ADDITIONAL DATA` prose — the extractor is genuinely reading them out of unstructured text | — | — | — | — | — | ≥90% sustained on the golden corpus |
| **04-L** | Part number | **VERIFIED** | `:698` `ManufacturerPartNumber`, `:685` `ItemMaterialCode`, `:700` `AlternatePartNumber` | 2901 (93%) · 2963 (95%) · 12. Corpus values (`P/N#HTGD337039P0002`, `Model#TY 95`, `909203904`) confirm both SEC material codes and vendor part numbers are captured | — | — | — | — | Charter records the per-`(BusinessUnitId, PartNo)` uniqueness fix | ≥90% sustained |
| **04-M** | Special notes | **DEFECTIVE** | `HeaderRemarks` `:656`, `ItemText` `:701`, `MaterialPotext` `:702` | `HeaderRemarks` 46/46 but **min 15 / avg 180 / max 522 characters** — because the prompt asks for *"A very brief (1-2 sentences) summary"*. Real blocks exceed 2,000 characters with ten numbered contractual terms. `LeadItems.MaterialPOText` **5/3121 (0.2%)**. Worse, the field is shared with pipeline diagnostics: real values read `"[NEEDS REVIEW] Item count mismatch: expected 39, extracted 1. For foreign suppliers, if delivery type is CIF or DDP…"` — system status and buyer terms concatenated into one column | Verbatim capture of the buyer's terms, separate from reviewer diagnostics | Two defects: (1) a lossy summary of contractually binding text; (2) `HeaderRemarks` doubles as a diagnostics channel, so the buyer's own words are diluted by pipeline messages a client should never see in that field. `ItemText` at 97% is healthy — the loss is concentrated at header level | Bid-bond waivers, catalogue-attachment requirements and LME metal-factor clauses decide disqualification. Summarising them is a commercial risk the client will recognise instantly | **P0** | Split into `HeaderRemarks` (verbatim buyer terms, generous length cap) and a separate `ProcessingNotes` column for `[NEEDS REVIEW]` diagnostics; drop the "1-2 sentences" instruction | On the `IMPORTANT NOTE TO BIDDERS` document, all ten numbered terms present verbatim; no `[NEEDS REVIEW]` text in `HeaderRemarks` |
| **04-N** | Header **and** line level | **VERIFIED** | Prompt emits header keys + `Items[]` (`:641-712`); `Extraction/ChunkedExtractionService.cs` chunks over `LineItemRegions` built at `ProductionDocumentReader.cs:164-198`; persisted to `Leads` (60 cols) + `LeadItems` (28 cols, FK `LeadID`) | 46 leads, **46/46 carry ≥1 item**, 3,121 items total (avg 68/lead — consistent with real SEC bid lists of 39-118 lines). Both levels demonstrably populated | — | Header-level dates are far weaker than line-level: `BidClosingDate` 30% header vs `BidClosingDateLine` 94% line. The header is being under-read relative to the lines | Reviewers see a document whose lines know the closing date and whose header does not | **P1** | Back-fill header dates from a consistent line-level majority, with a recorded derivation reason | Header `BidClosingDate` ≥ 90% where `BidClosingDateLine` is populated |
| **04-O** | Arabic field extraction | **NOT APPLICABLE** | — | Zero Arabic documents in production; corpus is entirely English | — | — | **Accepted limitation A1** (founder approved 2026-08-06) | — | Record in `04-risk-and-blocker-register.md` | None for pilot |

### 4.3 The four BRD-required fields captured nowhere at all

Stated plainly, because this is the headline of §4:

| Field | Prompt | DTO | DB column | Production rows |
|---|---|---|---|---|
| **Delivery location** | ❌ | ❌ | ❌ | 0 |
| **Saudi region / city** | ❌ | ❌ | ❌ | 0 |
| **Required delivery date** | ❌ | partial (`LeadTime`) | wrong type | **0 of 3,121** |
| **Closing time-of-day** | ❌ (format-blocked) | ❌ | column exists | **0 of 46** |

The first three are new fields, not tuning. The fourth is a one-line prompt change against a
column that already accepts a time. All four are demonstrably present in the real client
documents held in `Uploads/Evidence/tenants/80101/`.

---

## FR-RFQ-05

*Owner: Security / Reliability Reviewer. Production evidence read-only from the live Neon
database, 2026-08-06. Ingestion is live, so counts moved during the audit (44 → 47
occurrences); each figure is quoted against the n it was measured with.*

**Requirement text — AMENDMENTS AS VERSIONS.** An amended RFQ must become a **version of the
existing RFQ**, never an unrelated new one.

| Requirement ID | Status | Priority |
|---|---|---|
| FR-RFQ-05 | **DEFECTIVE** | **P0 — pilot blocker** |

| Column | Content |
|---|---|
| **Requirement ID** | FR-RFQ-05 |
| **Requirement text** | An amended RFQ must be recorded as a new **revision of the existing canonical lead/RFQ**, preserving lineage, prior state and downstream impact. It must never create an unrelated new canonical RFQ. |
| **Status** | **DEFECTIVE** — the model is complete and well designed; the decision logic contains a fall-through that converts a high-similarity amendment into a brand-new canonical RFQ, and the path has **never executed once in production**. |
| **Existing implementation (file:line)** | Revision creation: `LeadIdentity/LeadIdentityApplicationService.cs:200-241` (`CreateRevisionAsync`). Strong revision match `:125-139`; grouped revision match `:141-159`. Entities: `LeadIdentity/LeadIdentityEntities.cs` — `LeadRevision`, `LeadItemRevision`, `LeadRevisionDifference`, `LeadRevisionImpact`. Diff engine `:891-940`. Downstream impact fan-out `:811-825` (`AddImpactsAsync`). Human revision decision `:696-717`. Read model `:552-568` (`GetRevisionsAsync`); route `Controllers/LeadIngestionController.cs:94` (`GET /api/LeadIngestion/leads/{leadId}/revisions`). UI `Frontend/src/pages/Leads/LeadRevisionTimeline.tsx`, routed `Frontend/src/App.tsx:297`. DB immutability guard `Migrations/20260725022734_Release01BIntakeIdentityAcceptance.cs:422-434` (`trg_release01b_lead_occurrence_source_guard`, confirmed live in `pg_proc`). |
| **Exact evidence** | **The revision path has never run in production.** `SELECT "RevisionNumber", count(*) FROM "LeadRevisions" GROUP BY 1;` → `1 \| 46` — every revision row is the one minted at lead creation. `SELECT count(*) FROM "LeadRevisionDifferences";` → **0**. `SELECT "Classification", count(*) FROM "LeadIngestionOccurrences" GROUP BY 1;` → `New \| 47` — no `Revision`, no `ExactDuplicate`, no `PossibleMatchReviewRequired`. `SELECT "EventType", count(*) FROM "LeadIdentityAuditEvents" GROUP BY 1;` → `LEAD_CREATED \| 26`, `LEGACY_CANONICAL_IDENTITY_BACKFILLED \| 23` — no `LEAD_REVISION_CREATED`, no `INGESTION_DUPLICATE_RECORDED`, no `POSSIBLE_MATCH_RAISED`. **Production already contains the failure shape** — same tenant + same normalised customer scope + same normalised customer RFQ reference, yet separate canonical RFQs with separate client-facing references: `41600` → leads 398/399/406 (`NXR-2026-000007`, `NXR-2026-000008`, `NXR-2026-000015`); `6000281833` → leads 392/407 (`NXR-2026-000001`, `NXR-2026-000016`); `9500198197` → leads 395/402 (`NXR-2026-000004`, `NXR-2026-000011`). All seven classified `New`. *(All seven are `SourceChannel='Legacy'` backfill rows — they evidence the data shape and the backfill gap, not a live execution of the current code. The live-code failure is established by D-05.1.)* |
| **Missing behaviour** | (a) No revision has ever been produced from a real document, so lineage, diff rendering, `LeadRevisionImpact` fan-out to RFQ/Quote/Order and the revision timeline are **unproven end to end**. (b) `LeadIntakeDescriptor.EmailThreadId` is hard-coded `null` by the only caller (`Extraction/ExtractionWorker.cs:927`), so an amendment arriving as a **reply in the same mail thread** carries no thread identity — 0/47 occurrences have `EmailThreadId`. (c) `LogicalGroupKey` is 0/47, so the grouped-revision branch (`:141-159`) is dead code on every channel that has ever run. (d) `ReconcileAsync` has exactly one caller — `Extraction/ExtractionWorker.cs:921` — so any channel not terminating in an extraction job never reaches identity at all. |
| **Defects** | **D-05.1 (S1) — a high-similarity amendment is discarded and a new canonical RFQ is created.** `LeadIdentityApplicationService.cs:165-166`: `if (ranked is not null && (groupedLeadIds.Contains(ranked.Lead.Id) \|\| scope is null \|\| CustomerScope(ranked.Lead, null) is null))`. A possible match is raised **only when customer identity is unresolved**. When the incoming document *and* the candidate lead both resolve a customer scope — the normal, healthy case — and there is no `LogicalGroupKey` (0/47, never populated), a match scoring up to **1.00** (byte-identical line items) fails the condition, falls through to `:184-197` and mints a **new `Lead`, new `LeadRevision` #1, new `CommercialCaseReference`**. No possible match, no link, no operator signal. Fires whenever the reference string changes (`RFQ-123` → `RFQ-123 Rev B`) or is absent — 12 of 47 production leads carry no `RFQNo` at all. **D-05.2 (S2) — revision matching is capped at the 250 newest leads.** `:121-123` `.OrderByDescending(x => x.CreatedDate).Take(250)`. The unbounded DB path (`strongLeadId`, `:125-130`) fires only when the candidate lead's occurrence carries a non-null `CustomerScopeKey`; the scope fallback `:134-136` and **all** similarity ranking `:147-164` see only the 250 most recent leads. At the charter's 900 inquiries/month that is roughly **8 calendar days** of history. **D-05.3 (S3) — the Legacy backfill left `CustomerScopeKey` NULL.** `SELECT "SourceChannel", count(*), count(*) FILTER (WHERE "CustomerScopeKey" IS NOT NULL) FROM "LeadIngestionOccurrences" GROUP BY 1;` → `Legacy 23 → 0`; `ManualUpload 24 → 22`. The unbounded `strongLeadId` query at `:128` requires `o.CustomerScopeKey == scope`, so **all 23 backfilled canonical leads are unreachable** by the unbounded revision path and depend entirely on the 250-row window, compounding D-05.2. **D-05.4 (S2)** — stale fingerprints manufacture phantom amendments; see FR-RFQ-06 D-06.1. |
| **Security impact** | None directly. Revision rows are tenant-scoped (`BusinessUnitId` on `LeadRevision`, `LeadRevisionDifference`, `LeadRevisionImpact`) and source linkage is DB-immutable via `trg_release01b_lead_occurrence_source_guard`. Indirect integrity risk: D-05.1 fragments one customer commitment across several canonical records, so an access, retention or erasure action taken on one record does not cover the others. |
| **Pilot impact** | **Blocking.** The most quotable client scenario — "the buyer sends Rev B, Nexora shows it as version 2 of the same RFQ with a line-by-line diff" — is not demonstrable: it has never happened in production, and D-05.1 makes the most likely live rehearsal (same buyer, amended document, reference changed or absent) produce a second unrelated RFQ with a second client-facing number. That is precisely the "duplicate corruption / broken revision lineage" NO-GO condition in charter §6. |
| **Priority** | **P0** |
| **Recommended action** | 1. **Fix D-05.1**: raise `PossibleMatchReviewRequired` whenever `ranked.Score >= 0.65`, regardless of whether both scopes resolve; treat an *equal* resolved scope as **corroborating** evidence (raise the score, or auto-revision), never as grounds to suppress. Never fall through to `New` while a ≥0.65 candidate exists. 2. **Fix D-05.2**: replace `.Take(250)` with a tenant-scoped indexed candidate query (by `CustomerScopeKey`, normalised RFQ reference, or line-identity fingerprint via an index on `LeadItemRevisions.LineFingerprint`) so recall does not decay with volume. 3. **Fix D-05.3**: backfill `CustomerScopeKey` on the 23 `Legacy` occurrences from `Leads.CustomerID / Clientemail / BuyersName` using the same `CustomerScope` rule. 4. **Populate `EmailThreadId`** at `ExtractionWorker.cs:927` from mail metadata and add an in-thread revision arm ahead of similarity. 5. Re-drive the founder's golden corpus and prove at least one real `Revision` with differences, impacts and timeline. |
| **Acceptance evidence required** | (i) `SELECT "Classification", count(*) FROM "LeadIngestionOccurrences" GROUP BY 1;` shows a non-zero `Revision` count from a **real** corpus document. (ii) `SELECT "LeadId","RevisionNumber" FROM "LeadRevisions" WHERE "RevisionNumber" > 1;` non-empty, with matching `LeadRevisionDifferences`. (iii) `LeadIdentityAuditEvents` contains `LEAD_REVISION_CREATED`. (iv) The violation query returns **zero rows**: `SELECT "BusinessUnitID", lower(regexp_replace(coalesce("Clientemail","BuyersName"),'[^a-zA-Z0-9]','','g')), lower(regexp_replace("RFQNo",'[^a-zA-Z0-9]','','g')), count(*) FROM "Leads" WHERE "RFQNo" <> '' GROUP BY 1,2,3 HAVING count(*) > 1;`. (v) Browser: `LeadRevisionTimeline` renders v1→v2 with a line-level diff and the downstream-impact list. (vi) Regression test: same buyer, no RFQ reference, identical line items, second ingest → asserts **not** `New`. (vii) Volume test: an amendment to a lead older than 250 leads still links. |

---

## FR-RFQ-06

*Owner: Security / Reliability Reviewer.*

**Requirement text — IDENTITY DECISIONS.** Detect and present **Exact duplicate /
Revision-amendment / Possible match / New RFQ**, using at minimum customer-buyer, document
identity, RFQ reference, items, dates, attachment hashes, message identity and similarity
signals.

| Requirement ID | Status | Priority |
|---|---|---|
| FR-RFQ-06 | **PARTIAL** (with an S1 defect on the human-decision path) | **P0** |

| Column | Content |
|---|---|
| **Requirement ID** | FR-RFQ-06 |
| **Requirement text** | Classify every ingestion occurrence as `ExactDuplicate`, `Revision`, `PossibleMatchReviewRequired` or `New`, and present that decision with its evidence to a human, using customer-buyer identity, document identity, RFQ reference, items, dates, attachment hashes, message identity and similarity signals. |
| **Status** | **PARTIAL** — all four classes are modelled, computed and surfaced; **only `New` has ever executed in production**; two of the eight required signals are never populated; and the human decision path corrupts the canonical record. |
| **Existing implementation (file:line)** | Classification core `LeadIdentity/LeadIdentityApplicationService.cs:44-198` (`ReconcileCoreAsync`) — exact-duplicate arm `:104-119`, revision arms `:125-139` and `:141-159`, possible-match arm `:161-182`, new `:184-197`. Fingerprint `:842-848` (`Fingerprint` → `Snapshot` → `ItemSnapshot`). Customer scope `:864-869`. UoM equivalence inside the fingerprint `:863` → `Services/Uom/UomCanonicalizer.cs:288-297` (`EquivalenceKey`). Similarity `:872-881` (Jaccard ∨ 0.75 × containment over `LineIdentityFingerprint`). Human decision `:664-754` (`DecideMatchCoreAsync`), route `Controllers/LeadIngestionController.cs:102`. Presentation `:243-388` (`GetBatchAsync`), `:426-507` (`GetDuplicateUploadsAsync`), `:533-551` (`GetPossibleMatchesAsync`); UI `Frontend/src/pages/Leads/LeadIngestionBatchPage.tsx`, `PossibleMatchesPage` (`App.tsx:298`), sidebar `Sidebar.tsx:167`. Decision serialised into the intake ledger `Extraction/ExtractionWorker.cs:936-952`. Concurrency: `pg_advisory_xact_lock` at `:61` and `:670`. |
| **Exact evidence** | **Only one of four decisions has ever executed.** `SELECT "Classification", count(*) FROM "LeadIngestionOccurrences" GROUP BY 1;` → `New \| 47`. `SELECT count(*) FROM "LeadMatchCandidates";` → **0**. `SELECT count(*) FROM "LeadReviewAudits";` → **0**. `LeadIdentityAuditEvents` contains no `POSSIBLE_MATCH_RAISED` and no `POSSIBLE_MATCH_DECIDED`. **Signal coverage over all 47 production occurrences**: `LogicalInquiryFingerprint` 47 · `ContentHash` 24 · `ExternalSourceId` 24 · `SourceDocumentId` 24 · `SourceDocumentOccurrenceId` 24 · `CustomerScopeKey` 22 · **`EmailThreadId` 0** · **`LogicalGroupKey` 0** · **`MimeType` 0** · **`FileSize` 0**. Lead-side signals (n=47): `BuyersName` 39 · `RFQNo` 35 · `Clientemail` 21 · `BidClosingDate` 14 · **`CustomerID` 0**. Only two channels have ever reached the identity service — `Legacy` 23 (backfill) and `ManualUpload` 24 — so **email ingestion has never produced an identity decision**. |
| **Missing behaviour** | **Message identity — absent.** `EmailThreadId` is passed as literal `null` (`ExtractionWorker.cs:927`, 5th positional argument of `LeadIntakeDescriptor`); 0/47. No RFC-822 `Message-Id` / `In-Reply-To` / `References` signal participates in any decision. **Document identity — partial.** `MimeType` and `FileSize` are both passed as literal `null` (`ExtractionWorker.cs:928`) and are 0/47; only `ContentHash` + `SourceDocumentId` carry document identity, and only on the 24 live-path rows. **Customer-buyer — weak.** `Leads.CustomerID` is NULL on 47/47, so `CustomerScope` (`:866`) never returns the strong `customer:{id}` form and always degrades to `email:` or `buyer:` string matching; 8/47 leads resolve **no scope at all**, and for those the exact-duplicate content-hash arm (`:107`, requires `scope != null`) is structurally unreachable. **Dates — thin.** `BidClosingDate` present on only 14/47 leads, so the date component of the fingerprint is null for 70 % of the corpus. **Attachment hashes — one hash per job, not per attachment.** `job.ContentHash` hashes the single extracted document; there is no set-of-attachment-hashes signal for a multi-attachment mail. |
| **Defects** | **D-06.1 (S2) — stale dedup fingerprints; historical rows were not backfilled after UoM canonicalisation.** Commit `6ff5637` changed `ItemSnapshot` and `LineIdentityFingerprint` from `Normalize(x.UnitOfMeasure)` to `NormalizeUom(...)` (`LeadIdentityApplicationService.cs:847`, `:853`, `:863`). Stored `LogicalInquiryFingerprint` values written before that commit hash the raw spelling; the same document re-ingested today hashes the canonical code. **Quantified against production:** 7 stored spellings drift (`each`→`ea`, `pcs`→`ea`, `Pcs`→`ea`, `piece`→`ea`, `NOS`→`ea`, `Kit`→`set`, `Activ.unit`→`au`) — **2,898 of 3,121 line items (92.9 %)**, **10 of 46 canonical leads (21.7 %)**, 10 occurrence rows and 10 revision rows now carrying unreachable fingerprints. Blocker assessment below. **D-06.2 (S1) — `ApplySnapshotProjection` overwrites the canonical lead with normalised fingerprint text and drops every other field.** `:961-977`. `Snapshot()` (`:844-847`) stores only `Normalize()`d values, so on any human decision of `revision`, `link` or `create_new` (`:715`, `:730`) the canonical record is rewritten from it: `Lead.Rfqno` `RFQ-2026/0012` → `rfq20260012`; `Lead.BuyersName` `John Smith` → `johnsmith`; every `LeadItem` is deleted (`:970`) and rebuilt with **five** properties — `LineItemNo`, `ManufacturerPartNumber`, `ProductShortDescription`, `Quantity`, `UnitOfMeasure` — all normalised, silently discarding `UnitPrice`, `Currency`, `CustomerRfqno`, `ItemMaterialCode`, `ManufacturerName`, `LeadTime`, `BidClosingDateLine`, `Aiconfidence`, `ExtraFields`. The verbatim text is **not recoverable** from `LeadRevision.SnapshotJson` or `LeadRevisionDifferences` — both are normalised. The automatic path is unaffected: `ApplyCurrentProjection` (`:834-841`) uses `CloneCurrentItem` (`:979-990`) and preserves everything. Because `LeadMatchCandidates` is 0, **no production data is corrupted yet**; the defect fires the first time an operator uses the match-review queue, which is one click from the sidebar. **D-06.3 (S1)** — see FR-RFQ-05 D-05.1: a ≥0.65 match is silently downgraded to `New` when both scopes resolve. **D-06.4 (S3) — the new revision records the *old* customer RFQ reference.** `:706` sets `NormalizedCustomerRfqReference = Normalize(canonical.Rfqno)` **before** `ApplySnapshotProjection` (`:715`) updates `canonical.Rfqno`, so the latest revision indexes the superseded reference and the unbounded `strongLeadId` lookup (`:127`) lags one amendment behind. **D-06.5 (S3) — `Confidence` is a policy constant, not a confidence.** Hard-coded `1m` for `New`/`ExactDuplicate` and `.98m` for `Revision` (`:113`, `:187`, `:221`); only the possible-match arm carries a computed score. Charter §5 already records `AIConfidence` as fabricated; the same caution applies to anything rendered from `Confidence`. |
| **Security impact** | Moderate, and none of it cross-tenant. Every identity query in `ReconcileCoreAsync` and `DecideMatchCoreAsync` carries an explicit `BusinessUnitId` predicate (`:91`, `:105`, `:111`, `:122`, `:127-128`, `:144`, `:672`, `:676`), and `LeadIngestionOccurrences` is uniquely indexed on `("BusinessUnitId","IdempotencyKey")` (`IX_LeadIngestionOccurrences_BusinessUnitId_IdempotencyKey`, confirmed in `pg_indexes`), so an idempotency key cannot be replayed across tenants. `DecideMatchCoreAsync` resolves the candidate only from `occurrence.MatchCandidates` (`:686`), so a client-supplied `CandidateLeadId` cannot reach another tenant's lead. Optimistic concurrency is enforced (`:688`) and the decision is idempotency-keyed against `LeadIdentityAuditEvents` (`:673`). **The exposure is integrity, not confidentiality**: D-06.2 destroys buyer-supplied commercial text on a human action, with no undo and no verbatim copy retained. |
| **Pilot impact** | **Blocking.** Three of the four required decisions have never been produced from a real document, so FR-RFQ-06 cannot be demonstrated beyond "everything is New". Worse, the one operator action that *would* demonstrate it — opening Possible Matches and confirming a revision — is the action that corrupts the customer's own RFQ reference and part numbers (D-06.2). |
| **Priority** | **P0** (D-06.2, D-06.3) · **P1** (D-06.1 backfill, message identity) |
| **Recommended action** | 1. **D-06.2 — stop projecting the fingerprint snapshot onto the canonical record.** The revision snapshot is a *hash input*, not a *state document*. Persist a verbatim snapshot alongside it (or carry the incoming `Lead` through the review decision exactly as the automatic path does with `CloneCurrentItem`), and make `ApplySnapshotProjection` merge only genuinely changed fields. Add a regression test asserting `Rfqno`, `ManufacturerPartNumber`, `UnitPrice` and `Currency` survive a `revision` decision. 2. **D-06.3** — as FR-RFQ-05 recommended action 1. 3. **D-06.1 — backfill fingerprints** in a migration: recompute `LogicalInquiryFingerprint` on `LeadIngestionOccurrences` and `LeadRevisions`, and `Leads.CurrentInquiryFingerprint`, for the affected leads; add a `PolicyVersion` guard so a future fingerprint change is detected rather than silently degrading. 4. **Populate `EmailThreadId`, `MimeType`, `FileSize`** at `ExtractionWorker.cs:927-928`; add an in-thread identity arm before similarity. 5. Resolve `Leads.CustomerID` during ingestion so `CustomerScope` reaches its strong form. 6. Do not render `Confidence` as a percentage for `New`/`ExactDuplicate`/`Revision`; label it a policy constant. |
| **Acceptance evidence required** | (i) `SELECT "Classification", count(*) FROM "LeadIngestionOccurrences" GROUP BY 1;` returns non-zero counts for **all four** classes from real corpus documents. (ii) `SELECT count(*) FROM "LeadMatchCandidates";` > 0 with populated `MatchEvidenceJson` / `DifferencesJson` / `DownstreamImpactJson`. (iii) `SELECT count(*) FILTER (WHERE "EmailThreadId" IS NOT NULL), count(*) FILTER (WHERE "MimeType" IS NOT NULL) FROM "LeadIngestionOccurrences";` both > 0 after an email-channel ingest. (iv) **D-06.2 proof**: capture `Leads."RFQNo"` and the full `LeadItems` row set before and after a `revision` decision via `POST /api/LeadIngestion/match-reviews/{id}/decision`; assert byte-identical verbatim fields and zero dropped columns. (v) **D-06.1 proof**: after the backfill, re-ingesting a document whose items use `each`/`NOS`/`Kit` classifies as `ExactDuplicate`, not `Revision`. (vi) Browser: the Possible Matches queue renders candidate evidence and all four decision actions, and `LeadReviewAudits` gains its first row. |

### FR-RFQ-06 — UoM fingerprint drift: is it a pilot blocker?

**Assessment: NO — S2 technical debt with a mandatory pre-pilot backfill, not a blocker.**

Reasoning taken from the code paths rather than from the change description:

- `Similarity()` (`LeadIdentityApplicationService.cs:872-881`) recomputes `LineIdentityFingerprint`
  from the **live entities on both sides** at comparison time. It never reads a stored
  fingerprint, so similarity-based matching is **immune** to the drift.
- Only two comparisons read a *stored* fingerprint: the exact-duplicate arm (`:105`,
  `o.LogicalInquiryFingerprint == fingerprint`) and the revision-identity check (`:205`,
  `x.LogicalInquiryFingerprint == fingerprint`).
- When those miss, control does **not** default to `New` — it falls to the strong
  scope+reference arm (`:125-139`) and then to similarity (`:161-182`). The stored fingerprint
  is therefore **never the sole gate on creating a canonical RFQ**, so drift alone cannot cause
  a silent duplicate canonical record or silent loss.

**What it does cause**, on the 10 affected leads (21.7 % of the corpus; 2,898 of 3,121 items):
a genuine re-send is **downgraded from `ExactDuplicate` to `Revision`**, and the resulting diff
shows a *phantom unit change on every single line* (`each` → `EA`). The operator is shown a
fabricated amendment. That is a credibility and data-quality defect in front of a client, not
a data-loss defect.

**Condition.** The backfill in recommended action 3 must land **before** the pilot corpus is
loaded, otherwise every historical document in the demo set re-ingests as a fake amendment.
Tracked as **R-ID-01** in `04-risk-and-blocker-register.md`.

---

## FR-RFQ-03 — NUMBERING

> **Requirement.** Every accepted RFQ receives a unique, client-facing RFQ number of the form
> `RFQ-KSA-{YYYY}-{sequence}`, plus a **separate agreement / contract reference** for standing RFQ
> agreements. Number generation must be **concurrency-safe**.
>
> *Section owner: SDET / Pilot Auditor. Assessed under charter amendment A3 — reuse and map existing
> numbering; do not build a parallel scheme.*

### 3.0 What actually generates a client-facing reference today

Three distinct references exist. Only one is generated by a governed, concurrency-safe path.

| Reference | Generated where | Format in production | Populated (51 leads) |
|---|---|---|---|
| **`CommercialCaseReference`** (a.k.a. `NexoraSerial` downstream) | PostgreSQL trigger `nexora_assign_commercial_case()` — `Migrations/20260722033825_AddPermanentLeadReferences.cs:209-277` | `NXR-2026-000001` … `NXR-2026-000047` | **51 / 51, all distinct** |
| **`Leads.RFQNo`** | Not generated — **copied from the buyer's document by extraction** | `EL2130001`, `3C2-AMER AL-DOSSARY`, `C001046938`, `ESOSA C…` | 35 / 51 populated, **only 26 distinct** |
| **`Rfq.Rfqno`** (on conversion) | `Repositories/RfqRepository.cs:353-354` | `NXR-RFQ-{BU}-{yyyy}-{D8}` | n/a — no RFQ created in the re-drive cohort |

**The critical disambiguation, because it is easy to get backwards:** `RFQNo` is *the customer's own
reference*, not ours. It is not unique (26 distinct across 35 populated), it is not ours to control,
and it must never be presented as Nexora's RFQ number. The Nexora client-facing number is
`CommercialCaseReference`. `NexoraSerial` is **not a fourth generator** — it is a denormalised copy
of `CommercialCaseReference` propagated Lead → RFQ → Quote → Order (`Models/Rfq.CommercialIdentity.cs:26`,
`Models/Quote.CommercialIdentity.cs:29,46`), guarded against replacement at
`Rfq.CommercialIdentity.cs:14-24` ("An RFQ Nexora Serial cannot be replaced").

### 3.1 The generator, in detail

Allocation is a single atomic `nextval` inside a `BEFORE INSERT` trigger —
`Migrations/20260722033825_AddPermanentLeadReferences.cs:239`:

```sql
allocation_number := nextval('"CommercialCaseReferenceSequence"');
```

Client-supplied values are rejected outright (`:223-225`), and the format is assembled by token
substitution (`:249-255`) from a **per-tenant configuration row**:

```sql
generated_reference := replace(generated_reference, '{PREFIX}',   …upper(cfg."Prefix")…);
generated_reference := replace(generated_reference, '{YEAR}',     to_char(created_at, 'YYYY'));
generated_reference := replace(generated_reference, '{SEQUENCE}', lpad(allocation_number::text, cfg."SequencePadding", '0'));
```

Supported tokens: `{PREFIX} {YEAR} {FY} {BU} {SOURCE} {SEQUENCE}` — mirrored in C# at
`Services/LeadReferenceFormatter.cs:12-37` with validation at `:39-54` (padding 1–18, `{SEQUENCE}`
mandatory, unknown tokens rejected). Immutability after insert is enforced by a second trigger,
`nexora_protect_commercial_identity` (`:279-306`), mirrored in C# at `Services/LeadPersistenceRules.cs:20-49`.

**Production configuration (queried 2026-08-06 02:21 UTC) — 5 tenant rows, all identical:**

```
BusinessUnitID | Prefix | Format                      | SequencePadding | FinancialYearStartMonth
1,2,4,5,6      | NXR    | {PREFIX}-{YEAR}-{SEQUENCE}  | 6               | 1
```

### 3.2 Concurrency safety — SAFE on PostgreSQL, UNSAFE on the SQLite lane

| Path | Mechanism | Verdict |
|---|---|---|
| PostgreSQL (production) | `nextval` — atomic, non-transactional, gap-tolerant | **SAFE.** No advisory lock, `FOR UPDATE` or retry loop is needed, and none is used. Correct by construction. |
| Non-PostgreSQL fallback (SQLite test lane, offline tooling) | `Services/LeadPersistenceRules.cs:56-58` — `context.CommercialCases…Select(c => (long?)c.AllocationNumber).Max() ?? 0` | **UNSAFE** — classic read-max-then-write race. Provider-gated to non-Npgsql only (`LeadPersistenceRules.cs:16-17`), so it cannot execute in production. |

Backstops: `UX_CommercialCases_AllocationNumber` (unique, `:118-122`) and
`UX_Leads_BU_CommercialCaseReference` (unique, `:199-200`). A race would surface as a loud unique
violation, never as a duplicate client-facing number.

**Production proof:** 51 leads, 51 distinct `CommercialCaseReference`, sequence `last_value = 47`,
`AllocationNumber` 1→47 contiguous. Zero collisions across the concurrent re-drive that was running
15 parallel-attempt jobs at the time of audit.

### 3.3 DEFECT D-03.1 — the sequence is global, not per-tenant and not per-year

`Models/ErpRfqAutomationContext.Tenancy.cs:42` declares **one database-wide sequence**, and
`UX_CommercialCases_AllocationNumber` (`Migrations/20260722033825_AddPermanentLeadReferences.cs:118-122`)
is unique on `AllocationNumber` **alone** — not scoped by `BusinessUnitId`.

Two consequences, neither currently visible because only BU 1 has leads:

1. **Cross-tenant number bleed.** Tenant B's lead consumes a number out of tenant A's visible run.
   Tenant A sees `NXR-2026-000012` then `NXR-2026-000019` and cannot account for the gap. In a
   multi-tenant pilot this is a client-facing credibility defect, and it leaks a coarse signal about
   another tenant's volume.
2. **The counter never resets on 1 January.** `{YEAR}` is a cosmetic label, not a scope.
   `NXR-2027-000848` will follow `NXR-2026-000847`. The requirement's `{sequence}` implies a
   per-year series; this is a continuous one wearing a year label.

The same architecture repeats in `Repositories/RfqRepository.cs:353-354`, where
`NXR-RFQ-{BusinessUnitId}-{yyyy}-{D8}` embeds both the BU **and** the year as cosmetic segments over
a single global `nexora_rfq_number_seq`. Worse, `Rfq.Rfqno` has **no unique index** at all (only the
non-unique `IX_Leads_RFQNo`, `Models/ErpRfqAutomationContext.cs:390`).

**Reference implementation already in the codebase.** This is a solved problem here — it just was not
applied to the RFQ path. `CommercialFinance/CommercialFinanceApplicationService.cs:1147-1184` holds a
correctly-scoped counter keyed on `(BusinessUnitId, DocumentType, FiscalYear)` with `FOR UPDATE`:

```csharp
SELECT * FROM "LegalDocumentCounters"
WHERE "BusinessUnitId" = {businessUnitId} AND "DocumentType" = {documentType} AND "FiscalYear" = {fiscalYear}
FOR UPDATE
```

`OrderToCash/CustomerAwardApplicationService.cs:864-896` adds `pg_advisory_xact_lock(73001, businessUnitId)`
on top. Fixing D-03.1 is **porting an existing in-repo pattern**, not inventing one.

### 3.4 Mapping to `RFQ-KSA-{YYYY}-{sequence}` — a CONFIG CHANGE, not a rebuild

Charter amendment A3 is **feasible and confirmed**. `RFQ-KSA` appears nowhere in code — the only
occurrence in the repository is the charter's own prose (`00-execution-charter.md:56`). Producing
`RFQ-KSA-2026-000001` requires **no product code change**:

```sql
UPDATE "LeadReferenceConfigurations"
SET "Prefix" = 'RFQ-KSA', "Format" = '{PREFIX}-{YEAR}-{SEQUENCE}', "SequencePadding" = 6
WHERE "BusinessUnitID" = 1;
```

The sanitiser (`LeadReferenceFormatter.cs:56-64`) permits `[A-Z0-9_-]`, so `RFQ-KSA` survives
uppercasing and stripping intact. Two caveats, both real:

- **There is no API, service or UI that writes `LeadReferenceConfigurations`.** Zero controller hits.
  Rows are created only by the migration backfill (`:154-158`) or auto-seeded with `NXR` defaults by
  the trigger (`:230-233`). Today the change requires a DBA statement against production. For a
  pilot that is acceptable if deliberate and recorded; it should not be mistaken for a supported
  feature.
- **The `{SEQUENCE}` it yields is still the global, never-resetting counter** (D-03.1). Switching the
  prefix without fixing the scope produces `RFQ-KSA-2026-000048` as the *first* number the client
  ever sees, because it inherits the existing run.

### 3.5 DEFECT D-03.2 — the agreement / contract reference does not exist

The second half of the requirement has **no implementation of any kind**. No `Agreement`,
`Contract`, `StandingRfq`, `FrameworkAgreement`, `BlanketOrder` or `MasterAgreement` entity, table,
migration, DTO, endpoint or generator exists anywhere in backend or frontend.

The nearest thing is a free-text label with no entity and no number behind it:
`Models/Lead.cs:32` `public string? DurationAgreement { get; set; }` (varchar 200,
`Models/ErpRfqAutomationContext.cs:1037`), mirrored on `Models/Rfq.cs:34`. It is only ever copied
through (`Repositories/LeadRepository.cs:254,404,574,697,827`), never parsed, validated or numbered.

**Production: `DurationAgreement` is populated on 1 of 51 leads.** There is no standing-agreement
concept to reference, so this is a build, not a mapping.

### 3.6 FR-RFQ-03 traceability

| Requirement ID | Requirement text | Status | Existing implementation (file:line) | Exact evidence | Missing behaviour | Defects | Pilot impact | Priority | Recommended action | Acceptance evidence required |
|---|---|---|---|---|---|---|---|---|---|---|
| **FR-RFQ-03.1** | Unique client-facing RFQ number | **VERIFIED** | `Migrations/20260722033825_AddPermanentLeadReferences.cs:209-277` (trigger); `Services/LeadReferenceFormatter.cs:12-37`; unique indexes `:118-122`, `:199-200` | 51 leads → 51 distinct references `NXR-2026-000001…000047`; sequence `last_value=47`, contiguous; **0 collisions** during a concurrent 15-attempt re-drive (2026-08-06 02:21 UTC) | — | — | None. Every lead carries a stable client-facing reference today. | — | Preserve. Do not add a parallel scheme (A3). | Already held: production query above |
| **FR-RFQ-03.2** | Format `RFQ-KSA-{YYYY}-{sequence}` | **PARTIAL** | `LeadReferenceConfigurations` table + `{PREFIX}-{YEAR}-{SEQUENCE}` tokeniser; 5 tenant rows all `NXR`/pad 6 | Live config query 2026-08-06 02:21 UTC; `RFQ-KSA` absent from all code (only `00-execution-charter.md:56`) | Literal `RFQ-KSA` prefix not set; **no write path** for the config table | — | Low — cosmetic to the client, and one `UPDATE` away | **P2** | Confirm with client whether the literal string is required. If yes, set the config row for the pilot tenant **and** record it in the runbook as a manual DB step. | Screenshot of a lead detail page showing `RFQ-KSA-2026-…`, plus the executed SQL and its `RETURNING` output |
| **FR-RFQ-03.3** | Sequence is per-tenant and per-year | **DEFECTIVE** | `Models/ErpRfqAutomationContext.Tenancy.cs:42` (global sequence); `Migrations/…:118-122` (`UX_CommercialCases_AllocationNumber` unique on `AllocationNumber` alone) | Code proof above; not yet observable in prod because only BU 1 has leads (`CommercialCases` 47/47 rows on BU 1) | Per-`(tenant, year)` scoping; a 1 January reset | **D-03.1** — cross-tenant number bleed + counter never resets | **Medium** for a single-tenant pilot; **High** the moment a second tenant is live. Also produces an odd first impression: the client's first number would be `…-000048`. | **P1** | Port the in-repo `LegalDocumentCounters` pattern (`CommercialFinanceApplicationService.cs:1147-1184`) — counter keyed `(BusinessUnitId, FiscalYear)` under `FOR UPDATE`; make the unique index `(BusinessUnitId, AllocationNumber, Year)`. Keep `nextval` semantics for gap tolerance. | PG-IT concurrency test: 2 tenants × 50 parallel inserts → two independent contiguous runs, both starting at 1; plus a year-rollover test |
| **FR-RFQ-03.4** | Concurrency-safe generation | **VERIFIED** (production path) | `Migrations/…:239` `nextval`; unique-index backstops | 51/51 distinct under concurrent load; sequence contiguous 1→47 | — | Non-production SQLite fallback `LeadPersistenceRules.cs:56-58` uses `MAX()+1` — provider-gated, cannot execute in prod | None in production | **P3** | Leave the fallback; add an XML-doc note that it is single-writer only, so nobody promotes it. | Existing production evidence suffices |
| **FR-RFQ-03.5** | Separate agreement / contract reference for standing RFQ agreements | **MISSING** | None. `Models/Lead.cs:32` `DurationAgreement` is free text with no entity or number | Repo-wide search: no `Agreement`/`Contract`/`StandingRfq`/`FrameworkAgreement`/`BlanketOrder` entity, table or generator. Production: `DurationAgreement` populated on **1 of 51** leads | The entire concept: entity, reference generator, link from RFQ to agreement, UI | **D-03.2** | **Only if the pilot demos standing agreements.** Tech Connect's pilot journey is single-inquiry intake — confirm before funding a build. | **P2, scope-gated** | **Do not build speculatively.** Ask the client whether standing agreements are in the pilot journey. If no: record as an explicit accepted limitation in `04-risk-and-blocker-register.md`. If yes: it is a genuine new entity + generator, sized in days not hours. | If in scope: an agreement created, given a distinct reference, and two RFQs demonstrably linked to it |

**FR-RFQ-03 overall: PARTIAL.** The hard part — a unique, immutable, concurrency-safe, per-tenant
configurable client-facing number that demonstrably works under real concurrent load — **is done and
proven in production.** What remains is one scoping defect (D-03.1), one optional config flip
(03.2), and one genuinely absent concept (D-03.2) that may well be out of pilot scope.

---

## FR-RFQ-07 — ROUTING

> **Requirement.** Route accepted RFQs to the right Business Unit / Sales Engineer / team or review
> queue, with **configurable** rules over customer, product category, manufacturer-brand, Saudi
> region, territory, sales expertise, and workload/capacity where platform support exists.
>
> *Section owner: SDET / Pilot Auditor.*

### 7.0 The headline: routing executes on every lead and assigns nobody

**Production, queried read-only 2026-08-06 02:22–02:26 UTC:**

```sql
SELECT "MatchStatus","Outcome","DecisionCode", count(*) FROM lead_routing_decisions GROUP BY 1,2,3;
```

| MatchStatus | Outcome | DecisionCode | n | Window |
|---|---|---|---|---|
| `NoEvidence` | `Unassigned` | `NO_MATCH_EVIDENCE` | **44** | 2026-07-23 → 2026-08-06 02:22 |
| `NoEvidence` | `AssignedPrimary` | `MIGRATED_ASSIGNMENT` | 7 | 2026-05-12 → 2026-05-17 |

**Every single lead the routing engine has ever auto-routed — 44 of 44 — landed in the unassigned
queue.** The 7 assigned rows are `MIGRATED_ASSIGNMENT` back-fill from May 2026, written by a data
migration, not by the engine. `unassigned_work_items` holds **41 Open** items with SLA clocks
running (4-hour SLA, `RoutingPolicy.cs:8`).

This is not a silent failure — and that distinction matters. The engine runs, reaches a decision,
records a decision code, writes an explanation, and parks the lead in a visible human queue with an
SLA. **Nothing is lost.** But routing, as a capability, does not currently route.

### 7.1 Why — three empty tables and a gate with no key

| Table | Rows (prod) | Consequence |
|---|---|---|
| `sales_rep_profiles` | **0** | `CommercialRoutingApplicationService.cs:770-775` marks every user unavailable: `profile == null ? "Governed Sales Rep profile is required"`. **No user can ever be assigned.** |
| `customer_ownerships` | **0** | Stage 2 of the engine has nothing to look up → `NO_EFFECTIVE_OWNERSHIP` |
| `customer_identifiers` | **3** (1 Email, 1 CustomerName, 1 ErpAccount, all for the single `Customers` row) | Stage 1 finds no candidates → `NO_MATCH_EVIDENCE`, which is where all 44 decisions stop |
| `Leads.CustomerID` populated | **0 / 51** | No lead is linked to a customer, so no ownership lookup is even reachable |

**DEFECT D-07.1 (Sev 2) — `sales_rep_profiles` is a hard blocking gate with no write path.**
Routing refuses any owner lacking an eligible profile, yet **no controller and no UI can create
one**. `ISalesApplicationService.UpsertProfileAsync`
(`CommercialIntelligence/Sales/SalesApplicationService.cs:24-70`) is exposed by no endpoint; the only
writers in the entire repository are test fixtures and
`Backend/ERP_RFQ_Automation.AcceptanceFixture/Program.cs:514-516`.
`Frontend/src/pages/SalesManagement/RepProfilePage.tsx` is read-only metrics and renders no
territory, capacity or eligibility field. **Routing is unreachable in production by construction,
and no amount of data entry through the product can fix it.**

### 7.2 Architecture — a two-stage lookup, not a rules engine

`CommercialRouting/DeterministicRoutingEngine.cs:7-84`:

- **Stage 1 (`:14-42`)** — resolve *which customer this is*, from `customer_identifiers` filtered by
  `policy.IdentifierPrecedence`, gated on `MatchThreshold` (0.85) and `AmbiguityMargin` (0.10).
- **Stage 2 (`:44-68`, `:86-112`)** — read that customer's **already-assigned owner** from
  `customer_ownerships`, then check availability and apply workload relief (`:48-58`).

**The engine never chooses a sales engineer from a pool.** It looks up a pre-existing
customer→owner mapping. The one component that *does* score reps on skills —
`CommercialIntelligence/Sales/WeightedEligibleRepScoringEngine.cs:44-51`, which weights territory 20,
product category 25, team 10, capacity ×0.20, workload ×0.15 — **is dead code**, reachable from no
controller or service, called only from `CoreSalesScoringTests.cs:14-24`.

This matters for expectation-setting: the requirement's language ("route to the right Sales
Engineer… over expertise, region, territory") describes a matching engine. What exists is an account
ownership directory plus a workload tie-breaker. Both are legitimate designs; they are not the same
design, and the pilot narrative must not imply the first.

### 7.3 Routing dimensions — implemented vs absent

| Dimension | Status | Evidence |
|---|---|---|
| **Customer / client organisation** | **IMPLEMENTED — the primary dimension** | `DeterministicRoutingEngine.cs:14-42`, `:86-102` |
| **Workload / capacity** | **IMPLEMENTED — the most developed dimension** | 8 weighted factors, `CommercialRoutingApplicationService.cs:754-777`; backup relief at `DeterministicRoutingEngine.cs:52-53` |
| **Business unit** | **IMPLEMENTED** — hard tenant boundary + `Branch` ownership scope | `DeterministicRoutingEngine.cs:15,93,116`; scope key at `CommercialRoutingApplicationService.cs:802` |
| **Product category** | **PARTIAL** — an ownership *scope filter*, never a skill match; reads **only the first line item** | `CommercialRoutingApplicationService.cs:804` `lead.LeadItems.Select(i => i.CommodityProduct).FirstOrDefault(…)` |
| **Territory** | **DECLARED BUT STRUCTURALLY UNREACHABLE** | Ranked at `RoutingPolicy.cs:44`, but `BuildScopeKeysAsync` (`:799-810`) only ever emits `Branch` and `ProductCategory`, and `ScopeMatches` (`DeterministicRoutingEngine.cs:104-112`) requires the key to be present. A `Territory` ownership row can never match an auto-routed lead. |
| **Key account team** | **DECLARED BUT STRUCTURALLY UNREACHABLE** | Same defect, `RoutingPolicy.cs:45` |
| **Sales expertise / specialisation** | **ABSENT from the live engine** | Only in dead `WeightedEligibleRepScoringEngine.cs:44-45` |
| **Manufacturer / brand** | **ABSENT** | Zero occurrences in `CommercialRouting/` |
| **Saudi region** | **ABSENT** | Zero occurrences of `region`/`saudi` in any routing file |
| **Value / amount thresholds** | **ABSENT** | Only *confidence* thresholds exist |
| **Language** | **ABSENT** | (Consistent with A1 descoping) |

**DEFECT D-07.2 (Sev 3) — Territory and KeyAccountTeam are advertised in the policy precedence list
but can never fire.** They are silently inert rather than loudly unsupported: an operator who
configured a Territory ownership row would see it ignored with no diagnostic.

### 7.4 Are the rules configurable by a user? — NO

**All thresholds, weights and precedence orders are C# initialisers** in
`CommercialRouting/RoutingPolicy.cs:5-48` — `MatchThreshold = 0.85m`, `AmbiguityMargin = 0.10m`,
`UnassignedSla = 4h`, `MaximumWorkloadPoints = 100`, 8 workload weights, plus the
`IdentifierPrecedence` and `OwnershipPrecedence` lists.

Registered as a fixed singleton with **no configuration binding** — `Program.cs:282`:

```csharp
builder.Services.AddSingleton(new RoutingPolicy());
```

`grep -i routing` over `appsettings*.json` returns nothing. **There is no rules table, no rules CRUD
endpoint and no rules UI.** Changing any threshold is a code change plus a redeploy.

What *is* user-editable is the **data** routing reads, not the rules:

| Editable | API | UI |
|---|---|---|
| Customer→owner mapping | `POST /api/commercial-routing/customer-ownerships`; `POST /api/commercial-intelligence/account-ownership/{customerId}/assign` | `Frontend/src/pages/SalesManagement/AccountOwnershipPage.tsx` (`/sales/accounts`) |
| Customer identifiers | `POST /api/commercial-routing/customer-identifiers` | **none** |
| **Sales rep profile** (territory, product category, capacity, eligibility) | **NONE — D-07.1** | **none** |

The requirement's "configurable rules" is therefore **MISSING**, and the requirement's own hedge
("where platform support exists") does not cover it — the platform supports a *rules-shaped* policy
object, it simply exposes no way to edit it.

### 7.5 Is the routing decision explained to the user? — richly captured, thinly shown

**Server side: excellent.** `LeadRoutingDecision.Explanation` is a `jsonb` column
(`CommercialRoutingModelBuilderExtensions.cs:75`) written at
`DeterministicRoutingEngine.cs:188-223`. A real production row (lead 51, 2026-08-06 02:22 UTC):

```json
{"outcome":"Unassigned","matchStatus":"NoEvidence",
 "requestHash":"26bcbc2e46cb5c1de0274ed74ed059d9c47478066d0217fdbc39133364e64545",
 "decisionCode":"NO_MATCH_EVIDENCE",
 "workloadPolicy":{"weights":{"openRfq":8,"followUp":10,"leadLine":1,"openQuote":6,"activeLead":10,
   "urgentDeadline":15,"overdueDeadline":20,"approachingDeadline":8,"maximumLinePointsPerJourney":25},
   "maximumPoints":100,"backupReliefThresholdPoints":20},
 "consideredOwners":[]}
```

Nine decision codes are emitted (`DeterministicRoutingEngine.cs:20,39,42,46,58,63-65` plus
`MANUAL_ASSIGNMENT` at `CommercialRoutingApplicationService.cs:488`).

**UI side: the `Explanation` blob is never fetched.** `grep -rn "commercial-routing" Frontend/src`
returns **zero hits** — the frontend never calls the endpoint that returns it. What a user sees is a
one-line summary from a different endpoint, `RoutingQueuePage.tsx:40`:

```tsx
<Typography variant="caption">{row.recommendationReason} | {(row.matchConfidence * 100).toFixed(0)}% match | policy {row.policyVersion}</Typography>
```

**DEFECT D-07.3 (Sev 3).** With `consideredOwners: []` and `matchConfidence: 0`, today's pilot user
sees a raw enum — `NO_MATCH_EVIDENCE` — and `0% match`. They are told neither *why* no evidence was
found, nor what to do about it. The evidence to render a genuine explanation is already in the
database; only the read path and the UI are missing. **This is the cheapest high-value fix in
FR-RFQ-07.**

### 7.6 Manual override with reason + audit — the strongest part

**Reason enforcement (server-side, not just UI validation):**
`Controllers/CommercialIntelligenceController.cs:360-361`:

```csharp
if (item.SuggestedUserId.HasValue && item.SuggestedUserId != request.OwnerUserId && (reason?.Length ?? 0) < 5)
    return BadRequest(new { error = "A routing override reason of at least 5 characters is required." });
```

Same at `:248-249` for account reassignment; `CommercialRoutingApplicationService.cs:69` refuses
silent re-ownership ("Lead already has an owner. Use an explicit reassignment command.").

**Audit:** `lead_assignments` is an append-only bitemporal ledger —
`CommercialRoutingApplicationService.cs:475-514` closes the prior row (`EffectiveTo = now`) and
writes a new one carrying `FromUserId`, `ToUserId`, `ReasonCode`, `Comment`, `AssignedByUserId`,
`CorrelationId`, `IdempotencyKey`. Migration `20260730234426_Module03TenantSafeSalesRouting.cs`
makes `lead_routing_decisions` append-only in PostgreSQL. Optimistic concurrency
(`EnsureQueueVersion`, `:938-942`), expected-assignee enforcement (`:464-465`), request-hash
idempotency (`:853-868`), and dual-party notification (`:884-915`) are all present.

**Two gaps:**

- **D-07.4 (Sev 3)** — a **second, reason-less** reassignment path exists:
  `Frontend/src/pages/Leads/AssignedLeadsPage.tsx:312-340` → `POST /api/UnAssignedLead/assign`
  (`Controllers/UnAssignedLeadController.cs:115-157`) passes `Comment` but **never requires it**. The
  governed path's 5-character rule is bypassable through the UI.
- **D-07.5 (Sev 4)** — `GET /api/commercial-intelligence/leads/{leadId}/assignment-history`
  (`CommercialIntelligenceController.cs:281-299`) has **no frontend consumer** — only
  `Frontend/e2e/core-commercial-sales-force.spec.ts:97`. The audit trail is written and queryable but
  invisible in the product; `LeadDetailPage.tsx:331` shows a single `assignmentReason` field.

**Production caveat:** all 7 `lead_assignments` rows are May-2026 `MIGRATED_ASSIGNMENT` back-fill
with empty `Comment` and null `AssignedByUserId`. **The override path has never been exercised by a
human in production.**

### 7.7 Invocation and recovery — correct

Four call sites of `RouteLeadAsync`: the extraction pipeline
(`Extraction/ExtractionWorker.cs:1200-1222`, invoked at `:1006`, `:1082`, `:1093`), human dedup
review (`LeadIdentity/LeadIdentityApplicationService.cs:756-761`), a 60-second reconciliation worker
(`CommercialRouting/RoutingReconciliationWorker.cs:145-148`) that re-routes any lead with no
decision, and a manual API (`Controllers/CommercialRoutingController.cs:18-24`, unused by the UI).

The reconciliation worker is a genuine anti-silent-loss control. One caveat worth flagging:
`ExtractionWorker.cs:1203` `if (_routing is null) return;` — routing is an **optional** constructor
dependency (`:823,833`), so a DI misregistration would silently disable routing for the whole
pipeline with no startup error. Failures are caught and logged (`:1215-1221`) rather than failing the
job, which is correct, but combined with the null-check it means "routing never ran" and "routing ran
and found nothing" are indistinguishable without reading the decisions table.

### 7.8 FR-RFQ-07 traceability

| Requirement ID | Requirement text | Status | Existing implementation (file:line) | Exact evidence | Missing behaviour | Defects | Pilot impact | Priority | Recommended action | Acceptance evidence required |
|---|---|---|---|---|---|---|---|---|---|---|
| **FR-RFQ-07.1** | Route accepted RFQs to a Sales Engineer | **DEFECTIVE** | `CommercialRouting/DeterministicRoutingEngine.cs:7-84`; `CommercialRoutingApplicationService.cs:475-514` | **44 of 44 auto-decisions = `NO_MATCH_EVIDENCE`/`Unassigned`** (2026-08-06 02:22 UTC). The 7 assigned rows are May-2026 `MIGRATED_ASSIGNMENT` back-fill. `sales_rep_profiles` = 0, `customer_ownerships` = 0, `Leads.CustomerID` = 0/51 | An executable path to an assigned owner | **D-07.1** — `sales_rep_profiles` gate has no write path | **HIGH — routing cannot be demonstrated.** Every pilot lead lands unassigned. | **P0** | Expose `UpsertProfileAsync` on a controller + minimal admin UI; seed the pilot tenant's reps, `customer_identifiers` and `customer_ownerships`; re-run routing via the reconciliation worker | T-14: a lead routes to a named owner with decision code `PRIMARY_OWNER_ASSIGNED`, shown in the browser against a real backend |
| **FR-RFQ-07.2** | Route to a **review queue** when no confident target | **VERIFIED** | `CommercialRoutingDomain.cs:162-185` `UnassignedWorkItem`; `Frontend/src/pages/SalesManagement/RoutingQueuePage.tsx` (`/sales/routing`) | **41 Open items** with `ReasonCode=NO_MATCH_EVIDENCE`, `EnteredOn` and `SlaDueOn` populated (4 h SLA); queue page exists and renders | — | — | **Positive.** The safety net works: nothing is silently lost, everything is visible with an SLA. | — | Preserve. Demo this explicitly as the "nothing disappears" proof. | Screenshot of `/sales/routing` against a real backend showing the queue with SLA clocks |
| **FR-RFQ-07.3** | Rules configurable over the named dimensions | **MISSING** | `CommercialRouting/RoutingPolicy.cs:5-48`; `Program.cs:282` `AddSingleton(new RoutingPolicy())` | No rules table, no CRUD endpoint, no UI; `grep -i routing` over `appsettings*.json` returns nothing | Any user-facing configurability | — | **Medium.** Demoable as "policy-governed" but not as "configurable". Do not claim configurability. | **P2** | Do not build a rules engine for the pilot. Bind `RoutingPolicy` to `appsettings` as a first step so thresholds change without a code edit; record "rules are not user-configurable" as an explicit limitation | Config-bound threshold changed and observed to alter a routing decision |
| **FR-RFQ-07.4** | Dimension: customer | **VERIFIED (code)** / **UNPROVEN (prod)** | `DeterministicRoutingEngine.cs:14-42` | Executes on every lead; 3 `customer_identifiers` rows for 1 `Customers` row; 0 leads matched | Populated identifier data | — | Medium | **P1** | Seed identifiers for pilot customers | A lead matching a customer at ≥0.85 confidence |
| **FR-RFQ-07.5** | Dimension: workload / capacity | **VERIFIED (code)** / **UNPROVEN (prod)** | `CommercialRoutingApplicationService.cs:754-777`; relief at `DeterministicRoutingEngine.cs:52-53` | 8-factor weighted model present in every production `Explanation` blob | Any owner to measure | — | Low | **P2** | Prove once profiles exist | `BACKUP_OWNER_ASSIGNED_FOR_WORKLOAD` observed with two live owners |
| **FR-RFQ-07.6** | Dimension: business unit | **VERIFIED** | `DeterministicRoutingEngine.cs:15,93,116`; `CommercialRoutingApplicationService.cs:802` | Every decision is BU-scoped; `Branch` scope key always computed | — | — | None | — | Preserve | Held |
| **FR-RFQ-07.7** | Dimension: product category | **PARTIAL** | `CommercialRoutingApplicationService.cs:804` | Scope filter only; uses `LeadItems[0].CommodityProduct` — **first line item only** | Multi-category documents; category as a *skill match* | Multi-line RFQ spanning categories routes on line 1 alone | Medium | **P2** | Document the first-line-item limitation; consider a distinct-category set | A multi-category RFQ routed with the rule that fired shown to the user |
| **FR-RFQ-07.8** | Dimension: territory | **DEFECTIVE** | Declared `RoutingPolicy.cs:44`; unreachable via `DeterministicRoutingEngine.cs:104-112` vs `CommercialRoutingApplicationService.cs:799-810` | `BuildScopeKeysAsync` emits only `Branch` + `ProductCategory`; `ScopeMatches` requires the key | Territory scope key computation | **D-07.2** — silently inert, no diagnostic | Medium — a configured Territory rule is ignored without warning | **P2** | Either compute a territory scope key, or remove `Territory`/`KeyAccountTeam` from `OwnershipPrecedence` so an unsupported rule fails loudly | Territory ownership row demonstrably fires, or is loudly rejected at write time |
| **FR-RFQ-07.9** | Dimension: sales expertise | **MISSING** (live) | Dead code: `CommercialIntelligence/Sales/WeightedEligibleRepScoringEngine.cs:44-51` | Reachable from no controller; called only by `CoreSalesScoringTests.cs:14-24` | Skill-based matching in the live path | — | Low for pilot — ownership-directory routing is a defensible design | **P3** | Do not wire the dead engine for the pilot. Either delete it or mark it `[Obsolete]` so it is not mistaken for live capability | n/a — descope decision recorded in `03-decision-log.md` |
| **FR-RFQ-07.10** | Dimension: manufacturer / brand | **MISSING** | None | Zero occurrences in `CommercialRouting/` | Entire dimension | — | Low for pilot | **P3** | Record as accepted limitation | n/a |
| **FR-RFQ-07.11** | Dimension: Saudi region | **MISSING** | None | Zero occurrences of `region`/`saudi` in routing | Entire dimension | — | **Medium — named explicitly in the requirement and likely to be asked about by a Saudi client** | **P2** | Record as an explicit, dated limitation with a roadmap position. Do not improvise it. | n/a |
| **FR-RFQ-07.12** | Routing decision explained to the user | **PARTIAL** | Captured: `DeterministicRoutingEngine.cs:188-223` (jsonb). Shown: `Frontend/src/pages/SalesManagement/RoutingQueuePage.tsx:40` | Real production blob quoted in §7.5; `grep -rn "commercial-routing" Frontend/src` = **0 hits** | UI never reads `Explanation`; user sees a raw enum + `0% match` | **D-07.3** | **HIGH for pilot credibility** — the queue currently explains nothing actionable | **P1** | Surface `Explanation` on the queue and lead detail: which identifier matched, which rule fired, which owners were considered and why each was rejected. Data already exists. | Browser screenshot of a human-readable explanation against a real backend |
| **FR-RFQ-07.13** | Manual override with reason | **VERIFIED (code)** / **UNPROVEN (prod)** | `CommercialIntelligenceController.cs:360-361`, `:248-249`; `CommercialRoutingApplicationService.cs:69` | Server-side ≥5-char enforcement present on the governed path | — | **D-07.4** — reason-less bypass via `AssignedLeadsPage.tsx:312-340` → `UnAssignedLeadController.cs:115-157` | Medium — an audit trail with blank reasons is not an audit trail | **P1** | Require a reason on `UnAssignedLeadController.assign` too, or route that page through the governed command | T-15: override attempted with a 3-char reason → rejected; with a valid reason → `lead_assignments` row written |
| **FR-RFQ-07.14** | Override is audited | **PARTIAL** | `CommercialRoutingApplicationService.cs:475-514`; append-only migration `20260730234426_Module03TenantSafeSalesRouting.cs` | 7 rows, **all** May-2026 `MIGRATED_ASSIGNMENT` with empty `Comment` and null `AssignedByUserId`. **No human override has ever occurred in production.** | Nothing in the write path; the **read** path has no UI | **D-07.5** — history endpoint has no frontend consumer | Medium — cannot show a client the audit trail in the product | **P2** | Add an assignment-history panel to `LeadDetailPage` consuming the existing endpoint | T-15 evidence plus a browser screenshot of the history panel |

**FR-RFQ-07 overall: DEFECTIVE.** The machinery is well engineered — append-only bitemporal audit,
idempotency, optimistic concurrency, enforced override reasons, an SLA-backed unassigned queue, a
60-second reconciliation safety net, and a rich decision explanation captured on every lead. **None
of it can assign an owner in production**, because the eligibility gate it depends on
(`sales_rep_profiles`) has zero rows and no product path to create one.

**The good news for the pilot:** this is a data-and-plumbing problem, not an architecture problem.
D-07.1 (write path for rep profiles) plus seeding, and D-07.3 (surface the explanation already
stored), together convert FR-RFQ-07 from undemonstrable to demonstrable. Neither is a redesign.

**The honest framing for the client:** what Nexora has is *account-ownership routing with workload
balancing and a governed unassigned queue* — not multi-dimensional expertise/region/brand matching.
Manufacturer-brand and Saudi-region routing do not exist and should be presented as roadmap, never
as present capability.
