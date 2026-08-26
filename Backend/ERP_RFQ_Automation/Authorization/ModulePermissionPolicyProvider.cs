using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>
    /// Resolves the dynamic policy names produced by
    /// <see cref="RequireModulePermissionAttribute"/> ("ModulePermission:{module}:{action}")
    /// <see cref="RequireManagerRoleAttribute"/> ("RoleGate:ManagerOrAdmin"), and
    /// <see cref="RequireTenantOwnerRoleAttribute"/> ("RoleGate:TenantOwner") without
    /// any per-policy registration. Every other policy name (the legacy CanCreateRFQ /
    /// CanEditQuotation policies and the Platform-plane policies) falls through to the
    /// default provider, so existing behavior is unchanged.
    /// </summary>
    public sealed class ModulePermissionPolicyProvider : IAuthorizationPolicyProvider
    {
        private readonly DefaultAuthorizationPolicyProvider _fallback;

        public ModulePermissionPolicyProvider(IOptions<AuthorizationOptions> options)
            => _fallback = new DefaultAuthorizationPolicyProvider(options);

        public Task<AuthorizationPolicy> GetDefaultPolicyAsync() => _fallback.GetDefaultPolicyAsync();

        public Task<AuthorizationPolicy?> GetFallbackPolicyAsync() => _fallback.GetFallbackPolicyAsync();

        public Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
        {
            if (RequireModulePermissionAttribute.TryParsePolicy(policyName, out var moduleName, out var action))
            {
                var policy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new PermissionRequirement(moduleName, ToRepositoryAction(action)))
                    .Build();
                return Task.FromResult<AuthorizationPolicy?>(policy);
            }

            if (string.Equals(policyName, RequireManagerRoleAttribute.PolicyName, StringComparison.Ordinal))
            {
                var policy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ManagerRoleRequirement())
                    .Build();
                return Task.FromResult<AuthorizationPolicy?>(policy);
            }

            if (string.Equals(policyName, RequireTenantOwnerRoleAttribute.PolicyName, StringComparison.Ordinal))
            {
                var policy = new AuthorizationPolicyBuilder()
                    .RequireAuthenticatedUser()
                    .AddRequirements(new TenantOwnerRoleRequirement())
                    .Build();
                return Task.FromResult<AuthorizationPolicy?>(policy);
            }

            return _fallback.GetPolicyAsync(policyName);
        }

        /// <summary>
        /// Maps the enum onto the action strings RolePermissionRepository.CheckPermissionAsync
        /// already understands ("CanView" is the additive row-exists check).
        /// </summary>
        public static string ToRepositoryAction(PermissionAction action) => action switch
        {
            PermissionAction.View => "CanView",
            PermissionAction.Create => "CanCreate",
            PermissionAction.Edit => "CanEdit",
            PermissionAction.Delete => "CanDelete",
            _ => "Unknown" // fail-closed: repository denies unrecognized actions
        };
    }
}
