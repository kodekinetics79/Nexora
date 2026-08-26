using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Authorization;

/// <summary>
/// Enforces <see cref="RequireTenantOwnerRoleAttribute"/>. Missing, malformed, inactive or
/// cross-tenant role claims fail closed because <see cref="IRoleGate"/> resolves authority only
/// from an active role row in the claimed business unit.
/// </summary>
public sealed class TenantOwnerRoleHandler(
    IRoleGate roleGate,
    ILogger<TenantOwnerRoleHandler> logger)
    : AuthorizationHandler<TenantOwnerRoleRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TenantOwnerRoleRequirement requirement)
    {
        var roleIdClaim = context.User.FindFirst("roleId")?.Value;
        var businessUnitIdClaim = context.User.FindFirst("businessUnitId")?.Value;
        if (!long.TryParse(roleIdClaim, out var roleId)
            || !long.TryParse(businessUnitIdClaim, out var businessUnitId)
            || businessUnitId <= 0)
        {
            return;
        }

        if (!await roleGate.IsSuperAdminAsync(roleId, businessUnitId))
        {
            return;
        }

        logger.LogDebug(
            "Tenant-owner gate satisfied for role {RoleId} in business unit {BusinessUnitId}.",
            roleId,
            businessUnitId);
        context.Succeed(requirement);
    }
}
