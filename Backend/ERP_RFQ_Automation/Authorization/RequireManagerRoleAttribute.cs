using Microsoft.AspNetCore.Authorization;

namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>
    /// Restricts an endpoint to roles whose server-controlled <c>RoleRank</c> is Manager or higher.
    /// Human-readable role names never grant authority. Tenant owners always pass.
    /// Resolved dynamically by <see cref="ModulePermissionPolicyProvider"/> and enforced
    /// by <see cref="ManagerRoleHandler"/> via <see cref="IRoleGate"/>.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class RequireManagerRoleAttribute : AuthorizeAttribute
    {
        public const string PolicyName = "RoleGate:ManagerOrAdmin";

        public RequireManagerRoleAttribute() => Policy = PolicyName;
    }

    /// <summary>Marker requirement for the manager/admin role gate.</summary>
    public sealed class ManagerRoleRequirement : IAuthorizationRequirement
    {
    }
}
