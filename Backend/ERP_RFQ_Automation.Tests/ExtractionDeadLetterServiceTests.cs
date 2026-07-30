using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

public sealed class ExtractionDeadLetterServiceTests
{
    [Fact]
    public async Task VerifiedCleanSource_QueuesOneIdempotentRetry()
    {
        using var testDb = new TestDb();
        await using var db = testDb.ContextFor(7);
        var job = await SeedDeadLetterAsync(db, 7);
        var service = new ExtractionDeadLetterService(db, new AvailableStorage(), new Scanner(MalwareScanStatus.Clean));
        var command = new RecoverExtractionDeadLetterCommand("Pilot operator verified original evidence.", "recover-7-1");

        var result = await service.RecoverAsync(7, job.Id, "operator@example.com", command, default);
        var replay = await service.RecoverAsync(7, job.Id, "operator@example.com", command, default);

        Assert.Equal("RetryQueued", result.Status);
        Assert.False(result.BlocksReadiness);
        Assert.True(replay.IdempotentReplay);
        var persisted = await db.Set<ExtractionJob>().SingleAsync(x => x.Id == job.Id);
        Assert.Equal(ExtractionStatus.Pending, persisted.Status);
        Assert.Equal(6, persisted.MaxAttempts);
        Assert.NotNull(persisted.SourceDocumentOccurrenceId);
        Assert.Equal(IntakeOccurrenceStatus.Queued,
            (await db.Set<SourceDocumentOccurrence>().SingleAsync()).IntakeStatus);
        Assert.Equal(DocumentSecurityStatus.Cleared,
            (await db.Set<SourceDocument>().SingleAsync()).SecurityStatus);
        Assert.Single(await db.ExtractionDeadLetterEvents.ToListAsync());
    }

    [Fact]
    public async Task MissingSource_IsAuditedAndNoLongerPresentedAsRetryable()
    {
        using var testDb = new TestDb();
        await using var db = testDb.ContextFor(7);
        var job = await SeedDeadLetterAsync(db, 7);
        var service = new ExtractionDeadLetterService(db, new MissingStorage(), new Scanner(MalwareScanStatus.Clean));

        var result = await service.RecoverAsync(7, job.Id, "operator@example.com",
            new("Source verification requested for pilot closure.", "missing-7-1"), default);

        Assert.Equal("SourceObjectUnavailable", result.Status);
        Assert.False(result.BlocksReadiness);
        Assert.Empty(await service.ListAsync(7, default));
        Assert.Equal(ExtractionStatus.DeadLetter,
            (await db.Set<ExtractionJob>().SingleAsync(x => x.Id == job.Id)).Status);
    }

    [Fact]
    public async Task TenantCannotReadOrRecoverAnotherTenantsDeadLetter()
    {
        using var testDb = new TestDb();
        long jobId;
        await using (var owner = testDb.ContextFor(7))
            jobId = (await SeedDeadLetterAsync(owner, 7)).Id;
        await using var otherTenant = testDb.ContextFor(8);
        var service = new ExtractionDeadLetterService(
            otherTenant, new AvailableStorage(), new Scanner(MalwareScanStatus.Clean));

        Assert.Empty(await service.ListAsync(8, default));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.RecoverAsync(8, jobId,
            "other@example.com", new("Unauthorized recovery attempt.", "tenant-8-1"), default));
    }

    [Fact]
    public async Task ScannerOutage_DoesNotMutateOrResolveDeadLetter()
    {
        using var testDb = new TestDb();
        await using var db = testDb.ContextFor(7);
        var job = await SeedDeadLetterAsync(db, 7);
        var service = new ExtractionDeadLetterService(
            db, new AvailableStorage(), new Scanner(MalwareScanStatus.Unavailable));

        var failure = await Assert.ThrowsAsync<DeadLetterDependencyUnavailableException>(() =>
            service.RecoverAsync(7, job.Id, "operator@example.com",
                new("Retry after scanner health check.", "scanner-7-1"), default));

        Assert.Contains("scanner is unavailable", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.ExtractionDeadLetterEvents.ToListAsync());
    }

    [Fact]
    public async Task StorageOutage_DoesNotMisclassifySourceAsUnavailable()
    {
        using var testDb = new TestDb();
        await using var db = testDb.ContextFor(7);
        var job = await SeedDeadLetterAsync(db, 7);
        var service = new ExtractionDeadLetterService(
            db, new UnavailableStorage(), new Scanner(MalwareScanStatus.Clean));

        var failure = await Assert.ThrowsAsync<DeadLetterDependencyUnavailableException>(() =>
            service.RecoverAsync(7, job.Id, "operator@example.com",
                new("Verify source while storage is unavailable.", "storage-7-1"), default));

        Assert.Contains("storage is unavailable", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await db.ExtractionDeadLetterEvents.ToListAsync());
    }

    [Fact]
    public async Task IntegrityFailure_IsAuditedAndContinuesToBlockReadiness()
    {
        using var testDb = new TestDb();
        await using var db = testDb.ContextFor(7);
        var job = await SeedDeadLetterAsync(db, 7);
        var sourceId = await SeedClearedSourceAsync(db, job);
        var service = new ExtractionDeadLetterService(
            db, new IntegrityFailureStorage(), new Scanner(MalwareScanStatus.Clean));

        var result = await service.RecoverAsync(7, job.Id, "operator@example.com",
            new("Verify immutable evidence integrity.", "integrity-7-1"), default);

        Assert.Equal("EvidenceIntegrityFailure", result.Status);
        Assert.True(result.BlocksReadiness);
        Assert.Equal(ExtractionDeadLetterAction.EvidenceIntegrityFailure,
            (await db.ExtractionDeadLetterEvents.SingleAsync()).Action);
        Assert.True((await service.ListAsync(7, default)).Single().BlocksReadiness);
        var rejectedSource = await db.Set<SourceDocument>().SingleAsync(x => x.Id == sourceId);
        Assert.Equal(DocumentSecurityStatus.Rejected, rejectedSource.SecurityStatus);
        Assert.Null(rejectedSource.MalwareVerdictStatus);
        Assert.Null(rejectedSource.MalwareScannerEngine);
        Assert.Null(rejectedSource.MalwareSignatureVersion);
        Assert.Null(rejectedSource.MalwareScannedOn);
        Assert.False(rejectedSource.HasFreshCleanMalwareVerdict(
            DateTimeOffset.UtcNow, TimeSpan.FromDays(1)));

        var retry = new ExtractionDeadLetterService(
            db, new AvailableStorage(), new Scanner(MalwareScanStatus.Clean));
        await Assert.ThrowsAsync<InvalidOperationException>(() => retry.RecoverAsync(
            7, job.Id, "operator@example.com",
            new("A security finding cannot be softened by retry.", "integrity-7-2"), default));
        Assert.Single(await db.ExtractionDeadLetterEvents.ToListAsync());
    }

    [Fact]
    public async Task MalwareDetection_RejectsPreviouslyClearedSourceAndCannotBeRetried()
    {
        using var testDb = new TestDb();
        await using var db = testDb.ContextFor(7);
        var job = await SeedDeadLetterAsync(db, 7);
        var sourceId = await SeedClearedSourceAsync(db, job);
        var service = new ExtractionDeadLetterService(
            db, new AvailableStorage(), new Scanner(MalwareScanStatus.Infected));

        var result = await service.RecoverAsync(7, job.Id, "operator@example.com",
            new("Malware was detected during recovery.", "malware-7-1"), default);

        Assert.Equal("MalwareDetected", result.Status);
        Assert.True(result.BlocksReadiness);
        var rejectedSource = await db.Set<SourceDocument>().SingleAsync(x => x.Id == sourceId);
        Assert.Equal(DocumentSecurityStatus.Rejected, rejectedSource.SecurityStatus);
        Assert.Equal(MalwareScanStatus.Infected.ToString(), rejectedSource.MalwareVerdictStatus);
        Assert.Equal("test-scanner", rejectedSource.MalwareScannerEngine);
        Assert.Equal("test-signature", rejectedSource.MalwareSignatureVersion);
        Assert.NotNull(rejectedSource.MalwareScannedOn);
        Assert.False(rejectedSource.HasFreshCleanMalwareVerdict(
            DateTimeOffset.UtcNow, TimeSpan.FromDays(1)));
        Assert.Equal(ExtractionDeadLetterAction.MalwareDetected,
            (await db.ExtractionDeadLetterEvents.SingleAsync()).Action);

        var retry = new ExtractionDeadLetterService(
            db, new AvailableStorage(), new Scanner(MalwareScanStatus.Clean));
        await Assert.ThrowsAsync<InvalidOperationException>(() => retry.RecoverAsync(
            7, job.Id, "operator@example.com",
            new("A malware finding cannot be softened by retry.", "malware-7-2"), default));
        Assert.Single(await db.ExtractionDeadLetterEvents.ToListAsync());
    }

    [Fact]
    public async Task IdempotencyKey_IsScopedToOneTenantJob()
    {
        using var testDb = new TestDb();
        await using var db = testDb.ContextFor(7);
        var first = await SeedDeadLetterAsync(db, 7);
        var second = await SeedDeadLetterAsync(db, 7);
        var service = new ExtractionDeadLetterService(
            db, new AvailableStorage(), new Scanner(MalwareScanStatus.Clean));
        var command = new RecoverExtractionDeadLetterCommand("Recover separate jobs.", "shared-key");

        var firstResult = await service.RecoverAsync(7, first.Id, "operator@example.com", command, default);
        var secondResult = await service.RecoverAsync(7, second.Id, "operator@example.com", command, default);

        Assert.Equal("RetryQueued", firstResult.Status);
        Assert.Equal("RetryQueued", secondResult.Status);
        Assert.Equal(2, await db.ExtractionDeadLetterEvents.CountAsync());
    }

    [Fact]
    public void RecoveryEndpoint_RequiresTenantOperationsAndLeadCreationPermissions()
    {
        var method = typeof(OperationsReadinessController)
            .GetMethod(nameof(OperationsReadinessController.RecoverExtractionDeadLetter))!;
        var permissions = method.GetCustomAttributes(typeof(RequireModulePermissionAttribute), true)
            .Cast<RequireModulePermissionAttribute>()
            .Select(attribute => (attribute.ModuleName, attribute.Action))
            .ToArray();

        Assert.Contains(("Users", PermissionAction.Edit), permissions);
        Assert.Contains(("Leads", PermissionAction.Create), permissions);
    }

    [Fact]
    public void DurableS3Store_RecognizesLegacyLocalSourcePathAsUnavailable()
    {
        using var storage = new S3EvidenceObjectStorage(Options.Create(new S3EvidenceStorageOptions
        {
            ServiceUrl = "http://127.0.0.1:1",
            AccessKeyId = "test-access-key",
            SecretAccessKey = "test-secret-key",
            Bucket = "test-evidence",
        }));

        Assert.True(ExtractionDeadLetterService.IsLegacySourcePathUnavailable(
            storage, "Evidence/Upload/customer-rfq.csv"));
        Assert.False(ExtractionDeadLetterService.IsLegacySourcePathUnavailable(
            storage, "s3://test-evidence/Evidence/tenants/7/quarantine/source.csv"));
    }

    private static async Task<ExtractionJob> SeedDeadLetterAsync(
        ERP_RFQ_Automation.Models.ErpRfqAutomationContext db, long tenantId)
    {
        var now = DateTime.UtcNow;
        var job = new ExtractionJob
        {
            BatchId = Guid.NewGuid(),
            BusinessUnitId = tenantId,
            SourceType = ExtractionSourceType.ManualUpload,
            ContentHash = new string('a', 64),
            StoragePath = "evidence://immutable/source",
            FileName = "customer-rfq.csv",
            FileType = "csv",
            Status = ExtractionStatus.DeadLetter,
            Attempts = 3,
            MaxAttempts = 3,
            LastError = "Extraction provider failed.",
            NextAttemptAt = now,
            CreatedOn = now,
            UpdatedOn = now,
        };
        db.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private static async Task<long> SeedClearedSourceAsync(
        ERP_RFQ_Automation.Models.ErpRfqAutomationContext db,
        ExtractionJob job)
    {
        var corpus = DocumentCorpus.Create(
            job.BusinessUnitId, job.BatchId, CorpusSourceType.ManualUpload);
        db.Add(corpus);
        await db.SaveChangesAsync();

        var source = SourceDocument.Create(
            job.BusinessUnitId,
            corpus.Id,
            job.ContentHash,
            job.FileName!,
            "text/csv",
            "test-evidence",
            $"cleared/{job.ContentHash}.csv",
            "original-version",
            14);
        source.RecordMalwareVerdict(
            MalwareScanStatus.Clean, "previous-scanner", "previous-signatures");
        source.ReleaseFromQuarantine(
            "test-evidence", $"cleared/{job.ContentHash}.csv", "original-version");
        source.BindExtractionJob(job.Id);
        db.Add(source);
        await db.SaveChangesAsync();

        var occurrence = SourceDocumentOccurrence.Create(
            job.BusinessUnitId,
            source.Id,
            corpus.Id,
            $"dead-letter:{job.Id}",
            "{}",
            job.Id);
        occurrence.BindExtractionJob(job.Id);
        occurrence.MarkProcessing();
        occurrence.MarkDeadLetter("extraction_failed");
        db.Add(occurrence);
        await db.SaveChangesAsync();

        job.SourceDocumentOccurrenceId = occurrence.Id;
        await db.SaveChangesAsync();
        return source.Id;
    }

    private sealed class AvailableStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            Task.FromResult(new EvidenceObject(
                $"s3://test-evidence/{zone}/{sha256}{extension}", "test-evidence",
                $"{zone}/{sha256}{extension}", sha256, sha256, content.Length));
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream("clean evidence"u8.ToArray(), writable: false));
    }

    private sealed class MissingStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) => Task.FromException<Stream>(new FileNotFoundException());
    }

    private sealed class IntegrityFailureStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) => Task.FromException<Stream>(new InvalidDataException("hash mismatch"));
    }

    private sealed class UnavailableStorage : IEvidenceObjectStorage
    {
        public bool IsDurable => true;
        public Task ProbeAsync(CancellationToken ct = default) =>
            Task.FromException(new IOException("storage offline"));
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256,
            CancellationToken ct = default) => Task.FromException<Stream>(new FileNotFoundException());
    }

    private sealed class Scanner(MalwareScanStatus status) : IMalwareScanner
    {
        public Task<MalwareScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default) =>
            Task.FromResult(status switch
            {
                MalwareScanStatus.Clean => MalwareScanResult.Clean("test-scanner", "test-signatures"),
                MalwareScanStatus.Infected => MalwareScanResult.Infected("test-scanner", "test-signature"),
                MalwareScanStatus.Unavailable => MalwareScanResult.Unavailable("test-scanner", "offline"),
                _ => MalwareScanResult.Error("test-scanner", "error"),
            });
    }
}
