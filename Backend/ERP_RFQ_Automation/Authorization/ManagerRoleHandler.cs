using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging;

namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>
    /// Enforces <see cref="RequireManagerRoleAttribute"/>: caller's roleId claim must
    /// resolve to a role whose name contains admin/manager. Super-admin roles always
    /// pass. Missing/unparseable claims or unknown roles → not succeeded (fail-closed,
    /// surfaces as 403).
    /// </summary>
    public class ManagerRoleHandler : AuthorizationHandler<ManagerRoleRequirement>
    {
        private readonly IRoleGate _roleGate;
        private readonly ILogger<ManagerRoleHandler> _logger;

        public ManagerRoleHandler(IRoleGate roleGate, ILogger<ManagerRoleHandler> logger)
        {
            _roleGate = roleGate;
            _logger = logger;
        }

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ManagerRoleRequirement requirement)
        {
            var roleIdClaim = context.User.FindFirst("roleId")?.Value;
            var businessUnitIdClaim = context.User.FindFirst("businessUnitId")?.Value;
            if (!long.TryParse(roleIdClaim, out var roleId) ||
                !long.TryParse(businessUnitIdClaim, out var businessUnitId) || businessUnitId <= 0)
                return;

            if (await _roleGate.IsSuperAdminAsync(roleId, businessUnitId))
            {
                _logger.LogDebug("Manager gate bypassed for super-admin role {RoleId}.", roleId);
                context.Succeed(requirement);
                return;
            }

            if (await _roleGate.IsManagerOrAdminAsync(roleId, businessUnitId))
                context.Succeed(requirement);
        }
    }
}
