using System.Linq;
using ERP_RFQ_Automation.Extraction;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Platform.Hardening;

/// <summary>Tuning for <see cref="ExtractionQueueMetricsPoller"/>. Bound from
/// <c>Observability:QueueMetrics</c>.</summary>
public sealed class ExtractionQueueMetricsOptions
{
    public const string SectionName = "Observability:QueueMetrics";

    /// <summary>Set false to stop the poller entirely (the queue gauges then report nothing).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the single grouped queue query runs. This is the ONLY place the queue
    /// gauges cost a database round-trip — collection/scrape reads the cached snapshot.
    /// Cheap enough to run often; 15s resolves a stuck tenant well inside any sane alert
    /// window while costing one aggregate query per interval, not per request.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>Deadline for one poll. A poll that overruns is abandoned, not queued behind.</summary>
    public TimeSpan QueryTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Cardinality cap on published per-tenant series. Tenants are ranked by oldest pending
    /// age, so the cap always keeps the tenants an operator would page on; the remainder is
    /// reported as a single overflow series rather than silently dropped.
    /// </summary>
    public int MaxTenantSeries { get; set; } = 200;

    internal TimeSpan EffectiveInterval =>
        PollInterval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : PollInterval;
}

/// <summary>
/// Bounded background poller that keeps <see cref="IExtractionQueueSnapshotProvider"/>
/// warm for the queue ObservableGauges in <see cref="NexoraMetrics"/>.
///
/// <para><b>Why a poller and not a gauge callback that queries.</b> An ObservableGauge
/// callback fires on every collection cycle AND on every Prometheus scrape, from however
/// many scrapers are configured. Querying there turns "add a dashboard" into "add load
/// proportional to the number of people looking at the dashboard". One query per interval,
/// shared by every reader, is the only shape that stays honest under a real scrape fan-out.</para>
///
/// <para><b>One query.</b> A single GROUP BY over ExtractionJobs keyed on
/// (BusinessUnitId, Status, ready?, lease-lapsed?) returns at most
/// tenants × statuses × 4 rows and carries MIN(CreatedOn) per group — everything the
/// per-tenant gauges need, including the oldest-pending age, in one round trip.</para>
///
/// <para><b>Never fatal.</b> Metrics collection must not be able to take the host down or
/// hold a connection hostage: a failed poll logs once, marks the snapshot stale (the
/// snapshot_age gauge exposes that) and retries on the next tick.</para>
/// </summary>
public sealed class ExtractionQueueMetricsPoller : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IExtractionQueueSnapshotProvider _provider;
    private readonly ExtractionQueueMetricsOptions _options;
    private readonly ILogger<ExtractionQueueMetricsPoller> _log;
    private bool _loggedFailure;

    public ExtractionQueueMetricsPoller(
        IServiceScopeFactory scopeFactory,
        IExtractionQueueSnapshotProvider provider,
        ExtractionQueueMetricsOptions options,
        ILogger<ExtractionQueueMetricsPoller> log)
    {
        _scopeFactory = scopeFactory;
        _provider = provider;
        _options = options;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            _log.LogWarning(
                "Extraction queue metrics poller is DISABLED ({Key}:Enabled=false). The per-tenant "
                + "queue gauges — including oldest-pending-job age, the only signal that reveals a "
                + "starving tenant — will report nothing.",
                ExtractionQueueMetricsOptions.SectionName);
            return;
        }

        _log.LogInformation(
            "Extraction queue metrics poller started: interval {Interval}, query timeout {Timeout}, "
            + "max {MaxSeries} tenant series.",
            _options.EffectiveInterval, _options.QueryTimeout, _options.MaxTenantSeries);

        while (!stoppingToken.IsCancellationRequested)
        {
            await PollOnceAsync(stoppingToken);
            try { await Task.Delay(_options.EffectiveInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// One poll: query, fold, publish. Internal so a test can drive exactly N polls and
    /// prove that observing the gauges any number of times adds no further queries.
    /// </summary>
    internal async Task PollOnceAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.QueryTimeout);

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ErpRfqAutomationContext>();
            var groups = await QueryAsync(db, DateTime.UtcNow, timeout.Token);
            _provider.Publish(ExtractionQueueSnapshot.From(
                groups, DateTimeOffset.UtcNow, _options.MaxTenantSeries));
            _loggedFailure = false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown. Nothing to say.
        }
        catch (Exception ex)
        {
            // Log the FIRST failure of a run at Error and then stay quiet until it
            // recovers: a broken poll on a 15s loop would otherwise bury the log.
            if (!_loggedFailure)
            {
                _loggedFailure = true;
                _log.LogError(ex,
                    "Extraction queue metrics poll failed; the queue gauges are now STALE. "
                    + "nexora.extraction.queue.snapshot_age will keep climbing until this recovers.");
            }
            var stale = _provider.Current with { IsFresh = false, Error = ex.GetType().Name };
            _provider.Publish(stale);
        }
    }

    /// <summary>
    /// The single grouped query. Runs with no tenant scope pushed, so the global query
    /// filter is a no-op and (on PostgreSQL) the command interceptor resolves the
    /// BYPASSRLS pipeline role — this is a cross-tenant operations read by design.
    /// </summary>
    internal static async Task<IReadOnlyList<ExtractionQueueGroup>> QueryAsync(
        ErpRfqAutomationContext db, DateTime nowUtc, CancellationToken ct)
    {
        // Reduced test models (and any host without the extraction module mapped) simply
        // have no queue to report on.
        if (db.Model.FindEntityType(typeof(ExtractionJob)) is null)
            return Array.Empty<ExtractionQueueGroup>();

        var rows = await db.Set<ExtractionJob>().AsNoTracking()
            .GroupBy(j => new
            {
                j.BusinessUnitId,
                j.Status,
                Ready = j.NextAttemptAt <= nowUtc,
                LeaseLapsed = j.LeaseExpiresAt == null || j.LeaseExpiresAt <= nowUtc,
                InvariantBlocked = j.Status == ExtractionStatus.DeadLetter
                    && j.LastError != null && j.LastError.StartsWith("[EXTRACTION_INTAKE_"),
                Retry = j.Attempts > 1,
                RepeatedInvariantViolation = j.Status == ExtractionStatus.DeadLetter
                    && j.LastError != null && j.LastError.StartsWith("[EXTRACTION_INTAKE_")
                    && j.Attempts > 0
            })
            .Select(g => new
            {
                g.Key.BusinessUnitId,
                g.Key.Status,
                g.Key.Ready,
                g.Key.LeaseLapsed,
                g.Key.InvariantBlocked,
                g.Key.Retry,
                g.Key.RepeatedInvariantViolation,
                Count = g.LongCount(),
                OldestCreatedOn = g.Min(x => x.CreatedOn)
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new ExtractionQueueGroup(
                r.BusinessUnitId, r.Status.ToString(), r.Ready, r.LeaseLapsed, r.Count,
                r.OldestCreatedOn, r.InvariantBlocked, r.Retry, r.RepeatedInvariantViolation))
            .ToList();
    }
}
