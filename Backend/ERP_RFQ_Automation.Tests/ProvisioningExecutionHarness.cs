using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Notifications;
using ERP_RFQ_Automation.Notifications.Providers;
using ERP_RFQ_Automation.Platform.Models;
using ERP_RFQ_Automation.Platform.Onboarding;
using ERP_RFQ_Automation.Platform.Provisioning;
using ERP_RFQ_Automation.Platform.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// A real relational database plus a real DI container for the durable provisioning engine.
///
/// <para><b>Why it is not <see cref="TestDb"/>.</b> The engine's three tables are spliced into the
/// model by <c>ApplyTenantProvisioningModel</c>, and the context's <c>OnModelCreatingPartial</c>
/// does not call it yet — that line belongs to whoever owns the context. Rather than testing
/// against a hand-built model that would not be the real one, this harness replaces EF's
/// <c>IModelCustomizer</c>, which runs the context's OWN <c>OnModelCreating</c> and then adds the
/// provisioning tables. Everything under test therefore runs against the complete production
/// model — all ~40 entities, the platform schema, the query filters — with three tables added.</para>
///
/// <para><b>Why a container and not hand-wired collaborators.</b> The runner's central property is
/// that it uses SEPARATE DI scopes for the journal, for each step, and for the audit — a failure
/// record written on the step's own connection would be rolled back by the failure it records.
/// Hand-passing one context would test a design that does not exist.</para>
/// </summary>
public sealed class ProvisioningHarness : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    public ProvisioningHarness()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, ProvisioningModelCustomizer>()
            .EnableSensitiveDataLogging()
            .Options;

        using (var schema = new ErpRfqAutomationContext(options, new StubTenant(null)))
            schema.Database.EnsureCreated();

        var services = new ServiceCollection();
        services.AddLogging();

        // Scoped, exactly as Program.cs registers it: every scope the runner creates gets its own
        // context over the same in-memory database, which is what makes "the journal survives the
        // step's rollback" a property the test can actually observe.
        services.AddScoped(_ => new ErpRfqAutomationContext(options, new StubTenant(null)));
        services.AddScoped<ITenantBaselineSeeder, TenantBaselineSeeder>();
        services.AddScoped<IEmailSender>(_ => new ConsoleEmailSender(NullLogger<ConsoleEmailSender>.Instance));
        services.AddSingleton(Options.Create(new NotificationsOptions { AppBaseUrl = "https://app.nexora.test" }));
        services.AddSingleton(Options.Create(new TenantOnboardingOptions()));
        services.AddScoped<ITenantAdminInvitationService, TenantAdminInvitationService>();

        services.AddSingleton<IOptionsMonitor<ProvisioningOptions>>(
            new StaticOptionsMonitor<ProvisioningOptions>(new ProvisioningOptions
            {
                // The runner's own attempt budget is set to one so a test that wants a failure to
                // REST as a failure is not racing an automatic re-attempt.
                MaxAutomaticAttempts = 1,
                RetryBackoff = TimeSpan.Zero
            }));
        services.AddSingleton<ProvisioningRunSignal>();
        services.AddScoped<IProvisioningStepExecutor, ProvisioningStepExecutor>();
        services.AddScoped<ITenantProvisioningService, TenantProvisioningService>();
        services.AddScoped<IProvisioningDraftService, ProvisioningDraftService>();

        // The real audit service over the SAME scoped context, exactly as Program.cs registers it.
        // A stub would let a recovery pass its test while writing no evidence, which is the one
        // property of that operation worth proving.
        services.AddScoped<IPlatformAuditService, PlatformAuditService>();
        services.AddScoped<IProvisioningStepReconciler, ProvisioningStepReconciler>();
        services.AddScoped<IProvisioningLeaseRecovery, ProvisioningLeaseRecovery>();

        services.AddSingleton<ITenantProvisioningRunner, TenantProvisioningRunner>();

        _services = services.BuildServiceProvider();
    }

    public IServiceScope Scope() => _services.CreateScope();

    public ErpRfqAutomationContext Context() => new(
        new DbContextOptionsBuilder<ErpRfqAutomationContext>()
            .UseSqlite(_connection)
            .ReplaceService<IModelCustomizer, ProvisioningModelCustomizer>()
            .Options,
        new StubTenant(null));

    public ITenantProvisioningRunner Runner()
        => _services.GetRequiredService<ITenantProvisioningRunner>();

    /// <summary>Submits through the real service, on its own scope, as the console would.</summary>
    public async Task<ProvisioningSubmitResult> SubmitAsync(
        ProvisionTenantRequest request, string? idempotencyKey = null,
        string actor = "owner@nexora.app", long? actorId = 7)
    {
        using var scope = Scope();
        var service = scope.ServiceProvider.GetRequiredService<ITenantProvisioningService>();
        return await service.SubmitAsync(request, idempotencyKey, actor, actorId, CancellationToken.None);
    }

    /// <summary>Submits and drives the execution to a terminal state, as the worker would.</summary>
    public async Task<ProvisioningExecution> ProvisionAsync(
        ProvisionTenantRequest request, string? idempotencyKey = null)
    {
        var submitted = await SubmitAsync(request, idempotencyKey);
        Assert.Equal(ProvisioningSubmitOutcome.Created, submitted.Outcome);
        var outcome = await Runner().RunAsync(submitted.Execution!.Id);
        Assert.NotNull(outcome);
        return outcome!.Execution;
    }

    public async Task<ProvisioningExecution> ReloadAsync(long executionId)
    {
        await using var db = Context();
        return await db.Set<ProvisioningExecution>().AsNoTracking()
            .Include(e => e.Steps)
            .FirstAsync(e => e.Id == executionId);
    }

    /// <summary>
    /// A plan every fixture attaches, because a Billable tenant with none is refused outright —
    /// that refusal belongs to the commercial-terms tests and would only obscure the durability
    /// behaviour under test here.
    /// </summary>
    public async Task<long> PlanAsync()
    {
        await using var db = Context();
        var plan = new Plan { Code = $"pro-{Guid.NewGuid():N}", Name = "Pro", MonthlyPriceUsd = 499m };
        db.Set<Plan>().Add(plan);
        await db.SaveChangesAsync();
        return plan.Id;
    }

    public static ProvisionTenantRequest Request(
        string slug, string email, long planId, string? activation = null, string? password = null)
        => new()
        {
            Name = $"Tenant {slug}",
            Slug = slug,
            PlanId = planId,
            BaseCurrencyCode = "USD",
            CountryCode = "SA",
            AdminEmail = email,
            AdminFirstName = "Ada",
            AdminLastName = "Lovelace",
            AdminActivation = activation,
            AdminPassword = password
        };

    public void Dispose()
    {
        _services.Dispose();
        _connection.Dispose();
    }
}

/// <summary>
/// A fixed <see cref="IOptionsMonitor{T}"/>. The engine reads options through a monitor so a
/// configuration reload takes effect without a restart; a test needs the value, not the plumbing.
/// </summary>
public sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    public StaticOptionsMonitor(T value) => CurrentValue = value;

    public T CurrentValue { get; }

    public T Get(string? name) => CurrentValue;

    public IDisposable? OnChange(Action<T, string?> listener) => null;
}
