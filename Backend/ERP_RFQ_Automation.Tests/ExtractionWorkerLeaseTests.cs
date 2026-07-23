using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests;

public sealed class ExtractionWorkerLeaseTests
{
    [Fact]
    public async Task HungHeartbeatCancelsWorkByKnownLeaseDeadline()
    {
        var leaseDuration = TimeSpan.FromMilliseconds(1_500);
        var queue = new HangingRenewalQueue(leaseDuration);
        var extractor = new CancellationObservingExtractor();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IExtractionQueue>(queue)
            .AddSingleton<IExtractionDocumentReader, StubDocumentReader>()
            .AddSingleton<IChunkedExtractionService>(extractor)
            .AddSingleton<ILeadPersister, UnusedLeadPersister>()
            .BuildServiceProvider();

        var worker = new ExtractionWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            new ExtractionWorkerOptions
            {
                WorkerCount = 1,
                MaxConcurrentLlmCalls = 1,
                PerTenantConcurrencyCap = 1,
                LeaseDuration = leaseDuration,
                IdlePollDelay = TimeSpan.FromSeconds(10)
            },
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExtractionWorker>>());

        var startedAt = DateTime.UtcNow;
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.RenewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await extractor.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.InRange(DateTime.UtcNow - startedAt, TimeSpan.Zero, TimeSpan.FromSeconds(2.5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            await services.DisposeAsync();
        }
    }

    private sealed class HangingRenewalQueue : IExtractionQueue
    {
        private readonly ExtractionJob _job;
        private readonly TimeSpan _leaseDuration;
        private int _claimed;

        public HangingRenewalQueue(TimeSpan leaseDuration)
        {
            _leaseDuration = leaseDuration;
            _job = new ExtractionJob
            {
                Id = 42,
                BusinessUnitId = 7,
                BatchId = Guid.NewGuid(),
                ContentHash = new string('a', 64),
                StoragePath = "/tmp/hung-heartbeat.txt",
                SourceType = ExtractionSourceType.ManualUpload,
                Status = ExtractionStatus.Leased,
                Attempts = 1,
                MaxAttempts = 3,
                NextAttemptAt = DateTime.UtcNow
            };
        }

        public TaskCompletionSource RenewalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExtractionJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, int perTenantCap, CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _claimed, 1) != 0)
                return Task.FromResult<ExtractionJob?>(null);
            _job.LeaseExpiresAt = DateTime.UtcNow.Add(_leaseDuration);
            return Task.FromResult<ExtractionJob?>(_job);
        }

        public async Task<bool> RenewLeaseAsync(long jobId, string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
        {
            RenewalStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return true;
        }

        public Task<bool> SetStatusAsync(long jobId, string workerId, int leaseAttempt, ExtractionStatus status, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> FailAsync(long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
            => Task.FromResult(false);

        public Task<EnqueueResult> EnqueueAsync(EnqueueExtractionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> CompleteAsync(long jobId, string workerId, int leaseAttempt, long resultLeadId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubDocumentReader : IExtractionDocumentReader
    {
        public Task<DocumentExtractionInput> ReadAsync(ExtractionJob job, CancellationToken ct = default)
            => Task.FromResult(new DocumentExtractionInput
            {
                BusinessUnitId = job.BusinessUnitId,
                SourceDocumentName = "hung-heartbeat.txt",
                IsStructured = false,
                HeaderText = "RFQ",
                LineItemRegions = ["item"]
            });
    }

    private sealed class CancellationObservingExtractor : IChunkedExtractionService
    {
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ChunkedExtractionOutcome> ExtractAsync(DocumentExtractionInput input, CancellationToken ct = default)
            => ExtractUnstructuredAsync(input, ct);

        public async Task<ChunkedExtractionOutcome> ExtractUnstructuredAsync(DocumentExtractionInput input, CancellationToken ct = default)
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                throw new InvalidOperationException("Unreachable");
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult();
                throw;
            }
        }

        public Task<ChunkedExtractionOutcome> ExtractStructuredAsync(
            IReadOnlyList<RfqSpreadsheetRow> rows, long businessUnitId, string sourceName, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class UnusedLeadPersister : ILeadPersister
    {
        public Task<long> PersistAsync(ExtractionJob job, ChunkedExtractionOutcome outcome, CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<long?> PersistAndCompleteAsync(
            ExtractionJob job, ChunkedExtractionOutcome outcome, IExtractionQueue queue,
            string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
