using ERP_RFQ_Automation.CommercialRouting;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.Sla;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Horizontal-scale safety for the SLA sweep.
///
/// Two properties are asserted here, both of which were broken before:
///  1. SEND-ONCE IS A DATABASE GUARANTEE. The worker used to do
///     EventExistsAsync-then-insert against a deliberately NON-unique index, so two
///     instances sweeping at the same time both passed the lookup and both mailed the
///     customer. Now the claim row is INSERTed first against a unique
///     (BusinessUnitId, DedupKey) index and the loser takes a 23505 / UNIQUE violation.
///  2. TENANT SCOPE IS MANDATORY. The worker has no HttpContext, so it gets the
///     BYPASSRLS nexora_pipeline_app role, and a null tenant makes every EF global
///     filter (CurrentTenantId == null || ...) a no-op. All per-tenant work must run
///     inside a pushed tenant scope, and the worker must refuse to run rather than
///     silently fall back to the cross-tenant path.
/// </summary>
public sealed class SlaSweepWorkerScaleOutTests
{
    private const long Bu1 = 1;
    private const long Bu2 = 2;

    // ---------------------------------------------------------------- idempotency

    [Fact]
    public void SlaEvents_dedup_key_is_unique_per_business_unit()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(null);

        var index = ctx.Model.FindEntityType(typeof(SlaEvent))!
            .GetIndexes()
            .SingleOrDefault(i => i.GetDatabaseName() == "UX_SlaEvents_BU_DedupKey");

        Assert.NotNull(index);
        Assert.True(index!.IsUnique, "The SLA send-once index must be UNIQUE or two instances both send.");
        Assert.Equal(
            new[] { nameof(SlaEvent.BusinessUnitId), nameof(SlaEvent.DedupKey) },
            index.Properties.Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Concurrent_claims_for_the_same_alert_produce_exactly_one_event()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu1);
            await seed.SaveChangesAsync();
        }

        // Two independent DbContexts model two instances racing on the same alert. Both
        // go straight to the INSERT, i.e. the exact interleaving where both instances
        // looked first and both saw nothing — the case the old lookup-before-insert lost.
        using var instanceA = db.ContextFor(Bu1);
        using var instanceB = db.ContextFor(Bu1);
        var key = SlaEvent.BuildDedupKey("lead", 77, "overdue");

        var claimA = await SlaSweepWorker.InsertClaimAsync(instanceA, Bu1, "lead", 77, "overdue", key, default);
        var claimB = await SlaSweepWorker.InsertClaimAsync(instanceB, Bu1, "lead", 77, "overdue", key, default);

        Assert.NotNull(claimA);   // one instance wins and sends
        Assert.Null(claimB);      // the loser MUST NOT send

        using var verify = db.ContextFor(Bu1);
        Assert.Equal(1, await verify.Set<SlaEvent>().CountAsync(e => e.EntityId == 77));
    }

    [Fact]
    public async Task An_already_claimed_alert_short_circuits_without_an_insert()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu1);
            await seed.SaveChangesAsync();
        }

        using var ctx = db.ContextFor(Bu1);
        Assert.NotNull(await SlaSweepWorker.TryClaimEventAsync(ctx, Bu1, "lead", 88, "warn", null, default));

        // Steady state: the lookup is the cheap path, the unique index is the guarantee.
        Assert.Null(await SlaSweepWorker.TryClaimEventAsync(ctx, Bu1, "lead", 88, "warn", null, default));

        using var verify = db.ContextFor(Bu1);
        Assert.Equal(1, await verify.Set<SlaEvent>().CountAsync(e => e.EntityId == 88));
    }

    [Fact]
    public async Task Losing_a_claim_leaves_the_context_usable_for_the_next_alert()
    {
        // Regression guard: a failed INSERT must be detached, otherwise the poisoned
        // entry re-enters the next SaveChanges and the whole tenant's sweep dies.
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu1);
            await seed.SaveChangesAsync();
        }

        using var ctx = db.ContextFor(Bu1);
        var key = SlaEvent.BuildDedupKey("lead", 5, "warn");
        Assert.NotNull(await SlaSweepWorker.InsertClaimAsync(ctx, Bu1, "lead", 5, "warn", key, default));
        Assert.Null(await SlaSweepWorker.InsertClaimAsync(ctx, Bu1, "lead", 5, "warn", key, default));

        var next = await SlaSweepWorker.TryClaimEventAsync(ctx, Bu1, "lead", 6, "warn", null, default);

        Assert.NotNull(next);
        using var verify = db.ContextFor(Bu1);
        Assert.Equal(2, await verify.Set<SlaEvent>().CountAsync());
    }

    [Fact]
    public async Task Releasing_a_claim_lets_the_next_sweep_retry_the_alert()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu1);
            await seed.SaveChangesAsync();
        }

        using var ctx = db.ContextFor(Bu1);
        var claim = await SlaSweepWorker.TryClaimEventAsync(ctx, Bu1, "quote", 9, "expired", null, default);
        Assert.NotNull(claim);

        await SlaSweepWorker.ReleaseEventClaimAsync(ctx, claim!, default);

        Assert.NotNull(await SlaSweepWorker.TryClaimEventAsync(ctx, Bu1, "quote", 9, "expired", null, default));
    }

    [Fact]
    public async Task Stale_quote_digest_still_repeats_daily_but_only_once_per_day()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu1);
            await seed.SaveChangesAsync();
        }

        var monday = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc);
        var tuesday = monday.AddDays(1);

        using var ctx = db.ContextFor(Bu1);
        Assert.NotNull(await SlaSweepWorker.TryClaimEventAsync(
            ctx, Bu1, "quote-stale-digest", 42, "stale", monday, default));
        Assert.Null(await SlaSweepWorker.TryClaimEventAsync(
            ctx, Bu1, "quote-stale-digest", 42, "stale", monday, default));   // same day: blocked
        Assert.NotNull(await SlaSweepWorker.TryClaimEventAsync(
            ctx, Bu1, "quote-stale-digest", 42, "stale", tuesday, default));  // next day: allowed
    }

    [Fact]
    public async Task The_same_alert_key_in_a_different_tenant_is_not_blocked()
    {
        using var db = new TestDb();
        using (var seed = db.ContextFor(null))
        {
            Seed.EnsureBusinessUnit(seed, Bu1);
            Seed.EnsureBusinessUnit(seed, Bu2);
            await seed.SaveChangesAsync();
        }

        using var one = db.ContextFor(Bu1);
        using var two = db.ContextFor(Bu2);

        Assert.NotNull(await SlaSweepWorker.TryClaimEventAsync(one, Bu1, "lead", 1, "warn", null, default));
        Assert.NotNull(await SlaSweepWorker.TryClaimEventAsync(two, Bu2, "lead", 1, "warn", null, default));
    }

    [Fact]
    public void Unique_violations_are_recognised_for_postgres_and_sqlite()
    {
        var postgres = new DbUpdateException("save failed",
            new FakeDbException("23505", "duplicate key value violates unique constraint \"UX_SlaEvents_BU_DedupKey\""));
        var sqlite = new DbUpdateException("save failed",
            new InvalidOperationException("SQLite Error 19: 'UNIQUE constraint failed: SlaEvents.BusinessUnitId, SlaEvents.DedupKey'."));
        var unrelated = new DbUpdateException("save failed",
            new FakeDbException("23503", "insert or update violates foreign key constraint"));

        Assert.True(SlaSweepWorker.IsUniqueViolation(postgres));
        Assert.True(SlaSweepWorker.IsUniqueViolation(sqlite));
        Assert.False(SlaSweepWorker.IsUniqueViolation(unrelated));
    }

    // -------------------------------------------------------- tenant-scope enforcement

    [Fact]
    public void Workers_that_sweep_every_tenant_require_a_tenant_scope_accessor()
    {
        // Constructor-level guard: a worker that cannot push a tenant scope will run its
        // whole body under the BYPASSRLS pipeline role with the EF filters disabled.
        foreach (var workerType in new[] { typeof(SlaSweepWorker), typeof(RoutingReconciliationWorker) })
        {
            var takesTenantScope = workerType.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ITenantScopeAccessor)));
            Assert.True(takesTenantScope,
                $"{workerType.Name} must take ITenantScopeAccessor so its per-tenant work runs scoped.");
        }
    }

    [Fact]
    public async Task Sweep_runs_each_tenant_inside_its_own_tenant_scope()
    {
        using var host = new WorkerHost();
        await host.SeedTwoTenantsWithOverdueLeadsAsync();

        var worker = host.CreateSlaWorker();
        var swept = await worker.SweepOnceAsync(default);

        Assert.Equal(2, swept);

        // Every query the sweep ran saw exactly one tenant.
        Assert.Equal(new long?[] { Bu1, Bu2 }, host.ObservedTenants.OrderBy(x => x).ToArray());

        // And each tenant's alert went to that tenant's own user only.
        Assert.Equal(2, host.Notifications.Sent.Count);
        Assert.Contains(host.Notifications.Sent, s => s.BusinessUnitId == Bu1 && s.ToEmail == "owner1@tenant1.test");
        Assert.Contains(host.Notifications.Sent, s => s.BusinessUnitId == Bu2 && s.ToEmail == "owner2@tenant2.test");
        Assert.DoesNotContain(host.Notifications.Sent, s => s.BusinessUnitId == Bu1 && s.ToEmail == "owner2@tenant2.test");
        Assert.DoesNotContain(host.Notifications.Sent, s => s.BusinessUnitId == Bu2 && s.ToEmail == "owner1@tenant1.test");

        using var verify = host.UnscopedContext();
        var events = await verify.Set<SlaEvent>().IgnoreQueryFilters().ToListAsync();
        Assert.Equal(2, events.Count);
        Assert.Contains(events, e => e.BusinessUnitId == Bu1);
        Assert.Contains(events, e => e.BusinessUnitId == Bu2);
    }

    [Fact]
    public async Task Sweep_sends_nothing_when_the_tenant_scope_fails_to_apply()
    {
        // Fail-closed. If a future refactor breaks the scope wiring, the worker must
        // refuse to run rather than quietly sweep every tenant under the bypass role.
        using var host = new WorkerHost(honourTenantScope: false);
        await host.SeedTwoTenantsWithOverdueLeadsAsync();

        var worker = host.CreateSlaWorker();
        await worker.SweepOnceAsync(default);

        Assert.Empty(host.Notifications.Sent);
        using var verify = host.UnscopedContext();
        Assert.Empty(await verify.Set<SlaEvent>().IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task A_second_sweep_does_not_re_send_the_same_alert()
    {
        using var host = new WorkerHost();
        await host.SeedTwoTenantsWithOverdueLeadsAsync();

        var worker = host.CreateSlaWorker();
        await worker.SweepOnceAsync(default);
        await worker.SweepOnceAsync(default);

        Assert.Equal(2, host.Notifications.Sent.Count);
    }

    [Fact]
    public async Task Two_instances_sweeping_the_same_tenants_send_each_alert_once()
    {
        // The defect verbatim: two SlaSweepWorker instances against the same database.
        using var host = new WorkerHost();
        await host.SeedTwoTenantsWithOverdueLeadsAsync();

        var instanceA = host.CreateSlaWorker();
        var instanceB = host.CreateSlaWorker();

        await Task.WhenAll(instanceA.SweepOnceAsync(default), instanceB.SweepOnceAsync(default));

        Assert.Equal(2, host.Notifications.Sent.Count);
        using var verify = host.UnscopedContext();
        Assert.Equal(2, await verify.Set<SlaEvent>().IgnoreQueryFilters().CountAsync());
    }

    // ------------------------------------------------------- email poller leader election

    [Fact]
    public void Email_poller_lock_key_is_stable_across_processes()
    {
        // Must NOT be string.GetHashCode(), which is randomised per process — every
        // instance has to derive the same advisory-lock key from the same name or the
        // "single poller" guarantee silently becomes "one poller per instance".
        const long expected = -3232083163231540113;

        Assert.Equal(expected, ERP_RFQ_Automation.Services.PostgresAdvisoryLease.StableKey(
            ERP_RFQ_Automation.Services.EmailBackgroundService.PollLockName));
        Assert.Equal(
            ERP_RFQ_Automation.Services.PostgresAdvisoryLease.StableKey("nexora:email-poller"),
            ERP_RFQ_Automation.Services.PostgresAdvisoryLease.StableKey("NEXORA:EMAIL-POLLER"));
        Assert.NotEqual(
            ERP_RFQ_Automation.Services.PostgresAdvisoryLease.StableKey("nexora:email-poller"),
            ERP_RFQ_Automation.Services.PostgresAdvisoryLease.StableKey("nexora:other-poller"));
    }

    [Fact]
    public async Task Non_postgres_providers_get_an_always_granted_no_op_lease()
    {
        using var db = new TestDb();
        using var ctx = db.ContextFor(null);

        var lease = await ERP_RFQ_Automation.Services.PostgresAdvisoryLease.TryAcquireAsync(
            ctx, ERP_RFQ_Automation.Services.EmailBackgroundService.PollLockName, default);

        Assert.NotNull(lease);
        Assert.False(lease!.IsAdvisory);
        await lease.DisposeAsync();
    }

    // ---------------------------------------------------------------- test harness

    private sealed class FakeDbException : System.Data.Common.DbException
    {
        private readonly string _sqlState;
        public FakeDbException(string sqlState, string message) : base(message) => _sqlState = sqlState;
        public override string SqlState => _sqlState;
    }

    private sealed record SentAlert(string ToEmail, string Level, long BusinessUnitId);

    private sealed class CapturingNotifications : ISlaNotifications
    {
        private readonly object _gate = new();
        public List<SentAlert> Sent { get; } = new();

        public Task<bool> SendDeadlineAlertAsync(
            string toEmail, string? toName, string level, string entityLabel,
            string headline, string detail, long businessUnitId, CancellationToken ct = default)
        {
            lock (_gate) Sent.Add(new SentAlert(toEmail, level, businessUnitId));
            return Task.FromResult(true);
        }

        public Task<bool> SendStaleQuotesDigestAsync(
            string toEmail, string? toName, IReadOnlyList<StaleQuoteDigestLine> lines,
            long businessUnitId, CancellationToken ct = default)
        {
            lock (_gate) Sent.Add(new SentAlert(toEmail, "stale", businessUnitId));
            return Task.FromResult(true);
        }
    }

    private sealed class NoOpOutcomes : IQuoteOutcomeService
    {
        public Task<ERP_RFQ_Automation.DTOs.QuoteDTOs.QuoteResponseDTO> SetOutcomeAsync(
            long quoteId, long businessUnitId, string actorEmail,
            string outcome, string? reasonCode = null, string? note = null,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<bool> ExpireAsync(long quoteId, string reasonCode = "AUTO_EXPIRED", CancellationToken ct = default)
            => Task.FromResult(false);

        public Task MarkRespondedAsync(long quoteId, long businessUnitId, string actorEmail, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<OutcomeReasonDto>> GetOutcomeReasonsAsync(
            long businessUnitId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<OutcomeReasonDto>>(Array.Empty<OutcomeReasonDto>());
    }

    /// <summary>
    /// Mirrors the production wiring: an ambient <see cref="ITenantScopeAccessor"/> that
    /// background workers push onto, and a scoped <see cref="ITenantContext"/> that reads
    /// it when the DbContext is constructed — exactly how HttpTenantContext resolves a
    /// worker's tenant. Setting <c>honourTenantScope: false</c> models the broken wiring.
    /// </summary>
    private sealed class WorkerHost : IDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _provider;
        private readonly DbContextOptions<ErpRfqAutomationContext> _rawOptions;
        private readonly object _gate = new();

        public CapturingNotifications Notifications { get; } = new();
        public List<long?> ObservedTenants { get; } = new();

        public WorkerHost(bool honourTenantScope = true)
        {
            _connection = new SqliteConnection("Data Source=:memory:");
            _connection.Open();
            _rawOptions = new DbContextOptionsBuilder<ErpRfqAutomationContext>()
                .UseSqlite(_connection)
                .Options;
            using (var create = new ErpRfqAutomationContext(_rawOptions, new StubTenant(null)))
                create.Database.EnsureCreated();

            var services = new ServiceCollection();
            services.AddSingleton<ITenantScopeAccessor, TenantScopeAccessor>();
            services.AddScoped<ITenantContext>(sp =>
            {
                var ambient = honourTenantScope
                    ? sp.GetRequiredService<ITenantScopeAccessor>().BusinessUnitId
                    : null;
                if (ambient.HasValue)
                    lock (_gate) ObservedTenants.Add(ambient);
                return new StubTenant(ambient);
            });
            services.AddDbContext<ErpRfqAutomationContext>(
                o => o.UseSqlite(_connection), ServiceLifetime.Scoped);
            services.AddSingleton<ISlaNotifications>(Notifications);
            services.AddScoped<IQuoteOutcomeService, NoOpOutcomes>();
            _provider = services.BuildServiceProvider();
        }

        public SlaSweepWorker CreateSlaWorker() => new(
            _provider.GetRequiredService<IServiceScopeFactory>(),
            _provider.GetRequiredService<ITenantScopeAccessor>(),
            NullLogger<SlaSweepWorker>.Instance);

        public ErpRfqAutomationContext UnscopedContext() => new(_rawOptions, new StubTenant(null));

        public async Task SeedTwoTenantsWithOverdueLeadsAsync()
        {
            await using var seed = UnscopedContext();

            Seed.EnsureBusinessUnit(seed, Bu1);
            Seed.EnsureBusinessUnit(seed, Bu2);
            await seed.SaveChangesAsync();

            AddUser(seed, id: 11, buid: Bu1, email: "owner1@tenant1.test");
            AddUser(seed, id: 22, buid: Bu2, email: "owner2@tenant2.test");
            await seed.SaveChangesAsync();

            var overdue = DateTime.UtcNow.AddDays(-2);
            Seed.Lead(seed, leadId: 101, businessUnitId: Bu1).BidClosingDate = overdue;
            Seed.Lead(seed, leadId: 202, businessUnitId: Bu2).BidClosingDate = overdue;
            await seed.SaveChangesAsync();

            var lead1 = await seed.Leads.IgnoreQueryFilters().SingleAsync(l => l.Id == 101);
            var lead2 = await seed.Leads.IgnoreQueryFilters().SingleAsync(l => l.Id == 202);
            lead1.AssignTo = 11;
            lead2.AssignTo = 22;
            await seed.SaveChangesAsync();
        }

        private static void AddUser(ErpRfqAutomationContext ctx, long id, long buid, string email)
            => ctx.Users.Add(new User
            {
                Id = id,
                FirstName = "Owner",
                LastName = $"BU{buid}",
                Email = email,
                PasswordHash = "x",
                ImageUrl = "n/a",
                Buid = buid,
                IsActive = true,
                CreatedBy = "seed",
                CreatedOn = DateTime.UtcNow
            });

        public void Dispose()
        {
            _provider.Dispose();
            _connection.Dispose();
        }
    }
}
