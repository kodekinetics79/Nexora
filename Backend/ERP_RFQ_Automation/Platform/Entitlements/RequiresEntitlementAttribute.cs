using ERP_RFQ_Automation.MultiTenancy;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace ERP_RFQ_Automation.Platform.Entitlements;

/// <summary>
/// Declares and enforces a server-owned product capability at an HTTP boundary. The tenant is
/// always derived from the authenticated request scope; route and body tenant identifiers are
/// deliberately ignored.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public sealed class RequiresEntitlementAttribute(string key) : Attribute, IAsyncActionFilter
{
    public string Key { get; } = TypedEntitlementCatalog.RequireKnown(key);

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var tenant = context.HttpContext.RequestServices.GetRequiredService<ITenantContext>();
        if (tenant.BusinessUnitId is not long businessUnitId || businessUnitId <= 0)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Tenant entitlement context required",
                Detail = "The authenticated tenant scope could not be resolved.",
                Type = NexoraProblems.FeatureNotEntitled
            }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        var service = context.HttpContext.RequestServices.GetRequiredService<IEntitlementService>();
        var decision = await service.CheckFeatureAsync(businessUnitId, Key, context.HttpContext.RequestAborted);
        if (!decision.Allowed)
        {
            context.Result = new ObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Feature is not entitled",
                Detail = decision.Reason,
                Type = NexoraProblems.FeatureNotEntitled,
                Extensions = { ["entitlement"] = Key }
            }) { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }

        await next();
    }
}
