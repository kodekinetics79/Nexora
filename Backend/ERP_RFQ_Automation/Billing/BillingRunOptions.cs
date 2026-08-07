using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Billing;

/// <summary>
/// Cadence and scope of the scheduled billing run (<c>"Billing:Run"</c>).
///
/// <para><see cref="Enabled"/> defaults to TRUE, unlike the outbox dispatcher's opt-in
/// switch. The dispatcher talks to an external endpoint that may not exist yet, so
/// staying dark by default is safe; billing staying dark by default is exactly the
/// defect this worker closes — before it existed, statements were produced only when a
/// human clicked Compute in the console, so an environment where nobody clicked billed
/// nothing at all and looked perfectly healthy while doing it.</para>
/// </summary>
public sealed class BillingRunOptions
{
    public const string SectionName = "Billing:Run";

    /// <summary>Turn the sweep off. An explicit, configured decision — never the default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often the fleet is swept. Six hours because the output is a Draft preview of an
    /// open month: recomputing it more often costs a full meter aggregation per tenant and
    /// changes nothing anyone acts on, and less often means the console's month-to-date
    /// figure is stale enough for an operator to stop trusting it.
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    /// Delay before the first sweep, so process start is not competing with migrations,
    /// health probes and the other workers all waking at once.
    /// </summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Also recompute the PRIOR period's Draft each sweep, until it is finalized.
    ///
    /// <para>The prior month keeps changing after it closes: external AI requests reconcile
    /// asynchronously and dead-letter re-drives replay failed documents, both of which move
    /// meters (see <c>BillingStatementService.FinalizeSettleLag</c>). Without the catch-up,
    /// the last Draft computed before midnight on the last day of the month would be the
    /// one an operator finalizes, permanently under-billing everything that landed after
    /// it. Bounded to ONE extra period so the sweep cost stays linear in tenant count.</para>
    /// </summary>
    public bool CatchUpPriorPeriod { get; set; } = true;

    /// <summary>
    /// Upper bound on the random delay added before each sweep. Two instances that start
    /// together would otherwise sweep the same tenant at the same instant forever; the
    /// unique index makes that correct but not free, and the loser burns a full meter
    /// aggregation to discover it lost.
    /// </summary>
    public TimeSpan MaximumJitter { get; set; } = TimeSpan.FromSeconds(60);
}

internal sealed class BillingRunOptionsValidator : IValidateOptions<BillingRunOptions>
{
    public ValidateOptionsResult Validate(string? name, BillingRunOptions options)
    {
        var failures = new List<string>();

        // The floor is not cosmetic: each sweep re-aggregates every source ledger for
        // every tenant, so a misconfigured one-second interval is a self-inflicted outage.
        if (options.Interval < TimeSpan.FromMinutes(1) || options.Interval > TimeSpan.FromDays(1))
            failures.Add("Interval must be between one minute and one day.");
        if (options.InitialDelay < TimeSpan.Zero || options.InitialDelay > TimeSpan.FromHours(1))
            failures.Add("InitialDelay must be between zero and one hour.");
        if (options.MaximumJitter < TimeSpan.Zero || options.MaximumJitter > TimeSpan.FromMinutes(30))
            failures.Add("MaximumJitter must be between zero and thirty minutes.");
        if (options.MaximumJitter >= options.Interval)
            failures.Add("MaximumJitter must be shorter than Interval.");

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
