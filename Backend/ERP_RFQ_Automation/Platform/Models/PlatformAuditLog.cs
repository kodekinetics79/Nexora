namespace ERP_RFQ_Automation.Platform.Models;

/// <summary>
/// Append-only audit trail for every privileged platform-plane action. Never
/// updated or deleted by application code (WIRING.md notes the append-only
/// expectation and DB-level enforcement). Impersonation records BOTH actors:
/// <see cref="ActorPlatformUserId"/> (who acted) and <see cref="ActAsTenantId"/>
/// (whose tenant was acted on/into). (ADR-0005 §3, §4)
/// </summary>
public class PlatformAuditLog
{
    public long Id { get; set; }

    /// <summary>The platform operator who performed the action.</summary>
    public long ActorPlatformUserId { get; set; }

    /// <summary>Tenant acted upon / impersonated into, when applicable.</summary>
    public long? ActAsTenantId { get; set; }

    /// <summary>Verb, e.g. "tenant.provision", "tenant.suspend", "impersonate.issue".</summary>
    public string Action { get; set; } = null!;

    /// <summary>Type of the target, e.g. "Tenant", "PlatformUser".</summary>
    public string? TargetType { get; set; }

    /// <summary>Identifier of the target entity.</summary>
    public string? TargetId { get; set; }

    /// <summary>Free-form JSON context (reason, before/after, request args).</summary>
    public string? Metadata { get; set; }

    public string? Ip { get; set; }

    /// <summary>
    /// Outcome of the audited action: <see cref="PlatformAuditResults.Success"/> or
    /// <see cref="PlatformAuditResults.Failure"/>. Defaults to success so legacy writers
    /// and pre-existing rows stay semantically unchanged.
    /// </summary>
    public string Result { get; set; } = PlatformAuditResults.Success;

    public DateTime CreatedOn { get; set; } = DateTime.UtcNow;
}

/// <summary>Canonical values for <see cref="PlatformAuditLog.Result"/> (max length 16).</summary>
public static class PlatformAuditResults
{
    public const string Success = "success";
    public const string Failure = "failure";
}
