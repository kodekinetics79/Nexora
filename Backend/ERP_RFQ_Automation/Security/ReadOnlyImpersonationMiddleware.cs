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
/// 2. Read ALLOW-LIST (Sec-D3) — a safe GET is rejected with 403 unless its route is
///    explicitly permitted during impersonation. See <see cref="AllowedReadPaths"/>.
/// 3. Revocation enforcement — the token's <c>jti</c> must match a live
///    <see cref="ImpersonationSession"/> row (present, not revoked, not expired)
///    or the request is rejected with 401. Lookups are cached in the shared
///    <see cref="IMemoryCache"/> for <see cref="CacheTtl"/> (30s), which bounds
///    cross-instance revocation staleness; a same-process revoke evicts the entry
///    (see <see cref="EvictSession"/>) and therefore takes effect immediately
///    (P2-A12). Missing/unknown jti fails CLOSED.
///
/// <para><b>Sec-D3: why guard 2 was inverted.</b> It used to be a deny-list of file-download and
/// export routes. A deny-list has to anticipate the reachable surface, and this one was written
/// against the wrong set. An impersonation token carries no <c>roleId</c>, so
/// <c>PermissionHandler</c> denies every <c>[RequireModulePermission]</c> endpoint — which is
/// genuinely fail-closed, but it means the surface an impersonated session can actually reach is
/// precisely the endpoints with NO module permission, and the deny-list named none of them. The
/// tenant's entire customer and supplier contact directory and the AI copilot's transcripts and
/// decision audit were reachable, because nobody had thought to deny them. An allow-list cannot
/// fail that way: a route added tomorrow with no permission attribute is refused by default, and
/// making it reachable during impersonation is a decision someone has to write down here.</para>
/// </summary>
public sealed class ReadOnlyImpersonationMiddleware(RequestDelegate next, IMemoryCache cache)
{
    /// <summary>Upper bound on CROSS-INSTANCE revocation-decision staleness.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Sec-D3: the complete set of route prefixes a support-impersonation session may read.
    /// Anything not listed here is refused, including routes that do not exist yet.
    ///
    /// <para><b>The admission test each entry had to pass:</b> it explains how the tenant's
    /// system is CONFIGURED (which is what a support operator is diagnosing), and it contains no
    /// customer or supplier identity, no commercial figures, and no document content. That is why
    /// the list is short — and it is honest that it is short. An impersonated session already
    /// cannot open any module-gated screen, so a longer list would mostly be listing routes that
    /// 403 one layer further in anyway.</para>
    ///
    /// <para><b>Deliberately absent</b>, each having been reachable before this change:
    /// <c>/api/Contact</c> (the tenant's whole customer and supplier contact directory — names,
    /// emails, direct numbers: PII, and not configuration); <c>/api/agent</c> (copilot transcripts
    /// and the decision audit — the tenant's own commercial reasoning); <c>/api/GeneralDropdown</c>
    /// (mixes harmless status lists with supplier and warehouse names); <c>/api/SetupMaster</c>
    /// (tenant role rows); <c>/api/BusinessUnit/Dropdown</c>; <c>/api/list-views</c> (a specific
    /// operator's saved column layouts, which are theirs and not diagnostic). Each can be added
    /// here on a written decision — which is the point of the inversion.</para>
    /// </summary>
    internal static readonly string[] AllowedReadPaths =
    {
        // Tenant SLA policy: response-time thresholds and escalation windows. The commonest
        // support question ("why did this escalate?") is answered by this and nothing else.
        "/api/sla/policy",

        // Commercial policy: the tenant's own rules for margins, approvals and rounding. Config,
        // not figures — no customer, quote or order appears in it.
        "/api/commercial-policy",

        // Quote document configuration: numbering, templates and layout. Answers "why does their
        // quote number look like that", which is otherwise a screen-share.
        "/api/QuoteConfiguration",

        // Global reference data, identical for every tenant on the deployment and containing
        // nothing about this one. Listed explicitly rather than waved through as "harmless"
        // because that judgement should be visible.
        "/api/Country",
        "/api/State",
        "/api/City",
        "/api/Uom"
    };

    /// <summary>
    /// Retained as a SECOND check applied after the allow-list, not as the primary control.
    ///
    /// <para>It is redundant today — no allow-listed prefix has an export or download route
    /// beneath it — and it is kept precisely because that is a property of today's list. Someone
    /// widening a prefix above should not be able to hand out a bulk export by accident.</para>
    /// </summary>
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

        // Sec-D3: a safe read still has to be on the allow-list. Default is refusal.
        if (impersonated && !IsAllowedRead(context.Request.Path))
        {
            await WriteProblemAsync(context, StatusCodes.Status403Forbidden,
                NexoraProblems.ImpersonationReadDenied,
                "This data is not available while impersonating",
                "A support impersonation session may only read the tenant's own configuration. "
                + "Customer data, documents and exports are not available through it.");
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

    /// <summary>
    /// True only when the path sits under an <see cref="AllowedReadPaths"/> prefix AND does not
    /// end in one of the export/download suffixes. Everything else — including an empty path and
    /// anything the list has never heard of — is false.
    /// </summary>
    internal static bool IsAllowedRead(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value))
            return false;

        var allowed = false;
        foreach (var prefix in AllowedReadPaths)
        {
            // Segment matching, not string prefixing: "/api/sla/policy" must not admit
            // "/api/sla/policy-history" or "/api/slaX".
            if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                allowed = true;
                break;
            }
        }

        if (!allowed)
            return false;

        var trimmed = value.TrimEnd('/');
        foreach (var suffix in DeniedReadPathSuffixes)
        {
            if (trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
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
