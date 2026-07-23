using System.Security.Claims;

namespace ERP_RFQ_Automation.MultiTenancy;

/// <summary>
/// Prevents an authenticated tenant API request from falling back to a business unit
/// supplied in the route, query string, or body when its token has no valid tenant claim.
/// Platform control-plane routes have their own audience, scheme, and authorization policy.
/// </summary>
public sealed class TenantClaimGuardMiddleware
{
    private readonly RequestDelegate _next;

    public TenantClaimGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (RequiresTenantClaim(context) && !HasValidTenantClaim(context.User))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/problem+json";
            await context.Response.WriteAsJsonAsync(new
            {
                type = "https://nexora.invalid/problems/tenant-claim-required",
                title = "A valid tenant claim is required.",
                status = StatusCodes.Status403Forbidden
            });
            return;
        }

        await _next(context);
    }

    private static bool RequiresTenantClaim(HttpContext context)
        => context.User.Identity?.IsAuthenticated == true
           && !context.Request.Path.StartsWithSegments("/api/platform", StringComparison.OrdinalIgnoreCase);

    private static bool HasValidTenantClaim(ClaimsPrincipal user)
        => long.TryParse(user.FindFirst("businessUnitId")?.Value, out var businessUnitId)
           && businessUnitId > 0;
}

public static class TenantClaimGuardApplicationBuilderExtensions
{
    public static IApplicationBuilder UseTenantClaimGuard(this IApplicationBuilder app)
        => app.UseMiddleware<TenantClaimGuardMiddleware>();
}
