namespace ERP_RFQ_Automation.AI;

/// <summary>
/// Machine-readable outcomes of an external-provider allow-list evaluation. Every one of
/// these is a REFUSAL reason except <see cref="Authorized"/>: the gate is a whitelist, so
/// the absence of a matching row is a denial, never a default-allow.
/// </summary>
public static class AiExternalProviderTrustReasons
{
    public const string Authorized = "external_endpoint_authorized";

    /// <summary>No allow-list row for this tenant matches this provider/endpoint/model.</summary>
    public const string NotAuthorized = "external_endpoint_not_authorized";

    /// <summary>The deployment's endpoint could not be normalised, so nothing can match it.</summary>
    public const string EndpointUnresolved = "external_endpoint_unresolved";

    /// <summary>A matching row exists but has been revoked.</summary>
    public const string Revoked = "external_authorization_revoked";

    /// <summary>A matching row exists but its authorization window has ended.</summary>
    public const string Expired = "external_authorization_expired";

    /// <summary>A matching, live row exists but does not cover this AI purpose.</summary>
    public const string PurposeNotAuthorized = "external_authorization_purpose_denied";

    /// <summary>A matching, live row exists but does not cover unstructured documents.</summary>
    public const string UnstructuredNotAuthorized = "external_authorization_unstructured_denied";

    /// <summary>The tenant has no AI processing policy row at all.</summary>
    public const string PolicyMissing = "policy_missing";

    /// <summary>The tenant's AI processing policy is switched off.</summary>
    public const string PolicyDisabled = "policy_disabled";

    /// <summary>The tenant's policy still forbids external processing (secure default).</summary>
    public const string PolicyExternalProcessingDenied = "external_processing_denied";

    /// <summary>The caller's tenant scope does not match the requested business unit.</summary>
    public const string TenantMismatch = "external_trust_tenant_mismatch";

    /// <summary>The gate itself was not wired up. Fails closed, never open.</summary>
    public const string GateUnavailable = "external_trust_gate_unavailable";
}

/// <summary>
/// A tenant's explicit, attributed authorization for ONE external inference endpoint.
///
/// <para>
/// This is the whole point of the module: the previous control could only say "never"
/// (all external processing of unstructured documents refused) or, had the refusal been
/// deleted, "always" (any endpoint an environment variable happened to name). Neither is
/// a governance position a customer can audit. A row here says: <i>this named tenant
/// authorized this exact origin, for this model, for these purposes, including/excluding
/// unstructured documents, on this date, by this user, for this stated reason, until this
/// date.</i> Absence of a row is a refusal.
/// </para>
///
/// <para>
/// The allow-list decides only WHETHER a destination may be used. Every other control —
/// <see cref="AiProcessingPolicy.ExternalProcessingAllowed"/>, purpose/provider/model
/// policy denial, the reserve/attempt/settle token ledger and its budgets, the per-document
/// token cap, PII redaction before egress, the prompt-injection nonce boundary,
/// strict-JSON-or-fail parsing and line-item count conservation — still runs unchanged
/// afterwards. An authorization is a narrower door, not a bypass.
/// </para>
/// </summary>
public sealed class AiExternalProviderAuthorization
{
    public long Id { get; set; }

    /// <summary>Owning tenant. Enforced by an EF global query filter AND a Postgres RLS policy.</summary>
    public long BusinessUnitId { get; set; }

    /// <summary>Provider label, e.g. <c>Ollama</c>. Matched case-insensitively.</summary>
    public string Provider { get; set; } = null!;

    /// <summary>Normalised origin (<c>scheme://host[:port]</c>) — see <see cref="AiProviderEndpoint"/>.</summary>
    public string Endpoint { get; set; } = null!;

    /// <summary>Exact model id, or <see cref="AiProviderEndpoint.AnyModel"/> for every model at this endpoint.</summary>
    public string Model { get; set; } = AiProviderEndpoint.AnyModel;

    /// <summary>CSV of <see cref="AiPurposes"/> this authorization covers.</summary>
    public string AllowedPurposes { get; set; } = AiPurposes.RfqExtraction;

    /// <summary>
    /// The high-risk switch, deliberately separate from the purpose list: whole
    /// unstructured document text (a PDF/email body that has NOT been reduced to a
    /// redacted field/row payload) may egress to this endpoint. Defaults to false so an
    /// authorization created for a narrower job cannot silently widen to this.
    /// </summary>
    public bool UnstructuredDocumentsAllowed { get; set; }

    /// <summary>Why the tenant authorized it — DPA reference, ticket, approval note. Required.</summary>
    public string Justification { get; set; } = null!;

    public long AuthorizedByUserId { get; set; }

    /// <summary>Actor string in the codebase's existing <c>user:{id}</c> form.</summary>
    public string AuthorizedBy { get; set; } = null!;

    public DateTime AuthorizedOn { get; set; }

    /// <summary>Optional expiry. A lapsed authorization refuses exactly like a missing one.</summary>
    public DateTime? ExpiresOn { get; set; }

    public DateTime? RevokedOn { get; set; }
    public long? RevokedByUserId { get; set; }
    public string? RevokedBy { get; set; }
    public string? RevocationReason { get; set; }

    /// <summary>Optimistic concurrency token, matching the AI policy row's idiom.</summary>
    public long Version { get; set; } = 1;

    public DateTime UpdatedOn { get; set; }

    public bool IsRevoked => RevokedOn is not null;
    public bool IsExpired(DateTime now) => ExpiresOn is { } expiry && expiry <= now;
    public bool IsActive(DateTime now) => !IsRevoked && !IsExpired(now);

    public bool CoversPurpose(string purpose) =>
        AllowedPurposes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(purpose, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// The result of an allow-list evaluation. Always carries a reason code so the refusal (or
/// the approval) is legible in logs, diagnostics and the extraction outcome.
/// </summary>
public sealed record AiExternalProviderDecision(
    bool Allowed,
    string Reason,
    long? AuthorizationId = null,
    string? Endpoint = null,
    string? Model = null)
{
    public static AiExternalProviderDecision Deny(string reason, AiProviderDescriptor? provider = null) =>
        new(false, reason, null,
            provider is { IsResolved: true } ? provider.Endpoint : null,
            provider is not null && provider.Model.Length > 0 ? provider.Model : null);

    public static AiExternalProviderDecision Allow(long authorizationId, AiProviderDescriptor provider) =>
        new(true, AiExternalProviderTrustReasons.Authorized, authorizationId,
            provider.Endpoint, provider.Model);
}

/// <summary>Operator-facing projection of an authorization row.</summary>
public sealed record AiExternalProviderAuthorizationView(
    long Id, string Provider, string Endpoint, string Model, string AllowedPurposes,
    bool UnstructuredDocumentsAllowed, string Justification, long AuthorizedByUserId,
    string AuthorizedBy, DateTime AuthorizedOn, DateTime? ExpiresOn, DateTime? RevokedOn,
    string? RevokedBy, string? RevocationReason, bool IsActive, long Version, DateTime UpdatedOn);

/// <summary>
/// What the tenant is being asked to authorize, alongside what this deployment is
/// actually configured to call. Surfacing both together is what makes the decision
/// reviewable: an operator can see "you are about to authorize https://ollama.com, and
/// that is exactly what this deployment will call".
/// </summary>
public sealed record AiExternalProviderTrustView(
    AiProviderDescriptor ResolvedProvider,
    bool ResolvedProviderIsAuthorizedForUnstructured,
    string ResolvedProviderDecisionReason,
    IReadOnlyList<AiExternalProviderAuthorizationView> Authorizations);

/// <summary>
/// Grant command. <paramref name="Endpoint"/> is normalised server-side, so an operator
/// may paste the configured base URL verbatim and still get an exact-origin grant.
/// </summary>
public sealed record AuthorizeAiExternalProviderCommand(
    string Provider,
    string Endpoint,
    string? Model,
    string AllowedPurposes,
    bool UnstructuredDocumentsAllowed,
    string Justification,
    DateTime? ExpiresOn);

public sealed record RevokeAiExternalProviderCommand(long AuthorizationId, string Reason);

public sealed record AiExternalProviderMutationResult(
    AiExternalProviderAuthorizationView Authorization, bool IdempotentReplay);

/// <summary>
/// The allow-list. <see cref="EvaluateAsync"/> is the enforcement point; the remaining
/// members are the attributed, audited administration surface behind
/// <c>api/platform-governance/ai-trust/external-providers</c>.
/// </summary>
public interface IAiExternalProviderTrust
{
    /// <summary>The endpoint this process is configured to call, and why it is Local/External.</summary>
    AiProviderDescriptor ResolvedProvider { get; }

    /// <summary>
    /// Decides whether <paramref name="provider"/> may process work for
    /// <paramref name="businessUnitId"/>. Fails closed on every unexpected condition.
    /// </summary>
    Task<AiExternalProviderDecision> EvaluateAsync(
        long businessUnitId, AiProviderDescriptor provider, string purpose,
        bool unstructuredPayload, CancellationToken ct);

    Task<AiExternalProviderTrustView> GetAsync(long businessUnitId, CancellationToken ct);

    Task<AiExternalProviderMutationResult> AuthorizeAsync(
        long businessUnitId, long actorUserId, string idempotencyKey,
        AuthorizeAiExternalProviderCommand command, CancellationToken ct);

    Task<AiExternalProviderMutationResult> RevokeAsync(
        long businessUnitId, long actorUserId, string idempotencyKey,
        RevokeAiExternalProviderCommand command, CancellationToken ct);
}
