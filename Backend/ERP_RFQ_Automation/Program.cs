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
using System.Text.Json.Serialization;

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
builder.Services.AddDbContext<ErpRfqAutomationContext>(options =>
    options.UseNpgsql(connectionString, npgsql =>
    {
        npgsql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorCodesToAdd: null);
        npgsql.CommandTimeout(60);
    }));

// Per-request tenant scope for EF global query filters (ADR-0005 tenant isolation).
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ERP_RFQ_Automation.MultiTenancy.ITenantContext, ERP_RFQ_Automation.MultiTenancy.HttpTenantContext>();

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
builder.Services.AddScoped<IShipmentRepository, ShipmentRepository>();
builder.Services.AddScoped<IQuoteConfigurationRepository, QuoteConfigurationRepository>();
builder.Services.AddScoped<IDashboardRepository, DashboardRepository>();
// RBAC Authorization
builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();

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
builder.Services.AddHttpClient<OllamaLlmService>(client =>
{
    // DATA-06: honor the configured provider URL instead of a hardcoded localhost.
    var ollamaBaseUrl = builder.Configuration["Ollama:BaseUrl"];
    client.BaseAddress = new Uri(string.IsNullOrWhiteSpace(ollamaBaseUrl)
        ? "http://localhost:11434/"
        : ollamaBaseUrl);
});

// CORS restricted to configured frontend origins (SEC-13). AllowAnyOrigin is
// unsafe for a system with authenticated, tenant-scoped data. Set
// "Cors:AllowedOrigins" in configuration for the pilot; falls back to local dev.
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
builder.Services.AddCors(options =>
{
    options.AddPolicy("DefaultCors", policy =>
    {
        if (corsOrigins is { Length: > 0 })
        {
            policy.WithOrigins(corsOrigins).AllowAnyMethod().AllowAnyHeader().AllowCredentials();
        }
        else
        {
            // Fallback origins: local dev + the deployed Vercel frontend. In production,
            // set Cors:AllowedOrigins (env: Cors__AllowedOrigins__0, __1, ...) to the exact
            // frontend URL(s) instead of relying on this list.
            policy.WithOrigins(
                    "http://localhost:5173", "http://localhost:4173", "http://localhost:3000",
                    "https://nexora-ai-beryl.vercel.app")
                  .AllowAnyMethod().AllowAnyHeader();
        }
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
// Tenant + correlation-id logging scope — AFTER auth so the businessUnitId claim exists.
app.UsePlatformObservability();
app.UseAuthorization();
// Built-in rate limiter — AFTER auth so the per-tenant partition uses the claim.
app.UseRateLimiter();
app.MapControllers();
app.MapHealthChecks("/health");
app.Run();
