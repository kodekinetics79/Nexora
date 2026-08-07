using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Lifecycle;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A real service provider over a real SQLite database, wired the way Program.cs wires the
/// background path: a scoped <see cref="ErpRfqAutomationContext"/>, the real
/// <see cref="TenantAccessService"/> over a real <see cref="IMemoryCache"/>, and the real
/// <see cref="TenantWorkGate"/> on top.
///
/// <para>Nothing about the gate is faked. A stubbed <c>ITenantWorkGate</c> would prove only that
/// each worker calls a method — the thing actually worth proving is that a <c>Tenant</c> row with
/// <c>Status = Suspended</c> stops work reaching the tenant, through the same resolution path
/// production uses, including its ~60s cache.</para>
/// </summary>
public sealed class TenantWorkGateHarness : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _provider;

    public TenantWorkGateHarness(bool registerGate = true, Action<IServiceCollection>? configure = null)
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
        services.AddScoped<ITenantContext>(sp =>
            new BackgroundTenantContext(sp.GetRequiredService<ITenantScopeAccessor>()));
        services.AddDbContext<ErpRfqAutomationContext>(options => options.UseSqlite(_connection));
        services.AddScoped<ITenantAccessService, TenantAccessService>();

        // The real stores, because what is under test is which ROWS a worker claims. A faked store
        // would move the assertion off the thing that leaks.
        services.AddScoped<ERP_RFQ_Automation.QuoteDelivery.IQuoteDeliveryStore,
            ERP_RFQ_Automation.QuoteDelivery.QuoteDeliveryStore>();
        services.AddScoped<ERP_RFQ_Automation.CommercialFinance.IFinanceOutboxStore,
            ERP_RFQ_Automation.CommercialFinance.FinanceOutboxStore>();

        // The sender is the one thing that must NOT be real: it would put a quote PDF on a wire.
        // It fails before the external send, which drives the store's FailAsync path and leaves
        // AttemptCount as the observable proof that the row was claimed at all.
        services.AddScoped<ERP_RFQ_Automation.QuoteDelivery.IQuoteDeliverySender, RefusingQuoteSender>();

        // Absent registration is the "deployment without the lifecycle module" case: every worker
        // resolves the gate with GetService and carries on unfiltered when it is not there.
        if (registerGate) services.AddScoped<ITenantWorkGate, TenantWorkGate>();

        configure?.Invoke(services);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            // The defect this catches is the one that made the first version of these edits wrong:
            // four of the seven workers are hosted singletons, and a constructor dependency on the
            // scoped gate would have thrown here at startup rather than in a test.
            ValidateScopes = true,
            ValidateOnBuild = true
        });

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>().Database.EnsureCreated();
    }

    public IServiceProvider Services => _provider;

    public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

    public ITenantScopeAccessor TenantScope => _provider.GetRequiredService<ITenantScopeAccessor>();

    /// <summary>A context outside any DI scope, for seeding and assertions.</summary>
    public ErpRfqAutomationContext Context()
    {
        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseSqlite(_connection).Options;
        return new ErpRfqAutomationContext(options, new StubTenant(null));
    }

    /// <summary>
    /// A business unit, and the platform tenant that owns it.
    ///
    /// <para><paramref name="status"/> null seeds NO tenant row at all — the legacy business unit
    /// the fail-open contract exists for, and the case a background sweep must keep serving.</para>
    /// </summary>
    public async Task SeedTenantAsync(long businessUnitId, TenantStatus? status, string slug)
    {
        await using var db = Context();
        if (await db.BusinessUnits.FindAsync(businessUnitId) is null)
            Seed.BusinessUnit(db, businessUnitId);

        if (status is TenantStatus resolved)
            db.Set<Tenant>().Add(new Tenant
            {
                Name = $"Gate {slug}",
                Slug = slug,
                Status = resolved,
                PrimaryBusinessUnitId = businessUnitId,
                CreatedBy = "tests",
                CreatedOn = DateTime.UtcNow
            });

        await db.SaveChangesAsync();
        Evict(businessUnitId);
    }

    /// <summary>
    /// Drops the ~60s cached snapshot, the way every lifecycle mutation does. Without it a test
    /// that changes a tenant's status would keep reading the answer from before the change.
    /// </summary>
    public void Evict(long businessUnitId) =>
        _provider.GetRequiredService<IMemoryCache>().Remove(TenantAccessService.CacheKey(businessUnitId));

    public async Task SetStatusAsync(long businessUnitId, TenantStatus status)
    {
        await using var db = Context();
        var tenant = await db.Set<Tenant>().IgnoreQueryFilters()
            .SingleAsync(t => t.PrimaryBusinessUnitId == businessUnitId);
        tenant.Status = status;
        await db.SaveChangesAsync();
        Evict(businessUnitId);
    }

    public ITenantWorkGate Gate(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ITenantWorkGate>();

    public void Dispose()
    {
        _provider.Dispose();
        _connection.Dispose();
    }

    /// <summary>
    /// The tenant context a background worker gets: no HttpContext, so the scope is whatever
    /// <see cref="ITenantScopeAccessor.Push"/> last set. Mirrors <c>HttpTenantContext</c>'s
    /// fallback without dragging <c>IHttpContextAccessor</c> into the harness.
    /// </summary>
    private sealed class BackgroundTenantContext(ITenantScopeAccessor scope) : ITenantContext
    {
        public long? BusinessUnitId => scope.BusinessUnitId;
    }

    /// <summary>
    /// Fails before the external send, so a claimed delivery takes the store's FailAsync path and
    /// never reaches SMTP or the quote PDF generator. Sending for real is exactly the behaviour
    /// these tests exist to prove does not happen for a suspended tenant.
    /// </summary>
    private sealed class RefusingQuoteSender : ERP_RFQ_Automation.QuoteDelivery.IQuoteDeliverySender
    {
        public Task SendAsync(ERP_RFQ_Automation.QuoteDelivery.QuoteDeliveryEnvelope request, CancellationToken ct)
            => throw new ERP_RFQ_Automation.QuoteDelivery.QuoteDeliveryPreSendException(
                "TestHarnessRefusal", new NotSupportedException());
    }
}

/// <summary>Content root for <see cref="ERP_RFQ_Automation.Services.EmailService"/>, which builds
/// its tessdata path from it at construction.</summary>
public sealed class TenantWorkGateEnvironment(string root) : Microsoft.AspNetCore.Hosting.IWebHostEnvironment
{
    public string WebRootPath { get; set; } = root;
    public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string ApplicationName { get; set; } = "tests";
    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
    public string ContentRootPath { get; set; } = root;
    public string EnvironmentName { get; set; } = "Development";
}

/// <summary>Storage rooted at a scratch directory. Only <see cref="GetPath"/> is exercised — the
/// email service creates its attachment and raw-mail folders at construction.</summary>
public sealed class TenantWorkGateStorage(string root) : ERP_RFQ_Automation.Infrastructure.Storage.IFileStorage
{
    public string RootPath => root;
    public string ResolvePath(string storagePath) => Path.Combine(root, storagePath);
    public string GetPath(params string[] segments) => Path.Combine(new[] { root }.Concat(segments).ToArray());
    public Task<string> WriteImmutableAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
        => throw new NotSupportedException();
    public Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default)
        => throw new NotSupportedException();
}
