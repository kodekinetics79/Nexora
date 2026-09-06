using System.Data;
using System.Text.Json;
using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.MultiTenancy;
using ERP_RFQ_Automation.PlatformGovernance;
using Microsoft.EntityFrameworkCore;

namespace ERP_RFQ_Automation.AI;

/// <summary>
/// Per-tenant allow-list for external inference endpoints.
///
/// <para><b>Why this replaces a binary rule.</b> The previous control was
/// <c>if (ProviderClass == External) refuse</c>. That is a single global bit with two
/// terrible end states: refuse everything (what production actually did — every AI
/// extraction dead, discovered only because a consultant read the source), or, if someone
/// deletes the refusal, accept whatever host an environment variable names, with no record
/// of who decided that or when. This service makes the decision a first-class, per-tenant,
/// attributed database row: a named user authorized one exact origin, for one model (or an
/// explicit "any model"), for named purposes, with unstructured-document egress as its own
/// separate switch, with a written justification, optionally time-boxed, and revocable.
/// Everything not on the list still fails closed with the original refusal.</para>
///
/// <para><b>What it does NOT do.</b> It never bypasses another control. Even an authorized
/// endpoint must still satisfy <see cref="AiProcessingPolicy.ExternalProcessingAllowed"/>
/// (re-checked here as well as in the ledger), <see cref="AiProcessingPolicy.EgressPolicy"/>,
/// the purpose/provider/model policy denials, the reserve/attempt/settle token ledger and
/// its monthly + per-document budgets, the external-dependency ceiling, the prompt-injection
/// nonce boundary, strict-JSON-or-fail parsing and line-item count conservation. It does NOT
/// redact: nothing in this build does, whatever
/// <see cref="AiProcessingPolicy.RedactionRequired"/> says.</para>
/// </summary>
public sealed class AiExternalProviderTrustService : IAiExternalProviderTrust
{
    private const string AuditArea = "AITrust";
    private const string AuditAggregateType = "AiExternalProviderAuthorization";
    private const string AuthorizedAction = "EXTERNAL_PROVIDER_AUTHORIZED";
    private const string RevokedAction = "EXTERNAL_PROVIDER_REVOKED";

    /// <summary>
    /// Configuration key for <see cref="_autoProvisionInternal"/>:
    /// <c>Ai:ExternalProvider:AutoAuthorizeInternalDeployment</c>.
    /// </summary>
    internal const string AutoProvisionConfigKey =
        "Ai:ExternalProvider:AutoAuthorizeInternalDeployment";

    internal const string AutoProvisionActor = "system:auto-internal";

    internal const string AutoProvisionJustification =
        "Auto-provisioned for a single-tenant installation that explicitly set "
        + AutoProvisionConfigKey
        + "=true: whoever deployed this installation chose the inference endpoint and is also "
        + "the party whose documents egress to it, and no self-service tenant admin exists to "
        + "author a grant for it. Unset that key to require an explicit, human-authored "
        + "authorization with a named approver instead.";

    private readonly ErpRfqAutomationContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<AiExternalProviderTrustService> _log;
    private readonly bool _autoProvisionInternal;

    /// <param name="configuration">
    /// Optional, and last, so a unit test constructing this service directly gets the gate
    /// with NO auto-provisioning — those tests exist to prove the chain refuses, and a
    /// service that quietly authorized itself would make them assert nothing. The composed
    /// application always supplies it (DI resolves the longest constructor it can satisfy),
    /// which is what turns the behaviour on in a real deployment.
    /// </param>
    public AiExternalProviderTrustService(
        ErpRfqAutomationContext db,
        ITenantContext tenantContext,
        IAiProviderEndpointResolver endpointResolver,
        ILogger<AiExternalProviderTrustService> log,
        IConfiguration? configuration = null)
    {
        _db = db;
        _tenantContext = tenantContext;
        _log = log;
        // Defaults to FALSE, and the default is the whole point of the flag.
        //
        // It shipped defaulting to TRUE on the reasoning that this is an internal-use build
        // whose endpoint is chosen by whoever deploys it, and that requiring a grant only made
        // fresh tenants dead-letter every document. Both halves of that are true and neither
        // survives contact with a multi-tenant deployment: the only caller of the gate is the
        // EXTERNAL branch of the ledger and of the extraction path (see the remarks below), so
        // every run of this method is a decision to send one tenant's document text to a host
        // they do not control — taken with no human, no named approver, no justification a
        // customer could be shown, and invisible to the read-only pre-flight, which performs no
        // writes and therefore goes on reporting those very controls closed while documents
        // egress. A product sold on governance cannot make that decision by default. The
        // dead-lettering it was added to prevent is a real problem and belongs to the operator
        // console: one guided action that issues the same audited calls a person signs.
        _autoProvisionInternal = configuration is not null
            && (configuration.GetValue<bool?>(AutoProvisionConfigKey) ?? false);
        ResolvedProvider = endpointResolver.Current;
        KnownProviders = endpointResolver.All;
    }

    public AiProviderDescriptor ResolvedProvider { get; }

    /// <inheritdoc />
    public IReadOnlyList<AiProviderDescriptor> KnownProviders { get; }

    // ---- enforcement -----------------------------------------------------

    /// <inheritdoc />
    public async Task<AiExternalProviderDecision> EvaluateAsync(
        long businessUnitId, AiProviderDescriptor provider, string purpose,
        bool unstructuredPayload, CancellationToken ct)
    {
        await EnsureInternalDeploymentAuthorizationAsync(businessUnitId, provider, ct);
        return (await RunAsync(businessUnitId, provider, purpose, unstructuredPayload,
            stopAtFirstDenial: true, ct)).Decision;
    }

    /// <summary>
    /// Fills in the configuration a self-service tenant admin would have authored, for a
    /// deployment that has no such admin.
    ///
    /// <para><b>Why this exists.</b> Nine separate conditions have to hold before one PDF can
    /// be read (endpoint resolved, policy row present, enabled, external processing allowed,
    /// a matching grant, live, covering the purpose, an egress policy that permits whole
    /// documents, and that grant's separate unstructured switch). A fresh tenant satisfies
    /// exactly one of them — the endpoint — because every other default is the secure one.
    /// The result is not a warning: it is <c>EXTRACTION_AI_NOT_AUTHORIZED</c> on every
    /// document, which reads to an operator as "the extractor is broken".</para>
    ///
    /// <para><b>Off unless a deployment asks for it, and it is always about egress.</b> The
    /// gate this hangs off is reached only when the resolved provider class is
    /// <see cref="AiProviderClass.External"/> — both callers, the token ledger's
    /// <c>AuthorizeExternalAsync</c> and the conversational extraction gate, test that first —
    /// so there is no benign local case here: every row this writes widens one tenant's consent
    /// to send document text off their own infrastructure. That is a decision for a named human
    /// on the record, so <see cref="AutoProvisionConfigKey"/> defaults to FALSE and only a
    /// single-tenant installation whose deployer IS the customer should turn it on.</para>
    ///
    /// <para><b>What it will and will not do.</b> It only ever fills an ABSENT decision. It
    /// never overrides a REVOKED grant — revocation is a deliberate act and is respected, so
    /// turning this on cannot resurrect something a human switched off. A merely EXPIRED grant
    /// is treated as a lapse and re-provisioned, because an internal deployment losing its
    /// extractor at midnight on an expiry date is an outage, not a control. It writes real
    /// rows rather than short-circuiting the chain, so the audit event, the Trust Center, the
    /// readiness pre-flight, revocation and the token ledger all keep working on exactly the
    /// evidence they always used — the grant simply names <see cref="AutoProvisionActor"/>
    /// as its author instead of a person.</para>
    /// </summary>
    private async Task EnsureInternalDeploymentAuthorizationAsync(
        long businessUnitId, AiProviderDescriptor provider, CancellationToken ct)
    {
        if (!_autoProvisionInternal || businessUnitId <= 0 || !provider.IsResolved)
            return;
        // Same rule the evaluation itself enforces: never touch another tenant's allow-list,
        // and never act with no ambient tenant (the BYPASSRLS background-worker case).
        if (_tenantContext.BusinessUnitId != businessUnitId)
            return;

        var now = DateTime.UtcNow;
        var wrote = false;

        var policy = await _db.AiProcessingPolicies
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId, ct);
        if (policy is null)
        {
            policy = AiProcessingPolicy.CreateSecureDefault(businessUnitId, AutoProvisionActor, now);
            _db.AiProcessingPolicies.Add(policy);
            wrote = true;
        }

        // The three fields that stop an unstructured RFQ, widened only when they are the
        // secure default that no human has since narrowed on purpose.
        if (!policy.IsEnabled) { policy.IsEnabled = true; wrote = true; }
        if (!policy.ExternalProcessingAllowed) { policy.ExternalProcessingAllowed = true; wrote = true; }
        if (!AiEgressPolicies.PermitsWholeDocument(policy.EgressPolicy))
        {
            policy.EgressPolicy = AiEgressPolicies.FullDocument;
            wrote = true;
        }
        foreach (var purpose in AutoProvisionPurposes)
        {
            if (!policy.AllowedPurposes.Split(',', StringSplitOptions.RemoveEmptyEntries
                    | StringSplitOptions.TrimEntries)
                .Contains(purpose, StringComparer.OrdinalIgnoreCase))
            {
                policy.AllowedPurposes = string.Join(",", AutoProvisionPurposes);
                wrote = true;
                break;
            }
        }
        if (wrote)
        {
            policy.UpdatedOn = now;
            policy.UpdatedBy = AutoProvisionActor;
        }

        var existing = await _db.AiExternalProviderAuthorizations
            .Where(x => x.BusinessUnitId == businessUnitId && x.Endpoint == provider.Endpoint)
            .ToListAsync(ct);
        var matching = existing
            .Where(x => AiProviderEndpoint.ProviderMatches(x.Provider, provider.Provider)
                        && AiProviderEndpoint.ModelMatches(x.Model, provider.Model))
            .ToList();

        // A human said no. Leave it alone — that is the whole point of a revocable control.
        if (!matching.Any(x => x.IsRevoked) && !matching.Any(x => x.IsActive(now)))
        {
            _db.AiExternalProviderAuthorizations.Add(new AiExternalProviderAuthorization
            {
                BusinessUnitId = businessUnitId,
                Provider = provider.Provider,
                Endpoint = provider.Endpoint,
                Model = AiProviderEndpoint.AnyModel,
                AllowedPurposes = string.Join(",", AutoProvisionPurposes),
                UnstructuredDocumentsAllowed = true,
                Justification = AutoProvisionJustification,
                AuthorizedByUserId = 0,
                AuthorizedBy = AutoProvisionActor,
                AuthorizedOn = now,
                ExpiresOn = null,
            });
            wrote = true;
        }

        if (!wrote)
            return;

        try
        {
            await _db.SaveChangesAsync(ct);
            _log.LogInformation(
                "Auto-provisioned internal AI egress authorization for tenant {Tenant} at {Endpoint} "
                + "({ConfigKey} is on).", businessUnitId, provider.Endpoint, AutoProvisionConfigKey);
        }
        catch (DbUpdateException ex)
        {
            // Two workers can reach this concurrently on a tenant's first document. The loser
            // finds the winner's rows on the evaluation immediately below, so this is a
            // no-op, not a failure — and it must never fail the extraction it exists to allow.
            _db.ChangeTracker.Clear();
            _log.LogDebug(ex,
                "Concurrent auto-provision for tenant {Tenant}; using the row the other writer committed.",
                businessUnitId);
        }
    }

    private static readonly string[] AutoProvisionPurposes =
        [AiPurposes.RfqExtraction, AiPurposes.BoqDraft, AiPurposes.Agent];

    /// <summary>
    /// The same chain, evaluated to the END instead of stopping at the first closed lock.
    ///
    /// <para>Five controls in three layers have to agree before an unstructured document can
    /// be read, and each one used to be discoverable only by fixing the previous one and
    /// resubmitting. That cost the 2026-08 pilot days of dead-lettered documents. This exists
    /// so the whole chain can be reported in one pass — read-only, no reservation, no ledger
    /// row, no audit event, and no refusal warning for a document nobody sent.</para>
    ///
    /// <para>It is deliberately NOT on <see cref="IAiExternalProviderTrust"/> and its result
    /// is a report: no enforcement path may branch on it.</para>
    /// </summary>
    internal Task<AiExternalProviderEvaluation> EvaluateAllAsync(
        long businessUnitId, AiProviderDescriptor provider, string purpose,
        bool unstructuredPayload, CancellationToken ct) =>
        RunAsync(businessUnitId, provider, purpose, unstructuredPayload,
            stopAtFirstDenial: false, ct);

    /// <param name="stopAtFirstDenial">
    /// True for enforcement: the method returns at the first refusal, executing exactly the
    /// statements and issuing exactly the queries it always has. False for the pre-flight,
    /// which records the same refusal and keeps going wherever the remaining locks are still
    /// well-defined. The verdict is derived from the FIRST closed lock either way, so the two
    /// modes cannot disagree about whether a document may egress.
    /// </param>
    private async Task<AiExternalProviderEvaluation> RunAsync(
        long businessUnitId, AiProviderDescriptor provider, string purpose,
        bool unstructuredPayload, bool stopAtFirstDenial, CancellationToken ct)
    {
        var locks = new List<AiTrustLockOutcome>();
        AiExternalProviderAuthorization? granted = null;

        // A refusal that is NOT a lock an operator can open: it says the caller is wrong,
        // not that the tenant's configuration is closed. It carries no lock row, so a
        // pre-flight run outside the tenant's own scope can never report a green chain.
        AiExternalProviderEvaluation Refusal(string reason) =>
            new(AiExternalProviderDecision.Deny(reason, provider), locks);

        // Returns whether the caller must stop here.
        bool Blocked(string code, string reason, string current, string required)
        {
            locks.Add(new(code, AiReadinessStatus.Fail, reason, current, required));
            return stopAtFirstDenial;
        }

        void Open(string code, string current, string required) =>
            locks.Add(new(code, AiReadinessStatus.Pass, null, current, required));

        // Neither open nor closed: an earlier lock removed the thing this one tests, or the
        // payload does not reach it. It must not read as a green tick.
        void Moot(string code, string current) =>
            locks.Add(new(code, AiReadinessStatus.NotApplicable, null, current, string.Empty));

        AiExternalProviderEvaluation Result()
        {
            var closed = locks.FirstOrDefault(x => x.Status == AiReadinessStatus.Fail);
            return new(
                closed is not null
                    ? AiExternalProviderDecision.Deny(closed.DenialReason!, provider)
                    : AiExternalProviderDecision.Allow(granted!.Id, provider),
                locks);
        }

        if (businessUnitId <= 0)
            return Refusal(AiExternalProviderTrustReasons.TenantMismatch);

        // A caller may never evaluate another tenant's allow-list, and a caller with NO
        // ambient tenant may not evaluate one either.
        //
        // The null case used to be waved through on the reasoning that the query below is
        // explicitly predicated on businessUnitId anyway. That reasoning describes the happy
        // path and skips the one that matters: a null ambient tenant is the background-worker
        // case, where the EF global query filter is a no-op and the connection is BYPASSRLS.
        // Both of the isolation mechanisms this gate is layered on top of are switched off in
        // exactly the situation the check declined to examine, leaving a hand-written
        // predicate as the only thing standing between one tenant's egress authorization and
        // another's. AiGovernanceService.EnsureTenant already throws on a null ambient tenant;
        // this is the same rule, stated the same way. A background worker that legitimately
        // needs to evaluate a tenant's allow-list pushes that tenant's scope first — as the
        // extraction path already does — which is cheap, explicit and auditable.
        var scoped = _tenantContext.BusinessUnitId;
        if (scoped != businessUnitId)
        {
            _log.LogWarning(
                "External provider trust evaluation refused: tenant scope {ScopedTenant} does not match requested {RequestedTenant}.",
                scoped, businessUnitId);
            return Refusal(AiExternalProviderTrustReasons.TenantMismatch);
        }

        // Short-circuits in BOTH modes: with no origin there is nothing for an authorization
        // row to match, so reporting the rest of the chain against an empty endpoint would
        // invent refusals this deployment does not actually have.
        if (!provider.IsResolved)
        {
            Blocked(AiReadinessCodes.EndpointResolved, AiExternalProviderTrustReasons.EndpointUnresolved,
                $"unresolved ({provider.ClassificationReason})",
                "an absolute http/https URL with no credentials and no path");
            return Result();
        }
        Open(AiReadinessCodes.EndpointResolved, provider.Endpoint, provider.Endpoint);

        // The policy row remains authoritative. The allow-list narrows it; it can never
        // widen it, so a tenant whose secure default is still in force is refused here
        // before a single byte of document text is prepared for egress.
        var policy = await _db.AiProcessingPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId, ct);
        // Also a short-circuit in both modes: every remaining policy lock is a field ON this
        // row, and a field of a row that does not exist has no value to report.
        if (policy is null)
        {
            Blocked(AiReadinessCodes.PolicyPresent, AiExternalProviderTrustReasons.PolicyMissing,
                "no AiProcessingPolicy row for this tenant", "a provisioned AI processing policy row");
            return Result();
        }
        Open(AiReadinessCodes.PolicyPresent, "provisioned", "a provisioned AI processing policy row");

        if (!policy.IsEnabled)
        {
            if (Blocked(AiReadinessCodes.PolicyEnabled, AiExternalProviderTrustReasons.PolicyDisabled,
                    "IsEnabled = false", "IsEnabled = true"))
                return Result();
        }
        else Open(AiReadinessCodes.PolicyEnabled, "IsEnabled = true", "IsEnabled = true");

        if (!policy.ExternalProcessingAllowed)
        {
            if (Blocked(AiReadinessCodes.ExternalProcessingAllowed,
                    AiExternalProviderTrustReasons.PolicyExternalProcessingDenied,
                    "ExternalProcessingAllowed = false", "ExternalProcessingAllowed = true"))
                return Result();
        }
        else Open(AiReadinessCodes.ExternalProcessingAllowed,
            "ExternalProcessingAllowed = true", "ExternalProcessingAllowed = true");

        var candidates = await _db.AiExternalProviderAuthorizations.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Endpoint == provider.Endpoint)
            .ToListAsync(ct);

        var matching = candidates
            .Where(x => AiProviderEndpoint.ProviderMatches(x.Provider, provider.Provider)
                        && AiProviderEndpoint.EndpointMatches(x.Endpoint, provider.Endpoint)
                        && AiProviderEndpoint.ModelMatches(x.Model, provider.Model))
            .ToList();

        List<AiExternalProviderAuthorization>? forPurpose = null;
        if (matching.Count == 0)
        {
            if (Blocked(AiReadinessCodes.AuthorizationPresent, AiExternalProviderTrustReasons.NotAuthorized,
                    $"no grant matches {provider.Provider} / {provider.Endpoint} / {DescribeModel(provider.Model)}",
                    $"a grant for {provider.Provider} at {provider.Endpoint} naming model "
                    + $"{DescribeModel(provider.Model)} (or {AiProviderEndpoint.AnyModel} for any model)"))
                return Result();
            Moot(AiReadinessCodes.AuthorizationLive, "not evaluated: no matching grant to test");
            Moot(AiReadinessCodes.AuthorizationPurpose, "not evaluated: no matching grant to test");
        }
        else
        {
            Open(AiReadinessCodes.AuthorizationPresent,
                $"{matching.Count} matching grant(s)", "at least one matching grant");

            var now = DateTime.UtcNow;
            var live = matching.Where(x => x.IsActive(now)).ToList();
            if (live.Count == 0)
            {
                var revoked = matching.Any(x => x.IsRevoked);
                if (Blocked(AiReadinessCodes.AuthorizationLive,
                        revoked
                            ? AiExternalProviderTrustReasons.Revoked
                            : AiExternalProviderTrustReasons.Expired,
                        revoked
                            ? $"revoked on {Utc(matching.Where(x => x.IsRevoked).Max(x => x.RevokedOn))}"
                            : $"expired on {Utc(matching.Max(x => x.ExpiresOn))}",
                        "a live grant: re-authorize with a justification and a future expiry"))
                    return Result();
                Moot(AiReadinessCodes.AuthorizationPurpose, "not evaluated: no live grant to test");
            }
            else
            {
                Open(AiReadinessCodes.AuthorizationLive, $"{live.Count} live grant(s)", "a live grant");

                forPurpose = live.Where(x => x.CoversPurpose(purpose)).ToList();
                if (forPurpose.Count == 0)
                {
                    if (Blocked(AiReadinessCodes.AuthorizationPurpose,
                            AiExternalProviderTrustReasons.PurposeNotAuthorized,
                            $"AllowedPurposes = {string.Join(" | ", live.Select(x => x.AllowedPurposes))}",
                            $"a grant whose AllowedPurposes contains {purpose}"))
                        return Result();
                    forPurpose = null;
                }
                else
                {
                    Open(AiReadinessCodes.AuthorizationPurpose,
                        $"AllowedPurposes covers {purpose}", $"AllowedPurposes contains {purpose}");

                    // Prefer the most specific grant (an exact model beats a wildcard) so an
                    // operator can hold a narrow model grant alongside a broad one without the
                    // broad one silently deciding.
                    granted = forPurpose
                        .OrderBy(x => x.Model == AiProviderEndpoint.AnyModel ? 1 : 0)
                        .ThenByDescending(x => x.AuthorizedOn)
                        .First();
                }
            }
        }

        if (unstructuredPayload)
        {
            // The tenant-level egress policy, which until now was persisted, validated on
            // write, shown in the Trust Center and read by nothing. It is the tenant's
            // standing statement about what its data may ever become; the authorization below
            // is one destination's permission. Both must agree, and an unrecognised value
            // reads as the strict one.
            if (!AiEgressPolicies.PermitsWholeDocument(policy.EgressPolicy))
            {
                // Only the enforcing pass writes this. A read-only pre-flight that logged a
                // refusal would fill the egress record with documents nobody ever submitted.
                if (stopAtFirstDenial)
                    _log.LogWarning(
                        "Whole-document egress refused for tenant {Tenant}: EgressPolicy={EgressPolicy} does not permit it.",
                        businessUnitId, policy.EgressPolicy);
                if (Blocked(AiReadinessCodes.EgressPolicyWholeDocument,
                        AiExternalProviderTrustReasons.EgressPolicyForbidsWholeDocuments,
                        $"EgressPolicy = \"{policy.EgressPolicy}\"",
                        $"EgressPolicy = \"{AiEgressPolicies.FullDocument}\""))
                    return Result();
            }
            else Open(AiReadinessCodes.EgressPolicyWholeDocument,
                $"EgressPolicy = \"{policy.EgressPolicy}\"", $"EgressPolicy = \"{AiEgressPolicies.FullDocument}\"");

            if (forPurpose is null)
                Moot(AiReadinessCodes.AuthorizationUnstructured,
                    "not evaluated: no grant covering this purpose to test");
            else
            {
                var unstructured = forPurpose
                    .Where(x => x.UnstructuredDocumentsAllowed)
                    .OrderBy(x => x.Model == AiProviderEndpoint.AnyModel ? 1 : 0)
                    .ThenByDescending(x => x.AuthorizedOn)
                    .FirstOrDefault();
                if (unstructured is null)
                {
                    if (Blocked(AiReadinessCodes.AuthorizationUnstructured,
                            AiExternalProviderTrustReasons.UnstructuredNotAuthorized,
                            "UnstructuredDocumentsAllowed = false on every grant covering this purpose",
                            "UnstructuredDocumentsAllowed = true on the matching grant"))
                        return Result();
                }
                else
                {
                    Open(AiReadinessCodes.AuthorizationUnstructured,
                        "UnstructuredDocumentsAllowed = true", "UnstructuredDocumentsAllowed = true");
                    granted = unstructured;
                }
            }
        }
        else
        {
            Moot(AiReadinessCodes.EgressPolicyWholeDocument,
                "structured payload — no whole document egresses");
            Moot(AiReadinessCodes.AuthorizationUnstructured,
                "structured payload — no whole document egresses");
        }

        return Result();
    }

    private static string DescribeModel(string model) =>
        model.Length == 0 ? "(unset)" : model;

    private static string Utc(DateTime? value) =>
        value is { } moment ? moment.ToString("u") : "(unknown)";

    // ---- administration --------------------------------------------------

    public async Task<AiExternalProviderTrustView> GetAsync(long businessUnitId, CancellationToken ct)
    {
        PlatformGovernanceService.EnsureTenant(businessUnitId);
        var rows = await _db.AiExternalProviderAuthorizations.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId)
            .OrderByDescending(x => x.AuthorizedOn).ThenByDescending(x => x.Id)
            .ToListAsync(ct);

        var decision = await EvaluateAsync(
            businessUnitId, ResolvedProvider, AiPurposes.RfqExtraction, unstructuredPayload: true, ct);

        var now = DateTime.UtcNow;
        return new(ResolvedProvider, decision.Allowed, decision.Reason,
            rows.Select(x => Map(x, now)).ToList());
    }

    internal async Task<AiExternalProviderMutationResult> AuthorizeAsync(
        long businessUnitId, long actorUserId, string idempotencyKey,
        AuthorizeAiExternalProviderCommand command, CancellationToken ct,
        Func<AiExternalProviderAuthorization, CancellationToken, Task>? onAuthorized = null)
    {
        PlatformGovernanceService.EnsureActor(businessUnitId, actorUserId);
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160, "Idempotency-Key is required.");
        var (provider, endpoint, model, purposes, justification) = Validate(command);

        return await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var replay = await ReplayAsync(businessUnitId, idempotencyKey, ct);
            if (replay is not null)
                return new AiExternalProviderMutationResult(
                    Map(await RequireAsync(businessUnitId, long.Parse(replay.AggregateReference), ct), DateTime.UtcNow),
                    true);

            var now = DateTime.UtcNow;
            var existing = await _db.AiExternalProviderAuthorizations.SingleOrDefaultAsync(
                x => x.BusinessUnitId == businessUnitId && x.Provider == provider
                     && x.Endpoint == endpoint && x.Model == model, ct);

            AiExternalProviderAuthorization row;
            if (existing is null)
            {
                row = new AiExternalProviderAuthorization
                {
                    BusinessUnitId = businessUnitId,
                    Provider = provider,
                    Endpoint = endpoint,
                    Model = model,
                    AllowedPurposes = purposes,
                    UnstructuredDocumentsAllowed = command.UnstructuredDocumentsAllowed,
                    Justification = justification,
                    AuthorizedByUserId = actorUserId,
                    AuthorizedBy = $"user:{actorUserId}",
                    AuthorizedOn = now,
                    ExpiresOn = command.ExpiresOn,
                    Version = 1,
                    UpdatedOn = now
                };
                _db.AiExternalProviderAuthorizations.Add(row);
            }
            else
            {
                // Re-authorizing an endpoint (including un-revoking one) is itself an
                // attributed act: the actor, timestamp and justification are replaced and
                // the audit trail keeps the previous state.
                row = existing;
                row.AllowedPurposes = purposes;
                row.UnstructuredDocumentsAllowed = command.UnstructuredDocumentsAllowed;
                row.Justification = justification;
                row.AuthorizedByUserId = actorUserId;
                row.AuthorizedBy = $"user:{actorUserId}";
                row.AuthorizedOn = now;
                row.ExpiresOn = command.ExpiresOn;
                row.RevokedOn = null;
                row.RevokedBy = null;
                row.RevokedByUserId = null;
                row.RevocationReason = null;
                row.Version++;
                row.UpdatedOn = now;
            }

            await _db.SaveChangesAsync(ct);
            AddAudit(businessUnitId, actorUserId, idempotencyKey, AuthorizedAction, justification, row, now);
            await _db.SaveChangesAsync(ct);
            if (onAuthorized is not null)
                await onAuthorized(row, ct);
            await tx.CommitAsync(ct);

            _log.LogWarning(
                "External AI provider AUTHORIZED for tenant {Tenant} by {Actor}: {Provider} {Endpoint} model={Model} " +
                "purposes={Purposes} unstructured={Unstructured} expires={Expires}.",
                businessUnitId, row.AuthorizedBy, row.Provider, row.Endpoint, row.Model,
                row.AllowedPurposes, row.UnstructuredDocumentsAllowed, row.ExpiresOn);

            return new AiExternalProviderMutationResult(Map(row, now), false);
        });
    }

    internal async Task<AiExternalProviderMutationResult> RevokeAsync(
        long businessUnitId, long actorUserId, string idempotencyKey,
        RevokeAiExternalProviderCommand command, CancellationToken ct,
        Func<AiExternalProviderAuthorization, CancellationToken, Task>? onRevoked = null)
    {
        PlatformGovernanceService.EnsureActor(businessUnitId, actorUserId);
        idempotencyKey = PlatformGovernanceService.Required(idempotencyKey, 160, "Idempotency-Key is required.");
        var reason = PlatformGovernanceService.Required(command.Reason, 2000, "A revocation reason is required.");

        return await _db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            _db.ChangeTracker.Clear();
            await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);

            var replay = await ReplayAsync(businessUnitId, idempotencyKey, ct);
            if (replay is not null)
                return new AiExternalProviderMutationResult(
                    Map(await RequireAsync(businessUnitId, long.Parse(replay.AggregateReference), ct), DateTime.UtcNow),
                    true);

            var row = await RequireAsync(businessUnitId, command.AuthorizationId, ct);
            var now = DateTime.UtcNow;
            row.RevokedOn = now;
            row.RevokedByUserId = actorUserId;
            row.RevokedBy = $"user:{actorUserId}";
            row.RevocationReason = reason;
            row.Version++;
            row.UpdatedOn = now;

            AddAudit(businessUnitId, actorUserId, idempotencyKey, RevokedAction, reason, row, now);
            await _db.SaveChangesAsync(ct);
            if (onRevoked is not null)
                await onRevoked(row, ct);
            await tx.CommitAsync(ct);

            _log.LogWarning(
                "External AI provider REVOKED for tenant {Tenant} by {Actor}: {Provider} {Endpoint} model={Model}.",
                businessUnitId, row.RevokedBy, row.Provider, row.Endpoint, row.Model);

            return new AiExternalProviderMutationResult(Map(row, now), false);
        });
    }

    // ---- helpers ---------------------------------------------------------

    private async Task<AiExternalProviderAuthorization> RequireAsync(
        long businessUnitId, long authorizationId, CancellationToken ct) =>
        await _db.AiExternalProviderAuthorizations.SingleOrDefaultAsync(
            x => x.BusinessUnitId == businessUnitId && x.Id == authorizationId, ct)
        ?? throw new PlatformGovernanceNotFoundException("The external provider authorization was not found.");

    /// <summary>
    /// Idempotent replay: the same Idempotency-Key returns the same outcome instead of
    /// creating a second authorization. A key already spent on a DIFFERENT governance
    /// aggregate is a client bug, and is reported as a conflict rather than silently
    /// resolved against an unrelated row.
    /// </summary>
    private async Task<TenantGovernanceAuditEvent?> ReplayAsync(
        long businessUnitId, string key, CancellationToken ct)
    {
        var existing = await _db.TenantGovernanceAuditEvents.AsNoTracking().SingleOrDefaultAsync(
            x => x.BusinessUnitId == businessUnitId && x.IdempotencyKey == key, ct);
        if (existing is null) return null;
        if (!string.Equals(existing.AggregateType, AuditAggregateType, StringComparison.Ordinal))
            throw new PlatformGovernanceConflictException(
                "This Idempotency-Key has already been used for a different governance change.");
        return existing;
    }

    private void AddAudit(
        long businessUnitId, long actorUserId, string key, string action, string reason,
        AiExternalProviderAuthorization row, DateTime now) =>
        _db.TenantGovernanceAuditEvents.Add(new TenantGovernanceAuditEvent
        {
            BusinessUnitId = businessUnitId,
            Area = AuditArea,
            AggregateType = AuditAggregateType,
            AggregateReference = row.Id.ToString(),
            Action = action,
            Reason = reason,
            EvidenceJson = JsonSerializer.Serialize(Map(row, now)),
            IdempotencyKey = key,
            ActorUserId = actorUserId,
            OccurredOn = now
        });

    private static (string Provider, string Endpoint, string Model, string Purposes, string Justification)
        Validate(AuthorizeAiExternalProviderCommand command)
    {
        var provider = PlatformGovernanceService.Required(
            command.Provider, AiProviderEndpoint.MaxProviderLength, "A provider name is required.");

        if (!AiProviderEndpoint.TryNormalize(command.Endpoint, out var endpoint, out var reason))
            throw new PlatformGovernanceValidationException(
                $"The endpoint could not be accepted ({reason}). Supply an absolute http/https URL without credentials.");

        // Authorizing a loopback origin is meaningless (loopback is already Local and
        // never gated) and would create a misleading audit record. The test is the RESOLVED
        // class, not the reason TryNormalize reports: a name that merely looks like loopback
        // but answers with an off-box address is genuinely External, and refusing to
        // authorize it would leave the deployment unable to name the destination it is
        // actually calling.
        if (AiProviderEndpoint.Describe(provider, endpoint, command.Model).ProviderClass
            == AiProviderClass.Local)
            throw new PlatformGovernanceValidationException(
                "Loopback endpoints are local processing and do not require an external-provider authorization.");

        var model = AiProviderEndpoint.NormalizeModel(command.Model);
        if (model.Length == 0) model = AiProviderEndpoint.AnyModel;
        if (model.Length > AiProviderEndpoint.MaxModelLength)
            throw new PlatformGovernanceValidationException("The model identifier is too long.");

        var purposes = PlatformGovernanceService.Required(
            command.AllowedPurposes, 500, "At least one AI purpose must be authorized.");
        var parsed = purposes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (parsed.Length == 0)
            throw new PlatformGovernanceValidationException("At least one AI purpose must be authorized.");
        var known = new[] { AiPurposes.RfqExtraction, AiPurposes.BoqDraft, AiPurposes.Agent };
        var unknown = parsed.Where(p => !known.Contains(p, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (unknown.Length > 0)
            throw new PlatformGovernanceValidationException(
                $"Unknown AI purpose(s): {string.Join(", ", unknown)}. Valid purposes: {string.Join(", ", known)}.");

        // An authorization is a governance record. It is worthless without a reason, so
        // the reason is mandatory rather than an optional note.
        var justification = PlatformGovernanceService.Required(
            command.Justification, 2000,
            "A written justification (approval reference, DPA reference or ticket) is required.");

        if (command.ExpiresOn is { } expiry && expiry <= DateTime.UtcNow)
            throw new PlatformGovernanceValidationException("The expiry must be in the future.");

        return (provider, endpoint, model, string.Join(",", parsed), justification);
    }

    private static AiExternalProviderAuthorizationView Map(AiExternalProviderAuthorization x, DateTime now) =>
        new(x.Id, x.Provider, x.Endpoint, x.Model, x.AllowedPurposes, x.UnstructuredDocumentsAllowed,
            x.Justification, x.AuthorizedByUserId, x.AuthorizedBy, x.AuthorizedOn, x.ExpiresOn,
            x.RevokedOn, x.RevokedBy, x.RevocationReason, x.IsActive(now), x.Version, x.UpdatedOn);
}
