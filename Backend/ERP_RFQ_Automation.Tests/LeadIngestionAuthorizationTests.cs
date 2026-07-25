using ERP_RFQ_Automation.Authorization;
using ERP_RFQ_Automation.Controllers;

namespace ERP_RFQ_Automation.Tests;

public sealed class LeadIngestionAuthorizationTests
{
    [Theory]
    [InlineData(nameof(LeadIngestionController.Batch), "Leads", "View")]
    [InlineData(nameof(LeadIngestionController.Revisions), "Leads", "View")]
    [InlineData(nameof(LeadIngestionController.Decide), "Leads", "Edit")]
    [InlineData(nameof(LeadIngestionController.Analytics), "Dashboard", "View")]
    public void Endpoints_require_module_permission(string method, string module, string action)
    {
        var target = typeof(LeadIngestionController).GetMethods().Single(x => x.Name == method);
        var attribute = Assert.Single(target.GetCustomAttributes(typeof(RequireModulePermissionAttribute), true).Cast<RequireModulePermissionAttribute>());
        Assert.Contains(module, attribute.Policy, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(action, attribute.Policy, StringComparison.OrdinalIgnoreCase);
    }
}
