using Microsoft.AspNetCore.Authorization;

namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>
    /// Declarative module+action RBAC gate:
    /// <c>[RequireModulePermission("Leads", PermissionAction.Edit)]</c>.
    ///
    /// Implemented as an <see cref="AuthorizeAttribute"/> subclass with a dynamic policy
    /// name ("ModulePermission:{module}:{action}") resolved by
    /// <see cref="ModulePermissionPolicyProvider"/>, so NO per-policy registration in
    /// Program.cs is needed. The check itself runs through the existing
    /// <see cref="PermissionHandler"/> / RolePermissions query path.
    ///
    /// Module names MUST match the frontend PermissionGuard moduleName strings (which are
    /// the Modules.ModuleName rows): "Leads", "RFQ Management", "Quotations", "Orders",
    /// "Shipments", "Suppliers", "Supplier History", "Customers", "Products",
    /// "Product Categories", ...
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true)]
    public sealed class RequireModulePermissionAttribute : AuthorizeAttribute
    {
        public const string PolicyPrefix = "ModulePermission:";

        public RequireModulePermissionAttribute(string moduleName, PermissionAction action)
        {
            ModuleName = moduleName;
            Action = action;
            Policy = $"{PolicyPrefix}{moduleName}:{action}";
        }

        public string ModuleName { get; }

        public PermissionAction Action { get; }

        /// <summary>
        /// Parses a dynamic policy name back into (module, action). Module names may
        /// contain spaces and '&amp;' but never ':', so the last colon always separates
        /// the action. Returns false for names that don't use the prefix.
        /// </summary>
        public static bool TryParsePolicy(string? policyName, out string moduleName, out PermissionAction action)
        {
            moduleName = string.Empty;
            action = PermissionAction.View;

            if (policyName is null || !policyName.StartsWith(PolicyPrefix, StringComparison.Ordinal))
                return false;

            var lastColon = policyName.LastIndexOf(':');
            if (lastColon < PolicyPrefix.Length) // no module segment at all
                return false;

            moduleName = policyName[PolicyPrefix.Length..lastColon];
            var actionRaw = policyName[(lastColon + 1)..];

            return moduleName.Length > 0 && Enum.TryParse(actionRaw, ignoreCase: true, out action);
        }
    }
}
