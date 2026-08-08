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
/// (re-checked here as well as in the ledger), the purpose/provider/model policy denials,
/// the reserve/attempt/settle token ledger and its monthly + per-document budgets, the
/// external-dependency ceiling, PII redaction before egress, the prompt-injection nonce
/// boundary, strict-JSON-or-fail parsing and line-item count conservation.</para>
/// </summary>
public sealed class AiExternalProviderTrustService : IAiExternalProviderTrust
{
    private const string AuditArea = "AITrust";
    private const string AuditAggregateType = "AiExternalProviderAuthorization";
    private const string AuthorizedAction = "EXTERNAL_PROVIDER_AUTHORIZED";
    private const string RevokedAction = "EXTERNAL_PROVIDER_REVOKED";

    private readonly ErpRfqAutomationContext _db;
    private readonly ITenantContext _tenantContext;
    private readonly ILogger<AiExternalProviderTrustService> _log;

    public AiExternalProviderTrustService(
        ErpRfqAutomationContext db,
        ITenantContext tenantContext,
        IAiProviderEndpointResolver endpointResolver,
        ILogger<AiExternalProviderTrustService> log)
    {
        _db = db;
        _tenantContext = tenantContext;
        _log = log;
        ResolvedProvider = endpointResolver.Current;
    }

    public AiProviderDescriptor ResolvedProvider { get; }

    // ---- enforcement -----------------------------------------------------

    public async Task<AiExternalProviderDecision> EvaluateAsync(
        long businessUnitId, AiProviderDescriptor provider, string purpose,
        bool unstructuredPayload, CancellationToken ct)
    {
        if (businessUnitId <= 0)
            return AiExternalProviderDecision.Deny(AiExternalProviderTrustReasons.TenantMismatch, provider);

        // A caller may never evaluate another tenant's allow-list. Mirrors
        // AiGovernanceService.EnsureTenant. A null ambient tenant (background worker
        // sweep before Push) is still safe: the query below is explicitly predicated on
        // businessUnitId in addition to the global query filter.
        if (_tenantContext.BusinessUnitId is { } scoped && scoped != businessUnitId)
        {
            _log.LogWarning(
                "External provider trust evaluation refused: tenant scope {ScopedTenant} does not match requested {RequestedTenant}.",
                scoped, businessUnitId);
            return AiExternalProviderDecision.Deny(AiExternalProviderTrustReasons.TenantMismatch, provider);
        }

        if (!provider.IsResolved)
            return AiExternalProviderDecision.Deny(AiExternalProviderTrustReasons.EndpointUnresolved, provider);

        // The policy row remains authoritative. The allow-list narrows it; it can never
        // widen it, so a tenant whose secure default is still in force is refused here
        // before a single byte of document text is prepared for egress.
        var policy = await _db.AiProcessingPolicies.AsNoTracking()
            .SingleOrDefaultAsync(x => x.BusinessUnitId == businessUnitId, ct);
        if (policy is null)
            return AiExternalProviderDecision.Deny(AiExternalProviderTrustReasons.PolicyMissing, provider);
        if (!policy.IsEnabled)
            return AiExternalProviderDecision.Deny(AiExternalProviderTrustReasons.PolicyDisabled, provider);
        if (!policy.ExternalProcessingAllowed)
            return AiExternalProviderDecision.Deny(
                AiExternalProviderTrustReasons.PolicyExternalProcessingDenied, provider);

        var candidates = await _db.AiExternalProviderAuthorizations.AsNoTracking()
            .Where(x => x.BusinessUnitId == businessUnitId && x.Endpoint == provider.Endpoint)
            .ToListAsync(ct);

        var matching = candidates
            .Where(x => AiProviderEndpoint.ProviderMatches(x.Provider, provider.Provider)
                        && AiProviderEndpoint.EndpointMatches(x.Endpoint, provider.Endpoint)
                        && AiProviderEndpoint.ModelMatches(x.Model, provider.Model))
            .ToList();

        if (matching.Count == 0)
            return AiExternalProviderDecision.Deny(AiExternalProviderTrustReasons.NotAuthorized, provider);

        var now = DateTime.UtcNow;
        var live = matching.Where(x => x.IsActive(now)).ToList();
        if (live.Count == 0)
            return AiExternalProviderDecision.Deny(
                matching.Any(x => x.IsRevoked)
                    ? AiExternalProviderTrustReasons.Revoked
                    : AiExternalProviderTrustReasons.Expired,
                provider);

        var forPurpose = live.Where(x => x.CoversPurpose(purpose)).ToList();
        if (forPurpose.Count == 0)
            return AiExternalProviderDecision.Deny(
                AiExternalProviderTrustReasons.PurposeNotAuthorized, provider);

        // Prefer the most specific grant (an exact model beats a wildcard) so an operator
        // can hold a narrow model grant alongside a broad one without the broad one
        // silently deciding.
        var granted = forPurpose
            .OrderBy(x => x.Model == AiProviderEndpoint.AnyModel ? 1 : 0)
            .ThenByDescending(x => x.AuthorizedOn)
            .First();

        if (unstructuredPayload)
        {
            var unstructured = forPurpose
                .Where(x => x.UnstructuredDocumentsAllowed)
                .OrderBy(x => x.Model == AiProviderEndpoint.AnyModel ? 1 : 0)
                .ThenByDescending(x => x.AuthorizedOn)
                .FirstOrDefault();
            if (unstructured is null)
                return AiExternalProviderDecision.Deny(
                    AiExternalProviderTrustReasons.UnstructuredNotAuthorized, provider);
            granted = unstructured;
        }

        return AiExternalProviderDecision.Allow(granted.Id, provider);
    }

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
        // never gated) and would create a misleading audit record.
        if (reason == AiProviderEndpointReasons.LoopbackEndpoint)
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
