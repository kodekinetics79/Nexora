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
    public const string RoutingReconciliation = "routing-reconciliation";
    public const string EmailPoller = "email-poller";
    public const string AiReservationReconciliation = "ai-reservation-reconciliation";
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

    private static TimeSpan Tolerance(TimeSpan expectedInterval)
    {
        var tolerance = expectedInterval * 3 + TimeSpan.FromMinutes(1);
        if (tolerance < MinimumTolerance) return MinimumTolerance;
        return tolerance > MaximumTolerance ? MaximumTolerance : tolerance;
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
