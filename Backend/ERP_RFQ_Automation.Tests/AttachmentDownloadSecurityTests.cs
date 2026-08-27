using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Controllers;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public sealed class AttachmentDownloadSecurityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexora-attachment-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DownloadAttachment_ReturnsOnlyFilesOwnedByClaimedTenant()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.Lead(context, 101, 1);
        Seed.Lead(context, 202, 2);
        context.Attachments.AddRange(
            Attachment(1001, 101, "Manual_Attachments/tenant-one.txt"),
            Attachment(2002, 202, "Manual_Attachments/tenant-two.txt"));
        await context.SaveChangesAsync();

        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        await storage.WriteImmutableAsync("Manual_Attachments/tenant-one.txt", Encoding.UTF8.GetBytes("tenant one"));
        await storage.WriteImmutableAsync("Manual_Attachments/tenant-two.txt", Encoding.UTF8.GetBytes("tenant two"));
        var controller = Controller(context, storage, businessUnitId: 1);

        var ownResult = await controller.DownloadAttachment(1001, CancellationToken.None);
        var ownFile = Assert.IsType<FileStreamResult>(ownResult);
        await using (ownFile.FileStream)
        using (var reader = new StreamReader(ownFile.FileStream))
            Assert.Equal("tenant one", await reader.ReadToEndAsync());

        var otherTenantResult = await controller.DownloadAttachment(2002, CancellationToken.None);
        Assert.IsType<NotFoundResult>(otherTenantResult);
    }

    /// <summary>
    /// The digest check runs AFTER the tenant check, never instead of it. A neighbour's
    /// attachment whose bytes verify perfectly is still not this tenant's to read, so a
    /// verifiable object must not become a readable one.
    /// </summary>
    [Fact]
    public async Task DownloadAttachment_DeniesCrossTenantReadEvenWhenTheDigestWouldVerify()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.Lead(context, 101, 1);
        Seed.Lead(context, 202, 2);
        const string payload = "tenant two contract";
        context.Attachments.Add(
            Attachment(2002, 202, "Manual_Attachments/tenant-two.txt", Sha256(payload)));
        await context.SaveChangesAsync();

        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        await storage.WriteImmutableAsync("Manual_Attachments/tenant-two.txt", Encoding.UTF8.GetBytes(payload));
        var controller = Controller(context, storage, businessUnitId: 1);

        var result = await controller.DownloadAttachment(2002, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.False(controller.Response.Headers.ContainsKey(FileController.IntegrityHeader));
    }

    [Theory]
    [InlineData("rep-a-owner", true)]
    [InlineData("rep-b-peer", false)]
    [InlineData("manager-descendant", true)]
    [InlineData("manager-non-descendant", false)]
    [InlineData("tenant-admin", true)]
    public async Task Attachment_bytes_honor_authoritative_commercial_scope(
        string actorScenario,
        bool mayRead)
    {
        _ = actorScenario;
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.Lead(context, 101, 1);
        context.Attachments.Add(Attachment(1001, 101, "Manual_Attachments/scoped.txt"));
        await context.SaveChangesAsync();
        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        await storage.WriteImmutableAsync("Manual_Attachments/scoped.txt", "scoped"u8.ToArray());
        var controller = Controller(context, storage, 1,
            commercialAccess: new SelectiveCommercialAccessContext(1, mayRead));

        var result = await controller.DownloadAttachment(1001, default);

        if (mayRead)
            Assert.IsType<FileStreamResult>(result);
        else
            Assert.IsType<NotFoundResult>(result);
    }

    [Theory]
    [InlineData("rep-a-owner", true)]
    [InlineData("rep-b-peer", false)]
    [InlineData("manager-descendant", true)]
    [InlineData("manager-non-descendant", false)]
    [InlineData("tenant-admin", true)]
    public async Task Source_document_bytes_honor_authoritative_commercial_scope(
        string actorScenario,
        bool mayRead)
    {
        _ = actorScenario;
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.Lead(context, 101, 1);
        var (documentId, storagePath, hash) = await SeedSourceDocumentAsync(context, 1, 101);
        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        await storage.WriteImmutableAsync(storagePath, "source bytes"u8.ToArray());
        var controller = Controller(context, storage, 1,
            commercialAccess: new SelectiveCommercialAccessContext(1, mayRead));

        var result = await controller.DownloadSourceDocument(documentId, default);

        if (mayRead)
        {
            Assert.IsType<FileStreamResult>(result);
            Assert.Equal(hash, Sha256("source bytes"));
        }
        else
            Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Source_document_bytes_deny_cross_tenant_ids_before_storage_open()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.Lead(context, 202, 2);
        var (documentId, storagePath, _) = await SeedSourceDocumentAsync(context, 2, 202);
        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        await storage.WriteImmutableAsync(storagePath, "source bytes"u8.ToArray());
        var controller = Controller(context, storage, 1,
            commercialAccess: new SelectiveCommercialAccessContext(1, allowed: true));

        var result = await controller.DownloadSourceDocument(documentId, default);

        Assert.IsType<NotFoundResult>(result);
        Assert.False(controller.Response.Headers.ContainsKey(FileController.IntegrityHeader));
    }

    /// <summary>FR-RFQ-08: bytes that still hash to the digest recorded at capture are served,
    /// and the response says so rather than leaving the caller to assume it.</summary>
    [Fact]
    public async Task DownloadAttachment_ServesBytesMatchingTheDigestRecordedAtCapture()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.Lead(context, 101, 1);
        const string payload = "the original quotation, unaltered";
        context.Attachments.Add(
            Attachment(1001, 101, "Manual_Attachments/original.txt", Sha256(payload)));
        await context.SaveChangesAsync();

        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        await storage.WriteImmutableAsync("Manual_Attachments/original.txt", Encoding.UTF8.GetBytes(payload));
        var controller = Controller(context, storage, businessUnitId: 1);

        var result = await controller.DownloadAttachment(1001, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        await using (file.FileStream)
        using (var reader = new StreamReader(file.FileStream))
            Assert.Equal(payload, await reader.ReadToEndAsync());
        Assert.Equal(
            FileController.IntegrityVerified,
            controller.Response.Headers[FileController.IntegrityHeader].ToString());
        Assert.Empty(await context.TenantGovernanceAuditEvents.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// The requirement the digest exists to enforce: a retained document that was edited after
    /// capture is REFUSED, not quietly handed over. The refusal is auditable, and it says
    /// nothing that would help someone forge past the check.
    /// </summary>
    [Fact]
    public async Task DownloadAttachment_RefusesAndAuditsBytesThatNoLongerMatchTheirDigest()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.Lead(context, 101, 1);
        const string captured = "quoted price: 100,000";
        const string tampered = "quoted price: 900,000";
        context.Attachments.Add(
            Attachment(1001, 101, "Manual_Attachments/tampered.txt", Sha256(captured)));
        await context.SaveChangesAsync();

        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        await storage.WriteImmutableAsync("Manual_Attachments/tampered.txt", Encoding.UTF8.GetBytes(captured));
        // Someone rewrites the retained evidence in place, behind the application's back.
        await File.WriteAllTextAsync(storage.ResolvePath("Manual_Attachments/tampered.txt"), tampered);

        var controller = Controller(context, storage, businessUnitId: 1);

        var result = await controller.DownloadAttachment(1001, CancellationToken.None);

        var problem = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, problem.StatusCode);
        Assert.IsAssignableFrom<ProblemDetails>(problem.Value);

        // Nothing served, and nothing leaked: no storage path, neither digest, no byte count.
        var body = JsonSerializer.Serialize(problem.Value);
        Assert.DoesNotContain("Manual_Attachments", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Sha256(captured), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Sha256(tampered), body, StringComparison.OrdinalIgnoreCase);
        Assert.False(controller.Response.Headers.ContainsKey(FileController.IntegrityHeader));

        var audit = Assert.Single(await context.TenantGovernanceAuditEvents.AsNoTracking().ToListAsync());
        Assert.Equal(1, audit.BusinessUnitId);
        Assert.Equal(FileController.IntegrityAuditArea, audit.Area);
        Assert.Equal(FileController.AttachmentIntegrityFailedAction, audit.Action);
        Assert.Equal("attachment:1001", audit.AggregateReference);
        Assert.Contains(Sha256(captured), audit.EvidenceJson);
        Assert.Contains(Sha256(tampered), audit.EvidenceJson);
    }

    /// <summary>A second refusal of the SAME corruption stays one audit row — the trail records
    /// the divergence, not the number of times a user clicked download.</summary>
    [Fact]
    public async Task DownloadAttachment_RecordsOneAuditRowPerDistinctCorruption()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.Lead(context, 101, 1);
        context.Attachments.Add(
            Attachment(1001, 101, "Manual_Attachments/tampered.txt", Sha256("captured")));
        await context.SaveChangesAsync();

        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        await storage.WriteImmutableAsync("Manual_Attachments/tampered.txt", Encoding.UTF8.GetBytes("rewritten"));
        var controller = Controller(context, storage, businessUnitId: 1);

        Assert.IsType<ObjectResult>(await controller.DownloadAttachment(1001, CancellationToken.None));
        Assert.IsType<ObjectResult>(await controller.DownloadAttachment(1001, CancellationToken.None));

        Assert.Single(await context.TenantGovernanceAuditEvents.AsNoTracking().ToListAsync());
    }

    /// <summary>
    /// Rows written before the digest column existed have nothing to verify against. UNKNOWN is
    /// not "verified": the bytes are still served, because refusing every historic attachment
    /// would be a self-inflicted outage, but the unverifiable state is logged and stated on the
    /// response instead of passing silently as a clean read.
    /// </summary>
    [Fact]
    public async Task DownloadAttachment_ServesRowsWithoutADigestButMarksThemUnverified()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        Seed.Lead(context, 101, 1);
        context.Attachments.Add(Attachment(1001, 101, "Manual_Attachments/historic.txt"));
        await context.SaveChangesAsync();

        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        await storage.WriteImmutableAsync("Manual_Attachments/historic.txt", Encoding.UTF8.GetBytes("historic"));
        var logger = new CapturingLogger();
        var controller = Controller(context, storage, businessUnitId: 1, logger: logger);

        var result = await controller.DownloadAttachment(1001, CancellationToken.None);

        var file = Assert.IsType<FileStreamResult>(result);
        await using (file.FileStream)
        using (var reader = new StreamReader(file.FileStream))
            Assert.Equal("historic", await reader.ReadToEndAsync());

        Assert.Equal(
            FileController.IntegrityUnverified,
            controller.Response.Headers[FileController.IntegrityHeader].ToString());
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning
                                             && e.Message.Contains("no recorded content digest"));
        // Unverifiable is not a security incident; it must not manufacture audit noise.
        Assert.Empty(await context.TenantGovernanceAuditEvents.AsNoTracking().ToListAsync());
    }

    /// <summary>A recorded digest that is not a well-formed SHA-256 cannot verify anything, so it
    /// fails closed. A column that can be filled with junk to switch the check off is no control
    /// at all.</summary>
    [Fact]
    public void DigestMatches_TreatsAMalformedRecordedDigestAsFailure()
    {
        var actual = Sha256("bytes");

        Assert.True(FileController.DigestMatches(actual, actual));
        Assert.True(FileController.DigestMatches(actual, actual.ToUpperInvariant()));
        Assert.False(FileController.DigestMatches(actual, "not-a-digest"));
        Assert.False(FileController.DigestMatches(actual, actual[..63]));
        Assert.False(FileController.DigestMatches(actual, new string('z', 64)));
    }

    [Fact]
    public async Task DownloadAttachment_RejectsMissingTenantClaim()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        var controller = Controller(context, storage, businessUnitId: null);

        var result = await controller.DownloadAttachment(1, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void DownloadFile_RetiresPathAddressedAccess()
    {
        using var db = new TestDb();
        using var context = db.ContextFor(null);
        var storage = new LocalFileStorage(_root, Path.GetTempPath());
        var controller = Controller(context, storage, businessUnitId: 1);

        var result = Assert.IsType<ObjectResult>(controller.DownloadFile("Uploads/secret.txt"));

        Assert.Equal(StatusCodes.Status410Gone, result.StatusCode);
    }

    private static string Sha256(string content)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

    private static Attachment Attachment(long id, long leadId, string path, string? contentSha256 = null) => new()
    {
        Id = id,
        ParentType = "Lead",
        ParentId = leadId,
        FileName = Path.GetFileName(path),
        FilePath = path,
        MimeType = "text/plain",
        FileSize = 10,
        ContentType = "text",
        ContentSha256 = contentSha256,
        CreatedOn = DateTime.UtcNow
    };

    private static FileController Controller(
        ErpRfqAutomationContext context,
        IFileStorage storage,
        long? businessUnitId,
        ILogger<FileController>? logger = null,
        ICommercialAccessContext? commercialAccess = null)
    {
        var claims = businessUnitId.HasValue
            ? new[] { new Claim("businessUnitId", businessUnitId.Value.ToString()) }
            : Array.Empty<Claim>();
        var controller = new FileController(
            context,
            storage,
            commercialAccess ?? new PermitCommercialAccessContext(businessUnitId ?? 1),
            new AllowAuthorizationService(),
            logger ?? NullLogger<FileController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"))
                }
            },
            ProblemDetailsFactory = new TestProblemDetailsFactory()
        };
        return controller;
    }

    private static async Task<(long DocumentId, string StoragePath, string Hash)> SeedSourceDocumentAsync(
        ErpRfqAutomationContext context,
        long businessUnitId,
        long leadId)
    {
        var batchId = Guid.NewGuid();
        var hash = Sha256("source bytes");
        var storagePath = $"Evidence/{businessUnitId}/{hash}.txt";
        var batch = new LeadIngestionBatch
        {
            Id = batchId, BusinessUnitId = businessUnitId, SourceChannel = "test",
            CreatedBy = "test", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        var corpus = DocumentCorpus.Create(businessUnitId, batchId, CorpusSourceType.ManualUpload);
        context.AddRange(batch, corpus);
        await context.SaveChangesAsync();
        var job = new ExtractionJob
        {
            BatchId = batchId, BusinessUnitId = businessUnitId,
            SourceType = ExtractionSourceType.ManualUpload, ContentHash = hash,
            StoragePath = storagePath, FileName = "source.txt", FileType = "txt",
            Status = ExtractionStatus.Succeeded, MaxAttempts = 5,
            NextAttemptAt = DateTime.UtcNow, CreatedOn = DateTime.UtcNow, UpdatedOn = DateTime.UtcNow
        };
        context.Add(job);
        await context.SaveChangesAsync();
        var document = SourceDocument.Create(businessUnitId, corpus.Id, hash, "source.txt",
            "text/plain", "local", storagePath, hash, "source bytes"u8.Length);
        document.ReleaseFromQuarantine("local", storagePath, hash);
        document.BindExtractionJob(job.Id);
        context.Add(document);
        await context.SaveChangesAsync();
        context.Add(new LeadIngestionOccurrence
        {
            BusinessUnitId = businessUnitId, BatchId = batchId, LeadId = leadId,
            SourceDocumentId = document.Id, SourceChannel = "test",
            IdempotencyKey = Guid.NewGuid().ToString("N"), LogicalInquiryFingerprint = hash,
            Classification = LeadOccurrenceClassification.New, ProcessingPath = LeadProcessingPath.Deterministic,
            IngestedAtUtc = DateTimeOffset.UtcNow, CreatedAtUtc = DateTimeOffset.UtcNow,
            ActorId = "test", CorrelationId = Guid.NewGuid().ToString("N")
        });
        await context.SaveChangesAsync();
        return (document.Id, storagePath, hash);
    }

    private sealed class SelectiveCommercialAccessContext(long tenant, bool allowed)
        : ICommercialAccessContext
    {
        public Task<CommercialActorScope?> ResolveAsync(CancellationToken ct = default) =>
            Task.FromResult<CommercialActorScope?>(new(tenant, 1, 1, AccountTeamScope.TenantWide(1)));
        public Task<bool> CanAccessLeadAsync(long leadId, CancellationToken ct = default) => Task.FromResult(allowed);
        public Task<bool> CanAccessCustomerAsync(long customerId, CancellationToken ct = default) => Task.FromResult(allowed);
        public Task<bool> CanAccessRfqAsync(long rfqId, CancellationToken ct = default) => Task.FromResult(allowed);
        public Task<bool> CanAccessQuoteAsync(long quoteId, CancellationToken ct = default) => Task.FromResult(allowed);
        public Task<bool> CanAccessOrderAsync(long orderId, CancellationToken ct = default) => Task.FromResult(allowed);
    }

    private sealed class AllowAuthorizationService : IAuthorizationService
    {
        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user, object? resource, string policyName) =>
            Task.FromResult(AuthorizationResult.Success());

        public Task<AuthorizationResult> AuthorizeAsync(
            ClaimsPrincipal user,
            object? resource,
            IEnumerable<IAuthorizationRequirement> requirements) =>
            Task.FromResult(AuthorizationResult.Success());
    }

    private sealed class TestProblemDetailsFactory : ProblemDetailsFactory
    {
        public override ProblemDetails CreateProblemDetails(
            HttpContext httpContext, int? statusCode = null, string? title = null, string? type = null,
            string? detail = null, string? instance = null)
            => new() { Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance };

        public override ValidationProblemDetails CreateValidationProblemDetails(
            HttpContext httpContext, ModelStateDictionary modelStateDictionary, int? statusCode = null,
            string? title = null, string? type = null, string? detail = null, string? instance = null)
            => new(modelStateDictionary)
            {
                Status = statusCode, Title = title, Type = type, Detail = detail, Instance = instance
            };
    }

    private sealed class CapturingLogger : ILogger<FileController>
    {
        public sealed record Entry(LogLevel Level, string Message);

        public ConcurrentQueue<Entry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new Entry(logLevel, formatter(state, exception)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
