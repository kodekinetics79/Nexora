using System;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Entitlements;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace ERP_RFQ_Automation.Extraction;

/// <summary>
/// PostgreSQL-backed implementation of <see cref="IExtractionQueue"/>. The atomic
/// claim uses <c>FOR UPDATE ... SKIP LOCKED</c> raw SQL over the context's connection
/// so N workers take disjoint jobs, an expired lease is reclaimable (crash-safe), and
/// exactly-once is guaranteed by the unique (BusinessUnitId, ContentHash) index.
///
/// Scoped service — resolve one per worker loop / request. The entity types are read
/// through <c>context.Set&lt;T&gt;()</c>; the orchestrator registers the model config
/// (table name, text enum conversions, unique + claim indexes) per WIRING.md.
///
/// R-REL-01 (poison-pill isolation). <c>trg_release01b_intake_before_claim_guard</c>
/// refuses any transition to <c>Leased</c> whose durable intake occurrence is not
/// <c>Queued</c>/<c>Retryable</c> (or reclaimably <c>Processing</c>), raising 23514. That
/// invariant is correct and is deliberately NOT weakened here. What was wrong was the
/// queue's reaction to it: the candidate CTE never looked at the occurrence, so the same
/// unclaimable row was re-selected every poll, and the raised transaction rolled back the
/// <c>Attempts</c> increment, so it could never exhaust and never dead-letter — one row
/// starved every tenant, forever. Two changes close that:
///   (1) the claim now selects only jobs the guard would accept, so a poisoned row never
///       blocks the rest of the queue; and
///   (2) a poisoned row at the head of the queue is classified once and moved directly to
///       the governed dead-letter workflow. Retrying a structurally invalid row cannot make
///       its durable lineage valid and only creates an invariant-violation loop. A savepoint
///       around the claim keeps a backstop classification durable even when the trigger (or
///       any future invariant) still refuses a job the CTE believed claimable.
/// </summary>
public sealed class ExtractionQueue : IExtractionQueue
{
    private readonly ErpRfqAutomationContext _context;
    private readonly ILogger<ExtractionQueue> _log;
    private readonly ITenantContext? _tenantContext;
    private readonly IEntitlementService? _entitlements;
    private readonly ERP_RFQ_Automation.Platform.Hardening.NexoraMetrics? _metrics;

    public ExtractionQueue(
        ErpRfqAutomationContext context,
        ILogger<ExtractionQueue> log,
        ERP_RFQ_Automation.Platform.Hardening.NexoraMetrics? metrics = null)
    {
        _context = context;
        _log = log;
        _metrics = metrics;
    }

    public ExtractionQueue(
        ErpRfqAutomationContext context,
        ILogger<ExtractionQueue> log,
        ITenantContext tenantContext,
        IEntitlementService? entitlements = null,
        ERP_RFQ_Automation.Platform.Hardening.NexoraMetrics? metrics = null)
        : this(context, log, metrics)
    {
        _tenantContext = tenantContext;
        _entitlements = entitlements;
    }

    // All entity columns default to their property names (case-sensitive, quoted).
    private const string ReturningColumns =
        "j.\"Id\", j.\"SourceDocumentOccurrenceId\", j.\"BatchId\", j.\"BusinessUnitId\", j.\"SourceType\", j.\"ContentHash\", " +
        "j.\"StoragePath\", j.\"FileName\", j.\"FileType\", j.\"Status\", j.\"Priority\", " +
        "j.\"SchedulerTag\", j.\"Attempts\", j.\"MaxAttempts\", j.\"NextAttemptAt\", " +
        "j.\"LeasedBy\", j.\"LeaseExpiresAt\", j.\"LastError\", j.\"ResultLeadId\", " +
        "j.\"CreatedOn\", j.\"UpdatedOn\"";

    // The claim resolves inside a bounded head-of-line window taken in claim order. A job
    // that cannot be claimed only starves the queue while it sits AHEAD of claimable work,
    // and every blocked row in the window is quarantined in the same statement, so it leaves
    // the runnable queue immediately instead of holding it.
    //
    // The window is what keeps the poll cost independent of queue depth: the occurrence is
    // probed for at most this many rows, not for every eligible job (measured on a 20k-job
    // queue: ~10ms with the window, ~95ms when the probe is pushed into the scan predicate).
    // No new index is required — the probe is a single lookup on the existing
    // ak_source_document_occurrences_tenant_id (business_unit_id, id) unique key.
    //
    // Must stay comfortably above ExtractionWorkerOptions.WorkerCount: concurrent workers
    // SKIP LOCKED past each other's rows inside the window.
    private const int HeadOfLineLookahead = 32;

    // Per-tenant concurrency entitlement (P0): the effective cap for a tenant is its
    // plan's MaxConcurrentExtractionJobs (resolved via platform.Tenants →
    // platform.Plans on PrimaryBusinessUnitId, inside the same atomic statement), and
    // @cap — the ExtractionWorkerOptions.PerTenantConcurrencyCap config default —
    // remains the fallback for tenants without a plan or without a Tenant row.
    private const string SchedulingCtes = @"
plan_caps AS (
    SELECT t.""PrimaryBusinessUnitId"" AS buid,
           MAX(p.""MaxConcurrentExtractionJobs"") AS cap
    FROM platform.""Tenants"" t
    JOIN platform.""Plans"" p ON p.""Id"" = t.""PlanId""
    WHERE t.""PrimaryBusinessUnitId"" IS NOT NULL
    GROUP BY t.""PrimaryBusinessUnitId""
),
blocked_tenants AS (
    -- A Provisioning tenant has not passed authoritative activation and may not consume resources.
    -- Past-due, suspended, or archived tenants' queued jobs likewise must not be claimed. Legacy BUs
    -- without a platform.Tenants row are unaffected (fail open per LEDGER contract).
    SELECT t.""PrimaryBusinessUnitId"" AS buid
    FROM platform.""Tenants"" t
    WHERE t.""PrimaryBusinessUnitId"" IS NOT NULL
      AND t.""Status"" IN ('Provisioning','PastDue','Suspended','Archived')
),
inflight AS (
    SELECT ""BusinessUnitId"" AS buid, COUNT(*) AS cnt
    FROM ""ExtractionJobs""
    WHERE ""Status"" IN ('Leased','Extracting','Persisting')
      AND ""LeaseExpiresAt"" > @now
    GROUP BY ""BusinessUnitId""
)";

    // Everything except the intake-occurrence predicate that decides whether a job may be
    // worked right now. Shared verbatim by the claim, the head-of-line sweep and the
    // refusal recorder so the three can never disagree about which job is next.
    private const string EligibleJobs = @"
    FROM ""ExtractionJobs"" j
    LEFT JOIN inflight f ON f.buid = j.""BusinessUnitId""
    LEFT JOIN plan_caps pc ON pc.buid = j.""BusinessUnitId""
    LEFT JOIN blocked_tenants bt ON bt.buid = j.""BusinessUnitId""
    WHERE (
            j.""Status"" = 'Pending'
            OR (j.""Status"" IN ('Leased','Extracting','Persisting')
                AND (j.""LeaseExpiresAt"" IS NULL OR j.""LeaseExpiresAt"" <= @now))
          )
      AND j.""NextAttemptAt"" <= @now
      AND j.""Attempts"" < j.""MaxAttempts""
      AND bt.buid IS NULL
      -- P1-A3: a plan cap of 0 is 'not configured' → fall back to @cap, matching
      -- EntitlementService's <= 0 semantics (NULLIF), never a silent clamp to 1.
      AND COALESCE(f.cnt, 0) < GREATEST(COALESCE(NULLIF(pc.cap, 0), @cap), 1)";

    // Mirror of nexora_release01b_intake_before_claim_guard (deployed body:
    // Migrations/20260725035352_Release01CTransactionalIntakeHardening.cs:173-188). When this
    // is false the trigger raises 23514 on the transition to 'Leased' and the whole claim
    // rolls back — so the scheduler must not pick such a row. j is the pre-update (OLD) row
    // the trigger sees. A null occurrence link is invalid too: PostgreSQL's NOT EXISTS guard
    // rejects it, so the scheduler must quarantine it before attempting the transition.
    private const string IntakeAllowsClaim = @"(
            j.""SourceDocumentOccurrenceId"" IS NOT NULL
            AND EXISTS (
                SELECT 1
                FROM source_document_occurrences o
                WHERE o.business_unit_id = j.""BusinessUnitId""
                  AND o.id = j.""SourceDocumentOccurrenceId""
                  AND o.extraction_job_id = j.""Id""
                  AND (o.intake_status IN ('Queued','Retryable')
                    OR (o.intake_status = 'Processing'
                        AND j.""Status"" IN ('Leased','Extracting','Persisting')
                        AND (j.""LeaseExpiresAt"" IS NULL OR j.""LeaseExpiresAt"" <= @now)))))";

    private const string ClaimOrder =
        @"ORDER BY j.""Priority"" DESC, j.""SchedulerTag"" ASC, j.""CreatedOn"" ASC";

    // Bounded head-of-line window in claim order, tagged with claimability. Claimability is
    // projected (not filtered) so the occurrence is probed only for the rows that survive the
    // LIMIT — one scan and one top-N sort serve both the sweep and the claim. MATERIALIZED is
    // explicit so the two references can never become two scans.
    private static readonly string HeadCte = $@"head AS MATERIALIZED (
    SELECT j.""Id"" AS job_id, {IntakeAllowsClaim} AS intake_allows_claim
    {EligibleJobs}
    {ClaimOrder}
    LIMIT {HeadOfLineLookahead}
)";

    // Atomic weighted-fair claim. Live (non-expired) leases per tenant are counted so a
    // tenant already at its cap is skipped; among eligible jobs the highest Priority then
    // the lowest WFQ SchedulerTag wins. Expired leases (crashed workers) are reclaimable.
    //
    // R-REL-01: the claim is resolved inside `head`, and `candidate` takes the best row the
    // intake guard would accept — so an unclaimable job can no longer head-of-line block
    // every tenant. `quarantined` classifies each unclaimable row in the window with a
    // stable reason in the SAME statement, so it enters the operator's governed dead-letter
    // queue rather than being skipped in silence. The two sets are disjoint by construction (claimable vs not),
    // so they never contend for a row, and both use SKIP LOCKED so workers never wait.
    private static readonly string ClaimSql = $@"
WITH {SchedulingCtes},
exhausted AS (
    UPDATE ""ExtractionJobs""
    SET ""Status"" = 'DeadLetter',
        ""LeasedBy"" = NULL,
        ""LeaseExpiresAt"" = NULL,
        ""LastError"" = COALESCE(""LastError"", 'Lease expired after final attempt.'),
        ""UpdatedOn"" = @now
    WHERE ""Attempts"" >= ""MaxAttempts""
      AND (
            ""Status"" = 'Pending'
            OR (""Status"" IN ('Leased','Extracting','Persisting')
                AND (""LeaseExpiresAt"" IS NULL OR ""LeaseExpiresAt"" <= @now))
          )
    RETURNING ""Id""
),
{HeadCte},
blocked AS (
    -- EVERY unclaimable row in the window, so a run of them cannot hold the window: each is
    -- row-locked and leaves runnable eligibility through the quarantine update below.
    SELECT j.""Id""
    FROM ""ExtractionJobs"" j
    JOIN head h ON h.job_id = j.""Id"" AND NOT h.intake_allows_claim
    FOR UPDATE OF j SKIP LOCKED
),
quarantined AS (
    -- A structural lineage violation is not a transient extraction failure. Classify it
    -- ONCE and move it immediately to the existing governed DLQ. The status predicate that
    -- built `head`, plus this row lock, makes the transition idempotent across replicas.
    UPDATE ""ExtractionJobs"" j
    SET ""Status"" = 'DeadLetter',
        ""LeasedBy"" = NULL,
        ""LeaseExpiresAt"" = NULL,
        ""LastError"" = left(
            CASE
              WHEN j.""SourceDocumentOccurrenceId"" IS NULL
                THEN '[EXTRACTION_INTAKE_OCCURRENCE_MISSING] No durable intake occurrence is linked.'
              WHEN NOT EXISTS (
                SELECT 1 FROM source_document_occurrences o
                WHERE o.business_unit_id = j.""BusinessUnitId""
                  AND o.id = j.""SourceDocumentOccurrenceId"")
                THEN '[EXTRACTION_INTAKE_OCCURRENCE_MISSING] The linked durable intake occurrence does not exist.'
              WHEN EXISTS (
                SELECT 1 FROM source_document_occurrences o
                WHERE o.business_unit_id = j.""BusinessUnitId""
                  AND o.id = j.""SourceDocumentOccurrenceId""
                  AND o.extraction_job_id IS DISTINCT FROM j.""Id"")
                THEN '[EXTRACTION_INTAKE_JOB_LINK_MISMATCH] The durable intake occurrence is linked to a different queue job.'
              ELSE '[EXTRACTION_INTAKE_STATUS_NOT_QUEUE_ELIGIBLE] The durable intake occurrence is not Queued or Retryable.'
            END
            || ' QueueId=' || j.""Id""
            || '; TenantId=' || j.""BusinessUnitId""
            || '; OccurrenceId=' || COALESCE(j.""SourceDocumentOccurrenceId""::text, '(none)')
            || '; CorrelationId=' || j.""BatchId""::text || '.', 4000),
        ""UpdatedOn"" = @now
    FROM blocked b
    WHERE j.""Id"" = b.""Id""
    RETURNING j.""Id"", j.""BusinessUnitId"", j.""SourceDocumentOccurrenceId"", j.""BatchId""
),
candidate AS (
    SELECT j.""Id""
    FROM ""ExtractionJobs"" j
    JOIN head h ON h.job_id = j.""Id"" AND h.intake_allows_claim
    {ClaimOrder}
    FOR UPDATE OF j SKIP LOCKED
    LIMIT 1
)
UPDATE ""ExtractionJobs"" j
SET ""Status"" = 'Leased',
    ""LeasedBy"" = @worker,
    ""LeaseExpiresAt"" = @leaseExpiry,
    ""Attempts"" = j.""Attempts"" + 1,
    ""UpdatedOn"" = @now
FROM candidate c
WHERE j.""Id"" = c.""Id""
  -- Re-check mutable eligibility on the tuple UPDATE actually locks. A candidate CTE can
  -- be evaluated just before another short transaction commits its lease; without this
  -- predicate PostgreSQL's EvalPlanQual may update that now-live lease a second time.
  AND (j.""Status"" = 'Pending'
       OR (j.""Status"" IN ('Leased','Extracting','Persisting')
           AND (j.""LeaseExpiresAt"" IS NULL OR j.""LeaseExpiresAt"" <= @now)))
  AND j.""NextAttemptAt"" <= @now
  AND j.""Attempts"" < j.""MaxAttempts""
RETURNING {ReturningColumns};";

    // Backstop for the case the head window cannot model: the database refused a claim the
    // scheduler believed legal (23514). Re-selects from the same window on the same
    // deterministic order — rows the sweep already doubts first, then the exact row the claim
    // would have taken — and quarantines it once. Runs only after a
    // refusal, never on the hot path.
    private static readonly string RecordRefusedClaimSql = $@"
WITH {SchedulingCtes},
{HeadCte},
target AS (
    SELECT j.""Id""
    FROM ""ExtractionJobs"" j
    JOIN head h ON h.job_id = j.""Id""
    ORDER BY h.intake_allows_claim ASC, j.""Priority"" DESC, j.""SchedulerTag"" ASC, j.""CreatedOn"" ASC
    FOR UPDATE OF j SKIP LOCKED
    LIMIT 1
)
UPDATE ""ExtractionJobs"" j
SET ""Attempts"" = j.""Attempts"" + 1,
    ""Status"" = 'DeadLetter',
    ""LeasedBy"" = NULL,
    ""LeaseExpiresAt"" = NULL,
    ""LastError"" = left(@error, 4000),
    ""UpdatedOn"" = @now
FROM target t
WHERE j.""Id"" = t.""Id""
RETURNING j.""Id"", j.""BusinessUnitId"", j.""Status"", j.""Attempts"", j.""MaxAttempts"";";

    private const string ClaimSavepoint = "extraction_claim";

    public async Task<EnqueueResult> EnqueueAsync(EnqueueExtractionRequest request, CancellationToken ct = default)
    {
        if (request is null) throw new ArgumentNullException(nameof(request));
        if (string.IsNullOrWhiteSpace(request.StoragePath))
            throw new ArgumentException("StoragePath is required.", nameof(request));

        var hash = request.ContentHash;
        if (string.IsNullOrWhiteSpace(hash))
        {
            if (request.Content is null)
                throw new ArgumentException("Either Content or ContentHash must be supplied.", nameof(request));
            hash = ComputeSha256(request.Content);
        }
        hash = hash!.ToLowerInvariant();

        var jobs = _context.Set<ExtractionJob>();
        var tenants = _context.Set<TenantQueueState>();
        var batchId = request.BatchId ?? Guid.NewGuid();

        // Bounded retry: the DB unique index is the real idempotency guard; a retry also
        // covers the rare race where two enqueues create the tenant's WFQ state row at once.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var existing = await jobs.AsNoTracking()
                .Where(j => j.BusinessUnitId == request.BusinessUnitId
                    && (request.SourceDocumentOccurrenceId.HasValue
                        ? j.SourceDocumentOccurrenceId == request.SourceDocumentOccurrenceId
                        : j.SourceDocumentOccurrenceId == null && j.ContentHash == hash))
                .Select(j => new { j.Id, j.Status })
                .FirstOrDefaultAsync(ct);
            if (existing is not null)
                return new EnqueueResult
                {
                    JobId = existing.Id, BatchId = batchId, ContentHash = hash,
                    Outcome = EnqueueOutcome.Duplicate, ExistingStatus = existing.Status
                };

            // Plan entitlement (P0): monthly document quota, checked AFTER the duplicate
            // short-circuit (re-submitting known bytes consumes no quota) and BEFORE the
            // insert. Counts this tenant's jobs created since the first of the current
            // UTC month; the ~60s-cached plan resolution means no per-enqueue platform
            // query. No plan / no Tenant row → unlimited (contracted fail-open).
            if (_entitlements is not null)
            {
                var quota = await _entitlements.CheckDocumentQuotaAsync(request.BusinessUnitId, ct);
                if (!quota.Allowed)
                {
                    _log.LogWarning(
                        "Enqueue denied for tenant {BusinessUnitId}: monthly document quota reached ({Used}/{Limit}).",
                        request.BusinessUnitId, quota.Current, quota.Limit);
                    throw new DocumentQuotaExceededException(request.BusinessUnitId, quota);
                }
            }

            // WFQ share weight comes from the tenant's plan when one exists (heavier
            // plan → larger scheduling share), else the 1.0 default. P2-A6: the weight
            // is refreshed from the plan on EVERY enqueue — not only when the state row
            // is first created — so a plan change takes effect within the ~60s plan
            // cache window instead of being frozen at the tenant's first enqueue.
            var planWeight = _entitlements is null
                ? 1.0
                : await _entitlements.GetQueueWeightAsync(request.BusinessUnitId, 1.0, ct);
            if (planWeight <= 0) planWeight = 1.0;

            var state = await tenants.FindAsync(new object[] { request.BusinessUnitId }, ct);
            if (state is null)
            {
                state = new TenantQueueState
                {
                    BusinessUnitId = request.BusinessUnitId,
                    Weight = planWeight,
                    LastVTime = 0,
                    InFlight = 0
                };
                tenants.Add(state);
            }
            else if (state.Weight != planWeight)
            {
                state.Weight = planWeight;
            }

            // WFQ virtual finish tag: advance the tenant's virtual clock by 1/Weight so a
            // heavier weight yields smaller increments (earlier tags) without starving others.
            var virtualNow = (DateTime.UtcNow - DateTime.UnixEpoch).TotalSeconds;
            var weight = state.Weight <= 0 ? 1.0 : state.Weight;
            var tag = Math.Max(state.LastVTime, virtualNow) + (1.0 / weight);
            state.LastVTime = tag;

            var now = DateTime.UtcNow;
            var job = new ExtractionJob
            {
                SourceDocumentOccurrenceId = request.SourceDocumentOccurrenceId,
                BatchId = batchId,
                BusinessUnitId = request.BusinessUnitId,
                SourceType = request.SourceType,
                ContentHash = hash,
                StoragePath = request.StoragePath,
                FileName = request.FileName,
                FileType = request.FileType,
                Status = ExtractionStatus.Pending,
                Priority = request.Priority,
                SchedulerTag = tag,
                Attempts = 0,
                MaxAttempts = request.MaxAttempts <= 0 ? 5 : request.MaxAttempts,
                NextAttemptAt = now,
                CreatedOn = now,
                UpdatedOn = now
            };
            jobs.Add(job);

            try
            {
                await _context.SaveChangesAsync(ct);
                // Counted ONLY on a real insert. A duplicate short-circuit accepted no new
                // work, and counting it would make the enqueue rate lie about intake volume
                // exactly when a sender is retrying.
                _metrics?.JobEnqueued(request.BusinessUnitId);
                return new EnqueueResult { JobId = job.Id, BatchId = batchId, ContentHash = hash, Outcome = EnqueueOutcome.Enqueued };
            }
            catch (DbUpdateException ex) when (IsUniqueViolation(ex))
            {
                // Either the job already exists (idempotent duplicate) or the tenant-state row
                // was created concurrently. Discard tracked state and let the loop re-resolve.
                _context.ChangeTracker.Clear();
            }
        }

        // Final resolution after exhausting retries: the row must exist by now.
        var settled = await jobs.AsNoTracking()
            .Where(j => j.BusinessUnitId == request.BusinessUnitId
                && (request.SourceDocumentOccurrenceId.HasValue
                    ? j.SourceDocumentOccurrenceId == request.SourceDocumentOccurrenceId
                    : j.SourceDocumentOccurrenceId == null && j.ContentHash == hash))
            .Select(j => new { j.Id, j.Status })
            .FirstOrDefaultAsync(ct);
        if (settled is null)
            throw new InvalidOperationException(
                "The extraction job could not be enqueued or resolved after bounded retries.");
        return new EnqueueResult
        {
            JobId = settled.Id,
            BatchId = batchId,
            ContentHash = hash,
            Outcome = EnqueueOutcome.Duplicate,
            ExistingStatus = settled.Status
        };
    }

    public async Task<ExtractionJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, int perTenantCap, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var conn = await OpenAsync(ct);
        await using var transaction = await conn.BeginTransactionAsync(ct);
        await PrepareExecutionScopeAsync(conn, transaction, ct);

        // R-REL-01. A database invariant may still refuse the claim (23514 from the intake
        // guard). Without this savepoint the refusal rolls the whole transaction back and
        // DISCARDS the Attempts increment, so the offending job could never exhaust its
        // attempts, never dead-lettered, and was re-selected by every worker every 2s — one
        // row starving every tenant. The savepoint keeps the refusal recordable.
        await transaction.SaveAsync(ClaimSavepoint, ct);

        ExtractionJob? job = null;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = ClaimSql;
            AddParam(cmd, "now", now);
            AddParam(cmd, "leaseExpiry", now.Add(leaseDuration));
            AddParam(cmd, "worker", workerId);
            AddParam(cmd, "cap", perTenantCap < 1 ? 1 : perTenantCap);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (await reader.ReadAsync(ct))
                job = MapJob(reader);
        }
        catch (PostgresException refused) when (refused.SqlState == PostgresErrorCodes.CheckViolation)
        {
            // ExtractionJobs carries no CHECK constraint, so 23514 here is a trigger refusing
            // the transition to Leased. The invariant is right; looping on it was not.
            await transaction.RollbackAsync(ClaimSavepoint, ct);
            // Poison-message pressure. The SQLSTATE is the tag, never the message text —
            // the message names the offending job/occurrence and would be unbounded.
            _metrics?.ClaimRefused($"sqlstate_{refused.SqlState}", _tenantContext?.BusinessUnitId);
            var reason =
                $"[EXTRACTION_INTAKE_GUARD_REFUSED] Database guard refused the claim " +
                $"(SQLSTATE {refused.SqlState}); the job requires governed reconciliation.";
            var recorded = await RecordRefusedClaimAsync(conn, transaction, reason, now, perTenantCap, ct);
            await transaction.CommitAsync(ct);
            if (recorded is { } strike)
                _log.LogError(refused,
                    "Extraction job {JobId} (tenant {BusinessUnitId}) could not be claimed: {SqlState}. " +
                    "Refusal quarantined at attempt {Attempts}/{MaxAttempts}; job is now {Status}.",
                    strike.JobId, strike.BusinessUnitId, refused.SqlState,
                    strike.Attempts, strike.MaxAttempts, strike.Status);
            else
                _log.LogError(refused,
                    "A claim was refused with {SqlState} but the offending job could not be " +
                    "re-identified, so no attempt was recorded.", refused.SqlState);
            return null;
        }
        await transaction.CommitAsync(ct);
        if (job is not null) _metrics?.JobClaimed(job.BusinessUnitId);
        return job;
    }

    private async Task<RefusedClaim?> RecordRefusedClaimAsync(
        DbConnection conn, DbTransaction transaction, string reason,
        DateTime now, int perTenantCap, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = RecordRefusedClaimSql;
        AddParam(cmd, "now", now);
        AddParam(cmd, "cap", perTenantCap < 1 ? 1 : perTenantCap);
        AddParam(cmd, "error", Trim(reason, 4000));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return new RefusedClaim(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2),
            reader.GetInt32(3), reader.GetInt32(4));
    }

    private readonly record struct RefusedClaim(
        long JobId, long BusinessUnitId, string Status, int Attempts, int MaxAttempts);

    public async Task<bool> RenewLeaseAsync(
        long jobId, string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        const string sql = @"UPDATE ""ExtractionJobs""
SET ""LeaseExpiresAt"" = @leaseExpiry, ""UpdatedOn"" = @now
WHERE ""Id"" = @id AND ""LeasedBy"" = @worker AND ""Attempts"" = @attempt
  AND ""LeaseExpiresAt"" > @now
  AND ""Status"" IN ('Leased','Extracting','Persisting');";
        var rows = await ExecuteAsync(sql, ct,
            ("id", jobId), ("worker", workerId), ("attempt", leaseAttempt),
            ("now", now), ("leaseExpiry", now.Add(leaseDuration)));
        return rows > 0;
    }

    public async Task<bool> SetStatusAsync(
        long jobId, string workerId, int leaseAttempt, ExtractionStatus status, CancellationToken ct = default)
    {
        if (status is not (ExtractionStatus.Extracting or ExtractionStatus.Persisting))
            throw new ArgumentOutOfRangeException(nameof(status), "Only in-progress statuses are valid here.");
        const string sql = @"UPDATE ""ExtractionJobs""
SET ""Status"" = @status, ""UpdatedOn"" = @now
WHERE ""Id"" = @id AND ""LeasedBy"" = @worker AND ""Attempts"" = @attempt
  AND ""LeaseExpiresAt"" > @now
  AND ((@status = 'Extracting' AND ""Status"" = 'Leased')
    OR (@status = 'Persisting' AND ""Status"" = 'Extracting'));";
        var rows = await ExecuteAsync(sql, ct,
            ("id", jobId), ("worker", workerId), ("attempt", leaseAttempt),
            ("status", status.ToString()), ("now", DateTime.UtcNow));
        return rows > 0;
    }

    public async Task<bool> CompleteAsync(
        long jobId, string workerId, int leaseAttempt, long? resultLeadId, CancellationToken ct = default)
    {
        const string sql = @"UPDATE ""ExtractionJobs""
SET ""Status"" = 'Succeeded', ""ResultLeadId"" = @leadId, ""LeasedBy"" = NULL,
    ""LeaseExpiresAt"" = NULL, ""LastError"" = NULL, ""UpdatedOn"" = @now
WHERE ""Id"" = @id AND ""LeasedBy"" = @worker AND ""Attempts"" = @attempt
  AND ""LeaseExpiresAt"" > @now
  AND ""Status"" = 'Persisting';";
        var rows = await ExecuteAsync(sql, ct,
            ("id", jobId), ("worker", workerId), ("attempt", leaseAttempt),
            ("leadId", resultLeadId), ("now", DateTime.UtcNow));
        return rows > 0;
    }

    public async Task<bool> FailAsync(
        long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
    {
        // Attempts was already incremented at claim time. Reschedule with exponential
        // backoff (capped at 1h); once Attempts >= MaxAttempts the job is dead-lettered.
        const string sql = @"UPDATE ""ExtractionJobs""
SET ""Status"" = CASE WHEN ""Attempts"" >= ""MaxAttempts"" THEN 'DeadLetter' ELSE 'Pending' END,
    ""LastError"" = @error,
    ""NextAttemptAt"" = @now + (LEAST(POWER(2, ""Attempts"")::double precision, 3600) * INTERVAL '1 second'),
    ""LeasedBy"" = NULL,
    ""LeaseExpiresAt"" = NULL,
    ""UpdatedOn"" = @now
WHERE ""Id"" = @id AND ""LeasedBy"" = @worker AND ""Attempts"" = @attempt
  AND ""LeaseExpiresAt"" > @now
  AND ""Status"" IN ('Leased','Extracting','Persisting');";
        var rows = await ExecuteAsync(sql, ct,
            ("id", jobId), ("worker", workerId), ("attempt", leaseAttempt),
            ("error", Trim(error, 4000)), ("now", DateTime.UtcNow));
        return rows > 0;
    }

    public async Task<bool> FailPermanentlyAsync(
        long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
    {
        const string sql = @"UPDATE ""ExtractionJobs""
SET ""Status"" = 'DeadLetter',
    ""LastError"" = @error,
    ""NextAttemptAt"" = @now,
    ""LeasedBy"" = NULL,
    ""LeaseExpiresAt"" = NULL,
    ""UpdatedOn"" = @now
WHERE ""Id"" = @id AND ""LeasedBy"" = @worker AND ""Attempts"" = @attempt
  AND ""LeaseExpiresAt"" > @now
  AND ""Status"" IN ('Leased','Extracting','Persisting');";
        var rows = await ExecuteAsync(sql, ct,
            ("id", jobId), ("worker", workerId), ("attempt", leaseAttempt),
            ("error", Trim(error, 4000)), ("now", DateTime.UtcNow));
        return rows > 0;
    }

    // ---- helpers ---------------------------------------------------------

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var conn = _context.Database.GetDbConnection();
        if (conn.State != ConnectionState.Open)
            await conn.OpenAsync(ct);
        return conn;
    }

    private async Task<int> ExecuteAsync(string sql, CancellationToken ct, params (string Name, object? Value)[] parameters)
    {
        var conn = await OpenAsync(ct);
        var currentTransaction = _context.Database.CurrentTransaction?.GetDbTransaction();
        await using var ownedTransaction = currentTransaction is null
            ? await conn.BeginTransactionAsync(ct)
            : null;
        var transaction = currentTransaction ?? ownedTransaction!;
        await PrepareExecutionScopeAsync(conn, transaction, ct);
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            AddParam(cmd, name, value);
        var rows = await cmd.ExecuteNonQueryAsync(ct);
        if (ownedTransaction is not null)
            await ownedTransaction.CommitAsync(ct);
        return rows;
    }

    private async Task PrepareExecutionScopeAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken ct)
    {
        if (_tenantContext is null)
            return;

        await using var setup = connection.CreateCommand();
        setup.Transaction = transaction;
        if (_tenantContext.BusinessUnitId is { } businessUnitId)
        {
            setup.CommandText = $"SET LOCAL ROLE {TenantRlsCommandInterceptor.TenantRole}; " +
                "SELECT set_config('nexora.business_unit_id', @tenant_id, true);";
            AddParam(setup, "tenant_id", businessUnitId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        else
        {
            setup.CommandText = $"SET LOCAL ROLE {TenantRlsCommandInterceptor.PipelineRole};";
        }
        await setup.ExecuteNonQueryAsync(ct);
    }

    private static void AddParam(DbCommand cmd, string name, object? value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value ?? DBNull.Value;
        cmd.Parameters.Add(p);
    }

    private static ExtractionJob MapJob(DbDataReader r)
    {
        return new ExtractionJob
        {
            Id = r.GetInt64(r.GetOrdinal("Id")),
            SourceDocumentOccurrenceId = GetNullableInt64(r, "SourceDocumentOccurrenceId"),
            BatchId = r.GetGuid(r.GetOrdinal("BatchId")),
            BusinessUnitId = r.GetInt64(r.GetOrdinal("BusinessUnitId")),
            SourceType = Enum.Parse<ExtractionSourceType>(r.GetString(r.GetOrdinal("SourceType"))),
            ContentHash = r.GetString(r.GetOrdinal("ContentHash")),
            StoragePath = r.GetString(r.GetOrdinal("StoragePath")),
            FileName = GetNullableString(r, "FileName"),
            FileType = GetNullableString(r, "FileType"),
            Status = Enum.Parse<ExtractionStatus>(r.GetString(r.GetOrdinal("Status"))),
            Priority = r.GetInt32(r.GetOrdinal("Priority")),
            SchedulerTag = r.GetDouble(r.GetOrdinal("SchedulerTag")),
            Attempts = r.GetInt32(r.GetOrdinal("Attempts")),
            MaxAttempts = r.GetInt32(r.GetOrdinal("MaxAttempts")),
            NextAttemptAt = r.GetDateTime(r.GetOrdinal("NextAttemptAt")),
            LeasedBy = GetNullableString(r, "LeasedBy"),
            LeaseExpiresAt = GetNullableDateTime(r, "LeaseExpiresAt"),
            LastError = GetNullableString(r, "LastError"),
            ResultLeadId = GetNullableInt64(r, "ResultLeadId"),
            CreatedOn = r.GetDateTime(r.GetOrdinal("CreatedOn")),
            UpdatedOn = r.GetDateTime(r.GetOrdinal("UpdatedOn"))
        };
    }

    private static string? GetNullableString(DbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetString(i);
    }

    private static DateTime? GetNullableDateTime(DbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetDateTime(i);
    }

    private static long? GetNullableInt64(DbDataReader r, string col)
    {
        var i = r.GetOrdinal(col);
        return r.IsDBNull(i) ? null : r.GetInt64(i);
    }

    private static bool IsUniqueViolation(DbUpdateException ex)
        => ex.InnerException is PostgresException pg && pg.SqlState == PostgresErrorCodes.UniqueViolation;

    private static string ComputeSha256(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string Trim(string? s, int max)
        => string.IsNullOrEmpty(s) ? string.Empty : (s.Length <= max ? s : s.Substring(0, max));
}
