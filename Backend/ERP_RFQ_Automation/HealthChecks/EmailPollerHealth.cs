using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.HealthChecks;

/// <param name="LastSuccessUtc">When a mailbox was last polled end-to-end successfully.
/// Null means "never, in this process and in the durable record".</param>
/// <param name="LastFailureReason">Operator-readable reason for the current failure; null when healthy.</param>
public sealed record EmailPollerChannelStatus(
    DateTimeOffset? LastSeenUtc,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    int ConsecutiveFailures,
    string? LastFailureReason,
    bool LastFailureIsPermanent);

/// <summary>
/// CHANNEL health for the inbound mailbox, as opposed to LOOP liveness.
///
/// The distinction is the whole point. Until 2026-08-06 the email poller beat its liveness
/// heartbeat unconditionally at the bottom of every iteration and logged
/// "Email fetch completed successfully." 1.5 ms after
/// <c>MailKit.Security.AuthenticationException: Authentication failed</c>. The loop WAS alive —
/// it was alive and doing nothing — so every liveness surface stayed green while the door was
/// shut. Last successful mailbox contact was 2026-07-30; nothing anywhere said so.
///
/// This ledger records what actually happened to the mailbox:
/// <list type="bullet">
///   <item><description><see cref="RecordSuccess"/> — a mailbox was searched and drained.</description></item>
///   <item><description><see cref="RecordFailure"/> — it was not, and why.</description></item>
///   <item><description><see cref="StandBy"/> — this instance did not hold the poll lock, so it
///   learned nothing about the mailbox either way and must not claim it did.</description></item>
/// </list>
///
/// It is process-local (the house heartbeat idiom — see <see cref="IExtractionWorkerHeartbeat"/>,
/// <see cref="IQuoteDeliveryWorkerHeartbeat"/>), so it is SEEDED at startup from the durable
/// per-mailbox columns on <c>Email_Configurations</c>. A restart therefore cannot launder a
/// broken mailbox back to green, and a standby instance that never polls still reports the truth.
/// </summary>
public interface IEmailPollerHealth
{
    /// <summary>Last time the poll loop turned, whatever the outcome.</summary>
    DateTimeOffset? LastSeenUtc { get; }

    /// <summary>Timestamp of the last SUCCESSFUL mailbox poll. This is the value the lookback
    /// window is derived from, so it is never advanced by a failed cycle.</summary>
    DateTimeOffset? LastSuccessUtc { get; }

    DateTimeOffset? LastFailureUtc { get; }
    int ConsecutiveFailures { get; }
    string? LastFailureReason { get; }

    /// <summary>True when the recorded failure cannot heal on its own (bad/expired credentials,
    /// refused authorization). Retrying is still correct; pretending it might work is not.</summary>
    bool LastFailureIsPermanent { get; }

    /// <summary>The loop turned but this instance did not poll (another instance holds the
    /// advisory lock). Records liveness ONLY — it must never clear a failure or fabricate a
    /// success, because a standby learns nothing about the mailbox.</summary>
    void StandBy();

    void RecordSuccess(DateTimeOffset whenUtc);

    void RecordFailure(string reason, bool isPermanent, DateTimeOffset whenUtc);

    /// <summary>Hydrates the ledger from the durable per-mailbox state at startup. Applied only
    /// while this process has observed nothing itself, so a live observation always wins.</summary>
    void Seed(EmailPollerChannelStatus status);

    EmailPollerChannelStatus Snapshot();
}

public sealed class EmailPollerHealth : IEmailPollerHealth
{
    private readonly object _gate = new();
    private DateTimeOffset? _lastSeen;
    private DateTimeOffset? _lastSuccess;
    private DateTimeOffset? _lastFailure;
    private int _consecutiveFailures;
    private string? _lastFailureReason;
    private bool _lastFailureIsPermanent;
    private bool _observed;

    public DateTimeOffset? LastSeenUtc { get { lock (_gate) return _lastSeen; } }
    public DateTimeOffset? LastSuccessUtc { get { lock (_gate) return _lastSuccess; } }
    public DateTimeOffset? LastFailureUtc { get { lock (_gate) return _lastFailure; } }
    public int ConsecutiveFailures { get { lock (_gate) return _consecutiveFailures; } }
    public string? LastFailureReason { get { lock (_gate) return _lastFailureReason; } }
    public bool LastFailureIsPermanent { get { lock (_gate) return _lastFailureIsPermanent; } }

    public void StandBy()
    {
        lock (_gate) _lastSeen = DateTimeOffset.UtcNow;
    }

    public void RecordSuccess(DateTimeOffset whenUtc)
    {
        lock (_gate)
        {
            _observed = true;
            _lastSeen = whenUtc;
            _lastSuccess = whenUtc;
            _consecutiveFailures = 0;
            _lastFailureReason = null;
            _lastFailureIsPermanent = false;
        }
    }

    public void RecordFailure(string reason, bool isPermanent, DateTimeOffset whenUtc)
    {
        lock (_gate)
        {
            _observed = true;
            _lastSeen = whenUtc;
            _lastFailure = whenUtc;
            _consecutiveFailures++;
            _lastFailureReason = string.IsNullOrWhiteSpace(reason) ? "Unspecified mailbox failure." : reason;
            _lastFailureIsPermanent = isPermanent;
        }
    }

    public void Seed(EmailPollerChannelStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        lock (_gate)
        {
            // A live observation is always more current than the durable snapshot.
            if (_observed) return;
            _lastSuccess = status.LastSuccessUtc;
            _lastFailure = status.LastFailureUtc;
            _consecutiveFailures = status.ConsecutiveFailures;
            _lastFailureReason = status.LastFailureReason;
            _lastFailureIsPermanent = status.LastFailureIsPermanent;
        }
    }

    public EmailPollerChannelStatus Snapshot()
    {
        lock (_gate)
        {
            return new EmailPollerChannelStatus(
                _lastSeen, _lastSuccess, _lastFailure,
                _consecutiveFailures, _lastFailureReason, _lastFailureIsPermanent);
        }
    }
}

/// <summary>
/// Turns <c>/ready</c> red when the inbound mail channel is not working, and says why and since
/// when. Loop liveness stays with <see cref="BackgroundWorkerHealthCheck"/>
/// (<see cref="BackgroundWorkerNames.EmailPoller"/>); this check is about the DOOR.
///
/// Thresholds:
/// <list type="bullet">
///   <item><description>a PERMANENT failure (authentication/authorization refused) is red on the
///   first occurrence — no number of retries fixes an expired credential, so waiting three
///   cycles only delays the truth;</description></item>
///   <item><description>a transient failure (network, timeout, server busy) is red after three
///   consecutive cycles, matching <see cref="QuoteDeliveryWorkerHealthCheck"/> and
///   <c>ProcurementDispatchHealthCheck</c>, so a single blip does not flap the surface.</description></item>
/// </list>
/// A process that has neither succeeded nor failed reports Healthy: "has not polled yet" is not
/// evidence of a broken mailbox, and a poller that never starts is already covered by the
/// background-worker registry's startup grace.
/// </summary>
public sealed class EmailPollerHealthCheck : IHealthCheck
{
    internal const int TransientFailureThreshold = 3;

    private readonly IEmailPollerHealth _health;

    public EmailPollerHealthCheck(IEmailPollerHealth health) => _health = health;

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = _health.Snapshot();
        var data = new Dictionary<string, object>
        {
            ["lastSuccessfulPoll"] = status.LastSuccessUtc?.ToString("O") ?? "never",
            ["consecutiveFailures"] = status.ConsecutiveFailures
        };
        if (status.LastFailureUtc is { } failedAt) data["lastFailure"] = failedAt.ToString("O");
        if (status.LastFailureReason is { } reason) data["lastFailureReason"] = reason;

        if (status.ConsecutiveFailures > 0 &&
            (status.LastFailureIsPermanent || status.ConsecutiveFailures >= TransientFailureThreshold))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Inbound mail channel is failing: {status.LastFailureReason} "
                + $"Last successful poll: {status.LastSuccessUtc?.ToString("O") ?? "never"}. "
                + $"Consecutive failed cycles: {status.ConsecutiveFailures}.",
                data: data));
        }

        if (status.ConsecutiveFailures > 0)
        {
            return Task.FromResult(HealthCheckResult.Degraded(
                $"Inbound mail poll failed {status.ConsecutiveFailures} time(s): {status.LastFailureReason} "
                + $"Last successful poll: {status.LastSuccessUtc?.ToString("O") ?? "never"}.",
                data: data));
        }

        return Task.FromResult(status.LastSuccessUtc.HasValue
            ? HealthCheckResult.Healthy("Inbound mail channel polled successfully.", data)
            : HealthCheckResult.Healthy("Inbound mail channel has not completed a poll cycle yet.", data));
    }
}
