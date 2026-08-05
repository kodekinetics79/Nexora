using ERP_RFQ_Automation.Models;
using ERP_RFQ_Automation.Platform.Auth;
using ERP_RFQ_Automation.Platform.Entitlements;
using ERP_RFQ_Automation.Platform.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ERP_RFQ_Automation.Security;

/// <summary>
/// Three guards for support-impersonation tokens (<c>impersonated=true</c>):
/// 1. Read-only enforcement — any non-safe HTTP method is rejected with 403.
/// 2. Sensitive-read deny-list (Sec8) — even safe GETs are rejected with 403 when
///    they hit file-download / bulk-export style routes (customer document bytes and
///    bulk data have no place in a support session).
/// 3. Revocation enforcement — the token's <c>jti</c> must match a live
///    <see cref="ImpersonationSession"/> row (present, not revoked, not expired)
///    or the request is rejected with 401. Lookups are cached in the shared
///    <see cref="IMemoryCache"/> for <see cref="CacheTtl"/> (30s), which bounds
///    cross-instance revocation staleness; a same-process revoke evicts the entry
///    (see <see cref="EvictSession"/>) and therefore takes effect immediately
///    (P2-A12). Missing/unknown jti fails CLOSED.
/// </summary>
public sealed class ReadOnlyImpersonationMiddleware(RequestDelegate next, IMemoryCache cache)
{
    /// <summary>Upper bound on CROSS-INSTANCE revocation-decision staleness.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sec8: path prefixes an impersonated (support) session may never read. These are
    /// the real routes that stream customer file bytes or bulk data exports:
    /// - /api/File            → attachment/evidence file downloads (FileController)
    /// - /api/processing-evidence → extraction evidence payloads
    /// The suffix rules below additionally cover the uploader-template/export CSV
    /// endpoints (e.g. /api/CustomerUploader/export, /api/Boq/{id}/export.csv).
    /// </summary>
    internal static readonly string[] DeniedReadPathPrefixes =
    {
        "/api/File",
        "/api/processing-evidence"
    };

    internal static readonly string[] DeniedReadPathSuffixes =
    {
        "/export",
        "/export.csv",
        "/download-template",
        "/DownloadFile"
    };

    private readonly RequestDelegate _next = next;
    private readonly IMemoryCache _cache = cache;

    internal static string SessionCacheKey(string jti) => $"nexora:impersonation-session:{jti}";

    /// <summary>
    /// P2-A12: same-process immediate revocation — removes the cached validity
    /// decision for a jti so the next request re-reads the session row.
    /// </summary>
    public static void EvictSession(IMemoryCache cache, string jti)
        => cache.Remove(SessionCacheKey(jti));

    public async Task InvokeAsync(HttpContext context)
    {
        var impersonated = string.Equals(
            context.User.FindFirst(PlatformAuthConstants.ImpersonatedClaim)?.Value,
            "true", StringComparison.OrdinalIgnoreCase);
        var readOnly = impersonated || string.Equals(
            context.User.FindFirst(PlatformAuthConstants.ReadOnlyClaim)?.Value,
            "true", StringComparison.OrdinalIgnoreCase);
        var safeMethod = HttpMethods.IsGet(context.Request.Method) ||
                         HttpMethods.IsHead(context.Request.Method) ||
                         HttpMethods.IsOptions(context.Request.Method);

        if (readOnly && !safeMethod)
        {
            await WriteProblemAsync(context, StatusCodes.Status403Forbidden,
                NexoraProblems.ReadOnlyImpersonation,
                "Read-only impersonation session",
                "Support impersonation sessions cannot perform mutations.");
            return;
        }

        // Sec8: block file-download / export style reads for impersonated sessions.
        if (impersonated && IsDeniedRead(context.Request.Path))
        {
            await WriteProblemAsync(context, StatusCodes.Status403Forbidden,
                NexoraProblems.ImpersonationExportDenied,
                "File downloads and exports are not available while impersonating",
                "Support impersonation sessions cannot download customer files or export data.");
            return;
        }

        if (impersonated && !await IsSessionActiveAsync(context))
        {
            await WriteProblemAsync(context, StatusCodes.Status401Unauthorized,
                NexoraProblems.ImpersonationSessionRevoked,
                "Impersonation session is not active",
                "This support impersonation session has been revoked or is unknown.");
            return;
        }

        await _next(context);
    }

    internal static bool IsDeniedRead(PathString path)
    {
        foreach (var prefix in DeniedReadPathPrefixes)
        {
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var value = path.Value;
        if (string.IsNullOrEmpty(value))
            return false;
        var trimmed = value.TrimEnd('/');
        foreach (var suffix in DeniedReadPathSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static async Task WriteProblemAsync(
        HttpContext context, int status, string type, string title, string detail)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/problem+json";
        await context.Response.WriteAsJsonAsync(new { type, title, status, detail });
    }

    private async Task<bool> IsSessionActiveAsync(HttpContext context)
    {
        // The jti claim keys the revocable session row. A token without one (or one
        // minted before session tracking existed) is rejected: fail closed.
        var jti = context.User.FindFirst("jti")?.Value;
        if (string.IsNullOrWhiteSpace(jti))
            return false;

        if (_cache.TryGetValue(SessionCacheKey(jti), out bool cachedValid))
            return cachedValid;

        var db = context.RequestServices?.GetService<ErpRfqAutomationContext>();
        if (db is null)
            return false; // no way to verify => fail closed

        var now = DateTime.UtcNow;
        var session = await db.Set<ImpersonationSession>().AsNoTracking()
            .FirstOrDefaultAsync(s => s.Jti == jti, context.RequestAborted);
        var valid = session is not null
                    && session.RevokedAtUtc is null
                    && session.ExpiresAtUtc > now;

        _cache.Set(SessionCacheKey(jti), valid, CacheTtl);
        return valid;
    }
}

public static class ReadOnlyImpersonationMiddlewareExtensions
{
    public static IApplicationBuilder UseReadOnlyImpersonationGuard(this IApplicationBuilder app)
        => app.UseMiddleware<ReadOnlyImpersonationMiddleware>();
}
