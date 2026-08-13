# Phase 1 — Evidence Matrix: Email → Lead → RFQ

**Audited base:** `1601db6ad9e19655118ae5a051e1cba6b1649d34` (= `origin/main`, = production `/build-identity`).
**Branch:** `fix/enterprise-email-lead-participation`.
**Method:** every row cites executable code, migration, configuration or test at the audited SHA.
Documentation claims are recorded as claims, never as controls.

Production probes taken at audit time:

| Endpoint | Result |
| --- | --- |
| `/build-identity` | 200 — `{"revision":"1601db6ad9e19655118ae5a051e1cba6b1649d34","version":"1.0.0","environment":"Production"}` |
| `/health` | 200 `Healthy` |
| `/ready` | **503 `Unhealthy`** |

`/ready` returns a bare status string with no component breakdown, and every diagnostic
variant tried (`/healthz`, `/ready/detail`, `/api/platform/readiness`, `/health/ready`)
returns 401. **Which check is red cannot be determined from outside the deployment.**
That is itself a finding: readiness is un-triageable without dashboard access.

---

## 1. Email ingestion entry points

| # | Entry point | Code | Status |
| --- | --- | --- | --- |
| 1 | IMAP poller (background) | `Services/EmailBackgroundService.cs:88` → `RunPollCycleAsync:252` → `IEmailService.FetchAndSaveLeadsAsync` | Active. Single-instance by PostgreSQL advisory lock (`PollLockName = "nexora:email-poller"`, line 37). |
| 2 | Per-message processing | `Services/EmailService.cs:754` `ProcessSingleEmailAsync` | Active. The only writer of `EmailIngest`. |
| 3 | Unified-queue fan-out | `Services/EmailService.cs:906` `EnqueueEmailForExtractionAsync` → `Ingestion/Triage/EmailIngestEnqueuer.cs:35` | Active (`Ingestion:UseUnifiedQueue` defaults `true`, `appsettings.json:34`). |
| 4 | **Legacy direct-extraction path** | `Services/EmailService.cs:881-893` → `SaveLeadFromEmailAndAttachments:1057` | **Reachable.** Guarded only by `_useUnifiedQueue` (`EmailService.cs:160`), a plain config read. It creates a Lead directly (`EmailService.cs:1289`, `context.Leads.Add` at `:1315`) and writes attachments to disk (`SaveAttachmentsAsync:1881`, `File.Create` at `:1944`). |
| 5 | Manual "reprocess as inquiry" | `Controllers/EmailTriageController.cs`, re-uses `EmailIngestEnqueuer` | Active. |
| 6 | Watched-folder sweep | `Services/FolderService.cs` via `EmailBackgroundService.cs:308-352` | Active. **Not** an email door but shares the extraction queue and creates Leads (`FolderService.cs:427`, `:797`). Tenant list comes from directory names (`DiscoverTenantFolderIds`), not the platform database. |

**Finding 1.1 — two production ingestion gateways exist.** `DocumentIngestionService.cs:25`
throws if `Ingestion:UseUnifiedQueue=false` in Production, which blocks the *document*
door, but `EmailService` reads the same flag independently at `:160` and branches to the
legacy path *before* `IDocumentIngestion` is ever consulted. The legacy branch is dead only
by configuration, not by construction. Phase 2 requires it hard-retired.

## 2. Lead creation / reconciliation paths

| # | Path | Code | Goes through `ILeadIdentityApplicationService`? |
| --- | --- | --- | --- |
| 1 | Extraction worker (the queue) | `Extraction/ExtractionWorker.cs:1084` `ReconcileAsync`, lead built at `:1520` | **Yes** |
| 2 | Lead uploader | `Services/LeadUploaderService.cs:129`, `Leads.Add` at `:220` | Yes — consumer listed |
| 3 | Manual upload | `Services/ManualUploadService.cs:363`, `Leads.Add` at `:391` | Yes — `:405` records the door was converted |
| 4 | Lead ingestion controller | `Controllers/LeadIngestionController.cs` | Yes |
| 5 | **Watched folders** | `Services/FolderService.cs:427` and `:797`, `Leads.Add` at `:454`/`:824` | **Not a listed consumer of `ILeadIdentityApplicationService`** |
| 6 | **Legacy email path** | `Services/EmailService.cs:1289`, `Leads.Add` at `:1315` | **No** |
| 7 | **RFQ repository** | `Repositories/RfqRepository.cs:342` `Leads.Add` | **No** — creates a shell Lead beneath an RFQ |
| 8 | Golden-journey seeder | `Infrastructure/GoldenCommercialJourneySeeder.cs:210` | Seed/demo only |

`LeadIdentityApplicationService.cs:894` documents the invariant that doors must not
`_context.Leads.Add` directly. Rows 5, 6 and 7 do exactly that.

**Finding 2.1 — Lead identity is one job at a time, never one message.**
`ExtractionWorker.cs:1077-1098` loops per extracted inquiry *within a single job* and calls
`ReconcileAsync` with `SourceOccurrenceId = "extraction:{bu}:{jobId}:inquiry:{n}"`. Body and
attachments are separate jobs, so they reconcile **independently and concurrently**. The only
thing binding them is `LogicalGroupKey = "email:{Message-Id}"` (`EmailIngestEnqueuer.cs:65`),
which is carried into reconciliation at `ExtractionWorker.cs:1096` as a duplicate-detection
*signal* only. Nothing waits for siblings; nothing counts expected components.
**One email with a body and three attachments produces up to four independent Leads.**

**Finding 2.2 — multi-inquiry splitting runs per document, not per message.**
`MultiInquirySplitter` is called only from `Extraction/ChunkedExtractionService.cs:477`,
`:696` and `:754` — inside single-document extraction. Phase 2 requires splitting *after*
message assembly.

**Finding 2.3 — a body-only Lead is still constructible under partial failure.**
PR #26 closes the storage-outage case (see §8), but any *non*-storage exception on an
attachment is still caught per-file (`EmailIngestEnqueuer.cs:185` in the audited base) and
the body job proceeds, producing a Lead with no record of the priced attachment beyond a
`SkippedAttachmentsJson` string. There is no recoverable hold and no replay.

## 3. Lead-to-RFQ and direct RFQ creation paths

| # | Path | Code | Reachable with a `LeadId`? |
| --- | --- | --- | --- |
| 1 | Legacy conversion endpoint | `Controllers/LeadController.cs:125` → `Repositories/LeadRepository.cs:320` `ConvertLeadToRfqAsync` → `CreateRfqFromLeadAsync:390`, `new Rfq` at `:414`, `Rfqs.Add` at `:368` | Yes |
| 2 | Conversion intelligence | `Intelligence/Conversion/LeadConversionIntelligence.cs:214` `new Rfq`, `Rfqs.Add` at `:317` | Yes |
| 3 | Agent tool | `Intelligence/Conversion/ConversionAgentTools.cs:86` `ConvertLeadToRfqTool`, registered `Program.cs:710` | Yes — wraps path 2 |
| 4 | **RFQ repository direct create** | `Repositories/RfqRepository.cs:371` `Rfqs.Add`, and `:342` `Leads.Add` | Yes — attaches/creates a Lead under an RFQ |
| 5 | **RFQ controller direct create** | `Controllers/RfqController.cs:205` and `:300` `new Rfq` | Two distinct constructions |
| 6 | **RFQ uploader** | `Services/RfqUploaderService.cs:140` `new Rfq`, `Rfqs.Add` at `:155` | Yes |
| 7 | **Manual upload** | `Services/ManualUploadService.cs:1099` `new Rfq`, `Rfqs.Add` at `:1115` | Yes |

**Finding 3.1 — there are seven RFQ creation sites and no single promotion authority.**
Existing partial governance is acknowledged in comments (`RfqRepository.cs:306`, `:319`,
`:337` reference `LeadRepository.ConvertLeadToRfqAsync` and idempotency) but it is
*replicated reasoning*, not a shared transactional service. Phase 4 requires one.

## 4. Filesystem writers used by email / attachment ingestion

| # | Writer | Code | Production-authoritative? |
| --- | --- | --- | --- |
| 1 | **Raw `.eml`** | `Services/EmailService.cs:793-797` — `Path.Combine(_rawEmailPath, ...)` then `message.WriteTo(rawPath)` | **Yes — local disk is the only copy.** Never reaches `IEvidenceObjectStorage`. Directly violates the Phase 2 rule. |
| 2 | Legacy attachment writer | `Services/EmailService.cs:1944` `File.Create(physicalPath)` (in `SaveAttachmentsAsync:1881`) | Yes, on the legacy path |
| 3 | Manual upload | `Services/ManualUploadService.cs:567` `File.Create(physicalPath)` | Yes |
| 4 | Folder service | `Services/FolderService.cs:149` `FileStream`, `:881` `File.WriteAllBytesAsync`, `:1482` staged metadata | Yes |
| 5 | Extraction job metadata sidecar | `Extraction/ExtractionJobMetadata.cs:108-113` `File.WriteAllTextAsync` | Provenance sidecar on disk |
| 6 | Local evidence storage | `Infrastructure/Storage/IEvidenceObjectStorage.cs:235,257,300` | The configured provider in `render.yaml` (see §6) |
| 7 | Document reader temp | `Extraction/ProductionDocumentReader.cs:1041` | Transient |

`render.yaml:120-122` already names writers 2, 3 and 4 as legacy physical-path writers that
must be converted "or the document estate ends up split across two stores with two restore
procedures." **They have not been converted.** Writer 1 (the raw `.eml`) is not even on that list.

## 5. Active mailbox authentication mechanisms

Exactly one mechanism exists: **MailKit IMAP/SMTP with a stored plaintext-view password.**
There is no OAuth, no Graph, no connector abstraction.

| Call site | Identity used |
| --- | --- |
| `Services/EmailService.cs:456` (poller connect) | `config.EmailAddress` |
| `Services/EmailService.cs:515` (poller, second client) | `config.EmailAddress` |
| `Services/EmailService.cs:2045` (send) | `config.EmailAddress` |
| `Mailbox/MailboxConnectionProbe.cs:247` (**connection test**) | `request.Username` |
| `Mailbox/MailboxConnectionProbe.cs:319` (**connection test**) | `request.Username` |
| `Security/OutboundSmtpTransport.cs:26` | `configuration.Username` |
| `Notifications/Providers/SmtpEmailSender.cs:97` | `smtp.Username` |

**Finding 5.1 — CONFIRMED: the connection test does not test the poller's credential.**
`EmailConfiguration` carries both `Username` (`Models/EmailConfiguration.cs:22`) and
`EmailAddress` (`:16`) as independent columns. The operator's "test connection" authenticates
as `Username`; the poller authenticates as `EmailAddress`. Where a tenant's UPN differs from
the SMTP address — the normal case for shared and enterprise mailboxes — **a green test proves
nothing about the poller**, which is the precise failure mode `EmailBackgroundService.cs:22-33`
documents having already occurred in production for seven days.

Credential protection that *does* exist: `Models/EmailConfiguration.cs:36-37` — `[JsonIgnore]`
plus a `ProtectedSecretConverter` applied in `OnModelCreating`; migration
`20260806194714_ProtectMailboxCredentialsAtRest`; backfill `Security/MailboxCredentialProtectionBackfill.cs`.
Tests: `MailboxCredentialProtectionTests.cs`, `MailboxCredentialBackfillPostgreSqlTests.cs`.
Readiness/ledger honesty: `LastSuccessfulPollOn` is advanced only on genuine success
(`Models/EmailConfiguration.cs:65-70`) and a failed cycle does not beat the heartbeat
(`EmailBackgroundService.cs:165`). **These two Phase 5 requirements are already met at base.**

## 6. Backblaze (S3) and ClamAV configuration bindings

**Storage.** `Program.cs:137-146` binds `S3EvidenceStorageOptions` and selects
`S3EvidenceObjectStorage` *only* when `EvidenceStorage:Provider == "S3"`, otherwise
`LocalEvidenceObjectStorage`.

`render.yaml:124-125` sets `EvidenceStorage__Provider: Local`, with the four credential keys
(`ServiceUrl`, `Region`, `AccessKeyId`, `SecretAccessKey`, `Bucket`) **commented out**
(`render.yaml:126-134`).

> **Configuration-source conflict.** PR #26's incident report states production evidence
> storage "was pointed at a misspelled bucket" and that `/ready` showed `evidence-storage`
> Unhealthy. A bucket name only exists on the S3 path. Therefore **the live Render dashboard
> is running `Provider=S3` while `render.yaml` in the repository says `Local`.** A Blueprint
> sync today would silently switch production back to local disk. This is exactly the
> Phase 6 "reconcile the dashboard with `render.yaml`" item, and it is the most likely
> single cause of the current `/ready` 503.

**Finding 6.1 — readiness probe cleanup leaks a version on every probe.**
`IEvidenceObjectStorage.cs:333-371` `ProbeAsync` already uses a dedicated `_readiness/`
prefix (`:340` — **this Phase 6 sub-item is already satisfied at base**) and verifies
read-after-write (`:356-361`). But the `finally` block at `:365-369` issues
`DeleteObjectAsync` **without a `VersionId`**, while `EnsureVersioningEnabled`
(`:586`) refuses to be ready unless bucket versioning is on. On a versioned bucket a
versionless delete writes a *delete marker* and retains the object version, so **every
readiness probe permanently adds two versions.** The correct fix is to delete the exact
version returned by the PUT — the code already knows how to do this for real objects
(`:440-453` passes `VersionId` and comments on precisely this hazard). The probe path was
missed.

**Finding 6.2 — versioning guarantee is silently absent on R2.**
`render.yaml:118-121` records that `VerifyBucketVersioningAsync` swallows R2's 501.
Confirmed at `IEvidenceObjectStorage.cs:545-563`.

**Scanner.** `Program.cs:153-171` binds `MalwareScannerOptions` and selects via
`MalwareScannerFactory.Select`, logging the choice and probing at startup
(`MalwareScannerStartupProbe`, `Program.cs:171`).

`render.yaml:145-146` sets `DocumentInspection__Scanner__Provider: BuiltIn`.
`render.yaml:33-41` states the ClamAV private service **is intentionally not deployed**.
`render.yaml:136-144` states BuiltIn "is NOT an anti-virus engine… It will NOT detect real
malware," deferred to save $85/mo until after the pilot.

**Finding 6.3 — production scanner provider is `BuiltIn`, which Phase 6 forbids, and no
`clamd` service exists to switch to.** This is a deployment gap, not only a code gap.

`/ready` composition (`Program.cs:232-247`): `database`, `evidence-storage`,
`storage-capacity`, `malware-scanner`, `extraction-worker`, `quote-delivery-worker`,
`procurement-dispatch-worker`, `background-workers`, `email-poller`, OCR. Any one of these
red produces the observed 503.

## 7. Existing tests covering each path

Present at base (`Backend/ERP_RFQ_Automation.Tests/`, 389 files):

| Area | Tests |
| --- | --- |
| Email fan-out | `EmailIngestEnqueuerTests`, `EmailPollWindowAndIngestKeyTests`, `EmailIngestTenantScopeTests`, `EmailIngestCrossTenantDuplicatePostgreSqlTests` |
| Triage | `EmailTriageTests`, `EmailTriageServiceTests`, `EmailBodyNormalizerTests` |
| Poller honesty | `EmailChannelTruthfulnessTests` |
| Mailbox | `EmailConnectionTesterTests`, `MailboxAdministrationTests`, `MailboxCredentialProtectionTests`, `MailboxCredentialBackfillPostgreSqlTests` |
| Lead identity | `LeadIdentityApplicationServiceTests`, `LeadIdentityBaselineRevisionPostgreSqlTests`, `Release01ALeadIdentityPostgreSqlTests`, `LeadPersisterSplitTests`, `LeadIngestionAuditTests`, `LeadIngestionAuthorizationTests` |
| Conversion | `LeadConversionGovernanceTests` |
| RFQ | `RfqServerAuthorityTests`, `RfqCreateCommercialLineageTests`, `RfqTenantRoleCreatePostgreSqlTests`, `RfqLineParticipationTests`, `RfqLineParticipationEndpointTests` |
| Storage | `EvidenceObjectStorageTests`, `EvidenceStorageHealthCheckTests`, `AuthoritativeEvidencePostgreSqlTests`, `LocalFileStorageTests`, `StorageCapacityHealthCheckTests` |
| Scanner | `MalwareScannerHealthCheckTests` |

### Coverage gaps — no test exists for:

1. **One email → one Lead.** No test asserts that a body + N attachments converge on a single
   canonical Lead. `EmailIngestEnqueuerTests` proves jobs share a `BatchId`; per the brief,
   that is not proof of coherent assembly.
2. Message-level assembly states, expected/completed child-job counts, or a recoverable hold.
3. Embedded MIME `MessagePart` handling — `EmailIngestEnqueuer.cs:96-99` **skips** it with
   reason "embedded email message is not ingested". `.msg` is likewise unsupported
   (`IsSupportedExtension`).
4. Multi-inquiry splitting *across* body and attachments.
5. Pre-RFQ participation. **`RfqLineParticipation*` tests cover supplier line participation in
   an RFQ response (post-RFQ, migration `20260806044841`), which is a different concept
   entirely.** No Lead-level Participate/NoBid decision model exists.
6. Stale decisions after a Lead revision.
7. Any RFQ-creation bypass being rejected.
8. ClamAV infected / unavailable / recovered against a real `clamd`.
9. Backblaze probe version cleanup.
10. Restart during each assembly stage.

---

## 8. PR #26 review verdict

**Head:** `003d74f67d36e940a0383a564728e310a64bd687`, 24 files, +1459 / −23, `MERGEABLE`, open.

**Reviewed and sound — reuse:**

- `Infrastructure/Storage/EvidenceStorageProblem.cs` (new) — one RFC 7807 rendering of
  "document storage is unavailable" for every intake door, carrying `isConfigurationFault`
  so a caller is told whether retrying can ever work. Puts the sentence in `detail`, not
  `title`, which is the field the existing error boundaries actually render.
- `EvidenceStorageUnavailableException` typed at the storage boundary, with `Amazon.S3` types
  kept below it so a bucket name, endpoint or credential cannot reach a response body.
- Allow-list classification: only *named* faults are verdicts about the document; anything
  unrecognised fails toward "storage unavailable" rather than "retry this file."
- `EmailIngestEnqueuer.cs` change: on the first storage refusal, sets `storageOutage` and
  records every remaining attachment as skipped for the true reason without attempting a
  doomed upload; the body enqueue catches the same exception and creates no job.
- Removal of `ex.GetType().Name` from the durable, user-visible skip reason — that field was
  publishing `AmazonS3Exception` onto the triage screen.

This is directly load-bearing for Phase 2's rule *"if one attachment fails because storage or
ClamAV is unavailable, do not create a misleading body-only Lead."* It is the correct
foundation and I will build the assembly hold on top of it rather than re-deriving it.

**Gaps that PR #26 does not close (and does not claim to):**

1. **No recoverable hold.** After the refusal the `EmailIngest` reaches a terminal
   `Failed - N attachment(s) skipped` status. There is no state that says "hold, retry when
   storage returns," and no replay. The message is not lost (raw `.eml` is on disk) but
   nothing will ever pick it up again automatically.
2. **Ordering dependence.** Attachments are enqueued before the body, so a storage outage
   fails both. If storage fails on an attachment for a *non*-storage reason, or recovers
   between the attachment loop and the body enqueue, a body-only Lead is still produced.
3. **ClamAV unavailability is not covered** — only evidence storage.
4. The honest note in the PR body is accurate: the healthy-path regression is Postgres-only
   because a `DateTimeOffset` `ORDER BY` in pre-existing product code is rejected by SQLite.

**Verdict: reuse the storage-refusal boundary as-is; do not treat it as closing Phase 2.**
Do not merge PR #26 blind — it should land on its own merits, and this branch takes its
changes as a reviewed dependency.

---

## 9. Structural blockers on live acceptance (identified before coding)

These are properties of the deployment at the audited SHA, not of the work:

| Acceptance item | Status | Evidence |
| --- | --- | --- |
| 3. `/ready = 200` | **Currently failing** | Probe returns 503; component breakdown not externally visible (401 on every detail route) |
| 8. Real EICAR refused *by ClamAV* | **Blocked** | `render.yaml:145` `Provider: BuiltIn`; `render.yaml:33-41` says the `clamd` service is deliberately not deployed. BuiltIn does detect EICAR, but that would not be ClamAV refusing it |
| 12. Scanner provider is ClamAV | **Blocked** | same |
| 12. Storage provider is S3 | **Unverifiable from repo** | `render.yaml` says `Local`; PR #26's incident implies the dashboard says S3. The two disagree |
| 4–7, 9–11 (sandbox mailbox, evidence linkage, participation, RFQ lines, B2 hash read-back, restart replay) | **Blocked pending credentials** | No sandbox enterprise mailbox, Render dashboard, or Backblaze key is available to this session, and none may be requested |

**Consequence: a GO decision is not attainable at this SHA regardless of code quality**, because
two acceptance items require infrastructure that has been deliberately deferred. The honest
outcome for this slice is a code-complete branch plus an explicit BLOCKED live-acceptance
report and a NO-GO, with the deployment work named as the gating dependency.
