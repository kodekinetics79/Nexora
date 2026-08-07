using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Platform.Provisioning;

/// <summary>
/// Cadence and bounds of the durable provisioning engine (<c>"Platform:Provisioning"</c>).
///
/// <para><see cref="Enabled"/> defaults to TRUE, following <c>BillingRunOptions</c> rather than
/// the outbox dispatcher's opt-in switch. The dispatcher talks to an external endpoint that may
/// not exist yet, so staying dark is safe for it; a deployment where nothing advances accepted
/// provisioning work has operators watching a wizard that will never finish, which is worse than
/// the synchronous behaviour it replaced.</para>
/// </summary>
public sealed class ProvisioningOptions
{
    public const string SectionName = "Platform:Provisioning";

    /// <summary>Stop the runner. An explicit, configured decision — never the default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Idle poll interval. Short, because this is interactive work: an operator is watching a
    /// progress list while it runs, and the whole point of the durable path is that the list
    /// moves. The runner is also woken directly on submit
    /// (<see cref="ProvisioningRunSignal"/>), so this interval only bounds the recovery of work
    /// left behind by a process that died.
    /// </summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Delay before the first sweep, so process start is not competing with migrations, health
    /// probes and the other workers all waking at once.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// How long a claimed execution stays claimed. Must comfortably exceed the slowest step —
    /// the baseline seed writes several hundred rows — because a lease that expires under a
    /// runner that is still working invites a second runner onto the same tenant.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How many executions one sweep picks up. Provisioning is a rare, operator-driven act;
    /// a small batch keeps a burst of them from monopolising the connection pool.
    /// </summary>
    public int BatchSize { get; set; } = 4;

    /// <summary>
    /// How many times the runner itself re-attempts a failing execution before leaving it for a
    /// human.
    ///
    /// <para>Deliberately low. Every failure mode this engine actually sees — a taken email
    /// address, a reserved slug, a deactivated plan, a missing grant — is one a retry cannot fix,
    /// and hammering it a hundred times only buries the real message under a hundred identical
    /// log lines. The operator's explicit retry is unaffected by this ceiling: it is a human
    /// deciding the underlying cause is fixed.</para>
    /// </summary>
    public int MaxAutomaticAttempts { get; set; } = 3;

    /// <summary>
    /// Base of the exponential backoff between automatic attempts. An execution that failed on
    /// a transient database error is worth trying again shortly; one that failed on a permanent
    /// condition is not, and is marked terminal instead of waiting this out.
    /// </summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How long the synchronous compatibility path waits for a submitted execution to reach a
    /// terminal state before giving up and returning the execution id.
    ///
    /// <para>This is the bound that lets <c>POST /api/platform/tenants</c> keep its current
    /// response shape while running on the durable engine: it submits, waits, and projects the
    /// finished execution into the existing DTO. Chosen to sit inside a typical 100-second proxy
    /// timeout with room to spare.</para>
    /// </summary>
    public TimeSpan SynchronousWaitTimeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How long a draft survives without being touched. Drafts hold a customer's commercial
    /// terms, and one abandoned eighteen months ago is stale data with a name attached rather
    /// than work in progress.
    /// </summary>
    public TimeSpan DraftRetention { get; set; } = TimeSpan.FromDays(180);
}

internal sealed class ProvisioningOptionsValidator : IValidateOptions<ProvisioningOptions>
{
    public ValidateOptionsResult Validate(string? name, ProvisioningOptions options)
    {
        var failures = new List<string>();

        if (options.PollInterval < TimeSpan.FromSeconds(1) || options.PollInterval > TimeSpan.FromMinutes(5))
            failures.Add("PollInterval must be between one second and five minutes.");
        if (options.InitialDelay < TimeSpan.Zero || options.InitialDelay > TimeSpan.FromMinutes(10))
            failures.Add("InitialDelay must be between zero and ten minutes.");

        // The floor is not cosmetic: a lease shorter than the baseline seed takes means a second
        // runner joins a tenant that is still being built, and both write the same starter rows.
        if (options.LeaseDuration < TimeSpan.FromMinutes(1) || options.LeaseDuration > TimeSpan.FromHours(1))
            failures.Add("LeaseDuration must be between one minute and one hour.");
        if (options.LeaseDuration <= options.PollInterval)
            failures.Add("LeaseDuration must be longer than PollInterval, or a claim expires before it is renewed.");

        if (options.BatchSize is < 1 or > 50)
            failures.Add("BatchSize must be between 1 and 50.");
        if (options.MaxAutomaticAttempts is < 1 or > 20)
            failures.Add("MaxAutomaticAttempts must be between 1 and 20.");
        if (options.RetryBackoff < TimeSpan.Zero || options.RetryBackoff > TimeSpan.FromMinutes(10))
            failures.Add("RetryBackoff must be between zero and ten minutes.");
        if (options.SynchronousWaitTimeout < TimeSpan.FromSeconds(5)
            || options.SynchronousWaitTimeout > TimeSpan.FromMinutes(5))
            failures.Add("SynchronousWaitTimeout must be between five seconds and five minutes.");
        if (options.DraftRetention < TimeSpan.FromDays(1))
            failures.Add("DraftRetention must be at least one day.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

/// <summary>
/// Wakes the runner the instant work is accepted, so the console's first poll already shows a
/// step in flight instead of a queued execution sitting still for a poll interval.
///
/// <para>Deliberately a signal and not a queue. The queue is the
/// <c>platform.ProvisioningExecutions</c> table, which survives a process restart; this only
/// removes latency. A missed signal costs one poll interval and nothing else, which is why it is
/// safe for it to live in memory and be lost on restart.</para>
/// </summary>
public sealed class ProvisioningRunSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Nudge the runner. Never blocks, and never throws if a nudge is already pending.</summary>
    public void Poke()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A nudge is already outstanding, which does exactly the same job as this one.
        }
    }

    /// <summary>Waits for a nudge or for <paramref name="timeout"/>, whichever comes first.</summary>
    public async Task WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        try
        {
            await _signal.WaitAsync(timeout, ct);
        }
        catch (OperationCanceledException)
        {
            // Shutdown. The caller's loop reads the token itself.
        }
    }
}
