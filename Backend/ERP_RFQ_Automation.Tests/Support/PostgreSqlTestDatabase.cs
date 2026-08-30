using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Security.Claims;
using Testcontainers.PostgreSql;

namespace ERP_RFQ_Automation.Tests.Support;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgreSqlIntegrationCollection : ICollectionFixture<PostgreSqlTestDatabase>
{
    public const string Name = "PostgreSQL integration";
}

/// <summary>
/// One disposable PostgreSQL instance for production-dialect certification. Tests in
/// the collection are serialized and must clean up only the rows they create.
/// </summary>
public sealed class PostgreSqlTestDatabase : IAsyncLifetime
{
    public const string AuditActorSecret = "postgres-audit-actor-secret-at-least-32-bytes";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("nexora_tests")
        .WithUsername("nexora")
        .WithPassword("nexora-tests")
        .Build();

    private DbContextOptions<ErpRfqAutomationContext> _options = null!;
    private string _rlsConnectionString = null!;

    public async Task InitializeAsync()
    {
        // Program.cs sets this before building the production Npgsql model. The test
        // host must do the same or EF compares two different timestamp models and
        // reports a false pending-migration drift.
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        await _container.StartAsync();
        _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_container.GetConnectionString())
            .EnableDetailedErrors()
            .Options;
        var rlsConnection = new NpgsqlConnectionStringBuilder(_container.GetConnectionString())
        {
            ApplicationName = "NexoraRlsTests",
            MaxPoolSize = 1
        };
        _rlsConnectionString = rlsConnection.ConnectionString;

        await using var context = ContextFor(null);
        await context.Database.MigrateAsync();
        await context.Database.ExecuteSqlRawAsync("""
            INSERT INTO public."FinanceProviderSecrets" ("Name", "Secret", "UpdatedOn")
            VALUES ('AuditActor', {0}, now())
            ON CONFLICT ("Name") DO UPDATE
            SET "Secret" = EXCLUDED."Secret", "UpdatedOn" = EXCLUDED."UpdatedOn"
            """, AuditActorSecret);
    }

    public ErpRfqAutomationContext ContextFor(long? businessUnitId)
        => new(_options, new StubTenant(businessUnitId));

    public ErpRfqAutomationContext ContextForConnectionString(string connectionString, long? businessUnitId)
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(connectionString)
            .EnableDetailedErrors()
            .Options;
        return new ErpRfqAutomationContext(options, new StubTenant(businessUnitId));
    }

    public ErpRfqAutomationContext TenantContextWithRls(long businessUnitId)
    {
        var tenant = new StubTenant(businessUnitId);
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_rlsConnectionString)
            .AddInterceptors(new TenantRlsCommandInterceptor(tenant))
            .EnableDetailedErrors()
            .Options;
        return new ErpRfqAutomationContext(options, tenant);
    }

    /// <summary>
    /// A tenant application context with the same signed actor envelope used by production HTTP
    /// commands. The actor is explicit because one integration test often models several
    /// independent requests; request parameters never establish database tenant authority.
    /// </summary>
    public ErpRfqAutomationContext TenantApplicationContext(long businessUnitId, string actor)
    {
        var tenant = new StubTenant(businessUnitId);
        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, actor)], "PostgreSqlIntegration"))
            }
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CommercialFinance:AuditActorSecret"] = AuditActorSecret
            })
            .Build();
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(_rlsConnectionString)
            .AddInterceptors(new TenantRlsCommandInterceptor(tenant, http, configuration))
            .EnableDetailedErrors()
            .Options;
        return new ErpRfqAutomationContext(options, tenant);
    }

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    public string ConnectionString => _container.GetConnectionString();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
