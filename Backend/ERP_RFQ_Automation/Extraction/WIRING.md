# Extraction pipeline — WIRING (orchestrator checklist)

Implements the ADR-0003 core: **one document → one job → one lead**, durable DB-backed
queue with atomic `FOR UPDATE SKIP LOCKED` claim, bounded-concurrency worker pool, chunked
map/reduce extraction (no truncation, count-conservation asserts), structured-doc bypass to
the deterministic normalizer, and idempotent per-document persistence.

All code lives in `Extraction/` and references the DbContext via `context.Set<ExtractionJob>()`
/ `context.Set<TenantQueueState>()`, so nothing here edits existing files. The three steps
below are all that remain: **(1) model config + migration, (2) DI registration, (3) enqueue
call sites.** The project builds today; these steps make it run.

---

## 1. DbContext model config (`Models/ErpRfqAutomationContext.cs`, `OnModelCreating`)

Add these two `modelBuilder.Entity<>` blocks. Column names MUST stay equal to the property
names — the queue's raw SQL references `"ExtractionJobs"` and columns like `"ContentHash"`,
`"SchedulerTag"`, `"LeaseExpiresAt"` verbatim. **Enums MUST be stored as strings** (the SQL
compares against `'Pending'`, `'Leased'`, …).

```csharp
modelBuilder.Entity<ERP_RFQ_Automation.Extraction.ExtractionJob>(entity =>
{
    entity.ToTable("ExtractionJobs");
    entity.HasKey(e => e.Id);                       // long key -> identity by convention

    entity.Property(e => e.SourceType).HasConversion<string>().HasMaxLength(30).IsRequired();
    entity.Property(e => e.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
    entity.Property(e => e.ContentHash).HasMaxLength(64).IsRequired();
    entity.Property(e => e.StoragePath).HasMaxLength(1024).IsRequired();
    entity.Property(e => e.FileName).HasMaxLength(512);
    entity.Property(e => e.FileType).HasMaxLength(50);
    entity.Property(e => e.LeasedBy).HasMaxLength(200);
    entity.Property(e => e.LastError).HasMaxLength(4000);
    entity.Property(e => e.CreatedOn).HasDefaultValueSql("now()");
    entity.Property(e => e.UpdatedOn).HasDefaultValueSql("now()");

    // Idempotency: identical bytes for a tenant can be enqueued at most once.
    entity.HasIndex(e => new { e.BusinessUnitId, e.ContentHash })
          .IsUnique()
          .HasDatabaseName("UX_ExtractionJobs_BU_ContentHash");

    // Claim index: matches the ORDER BY (Priority DESC, SchedulerTag ASC) over eligible rows.
    entity.HasIndex(e => new { e.Status, e.NextAttemptAt, e.Priority, e.SchedulerTag })
          .HasDatabaseName("IX_ExtractionJobs_Claim");

    // Supports the per-tenant in-flight count in the claim CTE.
    entity.HasIndex(e => new { e.BusinessUnitId, e.Status })
          .HasDatabaseName("IX_ExtractionJobs_BU_Status");

    entity.HasIndex(e => e.BatchId).HasDatabaseName("IX_ExtractionJobs_BatchId");
});

modelBuilder.Entity<ERP_RFQ_Automation.Extraction.TenantQueueState>(entity =>
{
    entity.ToTable("TenantQueueStates");
    entity.HasKey(e => e.BusinessUnitId);
    entity.Property(e => e.BusinessUnitId).ValueGeneratedNever(); // tenant id, not generated
    entity.Property(e => e.Weight).HasDefaultValue(1.0);
});
```

Optionally (not required — `Set<T>()` already works) add DbSet properties for readability:

```csharp
public virtual DbSet<ERP_RFQ_Automation.Extraction.ExtractionJob> ExtractionJobs { get; set; }
public virtual DbSet<ERP_RFQ_Automation.Extraction.TenantQueueState> TenantQueueStates { get; set; }
```

Then create + apply the migration:

```bash
cd Backend/ERP_RFQ_Automation
dotnet ef migrations add AddExtractionPipeline
dotnet ef database update
```

> Note: `now()` returns `timestamptz`; all queue time comparisons are done with **C#-supplied
> `DateTime.UtcNow` parameters** (not `now()`), so behavior is timezone-independent and
> consistent with the app's `EnableLegacyTimestampBehavior` setting.

---

## 2. DI registration (`Program.cs`, before `builder.Build()`)

`ILLMService` and `ICanonicalRfqNormalizer` are already registered. Add:

```csharp
using ERP_RFQ_Automation.Extraction;

// Worker tuning (singleton). Bind from configuration if you like.
builder.Services.AddSingleton(new ExtractionWorkerOptions
{
    WorkerCount = 4,
    MaxConcurrentLlmCalls = 8,
    PerTenantConcurrencyCap = 4,
    LeaseDuration = TimeSpan.FromMinutes(5),
    IdlePollDelay = TimeSpan.FromSeconds(2)
});

// Queue + extraction services (scoped: they depend on the scoped DbContext / ILLMService).
builder.Services.AddScoped<IExtractionQueue, ExtractionQueue>();
builder.Services.AddScoped<IChunkedExtractionService, ChunkedExtractionService>();
builder.Services.AddScoped<ILeadPersister, LeadPersister>();

// Document reader: the DefaultExtractionDocumentReader is a text/CSV baseline.
// Replace with a production reader (see §4 note) that reuses the existing
// PDF/OCR/DOCX/XLSX extraction and real structured detection.
builder.Services.AddScoped<IExtractionDocumentReader, DefaultExtractionDocumentReader>();

// The worker pool (singleton hosted service; resolves the scoped services per job
// via IServiceScopeFactory, and caps concurrent LLM calls process-wide).
builder.Services.AddHostedService<ExtractionWorker>();
```

The `ExtractionWorker` is safe alongside the existing `EmailBackgroundService`; the host is
already configured with `BackgroundServiceExceptionBehavior.Ignore`, and each worker loop is
independently crash-isolated.

---

## 3. Enqueue call sites — FAN-OUT, one job per file (never merge)

The upload/email paths must do only cheap, reliable work: persist each file immutably, enqueue
**one job per file** sharing a single `BatchId`, and return `202 Accepted` + the batch id. No
parsing or LLM on the request/poll thread. This directly replaces the document-merging and
truncation defects.

### 3a. ManualUploadService / ManualUploadController

Inject `IExtractionQueue` and add a fan-out method (do **not** modify the existing
`ProcessUploadedFilesAsync`; add a new one and point the controller at it):

```csharp
// New method on ManualUploadService (inject IExtractionQueue _queue in the ctor).
public async Task<(Guid BatchId, int Enqueued, int Duplicates)> EnqueueUploadedFilesAsync(
    List<IFormFile> files, long businessUnitId, CancellationToken ct = default)
{
    var batchId = Guid.NewGuid();
    int enqueued = 0, duplicates = 0;

    foreach (var file in files)                       // ONE JOB PER FILE — no concatenation
    {
        if (file.Length == 0) continue;
        var ext = Path.GetExtension(file.FileName).ToLowerInvariant();

        // Read bytes once (hash + immutable store).
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        var bytes = ms.ToArray();

        // Persist immutably (content-addressed keeps identical bytes from duplicating on disk).
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var storagePath = Path.Combine(_attachmentPath, $"{hash}{ext}");
        if (!File.Exists(storagePath)) await File.WriteAllBytesAsync(storagePath, bytes, ct);

        var result = await _queue.EnqueueAsync(new EnqueueExtractionRequest
        {
            BusinessUnitId = businessUnitId,
            SourceType     = ExtractionSourceType.ManualUpload,
            StoragePath    = storagePath,
            FileName       = file.FileName,
            FileType       = ext.TrimStart('.'),
            Content        = bytes,          // queue computes SHA-256 + enforces idempotency
            ContentHash    = hash,           // (optional; supplied here since we already hashed)
            BatchId        = batchId,
            Priority       = 10              // interactive upload outranks bulk backfills
        }, ct);

        if (result.Outcome == EnqueueOutcome.Enqueued) enqueued++; else duplicates++;
    }
    return (batchId, enqueued, duplicates);
}
```

Controller (`ManualUploadController.UploadFiles`) returns `202` instead of blocking:

```csharp
var (batchId, enqueued, duplicates) = await _manualUploadService.EnqueueUploadedFilesAsync(files, targetBUId);
return Accepted(new { success = true, batchId, enqueued, duplicates,
                      message = "Files accepted; extraction is running in the background." });
```

### 3b. Email path (EmailService / EmailController)

In the mail-poll loop, after the durable `EmailIngest` + raw `.eml` is saved, iterate the
message's attachments and enqueue **one job per attachment** (share one `BatchId` per email),
using `SourceType = ExtractionSourceType.Email`. Remove the inline synchronous extraction from
the poll loop — the worker pool now does it. Persist each attachment's bytes to a
content-addressed path and pass them as `Content` so the queue hashes + de-dupes.

### Progress / status

Query `ExtractionJobs` by `BatchId` for batch progress (counts by `Status`); a finished batch
has no `Pending`/`Leased`/`Extracting`/`Persisting` rows. `DeadLetter` rows and leads whose
`ParseStatus = "NeedsReview"` feed the review-queue UX (production-hardening phase).

---

## Behavior guarantees delivered

- **No document merging** — fan-out enqueues one job (→ one Lead) per file.
- **No silent truncation** — chunked map/reduce unions every line-item chunk; `ChunkedExtractionService`
  asserts `Σ chunk items == parsed row count` and routes any mismatch/low-confidence/partial-chunk
  failure to a `NeedsReview` outcome (still persisted, never dropped).
- **Idempotent + race-safe** — unique `(BusinessUnitId, ContentHash)`; claim via `FOR UPDATE OF j
  SKIP LOCKED`; expired leases reclaimable (crash-safe).
- **Bounded concurrency + fairness** — `WorkerCount` loops, process-wide LLM `SemaphoreSlim`,
  per-tenant concurrency cap in the claim SQL, weighted-fair ordering by `SchedulerTag`.
- **Poison-doc isolation** — per-job try/catch → `FailAsync` reschedules with exponential backoff,
  dead-letters after `MaxAttempts`; the batch never fails as a unit and no slow doc blocks others.
- **Refusal ≠ failure** — a failure caused by a DECISION rather than a condition sets
  `ChunkedExtractionOutcome.PermanentFailure` and goes to `FailPermanentlyAsync` on attempt 1.
  Today that is exactly one case: an external AI provider the tenant has not authorized. The gate
  is seeded FALSE for every tenant and cannot change between attempts, so retrying it bought an
  hour of backoff and no new information. It carries the `EXTRACTION_AI_NOT_AUTHORIZED` marker,
  classifies as the `AI_NOT_AUTHORIZED` dead-letter category with an operator action naming what
  to switch on, and marks the intake occurrence `IngestionOutcomeState.AI_NOT_AUTHORIZED`. Nothing
  about the gate itself is weakened — zero bytes still leave the boundary.
- **Structured bypass** — `IsStructured` docs route to `ICanonicalRfqNormalizer` (deterministic,
  no LLM), the biggest throughput/cost lever.
```
