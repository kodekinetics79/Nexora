using System.Text.Json.Serialization;
using ERP_RFQ_Automation.Models;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.AI;

/// <summary>
/// How one control in the extraction chain currently stands.
///
/// <para><see cref="NotApplicable"/> is deliberately NOT a pass: it means the control cannot
/// bite in this configuration (a loopback deployment egresses nothing, a structured payload
/// is not a whole document) or that an earlier closed lock removed the thing it tests. It
/// neither blocks nor counts as ready, so a local deployment shows greyed rows rather than
/// green ticks it has not earned.</para>
///
/// <para>Serialized by NAME. This host adds no global string-enum converter, so an unannotated
/// enum reaches the operator console as 0/1/2 — a number that means nothing in the screenshot
/// this report exists to be pasted into, and that silently re-labels every row if a member is
/// ever inserted above another.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AiReadinessStatus
{
    Pass,
    Fail,
    NotApplicable,

    /// <summary>
    /// Open, and still a fact somebody has to decide about. It never blocks a document and
    /// never makes the report un-ready: it is for a control that is satisfied only because
    /// nobody has set it, where the unset value carries a standing cost or risk.
    /// </summary>
    Warn,

    /// <summary>
    /// NOT an independent finding: this control's outcome follows entirely from an earlier
    /// closed one, and opening that one settles this row without anybody touching it.
    ///
    /// <para>Distinct from <see cref="NotApplicable"/>, which says the control cannot bite in
    /// this configuration at all. Blocked rows are excluded from
    /// <see cref="AiExtractionReadinessReport.BlockingCount"/> — counting them told operators
    /// to fix three things when two settings were closed, and the third row's instruction was
    /// to do something they had already done.</para>
    /// </summary>
    Blocked
}

/// <summary>
/// Stable identifiers for the controls, in firing order. They are the contract the UI and
/// any operator tooling match on; the human-readable titles are not.
/// </summary>
public static class AiReadinessCodes
{
    public const string EndpointResolved = "endpoint_resolved";
    public const string PolicyPresent = "policy_present";
    public const string PolicyEnabled = "policy_enabled";
    public const string ExternalProcessingAllowed = "external_processing_allowed";
    public const string AuthorizationPresent = "authorization_present";
    public const string AuthorizationLive = "authorization_live";
    public const string AuthorizationPurpose = "authorization_purpose";
    public const string EgressPolicyWholeDocument = "egress_policy_whole_document";
    public const string AuthorizationUnstructured = "authorization_unstructured";
    public const string PolicyPurposeAllowed = "policy_purpose_allowed";
    public const string PolicyProviderAllowed = "policy_provider_allowed";
    public const string PolicyModelAllowed = "policy_model_allowed";
    public const string DependencyCeiling = "external_dependency_ceiling";
    public const string MonthlyHardBudget = "monthly_hard_token_budget";

    /// <summary>
    /// Not one of the fourteen controls: the pre-flight was asked about a tenant other than
    /// the authenticated one. It appears only in that case, and only so a mis-scoped call
    /// can never render as a ready chain with nothing closed.
    /// </summary>
    public const string TenantScope = "tenant_scope";
}

/// <summary>
/// One control in the extraction chain, as an operator has to act on it.
///
/// <para><see cref="CurrentValue"/> and <see cref="RequiredValue"/> are rendered
/// CONFIGURATION values only. Never an API key, never a connection string, never a
/// justification, never a byte of document text — a readiness page is one of the most widely
/// shared screenshots in an incident, and it must stay safe to paste into a ticket.</para>
/// </summary>
/// <param name="DenialReason">
/// The EXACT code the enforcing layer emits for this control, so the row can be matched
/// against a dead-lettered job's stored error. Null unless <see cref="Status"/> is Fail.
/// </param>
/// <param name="SetItIn">Where an operator changes it. Empty when nothing is to be set.</param>
public sealed record AiExtractionReadinessCheck(
    int Order,
    string Code,
    string Title,
    AiReadinessStatus Status,
    string? DenialReason,
    string CurrentValue,
    string RequiredValue,
    string SetItIn,
    string Detail);

/// <summary>
/// Everything that must agree before an unstructured RFQ document can be read by AI for one
/// tenant, in the order it fires.
///
/// <para><b>Read-only, and a report only.</b> It evaluates; it never remediates. The decision
/// to let customer document text leave the tenant's infrastructure stays an explicit,
/// attributable human act with a written justification and an expiry, made through the Owner
/// endpoints this report points at. <see cref="Ready"/> means "no control is closed at this
/// instant" — it is never an input to an enforcement decision, and no code path may branch on
/// it.</para>
/// </summary>
/// <param name="BlockingCount">
/// Root causes only. A control whose outcome follows from an earlier closed one is reported
/// as <see cref="AiReadinessStatus.Blocked"/> and is not counted here, so this number is the
/// number of settings an operator actually has to change.
/// </param>
/// <param name="WarningCount">
/// Controls that are open but carry a standing decision — today, a tenant with no monthly
/// token ceiling at all. Never affects <see cref="Ready"/>: these documents extract.
/// </param>
/// <param name="FirstBlockingReason">
/// The denial code of the first closed control. For controls 1-9 this is exactly what
/// <c>IAiExternalProviderTrust.EvaluateAsync</c> would return for the same tenant; for 10-14
/// it is what the token ledger's reservation would refuse with AFTER the allow-list gate has
/// already passed the document.
/// </param>
public sealed record AiExtractionReadinessReport(
    AiProviderDescriptor ResolvedProvider,
    string Purpose,
    bool UnstructuredPayload,
    bool Ready,
    string? FirstBlockingReason,
    int BlockingCount,
    int WarningCount,
    DateTime EvaluatedOnUtc,
    IReadOnlyList<AiExtractionReadinessCheck> Checks);

/// <summary>
/// The pre-flight.
///
/// <para><b>The defect it exists for.</b> Five controls in three layers must all agree before
/// an unstructured document is read. They fail with different reason codes, in different
/// layers, and there was no single place an operator could see which one was closed — so the
/// 2026-08 pilot dead-lettered every document it submitted for days, fixing one lock per
/// deploy, while the extraction log said the call had been ALLOWED right up to the ledger
/// refusing it for a capital letter in <c>AllowedModel</c>.</para>
///
/// <para><b>It owns no rules.</b> Controls 1-9 come from
/// <see cref="AiExternalProviderTrustService.EvaluateAllAsync"/> — the same statements, in the
/// same order, that decide a real document's fate — and 10-14 from
/// <see cref="AiPolicyDenials"/>, which the reservation itself calls. A pre-flight with its
/// own copy of the rules would drift from enforcement, and a report that disagrees with the
/// gate is worse than no report at all.</para>
///
/// <para>It performs no writes of any kind: no ledger row, no audit event, no policy or
/// authorization mutation, and no refusal log for a document nobody submitted.</para>
/// </summary>
public sealed class AiExtractionReadinessService(
    ErpRfqAutomationContext db,
    AiExternalProviderTrustService trust,
    IAiProviderEndpointResolver endpointResolver)
{
    private const string SetInAiPolicy =
        "PUT /api/platform/tenants/{id}/ai-policy (platform Owner, second factor required)";

    private const string SetInAiProviders =
        "POST /api/platform/tenants/{id}/ai-providers (platform Owner, second factor required)";

    private const string SetInDeployment =
        "Deployment configuration: Ollama:BaseUrl";

    private const string SetInProvisioning =
        "Tenant provisioning (the AI processing policy is created with the tenant)";

    /// <param name="businessUnitId">
    /// The tenant to report on, derived by the caller from the AUTHENTICATED context — a
    /// platform route id resolved against Tenant.PrimaryBusinessUnitId, or the tenant plane's
    /// own businessUnitId claim. Never a request body. The chain refuses outright unless the
    /// ambient tenant scope already matches it.
    /// </param>
    public async Task<AiExtractionReadinessReport> EvaluateAsync(long businessUnitId, CancellationToken ct)
    {
        var provider = endpointResolver.Current;
        var local = provider.ProviderClass == AiProviderClass.Local;
        const string purpose = AiPurposes.RfqExtraction;

        var evaluation = await trust.EvaluateAllAsync(
            businessUnitId, provider, purpose, unstructuredPayload: true, ct);
        var locks = evaluation.Locks.ToDictionary(x => x.Code, StringComparer.Ordinal);

        // Re-read rather than thread the row out of the gate: controls 10-12 live in a
        // different layer and must be reported from the row as it stands, not as an
        // enforcement path happened to observe it.
        var policy = await db.AiProcessingPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId, ct);

        // Local processing does not egress, so the destination controls cannot bite. The
        // policy row's purpose/provider/model tests still do — AiPolicyDenials.Evaluate runs
        // them for a local reservation too, which is the single fact most readers get wrong
        // about that method.
        var mootLocally = $"Provider class Local ({provider.ClassificationReason}) — nothing egresses";

        var checks = new List<AiExtractionReadinessCheck>
        {
            Trust(1, AiReadinessCodes.EndpointResolved, locks, "Inference endpoint resolves",
                SetInDeployment,
                "The origin this process will actually call, normalised to scheme://host[:port]. "
                + "An allow-list grant names this exact origin, so nothing can be authorized until it resolves."),

            Trust(2, AiReadinessCodes.PolicyPresent, locks, "AI processing policy exists",
                SetInProvisioning,
                "The tenant's standing AI governance row. Absent, every AI call for this tenant is refused."),

            Trust(3, AiReadinessCodes.PolicyEnabled, locks, "AI processing is enabled",
                SetInAiPolicy,
                "The tenant-wide switch. Off refuses local and external processing alike."),

            local
                ? Moot(4, AiReadinessCodes.ExternalProcessingAllowed, "External processing is consented to",
                    mootLocally)
                : Trust(4, AiReadinessCodes.ExternalProcessingAllowed, locks,
                    "External processing is consented to", SetInAiPolicy,
                    "Ships FALSE by default and stays false until a named Owner turns it on. The same request "
                    + "must also carry RedactionRequired, PrivacyReviewRequired, AllowedProvider and AllowedModel "
                    + "— the policy endpoint rejects the update otherwise."),

            local
                ? Moot(5, AiReadinessCodes.AuthorizationPresent, "A grant names this destination", mootLocally)
                : Trust(5, AiReadinessCodes.AuthorizationPresent, locks,
                    "A grant names this destination", SetInAiProviders,
                    "Provider is matched case-insensitively; the origin and the model are matched EXACTLY "
                    + $"(a grant may name {AiProviderEndpoint.AnyModel} to cover every model at that origin)."),

            local
                ? Moot(6, AiReadinessCodes.AuthorizationLive, "That grant is live", mootLocally)
                : Trust(6, AiReadinessCodes.AuthorizationLive, locks, "That grant is live", SetInAiProviders,
                    "A revoked or lapsed grant refuses exactly like a missing one. Re-authorizing is itself an "
                    + "attributed act: it records the actor, the justification and a new expiry."),

            local
                ? Moot(7, AiReadinessCodes.AuthorizationPurpose, "That grant covers RFQ extraction", mootLocally)
                : Trust(7, AiReadinessCodes.AuthorizationPurpose, locks,
                    "That grant covers RFQ extraction", SetInAiProviders,
                    "Purposes are per grant, so a destination approved for BOQ drafting alone does not "
                    + "silently widen to extraction."),

            local
                ? Moot(8, AiReadinessCodes.EgressPolicyWholeDocument, "Egress policy permits whole documents",
                    mootLocally)
                : Trust(8, AiReadinessCodes.EgressPolicyWholeDocument, locks,
                    "Egress policy permits whole documents", SetInAiPolicy,
                    $"Exactly \"{AiEgressPolicies.FullDocument}\" opts in, compared trimmed and "
                    + "case-insensitively — casing does not matter, surrounding text does, and every "
                    + $"unrecognised value reads as the strict \"{AiEgressPolicies.RedactedFieldsOnly}\". "
                    + "This is the tenant's standing statement about its data; the grant below is one "
                    + "destination's permission. Both must agree."),

            local
                ? Moot(9, AiReadinessCodes.AuthorizationUnstructured, "That grant covers unstructured documents",
                    mootLocally)
                : Trust(9, AiReadinessCodes.AuthorizationUnstructured, locks,
                    "That grant covers unstructured documents", SetInAiProviders,
                    "The high-risk switch, separate from the purpose list and defaulting to false: an ordinary "
                    + "grant does NOT cover whole document text."),

            PolicyCheck(10, AiReadinessCodes.PolicyPurposeAllowed, "Policy allows the RfqExtraction purpose",
                policy, AiPolicyDenials.PurposeDenied,
                p => AiPolicyDenials.PurposeAllowed(p, purpose),
                p => $"AllowedPurposes = \"{p.AllowedPurposes}\"",
                $"AllowedPurposes contains {purpose}",
                "Re-tested at the token ledger for local and external calls alike, and matched "
                + "case-insensitively."),

            PolicyCheck(11, AiReadinessCodes.PolicyProviderAllowed, "Policy allows this provider",
                policy, AiPolicyDenials.ProviderDenied,
                p => AiPolicyDenials.ProviderAllowed(p, provider.Provider),
                p => string.IsNullOrWhiteSpace(p.AllowedProvider)
                    ? "AllowedProvider = (unset — every provider allowed)"
                    : $"AllowedProvider = \"{p.AllowedProvider}\"",
                $"AllowedProvider = \"{provider.Provider}\" (or unset)",
                "Compared case-insensitively: a provider name is a label. Contrast the model below."),

            PolicyCheck(12, AiReadinessCodes.PolicyModelAllowed, "Policy allows this model",
                policy, AiPolicyDenials.ModelDenied,
                p => AiPolicyDenials.ModelAllowed(p, provider.Model),
                p => string.IsNullOrWhiteSpace(p.AllowedModel)
                    ? "AllowedModel = (unset — every model allowed)"
                    : $"AllowedModel = \"{p.AllowedModel}\"",
                $"AllowedModel = \"{provider.Model}\" (or unset)",
                "AllowedModel is compared ORDINAL — CASE-SENSITIVE — while AllowedProvider directly above it "
                + "is not. One capital letter refuses every document this tenant submits, and it is refused "
                + "in the token ledger AFTER the allow-list gate has already passed the document, under "
                + $"{AiPolicyDenials.ModelDenied}, which triages as a generic extraction failure rather than "
                + $"an authorization one. Copy the required value exactly as printed: {provider.Model}")
        };
        // 13 and 14 are appended rather than initialised with the rest: the ceiling row reports
        // which of the rows ABOVE it it is waiting on, and reading them is how it names the
        // same number and the same wording the operator is looking at.
        checks.Add(await CeilingCheckAsync(13, businessUnitId, provider, policy, checks, local, ct));
        checks.Add(await BudgetCheckAsync(14, businessUnitId, policy, ct));

        // A mis-scoped call carries no lock rows at all, because it says the CALLER is wrong
        // rather than that the tenant's configuration is closed. Without this row such a
        // report would render with nothing closed.
        if (evaluation.Locks.Count == 0 && !evaluation.Decision.Allowed)
            checks.Insert(0, new AiExtractionReadinessCheck(
                0, AiReadinessCodes.TenantScope, "Tenant scope", AiReadinessStatus.Fail,
                evaluation.Decision.Reason,
                "the ambient tenant scope does not match the tenant being reported on",
                "a request scoped to this tenant", "n/a — this is a caller fault, not a setting",
                "The chain refuses to evaluate another tenant's configuration, so nothing below was tested."));

        // Fail is a ROOT CAUSE: a control closed on its own account, that somebody has to go
        // and open. Blocked rows follow from one of these and settle themselves, so they are
        // reported and not counted.
        var blocking = checks.Where(x => x.Status == AiReadinessStatus.Fail).ToList();
        return new AiExtractionReadinessReport(
            provider, purpose, UnstructuredPayload: true,
            Ready: blocking.Count == 0,
            FirstBlockingReason: blocking.FirstOrDefault()?.DenialReason,
            BlockingCount: blocking.Count,
            WarningCount: checks.Count(x => x.Status == AiReadinessStatus.Warn),
            EvaluatedOnUtc: DateTime.UtcNow,
            Checks: checks);
    }

    /// <summary>Projects one lock the allow-list chain reported onto its operator-facing row.</summary>
    private static AiExtractionReadinessCheck Trust(
        int order, string code, IReadOnlyDictionary<string, AiTrustLockOutcome> locks,
        string title, string setItIn, string detail)
    {
        // An absent lock means an earlier closed one removed what this control tests. Not a
        // pass, not a failure of its own, and not NotApplicable either — this control applies
        // perfectly well, it simply was not reached. Nothing here is invented.
        if (!locks.TryGetValue(code, out var outcome))
            return new(order, code, title, AiReadinessStatus.Blocked, null,
                "not evaluated: an earlier control is closed", string.Empty, string.Empty, detail);

        var moot = outcome.Status == AiReadinessStatus.NotApplicable;
        return new(order, code, title, outcome.Status,
            outcome.Status == AiReadinessStatus.Fail ? outcome.DenialReason : null,
            outcome.CurrentValue,
            moot ? string.Empty : outcome.RequiredValue,
            moot ? string.Empty : setItIn,
            detail);
    }

    private static AiExtractionReadinessCheck Moot(int order, string code, string title, string current) =>
        new(order, code, title, AiReadinessStatus.NotApplicable, null, current,
            string.Empty, string.Empty,
            "Not applicable to this deployment: it neither blocks nor counts as ready.");

    /// <summary>
    /// One of the token ledger's policy-row denials. Evaluated through
    /// <see cref="AiPolicyDenials"/> so this row and the reservation cannot disagree.
    /// </summary>
    private static AiExtractionReadinessCheck PolicyCheck(
        int order, string code, string title, AiProcessingPolicy? policy, string denialReason,
        Func<AiProcessingPolicy, bool> allowed, Func<AiProcessingPolicy, string> current,
        string required, string detail)
    {
        if (policy is null)
            return new(order, code, title, AiReadinessStatus.NotApplicable, null,
                "not evaluated: no AI processing policy row", string.Empty, string.Empty, detail);

        var pass = allowed(policy);
        return new(order, code, title,
            pass ? AiReadinessStatus.Pass : AiReadinessStatus.Fail,
            pass ? null : denialReason,
            current(policy), required, SetInAiPolicy, detail);
    }

    /// <summary>
    /// The external-dependency ratio. A cost control, not an authorization — and an endpoint
    /// the tenant explicitly authorized is exempt from it, which is why this reads
    /// NotApplicable exactly when the destination controls above are all open.
    /// </summary>
    private async Task<AiExtractionReadinessCheck> CeilingCheckAsync(
        int order, long businessUnitId, AiProviderDescriptor provider, AiProcessingPolicy? policy,
        IReadOnlyList<AiExtractionReadinessCheck> earlier, bool local, CancellationToken ct)
    {
        const string title = "External dependency ceiling";
        const string detail =
            "Governs UNAUTHORIZED external usage only, as a share of the last 100 governed calls. A live "
            + "grant for this destination exempts the call from the ratio, and the exempting authorization "
            + "id is written to the ledger row. Since every unauthorized external reservation is now refused "
            + "outright by the allow-list gate, this is defence in depth rather than a live path.";

        if (local)
            return Moot(order, AiReadinessCodes.DependencyCeiling, title,
                $"Provider class Local ({provider.ClassificationReason}) — local calls are not rationed");

        // The destination controls a live grant exempts this ratio from, read from the rows
        // themselves so the number and the wording this row cites are the ones on screen.
        string[] destination =
        [
            AiReadinessCodes.EndpointResolved, AiReadinessCodes.PolicyPresent,
            AiReadinessCodes.PolicyEnabled, AiReadinessCodes.ExternalProcessingAllowed,
            AiReadinessCodes.AuthorizationPresent, AiReadinessCodes.AuthorizationLive,
            AiReadinessCodes.AuthorizationPurpose
        ];
        var waitingOn = earlier.FirstOrDefault(
            check => destination.Contains(check.Code) && check.Status != AiReadinessStatus.Pass);

        if (waitingOn is null)
            return Moot(order, AiReadinessCodes.DependencyCeiling, title,
                "exempt: this destination holds a live authorization covering this purpose");

        if (policy is null)
            return new(order, AiReadinessCodes.DependencyCeiling, title, AiReadinessStatus.Blocked,
                null, "not evaluated: no AI processing policy row", string.Empty, string.Empty, detail);

        // Reported, never counted. Reaching this line REQUIRES one of controls 1-7 to be
        // closed — a live grant for this destination exempts the call from the ratio — so the
        // ratio here is always measuring a state the operator is in the middle of leaving.
        // Counting it printed a fourteenth red row whose instruction was "authorize this
        // destination (controls 5-7)" while those very controls read Satisfied six rows above,
        // and turned two closed settings into "3 controls blocking".
        var recent = await AiPolicyDenials.RecentProviderClassesAsync(db.AiRequests, businessUnitId, ct);
        var ratio = AiPolicyDenials.ExternalDependencyRatio(recent) * 100m;

        return new(order, AiReadinessCodes.DependencyCeiling, title, AiReadinessStatus.Blocked, null,
            $"waiting on control {waitingOn.Order} ({waitingOn.Title}) — once that is open, this "
            + "destination's own grant exempts the call from the ratio. For reference: one more "
            + $"UNAUTHORIZED external call would be {ratio:0.0}% of the last {recent.Count + 1} "
            + $"governed calls, against ExternalDependencyCeilingPercent = "
            + $"{policy.ExternalDependencyCeilingPercent}",
            string.Empty, string.Empty, detail);
    }

    /// <summary>
    /// The monthly token ceiling — the reservation's LAST test, and the one control an
    /// allow-list grant does not exempt.
    ///
    /// <para>It is reported because a report that omits it recreates the exact defect this
    /// pre-flight exists to end: <c>MonthlyHardTokenLimit = 0</c> is a legal policy value
    /// (the endpoint rejects only negatives), an ordinary mid-month exhaustion produces the
    /// same state, and either way every control above reads open while the ledger refuses
    /// every document with <c>hard_budget_exceeded</c> — a code that would appear in no row.
    /// The tenant would be told READY and the documents would still die.</para>
    /// </summary>
    private async Task<AiExtractionReadinessCheck> BudgetCheckAsync(
        int order, long businessUnitId, AiProcessingPolicy? policy, CancellationToken ct)
    {
        const string title = "Monthly token budget has headroom";
        const string detail =
            "Reserved + settled tokens for the current UTC calendar month against "
            + "MonthlyHardTokenLimit. Zero is a legal limit and refuses every call; so does an "
            + "exhausted month. Leave the limit unset for no ceiling. The SOFT limit is not "
            + "tested here because it only flags a call in the ledger, it never refuses one. "
            + "This is the one control below which no allow-list grant exempts a call, and it "
            + "is applied after every control above it has already passed. An UNSET limit warns "
            + "rather than passes: documents extract, but the tenant's AI spend has no ceiling "
            + "at all, and a tick is the wrong thing to print next to an unpriced liability.";

        if (policy is null)
            return new(order, AiReadinessCodes.MonthlyHardBudget, title, AiReadinessStatus.NotApplicable,
                null, "not evaluated: no AI processing policy row", string.Empty, string.Empty, detail);

        // The POLICY's limit, not the period row's: the reservation copies this value onto the
        // row immediately before testing it, so a row carrying last month's limit is never
        // what bites, and reporting the row's copy would show a number that does not apply.
        // Warn, not Pass. Nothing is closed — every document extracts — but "no monthly
        // ceiling" is unbounded spend, and rendering that as a green tick is how a tenant
        // reaches go-live with no cost control anybody ever decided on. It is deliberately not
        // a Fail: a budget row that blocked because nobody typed a number would send operators
        // to change a control that is not shut.
        if (policy.MonthlyHardTokenLimit is not { } hard)
            return new(order, AiReadinessCodes.MonthlyHardBudget, title, AiReadinessStatus.Warn, null,
                "MonthlyHardTokenLimit = (unset — no monthly ceiling: this tenant's AI spend is unbounded)",
                "a monthly ceiling sized to this tenant's plan, or a deliberate decision to leave it open",
                SetInAiPolicy, detail);

        var period = AiPolicyDenials.BudgetPeriodStart(DateTime.UtcNow);
        var budget = await db.AiBudgetPeriods.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId && x.PeriodStartUtc == period, ct);
        // No row yet is a month in which nothing has been spent: the reservation creates it.
        var reserved = budget?.ReservedTokens ?? 0;
        var settled = budget?.SettledTokens ?? 0;

        // Asked with the smallest reserve the ledger can compute, so this closes only when NO
        // document of any size could be admitted. A budget with room for a short document and
        // not a long one is a fact about that document, not a closed control, and reporting it
        // as one would train operators to ignore the row.
        var exhausted = AiPolicyDenials.ExceedsHardTokenBudget(
            hard, reserved, settled, AiPolicyDenials.SmallestPossibleReservation);

        return new(order, AiReadinessCodes.MonthlyHardBudget, title,
            exhausted ? AiReadinessStatus.Fail : AiReadinessStatus.Pass,
            exhausted ? AiPolicyDenials.HardBudgetExceeded : null,
            $"MonthlyHardTokenLimit = {hard:N0}; {reserved + settled:N0} reserved + settled since "
            + $"{period:yyyy-MM-dd}, leaving {Math.Max(0, hard - reserved - settled):N0}",
            $"MonthlyHardTokenLimit above the {reserved + settled:N0} tokens already used this period "
            + "(or unset for no ceiling)",
            SetInAiPolicy, detail);
    }
}
