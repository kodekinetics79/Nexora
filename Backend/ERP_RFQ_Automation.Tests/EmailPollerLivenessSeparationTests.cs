using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// ING-08b — ONE DEAD MAILBOX MUST NOT MASK A DEAD POLLER.
///
/// <para>Production, 2026-08-24. <c>/ready</c> had been 503 for the entire life of the deployment
/// with two red checks: <c>email-poll-channel</c> ("Inbound mail channel is failing:
/// info@intelliflowsystem.com … Consecutive failed cycles: 11785") and <c>background-workers</c>
/// ("Background worker(s) stopped beating: email-poller"). The second was FALSE. The logs showed
/// the poller reading info@kodekinetics.com successfully every ~70 seconds, including four
/// minutes before the failing check ran.</para>
///
/// <para>The cause was one line. <c>EmailBackgroundService</c> beat the liveness heartbeat only
/// <c>if (cycleSucceeded is not false)</c>, and <c>RunPollCycleAsync</c> returned false whenever
/// <c>report.AnyFailed</c> — ANY mailbox. <c>Email_Configurations</c> id 5 had failed
/// authentication 11,782 consecutive times and had never once succeeded
/// (<c>LastSuccessfulPollOn</c> null), so <c>AnyFailed</c> was true on every cycle and the poller
/// never beat once since process start. The liveness signal was permanently red, which means a
/// REAL poller death was indistinguishable from the standing false alarm — the exact failure the
/// heartbeat exists to catch, masked by the heartbeat itself.</para>
///
/// <para>These tests pin the separation: the loop's heartbeat answers "is this loop turning", the
/// channel check answers "can each mailbox be read", and neither can silence the other.</para>
/// </summary>
public sealed class EmailPollerLivenessSeparationTests
{
    private const long Tenant = 91_402;

    // Modelled on the two production rows.
    private const long BrokenMailboxId = 5;
    private const string BrokenMailbox = "info@intelliflowsystem.com";
    private const long WorkingMailboxId = 9;
    private const string WorkingMailbox = "info@kodekinetics.com";

    private const string AuthFailure =
        "The mailbox rejected the configured credentials (authentication failed): 535 5.7.8";

    // ------------------------------------------------------------------ 1. liveness is liveness

    [Fact]
    public async Task A_cycle_with_one_healthy_and_one_failing_mailbox_beats_the_poller_heartbeat()
    {
        // THE REGRESSION. Two mailboxes, exactly as production: id 5 has never authenticated,
        // id 9 polls cleanly. Before the fix this cycle withheld the beat and `background-workers`
        // went on to report the loop as stopped.
        using var db = new TestDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero));
        var heartbeats = new BackgroundWorkerHeartbeats(clock);
        var health = new EmailPollerHealth();

        await RunOneCycleAsync(db, MixedMailboxes(), heartbeats, health);

        var beat = Assert.Single(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.EmailPoller);
        Assert.NotNull(beat.LastBeatUtc);
        Assert.True(beat.IsAlive);
        Assert.Equal(HealthStatus.Healthy, Workers(heartbeats).Status);
    }

    [Fact]
    public async Task A_cycle_in_which_the_only_mailbox_fails_still_beats_the_poller_heartbeat()
    {
        // Beating on "at least one mailbox succeeded" would have looked like a fix and was
        // rejected: with a single configured mailbox — the common case — it collapses straight
        // back into "every mailbox succeeded" and the false "worker stopped beating" returns.
        // The loop turned. That is the whole claim the heartbeat makes.
        using var db = new TestDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero));
        var heartbeats = new BackgroundWorkerHeartbeats(clock);
        var health = new EmailPollerHealth();

        await RunOneCycleAsync(db, OnlyBrokenMailbox(), heartbeats, health);

        var beat = Assert.Single(heartbeats.Snapshot(), s => s.Worker == BackgroundWorkerNames.EmailPoller);
        Assert.NotNull(beat.LastBeatUtc);
        Assert.Equal(HealthStatus.Healthy, Workers(heartbeats).Status);
    }

    [Fact]
    public async Task A_loop_that_stops_turning_still_goes_red_and_is_named()
    {
        // The control. Making the heartbeat unconditional on mailbox outcomes must not make it
        // unconditional full stop, or the alarm is merely quiet instead of merely loud. One real
        // cycle beats; nothing beats after it; the tolerance (3 x 5 min + 1 min) expires and
        // `background-workers` names email-poller — on its own evidence, not a mailbox's.
        using var db = new TestDb();
        var clock = new FakeClock(new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero));
        var heartbeats = new BackgroundWorkerHeartbeats(clock);

        await RunOneCycleAsync(db, MixedMailboxes(), heartbeats, new EmailPollerHealth());
        Assert.Equal(HealthStatus.Healthy, Workers(heartbeats).Status);

        clock.Advance(TimeSpan.FromMinutes(20));

        var result = Workers(heartbeats);
        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains(BackgroundWorkerNames.EmailPoller, result.Description);
    }

    // ------------------------------------------------- 2. the mailbox fault keeps its own name

    [Fact]
    public void A_failing_mailbox_is_reported_under_its_own_identity_not_as_a_dead_worker()
    {
        var now = new DateTimeOffset(2026, 8, 24, 9, 0, 0, TimeSpan.Zero);
        var health = new EmailPollerHealth();
        health.RecordSuccess(WorkingMailboxId, WorkingMailbox, now);
        health.RecordFailure(BrokenMailboxId, BrokenMailbox, AuthFailure, isPermanent: true, now);

        var channel = Channel(health);

        Assert.Equal(HealthStatus.Unhealthy, channel.Status);
        // WHICH mailbox, by the id an operator types into the mailbox screen.
        Assert.Contains($"mailbox {BrokenMailboxId} {BrokenMailbox}", channel.Description);
        Assert.Contains("authentication failed", channel.Description!, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 of 2 mailbox(es) failing", channel.Description);
        // ...and the mailbox that is working is still on the record, by name.
        Assert.Contains(WorkingMailbox, channel.Description);
        Assert.DoesNotContain($"mailbox {WorkingMailboxId} {WorkingMailbox}: ", channel.Description);
        // The sentence that tells the two failure modes apart at a glance.
        Assert.Contains("not a stopped poller", channel.Description);
    }

    [Fact]
    public void A_broken_mailbox_does_not_erase_the_working_mailboxs_last_successful_read()
    {
        // The production lie: "Last successful poll: never" on a deployment whose other mailbox
        // was being read every seventy seconds. RecordSuccess() was only ever called when EVERY
        // mailbox succeeded, so with one permanently broken row it was never called at all.
        var read = new DateTimeOffset(2026, 8, 24, 8, 56, 0, TimeSpan.Zero);
        var health = new EmailPollerHealth();
        health.RecordSuccess(WorkingMailboxId, WorkingMailbox, read);
        health.RecordFailure(BrokenMailboxId, BrokenMailbox, AuthFailure, isPermanent: true, read.AddSeconds(1));

        Assert.Equal(read, health.LastSuccessUtc);
        var working = Assert.Single(health.Mailboxes, m => m.MailboxId == WorkingMailboxId);
        Assert.Equal(read, working.LastSuccessUtc);
        Assert.False(working.IsFailing);
        // The line an operator reads on /ready. It said "never" in production while this mailbox
        // was being read a thousand times a day.
        var channel = Channel(health);
        Assert.Equal(read.ToString("O"), channel.Data["lastSuccessfulPoll"]);
        Assert.Contains($"{WorkingMailbox} (last read {read:O})", channel.Description);
    }

    [Fact]
    public void A_mailbox_that_has_never_once_succeeded_is_called_an_unfinished_setup()
    {
        // Item 3, the reportable half. A mailbox with thousands of failures and no lifetime
        // success is not an outage — it is a setup somebody started and never finished, and the
        // operator action ("fix the credentials or deactivate it") is different. The poller
        // deliberately keeps retrying it; see EmailBackgroundService for why it is not
        // auto-disabled.
        var health = new EmailPollerHealth();
        for (var i = 0; i < 11_782; i++)
            health.RecordFailure(BrokenMailboxId, BrokenMailbox, AuthFailure, true, DateTimeOffset.UtcNow);

        var result = Channel(health);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("11782 consecutive failed cycle(s)", result.Description);
        Assert.Contains("NEVER been read successfully since it was configured", result.Description);
        Assert.Contains("deactivate it", result.Description);
    }

    [Fact]
    public void A_mailbox_that_worked_and_broke_is_reported_as_an_outage_not_a_setup_problem()
    {
        var health = new EmailPollerHealth();
        health.RecordSuccess(WorkingMailboxId, WorkingMailbox, new DateTimeOffset(2026, 7, 30, 4, 15, 0, TimeSpan.Zero));
        health.RecordFailure(WorkingMailboxId, WorkingMailbox, AuthFailure, true, DateTimeOffset.UtcNow);

        var result = Channel(health);

        Assert.Contains("Last successful read 2026-07-30", result.Description);
        Assert.DoesNotContain("NEVER been read successfully", result.Description);
    }

    [Fact]
    public void A_cycle_that_never_reached_a_mailbox_is_reported_as_a_cycle_fault()
    {
        // The leader lock could not be evaluated, or the fetch threw. That is not a mailbox's
        // fault and must not be filed under one — previously this branch recorded nothing at all
        // and the channel check happily answered "has not completed a poll cycle yet".
        var health = new EmailPollerHealth();
        health.RecordCycleFailure(
            "The email-poller lock could not be evaluated (NpgsqlException: connection reset), "
            + "so no mailbox was polled this cycle.",
            isPermanent: false, DateTimeOffset.UtcNow);
        health.RecordCycleFailure("again", false, DateTimeOffset.UtcNow);
        health.RecordCycleFailure("and again", false, DateTimeOffset.UtcNow);

        var result = Channel(health);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("POLL CYCLE", result.Description);
        Assert.DoesNotContain("mailbox(es) failing", result.Description);

        // ...and a cycle that completes clears it without claiming any mailbox was read.
        health.RecordCycleCompleted(DateTimeOffset.UtcNow);
        Assert.Equal(HealthStatus.Healthy, Channel(health).Status);
        Assert.Null(health.LastSuccessUtc);
    }

    // ----------------------------------------- 3. the real poller publishes one verdict PER row

    [Fact]
    public async Task The_real_poller_records_a_verdict_per_mailbox_row_not_one_per_cycle()
    {
        // Wiring, not unit: EmailService.PublishChannelHealth is what erased the healthy
        // mailbox's success, and it is private. This drives the real FetchAndSaveLeadsAsync over
        // two real Email_Configurations rows and asserts the ledger came back keyed by row id.
        using var db = new TestDb();
        var (brokenId, otherId) = await SeedTwoMailboxesAsync(db);
        var health = new EmailPollerHealth();
        var service = CreateEmailService(db, health, out var temp);
        try
        {
            var report = await service.FetchAndSaveLeadsAsync(Tenant);

            Assert.Equal(2, report.Polled);
            Assert.Equal(2, report.Failed);
            Assert.Equal(2, health.Mailboxes.Count);
            var broken = Assert.Single(health.Mailboxes, m => m.MailboxId == brokenId);
            var other = Assert.Single(health.Mailboxes, m => m.MailboxId == otherId);
            Assert.Equal(BrokenMailbox, broken.Mailbox);
            Assert.Equal(WorkingMailbox, other.Mailbox);
            Assert.Equal(1, broken.ConsecutiveFailures);
            Assert.Equal(1, other.ConsecutiveFailures);

            var description = Channel(health).Description!;
            Assert.Contains($"mailbox {brokenId} {BrokenMailbox}", description);
            Assert.Contains($"mailbox {otherId} {WorkingMailbox}", description);
            Assert.Contains("2 of 2 mailbox(es) failing", description);
        }
        finally { TryDelete(temp); }
    }

    [Fact]
    public async Task A_second_failed_cycle_advances_only_the_mailbox_that_failed_again()
    {
        // Per-mailbox counters, not one shared number: the old ledger reported the WORST count
        // across every mailbox as though it belonged to all of them.
        using var db = new TestDb();
        var (brokenId, otherId) = await SeedTwoMailboxesAsync(db);
        var health = new EmailPollerHealth();
        var service = CreateEmailService(db, health, out var temp);
        try
        {
            await service.FetchAndSaveLeadsAsync(Tenant);
            // The second mailbox recovers; only the first keeps failing.
            health.RecordSuccess(otherId, WorkingMailbox, DateTimeOffset.UtcNow);
            await service.FetchAndSaveLeadsAsync(Tenant);

            Assert.Equal(2, Assert.Single(health.Mailboxes, m => m.MailboxId == brokenId).ConsecutiveFailures);
            Assert.Equal(1, Assert.Single(health.Mailboxes, m => m.MailboxId == otherId).ConsecutiveFailures);
        }
        finally { TryDelete(temp); }
    }

    [Fact]
    public async Task Deactivating_the_broken_mailbox_clears_the_alarm_on_the_next_cycle()
    {
        // The remedy an operator actually has: turn the mailbox off on Setup > Email Inboxes.
        // The ledger is process-local and only ever WRITTEN by a poll, so without a retirement
        // step the mailbox's last failure stayed on /ready until the next deploy — an alarm that
        // stays on after the fault is fixed is how people learn to ignore it.
        using var db = new TestDb();
        var health = new EmailPollerHealth();
        health.RecordFailure(BrokenMailboxId, BrokenMailbox, AuthFailure, isPermanent: true, DateTimeOffset.UtcNow);
        health.RecordSuccess(WorkingMailboxId, WorkingMailbox, DateTimeOffset.UtcNow);
        Assert.Equal(HealthStatus.Unhealthy, Channel(health).Status);

        // The next full cycle polls only the mailbox that is still active.
        await RunOneCycleAsync(
            db, OnlyWorkingMailbox(), new BackgroundWorkerHeartbeats(), health);

        Assert.Equal(HealthStatus.Healthy, Channel(health).Status);
        Assert.DoesNotContain(health.Mailboxes, m => m.MailboxId == BrokenMailboxId);
        // ...and the mailbox that is still there keeps its record.
        Assert.Contains(health.Mailboxes, m => m.MailboxId == WorkingMailboxId);
    }

    // ------------------------------------------------------------------------ test plumbing

    private static HealthCheckResult Channel(IEmailPollerHealth health)
        => new EmailPollerHealthCheck(health)
            .CheckHealthAsync(new HealthCheckContext(), default).GetAwaiter().GetResult();

    private static HealthCheckResult Workers(IBackgroundWorkerHeartbeats heartbeats)
        => new BackgroundWorkerHealthCheck(heartbeats)
            .CheckHealthAsync(new HealthCheckContext(), default).GetAwaiter().GetResult();

    /// <summary>The two production rows: id 5 has never authenticated, id 9 polls cleanly.</summary>
    private static StubEmailService MixedMailboxes()
        => new(new MailboxPollReport(new[]
        {
            new MailboxPollOutcome(BrokenMailboxId, BrokenMailbox, Succeeded: false, AuthFailure,
                FailureIsPermanent: true, LastSuccessfulPollOn: null,
                WindowSinceUtc: DateTime.UtcNow.AddDays(-1), LookbackCappedDays: 0,
                MessagesDownloaded: 0, MessagesAlreadyIngested: 0),
            new MailboxPollOutcome(WorkingMailboxId, WorkingMailbox, Succeeded: true, FailureReason: null,
                FailureIsPermanent: false, LastSuccessfulPollOn: DateTime.UtcNow,
                WindowSinceUtc: DateTime.UtcNow.AddDays(-1), LookbackCappedDays: 0,
                MessagesDownloaded: 1, MessagesAlreadyIngested: 12)
        }));

    private static StubEmailService OnlyWorkingMailbox()
        => new(new MailboxPollReport(new[]
        {
            new MailboxPollOutcome(WorkingMailboxId, WorkingMailbox, Succeeded: true, FailureReason: null,
                FailureIsPermanent: false, LastSuccessfulPollOn: DateTime.UtcNow,
                WindowSinceUtc: DateTime.UtcNow.AddDays(-1), LookbackCappedDays: 0,
                MessagesDownloaded: 0, MessagesAlreadyIngested: 12)
        }));

    private static StubEmailService OnlyBrokenMailbox()
        => new(new MailboxPollReport(new[]
        {
            new MailboxPollOutcome(BrokenMailboxId, BrokenMailbox, Succeeded: false, AuthFailure,
                FailureIsPermanent: true, LastSuccessfulPollOn: null,
                WindowSinceUtc: DateTime.UtcNow.AddDays(-1), LookbackCappedDays: 0,
                MessagesDownloaded: 0, MessagesAlreadyIngested: 0)
        }));

    /// <summary>
    /// Drives the REAL <see cref="EmailBackgroundService"/> loop for exactly one cycle — the
    /// entry point the host actually calls, so the heartbeat decision under test is the one that
    /// runs in production. The advisory lease is an always-granted no-op on non-PostgreSQL
    /// providers, so the leader path is the one exercised.
    /// </summary>
    private static async Task RunOneCycleAsync(
        TestDb db, StubEmailService emailService,
        IBackgroundWorkerHeartbeats heartbeats, IEmailPollerHealth health)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => db.ContextFor(null));
        services.AddScoped<IEmailService>(_ => emailService);
        using var provider = services.BuildServiceProvider();

        var worker = new EmailBackgroundService(
            provider, new NoopLogger<EmailBackgroundService>(), heartbeats, health);
        await worker.StartAsync(CancellationToken.None);
        await emailService.Polled.Task.WaitAsync(TestWaits.Liveness);
        // StopAsync awaits ExecuteAsync itself, and the loop only becomes cancellable again at
        // the Task.Delay AFTER the heartbeat decision — a deterministic barrier, not a sleep.
        await worker.StopAsync(CancellationToken.None);
    }

    private static async Task<(long BrokenId, long OtherId)> SeedTwoMailboxesAsync(TestDb db)
    {
        await using var ctx = db.ContextFor(null);
        Seed.EnsureBusinessUnit(ctx, Tenant);
        var broken = NewMailbox(BrokenMailbox);
        var other = NewMailbox(WorkingMailbox);
        ctx.EmailConfigurations.AddRange(broken, other);
        await ctx.SaveChangesAsync();
        return (broken.Id, other.Id);
    }

    private static EmailConfiguration NewMailbox(string address) => new()
    {
        BusinessUnitId = Tenant,
        ConfigurationName = address,
        EmailAddress = address,
        Protocol = "IMAP",
        // Port 1 on loopback: a real, fast, offline failure for both rows. What is under test is
        // that the two rows are reported SEPARATELY, which is independent of why each failed.
        Host = "127.0.0.1",
        Port = 1,
        Username = address,
        Password = "secret",
        UseSsl = false,
        PollingInterval = 5,
        IsActive = true,
        CreatedOn = DateTime.UtcNow
    };

    private static EmailService CreateEmailService(TestDb db, IEmailPollerHealth health, out string temp)
    {
        temp = Path.Combine(Path.GetTempPath(), "nexora-poller-liveness-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
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

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class StubEmailService(MailboxPollReport report) : IEmailService
    {
        public TaskCompletionSource Polled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
        {
            Polled.TrySetResult();
            return Task.FromResult(report);
        }

        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null) => Task.CompletedTask;
    }

    private sealed class SingleContextScopeFactory(
        TestDb db, ERP_RFQ_Automation.MultiTenancy.ITenantScopeAccessor tenantScope) : IServiceScopeFactory
    {
        public IServiceScope CreateScope()
        {
            var services = new ServiceCollection();
            services.AddSingleton(tenantScope);
            services.AddScoped(_ => db.ContextFor(tenantScope.BusinessUnitId));
            services.AddScoped<ILLMService>(_ => new StubLlm());
            return services.BuildServiceProvider().CreateScope();
        }
    }

    private sealed class StubEnvironment(string root) : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Test";
    }

    private sealed class TempStorage(string root) : ERP_RFQ_Automation.Infrastructure.Storage.IFileStorage
    {
        public string RootPath => root;
        public string ResolvePath(string storagePath) => Path.Combine(root, storagePath);
        public string GetPath(params string[] segments) => Path.Combine([root, .. segments]);
        public Task<string> WriteImmutableAsync(string relativePath, ReadOnlyMemory<byte> content, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests never write immutable objects.");
        public Task<Stream> OpenReadAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests never read storage.");
        public Task<bool> TryDeleteAsync(string storagePath, CancellationToken ct = default)
            => throw new InvalidOperationException("These tests never delete storage.");
    }
}
