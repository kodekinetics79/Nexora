using System.Text;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The regression twin of <see cref="EvidenceStorageOutageIngestionTests"/>: the refusals
/// there are only correct if the healthy path is untouched.
///
/// <para>Production dialect, because a successful ingest reaches the duplicate-candidate
/// lookup that orders by a <c>DateTimeOffset</c> — SQLite cannot translate that, so the whole
/// happy path is PostgreSQL-only here as it is everywhere else in this suite.</para>
/// </summary>
[Collection(PostgreSqlIntegrationCollection.Name)]
public sealed class EvidenceStorageOutageIngestionPostgreSqlTests
{
    private readonly PostgreSqlTestDatabase _database;

    public EvidenceStorageOutageIngestionPostgreSqlTests(PostgreSqlTestDatabase database)
        => _database = database;

    /// <summary>
    /// REGRESSION. Store the immutable source, THEN queue. The refusal added for the
    /// 2026-08-12 incident is built on that ordering, so if buying the refusal cost the
    /// ordering — a job that exists without its bytes — the fix is worse than the bug.
    /// </summary>
    [Fact]
    [Trait("Category", "PostgreSQL")]
    public async Task HealthyStore_StillStoresTheSourceBytesAndThenQueuesTheJob()
    {
        var tenantId = Random.Shared.Next(2_000_000, 8_000_000);
        var root = Path.Combine(Path.GetTempPath(), "nexora-evidence-regression-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var bytes = ValidCsv();
        try
        {
            await using var context = _database.ContextFor(null);
            Seed.EnsureBusinessUnit(context, tenantId);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var ingestion = new DocumentIngestionService(
                new ExtractionQueue(context, new NoopLogger<ExtractionQueue>(), new StubTenant(null)),
                new LocalEvidenceObjectStorage(new LocalFileStorage(root, root)),
                new ClearedInspectionService(),
                context,
                new NoopLogger<DocumentIngestionService>());

            var ingested = await ingestion.IngestAsync(
                bytes, "customer-rfq.csv", tenantId, ExtractionSourceType.ManualUpload, priority: 10);

            Assert.Equal(EnqueueOutcome.Enqueued, ingested.Outcome);
            Assert.True(ingested.JobId > 0);

            // The bytes are ON the store, not merely recorded as being there. A queued job
            // pointing at an object nobody wrote is the silent data loss the ordering prevents.
            Assert.True(File.Exists(ingested.StoragePath));
            Assert.Equal(bytes, await File.ReadAllBytesAsync(ingested.StoragePath));

            context.ChangeTracker.Clear();
            var source = await context.Set<SourceDocument>()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(ingested.ContentHash, source.ContentHash);
            Assert.Equal(bytes.LongLength, source.ByteSize);
            Assert.Equal(DocumentSecurityStatus.Cleared, source.SecurityStatus);

            var occurrence = await context.Set<SourceDocumentOccurrence>()
                .SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(ingested.SourceDocumentOccurrenceId, occurrence.Id);
            Assert.Equal(IntakeOccurrenceStatus.Queued, occurrence.IntakeStatus);
            Assert.Equal(ingested.JobId, occurrence.ExtractionJobId);

            var job = await context.Set<ExtractionJob>().SingleAsync(x => x.BusinessUnitId == tenantId);
            Assert.Equal(ingested.JobId, job.Id);
            Assert.Equal(ExtractionStatus.Pending, job.Status);
            Assert.Equal(occurrence.Id, job.SourceDocumentOccurrenceId);
            Assert.Equal(ingested.ContentHash, job.ContentHash);
            // The job addresses the object that was written before the job existed.
            Assert.Equal(ingested.StoragePath, job.StoragePath);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static byte[] ValidCsv() => Encoding.UTF8.GetBytes(
        "RFQ No,Buyer Name,Product Name,Quantity,Unit Price,Currency\n"
        + "RFQ-REGRESSION-1,Acme Procurement,Pressure Sensor,5,125.50,USD\n");

    private sealed class ClearedInspectionService : IFileInspectionService
    {
        public Task<FileInspectionResult> InspectAsync(
            FileInspectionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FileInspectionResult(
                FileInspectionStatus.Cleared, "text/csv",
                request.DeclaredLength ?? 0, "Inspection and malware scan passed.",
                "test-scanner", "clean")
            {
                MalwareStatus = MalwareScanStatus.Clean,
                ErrorCode = "security_scan_cleared"
            });
    }
}
