using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
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

                var durableOccurrence = await context.Set<SourceDocumentOccurrence>()
                    .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == ingested.SourceDocumentOccurrenceId);
                Assert.Equal(IntakeOccurrenceStatus.Queued, durableOccurrence.IntakeStatus);
                Assert.Equal(jobId, durableOccurrence.ExtractionJobId);
                Assert.Equal(durableOccurrence.Id,
                    (await context.Set<ExtractionJob>().SingleAsync(x => x.Id == jobId)).SourceDocumentOccurrenceId);

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
                var extracted = await extractor.ExtractStructuredAsync(rows, tenantId, "customer-rfq.csv");
                var outcome = new ChunkedExtractionOutcome
                {
                    Status = extracted.Status,
                    Result = extracted.Result,
                    ExpectedItemCount = extracted.ExpectedItemCount,
                    ExtractedItemCount = extracted.ExtractedItemCount,
                    ReviewReason = extracted.ReviewReason,
                    Diagnostics = extracted.Diagnostics,
                    SplitResults = extracted.SplitResults,
                    CanonicalImport = extracted.CanonicalImport,
                    AiProviderClass = ERP_RFQ_Automation.AI.AiProviderClass.Local
                };
                Assert.Equal(ExtractionOutcomeStatus.Ok, outcome.Status);

                var leadIdentity = new LeadIdentityApplicationService(context);
                var persister = new LeadPersister(context, new NoopLogger<LeadPersister>(),
                    leadIdentity: leadIdentity);
                var leadId = await persister.PersistAndCompleteAsync(job, outcome, queue,
                    "evidence-sit", job.Attempts, TimeSpan.FromMinutes(2));
                Assert.NotNull(leadId);

                context.ChangeTracker.Clear();
                var repeated = await ingestion.IngestAsync(bytes, "customer-rfq-copy.csv", tenantId,
                    ExtractionSourceType.ManualUpload, priority: int.MaxValue);
                var repeatedJob = (await queue.ClaimAsync("evidence-repeat", TimeSpan.FromMinutes(2), 1))!;
                Assert.Equal(repeated.JobId, repeatedJob.Id);
                Assert.True(await queue.SetStatusAsync(repeatedJob.Id, "evidence-repeat", repeatedJob.Attempts,
                    ExtractionStatus.Extracting));
                Assert.True(await queue.SetStatusAsync(repeatedJob.Id, "evidence-repeat", repeatedJob.Attempts,
                    ExtractionStatus.Persisting));
                Assert.NotNull(await new LeadPersister(context, new NoopLogger<LeadPersister>(),
                        leadIdentity: leadIdentity)
                    .PersistAndCompleteAsync(repeatedJob, outcome, queue, "evidence-repeat",
                        repeatedJob.Attempts, TimeSpan.FromMinutes(2)));
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
                Assert.Equal(2, await context.Set<ExtractionRun>()
                    .CountAsync(x => x.BusinessUnitId == tenantId && x.Status == ExtractionRunStatus.Completed));
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
    public async Task MultipleDocumentsInOneCorpus_AllocateDistinctCanonicalInquiryNumbers()
    {
        var tenantId = NewTenantId();
        var batchId = Guid.NewGuid();
        var root = NewStorageRoot();
        try
        {
            await using var context = _database.ContextFor(null);
            SeedTenant(context, tenantId);
            await context.SaveChangesAsync();
            var queue = NewQueue(context);
            var ingestion = NewIngestion(context, queue, root, ClearedInspection());

            await ingestion.IngestAsync(ValidCsv("RFQ-CORPUS-1"), "corpus-1.csv", tenantId,
                ExtractionSourceType.ManualUpload, batchId, priority: int.MaxValue,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "corpus-file-1" });
            await ingestion.IngestAsync(ValidCsv("RFQ-CORPUS-2"), "corpus-2.csv", tenantId,
                ExtractionSourceType.ManualUpload, batchId, priority: int.MaxValue,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "corpus-file-2" });
            Assert.Equal(2, await context.Set<ExtractionJob>()
                .CountAsync(x => x.BusinessUnitId == tenantId && x.Status == ExtractionStatus.Pending));

            for (var index = 0; index < 2; index++)
            {
                context.ChangeTracker.Clear();
                var workerId = $"corpus-number-{index}";
                var job = await queue.ClaimAsync(workerId, TimeSpan.FromMinutes(2), 1);
                Assert.NotNull(job);
                Assert.True(await queue.SetStatusAsync(job!.Id, workerId, job.Attempts,
                    ExtractionStatus.Extracting));
                Assert.True(await queue.SetStatusAsync(job.Id, workerId, job.Attempts,
                    ExtractionStatus.Persisting));

                var bytes = await File.ReadAllBytesAsync(job.StoragePath);
                var rows = new NativeSpreadsheetParser().ParseCsv(bytes, job.FileName!);
                var extractor = new ChunkedExtractionService(
                    new StubLlm(), new CanonicalRfqNormalizer(), new NoopLogger<ChunkedExtractionService>());
                var outcome = await extractor.ExtractStructuredAsync(rows, tenantId, job.FileName!);
                var persister = new LeadPersister(context, new NoopLogger<LeadPersister>());
                Assert.NotNull(await persister.PersistAndCompleteAsync(job, outcome, queue,
                    workerId, job.Attempts, TimeSpan.FromMinutes(2)));
            }

            context.ChangeTracker.Clear();
            var inquiryNumbers = await context.Set<CanonicalInquiry>()
                .Where(x => x.BusinessUnitId == tenantId)
                .OrderBy(x => x.InquiryNumber)
                .Select(x => x.InquiryNumber)
                .ToListAsync();
            Assert.Equal(new[] { 1, 2 }, inquiryNumbers);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ScannerOutageQuarantine_RetriesStoredBytesInSameBatchAndOccurrence()
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
            var storage = new LocalEvidenceObjectStorage(new LocalFileStorage(root, root));

            var unavailableInspection = new FileInspectionResult(
                FileInspectionStatus.Quarantined, "text/csv", bytes.Length,
                "Scanner unavailable.", "clamav", null)
            {
                MalwareStatus = MalwareScanStatus.Unavailable,
                IsRetryable = true,
                ErrorCode = "security_scanner_unavailable"
            };
            var quarantined = new DocumentIngestionService(queue, storage,
                new FixedInspectionService(unavailableInspection), context,
                new NoopLogger<DocumentIngestionService>());
            var occurrence = new ExtractionJobMetadata { SourceOccurrenceId = "quarantined-retry" };
            var outage = await Assert.ThrowsAsync<DocumentInspectionException>(() => quarantined.IngestAsync(
                bytes, "customer-rfq.csv", tenantId, ExtractionSourceType.ManualUpload, metadata: occurrence));
            Assert.NotNull(outage.BatchId);
            Assert.NotNull(outage.SourceDocumentOccurrenceId);
            Assert.Equal(IntakeOccurrenceStatus.AwaitingSecurityScan,
                (await context.Set<SourceDocumentOccurrence>()
                    .SingleAsync(x => x.BusinessUnitId == tenantId)).IntakeStatus);
            Assert.Empty(await context.Set<ExtractionJob>().Where(x => x.BusinessUnitId == tenantId).ToListAsync());

            context.ChangeTracker.Clear();
            var cleanIngestion = new DocumentIngestionService(queue, storage,
                new FixedInspectionService(ClearedInspection()), context,
                new NoopLogger<DocumentIngestionService>());
            var recovery = new SecurityScanRecoveryService(context, storage, cleanIngestion);
            var released = await recovery.RetryBatchAsync(tenantId, outage.BatchId.Value);

            Assert.Equal(1, released.Eligible);
            Assert.Equal(1, released.Queued);
            Assert.Equal(0, released.StillAwaiting);
            var source = await context.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(DocumentSecurityStatus.Cleared, source.SecurityStatus);
            Assert.Contains("/cleared/", source.ObjectKey, StringComparison.Ordinal);
            var storedOccurrence = await context.Set<SourceDocumentOccurrence>()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(outage.SourceDocumentOccurrenceId, storedOccurrence.Id);
            Assert.Equal(IntakeOccurrenceStatus.Queued, storedOccurrence.IntakeStatus);
            var queuedJob = Assert.Single(await context.Set<ExtractionJob>()
                .Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            queuedJob.Status = ExtractionStatus.Leased;
            queuedJob.LeasedBy = "security-recovery-sit";
            queuedJob.Attempts = 1;
            queuedJob.LeaseExpiresAt = DateTime.UtcNow.AddMinutes(2);
            queuedJob.UpdatedOn = DateTime.UtcNow;
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();
            var claimed = await context.Set<ExtractionJob>()
                .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == queuedJob.Id);
            Assert.True(await queue.SetStatusAsync(claimed.Id, "security-recovery-sit", claimed.Attempts,
                ExtractionStatus.Extracting));
            Assert.True(await queue.SetStatusAsync(claimed.Id, "security-recovery-sit", claimed.Attempts,
                ExtractionStatus.Persisting));
            var localOutcome = new ChunkedExtractionOutcome
            {
                Status = ExtractionOutcomeStatus.Ok,
                Result = Ext.Result(Ext.Items(2, 0.95), 0.95) with { Rfqno = "RFQ-SCANNER-RECOVERY" },
                ExpectedItemCount = 2,
                ExtractedItemCount = 2,
                AiProviderClass = ERP_RFQ_Automation.AI.AiProviderClass.Local,
                ProcessingPath = ExtractionProcessingPath.LocalModel
            };
            var identity = new LeadIdentityApplicationService(context);
            Assert.NotNull(await new LeadPersister(context, new NoopLogger<LeadPersister>(),
                    leadIdentity: identity)
                .PersistAndCompleteAsync(claimed, localOutcome, queue, "security-recovery-sit",
                    claimed.Attempts, TimeSpan.FromMinutes(2)));

            context.ChangeTracker.Clear();
            var duplicateRetry = await recovery.RetryBatchAsync(tenantId, outage.BatchId.Value);
            Assert.Equal(0, duplicateRetry.Eligible);
            Assert.Single(await context.Set<ExtractionJob>().Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            Assert.Single(await context.Set<ExtractionRun>().Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            Assert.Single(await context.Set<LeadIngestionOccurrence>().Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            var batch = await identity.GetBatchAsync(tenantId, outage.BatchId.Value);
            Assert.NotNull(batch);
            Assert.Equal(1, batch.FilesReceived);
            Assert.Equal(1, batch.LogicalInquiries);
            Assert.Equal(0, batch.Rejected);
            Assert.Equal(0, batch.ExternalOccurrences);
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
                    ExtractedItemCount = 1,
                    AiProviderClass = ERP_RFQ_Automation.AI.AiProviderClass.Local
                };

                var leadIdentity = new LeadIdentityApplicationService(context);
                var leadId = await new LeadPersister(context, new NoopLogger<LeadPersister>(),
                        leadIdentity: leadIdentity)
                    .PersistAndCompleteAsync(job, outcome, queue, "unstructured-sit", job.Attempts,
                        TimeSpan.FromMinutes(2));
                Assert.NotNull(leadId);

                context.ChangeTracker.Clear();
                var repeated = await NewIngestion(context, queue, root, ClearedInspection()).IngestAsync(
                    bytes, "unstructured-copy.txt", tenantId, ExtractionSourceType.ManualUpload,
                    priority: int.MaxValue);
                var repeatedJob = (await queue.ClaimAsync("unstructured-repeat", TimeSpan.FromMinutes(2), 1))!;
                Assert.Equal(repeated.JobId, repeatedJob.Id);
                Assert.True(await queue.SetStatusAsync(repeatedJob.Id, "unstructured-repeat", repeatedJob.Attempts,
                    ExtractionStatus.Extracting));
                Assert.True(await queue.SetStatusAsync(repeatedJob.Id, "unstructured-repeat", repeatedJob.Attempts,
                    ExtractionStatus.Persisting));
                Assert.NotNull(await new LeadPersister(context, new NoopLogger<LeadPersister>(),
                        leadIdentity: leadIdentity)
                    .PersistAndCompleteAsync(repeatedJob, outcome, queue, "unstructured-repeat",
                        repeatedJob.Attempts, TimeSpan.FromMinutes(2)));
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
            Assert.Equal(2, await verify.Set<ExtractionRun>()
                .CountAsync(x => x.BusinessUnitId == tenantId && x.Status == ExtractionRunStatus.Completed));
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
    public async Task ConcurrentIdenticalReceipts_CreateDistinctJobsAgainstOneContentObject()
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
            Assert.All(results, x => Assert.Equal(EnqueueOutcome.Enqueued, x.Outcome));
            Assert.Equal(2, results.Select(x => x.JobId).Distinct().Count());
            Assert.Equal(2, results.Select(x => x.SourceDocumentOccurrenceId).Distinct().Count());

            await using var verify = _database.ContextFor(null);
            Assert.Equal(2, await verify.Set<ExtractionJob>().CountAsync(x => x.BusinessUnitId == tenantId));
            Assert.Equal(1, await verify.Set<SourceDocument>().CountAsync(x => x.BusinessUnitId == tenantId));
            var occurrences = await verify.Set<SourceDocumentOccurrence>()
                .Where(x => x.BusinessUnitId == tenantId).ToListAsync();
            Assert.Equal(2, occurrences.Count);
            Assert.All(occurrences, x => Assert.Equal(IntakeOccurrenceStatus.Queued, x.IntakeStatus));
            Assert.Equal(2, occurrences.Select(x => x.ExtractionJobId).Distinct().Count());

        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task SuppliedSourceIdentity_RetryReusesOccurrenceAndJob()
    {
        var tenantId = NewTenantId();
        var bytes = ValidCsv("RFQ-RETRY-ID");
        var root = NewStorageRoot();
        try
        {
            await using var context = _database.ContextFor(null);
            SeedTenant(context, tenantId);
            await context.SaveChangesAsync();
            var ingestion = NewIngestion(context, NewQueue(context), root, ClearedInspection(bytes.Length));
            var metadata = new ExtractionJobMetadata { SourceOccurrenceId = "manual-request-42:stable-retry.csv" };

            var first = await ingestion.IngestAsync(
                bytes, "stable-retry.csv", tenantId, ExtractionSourceType.ManualUpload, metadata: metadata);
            context.ChangeTracker.Clear();
            var retry = await ingestion.IngestAsync(
                bytes, "stable-retry.csv", tenantId, ExtractionSourceType.ManualUpload, metadata: metadata);

            Assert.Equal(first.BatchId, retry.BatchId);
            Assert.Equal(first.SourceDocumentOccurrenceId, retry.SourceDocumentOccurrenceId);
            Assert.Equal(first.JobId, retry.JobId);
            Assert.Equal(EnqueueOutcome.Enqueued, first.Outcome);
            Assert.Equal(EnqueueOutcome.Duplicate, retry.Outcome);
            Assert.Equal(1, await context.Set<SourceDocumentOccurrence>()
                .CountAsync(x => x.BusinessUnitId == tenantId));
            Assert.Equal(1, await context.Set<ExtractionJob>()
                .CountAsync(x => x.BusinessUnitId == tenantId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task RejectedInspection_PersistsStructuredTerminalOccurrenceWithoutJob()
    {
        var tenantId = NewTenantId();
        var bytes = ValidCsv("RFQ-REJECTED");
        var root = NewStorageRoot();
        try
        {
            await using var context = _database.ContextFor(null);
            SeedTenant(context, tenantId);
            await context.SaveChangesAsync();
            var rejected = new FileInspectionResult(
                FileInspectionStatus.Rejected, "text/csv", bytes.Length,
                "Test signature was rejected.", "test-scanner", "blocked-signature");
            var ingestion = NewIngestion(context, NewQueue(context), root, rejected);

            var error = await Assert.ThrowsAsync<DocumentInspectionException>(() => ingestion.IngestAsync(
                bytes, "rejected.csv", tenantId, ExtractionSourceType.ManualUpload));
            Assert.Equal(FileInspectionStatus.Rejected, error.Inspection.Status);

            context.ChangeTracker.Clear();
            var occurrence = await context.Set<SourceDocumentOccurrence>()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(IntakeOccurrenceStatus.Rejected, occurrence.IntakeStatus);
            Assert.Equal("SecurityInspection", occurrence.LastErrorCategory);
            Assert.Equal("document_rejected", occurrence.LastErrorCode);
            using var details = JsonDocument.Parse(occurrence.LastErrorDetailsJson!);
            Assert.Equal("Rejected", details.RootElement.GetProperty("status").GetString());
            Assert.Equal("test-scanner", details.RootElement.GetProperty("scanner").GetString());
            Assert.Equal("blocked-signature", details.RootElement.GetProperty("signature").GetString());
            Assert.Empty(await context.Set<ExtractionJob>()
                .Where(x => x.BusinessUnitId == tenantId).ToListAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExtractionFailure_PreservesRetryableAuditableOccurrence()
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
            var ingested = await NewIngestion(context, queue, root, ClearedInspection()).IngestAsync(
                bytes, "retryable.csv", tenantId, ExtractionSourceType.ManualUpload,
                priority: int.MaxValue);

            var claimed = await queue.ClaimAsync("retryable-sit", TimeSpan.FromMinutes(2), 1);
            Assert.NotNull(claimed);
            var occurrence = await context.Set<SourceDocumentOccurrence>()
                .SingleAsync(x => x.Id == ingested.SourceDocumentOccurrenceId);
            occurrence.MarkProcessing();
            await context.SaveChangesAsync();
            occurrence.MarkRetryable("parser_failed");
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var preserved = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
                .SingleAsync(x => x.Id == ingested.SourceDocumentOccurrenceId);
            Assert.Equal(IntakeOccurrenceStatus.Retryable, preserved.IntakeStatus);
            Assert.Equal("parser_failed", preserved.LastErrorCode);
            Assert.Equal(ingested.JobId, preserved.ExtractionJobId);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("success")]
    [InlineData("retry")]
    [InlineData("dead-letter")]
    [Trait("Category", "PostgreSQL")]
    public async Task QueueTransition_AtomicallyMovesJobAndOccurrence(string outcome)
    {
        var tenantId = NewTenantId();
        var root = NewStorageRoot();
        try
        {
            await using var context = _database.ContextFor(null);
            SeedTenant(context, tenantId);
            await context.SaveChangesAsync();
            var queue = NewQueue(context);
            var ingestion = NewIngestion(context, queue, root, ClearedInspection());

            var ingested = await ingestion.IngestAsync(
                ValidCsv("RFQ-ATOMIC-" + outcome), $"atomic-{outcome}.csv", tenantId,
                ExtractionSourceType.ManualUpload, priority: 30);
            if (outcome == "dead-letter")
            {
                var job = await context.Set<ExtractionJob>().SingleAsync(x => x.Id == ingested.JobId);
                job.MaxAttempts = 1;
                await context.SaveChangesAsync();
            }

            context.ChangeTracker.Clear();
            var worker = "release01c-" + outcome;
            var claim = (await queue.ClaimAsync(worker, TimeSpan.FromMinutes(2), 1))!;
            context.ChangeTracker.Clear();
            Assert.Equal(IntakeOccurrenceStatus.Processing,
                (await context.Set<SourceDocumentOccurrence>().SingleAsync(x => x.Id == ingested.SourceDocumentOccurrenceId)).IntakeStatus);

            if (outcome == "success")
            {
                Assert.True(await queue.SetStatusAsync(claim.Id, worker, claim.Attempts, ExtractionStatus.Extracting));
                Assert.True(await queue.SetStatusAsync(claim.Id, worker, claim.Attempts, ExtractionStatus.Persisting));
                Assert.True(await queue.CompleteAsync(claim.Id, worker, claim.Attempts, null));
            }
            else
            {
                Assert.True(await queue.FailAsync(claim.Id, worker, claim.Attempts,
                    outcome == "retry" ? "parser_failed" : "poison_document"));
            }

            context.ChangeTracker.Clear();
            var expectedJob = outcome switch
            {
                "success" => ExtractionStatus.Succeeded,
                "retry" => ExtractionStatus.Pending,
                _ => ExtractionStatus.DeadLetter
            };
            var expectedOccurrence = outcome switch
            {
                "success" => IntakeOccurrenceStatus.Resolved,
                "retry" => IntakeOccurrenceStatus.Retryable,
                _ => IntakeOccurrenceStatus.DeadLetter
            };
            Assert.Equal(expectedJob,
                (await context.Set<ExtractionJob>().SingleAsync(x => x.Id == ingested.JobId)).Status);
            Assert.Equal(expectedOccurrence,
                (await context.Set<SourceDocumentOccurrence>().SingleAsync(x => x.Id == ingested.SourceDocumentOccurrenceId)).IntakeStatus);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task Analytics_UsesIntakeTimeCohortWhenReconciliationIsDelayed()
    {
        var tenantId = NewTenantId();
        var from = DateTimeOffset.UtcNow.AddHours(-4);
        var to = from.AddHours(1);
        var receivedInside = from.AddMinutes(10);
        var receivedOutside = to.AddMinutes(10);
        await using var context = _database.ContextFor(null);
        SeedTenant(context, tenantId);

        var corpus = DocumentCorpus.Create(tenantId, Guid.NewGuid(), CorpusSourceType.ManualUpload, receivedInside);
        context.Add(corpus);
        await context.SaveChangesAsync();
        var source = SourceDocument.Create(tenantId, corpus.Id, new string('a', 64), "cohort.csv", "text/csv",
            "test", $"cohort/{tenantId}", "v1", 100, receivedInside);
        source.MarkSecurityStatus(DocumentSecurityStatus.Cleared, receivedInside);
        context.Add(source);
        await context.SaveChangesAsync();
        var inside = SourceDocumentOccurrence.Create(tenantId, source.Id, corpus.Id,
            "release01c-cohort-inside", "{}", receivedOn: receivedInside);
        var outside = SourceDocumentOccurrence.Create(tenantId, source.Id, corpus.Id,
            "release01c-cohort-outside", "{}", receivedOn: receivedOutside);
        context.AddRange(inside, outside);
        await context.SaveChangesAsync();

        var insideBatch = Guid.NewGuid();
        var outsideBatch = Guid.NewGuid();
        context.AddRange(
            Batch(insideBatch, tenantId, receivedInside),
            Batch(outsideBatch, tenantId, receivedOutside));
        context.AddRange(
            ReconciledOccurrence(insideBatch, inside.Id, source.Id, tenantId,
                "inside-delayed", to.AddHours(2)),
            ReconciledOccurrence(outsideBatch, outside.Id, source.Id, tenantId,
                "outside-early", from.AddMinutes(20)));
        await context.SaveChangesAsync();

        var analytics = await new LeadIdentityApplicationService(context)
            .GetAnalyticsAsync(tenantId, from, to);
        var volume = analytics.Metrics.Single(x => x.Key == "ingestion-volume");
        var possible = analytics.Metrics.Single(x => x.Key == "possible-match-rate");

        Assert.Equal(1, volume.Numerator);
        Assert.Equal(new[] { inside.Id }, volume.OccurrenceIds);
        Assert.Equal(1, possible.Numerator);
        Assert.Equal(1, possible.Denominator);
        Assert.Equal(new[] { inside.Id }, possible.OccurrenceIds);
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

    private static FileInspectionResult ClearedInspection(int? inspectedLength = null) => new(
        FileInspectionStatus.Cleared, "text/csv", inspectedLength ?? ValidCsv().Length,
        "Inspection and malware scan passed.", "test-scanner", "clean");

    private static byte[] ValidCsv(string rfq = "RFQ-SIT-1") => Encoding.UTF8.GetBytes(
        "RFQ No,Buyer Name,Received Date,Bid Closing Date,Product Name,Quantity,Unit Price,Currency,Manufacturer,MPN,Lead Time\n" +
        $"{rfq},Acme Procurement,2026-07-23,2026-08-15,Pressure Sensor,5,125.50,USD,Contoso,PS-100,14\n" +
        $"{rfq},Acme Procurement,2026-07-23,2026-08-15,Control Valve,2,840.00,USD,Fabrikam,CV-200,21\n");

    private static LeadIngestionBatch Batch(Guid id, long tenantId, DateTimeOffset at) => new()
    {
        Id = id,
        BusinessUnitId = tenantId,
        SourceChannel = "ManualUpload",
        CreatedBy = "release01c-test",
        CreatedAtUtc = at,
        UpdatedAtUtc = at
    };

    private static LeadIngestionOccurrence ReconciledOccurrence(
        Guid batchId,
        long sourceOccurrenceId,
        long sourceDocumentId,
        long tenantId,
        string key,
        DateTimeOffset reconciledAt) => new()
    {
        BusinessUnitId = tenantId,
        BatchId = batchId,
        SourceDocumentId = sourceDocumentId,
        SourceDocumentOccurrenceId = sourceOccurrenceId,
        SourceChannel = "ManualUpload",
        IdempotencyKey = key,
        OriginalFileName = key + ".csv",
        ContentHash = new string(key[0], 64),
        LogicalInquiryFingerprint = new string(key[^1], 64),
        Classification = LeadOccurrenceClassification.PossibleMatchReviewRequired,
        Confidence = .75m,
        ProcessingPath = LeadProcessingPath.Deterministic,
        SourceReceivedAtUtc = reconciledAt,
        IngestedAtUtc = reconciledAt,
        CreatedAtUtc = reconciledAt,
        ActorType = "Test",
        ActorId = "release01c-test",
        CorrelationId = key
    };

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
        public Task<bool> CompleteAsync(long jobId, string workerId, int leaseAttempt, long? resultLeadId,
            CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> FailAsync(long jobId, string workerId, int leaseAttempt, string error,
            CancellationToken ct = default) => _inner.FailAsync(jobId, workerId, leaseAttempt, error, ct);
    }
}
