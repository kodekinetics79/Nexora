using System.Security.Cryptography;
using System.Text;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
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

        // The poisoned job was skipped, but NOT silently: the refusal survived as an attempt
        // with a reason, which is the only thing that lets it ever dead-letter.
        var poisoned = await JobAsync(context, poisonedJobId);
        Assert.Equal(ExtractionStatus.Pending, poisoned.Status);
        Assert.Null(poisoned.LeasedBy);
        Assert.Equal(1, poisoned.Attempts);
        Assert.NotNull(poisoned.LastError);
        Assert.Contains("intake occurrence", poisoned.LastError);
        Assert.Contains("Accepted", poisoned.LastError);
        Assert.Contains("(none)", poisoned.LastError);
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task UnclaimableJobExhaustsItsAttemptsAndBecomesVisibleToAnOperator()
    {
        const long tenant = 97_821;
        const int maxAttempts = 3;

        await using var context = Context();
        var queue = NewQueue(context);
        var poisonedJobId = await SeedJobAsync(
            context, queue, "starves-queue", tenant, maxAttempts, schedulerTag: 0, bindIntake: false);

        // Before the fix this loop ran forever: Attempts never moved because the 23514
        // rolled the increment back with the transaction.
        for (var cycle = 0; cycle < maxAttempts; cycle++)
        {
            await ElapseBackoffAsync(context, poisonedJobId);
            Assert.Null(await queue.ClaimAsync($"worker-{cycle}", TimeSpan.FromMinutes(5), 4));
            Assert.Equal(cycle + 1, (await JobAsync(context, poisonedJobId)).Attempts);
        }

        var deadLettered = await JobAsync(context, poisonedJobId);
        Assert.Equal(ExtractionStatus.DeadLetter, deadLettered.Status);
        Assert.Equal(maxAttempts, deadLettered.Attempts);
        Assert.Null(deadLettered.LeasedBy);
        Assert.NotNull(deadLettered.LastError);
        Assert.Contains("Extraction cannot start", deadLettered.LastError);
        Assert.Contains("intake occurrence", deadLettered.LastError);
        Assert.Contains("Accepted", deadLettered.LastError);
        Assert.Contains($"refused attempt {maxAttempts} of {maxAttempts}", deadLettered.LastError);

        // The intake occurrence itself is not laundered into a claimable state on the way out.
        var occurrence = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
            .SingleAsync(x => x.BusinessUnitId == tenant);
        Assert.Equal(IntakeOccurrenceStatus.Accepted, occurrence.IntakeStatus);
        Assert.Null(occurrence.ExtractionJobId);

        // What the operator actually sees: the job is in the dead-letter queue with a
        // truthful reason, and it blocks operations readiness until someone resolves it.
        var service = new ExtractionDeadLetterService(context, new UnusedStorage(), new UnusedScanner());
        var item = Assert.Single(await service.ListAsync(tenant, default));
        Assert.Equal(poisonedJobId, item.JobId);
        Assert.Equal(maxAttempts, item.Attempts);
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
    public async Task ClaimRefusedByTheDatabaseIsRecordedInsteadOfRetriedForever()
    {
        const long tenant = 97_841;
        const int maxAttempts = 2;

        await using var context = Context();
        var queue = NewQueue(context);

        // A legacy job with no durable intake link. The scheduler cannot tell this shape
        // apart from a claimable one — the deployed guard refuses it anyway. That is the
        // drift case the savepoint exists for: the refusal must still be charged, or the
        // job loops on every worker forever exactly as it did on 2026-08-05.
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

        for (var cycle = 0; cycle < maxAttempts; cycle++)
        {
            await ElapseBackoffAsync(context, enqueued.JobId);
            Assert.Null(await queue.ClaimAsync($"worker-{cycle}", TimeSpan.FromMinutes(5), 4));
            Assert.Equal(cycle + 1, (await JobAsync(context, enqueued.JobId)).Attempts);
        }

        var refused = await JobAsync(context, enqueued.JobId);
        Assert.Equal(ExtractionStatus.DeadLetter, refused.Status);
        Assert.Equal(maxAttempts, refused.Attempts);
        Assert.Null(refused.LeasedBy);
        Assert.NotNull(refused.LastError);
        Assert.Contains("SQLSTATE 23514", refused.LastError);
        Assert.Contains("durable intake occurrence", refused.LastError);
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

        // The poisoned neighbour never took a lease and never dead-lettered early.
        var poisoned = await JobAsync(context, poisonedJobId);
        Assert.Equal(ExtractionStatus.Pending, poisoned.Status);
        Assert.Null(poisoned.LeasedBy);
        Assert.Null(poisoned.LeaseExpiresAt);
        Assert.InRange(poisoned.Attempts, 1, 2);
    }

    // ---- helpers ---------------------------------------------------------

    private ErpRfqAutomationContext Context()
        => _server.ContextForConnectionString(_connectionString, null);

    private static ExtractionQueue NewQueue(ErpRfqAutomationContext context)
        => new(context, new NoopLogger<ExtractionQueue>());

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
