using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Splices the offboarding entities into <see cref="ErpRfqAutomationContext"/> for the duration of
/// a test.
///
/// <para>The production splice is one line in <c>ErpRfqAutomationContext.Tenancy.cs</c>
/// (<c>modelBuilder.ApplyTenantLifecycleModel()</c>), owned by whoever owns the context and its
/// migrations. Replacing <see cref="IModelCustomizer"/> reaches the same model through the options
/// instead, so this suite exercises the REAL extension against the REAL context without the tests
/// having to wait on — or silently diverge from — that splice. When the splice lands, calling the
/// extension twice is a no-op: <c>modelBuilder.Entity&lt;T&gt;()</c> is idempotent.</para>
/// </summary>
public sealed class TenantLifecycleModelCustomizer(ModelCustomizerDependencies dependencies)
    : RelationalModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder modelBuilder, DbContext context)
    {
        base.Customize(modelBuilder, context);
        modelBuilder.ApplyTenantLifecycleModel();
    }
}

/// <summary>
/// SQLite over the real model plus the offboarding tables. Same contract as
/// <see cref="TestDb"/> — one open in-memory connection shared by every context — so
/// "seed with one context, assert with another" behaves the same way.
/// </summary>
public sealed class TenantLifecycleTestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<ErpRfqAutomationContext> _options;

    public TenantLifecycleTestDb()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, TenantLifecycleModelCustomizer>()
            .EnableSensitiveDataLogging()
            .Options;

        using var ctx = ContextFor(null);
        ctx.Database.EnsureCreated();
    }

    public ErpRfqAutomationContext ContextFor(long? businessUnitId)
        => new(_options, new StubTenant(businessUnitId));

    public void Dispose() => _connection.Dispose();
}

/// <summary>
/// Shared construction for the offboarding service, so every test builds it the same way and a
/// change to its dependencies shows up in one place.
/// </summary>
public static class TenantLifecycleHarness
{
    /// <summary>
    /// A connection string that fails to connect INSTANTLY (port 1, connection refused) rather
    /// than after a timeout. Used by the tests that drive a purge to its failure path on the
    /// portable provider, where the owner connection the purge needs does not exist.
    /// </summary>
    public const string UnreachableOwnerConnection =
        "Host=127.0.0.1;Port=1;Database=x;Username=x;Password=x;Timeout=1;Command Timeout=1";

    /// <summary>
    /// A context over the shared PostgreSQL container that also knows the offboarding entities.
    /// <c>PostgreSqlTestDatabase.ContextFor</c> builds its options without the model customizer,
    /// so it cannot see them.
    /// </summary>
    public static ErpRfqAutomationContext PostgresContext(string connectionString) =>
        new(new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseNpgsql(connectionString)
            .ReplaceService<IModelCustomizer, TenantLifecycleModelCustomizer>()
            .EnableDetailedErrors()
            .Options,
            new StubTenant(null));

    public static TenantPurgeExecutor PurgeExecutor(string ownerConnectionString) =>
        new(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MigrationConnection"] = ownerConnectionString
        }).Build(), NullLogger<TenantPurgeExecutor>.Instance);

    public static TenantOffboardingService Service(
        ErpRfqAutomationContext context,
        string? ownerConnectionString = null,
        TenantLifecycleOptions? options = null,
        ITenantAccessService? tenantAccess = null,
        TimeProvider? timeProvider = null,
        ITenantOffboardingReadinessService? readiness = null)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:MigrationConnection"] = ownerConnectionString ?? UnreachableOwnerConnection,
            ["ConnectionStrings:DefaultConnection"] = ownerConnectionString ?? UnreachableOwnerConnection,
        }).Build();

        return new TenantOffboardingService(
            context,
            new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance),
            new TenantPurgeExecutor(configuration, NullLogger<TenantPurgeExecutor>.Instance),
            new TenantPersonalDataEraser(context, NullLogger<TenantPersonalDataEraser>.Instance),
            new TenantDataExportService(context, configuration, NullLogger<TenantDataExportService>.Instance),
            Options.Create(options ?? new TenantLifecycleOptions()),
            NullLogger<TenantOffboardingService>.Instance,
            readiness ?? AlwaysReadyOffboarding.Instance,
            tenantAccess,
            timeProvider: timeProvider);
    }

    /// <summary>An authenticated platform operator. The <c>sub</c> claim must be positive or
    /// <see cref="PlatformAuditService"/> refuses to write a success record at all.</summary>
    public static ClaimsPrincipal Operator(string email = "operator@example.test", long id = 7) =>
        new(new ClaimsIdentity(
        [
            new Claim("sub", id.ToString()),
            new Claim("email", email)
        ], "Platform"));

    public static Tenant NewTenant(string slug, TenantStatus status, long? primaryBusinessUnitId = null) => new()
    {
        Name = $"Offboarding {slug}",
        Slug = slug,
        Status = status,
        PrimaryBusinessUnitId = primaryBusinessUnitId,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };

    public static async Task<Tenant> SeedTenantAsync(
        TenantLifecycleTestDb db, string slug, TenantStatus status, long? primaryBusinessUnitId = null)
    {
        await using var seed = db.ContextFor(null);
        var tenant = NewTenant(slug, status, primaryBusinessUnitId);
        seed.Set<Tenant>().Add(tenant);
        await seed.SaveChangesAsync();
        return tenant;
    }

    /// <summary>
    /// Advances the isolated application's clock beyond the minimum retention window. The stored
    /// schedule is never edited, so tests exercise the same predicate production uses.
    /// </summary>
    public static void ElapseRetentionWindow(MutableTimeProvider clock) =>
        clock.Advance(TimeSpan.FromDays(31));

    /// <summary>Installs an isolated mutable clock in a WebApplicationFactory test host.</summary>
    public static MutableTimeProvider RegisterMutableClock(
        IServiceCollection services, DateTimeOffset? initial = null)
    {
        var clock = new MutableTimeProvider(initial);
        services.RemoveAll<TimeProvider>();
        services.AddSingleton<TimeProvider>(clock);
        return clock;
    }

    public sealed class MutableTimeProvider(DateTimeOffset? initial = null) : TimeProvider
    {
        private DateTimeOffset _utcNow = initial ?? DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan amount)
        {
            if (amount <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(amount));
            _utcNow = _utcNow.Add(amount);
        }
    }

    private sealed class AlwaysReadyOffboarding : ITenantOffboardingReadinessService
    {
        public static readonly AlwaysReadyOffboarding Instance = new();
        public Task<TenantOffboardingReadinessResult> AssessAsync(
            Tenant tenant, TenantOffboardingReadinessPhase phase, CancellationToken ct = default) =>
            Task.FromResult(new TenantOffboardingReadinessResult(true, []));
    }
}

/// <summary>
/// Creates the offboarding tables on the shared PostgreSQL container.
///
/// <para>The migration that adds them is owned by the schema owner and does not exist yet, so the
/// tables are created from the SAME <c>ApplyTenantLifecycleModel</c> configuration the migration
/// will be generated from. Hand-written DDL here would be a second definition free to drift from
/// the first, and the drift would surface as a green suite over a schema production does not have.</para>
/// </summary>
public static class TenantLifecycleSchema
{
    private sealed class SchemaContext(DbContextOptions<SchemaContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.ApplyTenantLifecycleModel();
    }

    public static async Task EnsureAsync(string connectionString)
    {
        var options = new DbContextOptionsBuilder<SchemaContext>()
            .UseNpgsql(connectionString)
            .Options;

        await using var context = new SchemaContext(options);

        // Idempotent: the collection fixture is shared across test classes and this runs from
        // each of their constructors.
        var exists = await context.Database
            .SqlQueryRaw<bool>("""SELECT to_regclass('platform."TenantOffboardings"') IS NOT NULL AS "Value" """)
            .SingleAsync();
        if (exists) return;

        await context.GetService<IRelationalDatabaseCreator>().CreateTablesAsync();
    }
}
