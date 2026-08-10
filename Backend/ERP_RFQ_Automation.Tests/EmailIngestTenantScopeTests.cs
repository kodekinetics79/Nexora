using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// SEC-ING-01 — the tenant scope the mailbox poller runs each mailbox inside, and the ORDER that
/// makes it real.
///
/// <para><c>EmailService</c> contained no call to <see cref="ITenantScopeAccessor.Push"/> at all,
/// so the entire ingest ran with <c>ITenantContext.BusinessUnitId == null</c>. That is not one
/// missing safeguard, it is both of them: the EF global query filters are written
/// <c>CurrentTenantId == null || …</c> and become no-ops, and
/// <c>TenantRlsCommandInterceptor.ResolveDatabaseRole</c> routes a background command with no
/// tenant to <c>nexora_pipeline_app</c>, which is created BYPASSRLS.</para>
///
/// <para>The portable lane, deliberately: what these two tests prove is C# object lifetime — when
/// the tenant is captured, and what the service does when it was captured wrong. Neither needs a
/// PostgreSQL role. The read the scope protects is proved on the PostgreSQL lane, in
/// <see cref="EmailIngestCrossTenantDuplicatePostgreSqlTests"/>, because that one does.</para>
/// </summary>
public sealed class EmailIngestTenantScopeTests
{
    private const long TenantA = 971_401;

    /// <summary>
    /// The ordering rule, stated as an executable fact rather than as a comment.
    ///
    /// <para><see cref="HttpTenantContext"/> reads the accessor in its CONSTRUCTOR, and
    /// <c>ErpRfqAutomationContext</c> captures that <see cref="ITenantContext"/> in ITS constructor.
    /// So a DI scope's DbContext believes whatever was ambient at the moment the scope first
    /// resolved it, and a push issued afterwards changes nothing for that scope. This is why
    /// <c>FetchAndSaveLeadsAsync</c> pushes BEFORE <c>CreateScope</c>, and why "we added a push"
    /// is not by itself a fix.</para>
    /// </summary>
    [Fact]
    public void A_push_after_the_scope_is_created_does_not_reach_the_DbContext()
    {
        using var host = new PollerHost();
        var accessor = host.Services.GetRequiredService<ITenantScopeAccessor>();

        // Wrong order: scope first.
        using (var scope = host.ScopeFactory.CreateScope())
        {
            var eager = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            using var late = accessor.Push(TenantA);
            Assert.Null(eager.ScopedTenantId);
        }

        // Right order: push first.
        using (var tenant = accessor.Push(TenantA))
        using (var scope = host.ScopeFactory.CreateScope())
        {
            Assert.Equal(TenantA,
                scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>().ScopedTenantId);
        }
    }

    /// <summary>
    /// The fail-closed guard: the poller refuses a mailbox whose scope did not reach the DbContext
    /// instead of polling it unscoped.
    ///
    /// <para>The mis-wiring modelled here is the realistic one and the one the accessor's own doc
    /// comment warns about: a SECOND <see cref="TenantScopeAccessor"/>. The scope is an AsyncLocal
    /// held per instance, so pushing into an accessor the container does not know about sets a
    /// value nothing ever reads — the push "succeeds", the DbContext stays unscoped, and before
    /// this guard the ingest would have carried on exactly as it did with no push at all.</para>
    ///
    /// <para>The proof that the guard is load-bearing is the mailbox never being reached: the
    /// configured host is 127.0.0.1:1, so a poll that got as far as IMAP fails with a socket error.
    /// The recorded failure names the tenant scope, and the durable poll ledger is untouched.</para>
    /// </summary>
    [Fact]
    public async Task A_mailbox_whose_scope_did_not_reach_the_DbContext_is_refused_not_polled()
    {
        using var host = new PollerHost();
        await host.SeedMailboxAsync(TenantA);

        var report = await host.PollAsync(tenantScope: new TenantScopeAccessor());

        var outcome = Assert.Single(report.Mailboxes);
        Assert.False(outcome.Succeeded);
        Assert.Contains("Tenant scope is mandatory for this worker", outcome.FailureReason);

        // The ledger is written through the tenant-scoped context, so a refused mailbox records
        // nothing: there was no scope to write it in. The channel still goes red through the
        // report above, which is the surface an operator reads.
        await using var verify = host.Context();
        var config = await verify.EmailConfigurations.IgnoreQueryFilters()
            .SingleAsync(c => c.BusinessUnitId == TenantA);
        Assert.Null(config.LastPollAttemptOn);
        Assert.Null(config.LastSuccessfulPollOn);
    }

    /// <summary>
    /// The same poll, wired the way Program.cs wires it: the container's own accessor. The scope
    /// guard passes, the mailbox is genuinely attempted, and the attempt is recorded — so the
    /// refusal above is a real discriminator and not something the poller does to every mailbox.
    /// </summary>
    [Fact]
    public async Task A_correctly_scoped_mailbox_is_polled_and_its_attempt_is_recorded()
    {
        using var host = new PollerHost();
        await host.SeedMailboxAsync(TenantA);

        var report = await host.PollAsync(
            tenantScope: host.Services.GetRequiredService<ITenantScopeAccessor>());

        var outcome = Assert.Single(report.Mailboxes);
        // 127.0.0.1:1 refuses the connection; the point is that it got THAT far.
        Assert.False(outcome.Succeeded);
        Assert.DoesNotContain("Tenant scope is mandatory", outcome.FailureReason);

        await using var verify = host.Context();
        var config = await verify.EmailConfigurations.IgnoreQueryFilters()
            .SingleAsync(c => c.BusinessUnitId == TenantA);
        Assert.NotNull(config.LastPollAttemptOn);
        Assert.Equal(1, config.ConsecutivePollFailures);
    }

    /// <summary>
    /// A real container over a real SQLite database, wired the way Program.cs wires the background
    /// path: a singleton <see cref="ITenantScopeAccessor"/>, a SCOPED <see cref="ITenantContext"/>
    /// that reads it once at construction, and a scoped DbContext that captures that.
    /// </summary>
    private sealed class PollerHost : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly string _root;

        public PollerHost()
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _root = Path.Combine(Path.GetTempPath(), "nexora-email-scope", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
            services.AddScoped<ITenantContext>(sp =>
                new ScopeCapturingTenantContext(sp.GetRequiredService<ITenantScopeAccessor>()));
            services.AddDbContext<ErpRfqAutomationContext>(options => options.UseSqlite(_connection));
            // Registered so a correctly scoped poll gets past ProcessConfigAsync's own resolution
            // and actually reaches the mailbox; it is never called, because the mailbox refuses
            // the connection first.
            services.AddScoped<ERP_RFQ_Automation.Services.Interfaces.ILLMService>(_ => new StubLlm());
            _provider = services.BuildServiceProvider(new ServiceProviderOptions
            {
                ValidateScopes = true,
                ValidateOnBuild = true
            });

            using var scope = _provider.CreateScope();
            scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>().Database.EnsureCreated();
        }

        public IServiceProvider Services => _provider;
        public IServiceScopeFactory ScopeFactory => _provider.GetRequiredService<IServiceScopeFactory>();

        /// <summary>A context outside any DI scope, for seeding and assertions.</summary>
        public ErpRfqAutomationContext Context() => new(
            new DbContextOptionsBuilder<ErpRfqAutomationContext>().UseSqlite(_connection).Options,
            new StubTenant(null));

        public async Task SeedMailboxAsync(long businessUnitId)
        {
            await using var db = Context();
            Seed.EnsureBusinessUnit(db, businessUnitId);
            db.EmailConfigurations.Add(new EmailConfiguration
            {
                BusinessUnitId = businessUnitId,
                ConfigurationName = $"intake-{businessUnitId}",
                EmailAddress = $"intake-{businessUnitId}@tenant.test",
                Protocol = "IMAP",
                // Unroutable by construction: a poll that gets this far must fail fast rather
                // than reach anything real.
                Host = "127.0.0.1",
                Port = 1,
                Username = $"intake-{businessUnitId}",
                Password = "secret",
                UseSsl = false,
                PollingInterval = 300,
                IsActive = true,
                CreatedOn = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        public Task<MailboxPollReport> PollAsync(ITenantScopeAccessor tenantScope)
        {
            var service = new EmailService(
                context: Context(),
                env: new TenantWorkGateEnvironment(_root),
                logger: NullLogger<EmailService>.Instance,
                llmService: null!,
                scopeFactory: ScopeFactory,
                configuration: new ConfigurationBuilder().Build(),
                storage: new TenantWorkGateStorage(_root),
                pollerHealth: null,
                workGate: null,
                tenantScope: tenantScope);
            return service.FetchAndSaveLeadsAsync();
        }

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
            try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
        }

        /// <summary>
        /// The tenant context a background worker gets: no HttpContext, so the tenant is whatever
        /// the accessor held AT CONSTRUCTION. Mirrors <see cref="HttpTenantContext"/>'s fallback —
        /// including the capture, which is the behaviour under test.
        /// </summary>
        private sealed class ScopeCapturingTenantContext : ITenantContext
        {
            public ScopeCapturingTenantContext(ITenantScopeAccessor scope)
                => BusinessUnitId = scope.BusinessUnitId;

            public long? BusinessUnitId { get; }
        }
    }
}
