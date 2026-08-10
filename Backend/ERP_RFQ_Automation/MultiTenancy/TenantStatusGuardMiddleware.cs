using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Entitlements;

namespace ERP_RFQ_Automation.MultiTenancy;

/// <summary>
/// Denies every authenticated tenant-plane request (businessUnitId claim present,
/// path outside /api/platform) whose owning platform Tenant is PastDue, Suspended or Archived
/// (403 problem+json), mirroring <see cref="TenantClaimGuardMiddleware"/>. Status is
/// resolved through the ~60s-cached <see cref="ITenantAccessService"/>, so the hot
/// path performs no database query. Legacy BusinessUnits with no Tenant row are admitted
/// by contract (resolved, no tenant to enforce). An UNREADABLE platform plane is refused
/// with 503 + Retry-After — Sec-D1; it used to be admitted.
///
/// Wire with <c>app.UseTenantStatusGuard()</c> immediately AFTER
/// <c>app.UseTenantClaimGuard()</c> (and therefore after authentication).
/// Requires <c>services.AddPlatformEntitlements()</c>.
/// </summary>
public sealed class TenantStatusGuardMiddleware
{
    private readonly RequestDelegate _next;

    public TenantStatusGuardMiddleware(RequestDelegate next) => _next = next;

    public async Task InvokeAsync(HttpContext context)
    {
        if (TryGetGuardedBusinessUnit(context, out var businessUnitId))
        {
            var tenantAccess = context.RequestServices.GetRequiredService<ITenantAccessService>();
            var access = await tenantAccess.GetAccessAsync(businessUnitId, context.RequestAborted);
            if (access.IsAccessDenied)
            {
                // P2-A10: the typed exception is the single source of the problem type,
                // title and status — the middleware only serializes it.
                //
                // Sec-D1: two denials, not one. An UNRESOLVABLE snapshot means the platform
                // plane could not be read, so claiming the tenant is suspended would be a
                // statement we have no evidence for; it answers 503 with Retry-After instead,
                // which is both honest and visible to uptime monitoring. A resolved
                // restricted status still answers 403 exactly as before.
                EntitlementDeniedException denial;
                if (access.IsUnresolvable)
                {
                    denial = new TenantAccessUnresolvableException(businessUnitId, access.UnresolvedReason);
                    context.Response.Headers.RetryAfter =
                        TenantAccessUnresolvableException.RetryAfterSeconds.ToString();
                }
                else
                {
                    denial = new TenantAccessDeniedException(businessUnitId, access.Status);
                }

                context.Response.StatusCode = denial.SuggestedStatusCode;
                context.Response.ContentType = "application/problem+json";
                await context.Response.WriteAsJsonAsync(new
                {
                    type = denial.ProblemType,
                    title = denial.Message,
                    status = denial.SuggestedStatusCode
                });
                return;
            }
        }

        await _next(context);
    }

    private static bool TryGetGuardedBusinessUnit(HttpContext context, out long businessUnitId)
    {
        businessUnitId = 0;
        return context.User.Identity?.IsAuthenticated == true
               && !context.Request.Path.StartsWithSegments("/api/platform", StringComparison.OrdinalIgnoreCase)
               && long.TryParse(context.User.FindFirst("businessUnitId")?.Value, out businessUnitId)
               && businessUnitId > 0;
    }
}

public static class TenantStatusGuardApplicationBuilderExtensions
{
    public static IApplicationBuilder UseTenantStatusGuard(this IApplicationBuilder app)
        => app.UseMiddleware<TenantStatusGuardMiddleware>();
}
