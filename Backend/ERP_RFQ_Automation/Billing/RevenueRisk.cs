using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Billing;

/// <summary>
/// Machine-readable causes of free service. Deliberately short, stable, lower-kebab
/// tokens: they land in logs, in a DTO the console filters on, and in whatever query
/// an operator writes at 2am — none of which survive prose.
/// </summary>
public static class RevenueLeakReasons
{
    /// <summary>Billable tenant with no plan: nothing charges the subscription, only metered usage.</summary>
    public const string NoPlan = "no-plan";

    /// <summary>Billable tenant whose plan has no <c>MonthlyPriceUsd</c>: the base line is a real zero.</summary>
    public const string PlanNotPriced = "plan-not-priced";

    /// <summary>
    /// Billable tenant with no pinned <see cref="Tenant.RateCardId"/>. Not a bug today and a
    /// repricing tomorrow: whatever card is active when the next statement computes is the
    /// card they are charged on, whether or not it is the one they negotiated.
    /// </summary>
    public const string UnpinnedRateCard = "unpinned-rate-card";

    /// <summary>Trial with no <c>TrialEndsOn</c> — indistinguishable from permanent free service.</summary>
    public const string TrialOpenEnded = "trial-open-ended";

    /// <summary>Trial past its end date and still in Trial mode: the account needs converting.</summary>
    public const string TrialExpired = "trial-expired";

    /// <summary>Billable tenant that has never had a statement computed at all.</summary>
    public const string NeverBilled = "never-billed";

    /// <summary>Billable tenant whose most recent statement totalled zero.</summary>
    public const string LastStatementChargedNothing = "last-statement-charged-nothing";

    /// <summary>
    /// A non-Billable mode with no <c>BillingModeReason</c>. Free service that nobody wrote
    /// down is free service nobody decided; the reason field is what turns an exemption into
    /// an auditable commercial choice.
    /// </summary>
    public const string ExemptionUnexplained = "exemption-unexplained";

    /// <summary>Billable tenant whose <c>BillingStartsOn</c> is still in the future.</summary>
    public const string BillingNotStarted = "billing-not-started";
}

/// <summary>
/// The remediation state the CEO asked for by name. A tenant is in
/// <see cref="CommercialConfigurationStates.Required"/> when it is consuming the platform
/// under terms nobody set: Billable or Trial with no plan, or a non-Billable exemption
/// with nothing written down.
///
/// <para><b>Derived, not persisted, and that is the point.</b> A stored flag is a second
/// copy of a fact the tenant row already states, and second copies drift: it would need
/// clearing logic, it could be dismissed while the cause remained, and a tenant fixed by
/// a direct database edit would stay flagged forever. Deriving it means the state cannot
/// be wrong and cannot be dismissed — an operator clears it by fixing the cause (assign a
/// plan, record the exemption reason), and the moment they do it disappears on its own.
/// It also needs no migration, so it applies to every tenant that already exists the
/// first time the readout is opened, which is exactly the remediation being asked for.</para>
/// </summary>
public static class CommercialConfigurationStates
{
    /// <summary>Terms are complete: the tenant is either priced, or exempt on the record.</summary>
    public const string Complete = "complete";

    /// <summary>Billable or Trial with no plan — priced by nobody, charged for nothing.</summary>
    public const string PlanMissing = "plan-missing";

    /// <summary>A non-Billable exemption with no <c>BillingModeReason</c> — free service nobody signed for.</summary>
    public const string ExemptionUnrecorded = "exemption-unrecorded";

    /// <summary>True for every state other than <see cref="Complete"/>.</summary>
    public static bool IsRequired(string state)
        => !string.Equals(state, Complete, StringComparison.Ordinal);

    /// <summary>
    /// The state for one tenant. Plan absence is checked first: a Billable tenant with
    /// neither a plan nor a reason has both problems, and the plan is the one that decides
    /// what it should be paying.
    ///
    /// <para>This runs on the platform plane, under a role with table-level SELECT, so it can
    /// read <c>BillingModeReason</c> directly. The tenant plane expresses the SAME rule as
    /// <c>TenantAccessSnapshot.CommercialConfigurationRequired</c> from a grant-limited
    /// snapshot, where the reason is reduced to a boolean or missing entirely. Two
    /// expressions because the planes have different inputs, not different rules.</para>
    /// </summary>
    public static string For(Tenant tenant)
    {
        if (tenant.BillingMode is TenantBillingMode.Billable or TenantBillingMode.Trial)
            return tenant.PlanId is null ? PlanMissing : Complete;
        return string.IsNullOrWhiteSpace(tenant.BillingModeReason) ? ExemptionUnrecorded : Complete;
    }
}

/// <summary>
/// One tenant's revenue posture: how it is charged, against what, and whether the last
/// statement actually moved money. Ids travel as numbers; the console normalizes them.
/// </summary>
public sealed record TenantRevenueRisk(
    long TenantId,
    string TenantName,
    string TenantSlug,
    string TenantStatus,
    string BillingMode,
    string? BillingModeReason,
    long? PlanId,
    string? PlanCode,
    decimal? PlanMonthlyPriceUsd,
    long? PinnedRateCardId,
    string? PinnedRateCardCode,
    DateTime? BillingStartsOn,
    DateTime? TrialEndsOn,
    bool TrialExpired,
    int? TrialDaysRemaining,
    string? LastStatementPeriod,
    string? LastStatementStatus,
    decimal? LastStatementTotal,
    DateTime? LastStatementComputedAtUtc,
    long? LastStatementRateCardId,
    bool LastStatementCharged,
    bool AtRisk,
    IReadOnlyList<string> LeakReasons)
{
    /// <summary>One of <see cref="CommercialConfigurationStates"/>.</summary>
    public string CommercialConfigurationState { get; init; } = CommercialConfigurationStates.Complete;

    /// <summary>
    /// The badge the console renders. Separate from <see cref="AtRisk"/> because the two
    /// mean different things to an operator: AtRisk includes conditions that are somebody
    /// else's problem to time (a trial that has not expired yet, a period before billing
    /// starts), while this one is always an unfinished piece of setup with a named owner
    /// and a button that fixes it.
    /// </summary>
    public bool CommercialConfigurationRequired
        => CommercialConfigurationStates.IsRequired(CommercialConfigurationState);
}

/// <summary>
/// The single place that decides whether a tenant is leaking revenue.
///
/// <para>Pure and static on purpose: the billing run evaluates it per tenant as it
/// sweeps (so the leak shows up in the logs at the moment the statement is computed)
/// and the console readout evaluates it over the whole fleet. Two implementations of
/// "is this account free?" would drift, and the one that drifts is always the one
/// nobody is watching.</para>
/// </summary>
public static class RevenueLeakEvaluator
{
    /// <summary>
    /// Every reason this tenant may be consuming the platform below its worth.
    /// <paramref name="lastStatement"/> is the most recently computed statement, or null
    /// when none exists.
    /// </summary>
    public static IReadOnlyList<string> Evaluate(
        Tenant tenant, Plan? plan, BillingStatement? lastStatement, DateTime nowUtc)
    {
        var reasons = new List<string>();

        if (tenant.BillingMode == TenantBillingMode.Billable)
        {
            if (tenant.PlanId is null)
                reasons.Add(RevenueLeakReasons.NoPlan);
            else if (plan?.MonthlyPriceUsd is null)
                reasons.Add(RevenueLeakReasons.PlanNotPriced);

            if (tenant.RateCardId is null)
                reasons.Add(RevenueLeakReasons.UnpinnedRateCard);

            if (tenant.BillingStartsOn is DateTime startsOn && startsOn > nowUtc)
                reasons.Add(RevenueLeakReasons.BillingNotStarted);

            // A billable tenant with no statement at all has been served for free for
            // however long it has existed — the leak the scheduled run exists to close.
            if (lastStatement is null)
                reasons.Add(RevenueLeakReasons.NeverBilled);
            else if (lastStatement.TotalAmount <= 0m)
                reasons.Add(RevenueLeakReasons.LastStatementChargedNothing);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(tenant.BillingModeReason))
                reasons.Add(RevenueLeakReasons.ExemptionUnexplained);

            if (tenant.BillingMode == TenantBillingMode.Trial)
            {
                if (tenant.TrialEndsOn is null)
                    reasons.Add(RevenueLeakReasons.TrialOpenEnded);
                else if (tenant.TrialEndsOn.Value <= nowUtc)
                    reasons.Add(RevenueLeakReasons.TrialExpired);
            }
        }

        return reasons;
    }

    /// <summary>True when a Trial tenant is past its own end date and still uncharged.</summary>
    public static bool IsTrialExpired(Tenant tenant, DateTime nowUtc)
        => tenant.BillingMode == TenantBillingMode.Trial
           && tenant.TrialEndsOn is DateTime endsOn
           && endsOn <= nowUtc;

    /// <summary>Assembles the wire readout for one tenant from rows the caller already loaded.</summary>
    public static TenantRevenueRisk Describe(
        Tenant tenant, Plan? plan, RateCard? pinnedCard, BillingStatement? lastStatement, DateTime nowUtc)
    {
        var reasons = Evaluate(tenant, plan, lastStatement, nowUtc);
        var trialExpired = IsTrialExpired(tenant, nowUtc);
        int? trialDaysRemaining = tenant.BillingMode == TenantBillingMode.Trial && tenant.TrialEndsOn is DateTime ends
            ? (int)Math.Ceiling((ends - nowUtc).TotalDays)
            : null;

        return new TenantRevenueRisk(
            tenant.Id,
            tenant.Name,
            tenant.Slug,
            tenant.Status.ToString(),
            tenant.BillingMode.ToString(),
            tenant.BillingModeReason,
            tenant.PlanId,
            plan?.Code,
            plan?.MonthlyPriceUsd,
            tenant.RateCardId,
            pinnedCard?.Code,
            tenant.BillingStartsOn,
            tenant.TrialEndsOn,
            trialExpired,
            trialDaysRemaining,
            lastStatement?.PeriodStartUtc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture),
            lastStatement?.Status.ToString(),
            lastStatement?.TotalAmount,
            lastStatement?.ComputedAtUtc,
            lastStatement?.RateCardId,
            lastStatement is not null && lastStatement.TotalAmount > 0m,
            reasons.Count > 0,
            reasons)
        {
            CommercialConfigurationState = CommercialConfigurationStates.For(tenant)
        };
    }
}
