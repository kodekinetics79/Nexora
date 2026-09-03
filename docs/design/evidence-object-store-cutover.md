# Remove the disk dependency: legacy writers to the evidence object store

Stream 4, item B. Status: designed 2026-09-02; writers, readers, migration job and flag
implemented in the same PR (flag off by default); disk removal is a later operator step.

## Problem

Every deploy is an outage because the web service carries a Render persistent disk
(`render.yaml:79-83`, `/var/data`), and a service with a disk cannot be replaced
zero-downtime: Render stops the old container, detaches, attaches, starts the new one.
Governed evidence already lives in S3 (production `source_documents.object_bucket`:
311 `NexoraBucket`, 96 `local`; `ExtractionJobs.StoragePath`: 320 `s3://`, 47 under
`/var/data`), but four legacy writers still put bytes on the disk through `IFileStorage`:

| Writer | What | Where on disk | Production rows pointing at disk |
|---|---|---|---|
| `Services/EmailService.cs:1261-1266` | raw `.eml` compat copy | `Raw_Emails/` | 324 `EmailIngests.RawEmailPath` |
| `Services/EmailService.cs:2750-2790` | lead attachments from mail | `RFQ_Attachments/` | 79 `Attachments` rows across the three folders, all without a digest, path stored with the Windows `Uploads\` prefix from the pre-Render host |
| `Services/ManualUploadService.cs:635-660` | lead attachments from upload | `Manual_Attachments/` | (in the 79) |
| `Services/FolderService.cs:590-625` | lead attachments from the watched folder | `Leads_Folder_Attachments/` | (in the 79) |

Readers: `Controllers/FileController.cs:186-210` (attachment download — the DI constructor
sets `_legacyStorage = null`, so a disk-path row already answers 404 in production),
`Services/EmailService.cs:1508,1600` (stranded-ingest recovery), `Ingestion/Assembly/
RawEmailEvidenceReader.cs:72-78` (fallback), `Ingestion/CanonicalRecord/
CanonicalIntakeRecordService.cs:403` (`File.Exists`), `Retention/TenantDataControlService.cs:
192-200,663-690` (raw-mail purge), `Retention/LegacyAttachmentPurgeResolver.cs:96-121`,
`Platform/Lifecycle/TenantStoragePurge.cs:199-225` (tenant purge of legacy paths).

Other disk users that are NOT evidence and stay as they are: the watched-folder intake
itself (`FolderService` polls `Tenants/{bu}/Watched`, never provisioned in production — see
`docs/lead-ingestion-pilot/02-current-state-rtm.md:103`), `StorageCapacityHealthCheck`
(reports on `RootPath`), `WebRootPath` writes for user/product images
(`Controllers/UserController.cs:240,395`, `Repositories/ProductRepository.cs:97`), OCR temp.

## Proposal

### 1. One writer/reader component: `ILegacyDocumentStore`

`Infrastructure/Storage/LegacyDocumentStore.cs`. Flag
`EvidenceStorage:RouteLegacyWritersToObjectStore` (bool, default **false**).

- `StoreAttachmentAsync(bu, legacyFolder, fileName, bytes)` → `(FilePath, ContentSha256, Size)`
  - flag off: writes `{root}/{legacyFolder}/{fileName}` and returns the same relative path the
    writers compute today (`Uploads/{folder}/{file}`), digest **also** recorded (new;
    harmless, the column exists);
  - flag on: `IEvidenceObjectStorage.WriteImmutableAsync(bu, "attachments", sha256, ext)` and
    returns the object `StorageUri` + digest.
- `StoreRawMailAsync(bu, emlBytes)` — flag off: `{root}/Raw_Emails/{guid}.eml`; flag on:
  zone `raw-mail`, same key `EmailInquiryCaptureService` will write for the same bytes
  (content-addressed ⇒ the second write is a HEAD hit, not a duplicate).
- `OpenAsync(pathOrUri, sha256?)` / `ExistsAsync` — object URI ⇒ `OpenVerifiedReadAsync`
  with the digest taken from the row or parsed from the key (`.../sha256/xx/<sha>.<ext>`);
  disk path ⇒ `IFileStorage` (containment) or the absolute legacy path.
- `IsObjectUri(string)` — `scheme://` (s3, test-evidence) or a key under `Evidence/`.

**Key scheme.** `Evidence/tenants/{bu}/{zone}/sha256/{xx}/{sha}.{ext}` — the existing
`LocalEvidenceObjectStorage.BuildKey`. One new zone, **`legacy`**, is added to the
whitelist in `ValidateIdentity` (`IEvidenceObjectStorage.cs:412-433`), for the same reason
`raw-mail` was: the retention purge swaps `/quarantine/` ↔ `/cleared/`
(`Retention/EvidenceRetentionEligibility.cs:101-115`) and deletes both, and a lead
attachment is a compatibility copy that is NOT inspected — writing uninspected bytes into
`cleared` would be a lie, into `quarantine` would let a document purge delete the copy the
lead screen still lists. `legacy` matches neither swap arm. (Named `legacy` rather than
`attachments` because the same zone also receives the seven pre-ledger extraction inputs
under `Extraction/`, which are not attachments either.) The tenant purge sweeps by prefix
`Evidence/tenants/{bu}/` (`TenantStoragePurge`) and therefore covers it.

### 2. Callers

- The four writers call the component; the writers' cleanup-on-failure branches
  (`File.Delete`) become no-ops for object writes (the object is immutable and
  content-addressed; an orphan is impossible because the row is written after the object).
  Finding while converting: `FolderService.SaveAttachmentsAsync` has **no caller** — the
  watched folder was rerouted through the unified document queue and the legacy write is
  dead code. Converted anyway so the switch is complete; noted so nobody expects it to run.
- Per-document retention purge (`LegacyAttachmentPurgeResolver`) cannot delete an
  object-store attachment copy: `Attachments` records no object version and a versioned
  bucket reclaims nothing without one. Such rows are reported `OBJECT_STORE_COPY` and left
  to the tenant purge's prefix sweep (all versions). A version column is a follow-up
  migration, not this stream's one.
- `FileController.DownloadAttachment`: a new branch, before the legacy fallback — if the row
  carries a digest AND its path is an object URI, serve through `_evidenceStorage`
  regardless of parent type (parent authorisation still runs first). This is the branch the
  migrated rows and the flag-on writers land on.
- `EmailService` recovery, `RawEmailEvidenceReader`, `CanonicalIntakeRecordService` read
  through the component.
- `TenantDataControlService` (raw-mail purge): an object URI is the SAME object the
  assembly's `RawEvidenceUri` names, whose bytes are governed by the assembly purge; the
  ingest purge records the path as object-governed and does not attempt a disk delete.
  `LegacyAttachmentPurgeResolver` and `TenantStoragePurge` skip object URIs the same way.

### 3. One-off migration job: `LegacyEvidenceMigrationJob`

`Infrastructure/Storage/LegacyEvidenceMigrationJob.cs`, run by a hosted service when
`EvidenceStorage:LegacyMigration:Enabled=true` (default false), or directly from a test.
Runs while the app serves: pages of 100 rows, one row per `SaveChanges`, no table locks, no
exclusive claims (a row is skipped if it changed under us).

Scope, per row, idempotent and re-runnable:

| Rows | Source bytes | Verify | Write | Rewrite |
|---|---|---|---|---|
| `Attachments` with a disk path (not `://`, not `purged:`) and `ParentType='Lead'` | `IFileStorage.ResolvePath(FilePath)` (handles the `Uploads\` prefix and Windows separators) | sha256 of bytes; if the row has a digest it must match, else refuse (`HASH_MISMATCH`) | zone `attachments`, or the zone already in the path when it is an evidence key | `FilePath = StorageUri`, `ContentSha256 = sha` |
| `EmailIngests.RawEmailPath` disk path, `BytesPurgedOn IS NULL` (the tombstone trigger from `20260824140000` refuses a path on a purged row) | absolute path if inside the root, else `SOURCE_MISSING` | sha256 | zone `raw-mail` | `RawEmailPath = StorageUri` |
| `ExtractionJobs.StoragePath` under the root (the `local` evidence objects) | path | sha256 **must equal** `ContentHash` and the bound `source_documents.content_hash`, else refuse | same zone and key as on disk | `StoragePath = StorageUri` only. `source_documents.object_bucket/key/version` stay `local`: the `nexora_protect_source_document_identity` trigger (`Sql/02_functions.sql:3301-3306`) freezes them once Cleared. Reported as a follow-up needing a governed relocation migration; this stream authors no second migration. |

Already-migrated `Attachments` and `EmailIngests` rows (object URI + digest) are re-opened
with `OpenVerifiedReadAsync` and counted `VERIFIED`, so a second run proves the objects still
hash; `ExtractionJobs` rows already on a URI are skipped (the extraction pipeline verifies
them on every read). Missing files
(`/app/Uploads/...`, `D:\Sites\...`, the laptop path) are reported `SOURCE_MISSING` and the
row is left exactly as it was — their bytes are already gone and this job must not pretend
otherwise. The report (counts per outcome, first 50 refusals) is logged at Warning.

### 4. Config change that lets the disk go

After the job reports zero migratable rows and the flag is on: set
`Storage__EnforcePersistentMount=false` and `Storage__RootPath=/tmp/nexora/uploads` (the
watched-folder intake and the write probe still need a writable root), remove the `disk:`
block, redeploy. `LocalFileStorage` (`IFileStorage.cs:58-99`) then boots without the mount
check. `StorageCapacityHealthCheck` starts reporting `/tmp`, which is honest.

### 5. Non-root runtime in the Dockerfile, gated by a build arg

`mcr.microsoft.com/dotnet/aspnet:8.0` ships user `app` (uid 1654). Running as `app` is safe
only when every path the process writes is writable by uid 1654: `/app/wwwroot` (image
uploads; created and chowned in the image), `/tmp`, and the storage root. A Render disk is
mounted root-owned at runtime, so with the disk still attached the startup write probe fails
and the container will not boot — and because every merge to `main` auto-deploys
(`autoDeployTrigger: checksPass`), an unconditional `USER app` would turn a merge into an
outage rather than a loud-but-safe refusal. The Dockerfile therefore declares
`ARG NEXORA_RUNTIME_USER=root` in the runtime stage and ends with
`USER ${NEXORA_RUNTIME_USER}`; the mkdir/chown of `/app/wwwroot`, `/var/data/nexora` and
`/tmp/nexora` is unconditional (inert under a mount, correct without one). The cutover is
`docker build --build-arg NEXORA_RUNTIME_USER=app` — on Render, a dashboard Docker build arg —
set only after step 4 of the rollout below. The default image is byte-for-byte today's
runtime posture.

### 6. `render.yaml` reconciliation

Production runs `EvidenceStorage__Provider=S3` (bucket `NexoraBucket`, B2 S3 endpoint —
memory note 2026-08-16, confirmed by 311 `NexoraBucket` rows and 320 `s3://` job paths written
through 2026-09-02) and `DocumentInspection__Scanner__Provider=ClamAV` (313 documents
scanned by engine `ClamAV`, latest 2026-09-02 18:21 UTC; the BuiltIn engine last wrote a
verdict on 2026-08-06). The file says `Local` and `BuiltIn`. It is rewritten to what runs,
the ClamAV private service block from `docs/RUNBOOK-CLAMAV-RENDER.md` is restored,
credentials are `sync: false`, and the new keys from this stream are declared with their
safe defaults.

## Failure modes considered

- **Two stores, two truths.** The flag routes ALL four writers at once; there is no per-writer
  switch, so the estate cannot be split three ways. The reader accepts both shapes forever,
  because 79 + 324 rows will be migrated and 24 rows never can be.
- **Guard placement.** Digest verification is inside `OpenVerifiedReadAsync` (the read
  itself), not in the controller.
- **One-way trap.** Flag on → off is safe: rows written as URIs keep being readable through
  the URI branch. Rows written to disk while off are picked up by the job later.
- **Scale.** The job streams one file at a time; the largest legacy attachment is 4.5 MB;
  raw mail is capped by the mailbox.
- **Poison rows.** Every refusal is per-row, logged, and leaves the row untouched; the job
  never stops on one bad row.
- **Fixture shape.** Tests use rows with the production path shapes (`Uploads\RFQ_Attachments\...`
  with backslashes, `/var/data/...` absolute, `/app/Uploads/...` missing).

## Tests

- `LegacyDocumentStoreTests`: flag off writes to disk with today's relative path; flag on
  writes to the object store under `attachments` / `raw-mail` with the content-addressed key.
- Wiring: `ManualUploadService.ProcessUploadedFilesAsync` with the flag on produces an
  `Attachments` row whose `FilePath` is the object URI and whose bytes are in the fake store;
  the same for `FolderService` (watched folder) and `EmailService` (stranded-ingest recovery
  reads a URI-shaped `RawEmailPath`).
- `LegacyEvidenceMigrationJobTests` (PostgreSQL): seed the three row kinds with production
  path shapes, run twice; second run writes nothing new, every migrated object re-verifies,
  missing sources are reported and untouched.
- `AttachmentDownloadSecurityTests`: a Lead attachment with an object URI and digest and no
  source-document link is served through the object store; a tampered object is refused.

## Rollout / rollback

1. Merge with the flag off. No behaviour change.
2. Render: `EvidenceStorage__RouteLegacyWritersToObjectStore=true`. New writes go to S3.
3. Render: `EvidenceStorage__LegacyMigration__Enabled=true`, restart, read the report; repeat
   until `Migrated=0, Verified=N`. Set the key back to false.
4. `Storage__EnforcePersistentMount=false`, `Storage__RootPath=/tmp/nexora/uploads`, remove
   the disk. Only now set the Render build arg `NEXORA_RUNTIME_USER=app`; until then the image
   runs as root exactly as before.
5. Rollback of 2: set the flag false. Rollback of 4: re-attach the disk — its contents were
   never deleted by this stream (the job copies; it does not remove).
