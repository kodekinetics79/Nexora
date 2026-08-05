using System;

namespace ERP_RFQ_Automation.Models;

/// <summary>
/// Append-only identity-and-access audit trail (RC-7).
///
/// Before this table there was NO audit write anywhere in <c>UserController</c>,
/// <c>RolePermissionController</c>, <c>SetupMasterController</c> or their repositories, and the
/// only provenance recorded was <c>User.Identity?.Name ?? request.CreatedBy ?? "System"</c> — with
/// <c>User.Identity.Name</c> always null (the tenant JWT carries sub/email/roleId/businessUnitId/jti
/// and never configures <c>NameClaimType</c>), so the CLIENT-SUPPLIED <c>CreatedBy</c> always won.
/// Attribution for every privilege change in the product was therefore forgeable by its subject.
///
/// Every field on this row is server-derived. <see cref="ActorUserId"/> / <see cref="ActorRoleId"/>
/// come from the validated token, never from a request body.
///
/// There is deliberately NO foreign key on <see cref="ActorUserId"/> or <see cref="TargetId"/>: the
/// record of "who deleted this user" must outlive the user it names. <see cref="BusinessUnitId"/>
/// does carry one, because a tenant-less audit row is unattributable and would sit outside the RLS
/// policy's reach.
/// </summary>
public class IamAuditEvent
{
    public long Id { get; set; }

    /// <summary>Tenant that owns the event. Stamped from the caller's <c>businessUnitId</c> claim.</summary>
    public long BusinessUnitId { get; set; }

    /// <summary>The authenticated user who performed the action; null only for system/migration writes.</summary>
    public long? ActorUserId { get; set; }

    /// <summary>The role the actor held AT THE TIME, so a later role rename cannot rewrite history.</summary>
    public long? ActorRoleId { get; set; }

    /// <summary>One of <see cref="ERP_RFQ_Automation.Authorization.IamAuditActions"/>.</summary>
    public string Action { get; set; } = null!;

    /// <summary>"User", "RolePermission", "Role".</summary>
    public string TargetType { get; set; } = null!;

    public long? TargetId { get; set; }

    /// <summary>Human-readable identity of the target captured at write time (email, role name).</summary>
    public string? TargetLabel { get; set; }

    public string? BeforeJson { get; set; }

    public string? AfterJson { get; set; }

    public string? Reason { get; set; }

    public string? CorrelationId { get; set; }

    public DateTime OccurredOn { get; set; }
}
