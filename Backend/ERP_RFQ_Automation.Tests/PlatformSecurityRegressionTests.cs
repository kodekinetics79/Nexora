using System.Data.Common;
using System.Security.Claims;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Controllers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

public class PlatformSecurityRegressionTests
{
    private const string TenantKey = "tenant-signing-key-that-is-at-least-32-bytes";
    private const string PlatformKey = "platform-signing-key-that-is-at-least-32-bytes";

    [Fact]
    public void Production_requires_a_platform_signing_key_without_tenant_key_fallback()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Jwt:Key"] = TenantKey
        });

        var error = Assert.Throws<InvalidOperationException>(() => RegisterPlatformAuthentication(configuration));

        Assert.Contains("required outside Development and Testing", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_requires_platform_and_tenant_signing_keys_to_be_distinct()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Jwt:Key"] = TenantKey,
            ["Jwt:PlatformKey"] = TenantKey
        });

        var error = Assert.Throws<InvalidOperationException>(() => RegisterPlatformAuthentication(configuration));

        Assert.Contains("must be distinct", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Production_accepts_a_distinct_platform_signing_key()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Jwt:Key"] = TenantKey,
            ["Jwt:PlatformKey"] = PlatformKey
        });

        RegisterPlatformAuthentication(configuration);
    }

    [Fact]
    public void Development_explicitly_allows_tenant_signing_key_fallback()
    {
        var configuration = Configuration(new Dictionary<string, string?>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["Jwt:Key"] = TenantKey
        });

        RegisterPlatformAuthentication(configuration);
    }

    [Fact]
    public async Task Audit_persistence_failure_propagates_to_the_caller()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var audit = new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => audit.WriteAsync(
            PlatformActor(), "tenant.suspend", nameof(Tenant), "42", ct: cancellation.Token));
    }

    [Fact]
    public async Task Audit_rejects_a_missing_platform_actor_identifier()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var audit = new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(() => audit.WriteAsync(
            new ClaimsPrincipal(new ClaimsIdentity("Platform")),
            "tenant.suspend",
            nameof(Tenant),
            "42"));

        Assert.Empty(await context.Set<PlatformAuditLog>().ToListAsync());
    }

    [Fact]
    public async Task Provision_rolls_back_tenant_when_required_audit_fails()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        var audit = new ThrowingAuditService();
        var controller = Controller(context, audit);

        var result = await controller.Provision(new ProvisionTenantRequest
        {
            Name = "Audit Failure Tenant",
            Slug = "audit-failure-tenant"
        }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal(1, audit.CallCount);

        await using var verification = db.ContextFor(null);
        Assert.False(await verification.Set<Tenant>().IgnoreQueryFilters()
            .AnyAsync(t => t.Slug == "audit-failure-tenant"));
        Assert.Empty(await verification.Set<PlatformAuditLog>().ToListAsync());
    }

    [Fact]
    public async Task Provision_clears_stale_tracked_state_before_constructing_the_attempt()
    {
        using var db = new TestDb();
        await using var context = db.ContextFor(null);
        context.Set<Tenant>().Add(new Tenant
        {
            Name = "Must Not Persist",
            Slug = "stale-provisioning-state",
            Status = TenantStatus.Provisioning,
            CreatedBy = "test",
            CreatedOn = DateTime.UtcNow
        });
        var audit = new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance);
        var controller = Controller(context, audit);

        var result = await controller.Provision(new ProvisionTenantRequest
        {
            Name = "Fresh Provisioning Tenant",
            Slug = "fresh-provisioning-tenant"
        }, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        await using var verification = db.ContextFor(null);
        Assert.False(await verification.Set<Tenant>().IgnoreQueryFilters()
            .AnyAsync(t => t.Slug == "stale-provisioning-state"));
        Assert.True(await verification.Set<Tenant>().IgnoreQueryFilters()
            .AnyAsync(t => t.Slug == "fresh-provisioning-tenant" && t.Status == TenantStatus.Active));
        Assert.Equal("tenant.provision", (await verification.Set<PlatformAuditLog>().SingleAsync()).Action);
    }

    [Fact]
    public async Task Provision_reconstructs_the_graph_after_a_transient_audit_rollback()
    {
        using var db = new RetryingTestDb();
        await using var context = db.Context();
        var audit = new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance);
        var controller = Controller(context, audit);
        db.FailFirstAuditInsert();

        var result = await controller.Provision(new ProvisionTenantRequest
        {
            Name = "Retried Provisioning Tenant",
            Slug = "retried-provisioning-tenant"
        }, CancellationToken.None);

        Assert.IsType<CreatedAtActionResult>(result.Result);
        Assert.Equal(1, db.TransientFailureCount);
        await using var verification = db.Context();
        var tenant = await verification.Set<Tenant>().IgnoreQueryFilters()
            .SingleAsync(t => t.Slug == "retried-provisioning-tenant");
        Assert.Equal(TenantStatus.Active, tenant.Status);
        Assert.NotNull(tenant.PrimaryBusinessUnitId);
        Assert.Equal(1, await verification.Set<PlatformAuditLog>()
            .CountAsync(a => a.Action == "tenant.provision" && a.ActAsTenantId == tenant.Id));
    }

    [Fact]
    public async Task Suspend_rolls_back_status_change_when_required_audit_fails()
    {
        using var db = new TestDb();
        var tenantId = await SeedActiveTenant(db, "active-tenant");

        await using var context = db.ContextFor(null);
        var audit = new ThrowingAuditService();
        var controller = Controller(context, audit);

        var result = await controller.Suspend(tenantId,
            new TenantStatusChangeRequest { Reason = "Security review" }, CancellationToken.None);

        Assert.Equal(StatusCodes.Status500InternalServerError, Assert.IsType<ObjectResult>(result.Result).StatusCode);
        Assert.Equal(1, audit.CallCount);

        await using var verification = db.ContextFor(null);
        var persisted = await verification.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        Assert.Equal(TenantStatus.Active, persisted.Status);
        Assert.Null(persisted.StatusReason);
        Assert.Empty(await verification.Set<PlatformAuditLog>().ToListAsync());
    }

    [Fact]
    public async Task Suspend_commits_status_and_required_audit_together()
    {
        using var db = new TestDb();
        var tenantId = await SeedActiveTenant(db, "committed-tenant");

        await using var context = db.ContextFor(null);
        var audit = new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance);
        var controller = Controller(context, audit);

        var result = await controller.Suspend(tenantId,
            new TenantStatusChangeRequest { Reason = "Planned suspension" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);

        await using var verification = db.ContextFor(null);
        var persisted = await verification.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        var auditEntry = await verification.Set<PlatformAuditLog>().SingleAsync();
        Assert.Equal(TenantStatus.Suspended, persisted.Status);
        Assert.Equal("tenant.suspend", auditEntry.Action);
        Assert.Equal(tenantId, auditEntry.ActAsTenantId);
    }

    [Fact]
    public async Task Suspend_reloads_authoritative_tenant_state_inside_the_attempt()
    {
        using var db = new TestDb();
        var tenantId = await SeedActiveTenant(db, "retry-safe-tenant");

        await using var context = db.ContextFor(null);
        var staleTrackedTenant = await context.Set<Tenant>().IgnoreQueryFilters()
            .SingleAsync(t => t.Id == tenantId);
        staleTrackedTenant.Name = "Uncommitted stale name";
        var audit = new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance);
        var controller = Controller(context, audit);

        var result = await controller.Suspend(tenantId,
            new TenantStatusChangeRequest { Reason = "Retry-safe suspension" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        await using var verification = db.ContextFor(null);
        var persisted = await verification.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        Assert.Equal("Active Tenant", persisted.Name);
        Assert.Equal(TenantStatus.Suspended, persisted.Status);
        Assert.Equal("Retry-safe suspension", persisted.StatusReason);
        Assert.Equal("tenant.suspend", (await verification.Set<PlatformAuditLog>().SingleAsync()).Action);
    }

    [Fact]
    public async Task Suspend_reloads_and_commits_state_after_a_transient_audit_rollback()
    {
        using var db = new RetryingTestDb();
        long tenantId;
        await using (var seed = db.Context())
        {
            var tenant = ActiveTenant("retried-suspension-tenant");
            seed.Set<Tenant>().Add(tenant);
            await seed.SaveChangesAsync();
            tenantId = tenant.Id;
        }

        await using var context = db.Context();
        var audit = new PlatformAuditService(context, NullLogger<PlatformAuditService>.Instance);
        var controller = Controller(context, audit);
        db.FailFirstAuditInsert();

        var result = await controller.Suspend(tenantId,
            new TenantStatusChangeRequest { Reason = "Transient retry validation" }, CancellationToken.None);

        Assert.IsType<OkObjectResult>(result.Result);
        Assert.Equal(1, db.TransientFailureCount);
        await using var verification = db.Context();
        var persisted = await verification.Set<Tenant>().IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId);
        Assert.Equal(TenantStatus.Suspended, persisted.Status);
        Assert.Equal("Transient retry validation", persisted.StatusReason);
        Assert.Equal(1, await verification.Set<PlatformAuditLog>()
            .CountAsync(a => a.Action == "tenant.suspend" && a.ActAsTenantId == tenantId));
    }

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static void RegisterPlatformAuthentication(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddAuthentication().AddPlatformJwtBearer(configuration);
    }

    private static TenantsController Controller(
        ErpRfqAutomationContext context,
        IPlatformAuditService audit)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new TenantsController(
            context,
            audit,
            NullLogger<TenantsController>.Instance,
            services.GetRequiredService<IServiceScopeFactory>(),
            new TenantScopeAccessor())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = PlatformActor() }
            }
        };
    }

    private static ClaimsPrincipal PlatformActor() => new(new ClaimsIdentity(
    [
        new Claim("sub", "7"),
        new Claim("email", "operator@example.test")
    ], "Platform"));

    private static async Task<long> SeedActiveTenant(TestDb db, string slug)
    {
        await using var seed = db.ContextFor(null);
        var tenant = ActiveTenant(slug);
        seed.Set<Tenant>().Add(tenant);
        await seed.SaveChangesAsync();
        return tenant.Id;
    }

    private static Tenant ActiveTenant(string slug) => new()
    {
        Name = "Active Tenant",
        Slug = slug,
        Status = TenantStatus.Active,
        CreatedBy = "test",
        CreatedOn = DateTime.UtcNow
    };

    private sealed class ThrowingAuditService : IPlatformAuditService
    {
        public int CallCount { get; private set; }

        public Task WriteAsync(
            ClaimsPrincipal actor,
            string action,
            string? targetType = null,
            string? targetId = null,
            object? metadata = null,
            long? actAsTenantId = null,
            HttpContext? httpContext = null,
            CancellationToken ct = default)
        {
            CallCount++;
            throw new InvalidOperationException("Simulated audit persistence failure.");
        }
    }

    private sealed class RetryingTestDb : IDisposable
    {
        private readonly SqliteConnection _connection = new("Data Source=:memory:");
        private readonly FailFirstCommandInterceptor _interceptor = new();
        private readonly DbContextOptions<ErpRfqAutomationContext> _options;

        public RetryingTestDb()
        {
            _connection.Open();
            _options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseSqlite(_connection)
                .AddInterceptors(_interceptor)
                .ReplaceService<IExecutionStrategyFactory, RetryingExecutionStrategyFactory>()
                .Options;
            using var context = Context();
            context.Database.EnsureCreated();
        }

        public int TransientFailureCount => _interceptor.FailureCount;

        public ErpRfqAutomationContext Context() => new(_options, new StubTenant(null));

        public void FailFirstAuditInsert() => _interceptor.Arm("PlatformAuditLogs");

        public void Dispose() => _connection.Dispose();
    }

    private sealed class RetryingExecutionStrategyFactory : IExecutionStrategyFactory
    {
        private readonly ExecutionStrategyDependencies _dependencies;

        public RetryingExecutionStrategyFactory(ExecutionStrategyDependencies dependencies)
        {
            _dependencies = dependencies;
        }

        public IExecutionStrategy Create() => new RetryingExecutionStrategy(_dependencies);
    }

    private sealed class RetryingExecutionStrategy : ExecutionStrategy
    {
        public RetryingExecutionStrategy(ExecutionStrategyDependencies dependencies)
            : base(dependencies, maxRetryCount: 2, maxRetryDelay: TimeSpan.Zero)
        {
        }

        protected override bool ShouldRetryOn(Exception exception) =>
            exception is SimulatedTransientException;
    }

    private sealed class FailFirstCommandInterceptor : DbCommandInterceptor
    {
        private string? _commandFragment;
        private int _failureCount;

        public int FailureCount => _failureCount;

        public void Arm(string commandFragment)
        {
            _commandFragment = commandFragment;
            _failureCount = 0;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_commandFragment is not null
                && command.CommandText.Contains(_commandFragment, StringComparison.Ordinal)
                && Interlocked.CompareExchange(ref _failureCount, 1, 0) == 0)
            {
                throw new SimulatedTransientException();
            }

            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private sealed class SimulatedTransientException : Exception;
}
