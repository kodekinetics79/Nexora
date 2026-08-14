using System;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// Where a job originated. Persisted as text (see WIRING.md model config) so the
/// stored value is human-readable and stable across enum reordering.
/// </summary>
public enum ExtractionSourceType
{
    Email,
    ManualUpload,
    ExcelTemplate,
    /// <summary>Watched-folder ingestion (SEC / Aramco / shared lead folders).</summary>
    Folder
}

/// <summary>
/// Durable state machine for a single-document extraction job.
///   Pending -> Leased -> Extracting -> Persisting -> Succeeded
///   (any transient failure) -> Pending (rescheduled with backoff)
///   (Attempts >= MaxAttempts) -> DeadLetter
///   (duplicate content hash on enqueue) -> Duplicate
/// Persisted as text so the queue's raw SQL can compare against literals like 'Pending'.
/// </summary>
public enum ExtractionStatus
{
    Pending,
    Leased,
    Extracting,
    Persisting,
    Succeeded,
    Failed,
    DeadLetter,
    Duplicate
}

/// <summary>
/// One row == one document == one unit of work. NEVER merge documents into a
/// single job (see ADR-0003): a batch of 1,000 files fans out to 1,000 jobs, each
/// producing its own Lead/RFQ. The DB table backs the outbox, retry, dead-letter,
/// idempotency and lease semantics with no external infrastructure.
///
/// Referenced from the DbContext via <c>context.Set&lt;ExtractionJob&gt;()</c>; the
/// orchestrator adds the <c>modelBuilder.Entity&lt;ExtractionJob&gt;()</c> config and
/// (optionally) a DbSet — see WIRING.md.
/// </summary>
public sealed class ExtractionJob
{
    public long Id { get; set; }

    /// <summary>The immutable Stage 1 receipt that owns this retryable unit of work.</summary>
    public long? SourceDocumentOccurrenceId { get; set; }

    /// <summary>
    /// THE email inquiry component this job was created for, where it was.
    ///
    /// <para>Ownership used to be inferred from three places that could disagree: a nullable
    /// <c>ExtractionJobId</c> written back onto the component, a
    /// <see cref="SourceDocumentOccurrenceId"/>, and a best-effort metadata sidecar. A
    /// best-effort hint must never authorize a tenant mutation, and two nullable back-references
    /// pointing at each other are not an ownership model. This column is the single authority,
    /// carried on the job itself, backed by a composite foreign key that includes the tenant so
    /// it cannot name another tenant's component.</para>
    ///
    /// <para>Null for every non-email job — manual upload, folder watch, API — which is what
    /// keeps those paths on their existing per-document behaviour.</para>
    /// </summary>
    public long? EmailInquiryComponentId { get; set; }

    /// <summary>Groups all jobs fanned out from one upload / poll batch (for progress + reporting).</summary>
    public Guid BatchId { get; set; }

    /// <summary>Tenant key. Drives the per-tenant concurrency cap and weighted-fair ordering.</summary>
    public long BusinessUnitId { get; set; }

    public ExtractionSourceType SourceType { get; set; }

    /// <summary>
    /// SHA-256 of the immutable file bytes, lower-case hex. Idempotency key together
    /// with BusinessUnitId (unique index). Re-enqueue of identical bytes -> Duplicate.
    /// </summary>
    public string ContentHash { get; set; } = null!;

    /// <summary>Path/URI to the immutably persisted source file (content-addressed on disk/blob).</summary>
    public string StoragePath { get; set; } = null!;

    /// <summary>Original client file name (diagnostics + attachment naming). Optional.</summary>
    public string? FileName { get; set; }

    /// <summary>Detected file type/extension (e.g. "pdf", "xlsx"). Optional; hints structured-vs-unstructured routing.</summary>
    public string? FileType { get; set; }

    public ExtractionStatus Status { get; set; } = ExtractionStatus.Pending;

    /// <summary>Higher runs first. Interactive uploads can outrank bulk backfills.</summary>
    public int Priority { get; set; }

    /// <summary>
    /// Weighted-fair-queuing virtual finish tag, assigned at enqueue from the tenant's
    /// <see cref="TenantQueueState"/>. Lower == served earlier. Ties broken by CreatedOn.
    /// </summary>
    public double SchedulerTag { get; set; }

    public int Attempts { get; set; }

    public int MaxAttempts { get; set; } = 5;

    /// <summary>Earliest time the job may be claimed again (backoff gate). UTC.</summary>
    public DateTime NextAttemptAt { get; set; }

    /// <summary>Worker id currently holding the lease, or null.</summary>
    public string? LeasedBy { get; set; }

    /// <summary>Lease expiry (UTC). A job Leased/Extracting/Persisting past this time is reclaimable (crash-safe).</summary>
    public DateTime? LeaseExpiresAt { get; set; }

    public string? LastError { get; set; }

    /// <summary>Id of the Lead produced on success (for traceability / dedup).</summary>
    public long? ResultLeadId { get; set; }

    public DateTime CreatedOn { get; set; }

    public DateTime UpdatedOn { get; set; }
}

/// <summary>
/// Per-tenant scheduling state for weighted-fair queuing (WFQ). One row per
/// BusinessUnitId. <see cref="LastVTime"/> is the tenant's virtual clock; each
/// enqueued job advances it by <c>1 / Weight</c> so heavier-weighted tenants get
/// proportionally more service without starving lighter ones. <see cref="InFlight"/>
/// is an advisory counter (the authoritative per-tenant concurrency cap is enforced
/// in the claim SQL by counting live leases).
/// </summary>
public sealed class TenantQueueState
{
    /// <summary>Primary key == tenant id.</summary>
    public long BusinessUnitId { get; set; }

    /// <summary>Advisory count of jobs currently leased/extracting/persisting for this tenant.</summary>
    public int InFlight { get; set; }

    /// <summary>Tenant virtual clock (seconds). Monotonic; only ever moves forward.</summary>
    public double LastVTime { get; set; }

    /// <summary>Relative service weight (default 1.0). Higher == more throughput share.</summary>
    public double Weight { get; set; } = 1.0;
}
