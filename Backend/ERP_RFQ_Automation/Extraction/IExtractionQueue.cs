using System;
using System.Threading;
using System.Threading.Tasks;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>Result of an enqueue attempt.</summary>
public enum EnqueueOutcome
{
    /// <summary>A new job row was created and is Pending.</summary>
    Enqueued,
    /// <summary>Identical content (same BusinessUnitId + ContentHash) was already enqueued/processed.</summary>
    Duplicate
}

/// <summary>
/// Request to enqueue exactly ONE document. The ingest path fans out — one request
/// per uploaded file — and must NOT merge files. Persist the file immutably first,
/// then enqueue with its <see cref="StoragePath"/>.
/// </summary>
public sealed class EnqueueExtractionRequest
{
    public long BusinessUnitId { get; init; }
    public ExtractionSourceType SourceType { get; init; }

    /// <summary>Path/URI of the already-persisted immutable file.</summary>
    public string StoragePath { get; init; } = null!;

    public string? FileName { get; init; }
    public string? FileType { get; init; }

    /// <summary>
    /// Raw file bytes used to compute the SHA-256 content hash. Provide this OR a
    /// precomputed <see cref="ContentHash"/>. Passing bytes keeps hashing in one place.
    /// </summary>
    public byte[]? Content { get; init; }

    /// <summary>Precomputed lower-case hex SHA-256. Optional if <see cref="Content"/> is supplied.</summary>
    public string? ContentHash { get; init; }

    /// <summary>Shared across all files of one upload/poll batch. A new Guid is used if null.</summary>
    public Guid? BatchId { get; init; }

    public int Priority { get; init; }

    public int MaxAttempts { get; init; } = 5;
}

public sealed class EnqueueResult
{
    public long JobId { get; init; }
    public Guid BatchId { get; init; }
    public string ContentHash { get; init; } = null!;
    public EnqueueOutcome Outcome { get; init; }
    public ExtractionStatus? ExistingStatus { get; init; }
}

/// <summary>
/// Durable, DB-backed job queue with an atomic, race-safe claim. Idempotent by
/// content hash. All transitions are single SQL statements so N workers take
/// disjoint jobs and a crashed worker's lease is reclaimable.
/// </summary>
public interface IExtractionQueue
{
    /// <summary>
    /// Idempotently enqueue one document. Computes SHA-256 (from Content when the hash
    /// is not supplied) and inserts a Pending job; a duplicate (BusinessUnitId,
    /// ContentHash) short-circuits to <see cref="EnqueueOutcome.Duplicate"/>.
    /// </summary>
    Task<EnqueueResult> EnqueueAsync(EnqueueExtractionRequest request, CancellationToken ct = default);

    /// <summary>
    /// Atomically claim the single best-eligible Pending job for <paramref name="workerId"/>,
    /// respecting the per-tenant concurrency cap and ordering by Priority, then WFQ
    /// SchedulerTag. Uses <c>FOR UPDATE ... SKIP LOCKED</c> so concurrent workers never
    /// collide. Returns null when nothing is currently claimable. Increments Attempts
    /// and sets the lease.
    /// </summary>
    Task<ExtractionJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, int perTenantCap, CancellationToken ct = default);

    /// <summary>Extend the lease only for the matching worker and claim generation.</summary>
    Task<bool> RenewLeaseAsync(long jobId, string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default);

    /// <summary>Advance the matching claim generation to Extracting or Persisting.</summary>
    Task<bool> SetStatusAsync(long jobId, string workerId, int leaseAttempt, ExtractionStatus status, CancellationToken ct = default);

    /// <summary>Complete the matching claim generation and clear its lease.</summary>
    Task<bool> CompleteAsync(long jobId, string workerId, int leaseAttempt, long resultLeadId, CancellationToken ct = default);

    /// <summary>
    /// Record a failure. Reschedules the job to Pending with exponential backoff, or
    /// transitions it to DeadLetter once Attempts &gt;= MaxAttempts. Poison-doc isolation:
    /// the failing document never blocks or fails the rest of the batch.
    /// </summary>
    Task<bool> FailAsync(long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default);
}
