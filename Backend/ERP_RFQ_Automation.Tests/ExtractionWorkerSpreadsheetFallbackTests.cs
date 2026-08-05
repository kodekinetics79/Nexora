using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Worker-level regression for the live production dead-letter bug: an .xls whose
/// layout the deterministic mapper does not recognize must ride the unstructured path,
/// and when the tenant has no authorized external provider the allow-list gate must
/// fail-close into a RETRYABLE hold (queue.FailAsync with a legible reason) — the job
/// must never be dead-lettered via FailPermanentlyAsync on attempt 1. Recognized
/// layouts must still complete deterministically without any LLM involvement.
/// </summary>
public sealed class ExtractionWorkerSpreadsheetFallbackTests
{
    [Fact]
    public async Task UnrecognizedXls_UnauthorizedExternalProvider_IsHeldRetryable_NeverDeadLettered()
    {
        var fixture = ReadFixture("unrecognized-layout-rfq.xls");
        var queue = new RecordingQueue(CreateJob(801, "C001046140.xls"));
        var llm = new StubLlm(AiProviderClass.External, Ext.Result(Ext.Items(3, 0.9), 0.9));
        using var services = BuildServices(queue, fixture, llm, new RecordingPersister());
        var worker = CreateWorker(services);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var recordedError = await queue.RetryableFailure.Task.WaitAsync(TimeSpan.FromSeconds(15));

            // Terminal state contract: retryable hold, NEVER a first-attempt dead-letter.
            Assert.False(queue.PermanentFailure.Task.IsCompleted);

            // The recorded reason is honest and specific: the spreadsheet was read, the
            // layout was not recognized, and the fail-closed hold explains what is next.
            Assert.Contains("The XLS spreadsheet was read successfully", recordedError);
            Assert.Contains("column layout was not recognized", recordedError);
            Assert.Contains("blocked for unstructured documents", recordedError);
            Assert.Contains("human review", recordedError);

            // Fail-closed means zero bytes of document content left the boundary.
            Assert.Equal(0, llm.CallCount);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    [Fact]
    public async Task RecognizedXls_StillCompletesDeterministically_WithoutTouchingTheLlm()
    {
        var fixture = ReadFixture("recognized-layout-rfq.xls");
        var queue = new RecordingQueue(CreateJob(802, "recognized.xls"));
        var llm = new StubLlm(AiProviderClass.External); // any call would return null and fail the run
        var persister = new RecordingPersister();
        using var services = BuildServices(queue, fixture, llm, persister);
        var worker = CreateWorker(services);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var outcome = await persister.Persisted.Task.WaitAsync(TimeSpan.FromSeconds(15));

            Assert.Equal(0, llm.CallCount); // structured fast-path fully preserved
            Assert.NotEqual(ExtractionOutcomeStatus.Failed, outcome.Status);
            Assert.NotNull(outcome.CanonicalImport);
            Assert.Equal(ExtractionProcessingPath.DeterministicRules, outcome.ProcessingPath);
            Assert.False(queue.RetryableFailure.Task.IsCompleted);
            Assert.False(queue.PermanentFailure.Task.IsCompleted);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
        }
    }

    // ---- harness ----------------------------------------------------------

    private static byte[] ReadFixture(string name)
        => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    private static ExtractionJob CreateJob(long id, string fileName) => new()
    {
        Id = id,
        BusinessUnitId = 7,
        BatchId = Guid.NewGuid(),
        SourceType = ExtractionSourceType.ManualUpload,
        ContentHash = new string('e', 64),
        StoragePath = "memory://evidence/object",
        FileName = fileName,
        FileType = "xls",
        Status = ExtractionStatus.Leased,
        Attempts = 1,
        MaxAttempts = 5,
        NextAttemptAt = DateTime.UtcNow
    };

    private static ServiceProvider BuildServices(
        RecordingQueue queue, byte[] documentBytes, StubLlm llm, RecordingPersister persister)
        => new ServiceCollection()
            .AddLogging()
            .AddSingleton<IExtractionQueue>(queue)
            .AddSingleton<IExtractionDocumentReader>(new ProductionDocumentReader(
                NullLogger<ProductionDocumentReader>.Instance,
                new TestEnvironment(AppContext.BaseDirectory),
                new MemoryStorage(documentBytes)))
            .AddSingleton<IChunkedExtractionService>(new ChunkedExtractionService(
                llm, new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>()))
            .AddSingleton<ILeadPersister>(persister)
            .BuildServiceProvider();

    private static ExtractionWorker CreateWorker(ServiceProvider services) => new(
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
        new TenantScopeAccessor());

    /// <summary>
    /// Hands out one job, then records which failure primitive the worker used.
    /// FailPermanentlyAsync is implemented EXPLICITLY — the interface's default member
    /// delegates to FailAsync, which would make dead-letters indistinguishable here.
    /// </summary>
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

    private sealed class RecordingPersister : ILeadPersister
    {
        public TaskCompletionSource<ChunkedExtractionOutcome> Persisted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<long> PersistAsync(
            ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<long?> PersistAndCompleteAsync(
            ExtractionJob job,
            ChunkedExtractionOutcome outcome,
            IExtractionQueue queue,
            string workerId,
            int leaseAttempt,
            TimeSpan leaseDuration,
            CancellationToken ct = default)
        {
            Persisted.TrySetResult(outcome);
            return Task.FromResult<long?>(55);
        }
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
