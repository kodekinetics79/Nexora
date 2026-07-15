using Microsoft.AspNetCore.Authorization;

namespace ERP_RFQ_Automation.Authorization
{
    public class PermissionRequirement : IAuthorizationRequirement
    {
        public string ModuleName { get; }
        public string Action { get; }

        public PermissionRequirement(string moduleName, string action)
        {
            ModuleName = moduleName;
            Action = action;
        }
    }
}
