using ERP_RFQ_Automation.HealthChecks;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Ingestion.Assembly;

/// <summary>
/// Runs <see cref="IEmailInquiryAssemblyRecoveryService"/> once at startup and then on its
/// configured interval.
///
/// <para><b>Once at startup, deliberately.</b> The failure this recovers from is a process that
/// died mid-assemble, and the commonest cause of that is the deploy that replaced it. The
/// instance that comes up is therefore the one most likely to have stranded messages waiting,
/// and making it wait a full interval before looking is the wrong default.</para>
///
/// <para><b>The advisory lease is an efficiency control, not the correctness boundary.</b> N
/// instances would each run the same platform-wide scan and then contend on the same rows.
/// The lease means one does. Correctness is the assembler's <c>FOR UPDATE</c> on the assembly
/// row, which holds whether the second caller is another sweep instance, a live extraction
/// worker, or both — so losing the lease is a skipped cycle, never a missed message.</para>
///
/// <para>The loop never dies. <c>HostOptions.BackgroundServiceExceptionBehavior</c> is
/// <c>Ignore</c>, so a faulted <c>ExecuteAsync</c> would be silently gone for the lifetime of
/// the process while <c>/ready</c> stayed green — which is exactly the class of invisible
/// failure this worker exists to remove, and it must not reintroduce it one level up.</para>
/// </summary>
public sealed class EmailInquiryAssemblyRecoveryWorker : BackgroundService
{
    /// <summary>
    /// Stable across deployments and instances. Changing it silently permits two leaders.
    /// </summary>
    internal const string LockName = "email-inquiry-assembly-recovery";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly EmailInquiryAssemblyRecoveryOptions _options;
    private readonly ILogger<EmailInquiryAssemblyRecoveryWorker> _log;
    private readonly IBackgroundWorkerHeartbeats? _heartbeats;
    private readonly Platform.Hardening.NexoraMetrics? _metrics;

    public EmailInquiryAssemblyRecoveryWorker(
        IServiceScopeFactory scopeFactory,
        EmailInquiryAssemblyRecoveryOptions options,
        ILogger<EmailInquiryAssemblyRecoveryWorker> log,
        IBackgroundWorkerHeartbeats? heartbeats = null,
        Platform.Hardening.NexoraMetrics? metrics = null)
    {
        _scopeFactory = scopeFactory;
        _options = options;
        _log = log;
        _heartbeats = heartbeats;
        _metrics = metrics;

        // Registered in the CONSTRUCTOR so a worker that never reaches its loop still fails the
        // liveness check once its startup grace expires. A worker that is never constructed is
        // never registered and never checked, so a host that disables this cannot go red for it.
        if (_options.Enabled)
            _heartbeats?.Register(
                BackgroundWorkerNames.EmailInquiryAssemblyRecovery, _options.ValidatedInterval);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogWarning(
                "Email inquiry assembly recovery is DISABLED. A message whose parts all finished "
                + "but whose lead was never built will stay stranded with nothing to find it.");
            return;
        }

        var interval = _options.ValidatedInterval;
        _log.LogInformation(
            "EmailInquiryAssemblyRecoveryWorker starting; interval {Interval}, batch {Batch} per "
            + "tenant, minimum age {MinimumAge}.",
            interval, _options.ValidatedBatchSize, _options.ValidatedMinimumAge);
        _heartbeats?.Beat(BackgroundWorkerNames.EmailInquiryAssemblyRecovery, interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunGuardedSweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _log.LogError(exception,
                    "Email inquiry assembly recovery sweep failed; retrying next interval.");
            }

            // Beat AFTER the iteration whether or not it succeeded: the heartbeat asserts the
            // LOOP is alive. A sweep that keeps failing is caught by the error log, not by
            // flipping readiness red on one transient database blip.
            _heartbeats?.Beat(BackgroundWorkerNames.EmailInquiryAssemblyRecovery, interval);

            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }

        _log.LogInformation("EmailInquiryAssemblyRecoveryWorker stopped.");
    }

    /// <summary>
    /// Acquires the lease, sweeps, and reports. Internal so a test can drive one cycle without
    /// the loop and without a timer.
    /// </summary>
    internal async Task<EmailInquiryRecoverySweepResult> RunGuardedSweepAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();

        await using var lease = await PostgresAdvisoryLease.TryAcquireAsync(context, LockName, ct);
        if (lease is null)
        {
            // Another instance is sweeping. Stand by rather than wait, so this instance stays
            // responsive and takes over the moment the holder disappears.
            _log.LogDebug("Email inquiry assembly recovery skipped: another instance holds the lease.");
            return EmailInquiryRecoverySweepResult.Skipped;
        }

        var service = scope.ServiceProvider.GetRequiredService<IEmailInquiryAssemblyRecoveryService>();
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            var result = await service.SweepOnceAsync(ct);
            _metrics?.AssemblyRecoverySwept(
                result.Candidates, result.Recovered, result.Failed, result.Duration.TotalMilliseconds);
            return result;
        }
        catch
        {
            // A sweep that throws — tenant enumeration failed, the gate refused, shutdown
            // cancelled it mid-way — used to record nothing at all, so the failure counter was
            // silent in exactly the case an operator most needs it.
            _metrics?.AssemblyRecoverySwept(
                candidates: 0, recovered: 0, failed: 1,
                durationMs: System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            throw;
        }
    }
}
