using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;

namespace ERP_RFQ_Automation.Authorization
{
    /// <summary>
    /// Turns authorization "forbid" results into a small, uniform JSON body instead of an
    /// empty 403. Deliberately generic — it must NOT leak which module/permission was
    /// missing. Challenge (401) and success flow through the default handler untouched.
    /// </summary>
    public sealed class ForbiddenJsonResultHandler : IAuthorizationMiddlewareResultHandler
    {
        private readonly AuthorizationMiddlewareResultHandler _default = new();

        public async Task HandleAsync(
            RequestDelegate next,
            HttpContext context,
            AuthorizationPolicy policy,
            PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Forbidden)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("{\"error\":\"You don't have permission for this action.\"}");
                return;
            }

            await _default.HandleAsync(next, context, policy, authorizeResult);
        }
    }
}
