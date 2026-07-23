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
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Hardening;
using ERP_RFQ_Automation.Notifications;
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
using ERP_RFQ_Automation.Security;
using System.Text.Json.Serialization;
using Npgsql;

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

// Platform-Owner control-plane services (ADR-0005)
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Auth.IPlatformAuthService, ERP_RFQ_Automation.Platform.Auth.PlatformAuthService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Platform.Services.IPlatformAuditService, ERP_RFQ_Automation.Platform.Services.PlatformAuditService>();

// Readiness/liveness health checks (DATA-05)
builder.Services.AddHealthChecks()
    .AddCheck<ERP_RFQ_Automation.HealthChecks.DatabaseHealthCheck>("database");
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
builder.Services.AddScoped<IGeneralDropdownRepository, GeneralDropdownRepository>();
builder.Services.AddScoped<ICustomerRepository, CustomerRepository>();
builder.Services.AddScoped<ISupplierRepository, SupplierRepository>();
builder.Services.AddScoped<IContactRepository, ContactRepository>();
builder.Services.AddScoped<ILeadRepository, LeadRepository>();
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
builder.Services.AddScoped<IQuoteService, QuoteService>();
builder.Services.AddScoped<IOrderRepository, OrderRepository>();
builder.Services.AddScoped<IOrderService, OrderService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialFinance.ICommercialFinanceApplicationService, ERP_RFQ_Automation.CommercialFinance.CommercialFinanceApplicationService>();
builder.Services.AddScoped<ERP_RFQ_Automation.CommercialFinance.IReceivablesOperationsService, ERP_RFQ_Automation.CommercialFinance.ReceivablesOperationsService>();
builder.Services.AddScoped<ICustomerAwardApplicationService, CustomerAwardApplicationService>();
builder.Services.AddScoped<IShipmentRepository, ShipmentRepository>();
builder.Services.AddScoped<IQuoteConfigurationRepository, QuoteConfigurationRepository>();
builder.Services.AddScoped<IDashboardRepository, DashboardRepository>();
builder.Services.AddScoped<ICommercialCaseQueryService, CommercialCaseQueryService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.IInventoryAvailabilityService, ERP_RFQ_Automation.Inventory.InventoryAvailabilityService>();
builder.Services.AddScoped<ERP_RFQ_Automation.Inventory.IOrderStockReservationService, ERP_RFQ_Automation.Inventory.OrderStockReservationService>();
builder.Services.AddScoped<ILifecycleApplicationService, LifecycleApplicationService>();
builder.Services.AddScoped<ILifecycleOutboxStore, LifecycleOutboxStore>();
builder.Services.AddCommercialFinanceOutboxDispatcher(builder.Configuration);
builder.Services.AddScoped<ICommercialRoutingApplicationService, CommercialRoutingApplicationService>();
builder.Services.AddScoped<ICustomFieldApplicationService, CustomFieldApplicationService>();
builder.Services.AddSingleton<DeterministicRoutingEngine>();
builder.Services.AddSingleton(new RoutingPolicy());
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
builder.Services.AddSingleton<TenantSmtpConcurrencyGate>();
builder.Services.AddSingleton<IOutboundSmtpTransport, MailKitOutboundSmtpTransport>();
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
builder.Services.AddSingleton<IAiGovernanceService, AiGovernanceService>();
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
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
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
    WorkerCount = 4,
    MaxConcurrentLlmCalls = 8,
    PerTenantConcurrencyCap = 4,
    LeaseDuration = TimeSpan.FromMinutes(5),
    IdlePollDelay = TimeSpan.FromSeconds(2)
});
builder.Services.AddScoped<IExtractionQueue, ExtractionQueue>();
builder.Services.AddScoped<IChunkedExtractionService, ChunkedExtractionService>();
builder.Services.AddScoped<ILeadPersister, LeadPersister>();
builder.Services.AddScoped<IExtractionDocumentReader, ProductionDocumentReader>();
builder.Services.AddHostedService<ExtractionWorker>();
// ING-05: unified ingestion gateway — the ONE door to the durable queue used by the
// modern upload endpoint, the email poller, the folder watcher and manual upload
// (each door still honours Ingestion:UseUnifiedQueue, default true).
builder.Services.AddScoped<IDocumentIngestion, DocumentIngestionService>();

// Transactional email / notifications (Notifications/): provider-agnostic sender
// (defaults to the safe console provider until an SMTP/SendGrid provider is
// configured). Powers RFQ→quote→order communications.
builder.Services.AddNotifications(builder.Configuration);

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

var app = builder.Build();

// Production defaults to applying migrations before serving traffic. This guarantees
// tenant-role/RLS policy installation is atomic with the application rollout. Set
// Database:ApplyMigrationsOnStartup=false only when an external release job owns it.
var applyMigrations = app.Configuration.GetValue<bool?>("Database:ApplyMigrationsOnStartup")
    ?? app.Environment.IsProduction();
var migrationConnection = app.Configuration.GetConnectionString("MigrationConnection")
    ?? ResolveDirectMigrationConnection(connectionString);
if (applyMigrations)
{
    var migrationOptions = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
        .UseNpgsql(migrationConnection, npgsql =>
        {
            npgsql.EnableRetryOnFailure(5, TimeSpan.FromSeconds(10), null);
            npgsql.CommandTimeout(120);
        })
        .Options;
    await using var migrationDb = new ErpRfqAutomationContext(migrationOptions);
    await migrationDb.Database.MigrateAsync();
}
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
               AND NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'nexora_ai_maintenance')
        FROM pg_roles runtime_role
        WHERE runtime_role.rolname = current_user;
        """;
    if (await command.ExecuteScalarAsync() is not true)
        throw new InvalidOperationException(
            "The runtime database role must be NOINHERIT, non-superuser, non-BYPASSRLS, a member of nexora_tenant_app, and have no AI maintenance bypass role.");
}

await DemoUserSeeder.EnsureAsync(app.Services, app.Configuration, app.Environment);

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
app.UseAuthorization();
// Built-in rate limiter — AFTER auth so the per-tenant partition uses the claim.
app.UseRateLimiter();
app.MapControllers();
app.MapHealthChecks("/health");
app.Run();
