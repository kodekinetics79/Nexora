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
    public const string TenantNotActivated = Base + "tenant-not-activated";
    public const string FeatureNotEntitled = Base + "feature-not-entitled";
    public const string DocumentQuotaExceeded = Base + "document-quota-exceeded";
    public const string SeatLimitExceeded = Base + "seat-limit-exceeded";
    public const string ReadOnlyImpersonation = Base + "read-only-impersonation";
    public const string ImpersonationSessionRevoked = Base + "impersonation-session-revoked";
    public const string ImpersonationExportDenied = Base + "impersonation-export-denied";
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
