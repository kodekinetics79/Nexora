using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Infrastructure.Storage;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// The assembly package must be reachable from a real container.
///
/// <para>Every one of these services compiled and passed its unit tests for weeks while being
/// registered nowhere — the whole package was unreachable dead code at runtime, and no test
/// noticed because unit tests construct their subjects directly. A capability that cannot be
/// resolved is not wired, whatever its coverage says.</para>
/// </summary>
public class EmailInquiryAssemblyRegistrationTests
{
    /// <summary>
    /// Builds the container from the SAME extension <c>Program</c> calls.
    ///
    /// <para>It used to re-declare the registrations, which made it assert properties of itself:
    /// deleting every <c>AddScoped</c> from <c>Program</c> left this green — the exact failure it
    /// was written to prevent. Only the surrounding infrastructure (context, storage) is
    /// substituted, because booting the real host needs a database, secrets and a mailbox.</para>
    /// </summary>
    private static ServiceProvider BuildContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("DataSource=:memory:");
        connection.Open();
        services.AddSingleton(connection);
        services.AddDbContext<ErpRfqAutomationContext>(o => o.UseSqlite(connection));
        // The unconfigured storage refuses every write, which is exactly right here: the
        // property under test is that the graph CONSTRUCTS, not that it can store anything.
        services.AddSingleton<IEvidenceObjectStorage>(
            new UnconfiguredEvidenceObjectStorage(
                new InvalidOperationException("Evidence storage is not configured in this test.")));

        // The production composition, not a copy of it.
        services.AddEmailInquiryAssembly();

        // ValidateOnBuild catches a scoped dependency captured by a singleton at build time,
        // which resolving service-by-service would miss entirely.
        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Theory]
    [InlineData(typeof(IEmailInquiryCaptureService))]
    [InlineData(typeof(IEmailInquiryAssemblyCoordinator))]
    [InlineData(typeof(IRawEmailEvidenceReader))]
    [InlineData(typeof(EmailInquiryLimits))]
    public void Every_assembly_service_resolves_from_the_container(Type contract)
    {
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        var resolved = scope.ServiceProvider.GetService(contract);

        Assert.NotNull(resolved);
    }

    [Fact]
    public void The_capability_resolves_as_one_graph_not_only_service_by_service()
    {
        // Resolving each in isolation can pass while the graph as a whole cannot be built —
        // a scoped dependency captured by a singleton, for instance.
        using var provider = BuildContainer();
        using var scope = provider.CreateScope();

        var capture = scope.ServiceProvider.GetRequiredService<IEmailInquiryCaptureService>();
        var coordinator = scope.ServiceProvider.GetRequiredService<IEmailInquiryAssemblyCoordinator>();
        var reader = scope.ServiceProvider.GetRequiredService<IRawEmailEvidenceReader>();

        Assert.IsType<EmailInquiryCaptureService>(capture);
        Assert.IsType<EmailInquiryAssemblyCoordinator>(coordinator);
        Assert.IsType<RawEmailEvidenceReader>(reader);
    }

    [Fact]
    public void Registration_alone_starts_no_background_work()
    {
        // Building the container must not poll a mailbox, touch storage or schedule anything.
        // An operator with no configured mailbox must be able to start the application.
        using var provider = BuildContainer();

        Assert.Empty(provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>());
    }

    [Fact]
    public void The_declared_message_limits_are_the_frozen_values()
    {
        using var provider = BuildContainer();
        var limits = provider.GetRequiredService<EmailInquiryLimits>();

        // These are commercial decisions, not arbitrary numbers: the 16 KB inline ceiling sits
        // below the band where a pasted requirements screenshot becomes indistinguishable from
        // a signature logo.
        Assert.Equal(3, limits.MaxNestingDepth);
        Assert.Equal(50, limits.MaxComponents);
        Assert.Equal(25L * 1024 * 1024, limits.MaxComponentBytes);
        Assert.Equal(100L * 1024 * 1024, limits.MaxTotalBytes);
        Assert.Equal(16L * 1024, limits.InlineAssetMaxBytes);
    }
}
