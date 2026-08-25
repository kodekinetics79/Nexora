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
using Microsoft.Extensions.Options;
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
                Assert.Equal(jobId, repeated.JobId);
                Assert.Equal(EnqueueOutcome.Duplicate, repeated.Outcome);
                Assert.Equal(1, await context.Set<ExtractionJob>()
                    .CountAsync(x => x.BusinessUnitId == tenantId));
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
                    .SingleAsync(x => x.BusinessUnitId == tenantId && x.ExtractionJobId == jobId
                                      && x.OriginalOccurrenceId == null);
                Assert.Contains("immutableObjects", occurrence.SourceMetadataJson);
                Assert.Contains("quarantine", occurrence.SourceMetadataJson);
                Assert.Contains("selected", occurrence.SourceMetadataJson);

                var run = await context.Set<ExtractionRun>()
                    .SingleAsync(x => x.BusinessUnitId == tenantId && x.ExtractionJobId == jobId);
                Assert.Equal(ExtractionRunStatus.Completed, run.Status);
                Assert.Equal(1, await context.Set<ExtractionRun>()
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
                var quoteCriticalEvidence = await context.Set<FieldEvidence>()
                    .Include(x => x.Region)
                    .Where(x => x.BusinessUnitId == tenantId
                        && (x.FieldName == "Quantity" || x.FieldName == "UnitOfMeasure"))
                    .ToListAsync();
                Assert.Equal(4, quoteCriticalEvidence.Count);
                Assert.All(quoteCriticalEvidence,
                    field => Assert.False(string.IsNullOrWhiteSpace(field.Region.SourceAddress)));
                Assert.All(quoteCriticalEvidence.Where(x => x.FieldName == "Quantity"),
                    field => Assert.False(string.IsNullOrWhiteSpace(field.RawValue)));

                var job = await context.Set<ExtractionJob>().SingleAsync(x => x.Id == jobId);
                Assert.Equal(ExtractionStatus.Succeeded, job.Status);
                Assert.NotNull(job.ResultLeadId);

                var immutableUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
                    context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE source_documents SET content_hash = {new string('b', 64)} WHERE id = {source.Id}"));
                Assert.Equal("23514", immutableUpdate.SqlState);

                var immutableObjectUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
                    context.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE source_documents SET object_key = {"tampered/source.csv"} WHERE id = {source.Id}"));
                Assert.Equal("23514", immutableObjectUpdate.SqlState);
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

    /// <summary>
    /// The production shape of the ClamAV outage: occurrences that ended up in the terminal
    /// <see cref="IntakeOccurrenceStatus.Rejected"/> state (the batch page then hides its retry
    /// control entirely) must still be discoverable and replayable tenant-wide, with no batch id
    /// and no re-upload. An infrastructure outage must never be a user-facing dead end.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ScannerRecovery_ReleasesTerminallyRejectedHoldsTenantWideWithoutABatchId()
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
                MalwareScannerMessages.ScannerUnreachable, "ClamAV", null)
            {
                MalwareStatus = MalwareScanStatus.Unavailable,
                IsRetryable = true,
                ErrorCode = "security_scanner_unavailable"
            };
            var blockedIngestion = new DocumentIngestionService(queue, storage,
                new FixedInspectionService(unavailableInspection), context,
                new NoopLogger<DocumentIngestionService>());
            var outage = await Assert.ThrowsAsync<DocumentInspectionException>(() => blockedIngestion.IngestAsync(
                bytes, "customer-rfq.csv", tenantId, ExtractionSourceType.ManualUpload,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "terminal-hold" }));

            // Drive the occurrence into the terminal state the owner's documents are stuck in.
            var held = await context.Set<SourceDocumentOccurrence>()
                .SingleAsync(x => x.BusinessUnitId == tenantId && x.Id == outage.SourceDocumentOccurrenceId);
            held.MarkRejected("SecurityInspection", "security_scanner_unavailable",
                "{\"reason\":\"scanner unreachable\",\"retryable\":true}");
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var cleanIngestion = new DocumentIngestionService(queue, storage,
                new FixedInspectionService(ClearedInspection()), context,
                new NoopLogger<DocumentIngestionService>());
            var recovery = new SecurityScanRecoveryService(context, storage, cleanIngestion);

            var blocked = await recovery.ListBlockedBatchesAsync(tenantId);
            var blockedBatch = Assert.Single(blocked);
            Assert.Equal(outage.BatchId, blockedBatch.BatchId);
            Assert.Equal(1, blockedBatch.BlockedFiles);

            var released = await recovery.RetryTenantAsync(tenantId);

            Assert.Equal(1, released.Eligible);
            Assert.Equal(1, released.Queued);
            Assert.False(released.MoreRemaining);
            Assert.Equal(outage.BatchId, Assert.Single(released.Batches));
            context.ChangeTracker.Clear();
            var recovered = await context.Set<SourceDocumentOccurrence>()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(IntakeOccurrenceStatus.Queued, recovered.IntakeStatus);
            Assert.Equal(DocumentSecurityStatus.Cleared,
                (await context.Set<SourceDocument>().SingleAsync(x => x.BusinessUnitId == tenantId)).SecurityStatus);
            Assert.Empty(await recovery.ListBlockedBatchesAsync(tenantId));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ScannerRecovery_MissingSourceObjectIsClassifiedWithoutCreatingWork()
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
            var writableStorage = new LocalEvidenceObjectStorage(new LocalFileStorage(root, root));
            var unavailableInspection = new FileInspectionResult(
                FileInspectionStatus.Quarantined, "text/csv", bytes.Length,
                "Scanner unavailable.", "clamav", null)
            {
                MalwareStatus = MalwareScanStatus.Unavailable,
                IsRetryable = true,
                ErrorCode = "security_scanner_unavailable"
            };
            var blockedIngestion = new DocumentIngestionService(queue, writableStorage,
                new FixedInspectionService(unavailableInspection), context,
                new NoopLogger<DocumentIngestionService>());
            var blocked = await Assert.ThrowsAsync<DocumentInspectionException>(() => blockedIngestion.IngestAsync(
                bytes, "missing-source.csv", tenantId, ExtractionSourceType.ManualUpload,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "missing-source" }));

            context.ChangeTracker.Clear();
            var cleanIngestion = new DocumentIngestionService(queue, writableStorage,
                new FixedInspectionService(ClearedInspection()), context,
                new NoopLogger<DocumentIngestionService>());
            var result = await new SecurityScanRecoveryService(
                    context, new UnavailableReadEvidenceStorage(), cleanIngestion)
                .RetryBatchAsync(tenantId, blocked.BatchId!.Value);

            Assert.Equal(1, result.Eligible);
            Assert.Equal(1, result.SourceObjectUnavailable);
            var item = Assert.Single(result.Items);
            Assert.Equal("SOURCE_OBJECT_UNAVAILABLE", item.Status);
            Assert.Equal("source_object_unavailable", item.ErrorCode);
            Assert.Null(item.ExtractionJobId);
            Assert.Empty(await context.Set<ExtractionJob>()
                .Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            context.ChangeTracker.Clear();
            var occurrence = await context.Set<SourceDocumentOccurrence>()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(IntakeOccurrenceStatus.Rejected, occurrence.IntakeStatus);
            Assert.Equal(IngestionOutcomeState.SOURCE_OBJECT_UNAVAILABLE, occurrence.OutcomeState);
            Assert.Equal("EvidenceStorage", occurrence.LastErrorCategory);
            Assert.Equal("source_object_unavailable", occurrence.LastErrorCode);

            var replay = await new SecurityScanRecoveryService(
                    context, new UnavailableReadEvidenceStorage(), cleanIngestion)
                .RetryBatchAsync(tenantId, blocked.BatchId.Value);
            Assert.Equal(0, replay.Eligible);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ScannerRecovery_TransientStorageFailureRemainsRetryable()
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
            var writableStorage = new LocalEvidenceObjectStorage(new LocalFileStorage(root, root));
            var unavailableInspection = new FileInspectionResult(
                FileInspectionStatus.Quarantined, "text/csv", bytes.Length,
                "Scanner unavailable.", "clamav", null)
            {
                MalwareStatus = MalwareScanStatus.Unavailable,
                IsRetryable = true,
                ErrorCode = "security_scanner_unavailable"
            };
            var blockedIngestion = new DocumentIngestionService(queue, writableStorage,
                new FixedInspectionService(unavailableInspection), context,
                new NoopLogger<DocumentIngestionService>());
            var blocked = await Assert.ThrowsAsync<DocumentInspectionException>(() => blockedIngestion.IngestAsync(
                bytes, "transient-source.csv", tenantId, ExtractionSourceType.ManualUpload,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "transient-source" }));

            context.ChangeTracker.Clear();
            var cleanIngestion = new DocumentIngestionService(queue, writableStorage,
                new FixedInspectionService(ClearedInspection()), context,
                new NoopLogger<DocumentIngestionService>());
            var result = await new SecurityScanRecoveryService(
                    context, new TransientReadEvidenceStorage(), cleanIngestion)
                .RetryBatchAsync(tenantId, blocked.BatchId!.Value);

            Assert.Equal(1, result.Eligible);
            Assert.Equal(1, result.StillAwaiting);
            Assert.Equal(0, result.SourceObjectUnavailable);
            Assert.Equal("AwaitingSecurityScan", Assert.Single(result.Items).Status);
            context.ChangeTracker.Clear();
            var occurrence = await context.Set<SourceDocumentOccurrence>()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(IntakeOccurrenceStatus.AwaitingSecurityScan, occurrence.IntakeStatus);
            Assert.Equal(IngestionOutcomeState.SECURITY_SCAN_BLOCKED, occurrence.OutcomeState);
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
                Assert.Equal(jobId, repeated.JobId);
                Assert.Equal(EnqueueOutcome.Duplicate, repeated.Outcome);
                Assert.Equal(1, await context.Set<ExtractionJob>()
                    .CountAsync(x => x.BusinessUnitId == tenantId));
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
            Assert.Equal(1, await verify.Set<ExtractionRun>()
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
    public async Task ConcurrentIdenticalReceipts_CreateOneJobAndTwoTenantScopedOccurrences()
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
            Assert.Equal(2, results.Select(x => x.SourceDocumentOccurrenceId).Distinct().Count());

            await using var verify = _database.ContextFor(null);
            Assert.Equal(1, await verify.Set<ExtractionJob>().CountAsync(x => x.BusinessUnitId == tenantId));
            Assert.Equal(1, await verify.Set<SourceDocument>().CountAsync(x => x.BusinessUnitId == tenantId));
            var occurrences = await verify.Set<SourceDocumentOccurrence>()
                .Where(x => x.BusinessUnitId == tenantId).OrderBy(x => x.ReceivedOn).ToListAsync();
            Assert.Equal(2, occurrences.Count);
            Assert.Single(occurrences.Select(x => x.ExtractionJobId).Distinct());
            var duplicate = Assert.Single(occurrences, x => x.OriginalOccurrenceId.HasValue);
            Assert.Equal(IntakeOccurrenceStatus.Queued,
                Assert.Single(occurrences, x => !x.OriginalOccurrenceId.HasValue).IntakeStatus);
            Assert.Equal(IntakeOccurrenceStatus.Queued, duplicate.IntakeStatus);
            Assert.Equal(IngestionOutcomeState.EXACT_DUPLICATE_CONFIRMED, duplicate.OutcomeState);
            Assert.False(duplicate.ProcessingReused);

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
    public async Task ExactHashDuplicate_PrecedesExtractionAndIsTenantSafeWithResourceAccounting()
    {
        var tenantId = NewTenantId();
        var otherTenantId = tenantId + 1;
        var bytes = ValidCsv("RFQ-PRESECURITY-DUPLICATE");
        var root = NewStorageRoot();
        try
        {
            await using var context = _database.ContextFor(null);
            SeedTenant(context, tenantId);
            SeedTenant(context, otherTenantId);
            await context.SaveChangesAsync();
            var scanner = new CountingScanner();
            var queue = NewQueue(context);
            var ingestion = NewGovernedIngestion(context, queue, root, scanner);

            var original = await ingestion.IngestAsync(bytes, "original.csv", tenantId,
                ExtractionSourceType.ManualUpload,
                priority: int.MaxValue,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "original", UploadedBy = "buyer@example.test" });
            context.ChangeTracker.Clear();
            var duplicate = await ingestion.IngestAsync(bytes, "forwarded-copy.csv", tenantId,
                ExtractionSourceType.ManualUpload,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "forwarded", UploadedBy = "rep@example.test" });

            Assert.Equal(1, scanner.Calls);
            Assert.Equal(original.JobId, duplicate.JobId);
            Assert.Equal(EnqueueOutcome.Duplicate, duplicate.Outcome);
            Assert.Equal(1, await context.Set<ExtractionJob>().CountAsync(x => x.BusinessUnitId == tenantId));
            Assert.Empty(await context.Leads.Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            Assert.Empty(await context.Rfqs.Where(x => x.BusinessUnitId == tenantId).ToListAsync());

            var storedDuplicate = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
                .SingleAsync(x => x.Id == duplicate.SourceDocumentOccurrenceId);
            Assert.Equal(original.SourceDocumentOccurrenceId, storedDuplicate.OriginalOccurrenceId);
            Assert.Equal(IngestionOutcomeState.EXACT_DUPLICATE_CONFIRMED, storedDuplicate.OutcomeState);
            Assert.True(storedDuplicate.MalwareScanReused);
            Assert.False(storedDuplicate.MalwareScanRerun);
            Assert.Equal(IntakeOccurrenceStatus.Queued, storedDuplicate.IntakeStatus);
            Assert.False(storedDuplicate.ProcessingReused);
            Assert.False(storedDuplicate.ParserReused);
            Assert.False(storedDuplicate.OcrReused);
            Assert.False(storedDuplicate.LocalModelReused);
            Assert.False(storedDuplicate.ExternalModelReused);
            Assert.Equal(bytes.LongLength, storedDuplicate.BytesUploaded);
            Assert.Equal(bytes.LongLength, storedDuplicate.StorageLogicalBytes);
            Assert.Equal(0, storedDuplicate.StoragePhysicalBytes);
            Assert.Equal(0m, storedDuplicate.ExternalProcessingCost);
            Assert.Equal("LOCAL_COMPUTE_UNPRICED", storedDuplicate.CostStatus);

            var identity = new LeadIdentityApplicationService(context);
            var duplicateRows = await identity.GetDuplicateUploadsAsync(tenantId);
            var duplicateRow = Assert.Single(duplicateRows);
            Assert.Equal(duplicate.SourceDocumentOccurrenceId, duplicateRow.OccurrenceId);
            Assert.Equal("rep@example.test", duplicateRow.UploadedBy);
            var summary = await identity.GetBatchAsync(tenantId, duplicate.BatchId);
            Assert.NotNull(summary);
            Assert.Equal(1, summary.FilesReceived);
            Assert.Equal(1, summary.ExactDuplicates);
            Assert.Equal(0, summary.LogicalInquiries);
            Assert.Equal(0, summary.Rejected);

            var claimed = await queue.ClaimAsync("shared-occurrence-test", TimeSpan.FromMinutes(2), 1);
            Assert.NotNull(claimed);
            Assert.True(await queue.SetStatusAsync(claimed!.Id, "shared-occurrence-test", claimed.Attempts,
                ExtractionStatus.Extracting));
            Assert.True(await queue.SetStatusAsync(claimed.Id, "shared-occurrence-test", claimed.Attempts,
                ExtractionStatus.Persisting));
            Assert.True(await queue.CompleteAsync(claimed.Id, "shared-occurrence-test", claimed.Attempts, null));
            context.ChangeTracker.Clear();
            var completedDuplicate = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
                .SingleAsync(x => x.Id == duplicate.SourceDocumentOccurrenceId);
            Assert.Equal(IntakeOccurrenceStatus.Resolved, completedDuplicate.IntakeStatus);
            Assert.True(completedDuplicate.ProcessingReused);
            Assert.True(completedDuplicate.ParserReused);
            Assert.True(completedDuplicate.OcrReused);
            Assert.True(completedDuplicate.LocalModelReused);
            Assert.True(completedDuplicate.ExternalModelReused);

            context.ChangeTracker.Clear();
            await ingestion.IngestAsync(bytes, "other-tenant.csv", otherTenantId,
                ExtractionSourceType.ManualUpload,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "other-tenant" });
            Assert.Equal(2, scanner.Calls);
            Assert.Empty(await identity.GetDuplicateUploadsAsync(otherTenantId));
            await using var rls = _database.TenantContextWithRls(otherTenantId);
            Assert.Empty(await rls.Set<SourceDocumentOccurrence>().AsNoTracking()
                .Where(x => x.OriginalOccurrenceId == original.SourceDocumentOccurrenceId).ToListAsync());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task ExactHashDuplicate_OfDeadLetterRemainsActionableAndDoesNotCreateWork()
    {
        var tenantId = NewTenantId();
        var bytes = ValidCsv("RFQ-DEADLETTER-DUPLICATE");
        var root = NewStorageRoot();
        try
        {
            await using var context = _database.ContextFor(null);
            SeedTenant(context, tenantId);
            await context.SaveChangesAsync();
            var queue = NewQueue(context);
            var ingestion = NewGovernedIngestion(context, queue, root, new CountingScanner());

            var original = await ingestion.IngestAsync(bytes, "original.csv", tenantId,
                ExtractionSourceType.ManualUpload,
                priority: int.MaxValue,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "deadletter-original" });
            var job = await context.Set<ExtractionJob>().SingleAsync(x => x.Id == original.JobId);
            job.MaxAttempts = 1;
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var claimed = await queue.ClaimAsync(
                "deadletter-duplicate-test", TimeSpan.FromMinutes(2), 1);
            Assert.NotNull(claimed);
            Assert.True(await queue.FailAsync(claimed.Id, "deadletter-duplicate-test",
                claimed.Attempts, "permanent_parse_failure"));
            context.ChangeTracker.Clear();

            var duplicate = await ingestion.IngestAsync(bytes, "forwarded.csv", tenantId,
                ExtractionSourceType.ManualUpload,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "deadletter-forwarded" });

            Assert.Equal(EnqueueOutcome.Duplicate, duplicate.Outcome);
            Assert.Equal(ExtractionStatus.DeadLetter, duplicate.ExistingStatus);
            Assert.Equal(original.JobId, duplicate.JobId);
            Assert.Single(await context.Set<ExtractionJob>()
                .Where(x => x.BusinessUnitId == tenantId).ToListAsync());
            var occurrence = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
                .SingleAsync(x => x.Id == duplicate.SourceDocumentOccurrenceId);
            Assert.Equal(IntakeOccurrenceStatus.DeadLetter, occurrence.IntakeStatus);
            Assert.Equal("extraction_dead_letter", occurrence.LastErrorCode);
            Assert.False(occurrence.ProcessingReused);
            Assert.Equal(IngestionOutcomeState.EXACT_DUPLICATE_CONFIRMED, occurrence.OutcomeState);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task BlockedStaleDuplicate_RescansAndResumesWithoutAnotherJob()
    {
        var tenantId = NewTenantId();
        var bytes = ValidCsv("RFQ-BLOCKED-DUPLICATE");
        var root = NewStorageRoot();
        try
        {
            await using var context = _database.ContextFor(null);
            SeedTenant(context, tenantId);
            await context.SaveChangesAsync();
            var scanner = new CountingScanner();
            var queue = NewQueue(context);
            var storage = new LocalEvidenceObjectStorage(new LocalFileStorage(root, root));
            var ingestion = NewGovernedIngestion(context, queue, storage, scanner);
            var original = await ingestion.IngestAsync(bytes, "original.csv", tenantId,
                ExtractionSourceType.ManualUpload,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "blocked-original" });
            var source = await context.Set<SourceDocument>().SingleAsync(x => x.BusinessUnitId == tenantId);
            source.RecordMalwareVerdict(MalwareScanStatus.Clean, "test-clamav", "old-signatures",
                DateTimeOffset.UtcNow.AddDays(-2));
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            scanner.Status = MalwareScanStatus.Unavailable;
            var blocked = await Assert.ThrowsAsync<DocumentInspectionException>(() => ingestion.IngestAsync(
                bytes, "blocked-copy.csv", tenantId, ExtractionSourceType.ManualUpload,
                metadata: new ExtractionJobMetadata { SourceOccurrenceId = "blocked-copy" }));
            var occurrence = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
                .SingleAsync(x => x.Id == blocked.SourceDocumentOccurrenceId);
            Assert.Equal(original.SourceDocumentOccurrenceId, occurrence.OriginalOccurrenceId);
            Assert.Equal(IntakeOccurrenceStatus.AwaitingSecurityScan, occurrence.IntakeStatus);
            Assert.Equal(IngestionOutcomeState.SECURITY_SCAN_BLOCKED, occurrence.OutcomeState);
            Assert.True(occurrence.MalwareScanRerun);
            Assert.Equal(1, await context.Set<ExtractionJob>().CountAsync(x => x.BusinessUnitId == tenantId));

            scanner.Status = MalwareScanStatus.Clean;
            context.ChangeTracker.Clear();
            var recovery = new SecurityScanRecoveryService(context, storage, ingestion);
            var result = await recovery.RetryBatchAsync(tenantId, blocked.BatchId!.Value);
            Assert.Equal(1, result.Eligible);
            Assert.Equal(1, result.Queued);
            context.ChangeTracker.Clear();
            var resumed = await context.Set<SourceDocumentOccurrence>().AsNoTracking()
                .SingleAsync(x => x.Id == blocked.SourceDocumentOccurrenceId);
            Assert.Equal(IntakeOccurrenceStatus.Queued, resumed.IntakeStatus);
            Assert.Equal(IngestionOutcomeState.EXACT_DUPLICATE_CONFIRMED, resumed.OutcomeState);
            Assert.False(resumed.ProcessingReused);
            Assert.Equal(original.JobId, resumed.ExtractionJobId);
            Assert.Equal(3, scanner.Calls);
            Assert.Equal(1, await context.Set<ExtractionJob>().CountAsync(x => x.BusinessUnitId == tenantId));
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

    private static DocumentIngestionService NewGovernedIngestion(
        ErpRfqAutomationContext context,
        IExtractionQueue queue,
        string root,
        IMalwareScanner scanner) => NewGovernedIngestion(
            context, queue, new LocalEvidenceObjectStorage(new LocalFileStorage(root, root)), scanner);

    private static DocumentIngestionService NewGovernedIngestion(
        ErpRfqAutomationContext context,
        IExtractionQueue queue,
        IEvidenceObjectStorage storage,
        IMalwareScanner scanner) => new(
            queue, storage, new DocumentFileInspectionService(scanner), context,
            new NoopLogger<DocumentIngestionService>(),
            Options.Create(new MalwareVerdictPolicyOptions { MaximumCleanVerdictAge = TimeSpan.FromHours(24) }));

    // SEC-ING-02: the tenant context is mandatory. Every context these tests build comes from
    // ContextFor(null) — the cross-tenant worker view — so the queue is given the matching
    // null-tenant StubTenant and takes the deliberate nexora_pipeline_app role.
    private static ExtractionQueue NewQueue(ErpRfqAutomationContext context) =>
        new(context, new NoopLogger<ExtractionQueue>(), new StubTenant(null));

    private static FileInspectionResult ClearedInspection(int? inspectedLength = null) => new(
        FileInspectionStatus.Cleared, "text/csv", inspectedLength ?? ValidCsv().Length,
        "Inspection and malware scan passed.", "test-scanner", "clean")
    {
        MalwareStatus = MalwareScanStatus.Clean,
        ErrorCode = "security_scan_cleared"
    };

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

    private sealed class CountingScanner : IMalwareScanner
    {
        public int Calls { get; private set; }
        public MalwareScanStatus Status { get; set; } = MalwareScanStatus.Clean;

        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(Status switch
            {
                MalwareScanStatus.Clean => MalwareScanResult.Clean("test-clamav", "daily-test"),
                MalwareScanStatus.Infected => MalwareScanResult.Infected("test-clamav", "test-signature"),
                MalwareScanStatus.Unavailable => MalwareScanResult.Unavailable("test-clamav", "daemon unavailable"),
                _ => MalwareScanResult.Error("test-clamav", "scanner error")
            });
        }
    }

    private sealed class UnavailableReadEvidenceStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) =>
            throw new FileNotFoundException("Authorized test object is unavailable.");
    }

    private sealed class TransientReadEvidenceStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) =>
            throw new IOException("Authorized test storage is temporarily unavailable.");
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
