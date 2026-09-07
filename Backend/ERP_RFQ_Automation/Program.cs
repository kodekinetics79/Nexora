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
using ERP_RFQ_Automation.MasterData;
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
using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Security.DocumentInspection;
using ERP_RFQ_Automation.Security.PasswordReset;
using Microsoft.AspNetCore.HttpOverrides;
using ERP_RFQ_Automation.Procurement;
using ERP_RFQ_Automation.CommercialDocuments;
using ERP_RFQ_Automation.SupplierGovernance;
using ERP_RFQ_Automation.SupplierQuotes;
using System.Text.Json.Serialization;
using Npgsql;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Activation;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Platform.DataAssets;
using ERP_RFQ_Automation.Billing;

// PostgreSQL migration: restore pre-6.0 Npgsql timestamp semantics so the
// existing DateTime usage (DateTime.Now / Unspecified-kind values inherited from
// the SQL Server codebase) maps to `timestamp without time zone` and is accepted
// regardless of DateTimeKind. Must run before any Npgsql data source is built.
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// Console logging that actually renders the per-request scope (correlation id, tenant, path)
// that TenantLoggingMiddleware pushes: JSON with scopes outside Development, single-line simple
// with scopes in Development. Before this the simple formatter dropped the scope entirely, so no
// production log line could be tied to the X-Correlation-ID a caller was holding.
builder.Logging.AddNexoraConsole(builder.Environment.IsDevelopment());

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

// Safety net for intake doors that do not catch it themselves: a durable-storage outage
// renders as the one honest 503 refusal instead of a bare 500 that names nothing.
builder.Services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(
    options => options.Filters.Add<EvidenceStorageProblemFilter>());

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
// LOOPBACK MAIL ALLOWANCE — requested by configuration, GRANTED only by environment.
//
// MailEndpointPolicy refuses loopback by default in every environment. A developer machine has
// no publicly-routable mail sink, so without this the mailbox journey could not be exercised end
// to end locally and the one path that loses a customer's mail was only ever tested against
// doubles. The environment check is a PARAMETER to the enabling call rather than a read inside
// it, so no key, variable or appsettings file can grant this on a non-Development host: a
// production deployment carrying the flag set true is a no-op, not a hole. Scoped to loopback
// only — private and link-local ranges stay refused everywhere.
if (ERP_RFQ_Automation.Security.MailEndpointPolicy.EnableLoopbackForLocalDevelopment(
        builder.Environment.IsDevelopment(),
        builder.Configuration.GetValue(
            ERP_RFQ_Automation.Security.MailEndpointPolicy.LoopbackAllowanceKey, false)))
{
    Console.WriteLine(
        "[mail] LOOPBACK MAIL ENDPOINTS ARE PERMITTED for this Development host "
        + $"({ERP_RFQ_Automation.Security.MailEndpointPolicy.LoopbackAllowanceKey}=true). "
        + "Private and link-local addresses remain refused. This cannot be enabled outside Development.");
}

builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
// The four legacy document doors write through ONE component whose target is ONE switch
// (EvidenceStorage:RouteLegacyWritersToObjectStore, default false = today's disk behaviour), and
// the one-off disk->object migration job behind its own switch. docs/design/evidence-object-store-cutover.md
builder.Services.AddSingleton<ILegacyDocumentStore, LegacyDocumentStore>();
builder.Services.AddScoped<LegacyEvidenceMigrationJob>();
builder.Services.AddHostedService<LegacyEvidenceMigrationHostedService>();
builder.Services.Configure<S3EvidenceStorageOptions>(
    builder.Configuration.GetSection(S3EvidenceStorageOptions.SectionName));
builder.Services.Configure<MalwareVerdictPolicyOptions>(
    builder.Configuration.GetSection(MalwareVerdictPolicyOptions.SectionName));
builder.Services.AddSingleton<IEvidenceObjectStorage>(services =>
{
    var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptions<S3EvidenceStorageOptions>>();
    if (!string.Equals(options.Value.Provider, "S3", StringComparison.OrdinalIgnoreCase))
        return new LocalEvidenceObjectStorage(services.GetRequiredService<IFileStorage>());

    try
    {
        return new S3EvidenceObjectStorage(options);
    }
    catch (InvalidOperationException exception)
    {
        // An S3 block missing its bucket, credentials or a usable endpoint used to throw out of
        // THIS factory, so resolving any intake controller failed and every upload answered an
        // unhandled 500 — the 2026-08-12 defect one degree worse, since a 500 names nothing at
        // all. Hand back a store that refuses honestly instead: readiness goes Unhealthy, every
        // door returns the one "document storage is not configured" outcome, and the reason a
        // human can act on is logged here, once, where infrastructure detail belongs.
        services.GetRequiredService<ILoggerFactory>()
            .CreateLogger("ERP_RFQ_Automation.Infrastructure.Storage")
            .LogError(exception,
                "Durable evidence storage is configured for S3 but could not be constructed. "
                + "Document intake is paused until the EvidenceStorage configuration is corrected.");
        return new UnconfiguredEvidenceObjectStorage(exception);
    }
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
    logging.AddNexoraConsole(builder.Environment.IsDevelopment());
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

// Server-authoritative platform MFA enforcement (Platform/Auth). FAIL FAST: this line reads and
// validates Platform:Mfa:* and throws before the host is built if a production deployment has
// claimed isolated-test-infrastructure status or configured a bound outside its permitted range —
// the same contract as SecretProtection.CreateFromConfiguration above and
// UnifiedDocumentIngestionGuard.Enforce below. Registered BEFORE the platform auth service, which
// takes the policy provider and the browser-trust ledger as optional dependencies and treats their
// absence as "enforce".
builder.Services.AddPlatformMfaPolicy(builder.Configuration, builder.Environment);

// Platform-Owner control-plane services (ADR-0005)
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Auth.IPlatformAuthService, ERP_RFQ_Automation.Platform.Auth.PlatformAuthService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Services.IPlatformAuditService, ERP_RFQ_Automation.Platform.Services.PlatformAuditService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Operations.PlatformDeadLetterRecoveryService>();
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
builder.Services.AddTenantDataAssetRegistry();

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
// Per-request tenant token re-check + same-process cache eviction. One class, two interfaces,
// so a rotation site and the validator share one cache entry by construction. SINGLETON, and
// it opens its own scope for the one database read: resolving the request's scoped DbContext
// from OnTokenValidated would freeze the request's tenant context at "no tenant" (see the
// class remarks).
builder.Services.AddSingleton<ERP_RFQ_Automation.Security.TenantSessionValidator>();
builder.Services.AddSingleton<ERP_RFQ_Automation.Security.ITenantSessionValidator>(
    services => services.GetRequiredService<ERP_RFQ_Automation.Security.TenantSessionValidator>());
builder.Services.AddSingleton<ERP_RFQ_Automation.Security.ITenantSessionCache>(
    services => services.GetRequiredService<ERP_RFQ_Automation.Security.TenantSessionValidator>());
builder.Services.AddPlatformEntitlements();
builder.Services.AddTenantActivationPolicy();
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
// FR-SPO-01. Bound from configuration so a one-buyer trading company can stand the control down
// deliberately; unbound configuration leaves it enforced, which is the default in the options type.
builder.Services.Configure<ERP_RFQ_Automation.Procurement.ProcurementApprovalOptions>(
    builder.Configuration.GetSection(ERP_RFQ_Automation.Procurement.ProcurementApprovalOptions.SectionName));
builder.Services.AddScoped<IProcurementApplicationService, ProcurementApplicationService>();
builder.Services.AddScoped<IProcurementHandoffService, ProcurementHandoffService>();
builder.Services.AddScoped<IProcurementIntegrationService, ProcurementIntegrationService>();
// Gate 5 / Module 6 (FR-MAS-01..05). Inbound supplier shipments, the Saudi entry-point master and
// the per-BU lead times the Material Available Date is derived from. Keyed to the supplier PO
// (decision R3); the outbound Models.Shipment is untouched.
builder.Services
    .AddScoped<ERP_RFQ_Automation.InboundLogistics.IInboundShipmentApplicationService,
        ERP_RFQ_Automation.InboundLogistics.InboundShipmentApplicationService>();
// The Gate 4/Gate 5 seam. Injected into ProcurementApplicationService so a goods receipt settles
// the inbound shipment that carried the material inside the receipt's own transaction, rather than
// leaving a shipment reading as in flight after its stock has already landed.
builder.Services
    .AddScoped<ERP_RFQ_Automation.InboundLogistics.IInboundShipmentReceiptSettlement,
        ERP_RFQ_Automation.InboundLogistics.InboundShipmentReceiptSettlement>();
builder.Services.AddSingleton<IProcurementDeliveryConfiguration, ProcurementDeliveryConfiguration>();
builder.Services.AddScoped<SupplierQuoteInboxService>();
builder.Services.AddScoped<SupplierNegotiationService>();
builder.Services.AddScoped<SupplierQuoteCommercialService>();
// The write path for the per-tenant commercial policy (input-tax recoverability, output tax rate,
// PO tolerances). The read path is an extension method on the DbContext and needs no registration.
builder.Services.AddScoped<ERP_RFQ_Automation.OrderToCash.CommercialMatchingPolicyService>();
builder.Services.AddScoped<ERP_RFQ_Automation.SupplierEvaluation.SupplierComparisonWeightsService>();
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
// FR-COM-01: reading an uploaded customer PO instead of re-keying it.
builder.Services.AddScoped<
    ERP_RFQ_Automation.OrderToCash.PurchaseOrderIntake.ICustomerPurchaseOrderDocumentService,
    ERP_RFQ_Automation.OrderToCash.PurchaseOrderIntake.CustomerPurchaseOrderDocumentService>();
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
// Gate 5 / FR-MTR-01..05. The recorder is registered so ProcurementApplicationService receives it
// by injection rather than falling back to its default construction, which keeps one instance per
// request and one place to substitute it in tests.
builder.Services.AddScoped<ERP_RFQ_Automation.Traceability.IMaterialLotRecorder, ERP_RFQ_Automation.Traceability.MaterialLotRecorder>();
builder.Services.AddScoped<ERP_RFQ_Automation.Traceability.IMaterialTraceabilityService, ERP_RFQ_Automation.Traceability.MaterialTraceabilityService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Traceability.IMaterialLotCertificateService, ERP_RFQ_Automation.Traceability.MaterialLotCertificateService>();
// Gate 7 / FR-DLM-01..07. The delivered-quantity ledger is the single definition of "delivered" in
// the product: the invoice ceiling, the delivery note and the order screens all read it, so it is
// registered once and injected rather than reconstructed, and there is exactly one place to change
// what the word means.
builder.Services.AddScoped<ERP_RFQ_Automation.Delivery.IDeliveredQuantityLedger, ERP_RFQ_Automation.Delivery.DeliveredQuantityLedger>();
builder.Services.AddScoped<ERP_RFQ_Automation.Delivery.IDeliveryConfirmationService, ERP_RFQ_Automation.Delivery.DeliveryConfirmationService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Delivery.IDeliveryProofEvidenceService, ERP_RFQ_Automation.Delivery.DeliveryProofEvidenceService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Delivery.IDeliveryNoteReadService, ERP_RFQ_Automation.Delivery.DeliveryNoteReadService>();
// Gate 6 / FR-INV-03. The seam that makes a goods issue declare the lots it moved. Registered
// unconditionally: NullLotFulfilmentDeclarer exists only so a caller cannot be written against a
// null reference, and if it were ever reached here, lot consumption would silently stop being
// declared and where-used trace would go quietly incomplete.
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.ILotFulfilmentDeclarer, ERP_RFQ_Automation.Traceability.MaterialLotFulfilmentDeclarer>();
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
builder.Services.AddCommercialFinanceOutboxDispatcher(builder.Configuration);
builder.Services.AddScoped<ICommercialRoutingApplicationService, CommercialRoutingApplicationService>();
builder.Services.AddScoped<ICustomFieldApplicationService, CustomFieldApplicationService>();
// AA-01 · tenant-defined custom fields (jsonb value bag) + per-user list-view columns.
builder.Services.AddScoped<ICustomFieldBagService, CustomFieldBagService>();
builder.Services.AddScoped<ERP_RFQ_Automation.ListViews.IListViewPreferenceService,
    ERP_RFQ_Automation.ListViews.ListViewPreferenceService>();
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
// ([RequireManagerRole]), the tenant-owner gate ([RequireTenantOwnerRole]), and the
// admin-rank module rule; IMemoryCache gives role-rank lookups a 60s TTL.
// ForbiddenJsonResultHandler turns every authorization 403 into a small generic JSON
// body that leaks no module names.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IRoleGate, RoleGate>();
// FR-CST-02 / FR-DSH-05: the account-team scope every customer, dashboard and search read is
// filtered by. Scoped because it reads team membership through the request-scoped DbContext, and
// because the answer is a property of the CALLER — a singleton would have to be keyed by user and
// would then be a cache of authorization decisions with no invalidation.
builder.Services.AddScoped<IAccountTeamScopeResolver, AccountTeamScopeResolver>();
builder.Services.AddScoped<ICommercialAccessContext, CommercialAccessContext>();
// FR-DSH-04: the cross-entity quick search behind the top bar. Scoped for the same reason —
// its permission and account-scope decisions are the caller's, not the process's.
builder.Services.AddScoped<ERP_RFQ_Automation.Search.IGlobalSearchService,
    ERP_RFQ_Automation.Search.GlobalSearchService>();
// RC-7: IAM audit trail. Scoped because it writes through the SAME request-scoped DbContext the
// repositories use — that shared instance is what makes an audit event commit or roll back with
// the mutation it describes, rather than being a best-effort log line beside it.
builder.Services.AddScoped<IIamAuditWriter, IamAuditWriter>();
// FR-MDM-05 / E44: the READ side of the master-data before/after trail. The WRITE side needs no
// registration at all — it is invoked from ErpRfqAutomationContext.SaveChanges, which is the point
// no write path (controller, repository, worker or spreadsheet uploader) can go around.
builder.Services.AddScoped<
    ERP_RFQ_Automation.MasterData.IMasterDataChangeHistoryReader,
    ERP_RFQ_Automation.MasterData.MasterDataChangeHistoryReader>();
builder.Services.AddSingleton<TenantSmtpConcurrencyGate>();
builder.Services.AddSingleton<IOutboundSmtpTransport, MailKitOutboundSmtpTransport>();
// Per-tenant outbound sender (issue #54): the tenant plane supplies the seam Notifications
// declares, so quotes and supplier RFQs leave from the tenant's own active SMTP mailbox and
// fall back to the platform sender only when the tenant has none.
builder.Services.AddScoped<ERP_RFQ_Automation.Notifications.Runtime.ITenantOutboundSenderSource,
    ERP_RFQ_Automation.Mailbox.TenantOutboundSenderSource>();
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
// Gives every EXISTING business unit the Setup_Master reference lists a new one is provisioned
// with (ShipmentStatus, PaymentMethod, LeadRejectedReason, RFQType, QuoteOutcomeReason). Adds a
// list only where the tenant has none; never edits a row. Guarded by
// TenantBaseline:ReconcileReferenceListsOnStartup (default true).
builder.Services.AddHostedService<ERP_RFQ_Automation.Platform.Services.TenantReferenceListStartupReconciler>();
builder.Services.AddScoped<IAuthorizationHandler, ManagerRoleHandler>();
builder.Services.AddScoped<IAuthorizationHandler, TenantOwnerRoleHandler>();
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
builder.Services.AddScoped<QuoteBackfillSpine>();
builder.Services.AddScoped<QuoteBackfillService>();
builder.Services.AddScoped<FolderService>();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ILLMService, OllamaLlmService>();
builder.Services.AddScoped<IAiGovernanceService, AiGovernanceService>();
// One authoritative answer to "which inference endpoint is this deployment pointed at,
// and why is it Local/External" (AI/AiProviderEndpoint.cs). Singleton: it is read from
// configuration once and logged at startup, so the resolution can never again be
// discoverable only by reading source.
builder.Services.AddSingleton<IAiProviderEndpointResolver, AiProviderEndpointResolver>();
// What this deployment pays for AI, beside the settings that name the endpoint it pays it to.
builder.Services.AddSingleton<IAiRateCardProvider, AiRateCardProvider>();
// Per-tenant allow-list of external inference endpoints (AI/AiExternalProviderTrustService.cs).
// Scoped: it reads the tenant-filtered ErpRfqAutomationContext. Its ABSENCE is a refusal,
// so a missing registration degrades to today's fail-closed behaviour, never to open egress.
builder.Services.AddScoped<AiExternalProviderTrustService>();
builder.Services.AddScoped<IAiExternalProviderTrust>(services =>
    services.GetRequiredService<AiExternalProviderTrustService>());
// Read-only pre-flight over the whole extraction chain (AI/AiExtractionReadiness.cs). It
// reports; it never remediates, and nothing enforces on its output.
builder.Services.AddScoped<AiExtractionReadinessService>();
builder.Services.AddSingleton<IAiReservationReconciler, AiReservationReconciler>();
builder.Services.AddHostedService<AiReservationReconciliationWorker>();
builder.Services.AddHttpClient<OllamaLlmService>(client =>
{
    // DATA-06: honor the configured provider URL instead of a hardcoded localhost.
    var ollamaBaseUrl = builder.Configuration["Ollama:BaseUrl"];
    client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(ollamaBaseUrl)
        ? "http://localhost:11434/"
        : ollamaBaseUrl);
})
    // The endpoint classification is decided once, from configuration. Without this the
    // client would happily follow a 307 off the loopback service — re-sending the method and
    // the document body to an arbitrary host — and would dial whatever address the name
    // resolved to at request time, which need not be the address that was classified.
    // AiEgressGuard refuses redirects outright and re-validates every connection against the
    // class this endpoint was classified as. (AI/AiEgressGuard.cs)
    .ConfigurePrimaryHttpMessageHandler(services =>
    {
        var endpointResolver = services.GetRequiredService<IAiProviderEndpointResolver>();
        return AiEgressGuard.CreateHandler(() => endpointResolver.Current.ProviderClass);
    })
    // SEC-G9: IHttpClientFactory's own logging writes the FULL request and response header
    // collection at Trace, and HttpClientFactoryOptions.ShouldRedactHeaderValue defaults to
    // redacting NOTHING. OllamaLlmService sets Authorization: Bearer <provider key>, so raising
    // the log level for a single diagnostic session — the one thing anyone does while chasing an
    // inference failure — would write the live key into the log sink. Named explicitly rather
    // than left to the default, in all five registrations.
    .RedactLoggedHeaders(OutboundHttpRedaction.SensitiveHeaders);

// CORS restricted to configured frontend origins (SEC-13). AllowAnyOrigin is
// unsafe for a system with authenticated, tenant-scoped data. Always include
// the known deployment origins, then merge env-configured origins for previews
// and future custom domains.
//
// SEC-G9: the six loopback origins this list used to carry UNCONDITIONALLY are now
// Development-only (TransportSecurityPolicy.DevelopmentOrigins). A CORS allow-list is
// enforced by the browser and not by the network, so while production admitted
// http://localhost:5173 any page a developer — or an attacker — served on that origin
// could call the production API and READ the response, with whatever bearer token the
// visitor's session carried. Normalisation and exact-match semantics are unchanged.
var corsOrigins = TransportSecurityPolicy.ResolveCorsOrigins(
    builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddCors(options =>
{
    options.AddPolicy("DefaultCors", policy =>
    {
        // FR-RFQ-08: the attachment download states whether the bytes were proved to match the
        // digest recorded at capture. The frontend is a different origin, so without an explicit
        // expose-list the browser hides that header and "unverified" would silently read as
        // "fine" — the exact ambiguity the header exists to remove.
        policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader()
            .WithExposedHeaders(ERP_RFQ_Automation.Controllers.FileController.IntegrityHeader);
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
        // lifetime — the honest revocation window was therefore up to 65 minutes. This shrinks
        // the part of the window that is pure configuration; the OnTokenValidated hook below is
        // the real revocation (formerly backlog item 2).
        ClockSkew = TimeSpan.FromSeconds(30)
    };
    // Live account re-check on every request (docs/design/token-revocation.md). Mirrors the
    // platform scheme in PlatformAuthExtensions: a deactivated, demoted or re-credentialed
    // account is refused on its NEXT request instead of keeping full access for the rest of the
    // token's 60-minute life. Fails closed if the check itself fails.
    options.Events = new JwtBearerEvents
    {
        OnTokenValidated = async context =>
        {
            try
            {
                var validator = context.HttpContext.RequestServices
                    .GetRequiredService<ERP_RFQ_Automation.Security.ITenantSessionValidator>();
                if (context.Principal is null
                    || !await validator.IsCurrentAsync(context.Principal, context.HttpContext.RequestAborted))
                    context.Fail("The account behind this token is no longer valid.");
            }
            catch (OperationCanceledException) when (context.HttpContext.RequestAborted.IsCancellationRequested)
            {
                context.Fail("Tenant session validation was cancelled.");
            }
            catch (Exception exception)
            {
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("ERP_RFQ_Automation.Security.TenantSessionValidator")
                    .LogWarning(exception, "Tenant session validation failed; the request is refused.");
                context.Fail("Tenant session validation failed.");
            }
        }
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
var requestedExtractionWorkerOptions = new ExtractionWorkerOptions
{
    WorkerCount = Math.Max(1, builder.Configuration.GetValue("Extraction:WorkerCount", 4)),
    MaxConcurrentLlmCalls = Math.Max(1, builder.Configuration.GetValue("Extraction:MaxConcurrentLlmCalls", 8)),
    PerTenantConcurrencyCap = Math.Max(1, builder.Configuration.GetValue("Extraction:PerTenantConcurrencyCap", 4)),
    LeaseDuration = TimeSpan.FromMinutes(5),
    IdlePollDelay = TimeSpan.FromSeconds(2)
};
builder.Services.AddSingleton(ExtractionWorkerCapacityPolicy.Apply(
    requestedExtractionWorkerOptions,
    GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));
builder.Services.AddSingleton<IExtractionHeavyWorkAdmission>(
    new ExtractionHeavyWorkAdmission(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes));
builder.Services.AddScoped<IExtractionQueue, ExtractionQueue>();
builder.Services.AddScoped<IChunkedExtractionService, ChunkedExtractionService>();
// ING-07: the conversational (email-body) extractor. Separate registration, separate prompt,
// same governed ILLMService — the document path is untouched.
builder.Services.AddScoped<ERP_RFQ_Automation.Extraction.Conversational.IConversationalExtractionService,
    ERP_RFQ_Automation.Extraction.Conversational.ConversationalExtractionService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Ingestion.Triage.IEmailTriageService,
    ERP_RFQ_Automation.Ingestion.Triage.EmailTriageService>();
// Spec §1: the canonical per-message intake record (read-only projection over the ledgers).
builder.Services.AddScoped<ERP_RFQ_Automation.Ingestion.CanonicalRecord.ICanonicalIntakeRecordService,
    ERP_RFQ_Automation.Ingestion.CanonicalRecord.CanonicalIntakeRecordService>();
builder.Services.AddScoped<ILeadPersister, LeadPersister>();
// The message barrier's payoff, registered here rather than with AddEmailInquiryAssembly
// because it depends on ILeadPersister: one email message becomes ONE Lead, built from every
// component's durable result once the last of them has finished. The worker calls it.
builder.Services.AddScoped<ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryLeadAssembler,
    ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryLeadAssembler>();
// THE one door into the pipeline, shared by the mailbox poller and the manual reprocess
// endpoint so the two cannot drift apart again. Registered beside IDocumentIngestion, which it
// needs, rather than in AddEmailInquiryAssembly.
builder.Services.AddScoped<ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryIntakeService,
    ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryIntakeService>();

// The stranded-message sweep. The worker commits the queue job and assembles afterwards, which
// is the correct order (assembling first duplicates the lead on retry) but leaves a window: a
// process that dies in between leaves every part complete, every result durable, and no lead —
// with nothing that would ever look again. Registered as options + service + worker so the
// sweep itself is testable without a timer or a hosted lifetime.
var assemblyRecoveryOptions = builder.Configuration
        .GetSection(ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryAssemblyRecoveryOptions.SectionName)
        .Get<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryAssemblyRecoveryOptions>()
    ?? new ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryAssemblyRecoveryOptions();
// The stranded-COMPONENT threshold is additionally readable at the operator-facing key
// Ingestion:Assembly:StrandedComponentSweepMinutes, which sits with the rest of the assembly
// settings rather than inside the recovery worker's own section. Applied second, so an explicit
// value there wins over one set in the recovery section; absent, the section (or the default)
// stands. One property, two discoverable places, and no third source of truth.
assemblyRecoveryOptions.StrandedComponentSweepMinutes = builder.Configuration.GetValue(
    "Ingestion:Assembly:StrandedComponentSweepMinutes",
    assemblyRecoveryOptions.StrandedComponentSweepMinutes);
builder.Services.AddSingleton(assemblyRecoveryOptions);
builder.Services.AddScoped<ERP_RFQ_Automation.Ingestion.Assembly.IEmailInquiryAssemblyRecoveryService,
    ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryAssemblyRecoveryService>();
builder.Services.AddHostedService<ERP_RFQ_Automation.Ingestion.Assembly.EmailInquiryAssemblyRecoveryWorker>();
builder.Services.AddScoped<IExtractionDocumentReader, ProductionDocumentReader>();
builder.Services.AddHostedService<ExtractionWorker>();
// ING-05: unified ingestion gateway — the ONE door to the durable queue used by the
// modern upload endpoint, the email poller, the folder watcher and manual upload
// Development may opt into a legacy diagnostic path; production fails startup if the
// durable gateway is disabled, because that path has no evidence or usage ledger.
UnifiedDocumentIngestionGuard.Enforce(builder.Environment.IsProduction(),
    builder.Configuration.GetValue("Ingestion:UseUnifiedQueue", true));
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

// Self-service password recovery (Security/PasswordReset): the "forgot password" flow for tenant
// users. Registered AFTER AddTenantOnboarding, which binds the TenantOnboarding section this
// module reads its link path, its window and — crucially — its password floor from. Until it
// existed, a tenant user who forgot their password could only get back in by having somebody with
// database access overwrite their hash, which is the same operator-holds-the-credential defect the
// activation flow was built to end.
builder.Services.AddTenantPasswordReset();

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
// Email inquiry assembly - one capability, composed in one place so the resolution test
// exercises the production composition instead of a copy of it.
builder.Services.AddEmailInquiryAssembly();
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
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialCases.Participation.ILeadParticipationService,
                           ERP_RFQ_Automation.CommercialCases.Participation.LeadParticipationService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialCases.Participation.ILeadDecisionWorkbenchService,
                           ERP_RFQ_Automation.CommercialCases.Participation.LeadDecisionWorkbenchService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialCases.Participation.IRfqRevisionImpactResolutionService,
                           ERP_RFQ_Automation.CommercialCases.Participation.RfqRevisionImpactResolutionService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialCases.Promotion.IRfqPromotionService,
                           ERP_RFQ_Automation.CommercialCases.Promotion.RfqPromotionService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Agent.IAgentTool, ERP_RFQ_Automation.Intelligence.Decision.LeadDecisionBriefTool>();

// WP-A1/A2: tenant-configurable SLA policy reader (SlaPolicy-backed; default 2h).
builder.Services.AddScoped<ERP_RFQ_Automation.MultiTenancy.ISlaPolicyReader,
                           ERP_RFQ_Automation.Sla.SlaPolicyReaderAdapter>();

// ==== SLA & deadline engine + quote outcome capture (Sla/) ====
// After AddNotifications(...) — SlaNotifications depends on IEmailSender.
builder.Services.AddScoped<ERP_RFQ_Automation.Sla.IQuoteOutcomeService, ERP_RFQ_Automation.Sla.QuoteOutcomeService>();
// The lead-stage loss reuses the quote's governed picklist rather than owning a second one.
builder.Services.AddScoped<ERP_RFQ_Automation.Sla.ILeadOutcomeReasons, ERP_RFQ_Automation.Sla.LeadOutcomeReasons>();
builder.Services.AddSingleton<ERP_RFQ_Automation.Sla.ISlaNotifications, ERP_RFQ_Automation.Sla.SlaNotifications>();
builder.Services.AddHostedService<ERP_RFQ_Automation.Sla.SlaSweepWorker>();

// ==== Gate 6 / FR-INV-04: minimum, maximum and reorder alerts (Inventory/) ====
// After the SLA registrations above: the reorder sweep reuses ISlaNotifications as its delivery
// channel and SlaSweepWorker's claim primitives as its send-once ledger, so the two engines cannot
// drift apart on whether a message can be sent twice.
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.IStockLedgerService,
                           ERP_RFQ_Automation.Inventory.StockLedgerService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.IReorderAlertService,
                           ERP_RFQ_Automation.Inventory.ReorderAlertService>();
builder.Services.AddHostedService<ERP_RFQ_Automation.Inventory.ReorderAlertSweepWorker>();

// ==== Gate 8: dashboards, scheduled reporting (Reporting/) ====
builder.Services.AddScoped<ERP_RFQ_Automation.Reporting.IGrossMarginService,
                           ERP_RFQ_Automation.Reporting.GrossMarginService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Reporting.IReportRenderer,
                           ERP_RFQ_Automation.Reporting.ReportRenderer>();
builder.Services.AddScoped<ERP_RFQ_Automation.Reporting.IReportBuilder,
                           ERP_RFQ_Automation.Reporting.ReportBuilder>();
builder.Services.AddScoped<ERP_RFQ_Automation.Reporting.IReportSubscriptionService,
                           ERP_RFQ_Automation.Reporting.ReportSubscriptionService>();
builder.Services.AddSingleton<ERP_RFQ_Automation.Reporting.IReportDelivery,
                              ERP_RFQ_Automation.Reporting.ReportDelivery>();
builder.Services.AddHostedService<ERP_RFQ_Automation.Reporting.ScheduledReportWorker>();

builder.Services.AddScoped<ERP_RFQ_Automation.PlatformGovernance.PlatformGovernanceService>();
builder.Services.AddScoped<ERP_RFQ_Automation.PlatformGovernance.HumanActionService>();
builder.Services.AddScoped<ERP_RFQ_Automation.PlatformGovernance.AiTrustCenterService>();
builder.Services.AddScoped<ERP_RFQ_Automation.PlatformGovernance.CommercialDocumentArchiveService>();
builder.Services.AddScoped<ERP_RFQ_Automation.PlatformGovernance.QualityAnalyticsService>();
// Evidence retention: purge stored BYTES, keep the evidence RECORD. The immutability
// triggers make the tombstone stronger proof than the file it replaces.
builder.Services.AddScoped<ERP_RFQ_Automation.Retention.LegacyAttachmentPurgeResolver>();
builder.Services.AddScoped<ERP_RFQ_Automation.Retention.EvidenceRetentionService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Retention.TenantDataControlService>();

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

// Sec-D1: prove the tenant plane can actually READ the columns that tenant-status enforcement
// and plan limits are resolved from, before serving a request. Runs in every environment that
// has the execution roles (it no-ops where they do not exist), because the failure it catches —
// a deployment cut between the grant-narrowing migration and the migration that granted the
// column a later query started projecting — is a release-ordering accident, not a
// production-only one. Throws, and is deliberately not caught: a process that cannot enforce
// suspension or plan limits must not serve traffic.
await TenantAccessGrantContract.AssertReadableAsync(connectionString, app.Logger);

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
// The body carries the correlation id alongside the generic message, so the one value a user
// can quote back is the one that finds the stack trace (Platform/Hardening/GlobalExceptionResponse).
app.UseExceptionHandler(errApp => errApp.Run(ERP_RFQ_Automation.Platform.Hardening.GlobalExceptionResponse.WriteAsync));

// Baseline security headers (SEC-13). SEC-G9 adds the Content-Security-Policy this set was
// missing; the policy itself and the reasoning behind every directive live in
// Infrastructure/TransportSecurityPolicy.cs. Set here rather than per-endpoint so it covers
// EVERY response, including the ones a future middleware registration starts producing —
// which is the whole point, given three writers store user-supplied files under the web root.
var contentSecurityPolicy = TransportSecurityPolicy.ContentSecurityPolicyFor(app.Environment);
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Content-Security-Policy"] = contentSecurityPolicy;
    await next();
});

// SEC-H6: must run before ANY middleware that reads the client IP or scheme — in
// particular UsePlatformObservability and UseRateLimiter further down.
app.UseForwardedHeaders();
// The platform network boundary consumes only the trusted, normalized RemoteIpAddress
// produced above. It never parses X-Forwarded-For itself, so an untrusted direct caller
// cannot spoof an allow-listed address. Production must explicitly choose AllowList or Any.
app.UseMiddleware<PlatformNetworkAccessMiddleware>();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
// SEC-G9: transport security — HTTPS redirection and HSTS, which were absent.
//
// WHAT UseForwardedHeaders ACTUALLY DOES HERE — measured, and the opposite of the intuitive
// reading. TLS is terminated at the hosting edge, so the socket is always plain HTTP. The
// process learns the real scheme from X-Forwarded-Proto via UseForwardedHeaders (above, and
// deliberately EARLIER in the pipeline than this line so the rewrite has already happened).
// It looks as though that rewrite could not occur, because Program.cs clears
// KnownProxies/KnownNetworks and no environment repopulates them — appsettings.json has no
// ForwardedHeaders section and render.yaml states the edge ranges "have not yet been supplied".
// It occurs anyway: ForwardedHeadersMiddleware runs its known-address check ONLY when at least
// one entry exists, so an empty pair trusts EVERY caller. Request.Scheme is therefore rewritten
// today, from anyone — which is precisely the spoofing exposure the SEC-H6 note above warns of
// for X-Forwarded-For. ForwardedHeadersBehaviourTests pins all of this.
//
// The consequence that shapes this block: the middleware CONSUMES X-Forwarded-Proto, so nothing
// downstream can read it. A redirect keyed on that header can never fire — a control that looks
// configured and does nothing. The decision is keyed on X-Original-Proto instead, which
// ForwardedHeadersMiddleware writes exactly when it rewrote the scheme.
//
// WHY NOT THE FRAMEWORK PAIR. UseHttpsRedirection redirects any request whose scheme is not
// https. Behind an edge that terminates TLS and does NOT label the scheme, that is every
// request, each answered with a 307 the edge forwards straight back — an infinite redirect.
// UseLoopSafeHttpsRedirection redirects only a request KNOWN to be plain: a configured trusted
// edge makes the scheme authoritative, or X-Original-Proto shows an edge labelled it. Where
// neither holds the scheme is genuinely unknowable and the request is SERVED. That pass-through
// is the loop guard and is what makes ON-by-default safe. Nothing about SEC-H6 is weakened: this
// middleware reads forwarding headers only to choose between redirect and serve, while the
// client address the rate limiter and PlatformNetworkAccessMiddleware depend on still comes
// solely from the normalized RemoteIpAddress.
//
// HSTS rides the same request-is-secure test rather than UseHsts, so it cannot end up as a
// header that is configured and never emitted. Development is excluded outright; the local
// console and the E2E harness both drive http://127.0.0.1 against a host with no certificate.
//
// STILL OWED BY THE DEPLOYMENT, and it is configuration rather than code: set
// ForwardedHeaders:KnownProxies/:KnownNetworks to the edge's ranges. Until then any caller can
// spoof X-Forwarded-For, so the rate limiter's per-IP partition and the platform network
// boundary are working from an attacker-supplied address — a pre-existing SEC-H6 gap this gate
// did not create and cannot close from code.
var httpsRedirectionEnabled =
    TransportSecurityPolicy.ShouldRedirectToHttps(app.Environment, app.Configuration);
var forwardedSchemeIsTrusted = TransportSecurityPolicy.ForwardedProtoIsTrusted(app.Configuration);
if (httpsRedirectionEnabled)
{
    app.UseLoopSafeHttpsRedirection(TransportSecurityPolicy.HstsHeaderValue, forwardedSchemeIsTrusted);
}
if (!app.Environment.IsDevelopment())
{
    app.Logger.LogInformation(
        "Transport security: HTTPS redirection and HSTS {State}; forwarded scheme is {Trust}. " +
        "Set ForwardedHeaders:KnownProxies or :KnownNetworks to the edge ranges to make the scheme " +
        "authoritative, or {Key} to override the first value.",
        httpsRedirectionEnabled ? "ENABLED" : "DISABLED",
        forwardedSchemeIsTrusted ? "TRUSTED" : "UNTRUSTED (only requests labelled X-Forwarded-Proto are redirected)",
        TransportSecurityPolicy.HttpsRedirectionEnabledKey);
}
// DELIBERATELY ABSENT: app.UseStaticFiles(). ProductRepository.PersistAttachmentAsync,
// CustomerController and UserController all write user-supplied bytes under WebRootPath with
// the uploaded extension preserved, and .html is on DocumentIntakeAllowList — so that one line
// would publish stored HTML on the API origin, unauthenticated, and the frontend keeps its JWT
// in localStorage. The Content-Security-Policy set above is the second line of defence if it is
// ever added; do not treat it as permission to add it.
// Use CORS
app.UseCors("DefaultCors");
app.UseAuthentication();
app.UseReadOnlyImpersonationGuard();
// Tenant + correlation-id logging scope — AFTER auth so the businessUnitId claim exists.
app.UsePlatformObservability();
// FR-MDM-05 / E44: publishes the authenticated caller as the ambient actor for master-data audit
// rows. AFTER UseAuthentication for the same reason as the line above — before it, User carries no
// claims and every audit row would be attributed to "system".
app.UseMasterDataAuditActor();
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
// /ready names the checks that failed and why. The default writer emits the single word
// "Unhealthy": on 2026-08-24 the deployment had been 503-ing for the life of the process and the
// two failing check names had to be recovered from the Render log stream, which is exactly the
// second system a probe exists to make unnecessary. The writer excludes exceptions and each
// check's Data bag, and redacts addresses and secret-shaped key/value pairs out of the
// descriptions — see HealthReportResponseWriter for why each of those is excluded rather than
// filtered case by case.
app.MapHealthChecks("/ready", new Microsoft.AspNetCore.Diagnostics.HealthChecks.HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = ERP_RFQ_Automation.HealthChecks.HealthReportResponseWriter.WriteAsync
}).AllowAnonymous();
// Deployment identity is deliberately public and narrowly bounded. Render supplies
// RENDER_GIT_COMMIT; exposing that value lets operators prove which immutable revision is
// answering before accepting a queue reconciliation or schema-sensitive rollout. Never add
// configuration values, connection details, migration names, or tenant state to this response.
app.MapGet("/build-identity", (IHostEnvironment environment) =>
    Results.Ok(ERP_RFQ_Automation.Infrastructure.BuildIdentity.Current(environment)))
    .AllowAnonymous();
// Zero-dependency Prometheus scrape endpoint (Platform/Hardening). Self-disabling: the
// call is a no-op unless ObservabilityExtensions.SelectExporter enabled it, which by
// default happens exactly when no OTLP collector is configured — so the process can
// never again register a meter and export nothing. Carries its own X-Scrape-Key check.
app.MapNexoraMetricsScrape();

app.Run();

public partial class Program;
