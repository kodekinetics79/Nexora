using ERP_RFQ_Automation.Interfaces;
using Microsoft.AspNetCore.Authorization;
using System.Security.Claims;

namespace ERP_RFQ_Automation.Authorization
{
    public class PermissionHandler : AuthorizationHandler<PermissionRequirement>
    {
        private readonly IRolePermissionRepository _repository;

        public PermissionHandler(IRolePermissionRepository repository)
        {
            _repository = repository;
        }

        protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
        {
            var roleIdClaim = context.User.FindFirst("roleId")?.Value;
            var buIdClaim = context.User.FindFirst("businessUnitId")?.Value;

            if (string.IsNullOrEmpty(roleIdClaim) || string.IsNullOrEmpty(buIdClaim))
            {
                return;
            }

            if (!long.TryParse(roleIdClaim, out long roleId) || !long.TryParse(buIdClaim, out long businessUnitId))
            {
                return;
            }

            // check if the user has the permission in the repository
            var hasPermission = await _repository.CheckPermissionAsync(roleId, requirement.ModuleName, requirement.Action, businessUnitId);

            if (hasPermission)
            {
                context.Succeed(requirement);
            }
        }
    }
}
