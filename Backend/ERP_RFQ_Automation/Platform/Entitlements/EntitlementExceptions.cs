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

/// <summary>Login / tenant-plane access denied because the owning platform Tenant is Suspended or Archived.</summary>
public sealed class TenantAccessDeniedException : EntitlementDeniedException
{
    public const string Type = NexoraProblems.TenantSuspended;

    public TenantAccessDeniedException(long businessUnitId, TenantStatus? status)
        : base("This organization's access has been " +
               (status == TenantStatus.Archived ? "archived" : "suspended") +
               ". Contact your administrator.",
            StatusCodes.Status403Forbidden, Type)
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
