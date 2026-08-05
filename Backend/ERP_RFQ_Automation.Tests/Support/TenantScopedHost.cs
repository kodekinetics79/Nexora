using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ERP_RFQ_Automation.Tests.Support;

/// <summary>
/// A DI container whose <see cref="ErpRfqAutomationContext"/> resolves its tenant from the
/// ambient <see cref="ITenantScopeAccessor"/> — the shape a background worker actually runs in.
///
/// <see cref="TestDb"/> hands out contexts bound to a FIXED tenant, which cannot express
/// "the worker pushed a scope and every service resolved inside it saw that scope". Anything
/// exercising <see cref="ITenantScopeAccessor.Push"/> (SLA sweep, quote delivery dispatch) needs
/// this instead. The SQLite connection is shared, so a context taken via
/// <see cref="ContextFor"/> for seeding sees the same database the scoped contexts do.
/// </summary>
public sealed class TenantScopedHost : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public TenantScopedHost(Action<IServiceCollection>? configure = null)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
        services.AddScoped<ITenantContext, ScopeOnlyTenantContext>();
        services.AddScoped(sp => new ErpRfqAutomationContext(Options(), sp.GetRequiredService<ITenantContext>()));
        configure?.Invoke(services);
        _provider = services.BuildServiceProvider();

        using var seedScope = _provider.CreateScope();
        seedScope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>().Database.EnsureCreated();
    }

    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();
    public ITenantScopeAccessor TenantScope => _provider.GetRequiredService<ITenantScopeAccessor>();

    /// <summary>A context bound to an explicit tenant (null = unfiltered), for seeding/asserting.</summary>
    public ErpRfqAutomationContext ContextFor(long? businessUnitId)
        => new(Options(), new StubTenant(businessUnitId));

    private DbContextOptions<ErpRfqAutomationContext> Options()
        => new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseSqlite(_connection)
            .EnableSensitiveDataLogging()
            .Options;

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The worker equivalent of <c>HttpTenantContext</c>: no HTTP claim, so the ambient pushed
    /// scope IS the tenant. Read once at construction, exactly like the HTTP implementation.
    /// </summary>
    private sealed class ScopeOnlyTenantContext(ITenantScopeAccessor scope) : ITenantContext
    {
        public long? BusinessUnitId { get; } = scope.BusinessUnitId;
    }
}
