# Lead / RFQ Ingestion — Repository Map

**Purpose.** The shared navigational map for the Client Pilot readiness program. Built once, reused
by every agent instead of re-scanning. Dense and navigational by design, not narrative.

**Roots**

| Symbol | Path |
|---|---|
| `⟨B⟩` | `Backend/ERP_RFQ_Automation/` |
| `⟨T⟩` | `Backend/ERP_RFQ_Automation.Tests/` |
| `⟨F⟩` | `Frontend/src/` |

Verified against the working tree and the live production service `srv-d9csjhe1a83c739phue0`
(deploy `dep-d9puheb7uimc7384ovk0`) on 2026-08-06.

> **Read §9 (DEAD CODE) before drawing any conclusion from a file you have just opened.** This
> codebase contains several orphaned parsers that read like live customer integrations and have
> misled previous reviews.

---

## 1. The one-line architecture

```
5 doors ──► IDocumentIngestion.IngestAsync ──► ExtractionJobs (PostgreSQL queue)
                     │                                   │
              immutable evidence                   ExtractionWorker (4 loops)
              + occurrence ledger                         │
                                              reader ─► chunker ─► LLM ─► LeadPersister
                                                                              │
                                                    identity/dedup ─► routing ─► Lead
```

**Every door funnels through exactly one gateway.** `⟨B⟩Extraction/DocumentIngestionService.cs:109
IngestAsync` is the single choke point; there is no second ingestion path in live code.

---

## 2. Ingestion entry points (the doors)

`ExtractionSourceType` — `⟨B⟩Extraction/ExtractionJob.cs:9-16` — enumerates `Email`, `ManualUpload`,
`ExcelTemplate`, `Folder`. This value is stamped on every job and is the authoritative per-door
production counter.

### 2.1 Email (IMAP poll)

| File : line | What | Status |
|---|---|---|
| `⟨B⟩Services/EmailBackgroundService.cs:22` | `class EmailBackgroundService : BackgroundService` — the poller | LIVE (`Program.cs:346`) |
| `⟨B⟩Services/EmailBackgroundService.cs:25` | `DefaultPollInterval = 300s`; overridden by `min(Email_Configurations.PollingInterval)` at `:161-184` | LIVE |
| `⟨B⟩Services/EmailBackgroundService.cs:42` | `ExecuteAsync` — infinite loop | LIVE |
| `⟨B⟩Services/EmailBackgroundService.cs:24,57` | advisory lock `nexora:email-poller` via `PostgresAdvisoryLease.TryAcquireAsync` — single-leader gate | LIVE |
| `⟨B⟩Services/EmailBackgroundService.cs:108` | `RunPollCycleAsync` — mail fetch `:114`, **then** folder sweep `:129-154` | LIVE |
| `⟨B⟩Services/EmailService.cs:89` | `FetchAndSaveLeadsAsync` — loads `IsActive AND Protocol='IMAP'` configs | LIVE |
| `⟨B⟩Services/EmailService.cs:126-133` | MailKit `ImapClient` connect / authenticate / `OpenAsync(ReadWrite)` | LIVE — **failing auth in prod** |
| `⟨B⟩Services/EmailService.cs:44,136,194` | `SEARCH_DAYS_BACK = 7`; query = `SentSince(d).And(NotSeen)` | LIVE — silent-loss vector |
| `⟨B⟩Services/EmailService.cs:200` | `ProcessSingleEmailAsync` — durable record before `\Seen` | LIVE |
| `⟨B⟩Services/EmailService.cs:210-215` | duplicate skip on `(From, To, Subject)` — **not** MessageId | LIVE — silent-loss vector |
| `⟨B⟩Services/EmailService.cs:231,247` | raw `.eml` to disk, then `EmailIngest` row committed | LIVE |
| `⟨B⟩Services/EmailService.cs:160` | `inbox.AddFlagsAsync(uid, MessageFlags.Seen)` — gated on `:148` | LIVE |
| `⟨B⟩Services/EmailService.cs:345-350` | `EnqueueEmailForExtractionAsync` → `EmailIngestEnqueuer` | LIVE |
| `⟨B⟩Ingestion/Triage/EmailBodyNormalizer.cs:97` | `Normalize` — quote boundary `:136`, thread depth `:172`, disclaimer strip `:228`, signature `:280` | LIVE |
| `⟨B⟩Ingestion/Triage/SenderPartyResolver.cs:50` | `ResolveAsync` — customer vs supplier; public-domain list `:25` | LIVE |
| `⟨B⟩Ingestion/Triage/DeterministicEmailTriage.cs:60` | `Evaluate` — IO-free rules; stop rules `:172,176,183,187` | LIVE |
| `⟨B⟩Ingestion/Triage/EmailTriageDecision.cs:30,77` | reason codes; document hints | LIVE |
| `⟨B⟩Ingestion/Triage/EmailIngestEnqueuer.cs:34` | `EnqueueAsync` — fans body + attachments into jobs; `MaxAttachmentBytes = 25 MB` `:32`; `:68` sets `BodyShape="prose"`; `IngestAsync` at `:143` (attachments) and `:177` (body) | LIVE |
| `⟨B⟩Ingestion/Triage/EmailTriageService.cs:48,81,200` | `IEmailTriageService`, `ListAsync`, `ReprocessAsync` (re-reads `.eml`, max 10 MB `:71`, re-enqueues `:234`) | LIVE (`Program.cs:482`) |
| `⟨B⟩Controllers/EmailController.cs:26` | `POST api/Email/fetch` — manual trigger; BU-claim check `:32-34` | LIVE |
| `⟨B⟩Controllers/EmailTriageController.cs:32,45` | `GET api/email-triage`; `POST api/email-triage/{id}/reprocess` (requires Reason + Idempotency-Key `:52-59`) | LIVE |

**No webhook, no push, no Graph/Exchange/EWS/POP3.** MailKit is the only mail library. SendGrid
appears **outbound only** (`⟨B⟩Notifications/Providers/SendGridEmailSender.cs:20`).
`⟨B⟩Controllers/SmtpController.cs:32` is outbound send, **not** an ingestion door.

### 2.2 Manual upload

| File : line | Route | Single / batch | Status |
|---|---|---|---|
| `⟨B⟩Controllers/ExtractionController.cs:52` | `POST api/Extraction/upload` | **BATCH** — `:57 List<IFormFile>`; caps `:37-39` (25 MB/file, 200 MB/batch, 50 files); `Idempotency-Key`; 202 Accepted; `:96 IngestAsync(ManualUpload)` | LIVE — **the canonical door** |
| `⟨B⟩Controllers/ManualUploadController.cs:57` | `POST api/ManualUpload/upload` | **BATCH** — `:59 List<IFormFile>` → `:86 IngestAsync(ManualUpload)` | LIVE |
| `⟨B⟩Controllers/ManualUploadController.cs:108` | `POST api/ManualUpload/upload-rfq-excel` | SINGLE — `:110 IFormFile` → `:142 IngestAsync(ExcelTemplate)` | LIVE, **0 production jobs ever** |
| `⟨B⟩Controllers/LeadUploaderController.cs:55` | `POST api/LeadUploader/upload-template` | SINGLE → `:74 IngestAsync(ManualUpload, priority:10)` | LIVE |
| `⟨B⟩Controllers/RfqUploaderController.cs:52` | `POST api/RfqUploader/upload-template` | SINGLE; validated `:87` | LIVE |
| `⟨B⟩Controllers/SupplierQuoteInboxController.cs:60` | `POST api/supplier-quote-inbox/documents` | SINGLE → `⟨B⟩SupplierQuotes/SupplierQuoteDocumentIntakeService.cs:74 IngestAsync` | LIVE — 5th door |
| `⟨B⟩Services/ManualUploadService.cs:428` | (service, not a route) | legacy service path, gated by `Ingestion:UseUnifiedQueue` (`:68,93`) | LIVE, flag defaults true |

All routes are `[Authorize]` + `[RequireModulePermission("Leads"/"RFQ Management", …)]`. Global
fallback policy `RequireAuthenticatedUser` at `Program.cs:336-344`, reinforced by
`app.MapControllers().RequireAuthorization()` at `Program.cs:745`. The only `[AllowAnonymous]`
surfaces in the tree are `⟨B⟩Controllers/AuthController.cs:12`,
`⟨B⟩Platform/Controllers/PlatformAuthController.cs:20` and the two health probes
(`Program.cs:751,755`).

### 2.3 Watched folder

| File : line | What | Status |
|---|---|---|
| `⟨B⟩Services/EmailBackgroundService.cs:131,138` | `DiscoverTenantFolderIds()` then per-BU `ProcessAllFoldersAsync`, inside a pushed tenant scope `:135` | LIVE but **never iterates** in prod |
| `⟨B⟩Services/FolderService.cs:149` | `ProcessAllFoldersAsync` — SEC `:165`, Aramco `:167` (`.docx` only), Shared `:169` | LIVE |
| `⟨B⟩Services/FolderService.cs:182` | `EnqueueFolderFilesAsync` — claim `:212`, symlink reject `:216`, ext filter `:224`, size cap `:243`, **`:251 IngestAsync(Folder)`**, archive `:280` | LIVE |
| `⟨B⟩Services/FolderService.cs:1332` | `DiscoverTenantFolderIds` — enumerates `{root}/Tenants/*`; **early-returns empty at `:1334-1335` if the directory is absent** | LIVE — this is why the door is inert |
| `⟨B⟩Services/FolderService.cs:1296` | `GetTenantFolderPath` → `{root}/Tenants/{buId}/Watched/{Shared,SEC,Aramco}`; aliases `CUSTOMER1`→SEC, `CUSTOMER2`→Aramco `:1300-1301` | LIVE |
| `⟨B⟩Services/FolderService.cs:1315,1321` | `SharedFolderExtensions`; `IsAllowedUploadExtension` (SEC=`.doc` only, Aramco=`.docx` only) | LIVE |
| `⟨B⟩Services/FolderService.cs:1491` | `RecoverStaleClaims` — returns files stranded > 5 min in `Processing/` | LIVE |
| `⟨B⟩Services/FolderService.cs:82` | `SaveFilesToSharedFolderAsync` — HTTP feeder; traversal guard `:99-119`, staging + atomic move `:126-141` | LIVE |
| `⟨B⟩Services/FolderIngestionRetryState.cs` | DB-backed retry ledger, table `FolderIngestionRetryStates` | LIVE, **0 rows** |
| `⟨B⟩Controllers/EmailController.cs:52,90` | `POST api/Email/upload-leads-folder` (multi-file, writes *into* the watched folder); `POST api/Email/process-all-folder-leads` (manual sweep) | LIVE |

**No SFTP. No SharePoint. No `FileSystemWatcher`.** Tree-wide grep returns zero implementation hits;
the only matches are EF scaffolding for `FolderIngestionRetryStates`. The "watcher" is a polling
sweep piggybacked on the email loop.

> **Naming trap.** `SEC` here is an internal ingestion label, **not** Saudi Electricity Company —
> see `⟨B⟩CustomerResolution/CustomerResolutionContracts.cs:50-52`. Elsewhere in the tree `SEC-07`,
> `SEC-H6` etc. are security-finding tags, unrelated to this folder.

### 2.4 Durable-capture ordering (per door)

| Door | Durable write | Queue write |
|---|---|---|
| Gateway (all doors) | `DocumentIngestionService.cs:144` bytes → immutable store; `:156` tx open; `:170` batch, `:193` document, `:209` occurrence; **`:231` COMMIT** | `:376 _queue.EnqueueAsync` → committed `:403` |
| Email | `.eml` at `EmailService.cs:231`; `EmailIngest` at `:247`; `\Seen` only after, at `:160` | via gateway |
| Folder | atomic claim `FolderService.cs:212`; archive only after ingest returns, `:280` | via gateway |
| HTTP | bytes in request memory until `IngestAsync` | via gateway |

Durable row precedes queue row on every door. Full analysis in `02-current-state-rtm.md` §1.4.

---

## 3. Extraction pipeline

### 3.1 Queue — PostgreSQL-backed, survives restart

`⟨B⟩Extraction/IExtractionQueue.cs` — `EnqueueOutcome:8-14`, `EnqueueExtractionRequest:21-48`
(`MaxAttempts` default 5 at `:47`), `IExtractionQueue:64` (`EnqueueAsync:71`, `ClaimAsync:80`,
`RenewLeaseAsync:83`, `SetStatusAsync:86`, `CompleteAsync:89`, `FailAsync:96`,
`FailPermanentlyAsync:99-104`).

`⟨B⟩Extraction/ExtractionQueue.cs` — **raw SQL over the EF `DbConnection`. Not a `Channel<T>`; there
is no in-memory buffer anywhere.**

| Line | What |
|---|---|
| `:68-136` | `ClaimSql` — CTEs `plan_caps:69`, `blocked_tenants:77`, **`exhausted:85-99` (auto-DeadLetter when `Attempts >= MaxAttempts`)**, `inflight:100`, `candidate:107` with `FOR UPDATE OF j SKIP LOCKED LIMIT 1` `:125-126`; order `Priority DESC, SchedulerTag ASC, CreatedOn ASC` `:124` |
| `:138-280` | `EnqueueAsync` — idempotency on `SourceDocumentOccurrenceId` else `ContentHash` `:163-165`; entitlement quota `:180-182`; WFQ weight `:197-199`; `SchedulerTag` `:239` |
| `:282-304` | `ClaimAsync` — explicit tx + `PrepareExecutionScopeAsync` (RLS `set_config`, `:423-440`) |
| `:306-319` | `RenewLeaseAsync` — fenced on `LeasedBy` + `Attempts` |
| `:321-351` | `SetStatusAsync` (`Leased→Extracting`, `Extracting→Persisting`), `CompleteAsync` (only from `Persisting`) |
| `:353-372` | `FailAsync` — `DeadLetter` when attempts exhausted, else `Pending`; backoff `LEAST(POWER(2,Attempts),3600)s` |
| `:374-391` | `FailPermanentlyAsync` — unconditional `DeadLetter` |

Crash recovery: a dead worker's row keeps `Status IN ('Leased','Extracting','Persisting')` with an
expired `LeaseExpiresAt` and is reclaimed by the `candidate` CTE at `:115-116`.

### 3.2 Worker

`⟨B⟩Extraction/ExtractionWorker.cs` (74 KB, four types in one file).

| Line | What |
|---|---|
| `:25-41` | `ExtractionWorkerOptions` (config `Extraction:*`; `Program.cs:468-475` → WorkerCount 4, MaxConcurrentLlmCalls 8, PerTenantConcurrencyCap 4, LeaseDuration 5 min, IdlePollDelay 2 s) |
| `:49-52` / `:59-72` | `IExtractionDocumentReader` / `ILeadPersister` |
| `:82` | `class ExtractionWorker : BackgroundService` |
| `:103` | `_llmGate = SemaphoreSlim(MaxConcurrentLlmCalls)` |
| **`:106-121`** | `ExecuteAsync` — spawns `WorkerCount` loops; workerId `{MachineName}:{runId8}:{i}` |
| `:123-146` | `RunLoopAsync` — 2 s idle poll `:131-132`; catch-all `:138-144` so a loop never dies |
| **`:149-384`** | `ProcessOnceAsync` — claim `:152-157`, tenant push `:161`, lease heartbeat `:178-179`, status `:188`, read `:215` |
| **`:221-259`** | **3-way dispatch** — structured `:222` (no LLM gate) · prose body `:228-246` (gated) · unstructured `:247-259` (gated) |
| `:291-292` | `PersistAndCompleteAsync` |
| `:315-334` | `DocumentParsingException` → `FailPermanentlyAsync` (immediate dead-letter) |
| `:335-365` | `EvidenceIntegrityException` → `LogCritical` + `source.Fail()` + `corpus.Fail()` + retryable fail |
| `:386-474` | `MaintainLeaseAsync` — renews at `LeaseDuration/3`; `MarkLost()` + cancel on failure |
| `:493-517` | `IsNonLeadCommercialDocumentAsync` — fails closed on `JsonException` `:515` |
| `:521-522` | `IsProseBody` — `metadata.BodyShape == "prose"` |
| `:818,1064-1097,1099-1134` | `LeadPersister`; `PersistAndCompleteAsync`; core (renew → persist → complete → commit) |
| `:1137,1175,1200` | duplicate detect / customer resolve / route |
| **`:670-816`** | `DefaultExtractionDocumentReader` — **DEAD CODE, see §9** |
| `:476-491, :596-616, :618-640` | `MarkIntakeProcessing/Failure/Finalized` — **no-ops on Npgsql** (`:483`, `:605`, `:627`) |

### 3.3 Chunking + LLM

`⟨B⟩Extraction/ChunkedExtractionService.cs`

| Line | What |
|---|---|
| `:151-168` | `IChunkedExtractionService` — `ExtractAsync:157` (dead in prod), `ExtractUnstructuredAsync:160`, `ExtractStructuredAsync:167` |
| `:215-218` | `MaxItemsPerChunk = 200`, `MaxChunkChars = 24_000`, `HeaderContextBudget = 6_000`, `MinAcceptableConfidence = 0.60` |
| `:270-297` | external-provider allow-list gate (`IAiExternalProviderTrust`; **absent gate = deny** `:273-275`) |
| **`:312-318`** | LLM call #1 — whole-document pass; key `extraction:{src}:a{n}:whole` |
| **`:415-421`** | LLM call #2 — per-chunk MAP call; key `extraction:{src}:a{n}:chunk:{i}:{n}` |
| `:427-444` | `AiPolicyDeniedException` handled distinctly from model failure |
| `:540-561` | REDUCE + `MultiInquirySplitter` |
| `:580-649` | `ExtractStructuredAsync` — **deterministic, zero LLM calls** |
| `:657-682` | `ItemsPerChunk` (bounded by `ExtractionOutputBudget`), `BuildChunks` (greedy: item cap OR 24 000 chars) |

`⟨B⟩Extraction/Conversational/ConversationalExtractionService.cs` — `:46` class, `:54
MaxProseChars = 24_000`, `:90-110` provider gate, **`:118-122` LLM call**, `:155
ProseAnchorVerifier.Verify`, `:250` a fruitless email becomes `NeedsReview`, never `Failed`.
Reached via `EmailIngestEnqueuer.cs:68` → `ExtractionWorker.cs:521-522` → `:239`. LIVE.

### 3.4 Document reader

`⟨B⟩Extraction/ProductionDocumentReader.cs` — registered at `Program.cs:485`.

| Line | Format | Library |
|---|---|---|
| `:109-126` | xlsx / xlsm / xls / csv | `NativeSpreadsheetParser`, EPPlus |
| `:131` → `:355` | pdf | PdfPig; fallback Docnet rasterise + Tesseract OCR `:389-444`, **`MaxOcrPages = 10`** |
| `:134` → `:205` | doc | `WordBinaryTextExtractor` (OLE) → OpenXML fallback |
| `:135` → `:226` | docx | `DocumentFormat.OpenXml` streaming `OpenXmlReader` `:255` |
| `:136` → `:468` | tiff | Tesseract, every frame |
| `:137` → `:446` | jpg/jpeg/png/bmp/gif/webp | Tesseract |
| `:138` | default | raw UTF-8 (this is the `.txt` path) |
| — | **msg / eml / html** | **NO BRANCH EXISTS** — and blocked upstream by the allow-list |

`:56 OcrLock` serialises pdfium + Tesseract globally. `:90-91 OpenVerifiedReadAsync` is the
hash-verified read; `:103`/`:700` throw `EvidenceIntegrityException`. `:567-619` unrecognised
spreadsheet layout falls back to the unstructured LLM path rather than failing.

Allow-list: `⟨B⟩Security/DocumentInspection/DocumentIntakeAllowList.cs:30-34` = `.pdf .doc .docx .xls
.xlsx .xlsm .csv .txt .png .jpg .jpeg .gif .bmp .tif .tiff .webp` — **no msg / eml / html / pptx**.

### 3.5 Job entity, dead-letter, recovery

- `⟨B⟩Extraction/ExtractionJob.cs:26-36` `ExtractionStatus { Pending, Leased, Extracting, Persisting, Succeeded, Failed, DeadLetter, Duplicate }` — persisted as **text**; **`Failed` is never written by any SQL statement** (see §9). `:48-110` the entity; `:120-133` `TenantQueueState` (WFQ).
- Table mapping: `⟨B⟩Models/ErpRfqAutomationContext.Tenancy.cs:446-467` (`ToTable("ExtractionJobs")` at `:448`; indexes `:460-466`); `:468-470` `TenantQueueStates`; RLS + alt key + FKs at `:266,290-293,312,324`.
- `⟨B⟩Extraction/ExtractionJobMetadata.cs` — **sidecar JSON, not a table**. `:31 SourceOccurrenceId`, `:38 EmailIngestId`, `:82 BodyShape`, `:87 TriageOutcome`, `:95 ThreadContinuation`, `:104-105 SidecarPath = "{storagePath}.bu{buId}.ingest.json"`.
- `⟨B⟩Extraction/ExtractionDeadLetterService.cs:37` — `:45 ListAsync` (top 200), `:89 RecoverAsync` (idempotent replay `:100`, blocks on malware / integrity `:109-117`). **Scoped (`Program.cs:158`), NOT hosted** — operator-triggered via `⟨B⟩Controllers/OperationsReadinessController.cs:116-134`.
- `⟨B⟩Extraction/SecurityScanRecoveryService.cs:63` — `:133 RetryAsync` re-reads immutable evidence and replays at `:235`. **Scoped (`Program.cs:491`), NOT hosted** — driven by `⟨B⟩Controllers/LeadIngestionController.cs:41,54,71`.
- `⟨B⟩Extraction/SecurityHoldRecovery.cs:15` — `RecoverableErrorCodes:18-22`, `IsRecoverableSecurityHold:24-73`. LIVE, 3 callers.
- `⟨B⟩Extraction/MultiInquirySplitter.cs:19` — **LIVE, not orphaned**; called from `ChunkedExtractionService.cs:347,558,613`.
- `⟨B⟩Extraction/ExtractionOutputBudget.cs:80`, `⟨B⟩Extraction/ProcessingEvidenceQuery.cs:10`, `⟨B⟩Extraction/ProcessingEvidenceContracts.cs` — LIVE.
- `⟨B⟩Extraction/Quantities/QuantityParser.cs:77` — LIVE but **outside** the extraction pipeline; sole caller `⟨B⟩Services/LeadUploaderService.cs:154`.

### 3.6 LLM / AI services

| File : line | Class | Provider | Status |
|---|---|---|---|
| `⟨B⟩Services/OllamaLlmService.cs:15` | `OllamaLlmService : ILLMService` | Ollama HTTP `api/chat` `:328`; model default `qwen2.5:14b` `:54` | LIVE — **the only `ILLMService`** (`Program.cs:358`) |
| **`⟨B⟩AI/AiGovernanceService.cs:121`** | `AiGovernanceService : IAiGovernanceService` (iface `:98`) | token ledger, reservations, ceilings | LIVE (`Program.cs:359`). `AiPurposes:10`, `AiPromptVersions:26`, `AiCallContext:61`, `AiReservation:76`, `AiPolicyDeniedException:109`, `ReserveAsync:171`, `RecordAttemptAsync:352`, `CompleteAsync:385` |
| `⟨B⟩AI/AiProviderEndpoint.cs:77,232` | endpoint classifier (loopback → Local, else External `:151-156`) | — | LIVE (`Program.cs:364`) |
| `⟨B⟩AI/AiExternalProviderTrustService.cs` | per-tenant external allow-list | — | LIVE (`Program.cs:368`) |
| `⟨B⟩AI/AiReservationReconciliationWorker.cs` | reconciler | — | LIVE hosted (`Program.cs:370`) |
| `⟨B⟩Agent/Llm/AnthropicAgentLlm.cs:17` | `AnthropicAgentLlm : IAgentLlm` | Anthropic Messages API `:19` | LIVE **only** when `Agent:Anthropic:ApiKey` is set; else `MockAgentLlm` |
| `⟨B⟩Boq/IVisionDocumentReader.cs:48` | `NotConfiguredVisionReader` | none | permanent no-op stub |

**No OpenAI, Gemini, Bedrock, Mistral or Cohere client exists anywhere in the tree.**
Production env: `Ollama__BaseUrl`, `Ollama__Model`, `Ollama__ApiKey`, `Agent__Anthropic__ApiKey`.

---

## 4. Identity / dedup layer

| File : line | What |
|---|---|
| `⟨B⟩LeadIdentity/LeadIdentityApplicationService.cs` | `:200-241 CreateRevisionAsync` — fingerprint match `:203-208`, exact → duplicate `:210-219`, new occurrence `:221-222`, revision `:223-233`, pointer move `:235-237`, audit `LEAD_REVISION_CREATED` `:238`; `:393` security-hold check |
| `⟨B⟩LeadIdentity/LeadIdentityEntities.cs:87-97, :99-120` | `LeadOccurrenceDocument`; `LeadRevision` (**append-only**) |
| `⟨B⟩LeadIdentity/LeadIdentityContracts.cs`, `LeadIdentityModelBuilderExtensions.cs`, `LeadIngestionAudit.cs` | contracts, mapping, audit |
| `⟨B⟩Deduplication/LeadDuplicateDetector.cs` | duplicate detection, called from `ExtractionWorker.cs:1137` |
| `⟨B⟩CustomerResolution/` | customer/party resolution, called from `ExtractionWorker.cs:1175` |
| `⟨B⟩Extraction/DocumentIngestionService.cs:184-195` | **content-hash identity** — different bytes ⇒ new `SourceDocument`; identical bytes ⇒ reuse + new occurrence `:197-211` + `:217-228 MarkExactDuplicateCandidate` |
| `⟨B⟩Extraction/DocumentIngestionService.cs:619-644` | `SourceOccurrenceIdentity.BuildKey` / `BuildFallbackBatchId` |

**Tables:** `Leads`, `LeadRevisions`, `LeadRevisionDifferences`, `LeadRevisionImpacts`,
`LeadMatchCandidates`, `lead_customer_match_candidates`, `LeadIdentityAuditEvents`,
`LeadIngestionOccurrences`, `LeadIngestionBatches`, `LeadOccurrenceDocuments`.

---

## 5. Evidence ledger and storage

### 5.1 Storage abstraction

`⟨B⟩Infrastructure/Storage/IEvidenceObjectStorage.cs`

| Line | What |
|---|---|
| `:9-15` | `EvidenceObject(StorageUri, Bucket, Key, Version, ETag, ByteSize)` |
| `:29-86` | `IEvidenceObjectStorage` — `IsDurable`, `ProbeAsync`, `WriteImmutableAsync`, `OpenVerifiedReadAsync`, `TryDeletePurgedObjectAsync:71-72`, `TryMeasureObjectAsync:85` |
| `:88-221` | `LocalEvidenceObjectStorage` — **`IsDurable => false` `:94`**; `ProbeAsync` no-op `:96` |
| **`:176-180`** | **`BuildKey` = `Evidence/tenants/{buId}/{zone}/sha256/{hash[..2]}/{hash}{ext}`** — tenant-scoped, content-addressed |
| `:182-190` | `ValidateIdentity` — BU > 0, zone ∈ {quarantine, cleared}, lowercase 64-hex |
| `:202-210` | `VerifyAsync` — length + SHA-256 + `CryptographicOperations.FixedTimeEquals` |
| `:212-220` | `CopyAndVerifyAsync` — **buffers whole object into memory** |
| `:223-233` | `S3EvidenceStorageOptions`, section `EvidenceStorage`, `Provider` default `Local` |
| `:235-524` | `S3EvidenceObjectStorage` — `IsDurable => true :261`; conditional PUT `IfNoneMatch="*" :325`; `ValidateExisting:450-458` (trusts caller metadata, does not re-hash); versioning check `:475-494` (**silently skipped on 405/501 at `:485-491`**) |
| `⟨B⟩Infrastructure/Storage/IFileStorage.cs:220-263` | local immutable write — refuses overwrite `:228-232`, re-hashes existing bytes `:248-263`, atomic `File.Move(overwrite:false)` `:234-243` |

Selection: `Program.cs:117-127`. **No presigned URLs anywhere** (zero grep hits for
`Presign`/`GetPreSignedUrl`). **No SSE/KMS, no S3 object-lock, no bucket policy** configured.

### 5.2 Ledger entities

`⟨B⟩DocumentIntelligence/Persistence/EvidenceLedgerEntities.cs`

- `SourceDocument :222-462` — factory `Create:283-287`; `BindExtractionJob:289-296` (one-way); `RecordInspection:305-309`; `RecordMalwareVerdict:311-324` (**no state guard**); `MarkSecurityStatus:326-341` (transition matrix `:328-336`, `Rejected` terminal); `RejectForSecurityFinding:343-370`; **`ReleaseFromQuarantine:372-385`** (the only path that reassigns `ObjectBucket`/`ObjectKey`/`ObjectVersion`, `:381-383`, permitted only from `Pending`/`Quarantined`); `RequestPurge:394-403`; `ConfirmPurged:411-421`; `BytesAvailable:426` (**zero callers**); `StartExtraction/…/Fail :428-453`.
- `SourceDocumentOccurrence :464-726` — ~20 state mutators; `MarkTerminalSecurityFailure:701-715` sets `IntakeStatus` directly at `:710`, **bypassing the `Transition` guard** its siblings use.
- DB-level immutability (`Migrations/20260730193414_SynchronizeSharedExtractionOccurrences.cs`): `:124-131` `nexora_protect_source_document_identity` freezes `business_unit_id`, `corpus_id`, `content_hash`, `original_file_name`, `byte_size`, `created_on`; `:133-138` freezes object pointers once `Cleared`; `:147-159` freezes `source_metadata`. `trg_source_documents_no_delete` refuses DELETE (`EvidenceLedgerEntities.cs:50-53`).

**Tables:** `source_documents`, `source_document_occurrences`, `document_corpora`, `document_pages`,
`document_regions`, `field_evidence`, `supplier_quote_field_evidence`, `extraction_runs`,
`extraction_dead_letter_events`, `evidence_retention_policies`.

### 5.3 Inspection and scanning

`⟨B⟩Security/DocumentInspection/`

- `DocumentInspectionContracts.cs:120-125 IMalwareScanner`; `:17-21 MalwareVerdictPolicyOptions.MaximumCleanVerdictAge` default 24 h.
- `MalwareScanners.cs:8-59` **`EicarMalwareScanner`** — `EngineName = "Nexora.EICAR"` `:10`; matches one hard-coded EICAR string `:13-14`; returns `Clean` for everything else `:33`. **A stub, not an AV.**
- `MalwareScanners.cs:71-218` `ClamAvInstreamMalwareScanner` — real clamd INSTREAM; defaults `127.0.0.1:3310` `:63-64`.
- `MalwareScannerProvisioning.cs:297-314` `UnconfiguredMalwareScanner` — always `Unavailable` (fail-closed).
- `MalwareScannerProvisioning.cs:121-187 Select` — config `DocumentInspection:Scanner:Provider`; unset → BuiltIn in Development `:132-139`, else **ClamAV** `:140-149`; `:245-259` emits the `REDUCED SECURITY POSTURE` warning.
- `DocumentFileInspectionService.cs` (33 KB) — structural / type / archive / macro checks.
- `DocumentIntakeAllowList.cs:30-34` — the extension allow-list.

### 5.4 Retention

`⟨B⟩Retention/` — `EvidenceRetentionEntities.cs:26-69` (default 90 d, min 30, max 3650, **`IsEnabled`
default false** `:41,:61`, `PolicyCode :55`); `EvidenceRetentionEligibility.cs:17-114`
(statutory exclusions `:26-33`, terminal states `:41-52`, skip reasons `:69-88`, `ZoneKeysFor:98-113`);
`EvidenceRetentionService.cs` (42 KB — `GetAsync:59`, `MeasureAsync:81`, `UpdatePolicyAsync:123`,
**`RunPurgeAsync:196-258`** refusing to act unless enabled `:207-210`, `PurgeOneAsync:262`,
`DeleteBytesAsync:295`, `EligibleAsync:511`); `LegacyAttachmentPurgeResolver.cs:39`
(`TombstonePrefix = "purged:"` `:41`).

**No hosted sweeper.** Reachable only via `⟨B⟩Controllers/PlatformGovernanceController.cs:154-157`
(GET), `:165-169` (PUT policy), `:186-191` (POST purge-run, `dryRun` default true).

### 5.5 Reading evidence back

- `⟨B⟩Controllers/FileController.cs:53-58` `GET api/File/DownloadFile` — **retired, unconditional `410 Gone`**.
- `⟨B⟩Controllers/FileController.cs:60-152` `GET api/File/attachment/{id}` — the **only** endpoint serving source-document bytes. `[RequireModulePermission("Leads", View)]` `:61`; tenant-filtered at `:64-66,80-84,88,97,103`; hash-verified read `:125-126`; size cross-check `:118-123`. **No `SecurityStatus` gate, no `PurgeState` gate, no audit write.** Revision resolution at `:86-99` uses `FirstOrDefault()` + filename match.
- `⟨B⟩Controllers/ProcessingEvidenceController.cs:26,40,54,68` over `⟨B⟩Extraction/ProcessingEvidenceQuery.cs:10`.

---

## 6. Review path, ops and routing

### 6.1 Review / decision

- `⟨B⟩Controllers/LeadDecisionController.cs`, `⟨B⟩Controllers/LeadController.cs`, `⟨B⟩Controllers/UnAssignedLeadController.cs`.
- `⟨B⟩Controllers/LeadIngestionController.cs` (route `api/LeadIngestion`, `[Authorize]` `:13`) — `:25 GET batches/{batchId}`, `:34 POST batches/{batchId}/retry-blocked-files`, `:49 GET blocked-files`, `:66 POST retry-blocked-files`, `:78 GET match-reviews`, `:86 GET duplicates`, `:94 GET leads/{leadId}/revisions`, `:102 POST match-reviews/{occurrenceId}/decision`, `:125 GET analytics`.
- `⟨B⟩Controllers/OperationsReadinessController.cs:116-134` — dead-letter list + recover.
- **Table `LeadReviewAudits` has 0 rows in production** — no human has ever completed a review.

### 6.2 Routing

`⟨B⟩CommercialRouting/` — `DeterministicRoutingEngine.cs`, `RoutingPolicy.cs`,
`CommercialRoutingApplicationService.cs`, `RoutingRequestFingerprint.cs`, `RoutingValueNormalizer.cs`,
`RoutingReconciliationWorker.cs` (hosted, `Program.cs:292`), `CustomerIdentityMaintenance.cs`.
Controller `⟨B⟩Controllers/CommercialRoutingController.cs`. Invoked from `ExtractionWorker.cs:1200`.
Tables: `lead_routing_decisions`, `lead_assignments`, `sales_rep_profiles`.

### 6.3 Hosted services (complete list)

| Line | Service |
|---|---|
| `Program.cs:151` | `MalwareScannerStartupProbe` |
| `Program.cs:229` | `ProcurementDispatchWorker` |
| `Program.cs:237` | `QuoteDeliveryWorker` |
| `Program.cs:292` | `RoutingReconciliationWorker` |
| **`Program.cs:346`** | **`EmailBackgroundService`** — IMAP poll + folder sweep |
| `Program.cs:370` | `AiReservationReconciliationWorker` |
| **`Program.cs:486`** | **`ExtractionWorker`** |
| `Program.cs:550` | `Sla.SlaSweepWorker` |
| `⟨B⟩CommercialFinance/FinanceOutboxDispatcherServiceCollectionExtensions.cs:50` | `FinanceOutboxDispatcherService` — the **only** flag-gated worker, disabled in `appsettings.json` |

`Program.cs:93-96` — `HostOptions.BackgroundServiceExceptionBehavior = Ignore`: a faulting worker
does **not** kill the host and does **not** restart itself.

**Health:** `Program.cs:177-187` registers `database` (live+ready), `evidence-storage`,
`storage-capacity`, `malware-scanner`, `extraction-worker`, `quote-delivery-worker`,
`procurement-dispatch-worker`, `BackgroundWorkerHealthCheck` — all `ready`-tagged except `database`.
Endpoints `Program.cs:748` `/health` (live) and `:752` `/ready`. **Render probes `/health` only.**
Heartbeat ledger: `⟨B⟩HealthChecks/BackgroundWorkerHeartbeats.cs:12-18`
(`sla-sweep`, `routing-reconciliation`, `email-poller`, `ai-reservation-reconciliation`).

### 6.4 Feature / config gates touching ingestion

| Key | Effect | Default |
|---|---|---|
| `Ingestion:UseUnifiedQueue` | queue vs legacy inline extraction — `EmailService.cs:82,300,320`, `ManualUploadService.cs:68,93` | `true` |
| `EvidenceStorage:Provider` | S3 vs local evidence store — `Program.cs:121-127` | `Local` (**and explicitly `Local` in production**) |
| `DocumentInspection:Scanner:Provider` | scanner selection — `MalwareScannerProvisioning.cs:121-187` | unset → ClamAV in Production (**explicitly `BuiltIn` in production env**) |
| `Extraction:WorkerCount` / `MaxConcurrentLlmCalls` / `PerTenantConcurrencyCap` | worker tuning — `Program.cs:468-474` | 4 / 8 / 4 |
| `Database:ApplyMigrationsOnStartup` | `Program.cs:606-607` | ON in Production |

**Production environment (24 keys on `srv-d9csjhe1a83c739phue0`):** `ASPNETCORE_ENVIRONMENT=Production`,
`EvidenceStorage__Provider=Local`, `DocumentInspection__Scanner__Provider=BuiltIn`,
`Storage__RootPath=/var/data/nexora/uploads`, `Storage__EnforcePersistentMount=true`,
`Storage__RequiredMountPath=/var/data`, `Database__AllowManagedOwnerRoleMigrationCompatibility=true`,
`Notifications__Provider`, `Ollama__*`, `Agent__Anthropic__ApiKey`, `Jwt__*`, `Cors__AllowedOrigins__0`,
`ConnectionStrings__{Default,Migration}Connection`, `PORT`.

---

## 7. Frontend ingestion / review pages

Router `⟨F⟩App.tsx` (`<Routes>` at 158-321); nav `⟨F⟩components/layout/Sidebar.tsx` (88-278).

| Component | Route(s) | In sidebar? |
|---|---|---|
| `⟨F⟩pages/Leads/ManualUploadLeadsPage.tsx` | `/procurement/leads/manual-upload` (App.tsx:296) **and** `/procurement/leads/intelligence` (`:293`) — same component mounted twice | Yes ×2 (Sidebar `:163`, `:160`) |
| `⟨F⟩pages/Leads/LeadIngestionBatchPage.tsx` | `/procurement/leads/ingestion/:batchId` (`:297`) | via `activePrefixes` |
| `⟨F⟩pages/Leads/DuplicateUploadsPage.tsx` | `/procurement/leads/duplicates` (`:299`) | Yes (`:165`) |
| `⟨F⟩pages/Leads/PossibleMatchesPage.tsx` | `/procurement/leads/possible-matches` (`:298`) | Yes (`:167`) |
| `⟨F⟩pages/Leads/InboundMailTriagePage.tsx` | `/procurement/leads/inbound-mail` (`:300`) | Yes (`:164`) |
| `⟨F⟩pages/ExtractionReview/ExtractionReviewPage.tsx` | `/procurement/extraction/review` (`:288`) | Yes (`:162` "Needs Review") |
| `⟨F⟩pages/ExtractionReview/ExtractionReviewDetailPage.tsx` (1308 lines) | `/procurement/extraction/review/:id` (`:289`) | via prefix |
| `⟨F⟩pages/Leads/LeadsPage.tsx` | `/procurement/leads/all` (`:292`) | Yes (`:161`); "Revisions" reuses it with `?view=revisions` (`:166`) |
| `⟨F⟩pages/Leads/LeadDetailPage.tsx` | `/procurement/leads/view/:id` (`:303`), `/leads/view/:id` (`:313`) | via prefix |

**Batch upload is real in the UI** — `⟨F⟩pages/Leads/ManualUploadLeadsPage.tsx`: `:218 <input type="file" multiple …>`, `:53 useState<File[]>`, `:146 files.forEach(f => fd.append('files', f))`,
`:196-200` hand-rolled `onDragOver`/`onDrop` accepting `Array.from(event.dataTransfer.files)`,
caps `:25-27` (25 MB / 200 MB / 50 files), success navigates to `/procurement/leads/ingestion/{batchId}` `:98`.
Endpoint: `POST /api/Extraction/upload` via `⟨F⟩api/services/leadService.ts:621 uploadGoverned` with an
`Idempotency-Key` header. **`react-dropzone` is not used anywhere.**

Evidence surfaces: `⟨F⟩pages/ExtractionReview/ExtractionReviewDetailPage.tsx:851-900` ("Source
Evidence" panel; download at `:886` via `openAuthenticatedFile('/api/File/attachment/{id}')`),
`⟨F⟩pages/ExtractionReview/FieldEvidencePopover.tsx`, `⟨F⟩pages/Leads/LeadDetailPage.tsx:96-100,370-408`,
`⟨F⟩components/common/CommercialProcessingEvidence.tsx`, `⟨F⟩utils/authenticatedFile.ts`.

Services: `⟨F⟩api/services/leadService.ts`, `extractionReviewService.ts` (`:285-320` processing-evidence
calls), `decisionService.ts`, `emailTriageService.ts` (`:292` list, `:307` reprocess).

**Confidence is deliberately not rendered** — `⟨F⟩pages/ExtractionReview/ExtractionReviewPage.tsx:26-31`
records that the percentage "was never measured … so it is no longer shown anywhere". Consistent with
charter §5.

**Frontend gaps** (see §9 for the dead API wrappers): `/api/LeadIngestion/blocked-files` has a service
method but no screen; `/api/LeadIngestion/analytics` has **no frontend consumer at all**.

---

## 8. Test project layout, lane split, migration conventions

### 8.1 Projects

- `⟨T⟩ERP_RFQ_Automation.Tests.csproj` — net8.0, xunit 2.9.3, EFCore.Sqlite 9.0.9, **Testcontainers.PostgreSql 4.13.0**, Mvc.Testing 8.0.21. **230 `.cs` files**, 190 at root.
- `Backend/ERP_RFQ_Automation.AcceptanceFixture/` — console Exe, **not in the .sln** → never built or run by CI.

Sub-folders: `Support/` (7), `HttpIntegration/` (8), `DocumentInspection/` (5), `CustomFields/` (5),
`CommercialRouting/` (4), `DocumentIntelligence/` (4), `CommercialCases/` (2), `Intelligence/` (2),
`PostgreSQL/` (2), `CommercialIntelligence/` (1), `Fixtures/` (4 `.xls` binaries), plus `TESTING.md`
and `TestAssemblyInitialization.cs`.

### 8.2 THE LANE SPLIT (read this before adding a test)

**There is no attribute-based router. Routing is decided by which fixture the test class uses.**

**SQLite lane (default, 84 files).** `⟨T⟩Support/TestDb.cs` — `:26` `new SqliteConnection("Data
Source=:memory:")`, `:30` `.UseSqlite`, **`:35` `ctx.Database.EnsureCreated()`** (schema from the EF
*model*, **not** migrations), `:48-52` `StubTenant : ITenantContext`. A test opts in simply by writing
`using var db = new TestDb();`. Plain `[Fact]`, no trait, no collection.

**PostgreSQL lane (39 files).** `⟨T⟩Support/PostgreSqlTestDatabase.cs` — `:9-13`
`[CollectionDefinition(Name, DisableParallelization = true)] class PostgreSqlIntegrationCollection :
ICollectionFixture<PostgreSqlTestDatabase>`; `:19` `IAsyncLifetime`; `:21-25`
`new PostgreSqlBuilder("postgres:16-alpine")`; `:35` Npgsql legacy-timestamp switch; `:38`
`.UseNpgsql(_container.GetConnectionString())`; **`:49` `await context.Database.MigrateAsync()`** — the
real migration chain, which is *the* difference from the SQLite lane; `:64-73`
`TenantContextWithRls(...)` adds `TenantRlsCommandInterceptor`, `MaxPoolSize = 1`.

Two markers, only the first of which actually routes:

1. **Class-level `[Collection(PostgreSqlIntegrationCollection.Name)]`** + primary-constructor injection of `PostgreSqlTestDatabase` — **this is what selects PostgreSQL.**
2. **Method-level `[Trait("Category", "PostgreSQL")]`** — a CI/filter label only; removing it changes nothing about which database is used.

Canonical example — `⟨T⟩ExtractionDeadLetterPostgreSqlTests.cs:11-16`:

```csharp
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ExtractionDeadLetterPostgreSqlTests(PostgreSqlTestDatabase database)
{
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RuntimeRole_ExecutesAuthoritativeRecoveryThroughRls()
```

Naming convention: Postgres-lane files end `…PostgreSqlTests.cs`. `Trait` is xUnit's built-in
attribute — there is **no** custom `PostgresFact` / `RequiresPostgres` in the tree, and **zero**
`IClassFixture` usages (everything is `ICollectionFixture`).

Third lane (HTTP): `[Collection(Release01BHttpCollection.Name)]` —
`⟨T⟩HttpIntegration/Release01BHttpApplication.cs:28-29` (7 files) — and
`[Collection(Module03SalesRoutingHttpCollection.Name)]` (1 file).

Global: `⟨T⟩TestAssemblyInitialization.cs:7-13` `[ModuleInitializer]` sets the Npgsql legacy-timestamp
switch before any model is cached.

**Postgres tests do NOT skip without Docker — they FAIL.** `InitializeAsync` calls
`_container.StartAsync()` unconditionally (`⟨T⟩Support/PostgreSqlTestDatabase.cs:36`). There is no
`Skip =`, no conditional-fact attribute, no Docker probe and no env-var gate anywhere in the project.
The documented escape hatch (`⟨T⟩TESTING.md:26-28`) is `dotnet test --filter "Category!=PostgreSQL"`,
**which no script or workflow in the repo actually passes.**

### 8.3 Ingestion-relevant test files (all under `⟨T⟩`)

- **Ingestion/upload:** `DocumentIngestionServiceTests.cs`, `DocumentIntakeAllowListTests.cs`, `LeadIngestionAuditTests.cs`, `LeadIngestionAuthorizationTests.cs`, `FolderIngestionSecurityTests.cs`, `ManualUploadControllerTrustTests.cs`, `UploadAndLoginHardeningTests.cs`, `UploadDoorReviewApprovalTests.cs`, `LeadPersisterSplitTests.cs`, `LeadReviewUpsertTests.cs`, `MultiInquirySplitterTests.cs`
- **Extraction:** `ExtractionControllerTests.cs`, `ExtractionQueueIdempotencyTests.cs`, `ExtractionWorkerLeaseTests.cs`, `ExtractionWorkerSpreadsheetFallbackTests.cs`, `ExtractionSchemaClientFieldsTests.cs`, `ExtractionAccuracyMeasurementTests.cs`, `ExtractionDeadLetterServiceTests.cs` (SQLite) / **`ExtractionDeadLetterPostgreSqlTests.cs`** (PG), `ChunkedExtractionServiceTests.cs`, `ConversationalExtractionServiceTests.cs`, `OllamaOutputTruncationTests.cs`, **`SynchronizeSharedExtractionOccurrencesMigrationPostgreSqlTests.cs`** (PG)
- **Evidence:** **`AuthoritativeEvidencePostgreSqlTests.cs`** (PG, 68 KB — the largest test file), **`EvidenceMigrationUpgradePostgreSqlTests.cs`** (PG), `EvidenceObjectStorageTests.cs`, `EvidenceRetentionPurgeTests.cs`, `EvidenceStorageHealthCheckTests.cs`, `ProcessingEvidenceTests.cs`, `DocumentIntelligence/Persistence/EvidenceLedger{Model,Domain}Tests.cs`
- **Email:** `EmailTriageTests.cs`, `EmailTriageServiceTests.cs`, `EmailBodyNormalizerTests.cs`, `EmailIngestEnqueuerTests.cs`
- **Reading/inspection:** `ProductionDocumentReaderTests.cs`, `ProductionDocumentReaderSpreadsheetFallbackTests.cs`, `WordBinaryTextExtractorTests.cs`, `RealDocumentBenchmarkTests.cs`, `DocumentInspection/*` (5, incl. **`MacroRejectionTruthPostgreSqlTests.cs`**)

### 8.4 CI

`.github/workflows/ci.yml` (repo root; there is **no** `.github` under `Backend/ERP_RFQ_Automation/`).
Backend job gated by `dorny/paths-filter@v3` on `Backend/**`; steps checkout → setup-dotnet 8.0.x →
NuGet cache → `dotnet restore` → `dotnet build -c Release` → `dotnet test ERP_RFQ_Automation.sln -c
Release --no-build --logger trx`.

**Both lanes run in one invocation** — no `--filter`, no `services: postgres:` block, no explicit
Docker step. It works only because `ubuntu-latest` ships Docker for Testcontainers. Frontend job:
`npm ci` → `npm run build` → `npm run e2e:list` → Playwright gated behind
`vars.E2E_ACCEPTANCE_ENABLED == 'true'`.

### 8.5 Migration conventions

`⟨B⟩Migrations/` — **209 files: 102 migrations + 102 `.Designer.cs` + `ErpRfqAutomationContextModelSnapshot.cs`.**
Naming: EF standard `yyyyMMddHHmmss_PascalCaseName.cs`. Range `20260715164249_InitialPostgres.cs` →
`20260805223247_GuardQuoteableQuantities.cs` (~3 weeks).

House style:

- **Every migration class carries a long `<summary>` XML doc comment explaining *rationale*, not mechanics** — naming the defect, the ruling, the specific tables and why those and not others, and citing production verification (e.g. `20260805223247_GuardQuoteableQuantities.cs`: "Verified against production before writing: 0 rows violate any of the three constraints", plus headed sections `WHY THESE THREE TABLES AND NOT LeadItems:` and `COORDINATION NOTE:`).
- **82 of 102 migrations (80 %) call `migrationBuilder.Sql(...)`** — but there is an explicit house rule *against* raw SQL where the model can express the change. `GuardQuoteableQuantities.cs` documents an earlier hand-written revision being rewritten model-first because "raw SQL is invisible to the EF model, so databases built from the model (the SQLite test databases, `EnsureCreated`) never received the constraints". **This ties the migration convention directly to the §8.2 lane split — a model-invisible change silently diverges the two lanes.**
- Raw `Sql()` survives where the model genuinely cannot express it: triggers (`trg_source_documents_no_delete`, `trg_source_documents_purge_forward_only`, `trg_protect_source_document_identity`), RLS policies, and data backfills.
- Uniform header: BOM + `using Microsoft.EntityFrameworkCore.Migrations;` + `#nullable disable` + namespace. `Up`/`Down` each carry `/// <inheritdoc />`.
- Postgres-native types spelled explicitly; new columns additive and `nullable: true`, with the doc comment stating how existing rows read.

Startup application — `Program.cs:603-629`: gate `:606-607`
`Database:ApplyMigrationsOnStartup ?? IsProduction()` (**ON in Production by default**); separate
`ConnectionStrings:MigrationConnection` else `ResolveDirectMigrationConnection` which strips the
pooler host (`:636-637`); second gate `:611-616` throws if
`Database:AllowManagedOwnerRoleMigrationCompatibility` is on without a distinct migration connection;
apply at **`:628 await migrationDb.Database.MigrateAsync()`** with `EnableRetryOnFailure(5, 10s)` and
`CommandTimeout(120)`. **`EnsureCreated` is never called in `Program.cs`** — it exists only in
`⟨T⟩Support/TestDb.cs:35`.

---

## 9. DEAD CODE — read this before citing any file

Marked explicitly because several of these read like live customer integrations.

| Symbol | File : line | Verdict |
|---|---|---|
| **`ProcessLegacyAramcoFolderAsync`** | `⟨B⟩Services/FolderService.cs:321` | **DEAD. Zero callers anywhere in the tree.** ~120 lines of Aramco RFP parsing. |
| **`ProcessLegacySecFolderAsync`** | `⟨B⟩Services/FolderService.cs:680` | **DEAD. Zero callers.** ~150 lines of SEC document parsing. |
| **`FolderService.SaveAttachmentsAsync`** | `⟨B⟩Services/FolderService.cs:832` | **DEAD by transitivity** — called only from `:444` and `:809`, both inside the two dead methods above. |
| **`FolderService.BuildExtraction`** | `⟨B⟩Services/FolderService.cs:950` | **DEAD by transitivity** — called only from `:753`, inside the dead SEC method. |
| **`DefaultExtractionDocumentReader`** | `⟨B⟩Extraction/ExtractionWorker.cs:670-816` | **DEAD.** Never registered (`Program.cs:485` registers `ProductionDocumentReader`), zero call sites, zero tests. Referenced only by two doc comments in `ProductionDocumentReader.cs:40,52`. ~147 lines. |
| `ILeadPersister.PersistAsync` | `⟨B⟩Extraction/ExtractionWorker.cs:61` | **DEAD IN PRODUCTION** — only `⟨T⟩LeadPersisterSplitTests.cs:90,135,194,239,282`. Production uses `PersistAndCompleteAsync` (`ExtractionWorker.cs:291`). |
| `IChunkedExtractionService.ExtractAsync` | `⟨B⟩Extraction/ChunkedExtractionService.cs:157` / impl `:249-254` | **DEAD IN PRODUCTION** — the worker calls `ExtractStructuredAsync` / `ExtractUnstructuredAsync` directly (`ExtractionWorker.cs:225,253`). Tests only. |
| `ExtractionStatus.Failed` | `⟨B⟩Extraction/ExtractionJob.cs:33` | **UNREACHABLE ENUM MEMBER** — no SQL statement in `ExtractionQueue.cs` ever writes `'Failed'`. |
| `SourceDocument.BytesAvailable` | `⟨B⟩DocumentIntelligence/Persistence/EvidenceLedgerEntities.cs:426` | **ZERO CALLERS.** Its own doc comment describes a 410-Gone tombstone contract that nothing implements. |
| `MarkIntakeProcessing/Failure/Finalized` bodies | `⟨B⟩Extraction/ExtractionWorker.cs:483, :605, :627` | **NO-OPS ON PostgreSQL** — each early-returns `if (db.Database.IsNpgsql())`. Dead on the production provider. |
| `NotConfiguredVisionReader` | `⟨B⟩Boq/IVisionDocumentReader.cs:48` | Registered, but a permanent no-op stub. The `AnthropicVisionReader` its comment references **does not exist**. |
| `GET api/File/DownloadFile` | `⟨B⟩Controllers/FileController.cs:53-58` | **DEAD BY DESIGN** — unconditional `410 Gone`. |
| `FileController` legacy ctor path | `⟨B⟩Controllers/FileController.cs:35, :111-115` | Unreachable via DI, but a live **unverified** (no hash check) path-based read for any direct construction. |
| Frontend upload wrappers | `⟨F⟩api/services/leadService.ts:615, :631, :637, :642, :648` | **DEAD** — `uploadManual`, `uploadRfqExcel`, `uploadBulkLeads`, `uploadToFolder`, `processAllFolderLeads` have zero callers outside the service file. |
| `⟨F⟩pages/PlatformGovernance/ArtifactStudioPage.tsx` | — | **ORPHANED COMPONENT** — the only `*Page.tsx` under `⟨F⟩pages/` with no route in `App.tsx`. |
| `Backend/ERP_RFQ_Automation.AcceptanceFixture/` | — | **NOT IN THE SOLUTION** — never built or run by CI. |
| `.msg` / `.eml` / `.html` reader branches | `⟨B⟩Extraction/ProductionDocumentReader.cs:129-139` | **DO NOT EXIST**, and are blocked upstream by `DocumentIntakeAllowList.cs:30-34`. Raw `.eml` is parsed by MimeKit in `⟨B⟩Services/EmailService.cs:232`, never by this reader. |

**Live-but-inert (distinct from dead):** the watched-folder door (§2.3) executes zero iterations in
production because `{root}/Tenants` does not exist; `EvidenceRetentionService` (§5.4) has no
scheduler; `ExtractionDeadLetterService` and `SecurityScanRecoveryService` (§3.5) are scoped and
operator-triggered, not hosted.

---

## 10. Production state at a glance (2026-08-06)

| Fact | Value |
|---|---|
| Service | `srv-d9csjhe1a83c739phue0`, web, **1 instance**, starter plan, Oregon, disk `/var/data` **5 GB** |
| Health probe path | `/health` (live-tagged checks only) — returns `Healthy`; **`/ready` returns `Unhealthy`** on `evidence-storage` |
| `ExtractionJobs` | 57, **all `SourceType='ManualUpload'`** — 21 Succeeded, 22 DeadLetter, 10 Pending, 4 Extracting |
| `source_documents` | 86, all `business_unit_id=1`, all `object_bucket='local'`, 86 distinct hashes, 0 orphans |
| Security status | 54 Quarantined · 30 Cleared · 2 Rejected; engine `Nexora.EICAR` on all |
| `source_document_occurrences` | 289 — 288 ManualUpload, 1 Email (Rejected) |
| `LeadIngestionOccurrences` | 35 — 12 ManualUpload (Service), 23 Legacy (one-shot migration backfill) |
| `Leads` | 33 — ManualUpload 22, Email 10 (all ≤ 2026-06-17, legacy), Manual 6, Aramco 2 |
| `EmailIngests` | 40, newest 2026-07-28T17:46:49Z; `TriageOutcome` NULL on all 40 |
| Email poller | loop alive (`Starting email fetch...` 02:05:20Z); **`MailKit.Security.AuthenticationException` every cycle**, yet logs `Email fetch completed successfully.` |
| Folder sweep | **zero iterations** — no `Folder sweep` or `No files found` log line has ever appeared |
| `FolderIngestionRetryStates` / `evidence_retention_policies` / `LeadReviewAudits` / `LeadMatchCandidates` / `ExtractionCorpusEntries` | **0 rows each** |
| Lost bytes | 19 jobs at `/app/Uploads/…` (ephemeral, wiped on redeploy) + 1 at `/Users/zackkhan/Downloads/…`; 45 of 86 documents have hit `SOURCE_OBJECT_UNAVAILABLE` |
| `extraction_dead_letter_events` | 114 — 85 `RetryQueued` (operator re-drives), 29 `SourceObjectUnavailable` |
