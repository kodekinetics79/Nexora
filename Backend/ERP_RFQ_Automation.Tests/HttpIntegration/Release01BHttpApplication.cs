using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests.HttpIntegration;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class Release01BHttpCollection : ICollectionFixture<Release01BHttpApplication>
{
    public const string Name = "Release 01B authenticated HTTP PostgreSQL";
}

public sealed class Release01BHttpApplication : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const long TenantA = 81_001;
    public const long TenantB = 81_002;
    public const long AllowedRole = 82_001;
    public const long DeniedRole = 82_002;
    public const long TenantACustomerId = 86_001;
    public const long TenantBCustomerId = 86_002;
    public const long TenantAContactId = 86_101;
    public const long TenantBContactId = 86_102;
    public const long TenantALeadId = 87_001;
    public const long TenantBLeadId = 87_002;
    public const long TenantAAttachmentId = 88_001;
    public const long TenantBAttachmentId = 88_002;
    public const long TenantAMatchOccurrenceId = 89_001;

    private const string Issuer = "nexora-release-01b-tests";
    private const string Audience = "nexora-release-01b-api";
    private const string JwtKey = "release-01b-http-integration-signing-key-2026";
    private const string TestSecret = "release-01b-commercial-finance-secret-2026";

    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("nexora_release_01b_http")
        .WithUsername("nexora")
        .WithPassword("nexora-release-01b-tests")
        .Build();
    private readonly TestEvidenceStorage _evidenceStorage = new();

    public Guid TenantABatchId { get; } = Guid.Parse("a1000000-0000-0000-0000-000000000001");
    public Guid TenantBBatchId { get; } = Guid.Parse("b1000000-0000-0000-0000-000000000001");

    public async Task InitializeAsync()
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await _postgres.StartAsync();

        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .ConfigureWarnings(warnings => warnings.Ignore(RelationalEventId.PendingModelChangesWarning))
            .Options;
        await using var db = new ErpRfqAutomationContext(options);
        await db.Database.MigrateAsync();
        await SeedAsync(db);

        // Force host construction only after migrations and representative data exist.
        _ = Server;
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _postgres.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _postgres.GetConnectionString());
        builder.UseSetting("ConnectionStrings:MigrationConnection", _postgres.GetConnectionString());
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:Audience", Audience);
        builder.UseSetting("Jwt:PlatformKey", JwtKey);
        builder.UseSetting("CommercialFinance:ContactVerificationSecret", TestSecret);
        builder.UseSetting("CommercialFinance:DunningProviderWebhookSecret", TestSecret);
        builder.UseSetting("CommercialFinance:AuditActorSecret", TestSecret);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IEvidenceObjectStorage>();
            services.AddSingleton<IEvidenceObjectStorage>(_evidenceStorage);
        });
    }

    public string Token(long roleId, long? tenantId, long userId = 83_001)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("roleId", roleId.ToString())
        };
        if (tenantId.HasValue)
            claims.Add(new Claim("businessUnitId", tenantId.Value.ToString()));

        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(JwtKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            Issuer,
            Audience,
            claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(10),
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public void CorruptTenantAEvidence() =>
        _evidenceStorage.Replace("test-evidence://tenant-a/v1", Encoding.UTF8.GetBytes("tampered-evidence"));

    public void RestoreTenantAEvidence() =>
        _evidenceStorage.Replace("test-evidence://tenant-a/v1", Encoding.UTF8.GetBytes("tenant-a-authoritative-evidence"));

    public async Task<(LeadOccurrenceClassification Classification, int AuditCount)> MatchDecisionStateAsync()
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_postgres.GetConnectionString()).Options;
        await using var db = new ErpRfqAutomationContext(options);
        var classification = await db.Set<LeadIngestionOccurrence>().IgnoreQueryFilters()
            .Where(x => x.Id == TenantAMatchOccurrenceId).Select(x => x.Classification).SingleAsync();
        var auditCount = await db.Set<LeadIdentityAuditEvent>().IgnoreQueryFilters()
            .CountAsync(x => x.OccurrenceId == TenantAMatchOccurrenceId && x.IdempotencyKey == "http-match-defer");
        return (classification, auditCount);
    }

    private async Task SeedAsync(ErpRfqAutomationContext db)
    {
        var now = DateTimeOffset.UtcNow;
        db.BusinessUnits.AddRange(
            BusinessUnit(TenantA, "HTTP-A"),
            BusinessUnit(TenantB, "HTTP-B"));

        const long leadsModuleId = 84_001;
        const long dashboardModuleId = 84_002;
        const long customersModuleId = 84_003;
        db.Modules.AddRange(
            Module(leadsModuleId, "Leads"),
            Module(dashboardModuleId, "Dashboard"),
            Module(customersModuleId, "Customers"));

        db.SetupMasters.AddRange(
            Role(AllowedRole, TenantA, "Release 01B Reader"),
            Role(DeniedRole, TenantA, "Release 01B Denied"));

        db.RolePermissions.AddRange(
            Permission(85_001, AllowedRole, leadsModuleId, TenantA, canEdit: true),
            Permission(85_002, AllowedRole, dashboardModuleId, TenantA),
            Permission(85_003, AllowedRole, customersModuleId, TenantA));

        db.Customers.AddRange(
            Customer(TenantACustomerId, TenantA, "Tenant A Customer", "buyer-a@nexora.invalid"),
            Customer(TenantBCustomerId, TenantB, "Tenant B Customer", "buyer-b@nexora.invalid"));
        db.Contacts.AddRange(
            Contact(TenantAContactId, TenantA, TenantACustomerId, "contact-a@nexora.invalid"),
            Contact(TenantBContactId, TenantB, TenantBCustomerId, "contact-b@nexora.invalid"));

        db.EmailConfigurations.AddRange(
            EmailConfiguration(86_201, TenantA, "tenant-a-ingestion@nexora.invalid"),
            EmailConfiguration(86_202, TenantB, "tenant-b-ingestion@nexora.invalid"));
        db.EmailIngests.AddRange(
            EmailIngest(86_301, 86_201, "tenant-a-message"),
            EmailIngest(86_302, 86_202, "tenant-b-message"));
        db.Leads.AddRange(
            Lead(TenantALeadId, TenantA, 86_301, TenantACustomerId, "HTTP-A-RFQ"),
            Lead(TenantBLeadId, TenantB, 86_302, TenantBCustomerId, "HTTP-B-RFQ"));

        db.Set<LeadIngestionBatch>().AddRange(
            Batch(TenantABatchId, TenantA, now),
            Batch(TenantBBatchId, TenantB, now));
        var tenantAOccurrence = Occurrence(TenantABatchId, TenantA, "tenant-a-new", LeadOccurrenceClassification.New, now);
        tenantAOccurrence.LeadId = TenantALeadId;
        tenantAOccurrence.OriginalFileName = "tenant-a.txt";
        var possibleMatch = Occurrence(TenantABatchId, TenantA, "tenant-a-possible", LeadOccurrenceClassification.PossibleMatchReviewRequired, now);
        possibleMatch.Id = TenantAMatchOccurrenceId;
        possibleMatch.MatchCandidates.Add(new LeadMatchCandidate
        {
            Id = 89_101,
            BusinessUnitId = TenantA,
            CandidateLeadId = TenantALeadId,
            Confidence = 0.73m,
            MatchEvidenceJson = "{\"rfqReference\":\"similar\"}",
            DifferencesJson = "[]",
            DownstreamImpactJson = "[]",
            ReviewState = LeadMatchReviewState.Pending,
            Version = 1
        });
        db.Set<LeadIngestionOccurrence>().AddRange(
            tenantAOccurrence,
            possibleMatch,
            Occurrence(TenantBBatchId, TenantB, "tenant-b-new", LeadOccurrenceClassification.New, now),
            Occurrence(TenantBBatchId, TenantB, "tenant-b-duplicate", LeadOccurrenceClassification.ExactDuplicate, now));
        await db.SaveChangesAsync();

        var corpusA = DocumentCorpus.Create(TenantA, TenantABatchId, CorpusSourceType.ManualUpload, now);
        var corpusB = DocumentCorpus.Create(TenantB, TenantBBatchId, CorpusSourceType.ManualUpload, now);
        db.Set<DocumentCorpus>().AddRange(corpusA, corpusB);
        await db.SaveChangesAsync();

        var tenantABytes = Encoding.UTF8.GetBytes("tenant-a-authoritative-evidence");
        var tenantBBytes = Encoding.UTF8.GetBytes("tenant-b-authoritative-evidence");
        var hashA = Sha256(tenantABytes);
        var hashB = Sha256(tenantBBytes);
        const string storageA = "test-evidence://tenant-a/v1";
        const string storageB = "test-evidence://tenant-b/v1";
        _evidenceStorage.Add(storageA, hashA, tenantABytes);
        _evidenceStorage.Add(storageB, hashB, tenantBBytes);
        var sourceA = SourceDocument.Create(TenantA, corpusA.Id, hashA, "tenant-a.txt",
            "text/plain", "acceptance", "tenant-a", "v1", tenantABytes.Length, now);
        var sourceB = SourceDocument.Create(TenantB, corpusB.Id, hashB, "tenant-b.txt",
            "text/plain", "acceptance", "tenant-b", "v1", tenantBBytes.Length, now);
        db.Set<SourceDocument>().AddRange(sourceA, sourceB);
        await db.SaveChangesAsync();

        var sourceOccurrenceA = SourceDocumentOccurrence.Create(TenantA, sourceA.Id, corpusA.Id, "http:tenant-a", "{}", receivedOn: now);
        var sourceOccurrenceB = SourceDocumentOccurrence.Create(TenantB, sourceB.Id, corpusB.Id, "http:tenant-b", "{}", receivedOn: now);
        db.Set<SourceDocumentOccurrence>().AddRange(sourceOccurrenceA, sourceOccurrenceB);
        await db.SaveChangesAsync();

        var jobA = ExtractionJob(TenantA, sourceOccurrenceA.Id, storageA, hashA, "tenant-a.txt", now);
        var jobB = ExtractionJob(TenantB, sourceOccurrenceB.Id, storageB, hashB, "tenant-b.txt", now);
        db.Set<ExtractionJob>().AddRange(jobA, jobB);
        await db.SaveChangesAsync();
        sourceA.BindExtractionJob(jobA.Id, now);
        sourceB.BindExtractionJob(jobB.Id, now);
        sourceOccurrenceA.BindExtractionJob(jobA.Id);
        sourceOccurrenceB.BindExtractionJob(jobB.Id);

        db.Set<LeadOccurrenceDocument>().AddRange(
            new LeadOccurrenceDocument { BusinessUnitId = TenantA, OccurrenceId = tenantAOccurrence.Id,
                SourceDocumentId = sourceA.Id, Role = "Primary", Ordinal = 0, LinkedAtUtc = now },
            new LeadOccurrenceDocument { BusinessUnitId = TenantB,
                OccurrenceId = await db.Set<LeadIngestionOccurrence>().Where(x => x.BusinessUnitId == TenantB && x.IdempotencyKey == "tenant-b-new").Select(x => x.Id).SingleAsync(),
                SourceDocumentId = sourceB.Id, Role = "Primary", Ordinal = 0, LinkedAtUtc = now });
        db.Attachments.AddRange(
            Attachment(TenantAAttachmentId, TenantALeadId, "tenant-a.txt", tenantABytes.Length),
            Attachment(TenantBAttachmentId, TenantBLeadId, "tenant-b.txt", tenantBBytes.Length));
        await db.SaveChangesAsync();
    }

    private static BusinessUnit BusinessUnit(long id, string code) => new()
    {
        Id = id,
        BusinessUnitCode = code,
        BusinessUnitName = $"Release 01B {code}",
        IsActive = true,
        CreatedBy = "release-01b-tests",
        CreatedOn = DateTime.UtcNow
    };

    private static Module Module(long id, string name) => new()
    {
        Id = id,
        ModuleName = name,
        IsActive = true,
        CreatedBy = "release-01b-tests",
        CreatedOn = DateTime.UtcNow
    };

    private static SetupMaster Role(long id, long tenantId, string name) => new()
    {
        SetupId = id,
        SetupType = "Role",
        SetupCode = name.Replace(' ', '_').ToUpperInvariant(),
        SetupValue = name,
        BusinessUnitId = tenantId,
        IsActive = true,
        CreatedBy = "release-01b-tests",
        CreatedOn = DateTime.UtcNow
    };

    private static RolePermission Permission(long id, long roleId, long moduleId, long tenantId, bool canEdit = false) => new()
    {
        Id = id,
        RoleId = roleId,
        ModuleId = moduleId,
        BusinessUnitId = tenantId,
        CanEdit = canEdit,
        CreatedBy = "release-01b-tests",
        CreatedOn = DateTime.UtcNow
    };

    private static Customer Customer(long id, long tenantId, string name, string email) => new()
    {
        Id = id, Buid = tenantId, Name = name, ContactEmail = email, ImageUrl = string.Empty,
        IsActive = true, CreatedBy = "release-01b-tests", CreatedOn = DateTime.UtcNow
    };

    private static Contact Contact(long id, long tenantId, long customerId, string email) => new()
    {
        Id = id, BusinessUnitId = tenantId, CustomerId = customerId, FirstName = "HTTP",
        LastName = "Contact", Email = email, IsActive = true, CreatedBy = "release-01b-tests",
        CreatedOn = DateTime.UtcNow
    };

    private static EmailConfiguration EmailConfiguration(long id, long tenantId, string email) => new()
    {
        Id = id, BusinessUnitId = tenantId, ConfigurationName = $"HTTP-{tenantId}", EmailAddress = email,
        Protocol = "IMAP", Host = "localhost", Port = 993, Username = "tests", Password = "tests",
        UseSsl = true, PollingInterval = 300, IsActive = true, CreatedOn = DateTime.UtcNow
    };

    private static EmailIngest EmailIngest(long id, long configurationId, string messageId) => new()
    {
        Id = id, EmailConfigurationId = configurationId, MessageId = messageId,
        FromEmail = "buyer@nexora.invalid", ParseStatus = "NeedsReview", CreatedOn = DateTime.UtcNow
    };

    private static Lead Lead(long id, long tenantId, long ingestId, long customerId, string rfq)
    {
        var lead = new Lead
        {
            Id = id, BusinessUnitId = tenantId, EmailIngestsId = ingestId,
            Rfqno = rfq, RecDate = DateTime.UtcNow, LeadSource = "HttpIntegration",
            CreatedBy = "release-01b-tests", CreatedDate = DateTime.UtcNow
        };
        lead.ResolveCommercialIdentity(customerId, null, "EXACT");
        return lead;
    }

    private static ExtractionJob ExtractionJob(long tenantId, long sourceOccurrenceId, string storagePath,
        string hash, string fileName, DateTimeOffset now) => new()
    {
        BusinessUnitId = tenantId, SourceDocumentOccurrenceId = sourceOccurrenceId,
        BatchId = Guid.NewGuid(), SourceType = ExtractionSourceType.ManualUpload,
        ContentHash = hash, StoragePath = storagePath, FileName = fileName, FileType = "txt",
        Status = ExtractionStatus.Succeeded, Attempts = 1, MaxAttempts = 5,
        NextAttemptAt = now.UtcDateTime, CreatedOn = now.UtcDateTime, UpdatedOn = now.UtcDateTime
    };

    private static Attachment Attachment(long id, long leadId, string fileName, long size) => new()
    {
        Id = id, ParentType = "Lead", ParentId = leadId, FileName = fileName,
        FilePath = "legacy-path-is-not-authoritative", MimeType = "text/plain", FileSize = size,
        ContentType = "text", CreatedOn = DateTime.UtcNow
    };

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static LeadIngestionBatch Batch(Guid id, long tenantId, DateTimeOffset now) => new()
    {
        Id = id,
        BusinessUnitId = tenantId,
        SourceChannel = "HttpIntegration",
        CreatedBy = "release-01b-tests",
        CreatedAtUtc = now,
        UpdatedAtUtc = now
    };

    private static LeadIngestionOccurrence Occurrence(
        Guid batchId,
        long tenantId,
        string key,
        LeadOccurrenceClassification classification,
        DateTimeOffset now) => new()
    {
        BusinessUnitId = tenantId,
        BatchId = batchId,
        SourceChannel = "HttpIntegration",
        IdempotencyKey = key,
        ExternalSourceId = key,
        OriginalFileName = key + ".xlsx",
        ContentHash = new string(tenantId == TenantA ? 'a' : 'b', 64),
        LogicalInquiryFingerprint = key,
        Classification = classification,
        Confidence = 1m,
        DecisionReasonsJson = "[\"release-01b-http-fixture\"]",
        ProcessingPath = LeadProcessingPath.Deterministic,
        SourceReceivedAtUtc = now.AddMinutes(-1),
        IngestedAtUtc = now,
        CreatedAtUtc = now,
        ActorType = "TestFixture",
        ActorId = "release-01b-tests",
        CorrelationId = key
    };

    private sealed class TestEvidenceStorage : IEvidenceObjectStorage
    {
        private readonly Dictionary<string, (string Hash, byte[] Bytes)> _objects = new(StringComparer.Ordinal);
        public bool IsDurable => true;
        public void Add(string uri, string hash, byte[] bytes) => _objects[uri] = (hash, bytes);
        public void Replace(string uri, byte[] bytes)
        {
            var current = _objects[uri];
            _objects[uri] = (current.Hash, bytes);
        }
        public Task ProbeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<EvidenceObject> WriteImmutableAsync(long businessUnitId, string zone, string sha256,
            string extension, ReadOnlyMemory<byte> content, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Stream> OpenVerifiedReadAsync(string storageUri, string expectedSha256, CancellationToken ct = default)
        {
            if (!_objects.TryGetValue(storageUri, out var stored))
                throw new FileNotFoundException();
            var actualHash = Sha256(stored.Bytes);
            if (!string.Equals(stored.Hash, expectedSha256, StringComparison.Ordinal)
                || !string.Equals(actualHash, expectedSha256, StringComparison.Ordinal))
                throw new InvalidDataException("Evidence hash mismatch.");
            return Task.FromResult<Stream>(new MemoryStream(stored.Bytes, writable: false));
        }
    }
}
