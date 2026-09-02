using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ERP_RFQ_Automation.Agent.Models;
using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.CommercialIntelligence.Sales;
using ERP_RFQ_Automation.CommercialCases.Participation;
using ERP_RFQ_Automation.CommercialCases.Promotion;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.DocumentIntelligence.Persistence;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.SupplierQuotes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
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
    public const long SupplierHistoryViewerRole = 82_003;
    public const long GrowthManagerRole = 82_004;
    public const long GrowthManagerUser = 83_002;
    public const long GrowthRepUser = 83_001;
    public const long TenantACustomerId = 86_001;
    public const long TenantBCustomerId = 86_002;
    public const long TenantAContactId = 86_101;
    public const long TenantBContactId = 86_102;
    public const long TenantASupplierContactId = 596_301;
    public const long TenantALeadId = 87_001;
    public const long TenantBLeadId = 87_002;
    public const long TenantAAttachmentId = 88_001;
    public const long TenantBAttachmentId = 88_002;
    public const long TenantAMatchOccurrenceId = 89_001;
    public const long TenantAProcurementCurrencyId = 396_010;
    public const long TenantAProcurementWarehouseId = 396_020;
    public const long TenantAProcurementProductId = 396_030;
    public const long TenantAProcurementSupplierId = 396_050;
    public const long TenantBProcurementSupplierId = 406_050;
    public const long TenantAProcurementRfqId = 396_060;
    public const long TenantAProcurementRfqItemId = 396_070;
    public const long TenantBProcurementRfqId = 406_060;
    public const long TenantASupplierQuoteId = 596_100;
    public const long TenantBSupplierQuoteId = 596_200;

    private const long TenantAProcurementOffset = 300_000;
    private const long TenantBProcurementOffset = 310_000;

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
        var runtimeConnection = new NpgsqlConnectionStringBuilder(_postgres.GetConnectionString())
        {
            Options = "-c role=nexora_tenant_app"
        }.ConnectionString;
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", runtimeConnection);
        builder.UseSetting("ConnectionStrings:MigrationConnection", _postgres.GetConnectionString());
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Jwt:Issuer", Issuer);
        builder.UseSetting("Jwt:Audience", Audience);
        builder.UseSetting("Jwt:PlatformKey", JwtKey);
        builder.UseSetting("CommercialFinance:ContactVerificationSecret", TestSecret);
        builder.UseSetting("CommercialFinance:DunningProviderWebhookSecret", TestSecret);
        builder.UseSetting("CommercialFinance:AuditActorSecret", TestSecret);
        // This authenticated HTTP fixture is not an observability test and deliberately has no
        // scraper. Non-Development hosts must make that posture explicit after the production
        // anonymous-metrics fail-closed boundary was introduced.
        builder.UseSetting("Observability:Prometheus:Enabled", "false");
        // The environment here is "Testing", not "Development", so the API fails closed
        // without a mailbox-credential protection key — exactly as a real deploy would.
        builder.UseSetting(
            ERP_RFQ_Automation.Security.SecretProtection.KeyConfigurationPath,
            Convert.ToBase64String(TestAssemblyInitialization.TestSecretProtectionKey));
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IHostedService>();
            services.RemoveAll<IEvidenceObjectStorage>();
            services.AddSingleton<IEvidenceObjectStorage>(_evidenceStorage);
        });
    }

    /// <param name="securityStamp">
    /// When null the token is shaped like one minted BEFORE the SecurityStamp release — no
    /// <c>sst</c> claim — which the validator accepts until <c>Auth:RequireSecurityStamp</c> is
    /// turned on. Pass the account's current stamp to mint a token the validator re-checks.
    /// </param>
    public string Token(long roleId, long? tenantId, long userId = 83_001, string? securityStamp = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, userId.ToString()),
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new("roleId", roleId.ToString())
        };
        if (tenantId.HasValue)
            claims.Add(new Claim("businessUnitId", tenantId.Value.ToString()));
        if (securityStamp is not null)
            claims.Add(new Claim(ERP_RFQ_Automation.Security.SecurityStamps.ClaimType, securityStamp));

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

    // ---- SecurityStamp (docs/design/token-revocation.md) -------------------------------------

    /// <summary>A throwaway active user in <paramref name="tenantId"/>, returned with the stamp
    /// the entity initialiser gave it — the same way every production row got one from the
    /// migration's per-row default.</summary>
    public async Task<(long Id, string Stamp)> CreateUserAsync(long tenantId, long roleId, string email)
    {
        await using var db = Context();
        var user = User(0, tenantId, roleId, "Revocation", email);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (user.Id, user.SecurityStamp);
    }

    /// <summary>Writes the account the way ANOTHER instance's deactivate would: straight to the
    /// row, with no eviction in this process's cache.</summary>
    public async Task DeactivateUserDirectlyAsync(long userId)
    {
        await using var db = Context();
        await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.IsActive, (bool?)false)
                .SetProperty(u => u.SecurityStamp, ERP_RFQ_Automation.Security.SecurityStamps.NewStamp()));
    }

    public async Task RemoveUserAsync(long userId)
    {
        await using var db = Context();
        await db.Users.IgnoreQueryFilters().Where(u => u.Id == userId).ExecuteDeleteAsync();
    }

    /// <summary>Same-process eviction, through the same interface the rotation sites use.</summary>
    public void EvictTenantSession(long userId)
    {
        using var scope = Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<ERP_RFQ_Automation.Security.ITenantSessionCache>().Evict(userId);
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

    public async Task MarkSolicitationSentAsync(long solicitationId)
    {
        await using var db = Context();
        var now = DateTime.UtcNow;
        var solicitation = await db.Set<SupplierSolicitation>().SingleAsync(x =>
            x.BusinessUnitId == TenantA && x.Id == solicitationId);
        solicitation.Status = SolicitationStatus.Sent;
        solicitation.SentOn = now;
        solicitation.UpdatedOn = now;
        solicitation.Version++;
        var outbox = await db.ProcurementOutboxMessages.SingleAsync(x =>
            x.BusinessUnitId == TenantA && x.SupplierSolicitationId == solicitationId);
        outbox.Status = ProcurementOutboxStatuses.Sent;
        outbox.ProviderReference = $"http-test-provider:{solicitationId}";
        outbox.SentOn = now;
        outbox.UpdatedOn = now;
        await db.SaveChangesAsync();
    }

    public async Task<long> CreateLegacyFixtureSolicitationAsync()
    {
        await using var db = Context();
        var service = new ProcurementApplicationService(db);
        var result = await service.CreateSolicitationAsync(new CreateSolicitationCommand(
            TenantA,
            TenantAProcurementRfqId,
            TenantAProcurementSupplierId,
            new[] { TenantAProcurementRfqItemId },
            DateTime.UtcNow.AddDays(2),
            $"http-fixture-solicitation:{Guid.NewGuid():N}",
            "release-01b-tests",
            $"http-fixture:{Guid.NewGuid():N}"));
        await MarkSolicitationSentAsync(result.Id);
        return result.Id;
    }

    public async Task<(long LineId, long Version)> IssuePurchaseOrderForHttpFlowAsync(long purchaseOrderId)
    {
        await using var db = Context();
        var service = new ProcurementApplicationService(db);
        var deliveredOn = DateTime.UtcNow;
        // FR-SPO-01. Release is downstream of approval, so the HTTP support fixture approves first.
        var approval = await service.ApprovePurchaseOrderAsync(new ApprovePurchaseOrderCommand(
            TenantA,
            purchaseOrderId,
            1,
            77,
            $"http-approve-support-{purchaseOrderId}",
            "release-01b-http-tests",
            $"http-approve-support-{purchaseOrderId}"));
        var result = await service.IssuePurchaseOrderAsync(new IssuePurchaseOrderCommand(
            TenantA,
            purchaseOrderId,
            approval.Version,
            $"provider-receipt:http-test-{purchaseOrderId}",
            $"http-issue-support-{purchaseOrderId}",
            "release-01b-http-tests",
            $"http-issue-support-{purchaseOrderId}",
            new string('a', 64),
            deliveredOn));
        var lineId = await db.SupplierPurchaseOrderLines
            .Where(x => x.BusinessUnitId == TenantA && x.SupplierPurchaseOrderId == purchaseOrderId)
            .Select(x => x.Id)
            .SingleAsync();
        return (lineId, await db.SupplierPurchaseOrders
            .Where(x => x.BusinessUnitId == TenantA && x.Id == result.Id)
            .Select(x => x.Version)
            .SingleAsync());
    }

    public async Task<(long LineId, long Version)> PurchaseOrderReceiptStateAsync(long purchaseOrderId)
    {
        await using var db = Context();
        var lineId = await db.SupplierPurchaseOrderLines
            .Where(x => x.BusinessUnitId == TenantA && x.SupplierPurchaseOrderId == purchaseOrderId)
            .Select(x => x.Id)
            .SingleAsync();
        var version = await db.SupplierPurchaseOrders
            .Where(x => x.BusinessUnitId == TenantA && x.Id == purchaseOrderId)
            .Select(x => x.Version)
            .SingleAsync();
        return (lineId, version);
    }

    public async Task<(long SolicitationTenantId, long PurchaseOrderTenantId, long ReceiptTenantId)>
        ProcurementOwnershipAsync(long solicitationId, long purchaseOrderId, long receiptId)
    {
        await using var db = Context();
        return (
            await db.Set<SupplierSolicitation>().IgnoreQueryFilters()
                .Where(x => x.Id == solicitationId).Select(x => x.BusinessUnitId).SingleAsync(),
            await db.SupplierPurchaseOrders.IgnoreQueryFilters()
                .Where(x => x.Id == purchaseOrderId).Select(x => x.BusinessUnitId).SingleAsync(),
            await db.GoodsReceipts.IgnoreQueryFilters()
                .Where(x => x.Id == receiptId).Select(x => x.BusinessUnitId).SingleAsync());
    }

    private ErpRfqAutomationContext Context() => new(
        new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options);

    private async Task SeedAsync(ErpRfqAutomationContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var enabledFeatures = JsonSerializer.Serialize(TypedEntitlementCatalog.Keys
            .ToDictionary(key => key, TypedEntitlementCatalog.IsRuntimeAvailable));
        var plan = new Plan
        {
            Id = 89_900,
            Code = "release-01b-http",
            Name = "Release 01B HTTP",
            Features = enabledFeatures,
            IsActive = true,
            Weight = 1,
            MaxConcurrentExtractionJobs = 4,
            MaxDocsPerMonth = 10_000,
            MaxSeats = 100,
            CreatedOn = now.UtcDateTime
        };
        db.Set<Plan>().Add(plan);
        db.Set<Tenant>().AddRange(
            // Entitlements set on each TENANT, not inherited from the shared plan: since
            // 20260818013530 the plan is a provisioning template and the tenant's own grant is what
            // [RequiresEntitlement] reads. Seeding only the plan is exactly the mistake the
            // migration's backfill exists to prevent in production.
            new Tenant
            {
                Id = 89_901, Name = "HTTP Tenant A", Slug = "http-tenant-a",
                Status = TenantStatus.Active, Plan = plan, PrimaryBusinessUnitId = TenantA,
                Entitlements = enabledFeatures,
                CreatedOn = now.UtcDateTime, CreatedBy = "release-01b-tests"
            },
            new Tenant
            {
                Id = 89_902, Name = "HTTP Tenant B", Slug = "http-tenant-b",
                Status = TenantStatus.Active, Plan = plan, PrimaryBusinessUnitId = TenantB,
                Entitlements = enabledFeatures,
                CreatedOn = now.UtcDateTime, CreatedBy = "release-01b-tests"
            });
        db.BusinessUnits.AddRange(
            BusinessUnit(TenantA, "HTTP-A"),
            BusinessUnit(TenantB, "HTTP-B"));

        const long leadsModuleId = 84_001;
        const long dashboardModuleId = 84_002;
        const long customersModuleId = 84_003;
        const long rfqManagementModuleId = 84_004;
        const long supplierHistoryModuleId = 84_005;
        const long ordersModuleId = 84_006;
        const long productsModuleId = 84_007;
        const long usersModuleId = 84_008;
        const long quotationsModuleId = 84_009;
        const long suppliersModuleId = 84_010;
        var supplierNegotiationModuleId = await db.Modules.Where(x =>
            x.ModuleName == "Supplier Negotiation").Select(x => x.Id).SingleAsync();
        db.Modules.AddRange(
            Module(leadsModuleId, "Leads"),
            Module(dashboardModuleId, "Dashboard"),
            Module(customersModuleId, "Customers"),
            Module(rfqManagementModuleId, "RFQ Management"),
            Module(supplierHistoryModuleId, "Supplier History"),
            Module(ordersModuleId, "Orders"),
            Module(productsModuleId, "Products"),
            Module(usersModuleId, "Users"),
            Module(quotationsModuleId, "Quotations"),
            Module(suppliersModuleId, "Suppliers"));

        db.SetupMasters.AddRange(
            Role(AllowedRole, TenantA, "Release 01B Reader"),
            Role(DeniedRole, TenantA, "Release 01B Denied"),
            Role(SupplierHistoryViewerRole, TenantA, "Supplier History Viewer"),
            Role(GrowthManagerRole, TenantA, "Commercial Manager", RoleRanks.Manager));

        db.RolePermissions.AddRange(
            Permission(85_001, AllowedRole, leadsModuleId, TenantA, canCreate: true, canEdit: true),
            Permission(85_002, AllowedRole, dashboardModuleId, TenantA),
            Permission(85_003, AllowedRole, customersModuleId, TenantA,
                canCreate: true, canEdit: true, canDelete: true),
            Permission(85_004, AllowedRole, rfqManagementModuleId, TenantA, canCreate: true, canEdit: true),
            Permission(85_005, AllowedRole, supplierHistoryModuleId, TenantA, canCreate: true, canEdit: true),
            Permission(85_006, AllowedRole, ordersModuleId, TenantA, canCreate: true, canEdit: true),
            Permission(85_007, AllowedRole, productsModuleId, TenantA, canCreate: true, canEdit: true),
            Permission(85_008, AllowedRole, usersModuleId, TenantA),
            Permission(85_009, AllowedRole, supplierNegotiationModuleId, TenantA, canEdit: true),
            Permission(85_010, SupplierHistoryViewerRole, supplierHistoryModuleId, TenantA),
            Permission(85_011, AllowedRole, quotationsModuleId, TenantA),
            Permission(85_012, GrowthManagerRole, leadsModuleId, TenantA, canEdit: true),
            Permission(85_013, GrowthManagerRole, customersModuleId, TenantA),
            Permission(85_014, GrowthManagerRole, rfqManagementModuleId, TenantA),
            Permission(85_015, GrowthManagerRole, quotationsModuleId, TenantA),
            Permission(85_016, GrowthManagerRole, ordersModuleId, TenantA),
            Permission(85_017, GrowthManagerRole, supplierHistoryModuleId, TenantA));
        db.RolePermissions.AddRange(
            Permission(85_018, AllowedRole, suppliersModuleId, TenantA,
                canCreate: true, canEdit: true, canDelete: true),
            Permission(85_019, SupplierHistoryViewerRole, suppliersModuleId, TenantA));

        db.Users.AddRange(
            User(GrowthRepUser, TenantA, AllowedRole, "Rep", "growth-rep@nexora.invalid"),
            User(GrowthManagerUser, TenantA, GrowthManagerRole, "Manager", "growth-manager@nexora.invalid"));

        db.Set<ExtractionJob>().AddRange(
            new ExtractionJob
            {
                Id = 395_001,
                BatchId = TenantABatchId,
                BusinessUnitId = TenantA,
                SourceType = ExtractionSourceType.ManualUpload,
                ContentHash = new string('1', 64),
                StoragePath = "test-evidence://tenant-a/queued",
                Status = ExtractionStatus.Pending,
                NextAttemptAt = now.UtcDateTime,
                CreatedOn = now.UtcDateTime,
                UpdatedOn = now.UtcDateTime
            },
            new ExtractionJob
            {
                Id = 395_002,
                BatchId = TenantBBatchId,
                BusinessUnitId = TenantB,
                SourceType = ExtractionSourceType.ManualUpload,
                ContentHash = new string('2', 64),
                StoragePath = "test-evidence://tenant-b/dead-letter",
                Status = ExtractionStatus.DeadLetter,
                NextAttemptAt = now.UtcDateTime,
                CreatedOn = now.UtcDateTime,
                UpdatedOn = now.UtcDateTime
            });

        db.Customers.AddRange(
            Customer(TenantACustomerId, TenantA, "Tenant A Customer", "buyer-a@nexora.invalid"),
            Customer(TenantBCustomerId, TenantB, "Tenant B Customer", "buyer-b@nexora.invalid"));
        db.Set<CustomerOwnership>().Add(new CustomerOwnership
        {
            Id = 86_401,
            BusinessUnitId = TenantA,
            CustomerId = TenantACustomerId,
            PrimaryUserId = GrowthRepUser,
            Scope = OwnershipScope.GeneralCustomer,
            Priority = 100,
            EffectiveFrom = now.UtcDateTime.AddDays(-1),
            IsActive = true,
            Source = "release-01b-tests",
            Reason = "Existing account assigned to the authenticated sales representative.",
            Version = 1
        });
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
            // Lead ownership, not customer ownership, is the authority for a rep's opportunity
            // scope. This is the authenticated rep's RFQ fixture; leaving it unassigned would
            // correctly keep it in the governed routing queue and make direct evidence reads 404.
            Lead(TenantALeadId, TenantA, 86_301, TenantACustomerId, "HTTP-A-RFQ", GrowthRepUser),
            Lead(TenantBLeadId, TenantB, 86_302, TenantBCustomerId, "HTTP-B-RFQ"));
        await db.SaveChangesAsync();

        ProcurementTestData.SeedGraph(db, TenantA, TenantAProcurementOffset);
        ProcurementTestData.SeedGraph(db, TenantB, TenantBProcurementOffset);
        db.Contacts.Add(new Contact
        {
            Id = TenantASupplierContactId,
            BusinessUnitId = TenantA,
            SupplierId = TenantAProcurementSupplierId,
            FirstName = "Supplier",
            LastName = "Contact",
            Email = "supplier-contact@nexora.invalid",
            IsActive = true,
            CreatedBy = "release-01b-tests",
            CreatedOn = DateTime.UtcNow,
            ConcurrencyToken = Guid.NewGuid()
        });
        db.FollowUpTasks.Add(new FollowUpTask
        {
            Id = 596_300,
            BusinessUnitId = TenantA,
            AssignedToUserId = GrowthManagerUser,
            AggregateType = "Rfq",
            AggregateId = TenantAProcurementRfqId,
            CustomerId = TenantACustomerId,
            DueAtUtc = now.UtcDateTime.AddDays(-2),
            Status = FollowUpStatus.Open,
            Priority = 1,
            PurposeCode = "CUSTOMER_DECISION",
            CreatedAtUtc = now.UtcDateTime.AddDays(-5),
            UpdatedAtUtc = now.UtcDateTime.AddDays(-5),
            Version = 1,
            CreatedBy = "release-01b-tests",
            CorrelationId = "growth-http-fixture",
            CreationIdempotencyKey = "growth-http-fixture"
        });

        db.Set<LeadIngestionBatch>().AddRange(
            Batch(TenantABatchId, TenantA, now),
            Batch(TenantBBatchId, TenantB, now));
        var tenantAOccurrence = Occurrence(TenantABatchId, TenantA, "tenant-a-new", LeadOccurrenceClassification.New, now);
        tenantAOccurrence.LeadId = TenantALeadId;
        tenantAOccurrence.OriginalFileName = "tenant-a.txt";
        var tenantBOccurrence = Occurrence(TenantBBatchId, TenantB, "tenant-b-new", LeadOccurrenceClassification.New, now);
        tenantBOccurrence.LeadId = TenantBLeadId;
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
            tenantBOccurrence,
            Occurrence(TenantBBatchId, TenantB, "tenant-b-duplicate", LeadOccurrenceClassification.ExactDuplicate, now));
        await db.SaveChangesAsync();

        await SeedPromotedRfqLineageAsync(db, TenantA, TenantALeadId, tenantAOccurrence.Id,
            TenantAProcurementRfqId, TenantAProcurementOffset, 597_100, now);
        await SeedPromotedRfqLineageAsync(db, TenantB, TenantBLeadId, tenantBOccurrence.Id,
            TenantBProcurementRfqId, TenantBProcurementOffset, 597_200, now);
        SeedSupplierNegotiationQuote(db, TenantA, TenantAProcurementOffset,
            TenantASupplierQuoteId, 596_110, 596_120, 596_130, 596_140, "A");
        SeedSupplierNegotiationQuote(db, TenantB, TenantBProcurementOffset,
            TenantBSupplierQuoteId, 596_210, 596_220, 596_230, 596_240, "B");

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
        sourceA.RecordMalwareVerdict(MalwareScanStatus.Clean, "http-test-scanner", "daily-test", now);
        sourceB.RecordMalwareVerdict(MalwareScanStatus.Clean, "http-test-scanner", "daily-test", now);
        sourceA.MarkSecurityStatus(DocumentSecurityStatus.Cleared, now);
        sourceB.MarkSecurityStatus(DocumentSecurityStatus.Cleared, now);
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

        var duplicateA = SourceDocumentOccurrence.Create(TenantA, sourceA.Id, corpusA.Id,
            "http:tenant-a:duplicate", "{\"metadata\":{\"UploadedBy\":\"tenant-a-uploader@nexora.invalid\"}}",
            receivedOn: now.AddMinutes(1));
        var duplicateB = SourceDocumentOccurrence.Create(TenantB, sourceB.Id, corpusB.Id,
            "http:tenant-b:duplicate", "{\"metadata\":{\"UploadedBy\":\"tenant-b-uploader@nexora.invalid\"}}",
            receivedOn: now.AddMinutes(1));
        duplicateA.RecordUploadResources(tenantABytes.Length, 1, 0);
        duplicateB.RecordUploadResources(tenantBBytes.Length, 1, 0);
        duplicateA.MarkExactDuplicateCandidate(sourceOccurrenceA.Id, requiresRescan: false);
        duplicateB.MarkExactDuplicateCandidate(sourceOccurrenceB.Id, requiresRescan: false);
        duplicateA.RecordSecurityWork(reused: true, rerun: false);
        duplicateB.RecordSecurityWork(reused: true, rerun: false);
        db.Set<SourceDocumentOccurrence>().AddRange(duplicateA, duplicateB);
        await db.SaveChangesAsync();
        duplicateA.ResolveByProcessingReuse(jobA.Id);
        duplicateB.ResolveByProcessingReuse(jobB.Id);
        duplicateA.ConfirmExactDuplicate(processingReused: true);
        duplicateB.ConfirmExactDuplicate(processingReused: true);

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

    private static void SeedSupplierNegotiationQuote(ErpRfqAutomationContext db, long tenantId,
        long procurementOffset, long quoteId, long demandId, long caseId, long solicitationId,
        long revisionId, string suffix)
    {
        var now = DateTime.UtcNow;
        var rfqId = ProcurementTestData.Rfq + procurementOffset;
        var rfqItemId = ProcurementTestData.RfqItem + procurementOffset;
        var nexoraSerial = db.Rfqs.Local.Single(x => x.Id == rfqId).NexoraSerial
            ?? throw new InvalidOperationException("The fixture RFQ must inherit a Nexora Serial before negotiation seeding.");
        var demand = new CommercialDemandLine
        {
            Id = demandId, BusinessUnitId = tenantId,
            RfqId = rfqId, RfqItemId = rfqItemId, NexoraSerial = nexoraSerial,
            IdentityKey = $"rfq:{rfqId}:line:{rfqItemId}",
            CreatedBy = "release-01b-tests", CreatedOn = now
        };
        var sourcingCase = new SourcingCase
        {
            Id = caseId, BusinessUnitId = tenantId, CommercialDemandLineId = demand.Id,
            RfqId = demand.RfqId, RfqItemId = demand.RfqItemId,
            ProductId = ProcurementTestData.Product + procurementOffset,
            NexoraSerial = demand.NexoraSerial, Description = "HTTP negotiation component",
            RequestedQuantity = 10, StockQuantity = 0, UnfulfilledQuantity = 10,
            SearchLimit = 10, Status = SourcingCaseStatuses.ComparisonReady,
            NextAction = "Review offers", ShortageDecisionKey = $"http-neg-shortage-{suffix}",
            IdempotencyKey = $"http-neg-case-{suffix}", RequestHash = new string('A', 64),
            CreatedBy = "release-01b-tests", UpdatedBy = "release-01b-tests",
            CreatedOn = now, UpdatedOn = now
        };
        var solicitation = new SupplierSolicitation
        {
            Id = solicitationId, BusinessUnitId = tenantId, RfqId = demand.RfqId,
            SupplierId = ProcurementTestData.Supplier + procurementOffset,
            SourcingCaseId = sourcingCase.Id, CommercialDemandLineId = demand.Id,
            NexoraSerial = demand.NexoraSerial, SupplierRfqNumber = $"SRFQ-HTTP-NEG-{suffix}",
            IdempotencyKey = $"http-neg-sol-{suffix}", RequestHash = new string('B', 64),
            RequestedRfqItemIdsJson = $"[{demand.RfqItemId}]", Status = SolicitationStatus.Responded,
            SentOn = now.AddDays(-1), RespondedOn = now,
            CreatedOn = now.AddDays(-1), UpdatedOn = now
        };
        var quote = new SupplierQuote
        {
            Id = quoteId, BusinessUnitId = tenantId, SupplierId = solicitation.SupplierId,
            SupplierSolicitationId = solicitation.Id, SourcingCaseId = sourcingCase.Id,
            RfqId = demand.RfqId, NexoraSerial = demand.NexoraSerial,
            SupplierQuoteReference = $"SQ-HTTP-NEG-{suffix}", CurrentRevisionNumber = 1,
            InboxStatus = SupplierQuoteInboxStatuses.ReadyForComparison, Version = 1,
            CreatedBy = "release-01b-tests", UpdatedBy = "release-01b-tests",
            CreatedOn = now, UpdatedOn = now
        };
        var revision = new SupplierQuoteRevision
        {
            Id = revisionId, BusinessUnitId = tenantId, SupplierQuoteId = quote.Id,
            RevisionNumber = 1, CaptureChannel = SupplierQuoteCaptureChannels.Manual,
            SourceIdentity = $"http-neg-source-{suffix}", SourceSha256 = new string('C', 64),
            CurrencyId = ProcurementTestData.Currency + procurementOffset,
            ValidUntil = now.AddDays(30), Incoterms = "FCA", FreightAmount = 10m,
            PaymentTerms = "NET 30", IdempotencyKey = $"http-neg-revision-{suffix}",
            RequestHash = new string('D', 64), CapturedOn = now,
            CapturedBy = "release-01b-tests", CorrelationId = $"http-neg-{suffix}",
            SupplierQuote = quote
        };
        revision.Lines.Add(new SupplierQuoteLine
        {
            Id = revisionId + 1, BusinessUnitId = tenantId,
            SupplierQuoteRevisionId = revision.Id, LineNumber = 1,
            RfqItemId = demand.RfqItemId, CommercialDemandLineId = demand.Id,
            PartNumber = $"HTTP-PART-{suffix}", Description = "HTTP negotiation component",
            Quantity = 10, AvailableQuantity = 10, UnitOfMeasure = "EA", UnitPrice = 10,
            LeadTimeDays = 5, AvailabilityType = "IN_STOCK"
        });
        quote.Revisions.Add(revision);
        db.CommercialDemandLines.Add(demand);
        db.SourcingCases.Add(sourcingCase);
        db.Set<SupplierSolicitation>().Add(solicitation);
        db.SupplierQuotes.Add(quote);
    }

    private static async Task SeedPromotedRfqLineageAsync(
        ErpRfqAutomationContext db,
        long tenantId,
        long leadId,
        long occurrenceId,
        long rfqId,
        long procurementOffset,
        long idBase,
        DateTimeOffset now)
    {
        var rfq = db.Rfqs.Local.Single(x => x.Id == rfqId);
        var lead = db.Leads.Local.Single(x => x.Id == leadId);
        var leadItem = new LeadItem
        {
            Id = idBase,
            LeadId = leadId,
            ItemMaterialCode = $"HTTP-PART-{tenantId}",
            ProductShortDescription = "HTTP promoted fixture line",
            Quantity = 10,
            UnitOfMeasure = "EA",
            Currency = $"Q{procurementOffset}",
            IsCurrentRevisionProjection = true
        };
        db.LeadItems.Add(leadItem);
        await db.SaveChangesAsync();

        var revision = new LeadRevision
        {
            Id = idBase + 1,
            BusinessUnitId = tenantId,
            LeadId = leadId,
            RevisionNumber = 1,
            EstablishedByOccurrenceId = occurrenceId,
            LogicalInquiryFingerprint = new string(tenantId == TenantA ? 'a' : 'b', 64),
            SnapshotJson = "{}",
            CustomerIdSnapshot = lead.CustomerId,
            CreatedAtUtc = now,
            CreatedBy = "release-01b-tests",
            ProcessingPath = LeadProcessingPath.Deterministic
        };
        var revisionLine = new LeadItemRevision
        {
            Id = idBase + 2,
            BusinessUnitId = tenantId,
            LeadId = leadId,
            LeadRevisionId = revision.Id,
            LeadItemId = leadItem.Id,
            LineNumber = 1,
            LineFingerprint = new string(tenantId == TenantA ? 'c' : 'd', 64),
            SnapshotJson = "{}"
        };
        revision.Items.Add(revisionLine);
        db.Set<LeadRevision>().Add(revision);
        await db.SaveChangesAsync();

        lead.CurrentRevisionId = revision.Id;
        lead.CurrentRevisionNumber = revision.RevisionNumber;
        var fit = new LeadFitAssessment
        {
            Id = idBase + 3,
            BusinessUnitId = tenantId,
            LeadId = leadId,
            LeadRevisionId = revision.Id,
            Sequence = 1,
            PolicyVersion = "release-01b-http-fixture/v1",
            Recommendation = "FIT",
            IsActionable = true,
            AssessmentJson = "{}",
            IdempotencyKey = $"release-01b-fit:{tenantId}:{leadId}",
            RequestHash = new string('e', 64),
            AssessedBy = "release-01b-tests",
            AssessedAtUtc = now
        };
        db.Set<LeadFitAssessment>().Add(fit);
        await db.SaveChangesAsync();

        var decision = new LeadParticipationDecision
        {
            Id = idBase + 4,
            BusinessUnitId = tenantId,
            LeadId = leadId,
            LeadRevisionId = revision.Id,
            FitAssessmentId = fit.Id,
            Sequence = 1,
            IsCommitted = true,
            Outcome = LeadParticipationOutcome.FullBid,
            Notes = "Representative committed participation fixture.",
            IdempotencyKey = $"release-01b-participation:{tenantId}:{leadId}",
            RequestHash = new string('f', 64),
            DecidedBy = "release-01b-tests",
            DecidedAtUtc = now
        };
        decision.Lines.Add(new LeadLineParticipationDecision
        {
            Id = idBase + 5,
            BusinessUnitId = tenantId,
            LeadId = leadId,
            LeadRevisionId = revision.Id,
            ParticipationDecisionId = decision.Id,
            LeadItemRevisionId = revisionLine.Id,
            Choice = LeadLineParticipationChoice.Bid,
            ReasonNotes = "Approved by the representative HTTP fixture.",
            ProductId = ProcurementTestData.Product + procurementOffset,
            Quantity = 10,
            UnitOfMeasure = "EA",
            UomId = checked((int)(ProcurementTestData.Uom + procurementOffset)),
            Currency = $"Q{procurementOffset}",
            CurrencyId = ProcurementTestData.Currency + procurementOffset,
            CatalogPolicyVersion = "release-01b-http-fixture/v1",
            WarningSnapshotJson = "{}"
        });
        db.Set<LeadParticipationDecision>().Add(decision);
        await db.SaveChangesAsync();

        var promotion = new RfqPromotion
        {
            Id = idBase + 6,
            BusinessUnitId = tenantId,
            LeadId = leadId,
            LeadRevisionId = revision.Id,
            ParticipationDecisionId = decision.Id,
            IdempotencyKey = $"release-01b-promotion:{tenantId}:{leadId}",
            RequestHash = new string('1', 64),
            PromotedBy = "release-01b-tests",
            PromotedAtUtc = now
        };
        db.Set<RfqPromotion>().Add(promotion);
        await db.SaveChangesAsync();

        db.Entry(rfq).Property(x => x.CommercialCaseId).CurrentValue = null;
        db.Entry(rfq).Property(x => x.NexoraSerial).CurrentValue = null;
        rfq.LeadId = leadId;
        rfq.PromotionId = promotion.Id;
        rfq.SourceLeadRevisionId = revision.Id;
        rfq.ParticipationDecisionId = decision.Id;
        rfq.CustomerId = lead.CustomerId;
        rfq.InheritCommercialIdentity(lead);
        var rfqItem = db.Rfqitems.Local.Single(x => x.Rfqid == rfqId);
        rfqItem.SourceBusinessUnitId = tenantId;
        rfqItem.SourceLeadId = leadId;
        rfqItem.SourceLeadRevisionId = revision.Id;
        rfqItem.SourceLeadItemRevisionId = revisionLine.Id;
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

    /// <summary>
    /// The ONE place every test in this collection seeds a role row.
    ///
    /// <paramref name="rank"/> is mandatory-by-intent: authority comes from the explicit
    /// <c>Setup_Master.RoleRank</c> column and NEVER from the role's name, so a role called
    /// "Commercial Manager" that is seeded without <see cref="RoleRanks.Manager"/> is — correctly —
    /// an ordinary member and is refused by <c>[RequireManagerRole]</c>. Seeding through this helper
    /// keeps the rank next to the name it is meant to describe instead of scattering it across
    /// call sites.
    /// </summary>
    public static SetupMaster Role(
        long id, long tenantId, string name, short rank = RoleRanks.Member, string createdBy = "release-01b-tests")
        => new()
        {
            SetupId = id,
            SetupType = "Role",
            SetupCode = name.Replace(' ', '_').ToUpperInvariant(),
            SetupValue = name,
            RoleRank = rank,
            BusinessUnitId = tenantId,
            IsActive = true,
            CreatedBy = createdBy,
            CreatedOn = DateTime.UtcNow
        };

    private static User User(long id, long tenantId, long roleId, string lastName, string email) => new()
    {
        Id = id,
        Buid = tenantId,
        RoleId = roleId,
        FirstName = "Growth",
        LastName = lastName,
        Email = email,
        PasswordHash = "not-used",
        ImageUrl = "n/a",
        IsActive = true,
        CreatedBy = "release-01b-tests",
        CreatedOn = DateTime.UtcNow
    };

    private static RolePermission Permission(long id, long roleId, long moduleId, long tenantId,
        bool canCreate = false, bool canEdit = false, bool canDelete = false) => new()
    {
        Id = id,
        RoleId = roleId,
        ModuleId = moduleId,
        BusinessUnitId = tenantId,
        CanCreate = canCreate,
        CanEdit = canEdit,
        CanDelete = canDelete,
        CreatedBy = "release-01b-tests",
        CreatedOn = DateTime.UtcNow
    };

    private static Customer Customer(long id, long tenantId, string name, string email) => new()
    {
        Id = id, Buid = tenantId, Name = name, ContactEmail = email, ImageUrl = string.Empty,
        IsActive = true, CreatedBy = "release-01b-tests", CreatedOn = DateTime.UtcNow,
        ConcurrencyToken = Guid.NewGuid()
    };

    private static Contact Contact(long id, long tenantId, long customerId, string email) => new()
    {
        Id = id, BusinessUnitId = tenantId, CustomerId = customerId, FirstName = "HTTP",
        LastName = "Contact", Email = email, IsActive = true, CreatedBy = "release-01b-tests",
        CreatedOn = DateTime.UtcNow, ConcurrencyToken = Guid.NewGuid()
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

    private static Lead Lead(
        long id, long tenantId, long ingestId, long customerId, string rfq, long? assignedTo = null)
    {
        var lead = new Lead
        {
            Id = id, BusinessUnitId = tenantId, EmailIngestsId = ingestId,
            Rfqno = rfq, RecDate = DateTime.UtcNow, LeadSource = "HttpIntegration",
            AssignTo = assignedTo,
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
