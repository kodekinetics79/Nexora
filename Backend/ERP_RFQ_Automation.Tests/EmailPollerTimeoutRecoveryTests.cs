using ERP_RFQ_Automation.Ingestion.Assembly;
using ERP_RFQ_Automation.Ingestion.Triage;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Services;
using ERP_RFQ_Automation.Services.Interfaces;
using ERP_RFQ_Automation.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MimeKit;

namespace ERP_RFQ_Automation.Tests;

/// <summary>
/// Regression for the 2026-08-27 production readiness incident. The IMAP poll path used
/// CancellationToken.None, so a provider that stopped replying held the cycle forever and
/// background-workers correctly reported email-poller dead. These tests drive the real hosted
/// loop and prove the deadline is cooperative, recovery beats liveness, and shutdown is not
/// misreported as a mailbox incident.
/// </summary>
public sealed class EmailPollerTimeoutRecoveryTests
{
    [Fact]
    public async Task AStalledCycleIsCancelledRecordedAndTheWorkerBeatsAgain()
    {
        using var db = new TestDb();
        var service = new BlockingEmailService();
        var heartbeats = new BackgroundWorkerHeartbeats();
        var channel = new EmailPollerHealth();
        using var provider = Provider(db, service);
        var worker = new EmailBackgroundService(
            provider,
            new NoopLogger<EmailBackgroundService>(),
            heartbeats,
            channel,
            pollCycleTimeout: TimeSpan.FromMilliseconds(75));

        await worker.StartAsync(CancellationToken.None);
        await service.Started.Task.WaitAsync(TestWaits.Liveness);
        await service.Cancelled.Task.WaitAsync(TestWaits.Liveness);
        await WaitUntilAsync(
            () => heartbeats.Snapshot().Single().LastBeatUtc is not null,
            TestWaits.Liveness);

        var heartbeat = Assert.Single(heartbeats.Snapshot());
        Assert.Equal(BackgroundWorkerNames.EmailPoller, heartbeat.Worker);
        Assert.NotNull(heartbeat.LastBeatUtc);
        Assert.Contains("deadline", channel.LastFailureReason!, StringComparison.OrdinalIgnoreCase);
        Assert.False(channel.LastFailureIsPermanent);

        await worker.StopAsync(CancellationToken.None).WaitAsync(TestWaits.Liveness);
    }

    [Fact]
    public async Task HostShutdownCancelsPollingWithoutAFalseTimeoutOrHeartbeat()
    {
        using var db = new TestDb();
        var service = new BlockingEmailService();
        var heartbeats = new BackgroundWorkerHeartbeats();
        var channel = new EmailPollerHealth();
        using var provider = Provider(db, service);
        var worker = new EmailBackgroundService(
            provider,
            new NoopLogger<EmailBackgroundService>(),
            heartbeats,
            channel,
            pollCycleTimeout: TimeSpan.FromSeconds(30));

        await worker.StartAsync(CancellationToken.None);
        await service.Started.Task.WaitAsync(TestWaits.Liveness);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TestWaits.Liveness);
        await service.Cancelled.Task.WaitAsync(TestWaits.Liveness);

        Assert.Null(Assert.Single(heartbeats.Snapshot()).LastBeatUtc);
        Assert.Null(channel.LastFailureReason);
    }

    [Fact]
    public async Task EmailServiceHonoursCancellationBeforeMailboxDiscovery()
    {
        using var db = new TestDb();
        var service = EmailChannelTruthfulnessTests.CreateEmailServiceForTimeoutTest(
            db, new EmailPollerHealth(), out var temp);
        try
        {
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.FetchAndSaveLeadsAsync(null, cancelled.Token));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CanonicalCaptureTimeoutPreservesCheckpointAndRetryDoesNotDuplicateTheEmail()
    {
        const long businessUnitId = 8401;
        const long configurationId = 8402;
        using var db = new TestDb();
        var service = EmailChannelTruthfulnessTests.CreateEmailServiceForTimeoutTest(
            db, new EmailPollerHealth(), out var temp);
        try
        {
            await using (var seed = db.ContextFor(null))
            {
                Seed.EnsureBusinessUnit(seed, businessUnitId);
                Seed.EmailConfig(seed, configurationId, businessUnitId);
                await seed.SaveChangesAsync();
            }

            var message = new MimeMessage
            {
                MessageId = "capture-timeout@example.test",
                Subject = "RFQ 8401",
                Body = new TextPart("plain") { Text = "Please quote 10 cable trays." }
            };
            message.From.Add(MailboxAddress.Parse("buyer@example.test"));
            message.To.Add(MailboxAddress.Parse("sales@example.test"));

            var blocking = new BlockingIntake();
            await using (var first = db.ContextFor(businessUnitId))
            {
                var configuration = await first.EmailConfigurations.SingleAsync();
                using var deadline = new CancellationTokenSource();
                var attempt = service.ProcessSingleEmailAsync(
                    message, configuration, first, new StubLlm(), blocking,
                    cancellationToken: deadline.Token);
                await blocking.Entered.Task.WaitAsync(TestWaits.Liveness);
                deadline.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attempt);
            }

            await using (var afterTimeout = db.ContextFor(businessUnitId))
            {
                var checkpoint = Assert.Single(await afterTimeout.EmailIngests.ToListAsync());
                Assert.Equal("Pending", checkpoint.ParseStatus);
            }

            blocking.ReleaseWith(new EmailInquiryIntakeResult(
                1, Guid.NewGuid(), Scheduled: 1, AlreadyScheduled: 0, Held: 0,
                ExpectedComponents: 1, AlreadyCaptured: true, SafeToAcknowledge: true,
                FailureReason: null));
            await using (var retry = db.ContextFor(businessUnitId))
            {
                var configuration = await retry.EmailConfigurations.SingleAsync();
                Assert.True(await service.ProcessSingleEmailAsync(
                    message, configuration, retry, new StubLlm(), blocking));
            }

            await using var final = db.ContextFor(businessUnitId);
            Assert.Single(await final.EmailIngests.ToListAsync());
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { }
        }
    }

    private static ServiceProvider Provider(TestDb db, IEmailService service)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => db.ContextFor(null));
        services.AddScoped(_ => service);
        return services.BuildServiceProvider();
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!predicate())
            await Task.Delay(10, deadline.Token);
    }

    private sealed class BlockingEmailService : IEmailService
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MailboxPollReport> FetchAndSaveLeadsAsync(long? businessUnitId = null)
            => throw new InvalidOperationException("The hosted worker must use the cancellable overload.");

        public async Task<MailboxPollReport> FetchAndSaveLeadsAsync(
            long? businessUnitId, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return MailboxPollReport.Empty;
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
        }

        public Task SendEmailAsync(string to, string subject, string body,
            List<(string FileName, byte[] FileContent, string ContentType)> attachments = null!,
            string fromEmail = null!, long? businessUnitId = null) => Task.CompletedTask;
    }

    private sealed class BlockingIntake : IEmailInquiryIntakeService
    {
        private EmailInquiryIntakeResult? _released;
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseWith(EmailInquiryIntakeResult result) => _released = result;

        public async Task<EmailInquiryIntakeResult> CaptureAndScheduleAsync(
            MimeMessage message, EmailIngest ingest, EmailConfiguration configuration,
            string? freshBodyText, EmailTriageDecision triage, string? clientEmail,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            if (_released is not null) return _released;
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The blocking intake unexpectedly completed.");
        }

        public Task<EmailInquiryResumeResult> ResumeSchedulingAsync(
            long businessUnitId, long assemblyId, CancellationToken ct = default,
            EmailInquirySchedulingGrant? grant = null)
            => Task.FromResult(new EmailInquiryResumeResult(
                EmailInquiryResumeOutcome.NothingToResume, 0, 0));
    }
}
