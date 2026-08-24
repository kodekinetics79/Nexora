using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ERP_RFQ_Automation.HealthChecks;

/// <summary>
/// What the poller knows about ONE mailbox. The unit of this ledger is the mailbox, not the
/// "channel", because a deployment has more than one mailbox and they fail independently.
///
/// <para><see cref="MailboxId"/> <c>0</c> is reserved for <see cref="EmailPollerHealth.CycleScopeId"/> —
/// the poll cycle itself, which can fail without any individual mailbox having been reached.</para>
/// </summary>
/// <param name="LastSuccessUtc">When this mailbox was last polled end-to-end successfully.
/// Null means "never, in this process and in the durable record".</param>
/// <param name="LastFailureReason">Operator-readable reason for the current failure; null when healthy.</param>
public sealed record EmailMailboxChannelStatus(
    long MailboxId,
    string Mailbox,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    int ConsecutiveFailures,
    string? LastFailureReason,
    bool LastFailureIsPermanent)
{
    public bool IsFailing => ConsecutiveFailures > 0;

    /// <summary>
    /// False for a mailbox that has never once been read since it was configured. That is a
    /// DIFFERENT fault from a mailbox that worked and broke — the first is an unfinished setup,
    /// the second is an incident — and an operator needs to be told which one they have.
    /// </summary>
    public bool HasEverSucceeded => LastSuccessUtc.HasValue;
}

/// <param name="Mailboxes">Per-mailbox detail. The scalar members above it are aggregates kept
/// for the callers that only ever wanted one number; anything that has to NAME the fault reads
/// this list.</param>
public sealed record EmailPollerChannelStatus(
    DateTimeOffset? LastSeenUtc,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    int ConsecutiveFailures,
    string? LastFailureReason,
    bool LastFailureIsPermanent,
    IReadOnlyList<EmailMailboxChannelStatus>? Mailboxes = null);

/// <summary>
/// CHANNEL health for the inbound mailboxes, as opposed to LOOP liveness.
///
/// The distinction is the whole point. Until 2026-08-06 the email poller beat its liveness
/// heartbeat unconditionally at the bottom of every iteration and logged
/// "Email fetch completed successfully." 1.5 ms after
/// <c>MailKit.Security.AuthenticationException: Authentication failed</c>. The loop WAS alive —
/// it was alive and doing nothing — so every liveness surface stayed green while the door was
/// shut. Last successful mailbox contact was 2026-07-30; nothing anywhere said so.
///
/// <para><b>2026-08-24 — AND THE LEDGER IS PER MAILBOX.</b> The first fix recorded ONE verdict for
/// the whole channel: <c>RecordFailure(report.FailureSummary)</c> if ANY mailbox failed, and
/// <c>RecordSuccess()</c> only if EVERY mailbox succeeded. Production ran two mailboxes — one that
/// had never authenticated in its life and one that polled cleanly every seventy seconds — so the
/// aggregate said "Inbound mail channel is failing … Last successful poll: never" while a mailbox
/// was being read successfully a thousand times a day. One broken mailbox erased the other's
/// success from the record. Every method here now takes the mailbox it is talking about.</para>
///
/// This ledger records what actually happened to each mailbox:
/// <list type="bullet">
///   <item><description><see cref="RecordSuccess(long, string, DateTimeOffset)"/> — a mailbox was searched and drained.</description></item>
///   <item><description><see cref="RecordFailure(long, string, string, bool, DateTimeOffset)"/> — it was not, and why.</description></item>
///   <item><description><see cref="RecordCycleFailure"/> — the cycle never got as far as a mailbox
///   (the poll threw, or the leader lock could not be evaluated).</description></item>
///   <item><description><see cref="StandBy"/> — this instance did not hold the poll lock, so it
///   learned nothing about any mailbox and must not claim it did.</description></item>
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

    /// <summary>Timestamp of the MOST RECENT successful mailbox poll anywhere in the deployment.
    /// Never advanced by a failed cycle. Per-mailbox recovery windows come from
    /// <see cref="Mailboxes"/>, not from this aggregate.</summary>
    DateTimeOffset? LastSuccessUtc { get; }

    DateTimeOffset? LastFailureUtc { get; }

    /// <summary>The worst per-mailbox consecutive-failure count. Zero when nothing is failing.</summary>
    int ConsecutiveFailures { get; }

    string? LastFailureReason { get; }

    /// <summary>True when a recorded failure cannot heal on its own (bad/expired credentials,
    /// refused authorization). Retrying is still correct; pretending it might work is not.</summary>
    bool LastFailureIsPermanent { get; }

    /// <summary>Every mailbox this process has observed or been seeded with, ordered by id.</summary>
    IReadOnlyList<EmailMailboxChannelStatus> Mailboxes { get; }

    /// <summary>The loop turned but this instance did not poll (another instance holds the
    /// advisory lock). Records liveness ONLY — it must never clear a failure or fabricate a
    /// success, because a standby learns nothing about the mailbox.</summary>
    void StandBy();

    void RecordSuccess(long mailboxId, string mailbox, DateTimeOffset whenUtc);

    void RecordFailure(long mailboxId, string mailbox, string reason, bool isPermanent, DateTimeOffset whenUtc);

    /// <summary>The poll CYCLE completed without throwing. Clears a previous cycle-scope failure
    /// and asserts nothing whatsoever about any mailbox — a cycle that ran with zero configured
    /// mailboxes proved nothing about a door it never opened.</summary>
    void RecordCycleCompleted(DateTimeOffset whenUtc);

    /// <summary>The poll cycle itself failed before (or instead of) reaching the mailboxes.</summary>
    void RecordCycleFailure(string reason, bool isPermanent, DateTimeOffset whenUtc);

    /// <summary>Records a whole-channel success. Retained for callers that hold no mailbox
    /// identity; per-mailbox reporting is the preferred path.</summary>
    void RecordSuccess(DateTimeOffset whenUtc);

    /// <inheritdoc cref="RecordCycleFailure"/>
    void RecordFailure(string reason, bool isPermanent, DateTimeOffset whenUtc);

    /// <summary>Hydrates the ledger from the durable per-mailbox state at startup. Applied only
    /// while this process has observed nothing itself, so a live observation always wins.</summary>
    void Seed(EmailPollerChannelStatus status);

    /// <summary>Per-mailbox seed from <c>Email_Configurations</c>. Same precedence rule as
    /// <see cref="Seed"/>: a live observation is never overwritten.</summary>
    void SeedMailboxes(IReadOnlyList<EmailMailboxChannelStatus> mailboxes);

    /// <summary>
    /// Drops the mailbox-scoped entries the poller is no longer responsible for — deactivated,
    /// deleted, or belonging to a suspended tenant.
    ///
    /// <para>Without this, the operator's own remedy does not clear the alarm. This ledger is
    /// process-local and only ever WRITTEN by a poll; a mailbox that stops being polled keeps its
    /// last recorded failure forever, so setting <c>IsActive = false</c> on a broken mailbox left
    /// <c>/ready</c> red until the next deploy — and an alarm that stays on after the fault is
    /// fixed is how people learn to ignore it. Only the unscoped background cycle may call this:
    /// a per-tenant manual fetch knows nothing about other tenants' mailboxes.</para>
    /// </summary>
    void RetireMailboxesExcept(IReadOnlyCollection<long> mailboxIds);

    EmailPollerChannelStatus Snapshot();
}

public sealed class EmailPollerHealth : IEmailPollerHealth
{
    /// <summary>Mailbox id reserved for the poll cycle itself. No <c>Email_Configurations</c> row
    /// can collide with it: the identity column starts at 1.</summary>
    public const long CycleScopeId = 0;

    internal const string CycleScopeName = "the poll cycle";

    private readonly object _gate = new();
    private readonly Dictionary<long, Entry> _entries = new();
    private DateTimeOffset? _lastSeen;
    private bool _observed;

    public DateTimeOffset? LastSeenUtc { get { lock (_gate) return _lastSeen; } }

    public DateTimeOffset? LastSuccessUtc
    {
        get { lock (_gate) return _entries.Values.Select(e => e.LastSuccess).Max(); }
    }

    public DateTimeOffset? LastFailureUtc
    {
        get { lock (_gate) return _entries.Values.Where(e => e.Failures > 0).Select(e => e.LastFailure).Max(); }
    }

    public int ConsecutiveFailures
    {
        get { lock (_gate) return _entries.Count == 0 ? 0 : _entries.Values.Max(e => e.Failures); }
    }

    public string? LastFailureReason
    {
        get
        {
            lock (_gate)
            {
                var failing = Failing();
                return failing.Count == 0 ? null : string.Join("; ", failing.Select(Describe));
            }
        }
    }

    public bool LastFailureIsPermanent
    {
        get { lock (_gate) return _entries.Values.Any(e => e.Failures > 0 && e.Permanent); }
    }

    public IReadOnlyList<EmailMailboxChannelStatus> Mailboxes
    {
        get { lock (_gate) return SnapshotEntries(); }
    }

    public void StandBy()
    {
        lock (_gate) _lastSeen = DateTimeOffset.UtcNow;
    }

    public void RecordSuccess(long mailboxId, string mailbox, DateTimeOffset whenUtc)
    {
        lock (_gate)
        {
            _observed = true;
            _lastSeen = whenUtc;
            var entry = For(mailboxId, mailbox);
            entry.Name = Name(mailboxId, mailbox);
            entry.LastSuccess = whenUtc;
            entry.Failures = 0;
            entry.Reason = null;
            entry.Permanent = false;
        }
    }

    public void RecordFailure(long mailboxId, string mailbox, string reason, bool isPermanent, DateTimeOffset whenUtc)
    {
        lock (_gate)
        {
            _observed = true;
            _lastSeen = whenUtc;
            var entry = For(mailboxId, mailbox);
            entry.Name = Name(mailboxId, mailbox);
            entry.LastFailure = whenUtc;
            entry.Failures++;
            entry.Reason = string.IsNullOrWhiteSpace(reason) ? "Unspecified mailbox failure." : reason;
            entry.Permanent = isPermanent;
        }
    }

    public void RecordCycleCompleted(DateTimeOffset whenUtc)
    {
        lock (_gate)
        {
            _observed = true;
            _lastSeen = whenUtc;
            // Deliberately does NOT stamp LastSuccess. "The cycle ran" is not "a mailbox was
            // read", and conflating the two is the original defect wearing a different hat.
            if (!_entries.TryGetValue(CycleScopeId, out var entry)) return;
            entry.Failures = 0;
            entry.Reason = null;
            entry.Permanent = false;
        }
    }

    public void RecordCycleFailure(string reason, bool isPermanent, DateTimeOffset whenUtc)
        => RecordFailure(CycleScopeId, CycleScopeName, reason, isPermanent, whenUtc);

    public void RecordSuccess(DateTimeOffset whenUtc)
        => RecordSuccess(CycleScopeId, CycleScopeName, whenUtc);

    public void RecordFailure(string reason, bool isPermanent, DateTimeOffset whenUtc)
        => RecordCycleFailure(reason, isPermanent, whenUtc);

    public void Seed(EmailPollerChannelStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (status.Mailboxes is { Count: > 0 } mailboxes)
        {
            SeedMailboxes(mailboxes);
            return;
        }
        lock (_gate)
        {
            // A live observation is always more current than the durable snapshot.
            if (_observed) return;
            var entry = For(CycleScopeId, CycleScopeName);
            entry.LastSuccess = status.LastSuccessUtc;
            entry.LastFailure = status.LastFailureUtc;
            entry.Failures = status.ConsecutiveFailures;
            entry.Reason = status.LastFailureReason;
            entry.Permanent = status.LastFailureIsPermanent;
        }
    }

    public void SeedMailboxes(IReadOnlyList<EmailMailboxChannelStatus> mailboxes)
    {
        ArgumentNullException.ThrowIfNull(mailboxes);
        lock (_gate)
        {
            if (_observed) return;
            foreach (var mailbox in mailboxes)
            {
                var entry = For(mailbox.MailboxId, mailbox.Mailbox);
                entry.Name = Name(mailbox.MailboxId, mailbox.Mailbox);
                entry.LastSuccess = mailbox.LastSuccessUtc;
                entry.LastFailure = mailbox.LastFailureUtc;
                entry.Failures = mailbox.ConsecutiveFailures;
                entry.Reason = mailbox.LastFailureReason;
                entry.Permanent = mailbox.LastFailureIsPermanent;
            }
        }
    }

    public void RetireMailboxesExcept(IReadOnlyCollection<long> mailboxIds)
    {
        ArgumentNullException.ThrowIfNull(mailboxIds);
        lock (_gate)
        {
            var retired = _entries.Keys
                .Where(id => id != CycleScopeId && !mailboxIds.Contains(id))
                .ToList();
            foreach (var id in retired) _entries.Remove(id);
        }
    }

    public EmailPollerChannelStatus Snapshot()
    {
        lock (_gate)
        {
            var failing = Failing();
            return new EmailPollerChannelStatus(
                _lastSeen,
                _entries.Values.Select(e => e.LastSuccess).Max(),
                failing.Count == 0 ? null : failing.Select(e => e.LastFailure).Max(),
                _entries.Count == 0 ? 0 : _entries.Values.Max(e => e.Failures),
                failing.Count == 0 ? null : string.Join("; ", failing.Select(Describe)),
                failing.Any(e => e.Permanent),
                SnapshotEntries());
        }
    }

    private List<Entry> Failing()
        => _entries.Values.Where(e => e.Failures > 0).OrderBy(e => e.MailboxId).ToList();

    private List<EmailMailboxChannelStatus> SnapshotEntries()
        => _entries.Values
            .OrderBy(e => e.MailboxId)
            .Select(e => new EmailMailboxChannelStatus(
                e.MailboxId, e.Name, e.LastSuccess, e.LastFailure, e.Failures, e.Reason, e.Permanent))
            .ToList();

    private Entry For(long mailboxId, string mailbox)
    {
        if (_entries.TryGetValue(mailboxId, out var existing)) return existing;
        var entry = new Entry(mailboxId, Name(mailboxId, mailbox));
        _entries[mailboxId] = entry;
        return entry;
    }

    private static string Name(long mailboxId, string mailbox)
        => string.IsNullOrWhiteSpace(mailbox)
            ? (mailboxId == CycleScopeId ? CycleScopeName : $"mailbox {mailboxId}")
            : mailbox.Trim();

    private static string Describe(Entry entry)
        => entry.MailboxId == CycleScopeId ? entry.Reason ?? entry.Name : $"{entry.Name}: {entry.Reason}";

    private sealed class Entry
    {
        public Entry(long mailboxId, string name)
        {
            MailboxId = mailboxId;
            Name = name;
        }

        public long MailboxId { get; }
        public string Name { get; set; }
        public DateTimeOffset? LastSuccess { get; set; }
        public DateTimeOffset? LastFailure { get; set; }
        public int Failures { get; set; }
        public string? Reason { get; set; }
        public bool Permanent { get; set; }
    }
}

/// <summary>
/// Turns <c>/ready</c> red when an inbound mailbox is not working, and says WHICH mailbox, why,
/// and since when. Loop liveness stays with <see cref="BackgroundWorkerHealthCheck"/>
/// (<see cref="BackgroundWorkerNames.EmailPoller"/>); this check is about the DOOR.
///
/// Thresholds, applied PER MAILBOX:
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
///
/// <para><b>The description names the mailbox and says what it is NOT.</b> On 2026-08-24 this
/// check and <c>background-workers</c> were both red for the same single cause — one mailbox that
/// had never authenticated — and rendered as the same colour, so "the poller is dead" and "one
/// mailbox is misconfigured" were indistinguishable. They are now different sentences on
/// different checks, and this one says so out loud.</para>
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
        var mailboxes = status.Mailboxes ?? Array.Empty<EmailMailboxChannelStatus>();
        var data = new Dictionary<string, object>
        {
            ["lastSuccessfulPoll"] = status.LastSuccessUtc?.ToString("O") ?? "never",
            ["consecutiveFailures"] = status.ConsecutiveFailures
        };
        if (status.LastFailureUtc is { } failedAt) data["lastFailure"] = failedAt.ToString("O");
        if (status.LastFailureReason is { } reason) data["lastFailureReason"] = reason;
        foreach (var mailbox in mailboxes)
        {
            var key = mailbox.MailboxId == EmailPollerHealth.CycleScopeId
                ? "poll-cycle"
                : $"mailbox:{mailbox.MailboxId}:{mailbox.Mailbox}";
            data[key] = mailbox.IsFailing
                ? $"failing ({mailbox.ConsecutiveFailures} consecutive), last read "
                  + (mailbox.LastSuccessUtc?.ToString("O") ?? "never")
                : "polling, last read " + (mailbox.LastSuccessUtc?.ToString("O") ?? "never");
        }

        var failing = mailboxes.Where(m => m.IsFailing).ToList();
        var worst = failing.Count == 0
            ? HealthStatus.Healthy
            : failing.Any(m => m.LastFailureIsPermanent || m.ConsecutiveFailures >= TransientFailureThreshold)
                ? HealthStatus.Unhealthy
                : HealthStatus.Degraded;

        if (worst == HealthStatus.Healthy)
        {
            return Task.FromResult(status.LastSuccessUtc.HasValue
                ? HealthCheckResult.Healthy(
                    $"Inbound mail channel polled successfully. {Healthy(mailboxes)}", data)
                : HealthCheckResult.Healthy("Inbound mail channel has not completed a poll cycle yet.", data));
        }

        var description = Describe(mailboxes, failing, status.LastSuccessUtc);
        return Task.FromResult(worst == HealthStatus.Unhealthy
            ? HealthCheckResult.Unhealthy(description, data: data)
            : HealthCheckResult.Degraded(description, data: data));
    }

    private static string Describe(
        IReadOnlyList<EmailMailboxChannelStatus> all,
        IReadOnlyList<EmailMailboxChannelStatus> failing,
        DateTimeOffset? lastSuccessAnywhere)
    {
        // The cycle-scope entry is not a mailbox and must not be counted as one, or "1 of 2
        // mailboxes failing" starts counting the loop itself as a mailbox.
        var mailboxCount = all.Count(m => m.MailboxId != EmailPollerHealth.CycleScopeId);
        var failingMailboxes = failing.Where(m => m.MailboxId != EmailPollerHealth.CycleScopeId).ToList();
        var cycle = failing.FirstOrDefault(m => m.MailboxId == EmailPollerHealth.CycleScopeId);
        var working = all.Where(m =>
            m.MailboxId != EmailPollerHealth.CycleScopeId && !m.IsFailing && m.HasEverSucceeded).ToList();

        var lines = new List<string>();
        if (mailboxCount > 0)
            lines.Add($"Inbound mail: {failingMailboxes.Count} of {mailboxCount} mailbox(es) failing.");
        foreach (var mailbox in failingMailboxes) lines.Add("FAILING - " + Sentence(mailbox));
        if (cycle is not null)
        {
            lines.Add($"POLL CYCLE - {cycle.ConsecutiveFailures} consecutive failed cycle(s): "
                + $"{cycle.LastFailureReason} "
                + $"Last successful mailbox read: {lastSuccessAnywhere?.ToString("O") ?? "never"}.");
        }
        if (working.Count > 0)
            lines.Add("STILL POLLING - " + string.Join(", ", working.Select(Working)) + ".");
        // The sentence that stops an operator misreading this as a dead worker. Both checks were
        // red for one cause on 2026-08-24 and nothing on either of them said which was which.
        lines.Add("This is a mail-channel fault, not a stopped poller; "
            + "the 'background-workers' check is the one that reports loop liveness.");
        return string.Join(" ", lines);
    }

    private static string Healthy(IReadOnlyList<EmailMailboxChannelStatus> mailboxes)
    {
        var polling = mailboxes
            .Where(m => m.MailboxId != EmailPollerHealth.CycleScopeId && m.HasEverSucceeded)
            .ToList();
        return polling.Count == 0
            ? string.Empty
            : $"{polling.Count} mailbox(es) polling: {string.Join(", ", polling.Select(Working))}.";
    }

    private static string Working(EmailMailboxChannelStatus mailbox)
        => $"{mailbox.Mailbox} (last read {mailbox.LastSuccessUtc?.ToString("O") ?? "never"})";

    private static string Sentence(EmailMailboxChannelStatus mailbox)
    {
        // "Never once succeeded" is a setup that was never finished, not an outage. It gets its
        // own sentence because the operator action is different: finish configuring it, or turn
        // it off — see the note on retrying in EmailBackgroundService.
        var history = mailbox.HasEverSucceeded
            ? $"Last successful read {mailbox.LastSuccessUtc:O}; no mail has been ingested from it since."
            : "This mailbox has NEVER been read successfully since it was configured - "
              + "it is an unfinished setup, not an outage. Fix its credentials or deactivate it "
              + "on the Email Inboxes setup screen (/setup/mailboxes).";
        // Count BEFORE the reason: a provider's refusal text ends without punctuation as often as
        // not, and "535 5.7.8 11785 consecutive failed cycle(s)" is two numbers running together.
        return $"mailbox {mailbox.MailboxId} {mailbox.Mailbox}: "
            + $"{mailbox.ConsecutiveFailures} consecutive failed cycle(s): {mailbox.LastFailureReason} {history}";
    }
}
