using System.Text.Json;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// ING-09 — THE TRIAGE SCREEN MUST NOT SAY "QUEUED" OVER A DEAD-LETTERED MESSAGE.
///
/// The only writer that resolved EmailIngest.ParseStatus downstream of "Queued" was the
/// persist/success path (LeadPersister.ResolveIngestAsync). When every extraction job of a
/// message dead-lettered, nothing ever wrote back: the DLQ held the truth and the triage
/// screen showed normal progress forever. These tests pin the failure-path counterpart in
/// the worker — a dead-lettered email job flips the ingest to a visible failed state, a
/// non-final failure does not, and a later successful retry flips it back through the
/// existing success path.
/// </summary>
public sealed class ExtractionFailureWritebackTests : IDisposable
{
    private const long Tenant = 7;
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ADeadLetteredEmailJobFlipsTheIngestToAVisibleFailedState()
    {
        // Final attempt (Attempts == MaxAttempts): the retryable failure below IS the
        // dead-letter arrival, exactly the condition FailAsync uses.
        var (job, ingestId) = await SeedEmailJobAsync(jobId: 901, attempts: 3, maxAttempts: 3);
        var queue = new RecordingQueue(job);
        using var services = BuildServices(queue);
        var worker = CreateWorker(services);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.RetryableFailure.Task.WaitAsync(TestWaits.Liveness);
        }
        finally
        {
            // StopAsync awaits the worker loops, and the writeback runs under
            // CancellationToken.None, so the whole failure-recording path — including the
            // ingest write — has completed by the time the assertions read the row. No
            // polling: a second context querying concurrently would race the worker on the
            // shared in-memory SQLite connection.
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }

        await using var verify = _db.ContextFor(null);
        var ingest = await verify.EmailIngests.AsNoTracking().SingleAsync(e => e.Id == ingestId);
        Assert.Equal(ExtractionWorker.DeadLetterParseStatus, ingest.ParseStatus);
        Assert.NotNull(ingest.ParsedAt);
        // The 50-char ParseStatus column is a hard budget; a value that silently truncates
        // would corrupt every equality read of this state.
        Assert.True(ExtractionWorker.DeadLetterParseStatus.Length <= 50);
    }

    [Fact]
    public async Task ANonFinalFailureLeavesTheIngestQueuedForTheRetry()
    {
        // Attempts 1 of 3: the job goes back to Pending with backoff. Announcing a failure
        // the queue is still going to retry would flap the triage screen on every attempt.
        var (job, ingestId) = await SeedEmailJobAsync(jobId: 902, attempts: 1, maxAttempts: 3);
        var queue = new RecordingQueue(job);
        using var services = BuildServices(queue);
        var worker = CreateWorker(services);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.RetryableFailure.Task.WaitAsync(TestWaits.Liveness);
        }
        finally
        {
            // StopAsync awaits the worker loops, so the whole failure-recording path has run
            // by the time the assertion below reads the row.
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }

        await using var verify = _db.ContextFor(null);
        Assert.Equal("Queued",
            (await verify.EmailIngests.AsNoTracking().SingleAsync(e => e.Id == ingestId)).ParseStatus);
    }

    [Fact]
    public async Task ASuccessfulRetryFlipsTheDeadLetteredIngestBackThroughResolveIngest()
    {
        // Dead-letter recovery replay: once the recovered job persists a lead, the EXISTING
        // success path (LeadPersister.ResolveIngestAsync) must overwrite the failed state —
        // the failure writeback only ever overwrites Queued/Pending, never the reverse.
        long ingestId;
        await using (var seed = _db.ContextFor(null))
        {
            Seed.BusinessUnit(seed, 1);
            Seed.EmailConfig(seed, 100, 1);
            var ingest = Seed.EmailIngest(seed, 500, 100, ExtractionWorker.DeadLetterParseStatus);
            await seed.SaveChangesAsync();
            ingestId = ingest.Id;

            var corpus = DocumentCorpus.Create(1, RetryJob().BatchId, CorpusSourceType.Email);
            seed.Add(corpus);
            await seed.SaveChangesAsync();
            var source = SourceDocument.Create(1, corpus.Id, RetryJob().ContentHash,
                "enquiry_body.txt", "text/plain", "test", "cleared/enquiry_body.txt", "v1", 1);
            source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
            seed.Add(source);
            await seed.SaveChangesAsync();
        }

        var storagePath = Path.Combine(Path.GetTempPath(), $"writeback-test-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(storagePath, "Subject: RFQ\n\nbody");
        var job = RetryJob(storagePath);
        try
        {
            await new ExtractionJobMetadata
            {
                EmailIngestId = ingestId,
                FromEmail = "buyer@customer.com",
                Subject = "RFQ for pumps",
                LeadSource = "Email",
                EmailSource = "Text Only"
            }.SaveAsync(storagePath, job.BusinessUnitId);

            await using (var ctx = _db.ContextFor(null))
            {
                var persister = new LeadPersister(ctx, new NoopLogger<LeadPersister>());
                await persister.PersistAsync(job, new ChunkedExtractionOutcome
                {
                    Status = ExtractionOutcomeStatus.Ok,
                    Result = Ext.Result(Ext.Items(1, 0.9), 0.9) with { Rfqno = "RFQ-77" },
                    ExpectedItemCount = 1,
                    ExtractedItemCount = 1
                });
            }

            await using var verify = _db.ContextFor(null);
            var ingest = await verify.EmailIngests.AsNoTracking().SingleAsync(e => e.Id == ingestId);
            Assert.Equal("NeedsReview", ingest.ParseStatus);
            Assert.NotNull(ingest.ParsedAt);
        }
        finally
        {
            try { File.Delete(storagePath); } catch { }
            try { File.Delete(ExtractionJobMetadata.SidecarPath(storagePath, job.BusinessUnitId)); } catch { }
        }
    }

    // ------------------------------------------------------------------------ test plumbing

    private static ExtractionJob RetryJob(string? storagePath = null) => new()
    {
        Id = 1,
        BatchId = new Guid("6f1f66a1-6b7e-4f34-9d5e-000000000077"),
        BusinessUnitId = 1,
        SourceType = ExtractionSourceType.ManualUpload,
        ContentHash = new string('a', 64),
        StoragePath = storagePath ?? "/nonexistent/extraction/doc.txt",
        FileName = "enquiry_body.txt",
        FileType = "txt",
        Attempts = 1
    };

    /// <summary>
    /// Seeds the full email lineage — BU, mailbox, "Queued" ingest, and an intake occurrence
    /// whose SourceMetadataJson carries the provenance sidecar naming the ingest — and returns
    /// a claimable Email job bound to that occurrence, exactly as the email door creates them.
    /// </summary>
    private async Task<(ExtractionJob Job, long IngestId)> SeedEmailJobAsync(
        long jobId, int attempts, int maxAttempts)
    {
        await using var ctx = _db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Tenant);
        Seed.EmailConfig(ctx, 10, Tenant);
        var ingest = Seed.EmailIngest(ctx, 600, 10, "Queued");
        await ctx.SaveChangesAsync();

        var batchId = Guid.NewGuid();
        var corpus = DocumentCorpus.Create(Tenant, batchId, CorpusSourceType.Email);
        ctx.Add(corpus);
        await ctx.SaveChangesAsync();
        var source = SourceDocument.Create(Tenant, corpus.Id, new string('e', 64),
            "enquiry_body.txt", "text/plain", "test-evidence", "cleared/enquiry_body.txt", "v1", 20);
        source.MarkSecurityStatus(DocumentSecurityStatus.Cleared);
        ctx.Add(source);
        await ctx.SaveChangesAsync();
        var occurrence = SourceDocumentOccurrence.Create(
            Tenant, source.Id, corpus.Id, $"writeback-test:{jobId}",
            JsonSerializer.Serialize(new
            {
                fileName = "enquiry_body.txt",
                sourceType = ExtractionSourceType.Email.ToString(),
                metadata = new ExtractionJobMetadata
                {
                    EmailIngestId = ingest.Id,
                    FromEmail = "ahmed@alnoortrading.ae",
                    Subject = "RFQ",
                    LeadSource = "Email",
                    EmailSource = "Text Only",
                    LogicalGroupKey = "email:writeback@sender.example"
                }
            }));
        occurrence.BindExtractionJob(jobId); // Queued — claimable by the worker
        ctx.Add(occurrence);
        await ctx.SaveChangesAsync();

        return (new ExtractionJob
        {
            Id = jobId,
            BusinessUnitId = Tenant,
            BatchId = batchId,
            SourceType = ExtractionSourceType.Email,
            SourceDocumentOccurrenceId = occurrence.Id,
            ContentHash = new string('e', 64),
            StoragePath = "memory://evidence/object",
            FileName = "enquiry_body.txt",
            FileType = "txt",
            Status = ExtractionStatus.Leased,
            Attempts = attempts,
            MaxAttempts = maxAttempts,
            NextAttemptAt = DateTime.UtcNow
        }, ingest.Id);
    }

    private ServiceProvider BuildServices(RecordingQueue queue)
    {
        var accessor = new TenantScopeAccessor();
        Accessor = accessor;
        return new ServiceCollection()
            .AddLogging()
            .AddSingleton<ITenantScopeAccessor>(accessor)
            // Reads the AMBIENT tenant at resolution time, exactly as HttpTenantContext does —
            // the worker's fail-closed scope guard requires the pushed tenant to reach the
            // DbContext this scope resolves.
            .AddScoped(_ => _db.ContextFor(accessor.BusinessUnitId))
            .AddSingleton<IExtractionQueue>(queue)
            .AddSingleton<IExtractionDocumentReader>(new ProductionDocumentReader(
                NullLogger<ProductionDocumentReader>.Instance,
                new TestEnvironment(AppContext.BaseDirectory),
                new MemoryStorage("Please quote 40 nos cable tray 300mm.\n"u8.ToArray())))
            .AddSingleton<IChunkedExtractionService>(new ChunkedExtractionService(
                // Local provider with NO scripted responses: every chunk fails, which is the
                // retryable failure path — the one that dead-letters on the final attempt.
                new StubLlm(ERP_RFQ_Automation.AI.AiProviderClass.Local),
                new CanonicalRfqNormalizer(),
                new NoopLogger<ChunkedExtractionService>()))
            .AddSingleton<ILeadPersister>(new UnusedPersister())
            .BuildServiceProvider();
    }

    private TenantScopeAccessor Accessor { get; set; } = null!;

    private ExtractionWorker CreateWorker(ServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(),
        new ExtractionWorkerOptions
        {
            WorkerCount = 1,
            MaxConcurrentLlmCalls = 1,
            PerTenantConcurrencyCap = 1,
            LeaseDuration = TimeSpan.FromSeconds(30),
            IdlePollDelay = TimeSpan.FromMilliseconds(25)
        },
        services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExtractionWorker>>(),
        Accessor);

    /// <summary>Hands out one job, then records which failure primitive the worker used.</summary>
    private sealed class RecordingQueue : IExtractionQueue
    {
        private readonly ExtractionJob _job;
        private int _claimed;

        public RecordingQueue(ExtractionJob job) => _job = job;

        public TaskCompletionSource<string> RetryableFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> PermanentFailure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExtractionJob?> ClaimAsync(
            string workerId, TimeSpan leaseDuration, int perTenantCap, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0)
                return Task.FromResult<ExtractionJob?>(null);
            _job.LeaseExpiresAt = DateTime.UtcNow.Add(leaseDuration);
            return Task.FromResult<ExtractionJob?>(_job);
        }

        public Task<bool> RenewLeaseAsync(
            long jobId, string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> SetStatusAsync(
            long jobId, string workerId, int leaseAttempt, ExtractionStatus status, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> FailAsync(
            long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
        {
            RetryableFailure.TrySetResult(error);
            return Task.FromResult(true);
        }

        public Task<bool> FailPermanentlyAsync(
            long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
        {
            PermanentFailure.TrySetResult(error);
            return Task.FromResult(true);
        }

        public Task<EnqueueResult> EnqueueAsync(EnqueueExtractionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> CompleteAsync(
            long jobId, string workerId, int leaseAttempt, long? resultLeadId, CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class UnusedPersister : ILeadPersister
    {
        public Task<long> PersistAsync(
            ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
            => throw new NotSupportedException("The failure tests never persist a lead.");

        public Task<long?> PersistAndCompleteAsync(
            ExtractionJob job, ChunkedExtractionOutcome outcome, IExtractionQueue queue,
            string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
            => throw new NotSupportedException("The failure tests never persist a lead.");
    }

    private sealed class MemoryStorage(byte[] content) : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(
            long businessUnitId, string zone, string sha256, string extension,
            ReadOnlyMemory<byte> value, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(
            string storageUri, string expectedSha256, CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(content, writable: false));
    }

    private sealed class TestEnvironment(string? contentRootPath = null) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = contentRootPath ?? Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
