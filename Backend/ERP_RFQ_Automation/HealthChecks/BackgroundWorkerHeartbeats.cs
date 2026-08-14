using System.Collections.Concurrent;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.HealthChecks;

/// <summary>
/// Canonical names for the background workers covered by
/// <see cref="BackgroundWorkerHealthCheck"/>. Extraction, quote-delivery and
/// procurement-dispatch keep their own dedicated heartbeats/health checks
/// (registered separately in Program.cs) and are deliberately NOT duplicated here.
/// </summary>
public static class BackgroundWorkerNames
{
    public const string SlaSweep = "sla-sweep";

    /// <summary>
    /// FR-INV-04. The reorder/overstock sweep. A faulted reorder loop is silent by construction —
    /// the failure mode is an alert that never arrives about stock that is quietly running out, and
    /// nobody reports the absence of a warning.
    /// </summary>
    public const string ReorderAlertSweep = "reorder-alert-sweep";
    public const string RoutingReconciliation = "routing-reconciliation";
    public const string EmailPoller = "email-poller";
    public const string AiReservationReconciliation = "ai-reservation-reconciliation";

    /// <summary>
    /// The scheduled billing run. A faulted billing loop is the most expensive silent
    /// failure the platform has — nothing is charged, no error surfaces, and the shortfall
    /// is only discovered when somebody asks why the month invoiced nothing.
    /// </summary>
    public const string BillingRun = "billing-run";

    /// <summary>
    /// FR-DSH-06 scheduled report delivery. A faulted reporting loop is silent by construction —
    /// the failure mode is an email that does not arrive, and nobody reports the absence of a report
    /// for weeks.
    /// </summary>
    public const string ScheduledReports = "scheduled-reports";

    /// <summary>
    /// The email-assembly recovery sweep. A faulted recovery loop is silent twice over: the
    /// defect it recovers from produces no error of its own, and a dead sweep produces none
    /// either — so an inquiry a customer sent simply never exists, and nobody reports the
    /// absence of a lead nobody knew to expect.
    /// </summary>
    public const string EmailInquiryAssemblyRecovery = "email-inquiry-assembly-recovery";
}

public sealed record BackgroundWorkerHeartbeatStatus(
    string Worker,
    DateTimeOffset RegisteredOnUtc,
    DateTimeOffset? LastBeatUtc,
    DateTimeOffset DeadlineUtc,
    bool IsAlive);

/// <summary>
/// Liveness ledger for the background workers that previously had NO heartbeat at
/// all. <c>HostOptions.BackgroundServiceExceptionBehavior</c> is
/// <c>Ignore</c> (Program.cs), so a worker whose <c>ExecuteAsync</c> faults is
/// silently gone for the lifetime of the process while <c>/ready</c> stays green.
/// Each worker registers itself in its constructor (so a worker that never reaches
/// its loop still fails the check once its startup grace expires) and beats once
/// per iteration.
///
/// A worker that is not registered is not checked — the check can therefore never
/// go red because of a worker the host chose not to run.
/// </summary>
public interface IBackgroundWorkerHeartbeats
{
    /// <summary>
    /// Declares a worker as expected-to-run. <paramref name="expectedInterval"/> is the
    /// worker's own loop period; the check allows three periods plus a minute of slack
    /// before it calls the worker dead.
    /// </summary>
    void Register(string worker, TimeSpan expectedInterval, TimeSpan? startupGrace = null);

    /// <summary>Records a completed iteration. Optionally re-states the expected period
    /// for workers whose interval is data-driven (the email poller reads it from the DB).</summary>
    void Beat(string worker, TimeSpan? expectedInterval = null);

    IReadOnlyList<BackgroundWorkerHeartbeatStatus> Snapshot();
}

public sealed class BackgroundWorkerHeartbeats : IBackgroundWorkerHeartbeats
{
    private static readonly TimeSpan MinimumTolerance = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Baseline ceiling on the staleness tolerance, so a worker that declares an absurd
    /// interval cannot make this check meaningless. See <see cref="Tolerance"/> for why it is
    /// a baseline rather than an absolute cap.
    /// </summary>
    private static readonly TimeSpan MaximumTolerance = TimeSpan.FromHours(6);
    private static readonly TimeSpan DefaultStartupGrace = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public BackgroundWorkerHeartbeats() : this(TimeProvider.System) { }

    public BackgroundWorkerHeartbeats(TimeProvider time) => _time = time;

    public void Register(string worker, TimeSpan expectedInterval, TimeSpan? startupGrace = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        var now = _time.GetUtcNow();
        _entries.AddOrUpdate(
            worker,
            _ => new Entry(now, Normalize(expectedInterval), startupGrace ?? DefaultStartupGrace),
            (_, existing) =>
            {
                existing.ExpectedInterval = Normalize(expectedInterval);
                return existing;
            });
    }

    public void Beat(string worker, TimeSpan? expectedInterval = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(worker);
        var entry = _entries.GetOrAdd(
            worker,
            _ => new Entry(_time.GetUtcNow(), Normalize(expectedInterval ?? MinimumTolerance), DefaultStartupGrace));
        if (expectedInterval is { } interval)
            entry.ExpectedInterval = Normalize(interval);
        entry.RecordBeat(_time.GetUtcNow());
    }

    public IReadOnlyList<BackgroundWorkerHeartbeatStatus> Snapshot()
    {
        var now = _time.GetUtcNow();
        return _entries
            .Select(pair =>
            {
                var entry = pair.Value;
                var lastBeat = entry.LastBeat;
                var deadline = lastBeat.HasValue
                    ? lastBeat.Value + Tolerance(entry.ExpectedInterval)
                    : entry.RegisteredOn + entry.StartupGrace;
                return new BackgroundWorkerHeartbeatStatus(
                    pair.Key, entry.RegisteredOn, lastBeat, deadline, now <= deadline);
            })
            .OrderBy(status => status.Worker, StringComparer.Ordinal)
            .ToList();
    }

    private static TimeSpan Normalize(TimeSpan interval)
        => interval <= TimeSpan.Zero ? MinimumTolerance : interval;

    /// <summary>
    /// How long a registered worker may stay silent before the check calls it dead: three of
    /// its own periods plus a minute of slack, bounded at both ends.
    ///
    /// <para>The upper bound is <see cref="MaximumTolerance"/> OR one period plus a minute,
    /// whichever is larger. The <c>Math.Max</c> is not a loosening — it is what stops the cap
    /// from being shorter than the interval it is judging. A worker cannot beat before its
    /// own period has elapsed, so a tolerance below that period declares the worker dead
    /// while it is doing exactly what it was configured to do, and an alarm that is red while
    /// nothing is wrong trains people to ignore it. It became reachable when the mailbox
    /// poll interval was corrected from seconds to minutes: the poller's documented maximum
    /// of 1440 stopped meaning 24 minutes and started meaning 24 hours, four times the flat
    /// six-hour cap. Workers at or under six hours — every other one today, including the
    /// six-hourly billing run — are unaffected but for that one minute of slack.</para>
    /// </summary>
    private static TimeSpan Tolerance(TimeSpan expectedInterval)
    {
        var slack = TimeSpan.FromMinutes(1);
        var tolerance = expectedInterval * 3 + slack;
        if (tolerance < MinimumTolerance) return MinimumTolerance;
        var ceiling = MaximumTolerance > expectedInterval + slack
            ? MaximumTolerance
            : expectedInterval + slack;
        return tolerance > ceiling ? ceiling : tolerance;
    }

    private sealed class Entry
    {
        private long _lastBeatTicks;

        public Entry(DateTimeOffset registeredOn, TimeSpan expectedInterval, TimeSpan startupGrace)
        {
            RegisteredOn = registeredOn;
            ExpectedInterval = expectedInterval;
            StartupGrace = startupGrace <= TimeSpan.Zero ? DefaultStartupGrace : startupGrace;
        }

        public DateTimeOffset RegisteredOn { get; }
        public TimeSpan StartupGrace { get; }

        private long _expectedIntervalTicks;
        public TimeSpan ExpectedInterval
        {
            get => new(Interlocked.Read(ref _expectedIntervalTicks));
            set => Interlocked.Exchange(ref _expectedIntervalTicks, value.Ticks);
        }

        public DateTimeOffset? LastBeat
        {
            get
            {
                var ticks = Interlocked.Read(ref _lastBeatTicks);
                return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
            }
        }

        public void RecordBeat(DateTimeOffset now)
            => Interlocked.Exchange(ref _lastBeatTicks, now.UtcDateTime.Ticks);
    }
}

/// <summary>
/// One readiness check covering every worker registered with
/// <see cref="IBackgroundWorkerHeartbeats"/>. Unhealthy names the dead workers so the
/// operator does not have to correlate logs to find which loop stopped.
/// </summary>
public sealed class BackgroundWorkerHealthCheck : IHealthCheck
{
    private readonly IBackgroundWorkerHeartbeats _heartbeats;

    public BackgroundWorkerHealthCheck(IBackgroundWorkerHeartbeats heartbeats) => _heartbeats = heartbeats;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var snapshot = _heartbeats.Snapshot();
        if (snapshot.Count == 0)
            return Task.FromResult(HealthCheckResult.Healthy("No background workers are registered."));

        var data = snapshot.ToDictionary(
            status => status.Worker,
            status => (object)(status.LastBeatUtc?.ToString("O") ?? "never"));

        var dead = snapshot.Where(status => !status.IsAlive).Select(status => status.Worker).ToList();
        return Task.FromResult(dead.Count == 0
            ? HealthCheckResult.Healthy($"{snapshot.Count} background worker(s) beating.", data)
            : HealthCheckResult.Unhealthy(
                $"Background worker(s) stopped beating: {string.Join(", ", dead)}.", data: data));
    }
}
