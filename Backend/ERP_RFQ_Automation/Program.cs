using ERP_RFQ_Automation.Interfaces;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Repositories;
using ERP_RFQ_Automation.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.OpenApi.Models;
using System.Reflection;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Services.DocumentIntelligence;
using ERP_RFQ_Automation.Authorization;
using Microsoft.AspNetCore.Authorization;
using OfficeOpenXml;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.LeadIdentity;
using ERP_RFQ_Automation.Email;
using ERP_RFQ_Automation.Mailbox;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Hardening;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Provisioning;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Platform.Notifications;
using ERP_RFQ_Automation.Agent;
using ERP_RFQ_Automation.Intelligence.Conversion;
using ERP_RFQ_Automation.Intelligence.Pricing;
using ERP_RFQ_Automation.Intelligence.Decision;
using ERP_RFQ_Automation.Boq;
using ERP_RFQ_Automation.Infrastructure;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.CommercialCases;
using ERP_RFQ_Automation.CommercialCases.Lifecycle;
using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.CommercialFinance;
using ERP_RFQ_Automation.OrderToCash;
using ERP_RFQ_Automation.CustomFields;
using ERP_RFQ_Automation.AI;
using ERP_RFQ_Automation.QuoteDelivery;
using ERP_RFQ_Automation.Security;
using ERP_RFQ_Automation.Security.DocumentInspection;
using Microsoft.AspNetCore.HttpOverrides;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.SupplierGovernance;
using ERP_RFQ_Automation.SupplierQuotes;
using System.Text.Json.Serialization;
using Npgsql;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Billing;

// PostgreSQL migration: restore pre-6.0 Npgsql timestamp semantics so the
// existing DateTime usage (DateTime.Now / Unspecified-kind values inherited from
// the SQL Server codebase) maps to `timestamp without time zone` and is accepted
// regardless of DateTimeKind. Must run before any Npgsql data source is built.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Fail fast on missing / placeholder critical configuration so a misconfigured
// deploy stops at startup instead of silently using placeholders or an empty
// signing key. (DATA-07, SEC-12)
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connectionString) || connectionString.Contains("__DB_"))
    throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection is missing or still contains placeholders. " +
        "Provide it via appsettings.Development.json, user-secrets, or environment variables.");

var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey) || jwtKey.Contains("__JWT_") || Encoding.UTF8.GetByteCount(jwtKey) < 32)
    throw new InvalidOperationException(
        "Jwt:Key is missing, a placeholder, or shorter than 256 bits (32 bytes). " +
        "Provide a strong signing key via secure configuration.");

var contactVerificationSecret = builder.Configuration["CommercialFinance:ContactVerificationSecret"];
var dunningProviderSecret = builder.Configuration["CommercialFinance:DunningProviderWebhookSecret"];
var auditActorSecret = builder.Configuration["CommercialFinance:AuditActorSecret"];
if (string.IsNullOrWhiteSpace(contactVerificationSecret) || Encoding.UTF8.GetByteCount(contactVerificationSecret) < 32 ||
    string.IsNullOrWhiteSpace(dunningProviderSecret) || Encoding.UTF8.GetByteCount(dunningProviderSecret) < 32 ||
    string.IsNullOrWhiteSpace(auditActorSecret) || Encoding.UTF8.GetByteCount(auditActorSecret) < 32)
    throw new InvalidOperationException(
        "Commercial finance provider secrets are required and must each contain at least 32 bytes.");

// Security:SecretProtectionKey encrypts stored CUSTOMER MAILBOX credentials at rest
// (Email_Configurations.Password, AES-256-GCM). Same fail-closed contract as Jwt:Key above:
// outside Development a missing/short/placeholder key stops the deploy rather than booting
// an API that writes corporate Exchange/O365 passwords in cleartext. Development falls back
// to an ephemeral process-lifetime key — logged loudly below — so the demo needs no setup.
var secretProtector = SecretProtection.CreateFromConfiguration(
    builder.Configuration, builder.Environment.IsDevelopment(), out var secretProtectionIsEphemeral);
SecretProtection.Use(secretProtector, secretProtectionIsEphemeral);
builder.Services.AddSingleton(secretProtector);

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
        // Tolerate numeric fields arriving as JSON strings (e.g. grid-edited qty /
        // unit price / lead time from the review workbench) so a corrected line
        // item can't 400 on submit.
        options.JsonSerializerOptions.NumberHandling = JsonNumberHandling.AllowReadingFromString;
    });

// Keep the host alive if a BackgroundService throws — a transient failure in the
// email poller must not tear down the whole API. (DATA-01)
builder.Services.Configure<HostOptions>(options =>
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore);

// Add DbContext with PostgreSQL / Npgsql (transient-fault resilience — DATA-04).
// Provider is chosen here; the connection string stays in configuration
// (ConnectionStrings:DefaultConnection) so pointing at Neon later is config-only.
builder.Services.AddScoped<TenantRlsCommandInterceptor>();
builder.Services.AddDbContext<ErpRfqAutomationContext>((services, options) =>
{
    options.UseNpgsql(connectionString, npgsql =>
    {
        npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null);
        npgsql.CommandTimeout(60);
    });
    options.AddInterceptors(services.GetRequiredService<TenantRlsCommandInterceptor>());
});

// Per-request tenant scope for EF global query filters (ADR-0005 tenant isolation).
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
builder.Services.AddScoped<ERP_RFQ_Automation.MultiTenancy.ITenantContext, ERP_RFQ_Automation.MultiTenancy.HttpTenantContext>();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
builder.Services.Configure<S3EvidenceStorageOptions>(
    builder.Configuration.GetSection(S3EvidenceStorageOptions.SectionName));
builder.Services.Configure<MalwareVerdictPolicyOptions>(
    builder.Configuration.GetSection(MalwareVerdictPolicyOptions.SectionName));
builder.Services.AddSingleton<IEvidenceObjectStorage>(services =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<S3EvidenceStorageOptions>>();
    return string.Equals(options.Value.Provider, "S3", StringComparison.OrdinalIgnoreCase)
        ? new S3EvidenceObjectStorage(options)
        : new LocalEvidenceObjectStorage(services.GetRequiredService<IFileStorage>());
});
// Malware scanner provider is chosen EXPLICITLY by configuration
// (DocumentInspection:Scanner:Provider = ClamAV | BuiltIn), never implicitly by environment.
// Absent configuration stays fail-closed: ClamAV everywhere except a Development host.
// The decision is logged immediately, and MalwareScannerStartupProbe probes the endpoint once at
// boot so an unreachable scanner is never discovered by a tenant instead of by an operator.
builder.Services.Configure<MalwareScannerOptions>(
    builder.Configuration.GetSection(MalwareScannerOptions.SectionName));
var malwareScannerSelection = MalwareScannerFactory.Select(
    builder.Configuration, builder.Environment.IsDevelopment());
using (var malwareScannerStartupLoggerFactory = LoggerFactory.Create(logging =>
{
    logging.AddConsole();
    logging.SetMinimumLevel(LogLevel.Information);
}))
{
    MalwareScannerFactory.LogSelection(
        malwareScannerStartupLoggerFactory.CreateLogger("ERP_RFQ_Automation.Security.MalwareScanner"),
        malwareScannerSelection,
        builder.Environment.EnvironmentName);
}
builder.Services.AddSingleton(malwareScannerSelection);
builder.Services.AddSingleton<IMalwareScanner>(_ =>
    MalwareScannerFactory.Create(malwareScannerSelection, builder.Configuration));
builder.Services.AddHostedService<MalwareScannerStartupProbe>();
// FR-RFQ-02: OCR had never executed on a production job and nothing anywhere verified that the
// native Tesseract/pdfium libraries bind in the deployed image — the first scanned tender in
// front of a client would also have been the first test of them. This probe settles it at boot
// and reports on /ready (Degraded, not Unhealthy: text documents are unaffected).
builder.Services.AddSingleton<IOcrEngineHealth, OcrEngineHealth>();
builder.Services.AddHostedService<OcrEngineStartupProbe>();
builder.Services.AddSingleton<IFileInspectionService>(services =>
{
    var options = builder.Configuration.GetSection("DocumentInspection:Limits")
        .Get<DocumentInspectionOptions>() ?? new DocumentInspectionOptions();
    return new DocumentFileInspectionService(services.GetRequiredService<IMalwareScanner>(), options);
});
builder.Services.AddScoped<ExtractionDeadLetterService>();

// Platform-Owner control-plane services (ADR-0005)
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Auth.IPlatformAuthService, ERP_RFQ_Automation.Platform.Auth.PlatformAuthService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Services.IPlatformAuditService, ERP_RFQ_Automation.Platform.Services.PlatformAuditService>();
// Scoped because it seeds through the SAME request-scoped DbContext and transaction as the
// provisioning that calls it — that shared instance is what makes the baseline commit or roll
// back with the tenant it belongs to, rather than surviving a failed provision as orphan rows.
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Services.ITenantBaselineSeeder, ERP_RFQ_Automation.Platform.Services.TenantBaselineSeeder>();
// Operator support desk (Platform/Support). The controllers need no registration; this is the
// tenant-purge erasure hook. Scoped because it writes through the SAME request-scoped DbContext as
// the purge that calls it — that shared instance is what lets the erasure commit or roll back with
// the purge, rather than emptying a customer's tickets beside a purge that then failed.
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Support.ISupportTicketRedactionService,
    ERP_RFQ_Automation.Platform.Support.SupportTicketRedactionService>();
// Tenant offboarding (Platform/Lifecycle): export, scheduled deletion with a retention clock,
// purge, and the separate personal-data erasure. Registered next to provisioning because it is
// the other end of the same lifecycle — creation is one line here and so is everything after it.
builder.Services.AddTenantLifecycle(builder.Configuration);

// Readiness/liveness health checks (DATA-05)
builder.Services.AddSingleton<ERP_RFQ_Automation.HealthChecks.IExtractionWorkerHeartbeat,
    ERP_RFQ_Automation.HealthChecks.ExtractionWorkerHeartbeat>();
builder.Services.AddSingleton<IProcurementDispatchHeartbeat, ProcurementDispatchHeartbeat>();
builder.Services.AddSingleton<ERP_RFQ_Automation.HealthChecks.IQuoteDeliveryWorkerHeartbeat,
    ERP_RFQ_Automation.HealthChecks.QuoteDeliveryWorkerHeartbeat>();
// Liveness for the workers that previously had NO heartbeat at all: routing
// reconciliation, the SLA sweep, the email poller and AI reservation reconciliation.
// Each registers itself in its constructor and beats once per loop iteration; the
// health check below names any that stop.
builder.Services.AddSingleton<ERP_RFQ_Automation.HealthChecks.IBackgroundWorkerHeartbeats,
    ERP_RFQ_Automation.HealthChecks.BackgroundWorkerHeartbeats>();
// ING-08: mailbox CHANNEL health, distinct from poll-loop liveness above. The loop can be
// perfectly alive while the mailbox refuses every authentication attempt — which is exactly
// what happened from 2026-07-30 to 2026-08-06 with every surface green.
builder.Services.AddSingleton<ERP_RFQ_Automation.HealthChecks.IEmailPollerHealth,
    ERP_RFQ_Automation.HealthChecks.EmailPollerHealth>();
builder.Services.AddHealthChecks()
    .AddCheck<ERP_RFQ_Automation.HealthChecks.DatabaseHealthCheck>("database", tags: new[] { "live", "ready" })
    .AddCheck<ERP_RFQ_Automation.HealthChecks.EvidenceStorageHealthCheck>("evidence-storage", tags: new[] { "ready" })
    // "ready" ONLY. A full disk must drain traffic, never trigger a restart: the replacement
    // process lands on the same full volume, fails LocalFileStorage's constructor write
    // probe, and the platform loops — consuming the window an operator needs to free space.
    .AddCheck<ERP_RFQ_Automation.HealthChecks.StorageCapacityHealthCheck>("storage-capacity", tags: new[] { "ready" })
    .AddCheck<ERP_RFQ_Automation.HealthChecks.MalwareScannerHealthCheck>("malware-scanner", tags: new[] { "ready" })
    .AddCheck<ERP_RFQ_Automation.HealthChecks.ExtractionWorkerHealthCheck>("extraction-worker", tags: new[] { "ready" })
    .AddCheck<ERP_RFQ_Automation.HealthChecks.QuoteDeliveryWorkerHealthCheck>("quote-delivery-worker", tags: new[] { "ready" })
    .AddCheck<ProcurementDispatchHealthCheck>("procurement-dispatch-worker", tags: new[] { "ready" })
    .AddCheck<ERP_RFQ_Automation.HealthChecks.BackgroundWorkerHealthCheck>(
        "background-workers", tags: new[] { "ready" })
    .AddCheck<ERP_RFQ_Automation.HealthChecks.EmailPollerHealthCheck>(
        "email-poll-channel", tags: new[] { "ready" })
    .AddCheck<ERP_RFQ_Automation.Security.DocumentInspection.OcrEngineHealthCheck>(
        "ocr-engine", tags: new[] { "ready" });
// Register repositories
builder.Services.AddScoped<ISetupMasterRepository, SetupMasterRepository>();
builder.Services.AddScoped<ICurrencyRepository, CurrencyRepository>();
builder.Services.AddScoped<IWarehouseRepository, WarehouseRepository>();
builder.Services.AddScoped<ITeamRepository, TeamRepository>();
builder.Services.AddScoped<IModuleRepository, ModuleRepository>();
builder.Services.AddScoped<IRolePermissionRepository, RolePermissionRepository>();
builder.Services.AddScoped<IUserGroupRepository, UserGroupRepository>();
builder.Services.AddScoped<IBusinessUnitRepository, BusinessUnitRepository>();
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddScoped<IAuthRepository, AuthRepository>();
builder.Services.AddPlatformEntitlements();
builder.Services.AddPlatformBilling(builder.Configuration);
builder.Services.AddScoped<IGeneralDropdownRepository, GeneralDropdownRepository>();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<ISupplierRepository, SupplierRepository>();
builder.Services.AddScoped<IContactRepository, ContactRepository>();
builder.Services.AddScoped<ILeadRepository, LeadRepository>();
builder.Services.AddScoped<ILeadIdentityApplicationService, LeadIdentityApplicationService>();
builder.Services.AddScoped<IProductCategoryRepository, ProductCategoryRepository>();
builder.Services.AddScoped<IProductSubCategoryRepository, ProductSubCategoryRepository>();
builder.Services.AddScoped<IProductRepository, ProductRepository>();
builder.Services.AddScoped<ICountryRepository, CountryRepository>();
builder.Services.AddScoped<IStateRepository, StateRepository>();
builder.Services.AddScoped<ICityRepository, CityRepository>();
builder.Services.AddScoped<IUomRepository, UomRepository>();
builder.Services.AddScoped<IRfqRepository, RfqRepository>();
builder.Services.AddScoped<IQuoteRepository, QuoteRepository>();
builder.Services.AddScoped<ISupplierPurchaseHistoryRepository, SupplierPurchaseHistoryRepository>();
builder.Services.AddScoped<ISupplierQuotedItemRepository, SupplierQuotedItemRepository>();
builder.Services.AddScoped<IProcurementApplicationService, ProcurementApplicationService>();
builder.Services.AddScoped<IProcurementHandoffService, ProcurementHandoffService>();
builder.Services.AddScoped<IProcurementIntegrationService, ProcurementIntegrationService>();
builder.Services.AddSingleton<IProcurementDeliveryConfiguration, ProcurementDeliveryConfiguration>();
builder.Services.AddScoped<SupplierQuoteInboxService>();
builder.Services.AddScoped<SupplierNegotiationService>();
builder.Services.AddScoped<SupplierQuoteCommercialService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialLearning.CommercialLearningService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialLearning.LearningGovernanceService>();
builder.Services.AddScoped<SupplierQuoteDocumentIntakeService>();
builder.Services.AddHostedService<ProcurementDispatchWorker>();
builder.Services.AddSingleton<ICommercialDocumentClassifier, DeterministicCommercialDocumentClassifier>();
builder.Services.AddScoped<CommercialDocumentClassificationService>();
builder.Services.AddScoped<SupplierGovernanceService>();
builder.Services.AddScoped<IQuoteService, QuoteService>();
builder.Services.AddScoped<IQuoteDeliveryStore, QuoteDeliveryStore>();
builder.Services.AddScoped<IQuoteDeliverySender, QuoteDeliverySender>();
builder.Services.AddSingleton<QuoteDeliveryDispatcher>();
builder.Services.AddHostedService<QuoteDeliveryWorker>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialFinance.ICommercialFinanceApplicationService, ERP_RFQ_Automation.CommercialFinance.CommercialFinanceApplicationService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialFinance.IReceivablesOperationsService, ERP_RFQ_Automation.CommercialFinance.ReceivablesOperationsService>();
builder.Services.AddScoped<ERP_RFQ_Automation.GeneralLedger.IGeneralLedgerService, ERP_RFQ_Automation.GeneralLedger.GeneralLedgerService>();
builder.Services.AddScoped<ERP_RFQ_Automation.BankReconciliation.Services.IBankReconciliationService, ERP_RFQ_Automation.BankReconciliation.Services.BankReconciliationService>();
builder.Services.AddScoped<ERP_RFQ_Automation.BankReconciliation.Services.IBankAdjustmentService, ERP_RFQ_Automation.BankReconciliation.Services.BankAdjustmentService>();
builder.Services.AddScoped<ERP_RFQ_Automation.GeneralLedger.IInternalSourceJournalPostingService, ERP_RFQ_Automation.GeneralLedger.InternalSourceJournalPostingService>();
builder.Services.AddScoped<ICustomerAwardApplicationService, CustomerAwardApplicationService>();
builder.Services.AddScoped<IShipmentRepository, ShipmentRepository>();
builder.Services.AddScoped<IQuoteConfigurationRepository, QuoteConfigurationRepository>();
builder.Services.AddScoped<IDashboardRepository, DashboardRepository>();
builder.Services.AddScoped<ICommercialCaseQueryService, CommercialCaseQueryService>();
// Currency conversion authority: effective-dated, approval-gated rates plus frozen
// per-document snapshots, so a later rate correction can never restate an issued quote.
// Seven call sites currently construct this directly because Program.cs was locked while
// the FX module landed; they can migrate to injection now that this registration exists.
builder.Services.AddScoped<ERP_RFQ_Automation.Fx.IFxConversionService, ERP_RFQ_Automation.Fx.FxConversionService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.IInventoryAvailabilityService, ERP_RFQ_Automation.Inventory.InventoryAvailabilityService>();
// The single gate permitted to write Inventory.QtyOnHand: every count, adjustment,
// reclassification and transfer posts a balancing InventoryMovement in the same
// transaction, so on-hand can be reconciled against the ledger.
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.IStockLedgerService, ERP_RFQ_Automation.Inventory.StockLedgerService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialIntelligence.Sales.ISalesPersistence, ERP_RFQ_Automation.CommercialIntelligence.Sales.EfSalesPersistence>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialIntelligence.Sales.ISalesApplicationService, ERP_RFQ_Automation.CommercialIntelligence.Sales.SalesApplicationService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialIntelligence.Exceptions.ICommercialExceptionApplicationService, ERP_RFQ_Automation.CommercialIntelligence.Exceptions.CommercialExceptionApplicationService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialIntelligence.Opportunity.IOpportunityPriorityApplicationService, ERP_RFQ_Automation.CommercialIntelligence.Opportunity.OpportunityPriorityApplicationService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialIntelligence.Growth.GrowthIntelligenceService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.IOrderStockReservationService, ERP_RFQ_Automation.Inventory.OrderStockReservationService>();
builder.Services.AddSingleton<ERP_RFQ_Automation.Inventory.Commercial.IProductIdentityResolver, ERP_RFQ_Automation.Inventory.Commercial.ProductIdentityResolver>();
builder.Services.AddSingleton<ERP_RFQ_Automation.Inventory.Commercial.IFulfilmentRouteService, ERP_RFQ_Automation.Inventory.Commercial.FulfilmentRouteService>();
builder.Services.AddScoped<ERP_RFQ_Automation.ProductIntelligence.IProductResolutionCatalog, ERP_RFQ_Automation.ProductIntelligence.EfProductResolutionCatalog>();
builder.Services.AddScoped<ERP_RFQ_Automation.ProductIntelligence.IApprovedProductReferenceSource, ERP_RFQ_Automation.ProductIntelligence.EfApprovedProductReferenceSource>();
builder.Services.AddScoped<ERP_RFQ_Automation.ProductIntelligence.IProductItemResolver, ERP_RFQ_Automation.ProductIntelligence.DeterministicProductItemResolver>();
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.Commercial.ILocalRelatedResourceRepository, ERP_RFQ_Automation.Inventory.Commercial.EfLocalRelatedResourceRepository>();
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.Commercial.ILocalRelatedResourceSearch, ERP_RFQ_Automation.Inventory.Commercial.LocalRelatedResourceSearch>();
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.Commercial.ILeadLineCommercialResolutionService, ERP_RFQ_Automation.Inventory.Commercial.LeadLineCommercialResolutionService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.Commercial.ICommercialLineResolutionApplicationService, ERP_RFQ_Automation.Inventory.Commercial.CommercialLineResolutionApplicationService>();
builder.Services.AddScoped<ILifecycleApplicationService, LifecycleApplicationService>();
builder.Services.AddScoped<ILifecycleOutboxStore, LifecycleOutboxStore>();
builder.Services.AddCommercialFinanceOutboxDispatcher(builder.Configuration);
builder.Services.AddScoped<ICommercialRoutingApplicationService, CommercialRoutingApplicationService>();
builder.Services.AddScoped<ICustomFieldApplicationService, CustomFieldApplicationService>();
builder.Services.AddSingleton<DeterministicRoutingEngine>();
builder.Services.AddSingleton(new RoutingPolicy());
// CLIENT ORGANISATION IDENTITY. The policy is a singleton so the thresholds behind every
// auto-link are one edit away from being retuned; the resolver and the learner are scoped
// because they write through the SAME request-scoped DbContext (and, for learning, the same
// transaction) as the review that triggers them.
builder.Services.AddSingleton(new ERP_RFQ_Automation.CustomerResolution.CustomerResolutionPolicy());
builder.Services.AddScoped<ERP_RFQ_Automation.CustomerResolution.ILeadCustomerResolutionService,
    ERP_RFQ_Automation.CustomerResolution.LeadCustomerResolutionService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CustomerResolution.ICustomerAliasLearner,
    ERP_RFQ_Automation.CustomerResolution.CustomerAliasLearner>();
builder.Services.AddHostedService<RoutingReconciliationWorker>();
// RBAC Authorization
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
// Server-side module RBAC (mirrors the frontend PermissionGuard):
// [RequireModulePermission("Leads", PermissionAction.Edit)] produces dynamic policy
// names ("ModulePermission:{module}:{action}") resolved by ModulePermissionPolicyProvider
// — no per-policy registration needed. IRoleGate backs the manager/admin gate
// ([RequireManagerRole]) and the super-admin bypass; IMemoryCache gives both a 60s TTL.
// ForbiddenJsonResultHandler turns every authorization 403 into a small generic JSON
// body that leaks no module names.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IRoleGate, RoleGate>();
// RC-7: IAM audit trail. Scoped because it writes through the SAME request-scoped DbContext the
// repositories use — that shared instance is what makes an audit event commit or roll back with
// the mutation it describes, rather than being a best-effort log line beside it.
builder.Services.AddScoped<IIamAuditWriter, IamAuditWriter>();
builder.Services.AddSingleton<TenantSmtpConcurrencyGate>();
builder.Services.AddSingleton<IOutboundSmtpTransport, MailKitOutboundSmtpTransport>();
// Stateless — every call carries its own settings — so a singleton is correct.
builder.Services.AddSingleton<IMailboxConnectionProbe, MailboxConnectionProbe>();
// The provider catalogue and the one connection test both planes share. After the probe, which it
// delegates the staged mail-protocol diagnosis to.
builder.Services.AddEmailProviders();
// Testing-only tenant data reset. Scoped because it reads the EF model through the request
// context; refuses Production internally regardless of how it is registered.
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Testing.TenantDataReset>();
// Makes the Module table match ModuleCatalog once the host starts. [RequireModulePermission]
// resolves by joining to a Module row on exact name and DENIES when nothing matches, so a gated
// endpoint whose module was never inserted is permanently forbidden to every non-super-admin —
// which is why most of this product was usable only by a super admin. Registered as a hosted
// service so it runs after any startup migration has applied, and so a slow or failing database
// delays reconciliation rather than the whole boot.
builder.Services.AddHostedService<ModuleCatalogStartupService>();
builder.Services.AddScoped<IAuthorizationHandler, ManagerRoleHandler>();
builder.Services.AddSingleton<IAuthorizationPolicyProvider, ModulePermissionPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, ForbiddenJsonResultHandler>();

ExcelPackage.LicenseContext = LicenseContext.NonCommercial;
builder.Services.AddAuthorization(options =>
{
    // RFQ Policies
    options.AddPolicy("CanCreateRFQ", policy => policy.Requirements.Add(new PermissionRequirement("RFQ", "CanCreate")));
    options.AddPolicy("CanEditRFQ", policy => policy.Requirements.Add(new PermissionRequirement("RFQ", "CanEdit")));
    options.AddPolicy("CanDeleteRFQ", policy => policy.Requirements.Add(new PermissionRequirement("RFQ", "CanDelete")));

    // Quotation Policies
    options.AddPolicy("CanCreateQuotation", policy => policy.Requirements.Add(new PermissionRequirement("Quotation", "CanCreate")));
    options.AddPolicy("CanEditQuotation", policy => policy.Requirements.Add(new PermissionRequirement("Quotation", "CanEdit")));
    options.AddPolicy("CanDeleteQuotation", policy => policy.Requirements.Add(new PermissionRequirement("Quotation", "CanDelete")));

    // Platform-Owner control plane: PlatformScope (default-deny) + role sub-policies (ADR-0005)
    options.AddPlatformPolicies();

    // SEC-H4: authorization was opt-in — an action with no [Authorize] attribute was
    // anonymous. The fallback policy applies to every endpoint that declares NO
    // authorization metadata of its own, so a newly added controller is authenticated by
    // default and anonymity must be requested explicitly with [AllowAnonymous].
    //
    // NOTE the scheme list: the fallback must accept BOTH bearer schemes, otherwise a
    // platform token would be rejected on any platform endpoint that does not carry its
    // own [Authorize(Policy = PlatformPolicies.*)] attribute. Endpoints that DO carry an
    // explicit policy are unaffected — the fallback only fires when there is no metadata.
    options.FallbackPolicy = new AuthorizationPolicyBuilder(
            JwtBearerDefaults.AuthenticationScheme,
            PlatformAuthConstants.Scheme)
        .RequireAuthenticatedUser()
        .Build();
});
// Register email processing services
builder.Services.AddHostedService<EmailBackgroundService>();
builder.Services.AddScoped<ManualUploadService>();
builder.Services.AddScoped<ProductUploaderService>();
builder.Services.AddScoped<ProductCategoryUploaderService>();
builder.Services.AddScoped<CustomerUploaderService>();
builder.Services.AddScoped<SupplierUploaderService>();
builder.Services.AddScoped<LeadUploaderService>();
builder.Services.AddScoped<RfqUploaderService>();
builder.Services.AddScoped<ICanonicalRfqNormalizer, CanonicalRfqNormalizer>();
builder.Services.AddScoped<QuotationUploaderService>();
builder.Services.AddScoped<FolderService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ILLMService, OllamaLlmService>();
builder.Services.AddScoped<IAiGovernanceService, AiGovernanceService>();
// One authoritative answer to "which inference endpoint is this deployment pointed at,
// and why is it Local/External" (AI/AiProviderEndpoint.cs). Singleton: it is read from
// configuration once and logged at startup, so the resolution can never again be
// discoverable only by reading source.
builder.Services.AddSingleton<IAiProviderEndpointResolver, AiProviderEndpointResolver>();
// Per-tenant allow-list of external inference endpoints (AI/AiExternalProviderTrustService.cs).
// Scoped: it reads the tenant-filtered ErpRfqAutomationContext. Its ABSENCE is a refusal,
// so a missing registration degrades to today's fail-closed behaviour, never to open egress.
builder.Services.AddScoped<AiExternalProviderTrustService>();
builder.Services.AddScoped<IAiExternalProviderTrust>(services =>
    services.GetRequiredService<AiExternalProviderTrustService>());
builder.Services.AddSingleton<IAiReservationReconciler, AiReservationReconciler>();
builder.Services.AddHostedService<AiReservationReconciliationWorker>();
builder.Services.AddHttpClient<OllamaLlmService>(client =>
{
    // DATA-06: honor the configured provider URL instead of a hardcoded localhost.
    var ollamaBaseUrl = builder.Configuration["Ollama:BaseUrl"];
    client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(ollamaBaseUrl)
        ? "http://localhost:11434/"
        : ollamaBaseUrl);
});

// CORS restricted to configured frontend origins (SEC-13). AllowAnyOrigin is
// unsafe for a system with authenticated, tenant-scoped data. Always include
// the known deployment origins, then merge env-configured origins for previews
// and future custom domains. Normalize trailing slashes because CORS origin
// matching is exact.
var configuredCorsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
var corsOrigins = configuredCorsOrigins
    .Concat([
        "http://localhost:5173",
        "http://localhost:4173",
        "http://localhost:3000",
        "http://127.0.0.1:5173",
        "http://127.0.0.1:4173",
        "http://127.0.0.1:3000",
        "https://nexora1-ai.vercel.app"
    ])
    .Where(origin => !string.IsNullOrWhiteSpace(origin))
    .Select(origin => origin.Trim().TrimEnd('/'))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToArray();
builder.Services.AddCors(options =>
{
    options.AddPolicy("DefaultCors", policy =>
    {
        policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader();
    });
});
// Configure JWT Authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "",
        ValidAudience = builder.Configuration["Jwt:Audience"] ?? "",
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
        // RC-7: without this, User.Identity.Name is ALWAYS null on a tenant token, which is why
        // every `User.Identity?.Name ?? request.CreatedBy` in the IAM controllers silently
        // resolved to the client-supplied value. AuthRepository now emits the matching claim.
        NameClaimType = System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Name,
        // Default ClockSkew is FIVE MINUTES, which is added on top of the 60-minute token
        // lifetime — the honest revocation window was therefore up to 65 minutes. Real
        // revocation needs a SecurityStamp/TokenVersion re-check (backlog item 2); this
        // shrinks the part of the window that is pure configuration.
        ClockSkew = TimeSpan.FromSeconds(30)
    };
})
// Second JWT scheme for the Platform-Owner plane (audience nexora-platform, scope=platform).
// A tenant token fails validation here and vice-versa — the hard boundary. (ADR-0005)
.AddPlatformJwtBearer(builder.Configuration);
// Configure Swagger for JWT Authentication
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "ERP RFQ Automation", Version = "v1" });
    // Define JWT Security Scheme
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    // Apply JWT Security to all endpoints
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});
builder.Services.AddEndpointsApiExplorer();
// Async extraction pipeline (ADR-0003): durable job queue + bounded worker pool.
builder.Services.AddSingleton(new ExtractionWorkerOptions
{
    WorkerCount = Math.Max(1, builder.Configuration.GetValue("Extraction:WorkerCount", 4)),
    MaxConcurrentLlmCalls = Math.Max(1, builder.Configuration.GetValue("Extraction:MaxConcurrentLlmCalls", 8)),
    PerTenantConcurrencyCap = Math.Max(1, builder.Configuration.GetValue("Extraction:PerTenantConcurrencyCap", 4)),
    LeaseDuration = TimeSpan.FromMinutes(5),
    IdlePollDelay = TimeSpan.FromSeconds(2)
});
builder.Services.AddScoped<IExtractionQueue, ExtractionQueue>();
builder.Services.AddScoped<IChunkedExtractionService, ChunkedExtractionService>();
// ING-07: the conversational (email-body) extractor. Separate registration, separate prompt,
// same governed ILLMService — the document path is untouched.
builder.Services.AddScoped<ERP_RFQ_Automation.Extraction.Conversational.IConversationalExtractionService,
    ERP_RFQ_Automation.Extraction.Conversational.ConversationalExtractionService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Ingestion.Triage.IEmailTriageService,
    ERP_RFQ_Automation.Ingestion.Triage.EmailTriageService>();
builder.Services.AddScoped<ILeadPersister, LeadPersister>();
builder.Services.AddScoped<IExtractionDocumentReader, ProductionDocumentReader>();
builder.Services.AddHostedService<ExtractionWorker>();
// ING-05: unified ingestion gateway — the ONE door to the durable queue used by the
// modern upload endpoint, the email poller, the folder watcher and manual upload
// (each door still honours Ingestion:UseUnifiedQueue, default true).
builder.Services.AddScoped<IDocumentIngestion, DocumentIngestionService>();
builder.Services.AddScoped<ISecurityScanRecoveryService, SecurityScanRecoveryService>();

// Transactional email / notifications (Notifications/): provider-agnostic sender
// (defaults to the safe console provider until an SMTP/SendGrid provider is
// configured). Powers RFQ→quote→order communications.
builder.Services.AddNotifications(builder.Configuration);

// Platform-operator-configurable outbound email (Platform/Notifications): the persisted
// sending identity, transport and credentials that AddNotifications resolves through at
// runtime, plus the operator endpoints and the startup warm-up. Registered AFTER
// AddNotifications, which declares the source seam this fills. Without it the module keeps
// working exactly as before, reading Notifications:* from configuration — which is the
// deployment where the CEO's provisioned tenant never received its activation email,
// because the default provider logs instead of sending and nothing said so.
builder.Services.AddPlatformEmailSettings();

// Founding-administrator activation (Platform/Onboarding): invite email + single-use
// activation link, so a customer's first credential is chosen by the customer and never
// passes through the operator's hands. Registered AFTER AddNotifications — it composes its
// invite through IEmailSender and reads Notifications:AppBaseUrl to build the link.
builder.Services.AddTenantOnboarding(builder.Configuration);

// Durable tenant provisioning (Platform/Provisioning): an execution + step journal per
// provisioning attempt, idempotency keys, reserved-address refusal, step-level retry with
// compensation, and server-side wizard drafts. Registered AFTER AddTenantOnboarding and after
// the baseline seeder above, because the step executor composes both. Replaces a single HTTP
// request wrapping a single transaction that did seven things, where a failure anywhere rolled
// all seven back and surfaced as a bare 500 with no record of which step broke.
builder.Services.AddDurableTenantProvisioning(builder.Configuration);

// Platform Hardening (Platform/Hardening): OpenTelemetry traces+metrics and
// tenant/IP-fair rate limiting. Both config-driven with safe fallbacks so an
// absent collector or missing config never breaks startup.
builder.Services.AddPlatformObservability(builder.Configuration);
builder.Services.AddPlatformRateLimiting(builder.Configuration);

// Autonomous sourcing copilot (Agent/): Claude tool-use loop + tenant-scoped tools +
// guardrail engine + immutable audit. Runs in mock mode until Agent:Anthropic:ApiKey is
// set, so it is fully demoable with no key. Depends on notifications/order/dashboard above.
builder.Services.AddAgentEngine(builder.Configuration);

// Lead→RFQ→Quote intelligence (Intelligence/): catalog product resolution on lead
// conversion, and the multi-signal pricing engine (price list, recent quotes,
// supplier quotes, purchase history, product master → recommended price + rationale
// + confidence per line). Both surfaced as HTTP endpoints AND copilot tools; the
// mutation tools ride the guardrail engine's unknown-mutation fail-safe (approval).
builder.Services.AddConversionIntelligence();
builder.Services.AddPricingIntelligence();
builder.Services.AddScoped<ERP_RFQ_Automation.Agent.IAgentTool, ERP_RFQ_Automation.Intelligence.Conversion.PreviewLeadConversionTool>();
builder.Services.AddScoped<ERP_RFQ_Automation.Agent.IAgentTool, ERP_RFQ_Automation.Intelligence.Conversion.ConvertLeadToRfqTool>();
builder.Services.AddScoped<ERP_RFQ_Automation.Agent.IAgentTool, ERP_RFQ_Automation.Intelligence.Pricing.PriceRfqTool>();
builder.Services.AddScoped<ERP_RFQ_Automation.Agent.IAgentTool, ERP_RFQ_Automation.Intelligence.Pricing.ApplyRfqPricingTool>();
// WP-B3: executor for below-floor holds — the approvals inbox approve endpoint
// re-invokes it via the tool registry (creation happens in BelowFloorGuard,
// already registered inside AddPricingIntelligence()).
builder.Services.AddScoped<ERP_RFQ_Automation.Agent.IAgentTool, ERP_RFQ_Automation.Intelligence.Pricing.ApproveBelowFloorQuoteTool>();
// WP-B4: append-only passive AI-metrics writer (never throws; own DI scope).
builder.Services.AddSingleton<ERP_RFQ_Automation.Metrics.IMetricRecorder, ERP_RFQ_Automation.Metrics.MetricRecorder>();

// Service RFQ → BOQ engine (Boq/): LLM drafting with honest TBDs, tenant assembly
// library, editor endpoints; vision seam ready for a drawing-capable model.
builder.Services.AddBoqEngine();
builder.Services.AddScoped<ERP_RFQ_Automation.Agent.IAgentTool, ERP_RFQ_Automation.Boq.DraftBoqTool>();
builder.Services.AddScoped<ERP_RFQ_Automation.Agent.IAgentTool, ERP_RFQ_Automation.Boq.GetBoqTool>();

// Lead Decision Brief (Intelligence/Decision): value/coverage/history/urgency →
// Bid/Review/Skip with plain-language reasons; feeds the leads grid + dashboard.
builder.Services.AddLeadDecisionIntelligence();
builder.Services.AddScoped<ERP_RFQ_Automation.Agent.IAgentTool, ERP_RFQ_Automation.Intelligence.Decision.LeadDecisionBriefTool>();

// WP-A3: duplicate-lead detection + quote-block (Deduplication/)
builder.Services.AddScoped<ERP_RFQ_Automation.Deduplication.ILeadDuplicateDetector,
                           ERP_RFQ_Automation.Deduplication.LeadDuplicateDetector>();

// WP-A1/A2: tenant-configurable SLA policy reader (SlaPolicy-backed; default 2h).
builder.Services.AddScoped<ERP_RFQ_Automation.MultiTenancy.ISlaPolicyReader,
                           ERP_RFQ_Automation.Sla.SlaPolicyReaderAdapter>();

// ==== SLA & deadline engine + quote outcome capture (Sla/) ====
// After AddNotifications(...) — SlaNotifications depends on IEmailSender.
builder.Services.AddScoped<ERP_RFQ_Automation.Sla.IQuoteOutcomeService, ERP_RFQ_Automation.Sla.QuoteOutcomeService>();
builder.Services.AddSingleton<ERP_RFQ_Automation.Sla.ISlaNotifications, ERP_RFQ_Automation.Sla.SlaNotifications>();
builder.Services.AddHostedService<ERP_RFQ_Automation.Sla.SlaSweepWorker>();
builder.Services.AddScoped<ERP_RFQ_Automation.PlatformGovernance.PlatformGovernanceService>();
builder.Services.AddScoped<ERP_RFQ_Automation.PlatformGovernance.HumanActionService>();
builder.Services.AddScoped<ERP_RFQ_Automation.PlatformGovernance.AiTrustCenterService>();
builder.Services.AddScoped<ERP_RFQ_Automation.PlatformGovernance.CommercialDocumentArchiveService>();
builder.Services.AddScoped<ERP_RFQ_Automation.PlatformGovernance.QualityAnalyticsService>();
// Evidence retention: purge stored BYTES, keep the evidence RECORD. The immutability
// triggers make the tombstone stronger proof than the file it replaces.
builder.Services.AddScoped<ERP_RFQ_Automation.Retention.LegacyAttachmentPurgeResolver>();
builder.Services.AddScoped<ERP_RFQ_Automation.Retention.EvidenceRetentionService>();

// SEC-H6: the app sits behind a TLS-terminating reverse proxy, so the socket peer is the
// proxy, not the client. Without this, the rate limiter's per-IP partition
// (RateLimitingExtensions.PartitionKey) buckets the entire internet together and request
// logs attribute everything to the proxy.
//
// KnownNetworks/KnownProxies are cleared and then populated from configuration. Leaving
// them EMPTY would trust the header from any caller, which lets an attacker spoof
// X-Forwarded-For and evade per-IP limits entirely — so set ForwardedHeaders:KnownProxies
// (or :KnownNetworks) in appsettings for each environment before deploying behind a proxy.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();

    foreach (var proxy in builder.Configuration.GetSection("ForwardedHeaders:KnownProxies")
                 .Get<string[]>() ?? Array.Empty<string>())
    {
        if (System.Net.IPAddress.TryParse(proxy, out var address))
            options.KnownProxies.Add(address);
    }

    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks")
                 .Get<string[]>() ?? Array.Empty<string>())
    {
        var parts = network.Split('/', 2);
        if (parts.Length == 2 && System.Net.IPAddress.TryParse(parts[0], out var prefix)
            && int.TryParse(parts[1], out var length))
        {
            options.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(prefix, length));
        }
    }
});

var app = builder.Build();

// Startup provider telemetry (loud on purpose). Constructing this singleton emits one
// WARNING when the configured inference endpoint is external and one INFO when it is
// loopback. Production ran external for weeks with nothing in the log saying so.
app.Services.GetRequiredService<IAiProviderEndpointResolver>();

// An ephemeral key is only reachable in Development (CreateFromConfiguration throws
// otherwise), but say so out loud: anything protected under it becomes unreadable at the
// next restart, which is confusing unless you know why.
if (SecretProtection.UsingEphemeralDevelopmentKey)
    app.Logger.LogWarning(
        "INSECURE: no {ConfigurationPath} configured, so a random process-lifetime key is protecting " +
        "stored mailbox credentials. Values encrypted now CANNOT be read after a restart. This fallback " +
        "exists for local development only and is refused outside Development.",
        SecretProtection.KeyConfigurationPath);

// Production defaults to applying migrations before serving traffic. This guarantees
// tenant-role/RLS policy installation is atomic with the application rollout. Set
// Database:ApplyMigrationsOnStartup=false only when an external release job owns it.
var applyMigrations = app.Configuration.GetValue<bool?>("Database:ApplyMigrationsOnStartup")
    ?? app.Environment.IsProduction();
var configuredMigrationConnection = app.Configuration.GetConnectionString("MigrationConnection");
var migrationConnection = configuredMigrationConnection
    ?? ResolveDirectMigrationConnection(connectionString);
var allowManagedOwnerCompatibility = app.Configuration.GetValue<bool>(
    "Database:AllowManagedOwnerRoleMigrationCompatibility");
if (allowManagedOwnerCompatibility && string.IsNullOrWhiteSpace(configuredMigrationConnection))
    throw new InvalidOperationException(
        "Managed owner migration compatibility requires an explicit ConnectionStrings:MigrationConnection " +
        "separate from the least-privilege runtime connection.");
if (applyMigrations)
{
    var migrationOptionsBuilder = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
        .UseNpgsql(migrationConnection, npgsql =>
        {
            npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
            npgsql.CommandTimeout(120);
        });
    if (allowManagedOwnerCompatibility)
        migrationOptionsBuilder.AddInterceptors(new ManagedPostgresMigrationCommandInterceptor());
    await using var migrationDb = new ErpRfqAutomationContext(migrationOptionsBuilder.Options);
    await migrationDb.Database.MigrateAsync();
}
// Backfill legacy cleartext mailbox credentials into the protected envelope. Deliberately a
// startup step rather than a `migrationBuilder.Sql` data migration: the key lives in
// application configuration, NOT in the database, so no SQL script can perform AES-256-GCM
// with it. Running it here is also where the key is guaranteed present and validated.
// Idempotent (skips anything already carrying the v1: prefix), so it is safe on every boot.
await MailboxCredentialProtectionBackfill.RunAsync(migrationConnection, secretProtector, app.Logger);


await SyncFinanceProviderSecretsAsync(
    migrationConnection, contactVerificationSecret, dunningProviderSecret, auditActorSecret);

if (app.Environment.IsProduction())
    await ValidateRuntimeDatabaseRoleAsync(connectionString);

static string ResolveDirectMigrationConnection(string runtimeConnection)
    => runtimeConnection.Replace("-pooler.", ".", StringComparison.OrdinalIgnoreCase);

static async Task SyncFinanceProviderSecretsAsync(
    string connectionString, string contactSecret, string deliverySecret, string auditActorSecret)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        INSERT INTO public."FinanceProviderSecrets" ("Name", "Secret", "UpdatedOn")
        VALUES ('ContactVerification', @contact_secret, now()),
               ('DunningDelivery', @delivery_secret, now()),
               ('AuditActor', @audit_actor_secret, now())
        ON CONFLICT ("Name") DO UPDATE
        SET "Secret" = EXCLUDED."Secret", "UpdatedOn" = EXCLUDED."UpdatedOn";
        """;
    command.Parameters.AddWithValue("contact_secret", contactSecret);
    command.Parameters.AddWithValue("delivery_secret", deliverySecret);
    command.Parameters.AddWithValue("audit_actor_secret", auditActorSecret);
    await command.ExecuteNonQueryAsync();
}

static async Task ValidateRuntimeDatabaseRoleAsync(string runtimeConnection)
{
    await using var connection = new NpgsqlConnection(runtimeConnection);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandText = """
        SELECT NOT runtime_role.rolinherit
               AND NOT runtime_role.rolsuper
               AND NOT runtime_role.rolbypassrls
               AND pg_has_role(current_user, 'nexora_tenant_app', 'MEMBER')
               AND pg_has_role(current_user, 'nexora_identity_app', 'MEMBER')
               AND pg_has_role(current_user, 'nexora_pipeline_app', 'MEMBER')
               AND EXISTS (
                   SELECT 1 FROM pg_roles identity_role
                   WHERE identity_role.rolname = 'nexora_identity_app'
                     AND NOT identity_role.rolcanlogin
                     AND NOT identity_role.rolinherit
                     AND NOT identity_role.rolsuper
                     AND NOT identity_role.rolcreatedb
                     AND NOT identity_role.rolcreaterole
                     AND identity_role.rolbypassrls)
               AND EXISTS (
                   SELECT 1 FROM pg_roles pipeline_role
                   WHERE pipeline_role.rolname = 'nexora_pipeline_app'
                     AND NOT pipeline_role.rolcanlogin
                     AND NOT pipeline_role.rolinherit
                     AND NOT pipeline_role.rolsuper
                     AND NOT pipeline_role.rolcreatedb
                     AND NOT pipeline_role.rolcreaterole
                     AND pipeline_role.rolbypassrls)
               AND NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_ai_maintenance')
        FROM pg_roles runtime_role
        WHERE runtime_role.rolname = current_user;
        """;
    if (await command.ExecuteScalarAsync() is not true)
        throw new InvalidOperationException(
            "The runtime database role must be NOINHERIT, non-superuser, non-BYPASSRLS, a member of the tenant, identity, and pipeline execution roles, and have no AI maintenance bypass role.");
}

// Prove the REQUEST PATH can reach the database before anything reports ready. Migrations and
// the seeders below open their own connections and can succeed while the context controllers
// resolve cannot authenticate at all. Off unless explicitly enabled.
await RequestPathDatabaseValidator.ValidateAsync(app.Services, app.Configuration);

await DemoUserSeeder.EnsureAsync(app.Services, app.Configuration, app.Environment);
// Local E2E only: fail-closed on GoldenJourneySeed:Enabled and refuses under Production.
await GoldenCommercialJourneySeeder.EnsureAsync(app.Services, app.Configuration, app.Environment);
await app.SeedPlatformOwnerAsync();

// Global exception handler — return a generic message to clients and log the
// detail server-side, instead of leaking exception internals. (DATA-12, SEC-16)
app.UseExceptionHandler(errApp => errApp.Run(async ctx =>
{
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    ctx.Response.ContentType = "application/json";
    await ctx.Response.WriteAsync("{\"error\":\"An unexpected error occurred.\"}");
}));

// Baseline security headers (SEC-13)
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

// SEC-H6: must run before ANY middleware that reads the client IP or scheme — in
// particular UsePlatformObservability and UseRateLimiter further down.
app.UseForwardedHeaders();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
//app.UseHttpsRedirection(); // enable at deploy time once TLS is terminated in front of the app
// Use CORS
app.UseCors("DefaultCors");
app.UseAuthentication();
app.UseReadOnlyImpersonationGuard();
// Tenant + correlation-id logging scope — AFTER auth so the businessUnitId claim exists.
app.UsePlatformObservability();
// Authenticated tenant routes fail closed when a token has no valid tenant claim.
app.UseTenantClaimGuard();
app.UseTenantStatusGuard();
app.UseAuthorization();
// Built-in rate limiter — AFTER auth so the per-tenant partition uses the claim.
app.UseRateLimiter();
// SEC-H4: belt-and-braces with the FallbackPolicy above — every controller endpoint
// carries authorization metadata, so a future `options.FallbackPolicy = null` or a
// mis-ordered middleware registration cannot silently reopen the whole API.
app.MapControllers().RequireAuthorization();
// SEC-H4: probes must stay reachable once the FallbackPolicy makes authentication the
// default. These expose only status + tag-filtered check names, never tenant data.
app.MapHealthChecks("/health", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
}).AllowAnonymous();
app.MapHealthChecks("/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
}).AllowAnonymous();
// Zero-dependency Prometheus scrape endpoint (Platform/Hardening). Self-disabling: the
// call is a no-op unless ObservabilityExtensions.SelectExporter enabled it, which by
// default happens exactly when no OTLP collector is configured — so the process can
// never again register a meter and export nothing. Carries its own X-Scrape-Key check.
app.MapNexoraMetricsScrape();

app.Run();

public partial class Program;
