using ERP_RFQ_Automation.Platform.Auth;

namespace ERP_RFQ_Automation.Security;

public sealed class ReadOnlyImpersonationMiddleware(RequestDelegate next)
{
    private readonly RequestDelegate _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        var readOnly = string.Equals(
            context.User.FindFirst(PlatformAuthConstants.ReadOnlyClaim)?.Value,
            "true",
            StringComparison.OrdinalIgnoreCase) ||
            string.Equals(context.User.FindFirst(PlatformAuthConstants.ImpersonatedClaim)?.Value,
                "true", StringComparison.OrdinalIgnoreCase);
        var safeMethod = HttpMethods.IsGet(context.Request.Method) ||
                         HttpMethods.IsHead(context.Request.Method) ||
                         HttpMethods.IsOptions(context.Request.Method);

        if (readOnly && !safeMethod)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://nexora.local/problems/read-only-impersonation",
                title = "Read-only impersonation session",
                status = StatusCodes.Status403Forbidden,
                detail = "Support impersonation sessions cannot perform mutations."
            });
            return;
        }

        await _next(context);
    }
}

public static class ReadOnlyImpersonationMiddlewareExtensions
{
    public static IApplicationBuilder UseReadOnlyImpersonationGuard(this IApplicationBuilder app)
        => app.UseMiddleware<ReadOnlyImpersonationMiddleware>();
}
