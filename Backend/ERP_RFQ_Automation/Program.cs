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
var builder = WebApplication.CreateBuilder(args);
// Add services to the container.
builder.Services.AddControllers();
// Add DbContext with SQL Server
builder.Services.AddDbContext<ErpRfqAutomationContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));
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
    client.BaseAddress = new Uri("http://localhost:11434/");
});

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNameCaseInsensitive = true;
    });

// Enable CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policy =>
        {
            policy.AllowAnyOrigin()
                  .AllowAnyMethod()
                  .AllowAnyHeader();
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
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? ""))
    };
});
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
var app = builder.Build();
// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
//app.UseHttpsRedirection();
// Use CORS
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.Run();
