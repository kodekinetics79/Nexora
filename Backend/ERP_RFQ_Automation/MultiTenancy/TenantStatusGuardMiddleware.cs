using System.Security.Claims;
using ERP_RFQ_Automation.Platform.Entitlements;

namespace ERP_RFQ_Automation.MultiTenancy;

/// <summary>
/// Denies every authenticated tenant-plane request (businessUnitId claim present,
/// path outside /api/platform) whose owning platform Tenant is PastDue, Suspended or Archived
/// (403 problem+json), mirroring <see cref="TenantClaimGuardMiddleware"/>. Status is
/// resolved through the ~60s-cached <see cref="ITenantAccessService"/>, so the hot
/// path performs no database query. Legacy BusinessUnits with no Tenant row fail
/// OPEN by contract, as does an unavailable platform plane.
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
                var denial = new TenantAccessDeniedException(businessUnitId, access.Status);
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
