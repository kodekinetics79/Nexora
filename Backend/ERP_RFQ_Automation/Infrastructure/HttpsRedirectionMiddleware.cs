using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;

namespace ERP_RFQ_Automation.Infrastructure;

/// <summary>
/// HTTP-to-HTTPS redirection and HSTS that cannot produce a redirect loop behind a
/// TLS-terminating proxy, and cannot silently do nothing either.
///
/// <para>WHAT <c>UseForwardedHeaders</c> ACTUALLY DOES HERE, measured rather than assumed —
/// <c>ForwardedHeadersBehaviourTests</c> pins all four facts against the exact options Program.cs
/// configures. <c>KnownProxies</c> and <c>KnownNetworks</c> are both cleared and no environment
/// repopulates them, and the intuitive reading of that ("trust no hop") is BACKWARDS:
/// <c>ForwardedHeadersMiddleware</c> runs its known-address check only when at least one entry
/// exists, so an empty pair trusts EVERY caller. The forwarded scheme is therefore applied today,
/// from anyone — which is exactly the spoofing exposure the SEC-H6 comment in Program.cs warns
/// about for <c>X-Forwarded-For</c>.</para>
///
/// <para>Two consequences, and the second one is why this file exists:</para>
/// <list type="number">
/// <item><c>Request.IsHttps</c> IS the truth behind an edge that labels the scheme, because the
/// rewrite has already happened by the time this middleware runs. So the secure test is simply
/// <c>IsHttps</c>.</item>
/// <item>The middleware CONSUMES <c>X-Forwarded-Proto</c> — it is removed from the request. Any
/// later middleware that reads that header for itself reads nothing, in every environment. A
/// redirect decision keyed on the raw header is a control that can never fire, which is worse
/// than no control because it looks like one.</item>
/// </list>
///
/// <para>So the "can I know this request's scheme" test is keyed on <c>X-Original-Proto</c>, which
/// <c>ForwardedHeadersMiddleware</c> writes precisely when it rewrote the scheme. It is exact
/// rather than heuristic: present if and only if an edge is in front AND labels the scheme. When
/// it is absent and no trusted proxy is configured, the scheme is genuinely unknowable and the
/// request is SERVED rather than redirected — that pass-through is the loop guard, because
/// assuming "unknown means plain" is what answers a TLS request with a 307 the edge forwards
/// straight back, forever.</para>
///
/// <para>Nothing here weakens SEC-H6. This middleware reads forwarding headers only to decide
/// whether to redirect; the client address that the rate limiter's per-IP partition and
/// <c>PlatformNetworkAccessMiddleware</c> depend on still comes solely from the normalized
/// <c>RemoteIpAddress</c> that <c>UseForwardedHeaders</c> produces.</para>
/// </summary>
public static class HttpsRedirectionMiddleware
{
    private static readonly string ForwardedProtoHeader = ForwardedHeadersDefaults.XForwardedProtoHeaderName;

    /// <summary>
    /// Written by <c>ForwardedHeadersMiddleware</c> when — and only when — it rewrote the scheme
    /// from a forwarded header. This is the evidence that survives it consuming its own input.
    /// </summary>
    private static readonly string OriginalProtoHeader = ForwardedHeadersDefaults.XOriginalProtoHeaderName;

    /// <summary>
    /// 307 rather than 308 or 301: a permanent redirect is cached by the browser and by
    /// intermediaries, so a deployment that has to fall back to plain HTTP — the exact situation
    /// a pilot may hit — would be un-reachable for users who had already visited. 307 also
    /// preserves the method and body, so a POST is not silently downgraded to a GET.
    /// </summary>
    private const int RedirectStatusCode = StatusCodes.Status307TemporaryRedirect;

    /// <param name="schemeIsAuthoritative">
    /// True when <c>ForwardedHeaders:KnownProxies</c>/<c>:KnownNetworks</c> name the edge, so the
    /// scheme is known for EVERY request — including one that reached the app directly, bypassing
    /// the edge and carrying no forwarding header at all. False when they are unset (the state of
    /// every environment today), in which case a request is redirected only when
    /// <c>X-Original-Proto</c> proves an edge labelled it.
    /// </param>
    public static IApplicationBuilder UseLoopSafeHttpsRedirection(
        this IApplicationBuilder app, string hstsHeaderValue, bool schemeIsAuthoritative)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentException.ThrowIfNullOrWhiteSpace(hstsHeaderValue);

        return app.Use(async (context, next) =>
        {
            // A loopback host has no certificate anyone can present and no public DNS name to
            // pin, so redirecting it breaks a health probe or an operator's curl for nothing, and
            // an HSTS entry for "localhost" poisons every other project on the machine. The same
            // exclusion the framework's HstsOptions ships with, for the same reasons.
            // A request with no Host has no destination to redirect TO; emitting
            // "https:///path" would replace a working response with a malformed one.
            if (!context.Request.Host.HasValue || IsLoopback(context.Request.Host.Host))
            {
                await next();
                return;
            }

            if (IsSecure(context.Request, schemeIsAuthoritative))
            {
                // HSTS only ever travels over a connection the client made securely; a browser
                // is required to ignore it otherwise, and sending it anyway would be a control
                // that looks configured and does nothing.
                context.Response.Headers["Strict-Transport-Security"] = hstsHeaderValue;
                await next();
                return;
            }

            if (!ShouldRedirect(context.Request, schemeIsAuthoritative))
            {
                await next();
                return;
            }

            context.Response.StatusCode = RedirectStatusCode;
            context.Response.Headers.Location = UriHelperBuild(context.Request.Host, context.Request);
        });
    }

    /// <summary>
    /// The request is known to be plain HTTP — not merely un-proved to be HTTPS. The difference
    /// is the loop guard: "unknown" must be served, never redirected.
    /// </summary>
    public static bool ShouldRedirect(HttpRequest request, bool schemeIsAuthoritative)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (IsSecure(request, schemeIsAuthoritative))
            return false;

        // X-Original-Proto is the evidence AFTER UseForwardedHeaders has run and consumed its own
        // input; the raw X-Forwarded-Proto is checked too so the decision stays correct if that
        // middleware is ever reordered, removed, or configured to leave entries behind.
        return schemeIsAuthoritative
            || request.Headers.ContainsKey(OriginalProtoHeader)
            || request.Headers.ContainsKey(ForwardedProtoHeader);
    }

    /// <summary>
    /// The client reached us over TLS — because the socket is TLS, or because
    /// <c>UseForwardedHeaders</c> already rewrote the scheme from <c>X-Forwarded-Proto</c>. Both
    /// surface as <see cref="HttpRequest.IsHttps"/>, so that is the whole test in the normal case.
    ///
    /// <para>The raw header is consulted only when the scheme is NOT authoritative — i.e. for a
    /// pipeline where the forwarded-headers middleware did not run at all. Once trusted proxies
    /// ARE configured this fallback is deliberately skipped, because in that configuration a
    /// header left behind is one the framework REFUSED (the peer was not a known proxy), and
    /// honouring a refused header here would quietly re-admit what that check just rejected.</para>
    /// </summary>
    public static bool IsSecure(HttpRequest request, bool schemeIsAuthoritative = false)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.IsHttps)
            return true;

        if (schemeIsAuthoritative)
            return false;

        if (!request.Headers.TryGetValue(ForwardedProtoHeader, out var forwarded))
            return false;

        // A chain sends "https, http" — leftmost is the CLIENT's hop, which is the one that
        // decides whether the browser spoke TLS.
        var first = forwarded.ToString().Split(',', 2)[0].Trim();
        return string.Equals(first, "https", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The framework's default HSTS excluded-host set.</summary>
    public static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
        || host is "127.0.0.1" or "[::1]" or "::1";

    private static string UriHelperBuild(HostString host, HttpRequest request)
    {
        // Rebuild on the default HTTPS port: the incoming Host carries whatever port the plain
        // HTTP listener answered on, and echoing it back produces https://host:80, which no
        // browser can complete.
        var authority = host.Port is null or 80 or 443 ? host.Host : $"{host.Host}:{host.Port}";
        return $"https://{authority}{request.PathBase}{request.Path}{request.QueryString}";
    }
}
