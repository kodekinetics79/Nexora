using Microsoft.AspNetCore.Authorization;

namespace ERP_RFQ_Automation.Authorization;

/// <summary>
/// Restricts a tenant-plane endpoint to the tenant's explicit Owner/SUPER_ADMIN tier.
///
/// <para>This is intentionally separate from a module permission. Roles at
/// <see cref="RoleRanks.Admin"/> satisfy every module permission by rank, but high-risk tenant
/// data governance belongs only to <see cref="RoleRanks.Owner"/>. The authority is read from the
/// stored role rank in the caller's tenant; the role's display name has no effect.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequireTenantOwnerRoleAttribute : AuthorizeAttribute
{
    public const string PolicyName = "RoleGate:TenantOwner";

    public RequireTenantOwnerRoleAttribute() => Policy = PolicyName;
}

/// <summary>Marker requirement for the tenant Owner/SUPER_ADMIN gate.</summary>
public sealed class TenantOwnerRoleRequirement : IAuthorizationRequirement
{
}
