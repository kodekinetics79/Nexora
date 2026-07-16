using System;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
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

    public ExtractionQueue(ErpRfqAutomationContext context, ILogger<ExtractionQueue> log)
    {
        _context = context;
        _log = log;
    }

    // All entity columns default to their property names (case-sensitive, quoted).
    private const string ReturningColumns =
        "j.\"Id\", j.\"BatchId\", j.\"BusinessUnitId\", j.\"SourceType\", j.\"ContentHash\", " +
        "j.\"StoragePath\", j.\"FileName\", j.\"FileType\", j.\"Status\", j.\"Priority\", " +
        "j.\"SchedulerTag\", j.\"Attempts\", j.\"MaxAttempts\", j.\"NextAttemptAt\", " +
        "j.\"LeasedBy\", j.\"LeaseExpiresAt\", j.\"LastError\", j.\"ResultLeadId\", " +
        "j.\"CreatedOn\", j.\"UpdatedOn\"";

    // Atomic weighted-fair claim. Live (non-expired) leases per tenant are counted so a
    // tenant already at its cap is skipped; among eligible jobs the highest Priority then
    // the lowest WFQ SchedulerTag wins. Expired leases (crashed workers) are reclaimable.
    private static readonly string ClaimSql = $@"
WITH inflight AS (
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
    WHERE (
            j.""Status"" = 'Pending'
            OR (j.""Status"" IN ('Leased','Extracting','Persisting')
                AND (j.""LeaseExpiresAt"" IS NULL OR j.""LeaseExpiresAt"" <= @now))
          )
      AND j.""NextAttemptAt"" <= @now
      AND COALESCE(f.cnt, 0) < @cap
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
            var existingId = await jobs.AsNoTracking()
                .Where(j => j.BusinessUnitId == request.BusinessUnitId && j.ContentHash == hash)
                .Select(j => j.Id)
                .FirstOrDefaultAsync(ct);
            if (existingId != 0)
                return new EnqueueResult { JobId = existingId, BatchId = batchId, ContentHash = hash, Outcome = EnqueueOutcome.Duplicate };

            var state = await tenants.FindAsync(new object[] { request.BusinessUnitId }, ct);
            if (state is null)
            {
                state = new TenantQueueState { BusinessUnitId = request.BusinessUnitId, Weight = 1.0, LastVTime = 0, InFlight = 0 };
                tenants.Add(state);
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
        var settledId = await jobs.AsNoTracking()
            .Where(j => j.BusinessUnitId == request.BusinessUnitId && j.ContentHash == hash)
            .Select(j => j.Id)
            .FirstOrDefaultAsync(ct);
        return new EnqueueResult
        {
            JobId = settledId,
            BatchId = batchId,
            ContentHash = hash,
            Outcome = EnqueueOutcome.Duplicate
        };
    }

    public async Task<ExtractionJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, int perTenantCap, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = ClaimSql;
        AddParam(cmd, "now", now);
        AddParam(cmd, "leaseExpiry", now.Add(leaseDuration));
        AddParam(cmd, "worker", workerId);
        AddParam(cmd, "cap", perTenantCap < 1 ? 1 : perTenantCap);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return null;
        return MapJob(reader);
    }

    public async Task<bool> RenewLeaseAsync(long jobId, string workerId, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        const string sql = @"UPDATE ""ExtractionJobs""
SET ""LeaseExpiresAt"" = @leaseExpiry, ""UpdatedOn"" = @now
WHERE ""Id"" = @id AND ""LeasedBy"" = @worker;";
        var rows = await ExecuteAsync(sql, ct,
            ("id", jobId), ("worker", workerId), ("now", now), ("leaseExpiry", now.Add(leaseDuration)));
        return rows > 0;
    }

    public async Task SetStatusAsync(long jobId, ExtractionStatus status, CancellationToken ct = default)
    {
        const string sql = @"UPDATE ""ExtractionJobs""
SET ""Status"" = @status, ""UpdatedOn"" = @now
WHERE ""Id"" = @id;";
        await ExecuteAsync(sql, ct, ("id", jobId), ("status", status.ToString()), ("now", DateTime.UtcNow));
    }

    public async Task CompleteAsync(long jobId, long resultLeadId, CancellationToken ct = default)
    {
        const string sql = @"UPDATE ""ExtractionJobs""
SET ""Status"" = 'Succeeded', ""ResultLeadId"" = @leadId, ""LeasedBy"" = NULL,
    ""LeaseExpiresAt"" = NULL, ""LastError"" = NULL, ""UpdatedOn"" = @now
WHERE ""Id"" = @id;";
        await ExecuteAsync(sql, ct, ("id", jobId), ("leadId", resultLeadId), ("now", DateTime.UtcNow));
    }

    public async Task FailAsync(long jobId, string error, CancellationToken ct = default)
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
WHERE ""Id"" = @id;";
        await ExecuteAsync(sql, ct, ("id", jobId), ("error", Trim(error, 4000)), ("now", DateTime.UtcNow));
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
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            AddParam(cmd, name, value);
        return await cmd.ExecuteNonQueryAsync(ct);
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
