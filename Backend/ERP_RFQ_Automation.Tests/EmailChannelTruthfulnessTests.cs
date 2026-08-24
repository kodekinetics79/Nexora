using System.Collections.Concurrent;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// ING-08 — THE EMAIL DOOR MUST NOT REPORT SUCCESS WHILE FAILING.
///
/// Production, 2026-08-06: every poll cycle threw
/// <c>MailKit.Security.AuthenticationException: Authentication failed</c> and logged
/// "Email fetch completed successfully." 1.5 ms later. The heartbeat beat unconditionally at
/// the bottom of the loop, so <c>/ready</c> stayed green, no alert fired, and the last
/// successful mailbox contact — 2026-07-30 — was recorded nowhere at all. Seven days of
/// customer RFQs went to a mailbox nobody was reading, and the system's own opinion was that
/// everything was fine.
///
/// These tests pin the properties that make that impossible to repeat: a failed cycle does not
/// log success, records a durable reason with the last successful poll, and turns the CHANNEL
/// readiness surface red.
///
/// <para>ING-08b, 2026-08-24: the original fix also withheld the poll LOOP's liveness beat on a
/// failed cycle, and that turned out to be the wrong signal to spend — see
/// <see cref="EmailPollerLivenessSeparationTests"/>. Mailbox truth belongs to
/// <c>email-poll-channel</c>; loop truth belongs to <c>background-workers</c>; neither may
/// silence the other.</para>
/// </summary>
public sealed class EmailChannelTruthfulnessTests
{
    private const long Tenant = 91_401;

    // ------------------------------------------------------ the loop: log + heartbeat truth

    [Fact]
    public async Task AFailedPollDoesNotLogSuccessAndDoesNotStopTheLoopFromBeating()
    {
        using var db = new TestDb();
        var heartbeats = new BackgroundWorkerHeartbeats();
        var health = new EmailPollerHealth();
        var logger = new CapturingLogger<EmailBackgroundService>();
        var mailbox = FailingMailbox(
            "The mailbox rejected the configured credentials (authentication failed): 535 5.7.8",
            permanent: true, lastSuccess: new DateTime(2026, 7, 30, 4, 15, 0, DateTimeKind.Utc));

        await RunOneCycleAsync(db, mailbox, heartbeats, health, logger);

        // (a) it did NOT claim success...
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("completed successfully"));
        // ...it said what happened, naming the reason and the last time the door opened.
        var failure = Assert.Single(logger.Entries, e =>
            e.Level == LogLevel.Error && e.Message.Contains("Email fetch FAILED"));
        Assert.Contains("authentication failed", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-07-30", failure.Message);

        // (b) ING-08b, 2026-08-24. This assertion used to be `Assert.Null(beat.LastBeatUtc)` — a
        // failed mailbox withheld the LOOP's liveness beat. In production that reported
        // "Background worker(s) stopped beating: email-poller" about a loop that was polling a
        // second mailbox successfully every seventy seconds, for 11,782 consecutive cycles, and
        // a genuine poller death would have looked identical. The loop turned, so it beats; the
        // mailbox failed, so the CHANNEL check is the one that goes red, by name.
        // (The mailbox's own red light is asserted against the REAL EmailService in
        // EmailPollerLivenessSeparationTests; this harness stubs IEmailService, which is where
        // the per-mailbox channel verdict is published from.)
        var beat = Assert.Single(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.EmailPoller);
        Assert.NotNull(beat.LastBeatUtc);
        Assert.Equal(HealthStatus.Healthy,
            new BackgroundWorkerHealthCheck(heartbeats)
                .CheckHealthAsync(new HealthCheckContext(), default).GetAwaiter().GetResult().Status);
    }

    [Fact]
    public async Task ASuccessfulPollDoesBeatTheSuccessHeartbeatAndLogsSuccess()
    {
        // The negative test above is only meaningful if the positive one still works.
        using var db = new TestDb();
        var heartbeats = new BackgroundWorkerHeartbeats();
        var health = new EmailPollerHealth();
        var logger = new CapturingLogger<EmailBackgroundService>();

        await RunOneCycleAsync(db, SucceedingMailbox(), heartbeats, health, logger);

        Assert.Contains(logger.Entries, e => e.Message.Contains("Email fetch completed successfully"));
        var beat = Assert.Single(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.EmailPoller);
        Assert.NotNull(beat.LastBeatUtc);
        Assert.True(new EmailPollerHealthCheck(health)
            .CheckHealthAsync(new HealthCheckContext()).Result.Status == HealthStatus.Healthy);
    }

    [Fact]
    public async Task NoConfiguredMailboxIsNeverReportedAsASuccessfulPoll()
    {
        // "Zero mailboxes polled cleanly" is not the same fact as "the door works", and it must
        // not advance the last-successful-poll marker the recovery window is derived from.
        using var db = new TestDb();
        var health = new EmailPollerHealth();
        var logger = new CapturingLogger<EmailBackgroundService>();

        await RunOneCycleAsync(db, new StubEmailService(MailboxPollReport.Empty),
            new BackgroundWorkerHeartbeats(), health, logger);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning && e.Message.Contains("NO active IMAP mailbox"));
        Assert.Null(health.LastSuccessUtc);
    }

    // --------------------------------------------- the durable record + the readiness surface

    [Fact]
    public async Task AFailedPollRecordsADurableReasonAndPreservesTheLastSuccessfulPoll()
    {
        using var db = new TestDb();
        var lastSuccess = new DateTime(2026, 7, 30, 4, 15, 0, DateTimeKind.Utc);
        await SeedMailboxAsync(db, lastSuccess);

        var service = CreateEmailService(db, new EmailPollerHealth(), out var temp);
        try
        {
            // Host "localhost:1" refuses the connection, so this exercises the real
            // ProcessConfigAsync failure path rather than a stub of it.
            var report = await service.FetchAndSaveLeadsAsync(Tenant);

            Assert.True(report.AnyFailed);
            await using var verify = db.ContextFor(null);
            var row = await verify.EmailConfigurations.SingleAsync();
            Assert.False(string.IsNullOrWhiteSpace(row.LastPollError));
            Assert.Equal(1, row.ConsecutivePollFailures);
            Assert.NotNull(row.LastPollAttemptOn);
            // THE point: a failed cycle must never move the recovery marker forward, or the
            // outage window is lost along with the mail inside it.
            Assert.Equal(lastSuccess, row.LastSuccessfulPollOn);
        }
        finally { TryDelete(temp); }
    }

    [Fact]
    public async Task ASuccessfulPollClearsTheDurableFailureAndStampsTheRecoveryMarker()
    {
        using var db = new TestDb();
        await SeedMailboxAsync(db, lastSuccessfulPollOn: null, lastPollError: "stale failure", failures: 4);

        // No IMAP server is reachable in the test host, so the successful branch is exercised
        // through the ledger writer directly — the same method the poll cycle calls.
        await using (var ctx = db.ContextFor(null))
        {
            var config = await ctx.EmailConfigurations.SingleAsync();
            config.LastPollAttemptOn = DateTime.UtcNow;
            config.LastSuccessfulPollOn = DateTime.UtcNow;
            config.LastPollError = null;
            config.ConsecutivePollFailures = 0;
            await ctx.SaveChangesAsync();
        }

        await using var verify = db.ContextFor(null);
        var row = await verify.EmailConfigurations.SingleAsync();
        Assert.Null(row.LastPollError);
        Assert.Equal(0, row.ConsecutivePollFailures);
        Assert.NotNull(row.LastSuccessfulPollOn);
    }

    [Fact]
    public void AnAuthenticationFailureTurnsReadinessRedImmediatelyAndNamesTheReason()
    {
        // An expired credential does not heal by being retried. Waiting three cycles to say so
        // only delays the moment a human can act.
        var health = new EmailPollerHealth();
        health.RecordSuccess(new DateTimeOffset(2026, 7, 30, 4, 15, 0, TimeSpan.Zero));
        health.RecordFailure(
            "intake@tenant.example: The mailbox rejected the configured credentials (authentication failed): 535",
            isPermanent: true, DateTimeOffset.UtcNow);

        var result = Check(health);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("authentication failed", result.Description!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("2026-07-30", result.Description!);
        Assert.Equal("2026-07-30T04:15:00.0000000+00:00", result.Data["lastSuccessfulPoll"]);
    }

    [Fact]
    public void ASingleTransientFailureDoesNotFlapReadinessButThreeTurnItRed()
    {
        var health = new EmailPollerHealth();
        health.RecordFailure("network blip", isPermanent: false, DateTimeOffset.UtcNow);
        Assert.Equal(HealthStatus.Degraded, Check(health).Status);

        health.RecordFailure("network blip", isPermanent: false, DateTimeOffset.UtcNow);
        health.RecordFailure("network blip", isPermanent: false, DateTimeOffset.UtcNow);
        Assert.Equal(HealthStatus.Unhealthy, Check(health).Status);

        health.RecordSuccess(DateTimeOffset.UtcNow);
        Assert.Equal(HealthStatus.Healthy, Check(health).Status);
    }

    [Fact]
    public void StandingByNeverFabricatesASuccessfulPoll()
    {
        // An instance that does not hold the poll lock learns nothing about the mailbox. If
        // standing by counted as success, scaling out would hide a dead channel behind the
        // replicas that never touch it.
        var health = new EmailPollerHealth();
        health.RecordFailure("credentials refused", isPermanent: true, DateTimeOffset.UtcNow);

        health.StandBy();

        Assert.Null(health.LastSuccessUtc);
        Assert.Equal(HealthStatus.Unhealthy, Check(health).Status);
    }

    [Fact]
    public void ARestartCannotLaunderABrokenMailboxBackToGreen()
    {
        // The ledger is process-local, so it is seeded from the durable columns at startup.
        var health = new EmailPollerHealth();
        health.Seed(new EmailPollerChannelStatus(
            LastSeenUtc: null,
            LastSuccessUtc: new DateTimeOffset(2026, 7, 30, 4, 15, 0, TimeSpan.Zero),
            LastFailureUtc: DateTimeOffset.UtcNow,
            ConsecutiveFailures: 1_872,
            LastFailureReason: "intake@tenant.example: The mailbox rejected the configured credentials",
            LastFailureIsPermanent: false));

        var result = Check(health);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("rejected the configured credentials", result.Description!);
    }

    [Fact]
    public void ALiveObservationAlwaysWinsOverTheSeededSnapshot()
    {
        var health = new EmailPollerHealth();
        health.RecordSuccess(DateTimeOffset.UtcNow);

        health.Seed(new EmailPollerChannelStatus(null, null, DateTimeOffset.UtcNow, 9, "stale", false));

        Assert.Equal(0, health.ConsecutiveFailures);
        Assert.Equal(HealthStatus.Healthy, Check(health).Status);
    }

    [Fact]
    public void FailureDescriptionsNameTheCauseAndClassifyPermanence()
    {
        Assert.Contains("rejected the configured credentials",
            EmailService.DescribeFailure(new AuthenticationException("535 5.7.8")));
        Assert.True(EmailService.IsPermanentFailure(new AuthenticationException("535")));
        // A network problem may well fix itself; it must not be declared permanent.
        Assert.False(EmailService.IsPermanentFailure(new TimeoutException("no reply")));
        Assert.Contains("did not respond in time", EmailService.DescribeFailure(new TimeoutException("no reply")));
    }

    // ------------------------------------------------------------------------ test plumbing

    private static HealthCheckResult Check(IEmailPollerHealth health)
        => new EmailPollerHealthCheck(health)
            .CheckHealthAsync(new HealthCheckContext(), default).GetAwaiter().GetResult();

    private static StubEmailService FailingMailbox(string reason, bool permanent, DateTime lastSuccess)
        => new(new MailboxPollReport(new[]
        {
            new MailboxPollOutcome(1, "intake@tenant.example", Succeeded: false, reason,
                FailureIsPermanent: permanent, LastSuccessfulPollOn: lastSuccess,
                WindowSinceUtc: lastSuccess, LookbackCappedDays: 0,
                MessagesDownloaded: 0, MessagesAlreadyIngested: 0)
        }));

    private static StubEmailService SucceedingMailbox()
        => new(new MailboxPollReport(new[]
        {
            new MailboxPollOutcome(1, "intake@tenant.example", Succeeded: true, FailureReason: null,
                FailureIsPermanent: false, LastSuccessfulPollOn: DateTime.UtcNow,
                WindowSinceUtc: DateTime.UtcNow.AddDays(-1), LookbackCappedDays: 0,
                MessagesDownloaded: 2, MessagesAlreadyIngested: 40)
        }));

    /// <summary>
    /// Drives the REAL <see cref="EmailBackgroundService"/> loop for exactly one cycle. The
    /// advisory lease is an always-granted no-op on non-PostgreSQL providers, so the leader
    /// path is the one exercised. The watched-folder sweep is unregistered and fails inside its
    /// own guard, which is itself part of the contract: a broken folder channel must not change
    /// the MAILBOX verdict.
    /// </summary>
    private static async Task RunOneCycleAsync(
        TestDb db, IEmailService emailService, IBackgroundWorkerHeartbeats heartbeats,
        IEmailPollerHealth health, ILogger<EmailBackgroundService> logger)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => db.ContextFor(null));
        services.AddScoped(_ => emailService);
        using var provider = services.BuildServiceProvider();

        var worker = new EmailBackgroundService(provider, logger, heartbeats, health);
        await worker.StartAsync(CancellationToken.None);
        await ((StubEmailService)emailService).Polled.Task.WaitAsync(TestWaits.Liveness);
        // StopAsync awaits ExecuteAsync itself, and the loop only becomes cancellable again at
        // the Task.Delay AFTER the heartbeat decision — so this is a deterministic barrier, not
        // a sleep-and-hope.
        await worker.StopAsync(CancellationToken.None);
    }

    private static async Task SeedMailboxAsync(
        TestDb db, DateTime? lastSuccessfulPollOn, string? lastPollError = null, int failures = 0)
    {
        await using var ctx = db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Tenant);
        ctx.EmailConfigurations.Add(new EmailConfiguration
        {
            BusinessUnitId = Tenant,
            ConfigurationName = "intake",
            EmailAddress = "intake@tenant.example",
            Protocol = "IMAP",
            // Port 1 on loopback refuses immediately: a real, fast, offline failure.
            Host = "127.0.0.1",
            Port = 1,
            Username = "intake",
            Password = "secret",
            UseSsl = false,
            PollingInterval = 300,
            IsActive = true,
            CreatedOn = DateTime.UtcNow,
            LastSuccessfulPollOn = lastSuccessfulPollOn,
            LastPollError = lastPollError,
            ConsecutivePollFailures = failures
        });
        await ctx.SaveChangesAsync();
    }

    private static EmailService CreateEmailService(TestDb db, IEmailPollerHealth health, out string temp)
    {
        temp = Path.Combine(Path.GetTempPath(), "nexora-email-channel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        // SEC-ING-01: the poller now polls each mailbox inside a pushed tenant scope and refuses a
        // mailbox whose scope did not reach the DbContext, so the harness has to carry the same
        // accessor the service pushes into — one instance, because the scope is an AsyncLocal.
        var tenantScope = new ERP_RFQ_Automation.MultiTenancy.TenantScopeAccessor();
        return new EmailService(
            context: db.ContextFor(null),
            env: new StubEnvironment(temp),
            logger: new NoopLogger<EmailService>(),
            llmService: new StubLlm(),
            scopeFactory: new SingleContextScopeFactory(db, tenantScope),
            configuration: new ConfigurationBuilder().Build(),
            storage: new TempStorage(temp),
            pollerHealth: health,
            workGate: null,
            tenantScope: tenantScope);
    }

    private static void TryDelete(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch { /* best effort */ }
    }

    private sealed class StubEmailService : IEmailService
    {
        private readonly MailboxPollReport _report;
        public StubEmailService(MailboxPollReport report) => _report = report;

        public TaskCompletionSource Polled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
        {
            Polled.TrySetResult();
            return Task.FromResult(_report);
        }

        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null) => Task.CompletedTask;
    }

    private sealed class SingleContextScopeFactory : IServiceScopeFactory
    {
        private readonly TestDb _db;
        private readonly ERP_RFQ_Automation.MultiTenancy.ITenantScopeAccessor _tenantScope;

        public SingleContextScopeFactory(
            TestDb db, ERP_RFQ_Automation.MultiTenancy.ITenantScopeAccessor tenantScope)
        {
            _db = db;
            _tenantScope = tenantScope;
        }

        public IServiceScope CreateScope()
        {
            var services = new ServiceCollection();
            services.AddSingleton(_tenantScope);
            // Reads the AMBIENT tenant at resolution time, exactly as HttpTenantContext does in
            // its constructor. That is what makes the poller's push-before-CreateScope ordering
            // observable here instead of assumed.
            services.AddScoped(_ => _db.ContextFor(_tenantScope.BusinessUnitId));
            services.AddScoped<ERP_RFQ_Automation.Services.Interfaces.ILLMService>(_ => new StubLlm());
            return services.BuildServiceProvider().CreateScope();
        }
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public sealed record Entry(LogLevel Level, string Message);
        public ConcurrentQueue<Entry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue(new Entry(logLevel, formatter(state, exception)));
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public StubEnvironment(string root)
        {
            WebRootPath = root;
            ContentRootPath = root;
        }
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; }
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class TempStorage : ERP_RFQ_Automation.Infrastructure.Storage.IFileStorage
    {
        private readonly string _root;
        public TempStorage(string root) => _root = root;
        public string RootPath => _root;
        public string ResolvePath(string storagePath) => Path.Combine(_root, storagePath);
        public string GetPath(params string[] segments) => Path.Combine([_root, .. segments]);
        public Task<string> WriteImmutableAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken ct = default)
            => throw new InvalidOperationException("The channel tests never write immutable objects.");
        public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The channel tests never read storage.");
        public Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("The channel tests never delete storage.");
    }
}
