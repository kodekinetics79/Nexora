using ERP_RFQ_Automation.Platform.Models;

namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// P2-A10: the ONE problem+json type namespace for the whole product. Every
/// hand-rolled or typed problem "type" URI must be <see cref="Base"/> + a slug —
/// never nexora.local, never a second base host.
/// </summary>
public static class NexoraProblems
{
    public const string Base = "https://nexora.invalid/problems/";

    public const string TenantSuspended = Base + "tenant-suspended";
    public const string TenantAccessUnresolvable = Base + "tenant-access-unresolvable";
    public const string TenantNotActivated = Base + "tenant-not-activated";
    public const string FeatureNotEntitled = Base + "feature-not-entitled";
    public const string DocumentQuotaExceeded = Base + "document-quota-exceeded";
    public const string SeatLimitExceeded = Base + "seat-limit-exceeded";
    public const string ReadOnlyImpersonation = Base + "read-only-impersonation";
    public const string ImpersonationSessionRevoked = Base + "impersonation-session-revoked";
    public const string ImpersonationExportDenied = Base + "impersonation-export-denied";

    /// <summary>
    /// Sec-D3: a read refused because the route is not on the impersonation allow-list. Replaces
    /// <see cref="ImpersonationExportDenied"/> as the answer for every refused impersonated read —
    /// the old type named exports specifically, and the refusal is now much broader than exports,
    /// so reusing it would misdescribe the denial to whoever is reading the response.
    /// </summary>
    public const string ImpersonationReadDenied = Base + "impersonation-read-denied";
}

/// <summary>
/// Base class for typed enforcement denials, so call paths deep below the API layer
/// (queue enqueue, login) can signal a clean, mappable outcome instead of a generic
/// 500. <see cref="SuggestedStatusCode"/> and <see cref="ProblemType"/> feed the
/// problem+json rendering at the API boundary.
/// </summary>
public abstract class EntitlementDeniedException : Exception
{
    protected EntitlementDeniedException(string message, int suggestedStatusCode, string problemType)
        : base(message)
    {
        SuggestedStatusCode = suggestedStatusCode;
        ProblemType = problemType;
    }

    public int SuggestedStatusCode { get; }

    public string ProblemType { get; }
}

/// <summary>A product execution boundary denied because its typed feature is unavailable.</summary>
public sealed class FeatureEntitlementDeniedException : EntitlementDeniedException
{
    public FeatureEntitlementDeniedException(long businessUnitId, string entitlement, EntitlementDecision decision)
        : base(decision.Reason ?? $"Entitlement '{entitlement}' is not available for this organization.",
            StatusCodes.Status403Forbidden, NexoraProblems.FeatureNotEntitled)
    {
        BusinessUnitId = businessUnitId;
        Entitlement = TypedEntitlementCatalog.RequireKnown(entitlement);
        Decision = decision;
    }

    public long BusinessUnitId { get; }
    public string Entitlement { get; }
    public EntitlementDecision Decision { get; }
}

/// <summary>
/// Login / tenant-plane access denied because the owning platform Tenant has not been activated
/// or is commercially/lifecycle restricted.
/// </summary>
public sealed class TenantAccessDeniedException : EntitlementDeniedException
{
    public const string Type = NexoraProblems.TenantSuspended;

    public TenantAccessDeniedException(long businessUnitId, TenantStatus? status)
        : base(status switch
            {
                TenantStatus.Provisioning =>
                    "This organization's workspace is still provisioning and has not passed authoritative activation. Contact your administrator.",
                TenantStatus.PastDue =>
                    "This organization's access is restricted because the account is past due. Contact your administrator.",
                TenantStatus.Archived =>
                    "This organization's access has been archived. Contact your administrator.",
                _ => "This organization's access has been suspended. Contact your administrator."
            },
            StatusCodes.Status403Forbidden,
            status == TenantStatus.Provisioning ? NexoraProblems.TenantNotActivated : Type)
    {
        BusinessUnitId = businessUnitId;
        Status = status;
    }

    public long BusinessUnitId { get; }

    public TenantStatus? Status { get; }
}

/// <summary>
/// Sec-D1: the platform control plane could not be read, so this business unit's tenant status
/// and plan are UNKNOWN and the request is refused.
///
/// <para>503 rather than 403, deliberately. 403 would tell an operator "this customer is
/// suspended", which is a claim we cannot make — nothing was read. 503 says what is true: the
/// service cannot answer right now. It is also the only status that makes the outage visible as
/// an outage on the caller's side and in every uptime monitor, which is what a whole-deployment
/// missing grant deserves. <c>Retry-After</c> is the tenant-access failure cache TTL, because that
/// is exactly how long the refusal will be remembered before the plane is tried again.</para>
/// </summary>
public sealed class TenantAccessUnresolvableException : EntitlementDeniedException
{
    public const string Type = NexoraProblems.TenantAccessUnresolvable;

    public TenantAccessUnresolvableException(long businessUnitId, string? reason = null)
        : base(reason ?? "This organization's subscription status could not be confirmed. Please try again shortly.",
            StatusCodes.Status503ServiceUnavailable, Type)
        => BusinessUnitId = businessUnitId;

    public long BusinessUnitId { get; }

    /// <summary>Seconds a caller should wait — the same window the refusal is cached for.</summary>
    public static int RetryAfterSeconds => (int)Math.Ceiling(TenantAccessService.FailureCacheTtl.TotalSeconds);
}

/// <summary>Document enqueue denied because the plan's monthly document quota is exhausted.</summary>
public sealed class DocumentQuotaExceededException : EntitlementDeniedException
{
    public const string Type = NexoraProblems.DocumentQuotaExceeded;

    public DocumentQuotaExceededException(long businessUnitId, EntitlementDecision decision)
        : base(decision.Reason ?? "The monthly document quota for this organization has been reached.",
            StatusCodes.Status429TooManyRequests, Type)
    {
        BusinessUnitId = businessUnitId;
        Decision = decision;
    }

    public long BusinessUnitId { get; }

    public EntitlementDecision Decision { get; }
}

/// <summary>
/// User create/reactivate denied because the plan's seat limit is reached (P2-A10:
/// previously an inline hand-rolled problem object in UserController; now typed so
/// <see cref="EntitlementProblemFilter"/> renders the single canonical shape).
/// </summary>
public sealed class SeatLimitExceededException : EntitlementDeniedException
{
    public const string Type = NexoraProblems.SeatLimitExceeded;

    public SeatLimitExceededException(long businessUnitId, EntitlementDecision decision)
        : base(decision.Reason ?? "The seat limit for this organization's plan has been reached.",
            StatusCodes.Status403Forbidden, Type)
    {
        BusinessUnitId = businessUnitId;
        Decision = decision;
    }

    public long BusinessUnitId { get; }

    public EntitlementDecision Decision { get; }
}
