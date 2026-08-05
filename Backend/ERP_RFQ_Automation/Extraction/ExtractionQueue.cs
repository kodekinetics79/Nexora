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
/// </summary>
public sealed class ExtractionQueue : IExtractionQueue
{
    private readonly ErpRfqAutomationContext _context;
    private readonly ILogger<ExtractionQueue> _log;
    private readonly ITenantContext? _tenantContext;
    private readonly IEntitlementService? _entitlements;

    public ExtractionQueue(ErpRfqAutomationContext context, ILogger<ExtractionQueue> log)
    {
        _context = context;
        _log = log;
    }

    public ExtractionQueue(
        ErpRfqAutomationContext context,
        ILogger<ExtractionQueue> log,
        ITenantContext tenantContext,
        IEntitlementService? entitlements = null)
        : this(context, log)
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

    // Atomic weighted-fair claim. Live (non-expired) leases per tenant are counted so a
    // tenant already at its cap is skipped; among eligible jobs the highest Priority then
    // the lowest WFQ SchedulerTag wins. Expired leases (crashed workers) are reclaimable.
    //
    // Per-tenant concurrency entitlement (P0): the effective cap for a tenant is its
    // plan's MaxConcurrentExtractionJobs (resolved via platform.Tenants →
    // platform.Plans on PrimaryBusinessUnitId, inside the same atomic statement), and
    // @cap — the ExtractionWorkerOptions.PerTenantConcurrencyCap config default —
    // remains the fallback for tenants without a plan or without a Tenant row.
    private static readonly string ClaimSql = $@"
WITH plan_caps AS (
    SELECT t.""PrimaryBusinessUnitId"" AS buid,
           MAX(p.""MaxConcurrentExtractionJobs"") AS cap
    FROM platform.""Tenants"" t
    JOIN platform.""Plans"" p ON p.""Id"" = t.""PlanId""
    WHERE t.""PrimaryBusinessUnitId"" IS NOT NULL
    GROUP BY t.""PrimaryBusinessUnitId""
),
blocked_tenants AS (
    -- P2-A8: Suspended/Archived tenants' queued jobs must not be claimed. Legacy BUs
    -- without a platform.Tenants row are unaffected (fail open per LEDGER contract).
    SELECT t.""PrimaryBusinessUnitId"" AS buid
    FROM platform.""Tenants"" t
    WHERE t.""PrimaryBusinessUnitId"" IS NOT NULL
      AND t.""Status"" IN ('Suspended','Archived')
),
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
inflight AS (
    SELECT ""BusinessUnitId"" AS buid, COUNT(*) AS cnt
    FROM ""ExtractionJobs""
    WHERE ""Status"" IN ('Leased','Extracting','Persisting')
      AND ""LeaseExpiresAt"" > @now
    GROUP BY ""BusinessUnitId""
),
candidate AS (
    SELECT j.""Id""
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
      AND COALESCE(f.cnt, 0) < GREATEST(COALESCE(NULLIF(pc.cap, 0), @cap), 1)
    ORDER BY j.""Priority"" DESC, j.""SchedulerTag"" ASC, j.""CreatedOn"" ASC
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
RETURNING {ReturningColumns};";

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
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = ClaimSql;
        AddParam(cmd, "now", now);
        AddParam(cmd, "leaseExpiry", now.Add(leaseDuration));
        AddParam(cmd, "worker", workerId);
        AddParam(cmd, "cap", perTenantCap < 1 ? 1 : perTenantCap);

        ExtractionJob? job = null;
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
                job = MapJob(reader);
        }
        await transaction.CommitAsync(ct);
        return job;
    }

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
