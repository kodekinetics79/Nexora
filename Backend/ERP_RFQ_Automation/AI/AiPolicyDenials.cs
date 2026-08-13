using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.AI;

/// <summary>
/// The policy-row denials the token ledger applies to every reservation, and the
/// external-dependency ratio it applies to unauthorized external ones.
///
/// <para><b>Why they moved out of <see cref="AiGovernanceService"/>.</b> They were a private
/// method, which was correct while exactly one caller enforced them and wrong the moment a
/// second surface had to REPORT them. The 2026-08 pilot dead-lettered every document under
/// <c>model_denied</c> — a refusal issued here, three locks after the extraction gate had
/// already logged that the call was ALLOWED, and classified downstream as a generic
/// extraction failure rather than an authorization one. The readiness pre-flight exists to
/// name that lock before a document is submitted, and it can only be trusted if it asks the
/// SAME code the reservation asks. A second copy of these tests would drift, and a
/// pre-flight that disagrees with enforcement is worse than none at all.</para>
///
/// <para>Nothing here is widened, reordered or softened by the move: the body of
/// <see cref="Evaluate"/> is the reservation's original sequence, statement for statement.</para>
/// </summary>
internal static class AiPolicyDenials
{
    // The exact strings the ledger has always written to AiRequests.ErrorCode and
    // ExtractionJobs.LastError. Three of them are the SAME refusal the allow-list gate
    // names, so they alias its constants rather than respelling them: two constants for one
    // reason code is precisely how two layers drift apart.
    internal const string PolicyMissing = AiExternalProviderTrustReasons.PolicyMissing;
    internal const string PolicyDisabled = AiExternalProviderTrustReasons.PolicyDisabled;
    internal const string ExternalProcessingDenied = AiExternalProviderTrustReasons.PolicyExternalProcessingDenied;
    internal const string PurposeDenied = "purpose_denied";
    internal const string ProviderDenied = "provider_denied";
    internal const string ModelDenied = "model_denied";

    /// <summary>The ratio refusal. Governs UNAUTHORIZED external usage only.</summary>
    internal const string ExternalDependencyCap = "external_dependency_cap";

    /// <summary>
    /// The monthly ceiling refusal. It is the LAST thing the reservation tests and the one
    /// control no allow-list grant exempts, so it refuses documents that every lock above it
    /// has already cleared.
    /// </summary>
    internal const string HardBudgetExceeded = "hard_budget_exceeded";

    /// <summary>How many recent governed calls the dependency ratio is measured over.</summary>
    internal const int DependencyWindow = 100;

    /// <summary>
    /// The calendar month a reservation is budgeted against, from a UTC instant. One
    /// expression, because <c>AiGovernanceService</c> reserves against it, settles against it
    /// and the readiness pre-flight reports the headroom left in it — three sites keying the
    /// same row.
    /// </summary>
    internal static DateTime BudgetPeriodStart(DateTime instantUtc) =>
        new(instantUtc.Year, instantUtc.Month, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// The smallest reservation <c>AiGovernanceService.ReserveAsync</c> can possibly compute:
    /// it refuses <c>maximumInputBytes</c> at or below zero, and floors both the output
    /// allowance and the attempt count at one, so every reserve is at least two tokens.
    ///
    /// <para>It exists so the pre-flight can answer "nothing fits in what is left this month"
    /// for a document it has not been given, without guessing at a size.</para>
    /// </summary>
    internal const long SmallestPossibleReservation = 2;

    /// <summary>
    /// Whether one more reservation of <paramref name="reserve"/> tokens would break the
    /// tenant's monthly hard ceiling. A null limit is no ceiling; ZERO is a legal limit that
    /// refuses everything, which is precisely the state that reads as "configured" on a
    /// policy page and dead-letters every document.
    ///
    /// <para>The reservation copies <c>AiProcessingPolicy.MonthlyHardTokenLimit</c> onto the
    /// period row immediately before applying this, so the policy's value is what bites and
    /// an older row's stored limit never does. The pre-flight must pass the policy's value
    /// for the same reason.</para>
    /// </summary>
    internal static bool ExceedsHardTokenBudget(
        long? hardTokenLimit, long reservedTokens, long settledTokens, long reserve) =>
        hardTokenLimit is { } hard && checked(reservedTokens + settledTokens + reserve) > hard;

    internal static bool PurposeAllowed(AiProcessingPolicy policy, string purpose) =>
        policy.AllowedPurposes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(purpose, StringComparer.OrdinalIgnoreCase);

    /// <summary>Case-insensitive: a provider name is a label, not an identifier.</summary>
    internal static bool ProviderAllowed(AiProcessingPolicy policy, string provider) =>
        string.IsNullOrWhiteSpace(policy.AllowedProvider)
        || string.Equals(policy.AllowedProvider, provider, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Ordinal, unlike <see cref="ProviderAllowed"/>. Model ids are case-sensitive at every
    /// provider we support — <see cref="AiProviderEndpoint.ModelMatches"/> states the same
    /// rule for the allow-list — so one capital letter in <c>AllowedModel</c> refuses every
    /// document the tenant submits. The asymmetry is real, it is deliberate, and it is why
    /// the readiness report prints the exact required string for an operator to copy instead
    /// of describing it in prose.
    /// </summary>
    internal static bool ModelAllowed(AiProcessingPolicy policy, string model) =>
        string.IsNullOrWhiteSpace(policy.AllowedModel)
        || string.Equals(policy.AllowedModel, model, StringComparison.Ordinal);

    /// <summary>
    /// The reservation's policy verdict, or null when the policy row permits this call.
    ///
    /// <para>Only <see cref="ExternalProcessingDenied"/> is conditional on the provider
    /// class. Purpose, provider and model are tested for LOCAL calls too — a fact worth
    /// stating here because it is the one most readers get wrong about this method.</para>
    /// </summary>
    internal static string? Evaluate(
        AiProcessingPolicy? policy,
        string purpose,
        string provider,
        string model,
        AiProviderClass providerClass)
    {
        if (policy is null) return PolicyMissing;
        if (!policy.IsEnabled) return PolicyDisabled;
        if (providerClass == AiProviderClass.External && !policy.ExternalProcessingAllowed)
            return ExternalProcessingDenied;
        if (!PurposeAllowed(policy, purpose)) return PurposeDenied;
        if (!ProviderAllowed(policy, provider)) return ProviderDenied;
        if (!ModelAllowed(policy, model)) return ModelDenied;
        return null;
    }

    /// <summary>
    /// The provider classes of this tenant's most recent governed calls, denied ones
    /// excluded — the sample the dependency ratio is computed over.
    /// </summary>
    internal static async Task<IReadOnlyList<AiProviderClass>> RecentProviderClassesAsync(
        IQueryable<AiRequest> requests, long businessUnitId, CancellationToken ct) =>
        await requests.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Status != AiCallStatuses.Denied)
            .OrderByDescending(x => x.CreatedOn).Take(DependencyWindow)
            .Select(x => x.ProviderClass).ToListAsync(ct);

    /// <summary>
    /// Whether one more external call would push the tenant past its configured ceiling.
    /// The "+1" on both sides is the call being considered: the ceiling is a forecast, not
    /// a history report.
    /// </summary>
    internal static bool ExceedsExternalDependencyCeiling(
        IReadOnlyList<AiProviderClass> recentProviderClasses, decimal ceilingPercent) =>
        ExternalDependencyRatio(recentProviderClasses) > ceilingPercent / 100m;

    /// <summary>The forecast ratio itself, so an operator can be shown the number.</summary>
    internal static decimal ExternalDependencyRatio(IReadOnlyList<AiProviderClass> recentProviderClasses) =>
        (recentProviderClasses.Count(x => x == AiProviderClass.External) + 1m)
        / (recentProviderClasses.Count + 1m);
}
