using System.Collections.Generic;
using System.Linq;

namespace ERP_RFQ_Automation.Platform.Hardening;

/// <summary>
/// One row of the single grouped queue query the poller runs. Deliberately a flat,
/// provider-agnostic shape so <see cref="ExtractionQueueSnapshot.From"/> can be unit
/// tested with no database at all.
/// </summary>
/// <param name="BusinessUnitId">Tenant the group belongs to.</param>
/// <param name="Status">Queue status name (<c>ExtractionStatus</c> as text).</param>
/// <param name="Ready">True when <c>NextAttemptAt &lt;= now</c> — i.e. not backed off.</param>
/// <param name="LeaseLapsed">True when the row carries no live lease (null or expired).</param>
/// <param name="Count">Rows in the group.</param>
/// <param name="OldestCreatedOnUtc">Oldest <c>CreatedOn</c> in the group.</param>
public readonly record struct ExtractionQueueGroup(
    long BusinessUnitId,
    string Status,
    bool Ready,
    bool LeaseLapsed,
    long Count,
    DateTime? OldestCreatedOnUtc,
    bool InvariantBlocked = false,
    bool Retry = false,
    bool RepeatedInvariantViolation = false);

/// <summary>
/// One tenant's queue posture. <see cref="OldestPendingAgeSeconds"/> is the number that
/// actually reveals a starving tenant: total queue depth stays flat while one tenant's
/// head-of-line job ages without bound, so depth alone cannot see it.
/// </summary>
public sealed record TenantQueueGauge(
    long BusinessUnitId,
    double OldestPendingAgeSeconds,
    long Pending,
    long PendingReady,
    long PendingBackedOff,
    long InFlight,
    long ExpiredLeases,
    long DeadLettered,
    long InvariantBlocked = 0,
    double OldestInvariantBlockedAgeSeconds = 0,
    long Retried = 0,
    long RepeatedInvariantViolations = 0);

/// <summary>
/// Immutable, point-in-time answer to "what does the extraction queue look like right
/// now, per tenant". Produced by <see cref="ExtractionQueueMetricsPoller"/> on a bounded
/// interval and read (never computed) by the ObservableGauges in
/// <see cref="NexoraMetrics"/>, so a metrics scrape costs zero database round-trips.
/// </summary>
public sealed record ExtractionQueueSnapshot(
    DateTimeOffset TakenAtUtc,
    IReadOnlyList<TenantQueueGauge> Tenants,
    long UnreportedTenants,
    bool IsFresh,
    string? Error,
    long InvariantAffectedTenants = 0)
{
    /// <summary>The "we have never successfully polled" value. Publishes no tenant series.</summary>
    public static readonly ExtractionQueueSnapshot Empty =
        new(DateTimeOffset.MinValue, Array.Empty<TenantQueueGauge>(), 0, IsFresh: false, Error: null);

    /// <summary>Statuses that mean "a worker holds (or held) this row".</summary>
    private static readonly HashSet<string> InFlightStatuses =
        new(StringComparer.Ordinal) { "Leased", "Extracting", "Persisting" };

    /// <summary>
    /// Folds the grouped query rows into per-tenant gauges. Pure: the caller supplies
    /// <paramref name="nowUtc"/>, so the computation is deterministic and testable.
    ///
    /// <para><b>Cardinality guard.</b> At most <paramref name="maxTenants"/> tenant series
    /// are published, chosen by the worst <see cref="TenantQueueGauge.OldestPendingAgeSeconds"/>
    /// first — the tenants an operator would page on. Everything beyond that is collapsed
    /// into <see cref="UnreportedTenants"/> rather than being silently dropped.</para>
    /// </summary>
    public static ExtractionQueueSnapshot From(
        IEnumerable<ExtractionQueueGroup> groups, DateTimeOffset nowUtc, int maxTenants = 200)
    {
        ArgumentNullException.ThrowIfNull(groups);
        if (maxTenants < 1) maxTenants = 1;

        var accumulators = new Dictionary<long, Accumulator>();
        foreach (var group in groups)
        {
            if (group.Count <= 0) continue;
            if (!accumulators.TryGetValue(group.BusinessUnitId, out var accumulator))
                accumulators[group.BusinessUnitId] = accumulator = new Accumulator();

            if (string.Equals(group.Status, "Pending", StringComparison.Ordinal))
            {
                accumulator.Pending += group.Count;
                if (group.Ready) accumulator.PendingReady += group.Count;
                else accumulator.PendingBackedOff += group.Count;
                // Age is measured from CreatedOn — the moment the tenant handed us the
                // work — NOT from NextAttemptAt. A job looping through exponential
                // backoff keeps a fresh NextAttemptAt forever while genuinely starving.
                if (group.OldestCreatedOnUtc is { } created
                    && (accumulator.OldestPendingUtc is null || created < accumulator.OldestPendingUtc))
                    accumulator.OldestPendingUtc = created;
            }
            else if (InFlightStatuses.Contains(group.Status))
            {
                accumulator.InFlight += group.Count;
                if (group.LeaseLapsed) accumulator.ExpiredLeases += group.Count;
            }
            else if (string.Equals(group.Status, "DeadLetter", StringComparison.Ordinal))
            {
                accumulator.DeadLettered += group.Count;
                if (group.InvariantBlocked)
                {
                    accumulator.InvariantBlocked += group.Count;
                    if (group.OldestCreatedOnUtc is { } created
                        && (accumulator.OldestInvariantBlockedUtc is null
                            || created < accumulator.OldestInvariantBlockedUtc))
                        accumulator.OldestInvariantBlockedUtc = created;
                }
            }
            if (group.Retry) accumulator.Retried += group.Count;
            if (group.RepeatedInvariantViolation)
                accumulator.RepeatedInvariantViolations += group.Count;
        }

        var tenants = accumulators
            .Select(pair => new TenantQueueGauge(
                pair.Key,
                pair.Value.OldestPendingUtc is { } oldest
                    ? Math.Max(0d, (nowUtc - new DateTimeOffset(
                        DateTime.SpecifyKind(oldest, DateTimeKind.Utc))).TotalSeconds)
                    : 0d,
                pair.Value.Pending,
                pair.Value.PendingReady,
                pair.Value.PendingBackedOff,
                pair.Value.InFlight,
                pair.Value.ExpiredLeases,
                pair.Value.DeadLettered,
                pair.Value.InvariantBlocked,
                pair.Value.OldestInvariantBlockedUtc is { } blocked
                    ? Math.Max(0d, (nowUtc - new DateTimeOffset(
                        DateTime.SpecifyKind(blocked, DateTimeKind.Utc))).TotalSeconds)
                    : 0d,
                pair.Value.Retried,
                pair.Value.RepeatedInvariantViolations))
            .OrderByDescending(x => x.OldestPendingAgeSeconds)
            .ThenByDescending(x => x.Pending)
            .ThenBy(x => x.BusinessUnitId)
            .ToList();

        var invariantAffectedTenants = tenants.LongCount(x => x.InvariantBlocked > 0);
        var unreported = 0L;
        if (tenants.Count > maxTenants)
        {
            unreported = tenants.Count - maxTenants;
            tenants = tenants.Take(maxTenants).ToList();
        }

        return new ExtractionQueueSnapshot(nowUtc, tenants, unreported, IsFresh: true, Error: null,
            InvariantAffectedTenants: invariantAffectedTenants);
    }

    private sealed class Accumulator
    {
        public long Pending;
        public long PendingReady;
        public long PendingBackedOff;
        public long InFlight;
        public long ExpiredLeases;
        public long DeadLettered;
        public long InvariantBlocked;
        public long Retried;
        public long RepeatedInvariantViolations;
        public DateTime? OldestPendingUtc;
        public DateTime? OldestInvariantBlockedUtc;
    }
}

/// <summary>
/// Holds the latest <see cref="ExtractionQueueSnapshot"/>. Singleton; the poller writes,
/// the ObservableGauges read. The reference swap is the only synchronisation needed —
/// snapshots are immutable, so a reader never sees a half-built one.
/// </summary>
public interface IExtractionQueueSnapshotProvider
{
    ExtractionQueueSnapshot Current { get; }

    /// <summary>Number of snapshots published so far. Diagnostic; asserted by tests that
    /// prove a scrape does not trigger a query.</summary>
    long PublishCount { get; }

    void Publish(ExtractionQueueSnapshot snapshot);
}

/// <inheritdoc />
public sealed class ExtractionQueueSnapshotProvider : IExtractionQueueSnapshotProvider
{
    private ExtractionQueueSnapshot _current = ExtractionQueueSnapshot.Empty;
    private long _publishCount;

    public ExtractionQueueSnapshot Current => Volatile.Read(ref _current);

    public long PublishCount => Interlocked.Read(ref _publishCount);

    public void Publish(ExtractionQueueSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Volatile.Write(ref _current, snapshot);
        Interlocked.Increment(ref _publishCount);
    }
}
