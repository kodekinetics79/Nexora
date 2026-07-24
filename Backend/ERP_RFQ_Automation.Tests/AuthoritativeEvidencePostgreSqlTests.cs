using System.Text;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ERP_RFQ_Automation.Tests;

[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class AuthoritativeEvidencePostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;

    public AuthoritativeEvidencePostgreSqlTests(PostgreSqlTestDatabase database) => _database = database;

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ClearedCsv_PersistsAtomicTenantBoundEvidenceGraph()
    {
        var tenantId = NewTenantId();
        var bytes = ValidCsv();
        var root = NewStorageRoot();
        try
        {
            long jobId;
            string hash;
            await using (var context = _database.ContextFor(null))
            {
                SeedTenant(context, tenantId);
                await context.SaveChangesAsync();

                var queue = NewQueue(context);
                var ingestion = NewIngestion(context, queue, root, ClearedInspection());
                var ingested = await ingestion.IngestAsync(bytes, "customer-rfq.csv", tenantId,
                    ExtractionSourceType.ManualUpload, priority: int.MaxValue);
                jobId = ingested.JobId;
                hash = ingested.ContentHash;

                var job = await queue.ClaimAsync("evidence-sit", TimeSpan.FromMinutes(2), 1);
                Assert.NotNull(job);
                Assert.Equal(jobId, job!.Id);
                Assert.True(await queue.SetStatusAsync(job.Id, "evidence-sit", job.Attempts,
                    ExtractionStatus.Extracting));
                Assert.True(await queue.SetStatusAsync(job.Id, "evidence-sit", job.Attempts,
                    ExtractionStatus.Persisting));

                var rows = new NativeSpreadsheetParser().ParseCsv(bytes, "customer-rfq.csv");
                var extractor = new ChunkedExtractionService(
                    new StubLlm(), new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());
                var outcome = await extractor.ExtractStructuredAsync(rows, tenantId, "customer-rfq.csv");
                Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);

                var persister = new LeadPersister(context, new NoopLogger<LeadPersister>());
                var leadId = await persister.PersistAndCompleteAsync(job, outcome, queue,
                    "evidence-sit", job.Attempts, TimeSpan.FromMinutes(2));
                Assert.NotNull(leadId);
            }

            long fieldEvidenceId;
            await using (var context = _database.ContextFor(null))
            {
                var source = await context.Set<SourceDocument>()
                    .SingleAsync(x => x.BusinessUnitId == tenantId && x.ContentHash == hash);
                Assert.Equal(DocumentSecurityStatus.Cleared, source.SecurityStatus);
                Assert.Equal(DocumentProcessingStatus.Completed, source.ProcessingStatus);
                Assert.Equal(1, source.PageCount);

                var occurrence = await context.Set<SourceDocumentOccurrence>()
                    .SingleAsync(x => x.BusinessUnitId == tenantId && x.ExtractionJobId == jobId);
                Assert.Contains("immutableObjects", occurrence.SourceMetadataJson);
                Assert.Contains("quarantine", occurrence.SourceMetadataJson);
                Assert.Contains("selected", occurrence.SourceMetadataJson);

                var run = await context.Set<ExtractionRun>()
                    .SingleAsync(x => x.BusinessUnitId == tenantId && x.ExtractionJobId == jobId);
                Assert.Equal(ExtractionRunStatus.Completed, run.Status);
                Assert.Equal(1, run.PageCount);
                Assert.Equal(2, run.LineItemCount);
                Assert.True(run.EvidenceCount >= 20);

                var inquiry = await context.Set<CanonicalInquiry>()
                    .Include(x => x.LineItems)
                    .SingleAsync(x => x.BusinessUnitId == tenantId);
                Assert.NotNull(inquiry.LeadId);
                Assert.Equal(CanonicalInquiryStatus.Validated, inquiry.Status);
                Assert.Equal(2, inquiry.LineItems.Count);
                Assert.All(inquiry.LineItems, line => Assert.NotNull(line.LeadItemId));

                var page = await context.Set<DocumentPage>()
                    .SingleAsync(x => x.BusinessUnitId == tenantId);
                Assert.Equal(DocumentPageKind.CsvSheet, page.PageKind);
                Assert.Equal("CSV", page.SheetName);

                var evidence = await context.Set<FieldEvidence>()
                    .Include(x => x.Region)
                    .FirstAsync(x => x.BusinessUnitId == tenantId && x.FieldName == "ProductName");
                fieldEvidenceId = evidence.Id;
                Assert.StartsWith("'CSV'!E", evidence.Region.SourceAddress);
                Assert.True(evidence.Region.RowNumber >= 2);

                var job = await context.Set<ExtractionJob>().SingleAsync(x => x.Id == jobId);
                Assert.Equal(ExtractionStatus.Succeeded, job.Status);
                Assert.NotNull(job.ResultLeadId);
            }

            await using (var otherTenant = _database.TenantContextWithRls(tenantId + 1))
            {
                Assert.Empty(await otherTenant.Set<SourceDocument>().AsNoTracking()
                    .Where(x => x.ContentHash == hash).ToListAsync());
                Assert.Empty(await otherTenant.Set<FieldEvidence>().AsNoTracking()
                    .Where(x => x.Id == fieldEvidenceId).ToListAsync());
            }

            await using (var context = _database.ContextFor(null))
            {
                var failure = await Assert.ThrowsAsync<PostgresException>(() =>
                    context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE field_evidence SET normalized_value = 'tampered' WHERE id = {fieldEvidenceId}"));
                Assert.Equal("55000", failure.SqlState);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task QuarantinedSource_CannotBeQueuedByLaterClearScan()
    {
        var tenantId = NewTenantId();
        var bytes = ValidCsv();
        var root = NewStorageRoot();
        try
        {
            await using var context = _database.ContextFor(null);
            SeedTenant(context, tenantId);
            await context.SaveChangesAsync();
            var queue = NewQueue(context);

            var quarantined = NewIngestion(context, queue, root, new FileInspectionResult(
                FileInspectionStatus.Quarantined, "text/csv", bytes.Length,
                "Scanner unavailable.", "clamav", null));
            await Assert.ThrowsAsync<DocumentInspectionException>(() => quarantined.IngestAsync(
                bytes, "customer-rfq.csv", tenantId, ExtractionSourceType.ManualUpload));

            context.ChangeTracker.Clear();
            var replay = NewIngestion(context, queue, root, ClearedInspection());
            var error = await Assert.ThrowsAsync<DocumentInspectionException>(() => replay.IngestAsync(
                bytes, "customer-rfq.csv", tenantId, ExtractionSourceType.ExcelTemplate));
            Assert.Equal(FileInspectionStatus.Quarantined, error.Inspection.Status);

            Assert.Empty(await context.Set<ExtractionJob>()
                .Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            var source = await context.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(DocumentSecurityStatus.Quarantined, source.SecurityStatus);
            Assert.Equal(2, await context.Set<SourceDocumentOccurrence>()
                .CountAsync(x => x.BusinessUnitId == tenantId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task UnstructuredSuccess_ReconcilesQueueSourceCorpusAndExtractionRun()
    {
        var tenantId = NewTenantId();
        var bytes = ValidCsv();
        var root = NewStorageRoot();
        try
        {
            long jobId;
            await using (var context = _database.ContextFor(null))
            {
                SeedTenant(context, tenantId);
                await context.SaveChangesAsync();
                var queue = NewQueue(context);
                var ingested = await NewIngestion(context, queue, root, ClearedInspection()).IngestAsync(
                    bytes, "unstructured.txt", tenantId, ExtractionSourceType.ManualUpload,
                    priority: int.MaxValue);
                jobId = ingested.JobId;
                var job = (await queue.ClaimAsync("unstructured-sit", TimeSpan.FromMinutes(2), 1))!;
                Assert.True(await queue.SetStatusAsync(job.Id, "unstructured-sit", job.Attempts,
                    ExtractionStatus.Extracting));
                Assert.True(await queue.SetStatusAsync(job.Id, "unstructured-sit", job.Attempts,
                    ExtractionStatus.Persisting));
                var outcome = new ChunkedExtractionOutcome
                {
                    Status = ExtractionOutcomeStatus.Ok,
                    Result = Ext.Result(Ext.Items(1, 0.95), 0.95) with { Rfqno = "RFQ-UNSTRUCTURED" },
                    ExpectedItemCount = 1,
                    ExtractedItemCount = 1
                };

                var leadId = await new LeadPersister(context, new NoopLogger<LeadPersister>())
                    .PersistAndCompleteAsync(job, outcome, queue, "unstructured-sit", job.Attempts,
                        TimeSpan.FromMinutes(2));
                Assert.NotNull(leadId);
            }

            await using var verify = _database.ContextFor(null);
            var source = await verify.Set<SourceDocument>().Include(x => x.Corpus)
                .SingleAsync(x => x.BusinessUnitId == tenantId && x.ExtractionJobId == jobId);
            var run = await verify.Set<ExtractionRun>()
                .SingleAsync(x => x.BusinessUnitId == tenantId && x.ExtractionJobId == jobId);
            var jobState = await verify.Set<ExtractionJob>().SingleAsync(x => x.Id == jobId);
            Assert.Equal(DocumentProcessingStatus.ReviewRequired, source.ProcessingStatus);
            Assert.Equal(CorpusStatus.ReviewRequired, source.Corpus.Status);
            Assert.Equal(ExtractionRunStatus.Completed, run.Status);
            Assert.Equal("llm-unstructured/v1", run.ParserVersion);
            Assert.Equal(ExtractionStatus.Succeeded, jobState.Status);
            Assert.NotNull(jobState.ResultLeadId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ConcurrentIdenticalUploads_CreateOneJobAndOneSource()
    {
        var tenantId = NewTenantId();
        var bytes = ValidCsv();
        var root = NewStorageRoot();
        try
        {
            await using (var seed = _database.ContextFor(null))
            {
                SeedTenant(seed, tenantId);
                await seed.SaveChangesAsync();
            }

            await using var firstContext = _database.ContextFor(null);
            await using var secondContext = _database.ContextFor(null);
            var firstQueue = NewQueue(firstContext);
            var secondQueue = NewQueue(secondContext);
            var first = NewIngestion(firstContext, firstQueue, root, ClearedInspection());
            var second = NewIngestion(secondContext, secondQueue, root, ClearedInspection());

            var results = await Task.WhenAll(
                first.IngestAsync(bytes, "one.csv", tenantId, ExtractionSourceType.ExcelTemplate),
                second.IngestAsync(bytes, "two.csv", tenantId, ExtractionSourceType.ManualUpload));
            Assert.Single(results, x => x.Outcome == EnqueueOutcome.Enqueued);
            Assert.Single(results, x => x.Outcome == EnqueueOutcome.Duplicate);
            Assert.Single(results.Select(x => x.JobId).Distinct());

            await using var verify = _database.ContextFor(null);
            Assert.Equal(1, await verify.Set<ExtractionJob>().CountAsync(x => x.BusinessUnitId == tenantId));
            Assert.Equal(1, await verify.Set<SourceDocument>().CountAsync(x => x.BusinessUnitId == tenantId));
            Assert.Equal(2, await verify.Set<SourceDocumentOccurrence>()
                .CountAsync(x => x.BusinessUnitId == tenantId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task FencedCompletionFailure_RollsBackLeadAndEvidenceGraph()
    {
        var tenantId = NewTenantId();
        var bytes = ValidCsv();
        var root = NewStorageRoot();
        try
        {
            long jobId;
            await using (var context = _database.ContextFor(null))
            {
                SeedTenant(context, tenantId);
                await context.SaveChangesAsync();
                var realQueue = NewQueue(context);
                var ingested = await NewIngestion(context, realQueue, root, ClearedInspection()).IngestAsync(
                    bytes, "rollback.csv", tenantId, ExtractionSourceType.ManualUpload,
                    priority: int.MaxValue);
                jobId = ingested.JobId;

                var job = (await realQueue.ClaimAsync("rollback-sit", TimeSpan.FromMinutes(2), 1))!;
                Assert.True(await realQueue.SetStatusAsync(job.Id, "rollback-sit", job.Attempts,
                    ExtractionStatus.Extracting));
                Assert.True(await realQueue.SetStatusAsync(job.Id, "rollback-sit", job.Attempts,
                    ExtractionStatus.Persisting));

                var rows = new NativeSpreadsheetParser().ParseCsv(bytes, "rollback.csv");
                var extractor = new ChunkedExtractionService(
                    new StubLlm(), new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());
                var outcome = await extractor.ExtractStructuredAsync(rows, tenantId, "rollback.csv");
                var persister = new LeadPersister(context, new NoopLogger<LeadPersister>());

                await Assert.ThrowsAsync<InvalidOperationException>(() => persister.PersistAndCompleteAsync(
                    job, outcome, new CompletionRejectingQueue(realQueue), "rollback-sit", job.Attempts,
                    TimeSpan.FromMinutes(2)));
            }

            await using var verify = _database.ContextFor(null);
            Assert.Empty(await verify.Leads.Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            Assert.Empty(await verify.Set<ExtractionRun>().Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            Assert.Empty(await verify.Set<DocumentPage>().Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            Assert.Empty(await verify.Set<CanonicalInquiry>().Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            Assert.Empty(await verify.Set<FieldEvidence>().Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            var source = await verify.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(DocumentProcessingStatus.Received, source.ProcessingStatus);
            Assert.Equal(ExtractionStatus.Persisting,
                (await verify.Set<ExtractionJob>().SingleAsync(x => x.Id == jobId)).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static DocumentIngestionService NewIngestion(
        ErpRfqAutomationContext context,
        IExtractionQueue queue,
        string root,
        FileInspectionResult inspection)
    {
        var files = new LocalFileStorage(root, root);
        return new DocumentIngestionService(queue, new LocalEvidenceObjectStorage(files),
            new FixedInspectionService(inspection), context, new NoopLogger<DocumentIngestionService>());
    }

    private static ExtractionQueue NewQueue(ErpRfqAutomationContext context) =>
        new(context, new NoopLogger<ExtractionQueue>());

    private static FileInspectionResult ClearedInspection() => new(
        FileInspectionStatus.Cleared, "text/csv", ValidCsv().Length,
        "Inspection and malware scan passed.", "test-scanner", "clean");

    private static byte[] ValidCsv() => Encoding.UTF8.GetBytes(
        "RFQ No,Buyer Name,Received Date,Bid Closing Date,Product Name,Quantity,Unit Price,Currency,Manufacturer,MPN,Lead Time\n" +
        "RFQ-SIT-1,Acme Procurement,2026-07-23,2026-08-15,Pressure Sensor,5,125.50,USD,Contoso,PS-100,14\n" +
        "RFQ-SIT-1,Acme Procurement,2026-07-23,2026-08-15,Control Valve,2,840.00,USD,Fabrikam,CV-200,21\n");

    private static void SeedTenant(ErpRfqAutomationContext context, long tenantId)
    {
        Seed.EnsureBusinessUnit(context, tenantId);
        Seed.EmailConfig(context, tenantId * 10 + 1, tenantId);
    }

    private static long NewTenantId() => Random.Shared.Next(2_000_000, 8_000_000);

    private static string NewStorageRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexora-evidence-sit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private sealed class FixedInspectionService : IFileInspectionService
    {
        private readonly FileInspectionResult _result;
        public FixedInspectionService(FileInspectionResult result) => _result = result;
        public Task<FileInspectionResult> InspectAsync(
            FileInspectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result with { InspectedLength = request.DeclaredLength ?? _result.InspectedLength });
    }

    private sealed class CompletionRejectingQueue : IExtractionQueue
    {
        private readonly IExtractionQueue _inner;
        public CompletionRejectingQueue(IExtractionQueue inner) => _inner = inner;
        public Task<EnqueueResult> EnqueueAsync(EnqueueExtractionRequest request, CancellationToken ct = default) =>
            _inner.EnqueueAsync(request, ct);
        public Task<ExtractionJob?> ClaimAsync(string workerId, TimeSpan leaseDuration, int perTenantCap,
            CancellationToken ct = default) => _inner.ClaimAsync(workerId, leaseDuration, perTenantCap, ct);
        public Task<bool> RenewLeaseAsync(long jobId, string workerId, int leaseAttempt, TimeSpan leaseDuration,
            CancellationToken ct = default) => _inner.RenewLeaseAsync(jobId, workerId, leaseAttempt, leaseDuration, ct);
        public Task<bool> SetStatusAsync(long jobId, string workerId, int leaseAttempt, ExtractionStatus status,
            CancellationToken ct = default) => _inner.SetStatusAsync(jobId, workerId, leaseAttempt, status, ct);
        public Task<bool> CompleteAsync(long jobId, string workerId, int leaseAttempt, long resultLeadId,
            CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> FailAsync(long jobId, string workerId, int leaseAttempt, string error,
            CancellationToken ct = default) => _inner.FailAsync(jobId, workerId, leaseAttempt, error, ct);
    }
}
