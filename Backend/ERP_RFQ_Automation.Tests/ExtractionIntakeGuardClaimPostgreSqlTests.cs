using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Hardening;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// R-REL-01 — poison-pill isolation in the extraction claim, certified against the
/// production dialect because the behaviour is trigger-dependent and cannot be proven on
/// SQLite: <c>trg_release01b_intake_before_claim_guard</c> raises 23514 when a job whose
/// durable intake occurrence is not queued tries to become <c>Leased</c>.
///
/// On 2026-08-05 one such row halted ingestion for EVERY tenant: the candidate CTE never
/// looked at <c>source_document_occurrences</c>, so the unclaimable job was re-selected on
/// every 2s poll, and the raised transaction rolled back the <c>Attempts</c> increment, so
/// it could never exhaust and never dead-letter. These tests pin the three properties that
/// close that class of incident — the queue keeps moving, the bad row becomes visible to an
/// operator, and the lease/fencing semantics are untouched.
///
/// Each test runs against its own migrated database on the shared server, so the claim
/// (which is global, not tenant-scoped) sees only the rows the test created.
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class ExtractionIntakeGuardClaimPostgreSqlTests : IAsyncLifetime
{
    private readonly PostgreSqlTestDatabase _server;
    private string _databaseName = null!;
    private string _connectionString = null!;

    public ExtractionIntakeGuardClaimPostgreSqlTests(PostgreSqlTestDatabase server) => _server = server;

    public async Task InitializeAsync()
    {
        _databaseName = $"nexora_intake_guard_{Guid.NewGuid():N}";
        await ExecuteAdminAsync($"CREATE DATABASE \"{_databaseName}\"");
        _connectionString = new NpgsqlConnectionStringBuilder(_server.ConnectionString)
        {
            Database = _databaseName
        }.ConnectionString;
        await using var context = Context();
        await context.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        NpgsqlConnection.ClearAllPools();
        await ExecuteAdminAsync($"DROP DATABASE IF EXISTS \"{_databaseName}\" WITH (FORCE)");
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task UnclaimableIntakeOccurrenceDoesNotBlockAnotherTenantsExtraction()
    {
        const long poisonedTenant = 97_811;
        const long healthyTenant = 97_812;

        await using var context = Context();
        var queue = NewQueue(context);

        // The 2026-08-05 shape exactly: a durable occurrence left in Accepted whose
        // extraction_job_id link was never written, with its job first in claim order.
        var poisonedJobId = await SeedJobAsync(
            context, queue, "poisoned", poisonedTenant, maxAttempts: 4, schedulerTag: 0, bindIntake: false);
        var healthyJobId = await SeedJobAsync(
            context, queue, "healthy", healthyTenant, maxAttempts: 5, schedulerTag: 1, bindIntake: true);

        // The database invariant is intact and still refuses the transition. It is the
        // queue's reaction that changed, not the guard.
        await using (var direct = Context())
        {
            var refusal = await Assert.ThrowsAsync<PostgresException>(() =>
                direct.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE "ExtractionJobs"
                    SET "Status" = 'Leased',
                        "LeasedBy" = 'direct-writer',
                        "LeaseExpiresAt" = now() + interval '1 minute',
                        "UpdatedOn" = now()
                    WHERE "Id" = {poisonedJobId}
                    """));
            Assert.Equal(PostgresErrorCodes.CheckViolation, refusal.SqlState);
            Assert.Contains("durable intake occurrence", refusal.MessageText);
        }

        // Head-of-line blocking is gone: the poisoned job sorts first, the healthy job runs.
        var claimed = await queue.ClaimAsync("worker-1", TimeSpan.FromMinutes(5), 4);
        Assert.NotNull(claimed);
        Assert.Equal(healthyJobId, claimed!.Id);
        Assert.Equal(healthyTenant, claimed.BusinessUnitId);
        Assert.Equal(1, claimed.Attempts);

        Assert.True(await queue.SetStatusAsync(
            healthyJobId, "worker-1", claimed.Attempts, ExtractionStatus.Extracting));
        Assert.True(await queue.SetStatusAsync(
            healthyJobId, "worker-1", claimed.Attempts, ExtractionStatus.Persisting));
        Assert.True(await queue.CompleteAsync(healthyJobId, "worker-1", claimed.Attempts, 4_242));
        Assert.Equal(ExtractionStatus.Succeeded, (await JobAsync(context, healthyJobId)).Status);

        // The poisoned job was skipped, but NOT silently or retried: structural lineage
        // cannot heal through extraction retries, so it is quarantined once in governed DLQ.
        var poisoned = await JobAsync(context, poisonedJobId);
        Assert.Equal(ExtractionStatus.DeadLetter, poisoned.Status);
        Assert.Null(poisoned.LeasedBy);
        Assert.Equal(0, poisoned.Attempts);
        Assert.NotNull(poisoned.LastError);
        Assert.StartsWith("[EXTRACTION_INTAKE_JOB_LINK_MISMATCH]", poisoned.LastError);
        Assert.Contains($"QueueId={poisonedJobId}", poisoned.LastError);
        Assert.DoesNotContain("poisoned.pdf", poisoned.LastError);

        // The real PostgreSQL observability query recognizes the stable code without
        // loading or publishing document names, paths, or raw exception messages.
        var groups = await ExtractionQueueMetricsPoller.QueryAsync(
            context, DateTime.UtcNow, CancellationToken.None);
        var snapshot = ExtractionQueueSnapshot.From(groups, DateTimeOffset.UtcNow);
        var blockedGauge = Assert.Single(snapshot.Tenants,
            x => x.BusinessUnitId == poisonedTenant);
        Assert.Equal(1, blockedGauge.InvariantBlocked);
        Assert.Equal(1, snapshot.InvariantAffectedTenants);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task UnclaimableJobIsClassifiedExactlyOnceAndVisibleToAnOperator()
    {
        const long tenant = 97_821;
        const int maxAttempts = 3;

        await using var context = Context();
        var queue = NewQueue(context);
        var poisonedJobId = await SeedJobAsync(
            context, queue, "starves-queue", tenant, maxAttempts, schedulerTag: 0, bindIntake: false);

        Assert.Null(await queue.ClaimAsync("worker-first", TimeSpan.FromMinutes(5), 4));
        var firstClassification = await JobAsync(context, poisonedJobId);
        for (var cycle = 0; cycle < 8; cycle++)
            Assert.Null(await queue.ClaimAsync($"worker-repeat-{cycle}", TimeSpan.FromMinutes(5), 4));

        var deadLettered = await JobAsync(context, poisonedJobId);
        Assert.Equal(ExtractionStatus.DeadLetter, deadLettered.Status);
        Assert.Equal(0, deadLettered.Attempts);
        Assert.Equal(firstClassification.UpdatedOn, deadLettered.UpdatedOn);
        Assert.Equal(firstClassification.LastError, deadLettered.LastError);
        Assert.Null(deadLettered.LeasedBy);
        Assert.NotNull(deadLettered.LastError);
        Assert.StartsWith("[EXTRACTION_INTAKE_JOB_LINK_MISMATCH]", deadLettered.LastError);

        // The intake occurrence itself is not laundered into a claimable state on the way out.
        var occurrence = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == tenant);
        // The occurrence is deliberately not laundered: the shared-occurrence trigger only
        // follows a job it is actually bound to. The job is quarantined; Accepted remains
        // truthful evidence of the producer's incomplete transaction.
        Assert.Equal(IntakeOccurrenceStatus.Accepted, occurrence.IntakeStatus);
        Assert.Null(occurrence.ExtractionJobId);

        // What the operator actually sees: the job is in the dead-letter queue with a
        // truthful reason, and it blocks operations readiness until someone resolves it.
        var service = new ExtractionDeadLetterService(context, new UnusedStorage(), new UnusedScanner());
        var item = Assert.Single(await service.ListAsync(tenant, default));
        Assert.Equal(poisonedJobId, item.JobId);
        Assert.Equal(0, item.Attempts);
        Assert.Equal("INTAKE_INVARIANT", item.FailureCategory);
        Assert.Equal("Open", item.Resolution);
        Assert.True(item.BlocksReadiness);

        // Same predicate OperationsReadinessController uses for the unresolved count.
        var unresolved = await context.Set<ExtractionJob>().AsNoTracking()
            .CountAsync(job => job.BusinessUnitId == tenant
                && job.Status == ExtractionStatus.DeadLetter
                && context.ExtractionDeadLetterEvents
                    .Where(disposition => disposition.BusinessUnitId == tenant
                        && disposition.ExtractionJobId == job.Id
                        && disposition.AttemptNumber == job.Attempts)
                    .OrderByDescending(disposition => disposition.Id)
                    .Select(disposition => (ExtractionDeadLetterAction?)disposition.Action)
                    .FirstOrDefault() != ExtractionDeadLetterAction.SourceObjectUnavailable);
        Assert.Equal(1, unresolved);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LegacyJobWithoutOccurrenceIsClassifiedWithoutHittingTheGuardLoop()
    {
        const long tenant = 97_841;
        const int maxAttempts = 2;

        await using var context = Context();
        var queue = NewQueue(context);

        // A legacy job with no durable intake link is classified before the guarded update.
        var enqueued = await queue.EnqueueAsync(new EnqueueExtractionRequest
        {
            BusinessUnitId = tenant,
            SourceType = ExtractionSourceType.ManualUpload,
            StoragePath = "test://unlinked",
            ContentHash = new string('e', 64),
            FileName = "unlinked.pdf",
            FileType = "pdf",
            Priority = int.MaxValue,
            MaxAttempts = maxAttempts
        });

        Assert.Null(await queue.ClaimAsync("worker", TimeSpan.FromMinutes(5), 4));

        var refused = await JobAsync(context, enqueued.JobId);
        Assert.Equal(ExtractionStatus.DeadLetter, refused.Status);
        Assert.Equal(0, refused.Attempts);
        Assert.Null(refused.LeasedBy);
        Assert.NotNull(refused.LastError);
        Assert.StartsWith("[EXTRACTION_INTAKE_OCCURRENCE_MISSING]", refused.LastError);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LeaseFencingAndExpiredLeaseReclaimAreUnchangedBesideAPoisonedRow()
    {
        const long tenant = 97_831;

        await using var context = Context();
        var queue = NewQueue(context);
        var poisonedJobId = await SeedJobAsync(
            context, queue, "neighbour", tenant, maxAttempts: 5, schedulerTag: 0, bindIntake: false);
        var jobId = await SeedJobAsync(
            context, queue, "fenced", tenant, maxAttempts: 3, schedulerTag: 1, bindIntake: true);

        var claim = await queue.ClaimAsync("worker-a", TimeSpan.FromMinutes(5), 4);
        Assert.Equal(jobId, claim!.Id);
        Assert.Equal(1, claim.Attempts);
        Assert.Equal(IntakeOccurrenceStatus.Processing, await IntakeStatusAsync(context, jobId));

        // Fencing generation + owner are still load-bearing.
        Assert.False(await queue.RenewLeaseAsync(jobId, "worker-b", claim.Attempts, TimeSpan.FromMinutes(5)));
        Assert.False(await queue.RenewLeaseAsync(jobId, "worker-a", claim.Attempts + 1, TimeSpan.FromMinutes(5)));
        Assert.False(await queue.SetStatusAsync(jobId, "worker-b", claim.Attempts, ExtractionStatus.Extracting));
        Assert.False(await queue.CompleteAsync(jobId, "worker-a", claim.Attempts, 7_001));
        Assert.True(await queue.RenewLeaseAsync(jobId, "worker-a", claim.Attempts, TimeSpan.FromMinutes(5)));
        Assert.True(await queue.SetStatusAsync(jobId, "worker-a", claim.Attempts, ExtractionStatus.Extracting));

        // Crashed worker: the lease expires with the occurrence left in Processing, and the
        // job must stay reclaimable — the claim predicate mirrors that branch of the guard.
        await context.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE "ExtractionJobs" SET "LeaseExpiresAt" = now() - INTERVAL '1 second' WHERE "Id" = {jobId}
            """);
        Assert.False(await queue.RenewLeaseAsync(jobId, "worker-a", claim.Attempts, TimeSpan.FromMinutes(5)));

        var reclaimed = await queue.ClaimAsync("worker-c", TimeSpan.FromMinutes(5), 4);
        Assert.Equal(jobId, reclaimed!.Id);
        Assert.Equal(2, reclaimed.Attempts);
        Assert.False(await queue.FailAsync(jobId, "worker-a", claim.Attempts, "stale generation"));
        Assert.True(await queue.FailAsync(jobId, "worker-c", reclaimed.Attempts, "transient extraction failure"));

        var retried = await JobAsync(context, jobId);
        Assert.Equal(ExtractionStatus.Pending, retried.Status);
        Assert.Equal("transient extraction failure", retried.LastError);
        Assert.Equal(IntakeOccurrenceStatus.Retryable, await IntakeStatusAsync(context, jobId));

        // The poisoned neighbour never took a lease and was quarantined exactly once.
        var poisoned = await JobAsync(context, poisonedJobId);
        Assert.Equal(ExtractionStatus.DeadLetter, poisoned.Status);
        Assert.Null(poisoned.LeasedBy);
        Assert.Null(poisoned.LeaseExpiresAt);
        Assert.Equal(0, poisoned.Attempts);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentWorkersQuarantineOneInvalidRowOnceAndClaimDisjointValidWork()
    {
        const long poisonedTenant = 97_851;
        await using (var seed = Context())
        {
            var queue = NewQueue(seed);
            await SeedJobAsync(seed, queue, "concurrent-poison", poisonedTenant, 5, 0, false);
            for (var i = 0; i < 8; i++)
                await SeedJobAsync(seed, queue, $"concurrent-valid-{i}", 97_860 + i, 5, i + 1, true);
        }

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(async i =>
        {
            await using var worker = Context();
            var queue = NewQueue(worker);
            for (var poll = 0; poll < 4; poll++)
            {
                var claim = await queue.ClaimAsync(
                    $"concurrent-worker-{i}", TimeSpan.FromMinutes(5), 4);
                if (claim is not null) return claim;
            }
            return null;
        }));

        Assert.All(claims, Assert.NotNull);
        Assert.Equal(8, claims.Select(x => x!.Id).Distinct().Count());
        await using var verify = Context();
        var poisoned = await verify.Set<ExtractionJob>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == poisonedTenant);
        Assert.Equal(ExtractionStatus.DeadLetter, poisoned.Status);
        Assert.Equal(0, poisoned.Attempts);
        Assert.StartsWith("[EXTRACTION_INTAKE_JOB_LINK_MISMATCH]", poisoned.LastError);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task LegacyReconciliationIsBoundedAndIdempotent()
    {
        const long tenantBase = 98_000;
        await using var context = Context();
        var queue = NewQueue(context);
        for (var i = 0; i < 40; i++)
            await SeedJobAsync(context, queue, $"bounded-poison-{i}", tenantBase + i, 5, i, false);
        var validId = await SeedJobAsync(context, queue, "bounded-valid", tenantBase + 100, 5, 100, true);

        // One poll touches at most HeadOfLineLookahead (32) legacy rows.
        Assert.Null(await queue.ClaimAsync("bounded-1", TimeSpan.FromMinutes(5), 4));
        Assert.Equal(32, await context.Set<ExtractionJob>().AsNoTracking()
            .CountAsync(x => x.Status == ExtractionStatus.DeadLetter));

        var claim = await queue.ClaimAsync("bounded-2", TimeSpan.FromMinutes(5), 4);
        Assert.Equal(validId, claim!.Id);
        Assert.Equal(40, await context.Set<ExtractionJob>().AsNoTracking()
            .CountAsync(x => x.Status == ExtractionStatus.DeadLetter));

        // Repeated polls cannot reclassify or mutate quarantined rows.
        var before = await context.Set<ExtractionJob>().AsNoTracking()
            .Where(x => x.Status == ExtractionStatus.DeadLetter)
            .Select(x => new { x.Id, x.Attempts, x.UpdatedOn, x.LastError }).OrderBy(x => x.Id).ToListAsync();
        Assert.Null(await queue.ClaimAsync("bounded-3", TimeSpan.FromMinutes(5), 4));
        var after = await context.Set<ExtractionJob>().AsNoTracking()
            .Where(x => x.Status == ExtractionStatus.DeadLetter)
            .Select(x => new { x.Id, x.Attempts, x.UpdatedOn, x.LastError }).OrderBy(x => x.Id).ToListAsync();
        Assert.Equal(before, after);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task QueueCreationAndOccurrenceTransitionCommitAtomically()
    {
        const long tenant = 98_101;
        await using var context = Context();
        var (occurrence, hash) = await SeedOccurrenceAsync(context, "atomic-commit", tenant);
        await using (var transaction = await context.Database.BeginTransactionAsync())
        {
            var enqueued = await NewQueue(context).EnqueueAsync(Request(tenant, occurrence.Id, hash, "atomic-commit"));
            occurrence.BindExtractionJob(enqueued.JobId);
            await context.SaveChangesAsync();
            await transaction.CommitAsync();
        }

        var claim = await NewQueue(context).ClaimAsync("atomic-worker", TimeSpan.FromMinutes(5), 1);
        Assert.NotNull(claim);
        Assert.Equal(tenant, claim!.BusinessUnitId);
        Assert.Equal(IntakeOccurrenceStatus.Processing, await IntakeStatusAsync(context, claim.Id));
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ProducerRollbackLeavesNoQueueRowAndNoFalseQueuedOccurrence()
    {
        const long tenant = 98_102;
        long occurrenceId;
        await using (var context = Context())
        {
            var seeded = await SeedOccurrenceAsync(context, "atomic-rollback", tenant);
            occurrenceId = seeded.Occurrence.Id;
            await using var transaction = await context.Database.BeginTransactionAsync();
            var enqueued = await NewQueue(context).EnqueueAsync(
                Request(tenant, occurrenceId, seeded.Hash, "atomic-rollback"));
            seeded.Occurrence.BindExtractionJob(enqueued.JobId);
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }

        await using var verify = Context();
        Assert.Empty(await verify.Set<ExtractionJob>().AsNoTracking()
            .Where(x => x.BusinessUnitId == tenant).ToListAsync());
        var occurrence = await verify.Set<SourceDocumentOccurrence>().AsNoTracking()
            .SingleAsync(x => x.Id == occurrenceId && x.BusinessUnitId == tenant);
        Assert.Equal(IntakeOccurrenceStatus.Accepted, occurrence.IntakeStatus);
        Assert.Null(occurrence.ExtractionJobId);
    }

    // ---- helpers ---------------------------------------------------------

    private ErpRfqAutomationContext Context()
        => _server.ContextForConnectionString(_connectionString, null);

    // SEC-ING-02: the tenant context is mandatory. Context() is built with a null tenant (the
    // cross-tenant worker view), so the queue gets the matching null-tenant StubTenant and takes
    // the deliberate nexora_pipeline_app role.
    private static ExtractionQueue NewQueue(ErpRfqAutomationContext context)
        => new(context, new NoopLogger<ExtractionQueue>(), new StubTenant(null));

    private static Task<ExtractionJob> JobAsync(ErpRfqAutomationContext context, long jobId)
        => context.Set<ExtractionJob>().AsNoTracking().SingleAsync(job => job.Id == jobId);

    private static async Task<IntakeOccurrenceStatus> IntakeStatusAsync(
        ErpRfqAutomationContext context, long jobId)
        => (await context.Set<SourceDocumentOccurrence>().AsNoTracking()
            .SingleAsync(x => x.ExtractionJobId == jobId)).IntakeStatus;

    /// <summary>Move the claim backoff into the past without waiting for wall-clock time.</summary>
    private static Task ElapseBackoffAsync(ErpRfqAutomationContext context, long jobId)
        => context.Set<ExtractionJob>().Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(update =>
                update.SetProperty(job => job.NextAttemptAt, DateTime.UtcNow.AddSeconds(-1)));

    /// <summary>
    /// Governed intake exactly as <c>DocumentIngestionService</c> writes it. With
    /// <paramref name="bindIntake"/> false the occurrence is left in Accepted with no
    /// extraction_job_id — the poisoned shape the 2026-08-05 incident produced.
    /// </summary>
    private static async Task<long> SeedJobAsync(
        ErpRfqAutomationContext context,
        IExtractionQueue queue,
        string marker,
        long businessUnitId,
        int maxAttempts,
        double schedulerTag,
        bool bindIntake)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker))).ToLowerInvariant();
        var corpus = DocumentCorpus.Create(businessUnitId, Guid.NewGuid(), CorpusSourceType.ManualUpload);
        context.Set<DocumentCorpus>().Add(corpus);
        await context.SaveChangesAsync();

        var source = SourceDocument.Create(businessUnitId, corpus.Id, hash, marker + ".pdf",
            "application/pdf", "acceptance", marker, "v1", 1);
        context.Set<SourceDocument>().Add(source);
        await context.SaveChangesAsync();

        var occurrence = SourceDocumentOccurrence.Create(
            businessUnitId, source.Id, corpus.Id, "intake-guard:" + marker, "{}");
        context.Set<SourceDocumentOccurrence>().Add(occurrence);
        await context.SaveChangesAsync();

        var enqueued = await queue.EnqueueAsync(new EnqueueExtractionRequest
        {
            BusinessUnitId = businessUnitId,
            SourceDocumentOccurrenceId = occurrence.Id,
            SourceType = ExtractionSourceType.ManualUpload,
            StoragePath = "test://" + marker,
            ContentHash = hash,
            FileName = marker + ".pdf",
            FileType = "pdf",
            Priority = int.MaxValue,
            MaxAttempts = maxAttempts
        });
        Assert.Equal(EnqueueOutcome.Enqueued, enqueued.Outcome);

        if (bindIntake)
        {
            occurrence.BindExtractionJob(enqueued.JobId);
            await context.SaveChangesAsync();
        }

        // Deterministic claim order for the test's own rows (lowest WFQ tag wins).
        await context.Set<ExtractionJob>().Where(job => job.Id == enqueued.JobId)
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.SchedulerTag, schedulerTag));
        return enqueued.JobId;
    }

    private static async Task<(SourceDocumentOccurrence Occurrence, string Hash)> SeedOccurrenceAsync(
        ErpRfqAutomationContext context, string marker, long businessUnitId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(marker))).ToLowerInvariant();
        var corpus = DocumentCorpus.Create(businessUnitId, Guid.NewGuid(), CorpusSourceType.ManualUpload);
        context.Set<DocumentCorpus>().Add(corpus);
        await context.SaveChangesAsync();
        var source = SourceDocument.Create(businessUnitId, corpus.Id, hash, marker + ".pdf",
            "application/pdf", "acceptance", marker, "v1", 1);
        context.Set<SourceDocument>().Add(source);
        await context.SaveChangesAsync();
        var occurrence = SourceDocumentOccurrence.Create(
            businessUnitId, source.Id, corpus.Id, "intake-guard:" + marker, "{}");
        context.Set<SourceDocumentOccurrence>().Add(occurrence);
        await context.SaveChangesAsync();
        return (occurrence, hash);
    }

    private static EnqueueExtractionRequest Request(
        long businessUnitId, long occurrenceId, string hash, string marker) => new()
    {
        BusinessUnitId = businessUnitId,
        SourceDocumentOccurrenceId = occurrenceId,
        SourceType = ExtractionSourceType.ManualUpload,
        StoragePath = "test://" + marker,
        ContentHash = hash,
        FileName = marker + ".pdf",
        FileType = "pdf",
        Priority = int.MaxValue,
        MaxAttempts = 5
    };

    private async Task ExecuteAdminAsync(string sql)
    {
        var admin = new NpgsqlConnectionStringBuilder(_server.ConnectionString) { Database = "postgres" };
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>ListAsync reads only the ledger; recovery collaborators are never touched.</summary>
    private sealed class UnusedStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class UnusedScanner : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
