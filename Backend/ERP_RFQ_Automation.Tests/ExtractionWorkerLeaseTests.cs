using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.MultiTenancy;
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
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExtractionWorker>>(),
            new TenantScopeAccessor());

        var startedAt = DateTime.UtcNow;
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await queue.RenewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await extractor.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.InRange(DateTime.UtcNow - startedAt, TimeSpan.Zero, TimeSpan.FromSeconds(6));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
            worker.Dispose();
            await services.DisposeAsync();
        }
    }

    [Fact]
    public async Task ClaimedJob_IsProcessedInsideItsTenantScope()
    {
        const long businessUnitId = 91_007;
        var tenantScope = new TenantScopeAccessor();
        var queue = new TenantObservingQueue(businessUnitId, tenantScope);
        var reader = new TenantObservingReader(tenantScope);
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IExtractionQueue>(queue)
            .AddSingleton<IExtractionDocumentReader>(reader)
            .AddSingleton<IChunkedExtractionService>(new CancellationObservingExtractor())
            .AddSingleton<ILeadPersister>(new UnusedLeadPersister())
            .BuildServiceProvider();
        var worker = new ExtractionWorker(
            services.GetRequiredService<IServiceScopeFactory>(),
            new ExtractionWorkerOptions
            {
                WorkerCount = 1,
                LeaseDuration = TimeSpan.FromSeconds(10),
                IdlePollDelay = TimeSpan.FromMilliseconds(25)
            },
            services.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ExtractionWorker>>(),
            tenantScope);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await reader.Observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.Failed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(queue.TenantAtClaim);
            Assert.Equal(businessUnitId, reader.TenantAtRead);
            Assert.Equal(businessUnitId, queue.TenantAtFailure);
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

        public Task<bool> CompleteAsync(long jobId, string workerId, int leaseAttempt, long? resultLeadId, CancellationToken ct = default)
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

    private sealed class TenantObservingReader(ITenantScopeAccessor tenantScope) : IExtractionDocumentReader
    {
        public TaskCompletionSource Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long? TenantAtRead { get; private set; }

        public Task<DocumentExtractionInput> ReadAsync(ExtractionJob job, CancellationToken ct = default)
        {
            TenantAtRead = tenantScope.BusinessUnitId;
            Observed.TrySetResult();
            throw new InvalidDataException("stop after tenant observation");
        }
    }

    private sealed class TenantObservingQueue(long businessUnitId, ITenantScopeAccessor tenantScope)
        : IExtractionQueue
    {
        private int _claimed;
        public long? TenantAtClaim { get; private set; }
        public long? TenantAtFailure { get; private set; }
        public TaskCompletionSource Failed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<ExtractionJob?> ClaimAsync(
            string workerId, TimeSpan leaseDuration, int perTenantCap, CancellationToken ct = default)
        {
            TenantAtClaim = tenantScope.BusinessUnitId;
            if (Interlocked.Exchange(ref _claimed, 1) != 0)
                return Task.FromResult<ExtractionJob?>(null);
            return Task.FromResult<ExtractionJob?>(new ExtractionJob
            {
                Id = 99,
                BusinessUnitId = businessUnitId,
                BatchId = Guid.NewGuid(),
                ContentHash = new string('a', 64),
                StoragePath = "evidence://test",
                Status = ExtractionStatus.Leased,
                Attempts = 1,
                MaxAttempts = 3,
                LeaseExpiresAt = DateTime.UtcNow.Add(leaseDuration),
                NextAttemptAt = DateTime.UtcNow
            });
        }

        public Task<bool> FailAsync(
            long jobId, string workerId, int leaseAttempt, string error, CancellationToken ct = default)
        {
            TenantAtFailure = tenantScope.BusinessUnitId;
            Failed.TrySetResult();
            return Task.FromResult(true);
        }

        public Task<bool> RenewLeaseAsync(long jobId, string workerId, int leaseAttempt, TimeSpan leaseDuration, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<bool> SetStatusAsync(long jobId, string workerId, int leaseAttempt, ExtractionStatus status, CancellationToken ct = default)
            => Task.FromResult(true);
        public Task<EnqueueResult> EnqueueAsync(EnqueueExtractionRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<bool> CompleteAsync(long jobId, string workerId, int leaseAttempt, long? resultLeadId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
