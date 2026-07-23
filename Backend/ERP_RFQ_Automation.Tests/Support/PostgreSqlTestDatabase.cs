using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("nexora_tests")
        .WithUsername("nexora")
        .WithPassword("nexora-tests")
        .Build();

    private DbContextOptions<ErpRfqAutomationContext> _options = null!;

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

        await using var context = ContextFor(null);
        await context.Database.MigrateAsync();
    }

    public ErpRfqAutomationContext ContextFor(long? businessUnitId)
        => new(_options, new StubTenant(businessUnitId));

    public async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(_container.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
